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
let private config = {
    MailboxCapacity = 8
    MaxSessions = 8
    PlayerMailboxCapacity = 4
    MaxPendingPerPlayer = 8
    MaxPendingChannelRequests = 16
    MaxPendingOutput = 64
}
let private chatConfig = { MailboxCapacity = 2; HistoryCapacity = 2 }

let private collect (output: Channel<'T>) (_: AgentContext<'T>) value = task {
    check (output.Writer.TryWrite value) "Test channel closed."
}

let private receive (output: Channel<'T>) = output.Reader.ReadAsync().AsTask().WaitAsync guard

let private post (agent: Agent<'T>) value = task {
    let! result = agent.PostAsync value
    equal AgentPostResult.Posted result
}

let private readPlayer (registry: Agent<SessionRegistryMessage>) connectionId = task {
    let! result = registry.TryAskAsync(fun reply -> SessionRegistryMessage.ReadPlayer(connectionId, reply)) |> awaitResult
    match result with
    | AgentAskResult.Replied value -> return value
    | other -> return failwithf "Expected routed player state: %A" other
}

type private OutputMessage =
    | HoldOutput of TaskCompletionSource<unit> * TaskCompletionSource<unit>
    | Forward of SessionOutput
    | Filler

let private slowOutput (events: Channel<SessionOutput>) (context: AgentContext<OutputMessage>) message = task {
    match message with
    | HoldOutput(entered, release) ->
        entered.TrySetResult() |> ignore
        do! release.Task.WaitAsync context.CancellationToken
    | Forward value -> check (events.Writer.TryWrite value) "Test channel closed."
    | Filler -> ()
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

// A rejected resolved player is closed; a repeated Open on the same connection is not.
let private consumeRejection events request result = task {
    rejected request RequestRejectionCode.SessionAlreadyOpen result
    match result with
    | SessionOutput.Send(_, ChatResponse.RequestRejected(_, rejection)) when rejection.Message = "Player already has a session." ->
        let! closed = receive events
        equal (SessionOutput.Close request.ConnectionId) closed
    | _ -> ()
}

let private reopen registry events request = task {
    let elapsed = System.Diagnostics.Stopwatch.StartNew()
    let mutable welcome = None
    while welcome.IsNone do
        check (elapsed.Elapsed < guard) "The old session did not release its player ID."
        do! post registry (SessionRegistryMessage.Open request)
        let! result = receive events
        match result with
        | SessionOutput.Send(_, ChatResponse.RequestRejected _) ->
            do! consumeRejection events request result
        | other -> welcome <- Some (opened request other)
    return welcome.Value
}

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
            let character = CharacterName.create 128 "Alice's character" |> ok
            do! post registry (SessionRegistryMessage.UpdatePlayer(alice.ConnectionId, PlayerUpdate.BeginCharacter character))
            let! aliceState = readPlayer registry alice.ConnectionId
            equal (ValueSome character) (ok aliceState).CharacterName

            do! post registry (SessionRegistryMessage.Open bob)
            let! second = receive events
            let second = opened bob second
            equal 2 second.Players.Length
            check (first.SelfPlayerId <> second.SelfPlayerId) "Profiles share an ID."
            let! bobState = readPlayer registry bob.ConnectionId
            equal ValueNone (ok bobState).CharacterName
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
            let! next = reopen registry events reconnect
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
            let! c = receive events
            let responses = [a; b; c]
            let winner = responses |> List.pick (function
                | SessionOutput.Send(id, ChatResponse.SessionOpened _) -> Some id
                | _ -> None)
            let accepted, denied = if winner = first.ConnectionId then first, second else second, first
            let failure = responses |> List.find (function
                | SessionOutput.Send(_, ChatResponse.RequestRejected _) -> true
                | _ -> false)
            rejected denied RequestRejectionCode.SessionAlreadyOpen failure
            check (List.contains (SessionOutput.Close denied.ConnectionId) responses) "Duplicate player remains connected."
            let success = responses |> List.find (function
                | SessionOutput.Send(_, ChatResponse.SessionOpened _) -> true
                | _ -> false)
            equal 1 (opened accepted success).Players.Length
        }
        do! withRegistry config (profiles.Ref.TryReliable().Value) realChat run
    })

    case "disconnect during resolution reserves no player ID and old replies cannot enter the new player" (fun () -> task {
        let requests = Channel.CreateUnbounded<ProfileRequest>()
        use profiles = Agent.Start(AgentOptions.create "controlled-profiles", collect requests)
        let run (registry: Agent<SessionRegistryMessage>) (events: Channel<SessionOutput>) = task {
            let first = openRequest "player"
            do! post registry (SessionRegistryMessage.Open first)
            let! pending = receive requests
            equal (ProfileCommand.GetOrCreate(first.Username, first.DisplayName)) pending.Command
            do! post registry (SessionRegistryMessage.Disconnect first.ConnectionId)

            let next = { first with ConnectionId = Guid.NewGuid() }
            do! post registry (SessionRegistryMessage.Open next)
            let! nextQuery = receive requests
            let! stale = pending.ReplyTo.PostAsync {
                OperationId = pending.OperationId
                Result = Ok (ProfileOutcome.Resolved(player first))
            }
            equal AgentDeliveryResult.Closed stale
            do! replyProfile nextQuery (Ok (ProfileOutcome.Resolved(player first)))
            let! welcome = receive events
            opened next welcome |> ignore
            equal 0 events.Reader.Count
        }
        do! withRegistry config (profiles.Ref.TryReliable().Value) realChat run
    })

    case "different usernames resolving to the same ID cannot bypass pending leave" (fun () -> task {
        let requests = Channel.CreateUnbounded<ProfileRequest>()
        let channelRequests = Channel.CreateUnbounded<ChannelRequest>()
        use profiles = Agent.Start(AgentOptions.create "controlled-profiles", collect requests)
        let makeChannel _ = Agent.Start(AgentOptions.create "controlled-chat", collect channelRequests)
        let run (registry: Agent<SessionRegistryMessage>) (events: Channel<SessionOutput>) = task {
            let request = openRequest "player"
            let profile = player request
            do! post registry (SessionRegistryMessage.Open request)
            let! lookup = receive requests
            do! replyProfile lookup (Ok (ProfileOutcome.Resolved profile))
            let! join = receive channelRequests
            equal (ChannelCommand.Join profile.PlayerId) join.Command
            do! post registry (SessionRegistryMessage.Disconnect request.ConnectionId)
            let snapshot = { ChannelId = globalId; Players = Set.singleton profile.PlayerId; Messages = []; HistoryCapacity = 2 }
            do! replyChannel registry join (ChannelOutcome.Joined snapshot)
            let! leave = receive channelRequests
            equal (ChannelCommand.Leave profile.PlayerId) leave.Command
            let next = openRequest "renamed_player"
            let nextProfile = player next
            do! post registry (SessionRegistryMessage.Open next)
            let! duplicateLookup = receive requests
            do! replyProfile duplicateLookup (Ok (ProfileOutcome.Resolved nextProfile))
            let! busy = receive events
            do! consumeRejection events next busy
            equal 0 channelRequests.Reader.Count
            do! replyChannel registry leave (ChannelOutcome.Left true)
            let next = { next with ConnectionId = Guid.NewGuid() }
            let mutable nextJoin = None
            let elapsed = System.Diagnostics.Stopwatch.StartNew()
            while nextJoin.IsNone do
                check (elapsed.Elapsed < guard) "The player ID was not released."
                do! post registry (SessionRegistryMessage.Open next)
                do! eventually (fun () -> requests.Reader.Count > 0 || events.Reader.Count > 0)
                if requests.Reader.Count > 0 then
                    let! query = receive requests
                    do! replyProfile query (Ok (ProfileOutcome.Resolved nextProfile))
                    do! eventually (fun () -> channelRequests.Reader.Count > 0 || events.Reader.Count > 0)
                    if channelRequests.Reader.Count > 0 then
                        let! command = receive channelRequests
                        nextJoin <- Some command
                    else
                        let! rejected = receive events
                        do! consumeRejection events next rejected
                else
                    let! rejected = receive events
                    do! consumeRejection events next rejected
            let nextJoin = nextJoin.Value
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
            do! replyProfile lookupAlice (Ok (ProfileOutcome.Resolved aliceProfile))
            let! joinAlice = receive channelRequests
            do! replyChannel registry joinAlice (ChannelOutcome.Joined(snapshot [aliceProfile.PlayerId] []))
            let! first = receive events
            opened alice first |> ignore

            do! post registry (SessionRegistryMessage.Open bob)
            let! lookupBob = receive requests
            do! replyProfile lookupBob (Ok (ProfileOutcome.Resolved bobProfile))
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

    case "one failed profile operation leaves existing players and later opens available" (fun () -> task {
        let requests = Channel.CreateUnbounded<ProfileRequest>()
        use profiles = Agent.Start(AgentOptions.create "controlled-profiles", collect requests)
        let run (registry: Agent<SessionRegistryMessage>) (events: Channel<SessionOutput>) = task {
            let alice, bob, carol = openRequest "alice", openRequest "bob", openRequest "carol"
            do! post registry (SessionRegistryMessage.Open alice)
            let! first = receive requests
            let aliceProfile = player alice
            do! replyProfile first (Ok (ProfileOutcome.Resolved aliceProfile))
            let! ready = receive events
            opened alice ready |> ignore

            do! post registry (SessionRegistryMessage.Open bob)
            let! failed = receive requests
            let expected = InvalidOperationException("only this query failed")
            do! replyProfile failed (Error (ProfileStoreError.Failed expected))
            let! failure = receive events
            match failure with
            | SessionOutput.PlayerFailed(id, PlayerFailure.Profile(ProfileStoreError.Failed error)) ->
                equal bob.ConnectionId id
                check (Object.ReferenceEquals(expected, error)) "Original query failure was lost."
            | other -> failwithf "Expected individual player failure: %A" other
            let! closed = receive events
            equal (SessionOutput.Close bob.ConnectionId) closed
            check registry.IsAcceptingMessages "One query failure stopped the registry."

            do! post registry (SessionRegistryMessage.Open carol)
            let! next = receive requests
            let carolProfile = PlayerData.create (PlayerId.create 43UL |> ok) carol.Username carol.DisplayName
            do! replyProfile next (Ok (ProfileOutcome.Resolved carolProfile))
            let! ready = receive events
            let welcome = opened carol ready
            equal [aliceProfile; carolProfile] welcome.Players
            let! joined = receive events
            equal (SessionOutput.Send(alice.ConnectionId, ChatResponse.PlayerJoined carolProfile)) joined
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

    case "Stop cancels resolution without waiting for an admitted storage query" (fun () -> task {
        let requests = Channel.CreateUnbounded<ProfileRequest>()
        let channelRequests = Channel.CreateUnbounded<ChannelRequest>()
        use profiles = Agent.Start(AgentOptions.create "controlled-profiles", collect requests)
        let makeChannel _ = Agent.Start(AgentOptions.create "controlled-chat", collect channelRequests)
        let run (registry: Agent<SessionRegistryMessage>) (events: Channel<SessionOutput>) = task {
            let request = openRequest "player"
            do! post registry (SessionRegistryMessage.Open request)
            let! pending = receive requests
            do! post registry SessionRegistryMessage.Stop
            let! closed = receive events
            equal (SessionOutput.Close request.ConnectionId) closed
            do! awaitUnit registry.Completion

            let! late = pending.ReplyTo.PostAsync {
                OperationId = pending.OperationId
                Result = Ok (ProfileOutcome.Resolved(player request))
            }
            equal AgentDeliveryResult.Closed late
            equal 0 channelRequests.Reader.Count
            equal 0 events.Reader.Count
            equal AgentPostResult.Closed (registry.TryPost(SessionRegistryMessage.Open(openRequest "next")))
        }
        do! withRegistry config (profiles.Ref.TryReliable().Value) makeChannel run
    })

    case "a blocked profile lane does not delay an online player's channel leave" (fun () -> task {
        let entered, release = gate<unit>(), gate<unit>()
        let requests = Channel.CreateUnbounded<ProfileRequest>()
        let hold (context: AgentContext<ProfileRequest>) request = task {
            check (requests.Writer.TryWrite request) "Test channel closed."
            match request.Command with
            | ProfileCommand.GetOrCreate(name, _) when Username.value name = "blocked" ->
                entered.TrySetResult() |> ignore
                do! release.Task.WaitAsync context.CancellationToken
            | ProfileCommand.GetOrCreate _ | ProfileCommand.FindByUsername _ | ProfileCommand.Create _ -> ()
        }
        use profiles = Agent.Start(options "slow-profiles" (AgentMailbox.boundedWait 1), hold)
        let channelRequests = Channel.CreateUnbounded<ChannelRequest>()
        let makeChannel _ = Agent.Start(AgentOptions.create "controlled-chat", collect channelRequests)
        let run (registry: Agent<SessionRegistryMessage>) (events: Channel<SessionOutput>) = task {
            let online = openRequest "online"
            let profile = player online
            do! post registry (SessionRegistryMessage.Open online)
            let! query = receive requests
            do! replyProfile query (Ok (ProfileOutcome.Resolved profile))
            let! join = receive channelRequests
            let snapshot = { ChannelId = globalId; Players = Set.singleton profile.PlayerId; Messages = []; HistoryCapacity = 2 }
            do! replyChannel registry join (ChannelOutcome.Joined snapshot)
            let! ready = receive events
            opened online ready |> ignore

            do! post registry (SessionRegistryMessage.Open(openRequest "blocked"))
            do! awaitResult entered.Task
            do! post registry (SessionRegistryMessage.Open(openRequest "queued"))
            do! eventually (fun () -> profiles.QueueLength = 1)
            do! post registry (SessionRegistryMessage.Open(openRequest "waiting"))
            do! post registry (SessionRegistryMessage.Disconnect online.ConnectionId)
            let! leave = receive channelRequests
            equal (ChannelCommand.Leave profile.PlayerId) leave.Command
            check (not release.Task.IsCompleted) "Storage was released before the independent channel lane made progress."
            do! replyChannel registry leave (ChannelOutcome.Left true)

            do! post registry SessionRegistryMessage.Stop
            do! awaitUnit registry.Completion
            release.SetResult()
        }
        do! withRegistry config (profiles.Ref.TryReliable().Value) makeChannel run
    })

    case "full network output permits disconnect cleanup and delays only final draining" (fun () -> task {
        let entered, release = gate<unit>(), gate<unit>()
        let events = Channel.CreateUnbounded<SessionOutput>()
        use receiver = Agent.Start(options "slow-network-output" (AgentMailbox.boundedWait 1), slowOutput events)
        do! post receiver (HoldOutput(entered, release))
        do! awaitResult entered.Task
        do! post receiver Filler

        use profiles = MemoryProfileStore.start 2 |> ok
        let channelRequests = Channel.CreateUnbounded<ChannelRequest>()
        use channel = Agent.Start(AgentOptions.create "controlled-chat", collect channelRequests)
        use registry = SessionRegistry.start config globalId (profiles.Ref.TryReliable().Value)
                           (receiver.Ref.TryReliable().Value.Map Forward) |> ok
        do! post registry (SessionRegistryMessage.BindChannel(channel.Ref.TryReliable().Value))
        let request = openRequest "player"
        do! post registry (SessionRegistryMessage.Open request)
        let! join = receive channelRequests
        let playerId =
            match join.Command with
            | ChannelCommand.Join id -> id
            | other -> failwithf "Expected join: %A" other
        let snapshot = { ChannelId = globalId; Players = Set.singleton playerId; Messages = []; HistoryCapacity = 2 }
        do! replyChannel registry join (ChannelOutcome.Joined snapshot)
        let! ready = readPlayer registry request.ConnectionId
        equal playerId (ok ready).Data.PlayerId

        do! post registry (SessionRegistryMessage.Disconnect request.ConnectionId)
        let! leave = receive channelRequests
        equal (ChannelCommand.Leave playerId) leave.Command
        check (not release.Task.IsCompleted) "Output was released before disconnect made progress."
        do! replyChannel registry leave (ChannelOutcome.Left true)
        do! post registry SessionRegistryMessage.Stop
        let! stopped = readPlayer registry request.ConnectionId
        equal (Error PlayerStateError.Closed) stopped
        check (not registry.Completion.IsCompleted) "Shutdown discarded an admitted output."

        release.SetResult()
        do! awaitUnit registry.Completion
        receiver.Complete() |> ignore
        channel.Complete() |> ignore
        do! awaitUnit receiver.Completion
        do! awaitUnit channel.Completion
        let! welcome = receive events
        opened request welcome |> ignore
    })

    case "registry Abort closes all child reply addresses before Completion returns" (fun () -> task {
        let requests = Channel.CreateUnbounded<ProfileRequest>()
        use profiles = Agent.Start(AgentOptions.create "controlled-profiles", collect requests)
        let run (registry: Agent<SessionRegistryMessage>) (_: Channel<SessionOutput>) = task {
            do! post registry (SessionRegistryMessage.Open(openRequest "alice"))
            do! post registry (SessionRegistryMessage.Open(openRequest "bob"))
            let! first = receive requests
            let! second = receive requests

            registry.Abort()
            let! _ = terminal registry.Completion
            check registry.Completion.IsCanceled "Abort was not reported as cancellation."
            for request in [first; second] do
                let! late = request.ReplyTo.PostAsync {
                    OperationId = request.OperationId
                    Result = Error ProfileStoreError.Canceled
                }
                equal AgentDeliveryResult.Closed late
        }
        do! withRegistry config (profiles.Ref.TryReliable().Value) realChat run
    })

    case "pending channel limit includes admitted operations still waiting for replies" (fun () -> task {
        let requests = Channel.CreateUnbounded<ProfileRequest>()
        let channelRequests = Channel.CreateUnbounded<ChannelRequest>()
        use profiles = Agent.Start(AgentOptions.create "controlled-profiles", collect requests)
        let makeChannel _ = Agent.Start(AgentOptions.create "controlled-chat", collect channelRequests)
        let run (registry: Agent<SessionRegistryMessage>) (events: Channel<SessionOutput>) = task {
            let first, second = openRequest "first", openRequest "second"
            do! post registry (SessionRegistryMessage.Open first)
            let! query = receive requests
            do! replyProfile query (Ok (ProfileOutcome.Resolved(player first)))
            let! _ = receive channelRequests

            // Admission is complete, but the first Join has no response yet.
            do! post registry (SessionRegistryMessage.Open second)
            let! query = receive requests
            let secondProfile = PlayerData.create (PlayerId.create 43UL |> ok) second.Username second.DisplayName
            do! replyProfile query (Ok (ProfileOutcome.Resolved secondProfile))
            let! failure = receive events
            equal (SessionOutput.Failed SessionRegistryFailure.Overloaded) failure
            let! firstClose = receive events
            let! secondClose = receive events
            let closedId = function
                | SessionOutput.Close id -> id
                | other -> failwithf "Expected closed connection: %A" other
            equal (Set.ofList [first.ConnectionId; second.ConnectionId])
                  (Set.ofList [closedId firstClose; closedId secondClose])
            let! _ = terminal registry.Completion
            equal 0 channelRequests.Reader.Count
        }
        do! withRegistry { config with MaxPendingChannelRequests = 1 }
                (profiles.Ref.TryReliable().Value) makeChannel run
    })

    case "an unexpected player fault settles forwarded reads and leaves other players online" (fun () -> task {
        use profiles = MemoryProfileStore.start 2 |> ok
        let run (registry: Agent<SessionRegistryMessage>) (events: Channel<SessionOutput>) = task {
            let healthy, broken = openRequest "healthy", openRequest "broken"
            do! post registry (SessionRegistryMessage.Open healthy)
            let! first = receive events
            let healthyWelcome = opened healthy first
            do! post registry (SessionRegistryMessage.Open broken)
            let! second = receive events
            let brokenWelcome = opened broken second
            let! joined = receive events
            match joined with
            | SessionOutput.Send(id, ChatResponse.PlayerJoined data) ->
                equal healthy.ConnectionId id
                equal brokenWelcome.SelfPlayerId data.PlayerId
            | other -> failwithf "Expected presence join: %A" other

            let health = ActorValueInfo.create (ActorValueName.create 64 "Health" |> ok)
                             (ActorValueState.resource 50.0f 100.0f |> ok)
            // Bypass the domain factory only to emulate an internal bug: a null key
            // throws in the child's dictionary. No production fault-injection API.
            let corruptKey = Unchecked.defaultof<ActorValueKey>
            do! post registry (SessionRegistryMessage.UpdatePlayer(broken.ConnectionId,
                                  PlayerUpdate.SetActorValues [corruptKey, health]))
            let! pendingRead = registry.TryAskAsync(
                                   (fun reply -> SessionRegistryMessage.ReadPlayer(broken.ConnectionId, reply)),
                                   timeout = System.Threading.Timeout.InfiniteTimeSpan) |> awaitResult
            expectReply (Error PlayerStateError.Closed) pendingRead

            let! failure = receive events
            match failure with
            | SessionOutput.PlayerFailed(id, PlayerFailure.Unexpected (:? ArgumentNullException)) ->
                equal broken.ConnectionId id
            | other -> failwithf "Expected the actual child fault: %A" other
            let! closed = receive events
            equal (SessionOutput.Close broken.ConnectionId) closed
            let! left = receive events
            equal (SessionOutput.Send(healthy.ConnectionId, ChatResponse.PlayerLeft brokenWelcome.SelfPlayerId)) left

            let! stillOnline = readPlayer registry healthy.ConnectionId
            equal healthyWelcome.SelfPlayerId (ok stillOnline).Data.PlayerId
            check registry.IsAcceptingMessages "The child fault stopped unrelated players."
        }
        do! withRegistry config (profiles.Ref.TryReliable().Value) realChat run
    })
]
