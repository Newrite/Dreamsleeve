module Dreamsleeve.Server.Tests.ChatRoomAgentTests

open System
open System.Threading.Channels
open System.Threading.Tasks
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain
open Expecto
open AgentTests
open BackgroundTests

let private ok = function Ok value -> value | Error error -> failwithf "%A" error
let private config = { MailboxCapacity = 4; ControlReserve = 2; HistoryCapacity = 2; MaxControlDeliveries = 4
                       RateBurst = 100; RateRefillMs = 1000; DuplicateWindowMs = 0 }
let private channelId = ChatChannelId.create 1UL |> ok
let private profile number =
    PlayerData.create (PlayerId.create number |> ok)
        (Username.create 32 $"player{number}" |> ok) (DisplayName.create 64 $"Player {number}" |> ok)

let private collect (output: Channel<'T>) (_: AgentContext<'T>) value = task {
    check (output.Writer.TryWrite value) "Test output closed."
}
let private receive (output: Channel<'T>) = output.Reader.ReadAsync().AsTask().WaitAsync guard
let private post (agent: Agent<'T>) value = task {
    let! result = agent.PostAsync value
    equal AgentPostResult.Posted result
}
let private stop (agent: Agent<'T>) = task {
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
}
let private subscription number (agent: Agent<ChatRoomEvent>) = {
    ConnectionId = Guid.NewGuid(); Profile = profile number; Events = agent.Ref.TryReliable().Value
}
let private publish (room: Agent<ChatRoomCommand>) (subscriber: Subscription<ChatRoomEvent>) requestId text =
    post room (ChatRoomCommand.Publish {
        ConnectionId = subscriber.ConnectionId; RequestId = requestId
        Text = ChatMessageText.create 256 text |> ok; ReplyTo = subscriber.Events
        CharacterName = ValueNone; Fingerprint = Moderation.normalize text
        Flagged = if text.StartsWith "flag" then [{ Start = 0; Length = 4 }] else []
    })
let private accepted requestId = function
    | ChatRoomEvent.Accepted(actual, message) -> equal requestId actual; message
    | other -> failwithf "Expected accepted publication: %A" other
let private joined = function
    | ChatRoomEvent.Joined snapshot -> snapshot
    | other -> failwithf "Expected join snapshot: %A" other
let private history (room: Agent<ChatRoomCommand>) = task {
    let! result = room.AskAsync(fun reply -> ChatRoomCommand.ReadHistory(ValueNone, 10, reply)) |> awaitResult
    return ok result
}

type private SlowMessage<'T> =
    | Hold of TaskCompletionSource<unit> * TaskCompletionSource<unit>
    | Value of 'T
    | Filler
let private slow (events: Channel<'T>) (context: AgentContext<SlowMessage<'T>>) = function
    | Hold(entered, release) -> task {
        entered.TrySetResult() |> ignore
        do! release.Task.WaitAsync context.CancellationToken
      }
    | Value event -> task { check (events.Writer.TryWrite event) "Test output closed." }
    | Filler -> Task.FromResult()
let private block (agent: Agent<SlowMessage<'T>>) = task {
    let entered, release = gate<unit>(), gate<unit>()
    do! post agent (Hold(entered, release))
    do! awaitResult entered.Task
    do! post agent Filler
    return release
}

let private rateLimited requestId = function
    | ChatRoomEvent.Rejected(actual, rejection) ->
        equal requestId actual
        equal RequestRejectionCode.RateLimited rejection.Code
    | other -> failwithf "Expected rate limit: %A" other

let tests = testList "ChatRoomAgent" [
    case "burst limit is per account and survives a reconnect; refusals are not stored" (fun () -> task {
        let hostEvents, events, replies = Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<ChatRoomEvent>(), Channel.CreateUnbounded<Guid>()
        use host = Agent.Start(AgentOptions.create "host", collect hostEvents)
        use player = Agent.Start(AgentOptions.create "player", collect events)
        use cleanup = Agent.Start(AgentOptions.create "cleanup", collect replies)
        let limited = { config with HistoryCapacity = 8; RateBurst = 2; RateRefillMs = 60000; DuplicateWindowMs = 0 }
        use room = ChatRoomAgent.start limited channelId (host.Ref.TryReliable().Value) |> ok
        let first = subscription 1UL player
        do! post room (ChatRoomCommand.Join first)
        let! _ = receive events
        do! publish room first 1UL "one"
        let! one = receive events
        accepted 1UL one |> ignore
        do! publish room first 2UL "two"
        let! two = receive events
        accepted 2UL two |> ignore

        // A new connection of the same account inherits the spent burst.
        do! post room (ChatRoomCommand.Detach { ConnectionId = first.ConnectionId; ReplyTo = cleanup.Ref.TryReliable().Value })
        let! _ = receive replies
        let second = subscription 1UL player
        do! post room (ChatRoomCommand.Join second)
        let! _ = receive events
        do! publish room second 3UL "three"
        let! refused = receive events
        rateLimited 3UL refused
        let! retained = history room
        equal 2 retained.Messages.Length

        // Another account has its own budget.
        let otherEvents = Channel.CreateUnbounded<ChatRoomEvent>()
        use otherPlayer = Agent.Start(AgentOptions.create "other", collect otherEvents)
        let other = subscription 2UL otherPlayer
        do! post room (ChatRoomCommand.Join other)
        let! _ = receive otherEvents
        do! publish room other 4UL "three"
        let! own = receive otherEvents
        accepted 4UL own |> ignore
        do! stop room
    })

    case "normalized repeats are refused within the window and tokens refill over time" (fun () -> task {
        let hostEvents, events = Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<ChatRoomEvent>()
        use host = Agent.Start(AgentOptions.create "host", collect hostEvents)
        use player = Agent.Start(AgentOptions.create "player", collect events)
        let limited = { config with HistoryCapacity = 8; RateBurst = 1; RateRefillMs = 50; DuplicateWindowMs = 60000 }
        use room = ChatRoomAgent.start limited channelId (host.Ref.TryReliable().Value) |> ok
        let alice = subscription 1UL player
        do! post room (ChatRoomCommand.Join alice)
        let! _ = receive events
        do! publish room alice 1UL "Hello   there"
        let! first = receive events
        accepted 1UL first |> ignore
        do! publish room alice 2UL "different"
        let! tooFast = receive events
        rateLimited 2UL tooFast
        do! Task.Delay 120
        // Case, spacing and a zero-width space do not make a new message.
        do! publish room alice 3UL "HELLO there\u200B"
        let! repeated = receive events
        rateLimited 3UL repeated
        do! publish room alice 4UL "different"
        let! later = receive events
        accepted 4UL later |> ignore
        do! stop room
    })

    case "channel creates the authoritative publication and orders snapshot before later events" (fun () -> task {
        let hostEvents, aliceEvents, bobEvents, nextEvents =
            Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<ChatRoomEvent>(),
            Channel.CreateUnbounded<ChatRoomEvent>(), Channel.CreateUnbounded<ChatRoomEvent>()
        use host = Agent.Start(AgentOptions.create "host", collect hostEvents)
        use alice = Agent.Start(AgentOptions.create "alice", collect aliceEvents)
        use bob = Agent.Start(AgentOptions.create "bob", collect bobEvents)
        use newcomer = Agent.Start(AgentOptions.create "next", collect nextEvents)
        use room = ChatRoomAgent.start config channelId (host.Ref.TryReliable().Value) |> ok
        let a, b, n = subscription 1UL alice, subscription 2UL bob, subscription 3UL newcomer
        do! post room (ChatRoomCommand.Join a)
        do! post room (ChatRoomCommand.Join b)
        let! _ = receive aliceEvents
        let! _ = receive bobEvents
        do! publish room a 7UL "first"
        let! first = receive aliceEvents
        let first = accepted 7UL first
        let! broadcast = receive bobEvents
        equal (ChatRoomEvent.Published first) broadcast
        equal a.Profile first.Author
        equal [] first.Flagged
        equal 1UL (ChatMessageId.value first.MessageId)
        equal channelId first.ChannelId
        equal 0L (first.SentAt.Ticks % TimeSpan.TicksPerMillisecond)

        do! post room (ChatRoomCommand.Join n)
        do! publish room b 8UL "second"
        let! snapshot = receive nextEvents
        equal [first] (joined snapshot).Messages
        let! second = receive bobEvents
        let second = accepted 8UL second
        let! next = receive nextEvents
        equal (ChatRoomEvent.Published second) next
        let! aliceCopy = receive aliceEvents
        equal (ChatRoomEvent.Published second) aliceCopy
        equal 2UL (ChatMessageId.value second.MessageId)
        let! retained = history room
        equal [first; second] retained.Messages
        check (not (aliceEvents.Reader.TryPeek() |> fst)) "Author received a second copy."
        do! stop room
    })

    case "flagged ranges travel with the stored message and its broadcast" (fun () -> task {
        let hostEvents, events = Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<ChatRoomEvent>()
        use host = Agent.Start(AgentOptions.create "host", collect hostEvents)
        use player = Agent.Start(AgentOptions.create "player", collect events)
        use room = ChatRoomAgent.start config channelId (host.Ref.TryReliable().Value) |> ok
        let alice = subscription 1UL player
        do! post room (ChatRoomCommand.Join alice)
        let! _ = receive events
        do! publish room alice 1UL "flag me"
        let! published = receive events
        let message = accepted 1UL published
        equal [{ Start = 0; Length = 4 }] message.Flagged
        let! retained = history room
        equal [{ Start = 0; Length = 4 }] retained.Messages.Head.Flagged
        do! stop room
    })

    case "Detach acknowledges removal and late old commands cannot affect a replacement connection" (fun () -> task {
        let hostEvents, events, replies = Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<ChatRoomEvent>(), Channel.CreateUnbounded<Guid>()
        use host = Agent.Start(AgentOptions.create "host", collect hostEvents)
        use player = Agent.Start(AgentOptions.create "player", collect events)
        use cleanup = Agent.Start(AgentOptions.create "cleanup", collect replies)
        use room = ChatRoomAgent.start config channelId (host.Ref.TryReliable().Value) |> ok
        let old = subscription 1UL player
        let detach id = post room (ChatRoomCommand.Detach { ConnectionId = id; ReplyTo = cleanup.Ref.TryReliable().Value })
        do! post room (ChatRoomCommand.Join old)
        let! _ = receive events
        do! detach old.ConnectionId
        let! removed = receive replies
        equal old.ConnectionId removed
        do! publish room old 3UL "refused"
        let! refusal = receive events
        match refusal with
        | ChatRoomEvent.Rejected(3UL, error) -> equal RequestRejectionCode.NotChannelMember error.Code
        | other -> failwithf "Expected correlated refusal: %A" other

        let replacement = { old with ConnectionId = Guid.NewGuid() }
        do! post room (ChatRoomCommand.Join replacement)
        let! _ = receive events
        do! detach old.ConnectionId
        let! _ = receive replies
        do! publish room replacement 4UL "accepted"
        let! message = receive events
        let message = accepted 4UL message
        let! retained = history room
        equal [message] retained.Messages
        equal 1UL (ChatMessageId.value message.MessageId)
        do! stop room
    })

    case "a full subscriber is removed without delaying the author or later fanout" (fun () -> task {
        let hostEvents, aliceEvents, slowEvents = Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<ChatRoomEvent>(), Channel.CreateUnbounded<ChatRoomEvent>()
        use host = Agent.Start(AgentOptions.create "host", collect hostEvents)
        use alice = Agent.Start(AgentOptions.create "alice", collect aliceEvents)
        use receiver = Agent.Start(options "slow" (AgentMailbox.boundedWait 1), slow slowEvents)
        use room = ChatRoomAgent.start config channelId (host.Ref.TryReliable().Value) |> ok
        let a = subscription 1UL alice
        let s = { ConnectionId = Guid.NewGuid(); Profile = profile 2UL; Events = receiver.Ref.TryReliable().Value.Map Value }
        do! post room (ChatRoomCommand.Join a)
        do! post room (ChatRoomCommand.Join s)
        let! _ = receive aliceEvents
        let! _ = receive slowEvents
        let! release = block receiver
        do! publish room a 1UL "one"
        let! first = receive aliceEvents
        accepted 1UL first |> ignore
        let! failure = receive hostEvents
        equal (SessionHostCommand.SlowConsumer s.ConnectionId) failure
        check (not release.Task.IsCompleted) "Slow subscriber was released before healthy progress."
        do! publish room a 2UL "two"
        let! second = receive aliceEvents
        accepted 2UL second |> ignore
        let! retained = history room
        equal 2 retained.Messages.Length
        equal 0 hostEvents.Reader.Count
        release.SetResult()
        do! stop room
        do! stop receiver
    })

    case "identity conflicts do not replace the existing author binding" (fun () -> task {
        let hostEvents, events, conflictingEvents = Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<ChatRoomEvent>(), Channel.CreateUnbounded<ChatRoomEvent>()
        use host = Agent.Start(AgentOptions.create "host", collect hostEvents)
        use player = Agent.Start(AgentOptions.create "player", collect events)
        use conflict = Agent.Start(AgentOptions.create "conflict", collect conflictingEvents)
        use room = ChatRoomAgent.start config channelId (host.Ref.TryReliable().Value) |> ok
        let original, duplicate = subscription 1UL player, subscription 1UL conflict
        do! post room (ChatRoomCommand.Join original)
        let! _ = receive events
        do! post room (ChatRoomCommand.Join duplicate)
        let! rejected = receive conflictingEvents
        match rejected with ChatRoomEvent.JoinFailed _ -> () | other -> failwithf "Expected join refusal: %A" other
        do! publish room original 1UL "still here"
        let! message = receive events
        equal original.Profile (accepted 1UL message).Author
        do! stop room
    })

    case "full cleanup acknowledgment is an observable source termination" (fun () -> task {
        let hostEvents, ackEvents = Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<Guid>()
        use host = Agent.Start(AgentOptions.create "host", collect hostEvents)
        use cleanup = Agent.Start(options "full-cleanup" (AgentMailbox.boundedWait 1), slow ackEvents)
        let! release = block cleanup
        use room = ChatRoomAgent.start config channelId (host.Ref.TryReliable().Value) |> ok
        do! post room (ChatRoomCommand.Detach { ConnectionId = Guid.NewGuid(); ReplyTo = cleanup.Ref.TryReliable().Value.Map Value })
        let! _ = terminal room.Completion
        check room.Completion.IsCanceled "A lost cleanup reply left the source silently running."
        release.SetResult()
        do! stop cleanup
    })

    case "full bounded host control output cannot silently lose slow-consumer notifications" (fun () -> task {
        let hostEvents, ignored = Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<ChatRoomEvent>()
        use host = Agent.Start(options "blocked-host" (AgentMailbox.boundedWait 1), slow hostEvents)
        use receiver = Agent.Start(options "blocked-recipient" (AgentMailbox.boundedWait 1), slow ignored)
        let! releaseHost = block host
        let! releaseReceiver = block receiver
        use room = ChatRoomAgent.start { config with MaxControlDeliveries = 1 } channelId (host.Ref.TryReliable().Value.Map Value) |> ok
        for number in [1UL; 2UL] do
            do! post room (ChatRoomCommand.Join { ConnectionId = Guid.NewGuid(); Profile = profile number; Events = receiver.Ref.TryReliable().Value.Map Value })
        let! _ = terminal room.Completion
        check room.Completion.IsCanceled "Source ignored overflowing its only control path."
        releaseHost.SetResult()
        releaseReceiver.SetResult()
        do! stop host
        do! stop receiver
    })

    case "independent channels allocate their own ordered message IDs" (fun () -> task {
        let hostEvents, events = Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<ChatRoomEvent>()
        use host = Agent.Start(AgentOptions.create "host", collect hostEvents)
        use player = Agent.Start(AgentOptions.create "player", collect events)
        let secondId = ChatChannelId.create 2UL |> ok
        use first = ChatRoomAgent.start config channelId (host.Ref.TryReliable().Value) |> ok
        use second = ChatRoomAgent.start config secondId (host.Ref.TryReliable().Value) |> ok
        let subscriber = subscription 1UL player
        do! post first (ChatRoomCommand.Join subscriber)
        let! _ = receive events
        do! post second (ChatRoomCommand.Join subscriber)
        let! _ = receive events
        do! publish first subscriber 1UL "first channel"
        let! one = receive events
        do! publish second subscriber 2UL "second channel"
        let! two = receive events
        let one, two = accepted 1UL one, accepted 2UL two
        equal 1UL (ChatMessageId.value one.MessageId)
        equal 1UL (ChatMessageId.value two.MessageId)
        equal channelId one.ChannelId
        equal secondId two.ChannelId
        do! stop first
        do! stop second
    })

    case "an unexpected delivery mapper fault remains observable as the original source failure" (fun () -> task {
        let hostEvents, ignored = Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<ChatRoomEvent>()
        use host = Agent.Start(AgentOptions.create "host", collect hostEvents)
        use receiver = Agent.Start(AgentOptions.create "receiver", collect ignored)
        use room = ChatRoomAgent.start config channelId (host.Ref.TryReliable().Value) |> ok
        let expected = InvalidOperationException("broken internal event mapper")
        let broken = receiver.Ref.TryReliable().Value.Map(fun (_: ChatRoomEvent) -> raise expected)
        do! post room (ChatRoomCommand.Join { ConnectionId = Guid.NewGuid(); Profile = profile 1UL; Events = broken })
        let! _ = terminal room.Completion
        check room.Completion.IsFaulted "Source failure was converted to successful completion."
        expectStopFault expected room.StopReason
    })

    case "history eviction preserves detached snapshots and reports a cursor gap" (fun () -> task {
        let hostEvents, events = Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<ChatRoomEvent>()
        use host = Agent.Start(AgentOptions.create "host", collect hostEvents)
        use player = Agent.Start(AgentOptions.create "player", collect events)
        use room = ChatRoomAgent.start config channelId (host.Ref.TryReliable().Value) |> ok
        let subscriber = subscription 1UL player
        do! post room (ChatRoomCommand.Join subscriber)
        let! _ = receive events
        for requestId in 1UL..4UL do
            do! publish room subscriber requestId $"message {requestId}"
            let! message = receive events
            accepted requestId message |> ignore

        do! post room (ChatRoomCommand.Join subscriber)
        let! response = receive events
        let snapshot = joined response
        let ids messages = messages |> List.map (fun (message: ChatMessage) -> ChatMessageId.value message.MessageId)
        equal 2 snapshot.HistoryCapacity
        equal (Set.singleton subscriber.Profile.PlayerId) snapshot.Players
        equal [3UL; 4UL] (ids snapshot.Messages)

        let cursor = ChatMessageId.create 1UL |> ok
        let! result = room.AskAsync(fun reply -> ChatRoomCommand.ReadHistory(ValueSome cursor, 1, reply)) |> awaitResult
        let page = ok result
        equal [3UL] (ids page.Messages)
        check page.HasGap "Evicted messages before this page were not reported."
        check page.HasMore "The retained fourth message was not reported."
        equal (ValueSome(ChatMessageId.create 3UL |> ok)) page.NextCursor

        do! publish room subscriber 5UL "fifth"
        let! _ = receive events
        let! current = history room
        equal [4UL; 5UL] (ids current.Messages)
        equal [3UL; 4UL] (ids snapshot.Messages)
        equal [3UL] (ids page.Messages)
        do! stop room
    })

]
