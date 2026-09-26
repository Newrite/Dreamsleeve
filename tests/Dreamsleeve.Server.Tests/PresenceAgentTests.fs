module Dreamsleeve.Server.Tests.PresenceAgentTests

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
let private config = { MailboxCapacity = 4; ControlReserve = 2; MaxControlDeliveries = 4 }
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
let private subscription number (agent: Agent<PresenceEvent>) = {
    ConnectionId = Guid.NewGuid(); Profile = profile number; Events = agent.Ref.TryReliable().Value
}
let private snapshot = function
    | PresenceEvent.Snapshot profiles -> profiles
    | other -> failwithf "Expected presence snapshot: %A" other

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

let tests = testList "PresenceAgent" [
    case "snapshot includes self and later joins and leaves stay in source order" (fun () -> task {
        let hostEvents, aliceEvents, bobEvents, acknowledgments =
            Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<PresenceEvent>(),
            Channel.CreateUnbounded<PresenceEvent>(), Channel.CreateUnbounded<Guid>()
        use host = Agent.Start(AgentOptions.create "host", collect hostEvents)
        use alice = Agent.Start(AgentOptions.create "alice", collect aliceEvents)
        use bob = Agent.Start(AgentOptions.create "bob", collect bobEvents)
        use cleanup = Agent.Start(AgentOptions.create "cleanup", collect acknowledgments)
        use presence = PresenceAgent.start config (host.Ref.TryReliable().Value) |> ok
        let a, b = subscription 1UL alice, subscription 2UL bob
        let detach = { ConnectionId = a.ConnectionId; ReplyTo = cleanup.Ref.TryReliable().Value }
        do! post presence (PresenceCommand.Join a)
        let! first = receive aliceEvents
        equal [a.Profile] (snapshot first)
        do! post presence (PresenceCommand.Join b)
        let! second = receive bobEvents
        equal [a.Profile; b.Profile] (snapshot second)
        let! joined = receive aliceEvents
        equal (PresenceEvent.Joined b.Profile) joined
        do! post presence (PresenceCommand.Detach detach)
        let! left = receive bobEvents
        equal (PresenceEvent.Left a.Profile.PlayerId) left
        let! ack = receive acknowledgments
        equal a.ConnectionId ack
        do! post presence (PresenceCommand.Detach detach)
        let! _ = receive acknowledgments
        equal 0 bobEvents.Reader.Count
        do! stop presence
    })

    case "an old connection cannot detach the replacement with the same player ID" (fun () -> task {
        let hostEvents, events, acknowledgments = Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<PresenceEvent>(), Channel.CreateUnbounded<Guid>()
        use host = Agent.Start(AgentOptions.create "host", collect hostEvents)
        use player = Agent.Start(AgentOptions.create "player", collect events)
        use cleanup = Agent.Start(AgentOptions.create "cleanup", collect acknowledgments)
        use presence = PresenceAgent.start config (host.Ref.TryReliable().Value) |> ok
        let old = subscription 1UL player
        let replacement = { old with ConnectionId = Guid.NewGuid() }
        let detach connectionId = post presence (PresenceCommand.Detach { ConnectionId = connectionId; ReplyTo = cleanup.Ref.TryReliable().Value })
        do! post presence (PresenceCommand.Join old)
        let! _ = receive events
        do! post presence (PresenceCommand.Join replacement)
        let! refused = receive hostEvents
        equal (SessionHostCommand.Close(replacement.ConnectionId, "presence_identity_conflict")) refused
        do! detach old.ConnectionId
        let! _ = receive acknowledgments
        do! post presence (PresenceCommand.Join replacement)
        let! ready = receive events
        equal [replacement.Profile] (snapshot ready)
        do! detach old.ConnectionId
        let! _ = receive acknowledgments
        do! post presence (PresenceCommand.Join replacement)
        let! stillPresent = receive events
        equal [replacement.Profile] (snapshot stillPresent)
        do! stop presence
    })

    case "a full subscriber is removed and its Left follows the triggering event for healthy peers" (fun () -> task {
        let hostEvents, fastEvents, slowEvents, nextEvents =
            Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<PresenceEvent>(),
            Channel.CreateUnbounded<PresenceEvent>(), Channel.CreateUnbounded<PresenceEvent>()
        use host = Agent.Start(AgentOptions.create "host", collect hostEvents)
        use fast = Agent.Start(AgentOptions.create "fast", collect fastEvents)
        use receiver = Agent.Start(options "slow" (AgentMailbox.boundedWait 1), slow slowEvents)
        use newcomer = Agent.Start(AgentOptions.create "newcomer", collect nextEvents)
        use presence = PresenceAgent.start config (host.Ref.TryReliable().Value) |> ok
        let a, n = subscription 1UL fast, subscription 3UL newcomer
        let s = { ConnectionId = Guid.NewGuid(); Profile = profile 2UL; Events = receiver.Ref.TryReliable().Value.Map Value }
        do! post presence (PresenceCommand.Join a)
        let! _ = receive fastEvents
        do! post presence (PresenceCommand.Join s)
        let! _ = receive slowEvents
        let! _ = receive fastEvents
        let! release = block receiver
        do! post presence (PresenceCommand.Join n)
        let! initial = receive nextEvents
        equal [a.Profile; s.Profile; n.Profile] (snapshot initial)
        let! joined = receive fastEvents
        equal (PresenceEvent.Joined n.Profile) joined
        let! leftFast = receive fastEvents
        let! leftNew = receive nextEvents
        equal (PresenceEvent.Left s.Profile.PlayerId) leftFast
        equal leftFast leftNew
        let! failure = receive hostEvents
        equal (SessionHostCommand.SlowConsumer s.ConnectionId) failure
        check (not release.Task.IsCompleted) "Other subscribers waited for the slow recipient."
        release.SetResult()
        do! stop presence
        do! stop receiver
    })

    case "refused initial snapshot creates no joined or left event for existing subscribers" (fun () -> task {
        let hostEvents, events, ignored, acknowledgments =
            Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<PresenceEvent>(),
            Channel.CreateUnbounded<PresenceEvent>(), Channel.CreateUnbounded<Guid>()
        use host = Agent.Start(AgentOptions.create "host", collect hostEvents)
        use healthy = Agent.Start(AgentOptions.create "healthy", collect events)
        use receiver = Agent.Start(options "blocked" (AgentMailbox.boundedWait 1), slow ignored)
        use cleanup = Agent.Start(AgentOptions.create "cleanup", collect acknowledgments)
        use presence = PresenceAgent.start config (host.Ref.TryReliable().Value) |> ok
        do! post presence (PresenceCommand.Join(subscription 1UL healthy))
        let! _ = receive events
        let! release = block receiver
        let rejected = { ConnectionId = Guid.NewGuid(); Profile = profile 2UL; Events = receiver.Ref.TryReliable().Value.Map Value }
        do! post presence (PresenceCommand.Join rejected)
        let! failed = receive hostEvents
        equal (SessionHostCommand.SlowConsumer rejected.ConnectionId) failed
        do! post presence (PresenceCommand.Detach { ConnectionId = rejected.ConnectionId; ReplyTo = cleanup.Ref.TryReliable().Value })
        let! _ = receive acknowledgments
        equal 0 events.Reader.Count
        release.SetResult()
        do! stop presence
        do! stop receiver
    })

    case "cleanup acknowledgment overflow terminates the source visibly" (fun () -> task {
        let hostEvents, ignored = Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<Guid>()
        use host = Agent.Start(AgentOptions.create "host", collect hostEvents)
        use cleanup = Agent.Start(options "blocked-cleanup" (AgentMailbox.boundedWait 1), slow ignored)
        let! release = block cleanup
        use presence = PresenceAgent.start config (host.Ref.TryReliable().Value) |> ok
        do! post presence (PresenceCommand.Detach { ConnectionId = Guid.NewGuid(); ReplyTo = cleanup.Ref.TryReliable().Value.Map Value })
        let! _ = terminal presence.Completion
        check presence.Completion.IsCanceled "Cleanup failure was swallowed."
        release.SetResult()
        do! stop cleanup
    })
]
