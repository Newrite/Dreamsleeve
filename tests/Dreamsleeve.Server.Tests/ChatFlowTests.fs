module Dreamsleeve.Server.Tests.ChatFlowTests

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
    MaxPendingChatRequests = 16
    MaxPendingOutput = 64
}

let private collect (events: Channel<'T>) (_: AgentContext<'T>) value = task {
    check (events.Writer.TryWrite value) "Test output closed."
}

let private receive (events: Channel<'T>) = events.Reader.ReadAsync().AsTask().WaitAsync guard

let private post (agent: Agent<'T>) value = task {
    let! result = agent.PostAsync value
    equal AgentPostResult.Posted result
}

type private Fixture = {
    Registry: Agent<SessionRegistryMessage>
    Channel: Agent<ChannelRequest>
    Events: Channel<SessionOutput>
    Replies: Channel<ChannelReply>
    ManualReplies: bool
}

let private withRegistry settings manualReplies run = task {
    let events = Channel.CreateUnbounded<SessionOutput>()
    let replies = Channel.CreateUnbounded<ChannelReply>()
    use receiver = Agent.Start(AgentOptions.create "network-output", collect events)
    use profiles = MemoryProfileStore.start { MailboxCapacity = 2; MaxPendingReplies = 4 } |> ok
    use registry = SessionRegistry.start settings globalId (profiles.Ref.TryReliable().Value) (receiver.Ref.TryReliable().Value) |> ok
    use relay = Agent.Start(AgentOptions.create "controlled-channel-output", collect replies)
    let channelOutput =
        if manualReplies then relay.Ref.TryReliable().Value
        else registry.Ref.TryReliable().Value.Map SessionRegistryMessage.ChannelReplied
    use channel = ChatAgent.start { MailboxCapacity = 2; HistoryCapacity = 2; MaxPendingReplies = 4 } globalId channelOutput |> ok

    try
        do! post registry (SessionRegistryMessage.BindChannel(channel.Ref.TryReliable().Value))
        do! run { Registry = registry; Channel = channel; Events = events; Replies = replies; ManualReplies = manualReplies }
    finally
        registry.Abort()
        channel.Abort()
        relay.Abort()
        profiles.Abort()
        receiver.Abort()
}

let private forward fixture reply = post fixture.Registry (SessionRegistryMessage.ChannelReplied reply)

let private openPlayer fixture name = task {
    let request: SessionOpenRequest = {
        ConnectionId = Guid.NewGuid(); RequestId = 1UL
        Username = Username.create 32 name |> ok
        DisplayName = DisplayName.create 64 name |> ok
    }
    do! post fixture.Registry (SessionRegistryMessage.Open request)
    if fixture.ManualReplies then
        let! reply = receive fixture.Replies
        do! forward fixture reply

    let! response = receive fixture.Events
    match response with
    | SessionOutput.Send(connectionId, ChatResponse.SessionOpened(requestId, welcome)) ->
        equal request.ConnectionId connectionId
        equal request.RequestId requestId
        return request, welcome
    | other -> return failwithf "Expected SessionOpened: %A" other
}

let private request (session: SessionOpenRequest) requestId text : ChatSendRequest = {
    ConnectionId = session.ConnectionId; RequestId = requestId; ChannelId = globalId
    Text = ChatMessageText.create 512 text |> ok
}

let private send fixture request = post fixture.Registry (SessionRegistryMessage.SendChat request)

let private expectRejected fixture (request: ChatSendRequest) code = task {
    let! response = receive fixture.Events
    match response with
    | SessionOutput.Send(connectionId, ChatResponse.RequestRejected(requestId, rejection)) ->
        equal request.ConnectionId connectionId
        equal request.RequestId requestId
        equal code rejection.Code
    | other -> failwithf "Expected request rejection: %A" other
}

// An immediate response passes through the same output FIFO: unexpected echoes or
// duplicate replies are observed here without sleeping to assert their absence.
let private barrier fixture = task {
    let probe = {
        ConnectionId = Guid.NewGuid(); RequestId = 99UL; ChannelId = globalId
        Text = ChatMessageText.create 512 "barrier" |> ok
    }
    do! send fixture probe
    do! expectRejected fixture probe RequestRejectionCode.SessionNotReady
}

let private acceptedPair fixture (request: ChatSendRequest) otherConnection = task {
    let! first = receive fixture.Events
    let! second = receive fixture.Events
    let responses = [first; second]
    let message = responses |> List.pick (function
        | SessionOutput.Send(id, ChatResponse.ChatAccepted(requestId, message)) ->
            equal request.ConnectionId id
            equal request.RequestId requestId
            equal request.Text message.MessageText
            Some message
        | _ -> None)
    check (List.contains (SessionOutput.Send(otherConnection, ChatResponse.ChatPublished message)) responses)
        "Other player must receive exactly the same message without request correlation."
    return message
}

let private twoPlayers fixture = task {
    let! alice, first = openPlayer fixture "Alice"
    let! bob, second = openPlayer fixture "Bob"
    let! presence = receive fixture.Events
    let bobProfile = second.Players |> List.find (fun player -> player.PlayerId = second.SelfPlayerId)
    equal (SessionOutput.Send(alice.ConnectionId, ChatResponse.PlayerJoined bobProfile)) presence
    return alice, first, bob, second
}

let tests = testList "SendChat" [
    case "two players receive authoritative ordered messages and newcomers receive retained history" (fun () ->
        withRegistry config false (fun fixture -> task {
            let! alice, aliceWelcome, bob, bobWelcome = twoPlayers fixture
            let started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            let messages = ResizeArray<ChatMessage>()
            for session, welcome, other, id in [alice, aliceWelcome, bob, 2UL; bob, bobWelcome, alice, 2UL; alice, aliceWelcome, bob, 3UL] do
                let command = request session id (sprintf "message %d" messages.Count)
                do! send fixture command
                let! message = acceptedPair fixture command other.ConnectionId
                equal globalId message.ChannelId
                equal welcome.SelfPlayerId message.Author.PlayerId
                equal session.DisplayName message.Author.DisplayName
                check (message.SentAt.ToUnixTimeMilliseconds() >= started) "Timestamp predates the send."
                check (message.SentAt <= DateTimeOffset.UtcNow) "Timestamp is in the future."
                let codec = ChatCodec.create ServerConfig.defaults |> ok
                ChatCodec.encodeServer codec (ChatResponse.ChatAccepted(id, message)) |> ok |> ignore
                ChatCodec.encodeServer codec (ChatResponse.ChatPublished message) |> ok |> ignore
                messages.Add message

            equal [1UL; 2UL; 3UL] (messages |> Seq.map (fun m -> ChatMessageId.value m.MessageId) |> List.ofSeq)
            let! _, welcome = openPlayer fixture "Charlie"
            equal [messages[1]; messages[2]] welcome.RecentMessages
            let! _ = receive fixture.Events
            let! _ = receive fixture.Events
            do! barrier fixture
        }))

    case "a join snapshot is followed by a later publication exactly once" (fun () ->
        withRegistry config true (fun fixture -> task {
            let! alice, _ = openPlayer fixture "Alice"
            let bob: SessionOpenRequest = {
                ConnectionId = Guid.NewGuid(); RequestId = 1UL
                Username = Username.create 32 "Bob" |> ok; DisplayName = DisplayName.create 64 "Bob" |> ok
            }
            do! post fixture.Registry (SessionRegistryMessage.Open bob)
            let! joined = receive fixture.Replies
            let command = request alice 2UL "after the snapshot"
            do! send fixture command
            let! published = receive fixture.Replies

            do! forward fixture joined
            let! welcome = receive fixture.Events
            match welcome with
            | SessionOutput.Send(id, ChatResponse.SessionOpened(_, snapshot)) ->
                equal bob.ConnectionId id
                equal [] snapshot.RecentMessages
            | other -> failwithf "Expected bootstrap before publication: %A" other
            let! _ = receive fixture.Events
            do! forward fixture published
            let! _ = acceptedPair fixture command bob.ConnectionId
            do! barrier fixture
        }))

    case "unopened, joining, disconnected and wrong-channel requests never reach history" (fun () ->
        withRegistry config true (fun fixture -> task {
            do! barrier fixture
            let session: SessionOpenRequest = {
                ConnectionId = Guid.NewGuid(); RequestId = 1UL
                Username = Username.create 32 "Alice" |> ok; DisplayName = DisplayName.create 64 "Alice" |> ok
            }
            do! post fixture.Registry (SessionRegistryMessage.Open session)
            let! joined = receive fixture.Replies
            let command = request session 2UL "too early"
            do! send fixture command
            do! expectRejected fixture command RequestRejectionCode.SessionNotReady
            do! forward fixture joined
            let! _ = receive fixture.Events

            let wrongChannel = { command with ChannelId = ChatChannelId.create 2UL |> ok }
            do! send fixture wrongChannel
            do! expectRejected fixture wrongChannel RequestRejectionCode.ChannelNotFound
            do! post fixture.Registry (SessionRegistryMessage.Disconnect session.ConnectionId)
            do! send fixture command
            do! expectRejected fixture command RequestRejectionCode.SessionNotReady
            let! left = receive fixture.Replies
            do! forward fixture left

            let! _, welcome = openPlayer fixture "Bob"
            equal [] welcome.RecentMessages
            do! barrier fixture
        }))

    case "global admission limit rejects before history changes and leaves room for membership" (fun () ->
        withRegistry { config with MaxPendingChatRequests = 1 } true (fun fixture -> task {
            let! alice, _, bob, _ = twoPlayers fixture
            let first = request alice 2UL "accepted"
            do! send fixture first
            let! published = receive fixture.Replies
            let denied = request bob 2UL "must not be retained"
            do! send fixture denied
            do! expectRejected fixture denied RequestRejectionCode.Overloaded

            // Join still reaches the real channel while its publication reply is held.
            let newcomer: SessionOpenRequest = {
                ConnectionId = Guid.NewGuid(); RequestId = 1UL
                Username = Username.create 32 "Charlie" |> ok; DisplayName = DisplayName.create 64 "Charlie" |> ok
            }
            do! post fixture.Registry (SessionRegistryMessage.Open newcomer)
            let! joined = receive fixture.Replies
            do! forward fixture published
            let! message = acceptedPair fixture first bob.ConnectionId
            do! forward fixture joined
            let! welcome = receive fixture.Events
            match welcome with
            | SessionOutput.Send(id, ChatResponse.SessionOpened(_, snapshot)) ->
                equal newcomer.ConnectionId id
                equal [message] snapshot.RecentMessages
            | other -> failwithf "Expected newcomer snapshot: %A" other
            let! _ = receive fixture.Events
            let! _ = receive fixture.Events

            // The rejected request did not close Bob's session; released capacity is reusable.
            let next = request bob 3UL "after overload"
            do! send fixture next
            let! nextReply = receive fixture.Replies
            do! forward fixture nextReply
            let! _ = receive fixture.Events
            let! _ = receive fixture.Events
            let! _ = receive fixture.Events
            do! barrier fixture
        }))

    case "per-player pending limit allows another player to publish" (fun () ->
        withRegistry { config with MaxPendingPerPlayer = 1; MaxPendingChatRequests = 3 } true (fun fixture -> task {
            let! alice, _, bob, _ = twoPlayers fixture
            let first = request alice 2UL "alice"
            do! send fixture first
            let! firstReply = receive fixture.Replies
            let denied = request alice 3UL "too many"
            do! send fixture denied
            do! expectRejected fixture denied RequestRejectionCode.Overloaded
            let second = request bob 2UL "bob"
            do! send fixture second
            let! secondReply = receive fixture.Replies
            do! forward fixture firstReply
            let! _ = acceptedPair fixture first bob.ConnectionId
            do! forward fixture secondReply
            let! _ = acceptedPair fixture second alice.ConnectionId
            do! barrier fixture
        }))

    case "disconnect suppresses the author's ack but preserves fanout and history; duplicate replies are ignored" (fun () ->
        withRegistry config true (fun fixture -> task {
            let! alice, aliceWelcome, bob, _ = twoPlayers fixture
            let command = request alice 2UL "already accepted"
            do! send fixture command
            let! published = receive fixture.Replies
            do! post fixture.Registry (SessionRegistryMessage.Disconnect alice.ConnectionId)
            let! left = receive fixture.Replies
            do! forward fixture published
            let! received = receive fixture.Events
            let message =
                match received with
                | SessionOutput.Send(id, ChatResponse.ChatPublished message) -> equal bob.ConnectionId id; message
                | other -> failwithf "Expected publication for the remaining player: %A" other
            do! forward fixture left
            let! presence = receive fixture.Events
            equal (SessionOutput.Send(bob.ConnectionId, ChatResponse.PlayerLeft aliceWelcome.SelfPlayerId)) presence
            let! _, welcome = openPlayer fixture "Charlie"
            equal [message] welcome.RecentMessages
            let! _ = receive fixture.Events
            do! forward fixture published
            do! barrier fixture
        }))

    case "a channel membership rejection keeps the session usable" (fun () ->
        withRegistry config true (fun fixture -> task {
            let! alice, welcome = openPlayer fixture "Alice"
            // Exercise the channel's authorization boundary independently of the routing view.
            do! post fixture.Channel { OperationId = Guid.NewGuid(); Command = ChannelCommand.Leave welcome.SelfPlayerId }
            let! _ = receive fixture.Replies
            let command = request alice 2UL "no membership"
            do! send fixture command
            let! reply = receive fixture.Replies
            equal (Error (DomainError.NotChatMember welcome.SelfPlayerId)) reply.Result
            do! forward fixture reply
            do! expectRejected fixture command RequestRejectionCode.NotChannelMember
            let! _, next = openPlayer fixture "Bob"
            equal [] next.RecentMessages
            equal 2 next.Players.Length
            let! _ = receive fixture.Events
            do! barrier fixture
        }))

    case "Stop drains accepted publications and leaves without echoing to closed sessions" (fun () ->
        withRegistry config true (fun fixture -> task {
            let! alice, _, bob, _ = twoPlayers fixture
            do! send fixture (request alice 2UL "before shutdown")
            let! publication = receive fixture.Replies
            do! post fixture.Registry SessionRegistryMessage.Stop
            let! first = receive fixture.Events
            let! second = receive fixture.Events
            let closed = [first; second] |> List.map (function
                | SessionOutput.Close id -> id
                | other -> failwithf "Expected Close: %A" other) |> Set.ofList
            equal (Set.ofList [alice.ConnectionId; bob.ConnectionId]) closed
            let! leave1 = receive fixture.Replies
            let! leave2 = receive fixture.Replies
            check (not fixture.Registry.Completion.IsCompleted) "Stop lost pending channel operations."
            do! forward fixture publication
            do! forward fixture leave1
            do! forward fixture leave2
            do! awaitUnit fixture.Registry.Completion
            check (not (fixture.Events.Reader.TryPeek() |> fst)) "Closed sessions received a chat echo."
        }))

    case "a mismatched accepted message fails the registry instead of acknowledging the wrong request" (fun () ->
        withRegistry config true (fun fixture -> task {
            let! alice, _ = openPlayer fixture "Alice"
            do! send fixture (request alice 2UL "original")
            let! reply = receive fixture.Replies
            let broken =
                match reply.Result with
                | Ok (ChannelOutcome.Published(message, recipients)) ->
                    let changed = ChatMessage.create message.MessageId message.ChannelId message.Author
                                      (ChatMessageText.create 512 "different" |> ok) message.SentAt
                    { reply with Result = Ok (ChannelOutcome.Published(changed, recipients)) }
                | other -> failwithf "Expected accepted publication: %A" other
            do! forward fixture broken
            let! failure = receive fixture.Events
            equal (SessionOutput.Failed(SessionRegistryFailure.InvalidReply "accepted publication")) failure
            let! closed = receive fixture.Events
            equal (SessionOutput.Close alice.ConnectionId) closed
            let! _ = terminal fixture.Registry.Completion
            ()
        }))
]
