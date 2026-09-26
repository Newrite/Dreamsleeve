module Dreamsleeve.Server.Tests.SessionRegistryTests

open System
open System.Threading.Channels
open System.Threading.Tasks
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure
open Expecto
open AgentTests
open BackgroundTests

let private ok = function Ok value -> value | Error error -> failwithf "%A" error
let private globalId = ChatChannelId.create 1UL |> ok
let private config = { MailboxCapacity = 4; MaxSessions = 8 }
let private chatConfig = { MailboxCapacity = 2; HistoryCapacity = 2 }

let private collect (output: Channel<'T>) (_: AgentContext<'T>) value = task {
    check (output.Writer.TryWrite value) "Test channel closed."
}

let private receive (output: Channel<'T>) = output.Reader.ReadAsync().AsTask().WaitAsync guard

let private post (agent: Agent<'T>) value = task {
    let! result = agent.PostAsync value
    equal AgentPostResult.Posted result
}

let private openRequest name : SessionOpenRequest = {
    ConnectionId = Guid.NewGuid()
    RequestId = 1UL
    Username = Username.create 32 name |> ok
    DisplayName = DisplayName.create 64 name |> ok
}

let private player (request: SessionOpenRequest) =
    PlayerData.create (PlayerId.create 42UL |> ok) request.Username request.DisplayName

let private opened request = function
    | SessionOutput.Send(connectionId, ChatResponse.SessionOpened(requestId, welcome)) ->
        equal request.ConnectionId connectionId
        equal request.RequestId requestId
        equal globalId welcome.GlobalChannelId
        welcome
    | other -> failwithf "Expected SessionOpened: %A" other

let private rejected request code = function
    | SessionOutput.Send(connectionId, ChatResponse.RequestRejected(requestId, rejection)) ->
        equal request.ConnectionId connectionId
        equal request.RequestId requestId
        equal code rejection.Code
    | other -> failwithf "Expected RequestRejected: %A" other

let private replyProfile (request: ProfileRequest) result = task {
    let! admission = request.ReplyTo.PostAsync { OperationId = request.OperationId; Result = result }
    equal AgentDeliveryResult.Posted admission
}

let private replyChannel registry (request: ChannelRequest) outcome =
    post registry (SessionRegistryMessage.ChannelReplied {
        ChannelId = globalId; OperationId = request.OperationId; Result = Ok outcome
    })

let private realChat output = ChatAgent.start chatConfig globalId output |> ok

let private withRegistry settings profiles
                         (makeChannel: ReliableAgentRef<ChannelReply> -> Agent<ChannelRequest>)
                         (run: Agent<SessionRegistryMessage> -> Channel<SessionOutput> -> Task<unit>) = task {
    let events = Channel.CreateUnbounded<SessionOutput>()
    use receiver = Agent.Start(AgentOptions.create "network-output", collect events)
    use registry = SessionRegistry.start settings globalId profiles (receiver.Ref.TryReliable().Value) |> ok
    use channel = makeChannel (registry.Ref.TryReliable().Value.Map SessionRegistryMessage.ChannelReplied)
    do! post registry (SessionRegistryMessage.BindChannel(channel.Ref.TryReliable().Value))

    do! run registry events

    if registry.IsAcceptingMessages then
        do! post registry SessionRegistryMessage.Stop
        do! awaitUnit registry.Completion
    else
        let! _ = terminal registry.Completion
        ()
    channel.Complete() |> ignore
    do! awaitUnit channel.Completion
    receiver.Complete() |> ignore
    do! awaitUnit receiver.Completion
}

let tests = testList "SessionRegistry" [
    case "real stores open sessions, announce presence and retain profiles across reconnect" (fun () -> task {
        use profiles = MemoryProfileStore.start 2 |> ok
        let run (registry: Agent<SessionRegistryMessage>) (events: Channel<SessionOutput>) = task {
            let alice, bob = openRequest "Alice", openRequest "Bob"
            do! post registry (SessionRegistryMessage.Open alice)
            let! first = receive events
            let first = opened alice first
            equal 1 first.Players.Length
            equal first.SelfPlayerId first.Players.Head.PlayerId
            let codec = ChatCodec.create ServerConfig.defaults |> ok
            ChatCodec.encodeServer codec (ChatResponse.SessionOpened(alice.RequestId, first)) |> ok |> ignore

            do! post registry (SessionRegistryMessage.Open bob)
            let! second = receive events
            let second = opened bob second
            equal 2 second.Players.Length
            check (first.SelfPlayerId <> second.SelfPlayerId) "Profiles share an ID."
            let! joined = receive events
            match joined with
            | SessionOutput.Send(id, ChatResponse.PlayerJoined profile) ->
                equal alice.ConnectionId id
                equal second.SelfPlayerId profile.PlayerId
            | other -> failwithf "Expected presence join: %A" other

            do! post registry (SessionRegistryMessage.Disconnect alice.ConnectionId)
            let! left = receive events
            equal (SessionOutput.Send(bob.ConnectionId, ChatResponse.PlayerLeft first.SelfPlayerId)) left
            let reconnect = { alice with ConnectionId = Guid.NewGuid(); DisplayName = DisplayName.create 64 "Changed" |> ok }
            do! post registry (SessionRegistryMessage.Open reconnect)
            let! next = receive events
            let next = opened reconnect next
            equal first.SelfPlayerId next.SelfPlayerId
            equal alice.DisplayName (next.Players |> List.find (fun p -> p.PlayerId = next.SelfPlayerId)).DisplayName
            let! _ = receive events
            // A stale disconnect for the old connection cannot remove the replacement.
            do! post registry (SessionRegistryMessage.Disconnect alice.ConnectionId)
            do! post registry (SessionRegistryMessage.Open reconnect)
            let! repeated = receive events
            rejected reconnect RequestRejectionCode.SessionAlreadyOpen repeated
        }
        do! withRegistry config (profiles.Ref.TryReliable().Value) realChat run
    })

    case "one canonical username can have only one pending or online session" (fun () -> task {
        use profiles = MemoryProfileStore.start 2 |> ok
        let run (registry: Agent<SessionRegistryMessage>) (events: Channel<SessionOutput>) = task {
            let first, second = openRequest "USER", openRequest "user"
            do! post registry (SessionRegistryMessage.Open first)
            do! post registry (SessionRegistryMessage.Open second)
            let! a = receive events
            let! b = receive events
            let responses = [a; b]
            let failure = responses |> List.find (function
                | SessionOutput.Send(_, ChatResponse.RequestRejected _) -> true
                | _ -> false)
            rejected second RequestRejectionCode.UsernameTaken failure
            let success = responses |> List.find (function
                | SessionOutput.Send(_, ChatResponse.SessionOpened _) -> true
                | _ -> false)
            opened first success |> ignore
        }
        do! withRegistry config (profiles.Ref.TryReliable().Value) realChat run
    })

    case "disconnect during lookup ignores stale replies and releases the reserved name" (fun () -> task {
        let requests = Channel.CreateUnbounded<ProfileRequest>()
        use profiles = Agent.Start(AgentOptions.create "controlled-profiles", collect requests)
        let run (registry: Agent<SessionRegistryMessage>) (events: Channel<SessionOutput>) = task {
            let first = openRequest "player"
            do! post registry (SessionRegistryMessage.Open first)
            let! pending = receive requests
            do! post registry (SessionRegistryMessage.Disconnect first.ConnectionId)
            let next = { first with ConnectionId = Guid.NewGuid() }
            do! post registry (SessionRegistryMessage.Open next)
            let! busy = receive events
            rejected next RequestRejectionCode.UsernameTaken busy
            do! replyProfile pending (Ok (ProfileOutcome.Found(Some(player first))))
            do! post registry (SessionRegistryMessage.Open next)
            let! nextLookup = receive requests
            do! replyProfile pending (Ok (ProfileOutcome.Found(Some(player first))))
            do! replyProfile nextLookup (Ok (ProfileOutcome.Found(Some(player first))))
            let! welcome = receive events
            opened next welcome |> ignore
            check (not (events.Reader.TryPeek() |> fst)) "A stale reply produced an extra event."
        }
        do! withRegistry config (profiles.Ref.TryReliable().Value) realChat run
    })

    case "disconnect during creation keeps the created profile but does not enter chat" (fun () -> task {
        let requests = Channel.CreateUnbounded<ProfileRequest>()
        let channelRequests = Channel.CreateUnbounded<ChannelRequest>()
        use profiles = Agent.Start(AgentOptions.create "controlled-profiles", collect requests)
        let makeChannel _ = Agent.Start(AgentOptions.create "controlled-chat", collect channelRequests)
        let run (registry: Agent<SessionRegistryMessage>) (events: Channel<SessionOutput>) = task {
            let request = openRequest "player"
            do! post registry (SessionRegistryMessage.Open request)
            let! lookup = receive requests
            do! replyProfile lookup (Ok (ProfileOutcome.Found None))
            let! create = receive requests
            equal (ProfileCommand.Create(request.Username, request.DisplayName)) create.Command
            do! post registry (SessionRegistryMessage.Disconnect request.ConnectionId)
            do! replyProfile create (Ok (ProfileOutcome.Created(player request)))
            do! post registry SessionRegistryMessage.Stop
            do! awaitUnit registry.Completion
            equal 0 channelRequests.Reader.Count
            equal 0 events.Reader.Count
        }
        do! withRegistry config (profiles.Ref.TryReliable().Value) makeChannel run
    })

    case "disconnect during join waits for leave before allowing reuse of the name" (fun () -> task {
        let requests = Channel.CreateUnbounded<ProfileRequest>()
        let channelRequests = Channel.CreateUnbounded<ChannelRequest>()
        use profiles = Agent.Start(AgentOptions.create "controlled-profiles", collect requests)
        let makeChannel _ = Agent.Start(AgentOptions.create "controlled-chat", collect channelRequests)
        let run (registry: Agent<SessionRegistryMessage>) (events: Channel<SessionOutput>) = task {
            let request = openRequest "player"
            let profile = player request
            do! post registry (SessionRegistryMessage.Open request)
            let! lookup = receive requests
            do! replyProfile lookup (Ok (ProfileOutcome.Found(Some profile)))
            let! join = receive channelRequests
            equal (ChannelCommand.Join profile.PlayerId) join.Command
            do! post registry (SessionRegistryMessage.Disconnect request.ConnectionId)
            let snapshot = { ChannelId = globalId; Players = Set.singleton profile.PlayerId; Messages = []; HistoryCapacity = 2 }
            do! replyChannel registry join (ChannelOutcome.Joined snapshot)
            let! leave = receive channelRequests
            equal (ChannelCommand.Leave profile.PlayerId) leave.Command
            let next = { request with ConnectionId = Guid.NewGuid() }
            do! post registry (SessionRegistryMessage.Open next)
            let! busy = receive events
            rejected next RequestRejectionCode.UsernameTaken busy
            do! replyChannel registry leave (ChannelOutcome.Left true)
            do! post registry (SessionRegistryMessage.Open next)
            let! nextLookup = receive requests
            do! replyProfile nextLookup (Ok (ProfileOutcome.Found(Some profile)))
            let! nextJoin = receive channelRequests
            do! replyChannel registry join (ChannelOutcome.Joined snapshot)
            do! replyChannel registry nextJoin (ChannelOutcome.Joined snapshot)
            let! welcome = receive events
            opened next welcome |> ignore
            do! post registry SessionRegistryMessage.Stop
            let! closed = receive events
            equal (SessionOutput.Close next.ConnectionId) closed
            let! finalLeave = receive channelRequests
            do! replyChannel registry finalLeave (ChannelOutcome.Left true)
            do! awaitUnit registry.Completion
        }
        do! withRegistry config (profiles.Ref.TryReliable().Value) makeChannel run
    })

    case "a pending leave follows a new session snapshot without losing presence ordering" (fun () -> task {
        let requests = Channel.CreateUnbounded<ProfileRequest>()
        let channelRequests = Channel.CreateUnbounded<ChannelRequest>()
        use profiles = Agent.Start(AgentOptions.create "controlled-profiles", collect requests)
        let makeChannel _ = Agent.Start(AgentOptions.create "controlled-chat", collect channelRequests)
        let run (registry: Agent<SessionRegistryMessage>) (events: Channel<SessionOutput>) = task {
            let alice, bob = openRequest "alice", openRequest "bob"
            let aliceProfile = player alice
            let bobProfile = PlayerData.create (PlayerId.create 43UL |> ok) bob.Username bob.DisplayName
            let snapshot players messages = {
                ChannelId = globalId; Players = Set.ofList players; Messages = messages; HistoryCapacity = 2
            }
            do! post registry (SessionRegistryMessage.Open alice)
            let! lookupAlice = receive requests
            do! replyProfile lookupAlice (Ok (ProfileOutcome.Found(Some aliceProfile)))
            let! joinAlice = receive channelRequests
            do! replyChannel registry joinAlice (ChannelOutcome.Joined(snapshot [aliceProfile.PlayerId] []))
            let! first = receive events
            opened alice first |> ignore

            do! post registry (SessionRegistryMessage.Open bob)
            let! lookupBob = receive requests
            do! replyProfile lookupBob (Ok (ProfileOutcome.Found(Some bobProfile)))
            let! joinBob = receive channelRequests
            do! post registry (SessionRegistryMessage.Disconnect alice.ConnectionId)
            let! leaveAlice = receive channelRequests
            let oldMessage = ChatMessage.create (ChatMessageId.create 1UL |> ok) globalId aliceProfile
                                (ChatMessageText.create 64 "history" |> ok) DateTimeOffset.UnixEpoch
            do! replyChannel registry joinBob (ChannelOutcome.Joined(snapshot [aliceProfile.PlayerId; bobProfile.PlayerId] [oldMessage]))
            let! second = receive events
            let welcome = opened bob second
            equal [aliceProfile; bobProfile] welcome.Players
            equal [oldMessage] welcome.RecentMessages
            do! replyChannel registry leaveAlice (ChannelOutcome.Left true)
            let! left = receive events
            equal (SessionOutput.Send(bob.ConnectionId, ChatResponse.PlayerLeft aliceProfile.PlayerId)) left

            do! post registry SessionRegistryMessage.Stop
            let! closed = receive events
            equal (SessionOutput.Close bob.ConnectionId) closed
            let! leaveBob = receive channelRequests
            do! replyChannel registry leaveBob (ChannelOutcome.Left true)
            do! awaitUnit registry.Completion
        }
        do! withRegistry config (profiles.Ref.TryReliable().Value) makeChannel run
    })

    case "closed storage fails visibly and stops pending sessions" (fun () -> task {
        let requests = Channel.CreateUnbounded<ProfileRequest>()
        use profiles = Agent.Start(AgentOptions.create "closed-profiles", collect requests)
        profiles.Complete() |> ignore
        do! awaitUnit profiles.Completion
        let run (registry: Agent<SessionRegistryMessage>) (events: Channel<SessionOutput>) = task {
            let request = openRequest "player"
            do! post registry (SessionRegistryMessage.Open request)
            let! failure = receive events
            equal (SessionOutput.Failed SessionRegistryFailure.DependencyUnavailable) failure
            let! closed = receive events
            equal (SessionOutput.Close request.ConnectionId) closed
            let! _ = terminal registry.Completion
            check registry.Completion.IsCanceled "Failed registry kept accepting work."
        }
        do! withRegistry config (profiles.Ref.TryReliable().Value) realChat run
    })

    case "the owner can report a dependency crash after request admission" (fun () -> task {
        let requests = Channel.CreateUnbounded<ProfileRequest>()
        use profiles = Agent.Start(AgentOptions.create "controlled-profiles", collect requests)
        let run (registry: Agent<SessionRegistryMessage>) (events: Channel<SessionOutput>) = task {
            let request = openRequest "player"
            let expected = InvalidOperationException("storage failed after admission")
            do! post registry (SessionRegistryMessage.Open request)
            let! _ = receive requests
            do! post registry (SessionRegistryMessage.DependencyStopped(SessionRegistryFailure.OperationFailed expected))
            let! failure = receive events
            match failure with
            | SessionOutput.Failed(SessionRegistryFailure.OperationFailed error) ->
                check (Object.ReferenceEquals(expected, error)) "Original failure was lost."
            | other -> failwithf "Expected dependency failure: %A" other
            let! closed = receive events
            equal (SessionOutput.Close request.ConnectionId) closed
            let! _ = terminal registry.Completion
            ()
        }
        do! withRegistry config (profiles.Ref.TryReliable().Value) realChat run
    })

    case "Stop waits for pending lookup results and prevents new opening" (fun () -> task {
        let requests = Channel.CreateUnbounded<ProfileRequest>()
        use profiles = Agent.Start(AgentOptions.create "controlled-profiles", collect requests)
        let run (registry: Agent<SessionRegistryMessage>) (events: Channel<SessionOutput>) = task {
            let request = openRequest "player"
            do! post registry (SessionRegistryMessage.Open request)
            let! pending = receive requests
            do! post registry SessionRegistryMessage.Stop
            let! closed = receive events
            equal (SessionOutput.Close request.ConnectionId) closed
            check (not registry.Completion.IsCompleted) "Stop lost a pending operation."
            let next = openRequest "next"
            do! post registry (SessionRegistryMessage.Open next)
            let! refused = receive events
            equal (SessionOutput.Close next.ConnectionId) refused
            do! replyProfile pending (Ok (ProfileOutcome.Found None))
            do! awaitUnit registry.Completion
            equal 0 requests.Reader.Count
        }
        do! withRegistry config (profiles.Ref.TryReliable().Value) realChat run
    })

    case "a full dependency mailbox does not block disconnect or session capacity checks" (fun () -> task {
        let entered, release = gate<unit>(), gate<unit>()
        let hold (context: AgentContext<ProfileRequest>) _ = task {
            entered.TrySetResult() |> ignore
            do! release.Task.WaitAsync context.CancellationToken
        }
        use profiles = Agent.Start(options "slow-profiles" (AgentMailbox.boundedWait 1), hold)
        let run (registry: Agent<SessionRegistryMessage>) (events: Channel<SessionOutput>) = task {
            let a, b, c = openRequest "a", openRequest "b", openRequest "c"
            do! post registry (SessionRegistryMessage.Open a)
            do! awaitResult entered.Task
            do! post registry (SessionRegistryMessage.Open b)
            do! eventually (fun () -> profiles.QueueLength = 1)
            do! post registry (SessionRegistryMessage.Open c)
            do! post registry (SessionRegistryMessage.Disconnect c.ConnectionId)
            let excess = openRequest "excess"
            do! post registry (SessionRegistryMessage.Open excess)
            let! closed = receive events
            equal (SessionOutput.Close excess.ConnectionId) closed
            registry.Abort()
            let! _ = terminal registry.Completion
            release.SetResult()
        }
        do! withRegistry { config with MaxSessions = 3 } (profiles.Ref.TryReliable().Value) realChat run
    })
]
