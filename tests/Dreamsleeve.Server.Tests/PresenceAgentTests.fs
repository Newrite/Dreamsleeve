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
let private config = { MailboxCapacity = 4; ControlReserve = 2; MaxControlDeliveries = 4; ReplicationIntervalMs = 60000 }
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
    ConnectionId = Guid.NewGuid(); Snapshot = Player.snapshot (Player.create (profile number)); Events = agent.Ref.TryReliable().Value
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

type private Fixture = {
    Presence: Agent<PresenceCommand>
    Host: Channel<SessionHostCommand>
    Alice: PresenceSubscription
    Bob: PresenceSubscription
    Late: PresenceSubscription
    AliceEvents: Channel<PresenceEvent>
    BobEvents: Channel<PresenceEvent>
    LateEvents: Channel<PresenceEvent>
    Cleanup: ReliableAgentRef<Guid>
    Acknowledgments: Channel<Guid>
}

let private withPresence settings run = task {
    let hostEvents, aliceEvents, bobEvents, lateEvents, acknowledgments =
        Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<PresenceEvent>(),
        Channel.CreateUnbounded<PresenceEvent>(), Channel.CreateUnbounded<PresenceEvent>(), Channel.CreateUnbounded<Guid>()
    use host = Agent.Start(AgentOptions.create "host", collect hostEvents)
    use alice = Agent.Start(AgentOptions.create "alice", collect aliceEvents)
    use bob = Agent.Start(AgentOptions.create "bob", collect bobEvents)
    use late = Agent.Start(AgentOptions.create "late", collect lateEvents)
    use cleanup = Agent.Start(AgentOptions.create "cleanup", collect acknowledgments)
    use presence = PresenceAgent.start settings (host.Ref.TryReliable().Value) |> ok
    let a, b = subscription 1UL alice, subscription 2UL bob
    do! post presence (PresenceCommand.Join a)
    let! _ = receive aliceEvents
    do! post presence (PresenceCommand.Join b)
    let! _ = receive bobEvents
    let! _ = receive aliceEvents
    do! run {
        Presence = presence; Host = hostEvents; Alice = a; Bob = b; Late = subscription 3UL late
        AliceEvents = aliceEvents; BobEvents = bobEvents; LateEvents = lateEvents
        Cleanup = cleanup.Ref.TryReliable().Value; Acknowledgments = acknowledgments
    }
    do! stop presence
}

let private changed fixture value =
    post fixture.Presence (PresenceCommand.Update(fixture.Alice.ConnectionId, value))

let private flushBoth fixture expected = task {
    do! post fixture.Presence PresenceCommand.Flush
    let! author = receive fixture.AliceEvents
    let! observer = receive fixture.BobEvents
    equal expected author
    equal author observer
}

let private location x =
    let form = FormKey.create (PluginName.create 255 "Skyrim.esm" |> ok) (LocalFormId.create 60u |> ok)
    PlayerLocation.create (Location.create form (LocationName.create 128 "Whiterun" |> ok))
        (Position.create x 2.0f 3.0f |> ok) Rotation.zero

let private character subscription =
    Player.create subscription.Snapshot.Data |> Player.beginCharacter (CharacterName.create 128 "Nerevar" |> ok)

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
        equal [a.Snapshot] (snapshot first)
        do! post presence (PresenceCommand.Join b)
        let! second = receive bobEvents
        equal [a.Snapshot; b.Snapshot] (snapshot second)
        let! joined = receive aliceEvents
        equal (PresenceEvent.Joined b.Snapshot) joined
        do! post presence (PresenceCommand.Detach detach)
        let! left = receive bobEvents
        equal (PresenceEvent.Left a.Snapshot.Data.PlayerId) left
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
        equal [replacement.Snapshot] (snapshot ready)
        do! detach old.ConnectionId
        let! _ = receive acknowledgments
        do! post presence (PresenceCommand.Join replacement)
        let! stillPresent = receive events
        equal [replacement.Snapshot] (snapshot stillPresent)
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
        let s = { ConnectionId = Guid.NewGuid(); Snapshot = Player.snapshot (Player.create (profile 2UL)); Events = receiver.Ref.TryReliable().Value.Map Value }
        do! post presence (PresenceCommand.Join a)
        let! _ = receive fastEvents
        do! post presence (PresenceCommand.Join s)
        let! _ = receive slowEvents
        let! _ = receive fastEvents
        let! release = block receiver
        do! post presence (PresenceCommand.Join n)
        let! initial = receive nextEvents
        equal [a.Snapshot; s.Snapshot; n.Snapshot] (snapshot initial)
        let! joined = receive fastEvents
        equal (PresenceEvent.Joined n.Snapshot) joined
        let! leftFast = receive fastEvents
        let! leftNew = receive nextEvents
        equal (PresenceEvent.Left s.Snapshot.Data.PlayerId) leftFast
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
        let rejected = { ConnectionId = Guid.NewGuid(); Snapshot = Player.snapshot (Player.create (profile 2UL)); Events = receiver.Ref.TryReliable().Value.Map Value }
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
    case "flush coalesces latest telemetry and location-only changes reach author and observer" (fun () ->
        withPresence config (fun fixture -> task {
            let player = character fixture.Alice
            let first = Player.snapshot player
            let latest = Player.snapshot (Player.replaceSample (ValueSome (location 10.0f)) Map.empty player)
            do! changed fixture first
            do! changed fixture latest
            do! flushBoth fixture (PresenceEvent.Updated latest)

            let moved = { latest with Location = ValueSome (location 20.0f) }
            do! changed fixture moved
            do! flushBoth fixture (PresenceEvent.Moved(moved.Data.PlayerId, moved.Location))
            do! changed fixture { moved with Location = ValueNone }
            do! flushBoth fixture (PresenceEvent.Moved(moved.Data.PlayerId, ValueNone))

            // Rejoining the same member is an ordered read barrier, never a reset.
            do! changed fixture { moved with Location = ValueNone }
            do! post fixture.Presence PresenceCommand.Flush
            do! post fixture.Presence (PresenceCommand.Join fixture.Alice)
            let! unchanged = receive fixture.AliceEvents
            equal [{ moved with Location = ValueNone }; fixture.Bob.Snapshot] (snapshot unchanged)
            equal 0 fixture.BobEvents.Reader.Count
        }))

    case "same-name character reset survives coalescing and replaces telemetry" (fun () ->
        withPresence config (fun fixture -> task {
            let name = CharacterName.create 128 "Nerevar" |> ok
            let key = ActorValueKey.create 128 "skyrim:health" |> ok
            let health = ActorValueInfo.create (ActorValueName.create 64 "Health" |> ok) (ActorValueState.resource 20.0f 100.0f |> ok)
            let player = character fixture.Alice |> Player.replaceSample (ValueSome (location 10.0f)) (Map.ofList [(key, health)])
            let first = Player.snapshot player
            do! changed fixture first
            do! flushBoth fixture (PresenceEvent.Updated first)

            let reset = Player.beginCharacter name player
            let left = Player.clearGameState reset
            let restarted = Player.beginCharacter name left |> Player.snapshot
            do! changed fixture (Player.snapshot reset)
            do! changed fixture (Player.snapshot left)
            do! changed fixture restarted
            do! flushBoth fixture (PresenceEvent.Updated restarted)
            equal (first.CharacterGeneration + 3UL) restarted.CharacterGeneration
            equal first.CharacterName restarted.CharacterName
            equal Map.empty restarted.ActorValues
            equal ValueNone restarted.Location
        }))

    case "late join sees latest full state before the next replication flush" (fun () ->
        withPresence config (fun fixture -> task {
            let latest = character fixture.Alice |> Player.replaceSample (ValueSome (location 5.0f)) Map.empty |> Player.snapshot
            do! changed fixture latest
            do! post fixture.Presence (PresenceCommand.Join fixture.Late)
            let! initial = receive fixture.LateEvents
            equal [latest; fixture.Bob.Snapshot; fixture.Late.Snapshot] (snapshot initial)
            for events in [fixture.AliceEvents; fixture.BobEvents] do
                let! published = receive events
                let! joined = receive events
                equal (PresenceEvent.Updated latest) published
                equal (PresenceEvent.Joined fixture.Late.Snapshot) joined
            do! post fixture.Presence PresenceCommand.Flush
            do! post fixture.Presence (PresenceCommand.Join fixture.Late)
            let! stable = receive fixture.LateEvents
            equal [latest; fixture.Bob.Snapshot; fixture.Late.Snapshot] (snapshot stable)
            equal 0 fixture.AliceEvents.Reader.Count
            equal 0 fixture.BobEvents.Reader.Count
        }))

    case "detach clears dirty state and old connection cannot update its replacement" (fun () ->
        withPresence config (fun fixture -> task {
            let latest = character fixture.Alice |> Player.snapshot
            do! changed fixture latest
            do! post fixture.Presence (PresenceCommand.Detach { ConnectionId = fixture.Alice.ConnectionId; ReplyTo = fixture.Cleanup })
            let! _ = receive fixture.Acknowledgments
            let! departed = receive fixture.BobEvents
            equal (PresenceEvent.Left latest.Data.PlayerId) departed

            let replacement = { fixture.Alice with ConnectionId = Guid.NewGuid() }
            do! post fixture.Presence (PresenceCommand.Join replacement)
            let! initial = receive fixture.AliceEvents
            equal [replacement.Snapshot; fixture.Bob.Snapshot] (snapshot initial)
            let! joined = receive fixture.BobEvents
            equal (PresenceEvent.Joined replacement.Snapshot) joined
            do! changed fixture latest
            do! post fixture.Presence PresenceCommand.Flush
            do! post fixture.Presence (PresenceCommand.Join replacement)
            let! stable = receive fixture.AliceEvents
            equal [replacement.Snapshot; fixture.Bob.Snapshot] (snapshot stable)
            equal 0 fixture.BobEvents.Reader.Count
        }))

    case "presence refuses an update that changes connection-owned identity" (fun () ->
        withPresence config (fun fixture -> task {
            do! changed fixture fixture.Bob.Snapshot
            let! refused = receive fixture.Host
            equal (SessionHostCommand.Close(fixture.Alice.ConnectionId, "presence_identity_conflict")) refused
            do! post fixture.Presence PresenceCommand.Flush
            do! post fixture.Presence (PresenceCommand.Join fixture.Alice)
            let! stable = receive fixture.AliceEvents
            equal [fixture.Alice.Snapshot; fixture.Bob.Snapshot] (snapshot stable)
            equal 0 fixture.BobEvents.Reader.Count
        }))

    case "dirty telemetry flushes automatically without a global runtime tick" (fun () ->
        withPresence { config with ReplicationIntervalMs = 10 } (fun fixture -> task {
            let latest = character fixture.Alice |> Player.snapshot
            do! changed fixture latest
            let! author = receive fixture.AliceEvents
            let! observer = receive fixture.BobEvents
            equal (PresenceEvent.Updated latest) author
            equal author observer
        }))

    case "Complete detaches the pending replication timer without waiting for its interval" (fun () ->
        withPresence config (fun fixture -> task {
            do! changed fixture (character fixture.Alice |> Player.snapshot)
            // The long configured interval must not extend the ownership barrier.
            fixture.Presence.Complete() |> ignore
            do! fixture.Presence.Completion.WaitAsync(TimeSpan.FromSeconds 1.)
        }))

    case "bootstrap baseline survives state reverting before the pending tick" (fun () -> task {
        for metadata in [false; true] do
            do! withPresence config (fun fixture -> task {
                let original = character fixture.Alice |> Player.snapshot
                do! changed fixture original
                do! flushBoth fixture (PresenceEvent.Updated original)
                let activity = PlayerActivity.create 256 64 ActivityKind.Menu ValueNone LockDifficulty.Unknown (ValueSome "inventory") |> ok
                let details = PlayerDetails.create ValueNone ValueNone activity ValueNone ValueNone
                let temporary =
                    if metadata then { original with Details = details }
                    else { original with Location = ValueSome (location 5.0f) }
                let event value =
                    if metadata then PresenceEvent.Updated value
                    else PresenceEvent.Moved(value.Data.PlayerId, value.Location)
                do! changed fixture temporary
                do! post fixture.Presence (PresenceCommand.Join fixture.Late)
                let! newcomer = receive fixture.LateEvents
                equal temporary (snapshot newcomer |> List.head)
                for events in [fixture.AliceEvents; fixture.BobEvents] do
                    let! published = receive events
                    equal (event temporary) published
                    let! joined = receive events
                    equal (PresenceEvent.Joined fixture.Late.Snapshot) joined

                do! changed fixture original
                do! flushBoth fixture (event original)
                let! restored = receive fixture.LateEvents
                equal (event original) restored
            })
    })

    case "join flush removes a slow existing subscriber without resurrecting its stale snapshot" (fun () -> task {
        let hostEvents, fastEvents, slowEvents =
            Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<PresenceEvent>(), Channel.CreateUnbounded<PresenceEvent>()
        use host = Agent.Start(AgentOptions.create "host", collect hostEvents)
        use fast = Agent.Start(AgentOptions.create "fast", collect fastEvents)
        use receiver = Agent.Start(options "slow" (AgentMailbox.boundedWait 1), slow slowEvents)
        use presence = PresenceAgent.start config (host.Ref.TryReliable().Value) |> ok
        let a = subscription 1UL fast
        let s = { ConnectionId = Guid.NewGuid(); Snapshot = Player.snapshot (Player.create (profile 2UL)); Events = receiver.Ref.TryReliable().Value.Map Value }
        do! post presence (PresenceCommand.Join a)
        let! _ = receive fastEvents
        do! post presence (PresenceCommand.Join s)
        let! _ = receive fastEvents
        let! _ = receive slowEvents
        let! release = block receiver
        let changed = character s |> Player.snapshot
        do! post presence (PresenceCommand.Update(s.ConnectionId, changed))
        do! post presence (PresenceCommand.Join s)
        let! updated = receive fastEvents
        let! left = receive fastEvents
        equal (PresenceEvent.Updated changed) updated
        equal (PresenceEvent.Left s.Snapshot.Data.PlayerId) left
        let! failure = receive hostEvents
        equal (SessionHostCommand.SlowConsumer s.ConnectionId) failure
        do! post presence (PresenceCommand.Join a)
        let! survivors = receive fastEvents
        equal [a.Snapshot] (snapshot survivors)
        check (not release.Task.IsCompleted) "Replication waited for the blocked recipient."
        release.SetResult()
        do! stop receiver
        do! stop presence
    })

]
