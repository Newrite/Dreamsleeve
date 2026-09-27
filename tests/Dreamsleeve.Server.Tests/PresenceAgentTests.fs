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
let private config = { MailboxCapacity = 4; ControlReserve = 2; MaxControlDeliveries = 4; ReplicationIntervalMs = 60000; VisibilityDistance = 8192.0f }
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

let private withPresenceUsing settings initialize run = task {
    let hostEvents, aliceEvents, bobEvents, lateEvents, acknowledgments =
        Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<PresenceEvent>(),
        Channel.CreateUnbounded<PresenceEvent>(), Channel.CreateUnbounded<PresenceEvent>(), Channel.CreateUnbounded<Guid>()
    use host = Agent.Start(AgentOptions.create "host", collect hostEvents)
    use alice = Agent.Start(AgentOptions.create "alice", collect aliceEvents)
    use bob = Agent.Start(AgentOptions.create "bob", collect bobEvents)
    use late = Agent.Start(AgentOptions.create "late", collect lateEvents)
    use cleanup = Agent.Start(AgentOptions.create "cleanup", collect acknowledgments)
    use presence = PresenceAgent.start settings (host.Ref.TryReliable().Value) |> ok
    let a, b = initialize (subscription 1UL alice, subscription 2UL bob)
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

let private withPresence settings run = withPresenceUsing settings id run

let private readSnapshot fixture subscription events = task {
    do! post fixture.Presence (PresenceCommand.Join subscription)
    let! event = receive events
    return snapshot event
}

let private changed fixture value =
    post fixture.Presence (PresenceCommand.Update(fixture.Alice.ConnectionId, value))

let private flushViews fixture expectedAuthor expectedObserver = task {
    do! post fixture.Presence PresenceCommand.Flush
    let! author = receive fixture.AliceEvents
    let! observer = receive fixture.BobEvents
    equal expectedAuthor author
    equal expectedObserver observer
}

let private flushBoth fixture expected = flushViews fixture expected expected

let private location x =
    let form = FormKey.create (PluginName.create 255 "Skyrim.esm" |> ok) (LocalFormId.create 60u |> ok)
    PlayerLocation.create (Location.create form (LocationName.create 128 "Whiterun" |> ok))
        (Position.create x 2.0f 3.0f |> ok) Rotation.zero

let private character subscription =
    Player.create subscription.Snapshot.Data |> Player.beginCharacter (CharacterName.create 128 "Nerevar" |> ok)

let private withPositions settings aliceLocation bobLocation run =
    let initialize (alice, bob) =
        let positioned subscription location =
            { subscription with Snapshot = character subscription |> Player.applyUpdate (PlayerUpdate.Move location) |> Player.snapshot }
        positioned alice aliceLocation, positioned bob bobLocation
    withPresenceUsing settings initialize run

let private hidden (value: PlayerSnapshot) = { value with Location = ValueNone }

// Repeated Join is a FIFO barrier. Collect preceding deltas without sleeps or
// assumptions about execution order in the recipient agents.
let private view fixture subscription events = task {
    do! post fixture.Presence (PresenceCommand.Join subscription)
    let changes = ResizeArray<PresenceEvent>()
    let mutable result = None
    while result.IsNone do
        let! event = receive events
        match event with
        | PresenceEvent.Snapshot players -> result <- Some players
        | other -> changes.Add other
    return List.ofSeq changes, result.Value
}

let private flushViewsNow fixture = task {
    do! post fixture.Presence PresenceCommand.Flush
    let! alice = view fixture fixture.Alice fixture.AliceEvents
    let! bob = view fixture fixture.Bob fixture.BobEvents
    return alice, bob
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
            let latest = Player.snapshot (Player.applyUpdate (PlayerUpdate.Move(ValueSome (location 10.0f))) player)
            do! changed fixture first
            do! changed fixture latest
            do! flushViews fixture (PresenceEvent.Updated latest) (PresenceEvent.Updated(hidden latest))

            let moved = { latest with Location = ValueSome (location 20.0f) }
            do! changed fixture moved
            do! post fixture.Presence PresenceCommand.Flush
            let! movement = receive fixture.AliceEvents
            equal (PresenceEvent.Moved(moved.Data.PlayerId, moved.Location)) movement
            let! observer = readSnapshot fixture fixture.Bob fixture.BobEvents
            equal [hidden moved; fixture.Bob.Snapshot] observer
            do! changed fixture { moved with Location = ValueNone }
            do! post fixture.Presence PresenceCommand.Flush
            let! cleared = receive fixture.AliceEvents
            equal (PresenceEvent.Moved(moved.Data.PlayerId, ValueNone)) cleared
            let! observer = readSnapshot fixture fixture.Bob fixture.BobEvents
            equal [hidden moved; fixture.Bob.Snapshot] observer

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
            let player = character fixture.Alice |> Player.applyUpdate (PlayerUpdate.Move(ValueSome (location 10.0f))) |> Player.replaceActorValues (Map.ofList [(key, health)])
            let first = Player.snapshot player
            do! changed fixture first
            do! flushViews fixture (PresenceEvent.Updated first) (PresenceEvent.Updated(hidden first))

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
            let latest = character fixture.Alice |> Player.applyUpdate (PlayerUpdate.Move(ValueSome (location 5.0f))) |> Player.snapshot
            do! changed fixture latest
            do! post fixture.Presence (PresenceCommand.Join fixture.Late)
            let! initial = receive fixture.LateEvents
            equal [hidden latest; fixture.Bob.Snapshot; fixture.Late.Snapshot] (snapshot initial)
            for events, expected in [fixture.AliceEvents, latest; fixture.BobEvents, hidden latest] do
                let! published = receive events
                let! joined = receive events
                equal (PresenceEvent.Updated expected) published
                equal (PresenceEvent.Joined fixture.Late.Snapshot) joined
            do! post fixture.Presence PresenceCommand.Flush
            do! post fixture.Presence (PresenceCommand.Join fixture.Late)
            let! stable = receive fixture.LateEvents
            equal [hidden latest; fixture.Bob.Snapshot; fixture.Late.Snapshot] (snapshot stable)
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

    case "bootstrap baseline survives metadata reverting before the pending tick" (fun () ->
        withPresence config (fun fixture -> task {
            let original = character fixture.Alice |> Player.snapshot
            do! changed fixture original
            do! flushBoth fixture (PresenceEvent.Updated original)
            let activity = PlayerActivity.create 256 64 ActivityKind.Menu ValueNone LockDifficulty.Unknown (ValueSome "inventory") |> ok
            let details = PlayerDetails.create ValueNone ValueNone activity ValueNone ValueNone
            let temporary = { original with Details = details }
            do! changed fixture temporary
            do! post fixture.Presence (PresenceCommand.Join fixture.Late)
            let! newcomer = receive fixture.LateEvents
            equal temporary (snapshot newcomer |> List.head)
            for events in [fixture.AliceEvents; fixture.BobEvents] do
                let! published = receive events
                equal (PresenceEvent.MetadataChanged(original.Data.PlayerId, ValueNone, ValueSome details)) published
                let! joined = receive events
                equal (PresenceEvent.Joined fixture.Late.Snapshot) joined

            do! changed fixture original
            do! flushBoth fixture (PresenceEvent.MetadataChanged(original.Data.PlayerId, ValueNone, ValueSome original.Details))
            let! restored = receive fixture.LateEvents
            equal (PresenceEvent.MetadataChanged(original.Data.PlayerId, ValueNone, ValueSome original.Details)) restored
        }))

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


    case "radius boundary is inclusive and metadata cannot expose hidden locations" (fun () ->
        withPositions { config with VisibilityDistance = 10.0f } (ValueSome (location 0.0f)) (ValueSome (location 10.0f)) (fun fixture -> task {
            let! _, initial = view fixture fixture.Bob fixture.BobEvents
            equal fixture.Alice.Snapshot (List.head initial)
            let outside = { fixture.Alice.Snapshot with Location = ValueSome (location -1.0f) }
            do! changed fixture outside
            let! (author, _), (observer, _) = flushViewsNow fixture
            equal [PresenceEvent.Moved(outside.Data.PlayerId, outside.Location); PresenceEvent.Moved(fixture.Bob.Snapshot.Data.PlayerId, ValueNone)] author
            equal [PresenceEvent.Moved(outside.Data.PlayerId, ValueNone)] observer

            let hiddenMove = { outside with Location = ValueSome (location -2.0f) }
            do! changed fixture hiddenMove
            let! _, (observer, _) = flushViewsNow fixture
            equal [] observer

            let renamed = { hiddenMove with CharacterName = ValueSome (CharacterName.create 128 "Renamed" |> ok) }
            do! changed fixture renamed
            let! _, (observer, online) = flushViewsNow fixture
            equal [PresenceEvent.Updated(hidden renamed)] observer
            equal 2 online.Length
        }))

    case "observer movement restores stationary source and clears it on space change or unknown position" (fun () ->
        withPositions { config with VisibilityDistance = 10.0f } (ValueSome (location 0.0f)) (ValueSome (location 20.0f)) (fun fixture -> task {
            let bob = { fixture.Bob.Snapshot with Location = ValueSome (location 5.0f) }
            do! post fixture.Presence (PresenceCommand.Update(fixture.Bob.ConnectionId, bob))
            let! _, (events, online) = flushViewsNow fixture
            check (List.contains (PresenceEvent.Moved(fixture.Alice.Snapshot.Data.PlayerId, fixture.Alice.Snapshot.Location)) events) "Stationary source enters view."
            equal fixture.Alice.Snapshot (List.head online)

            let otherSpace = FormKey.create (PluginName.create 255 "Other.esm" |> ok) (LocalFormId.create 60u |> ok)
            let target = PlayerLocation.create (Location.create otherSpace (LocationName.create 128 "Whiterun" |> ok)) (location 0.0f).Position Rotation.zero
            let far = { bob with Location = ValueSome target }
            do! post fixture.Presence (PresenceCommand.Update(fixture.Bob.ConnectionId, far))
            let! _, (events, online) = flushViewsNow fixture
            check (List.contains (PresenceEvent.Moved(fixture.Alice.Snapshot.Data.PlayerId, ValueNone)) events) "Different FormKey hides same coordinates and label."
            equal (hidden fixture.Alice.Snapshot) (List.head online)

            do! post fixture.Presence (PresenceCommand.Update(fixture.Bob.ConnectionId, bob))
            let! _ = flushViewsNow fixture
            do! post fixture.Presence (PresenceCommand.Update(fixture.Bob.ConnectionId, { bob with Location = ValueNone }))
            let! _, (events, _) = flushViewsNow fixture
            check (List.contains (PresenceEvent.Moved(fixture.Alice.Snapshot.Data.PlayerId, ValueNone)) events) "Losing own position clears remote presence."
        }))

    case "simultaneously moving peers use a common old baseline and late join can observe reversal" (fun () ->
        withPositions { config with VisibilityDistance = 10.0f } (ValueSome (location 0.0f)) (ValueSome (location 5.0f)) (fun fixture -> task {
            let a = { fixture.Alice.Snapshot with Location = ValueSome (location 100.0f) }
            let b = { fixture.Bob.Snapshot with Location = ValueSome (location 105.0f) }
            do! changed fixture a
            do! post fixture.Presence (PresenceCommand.Update(fixture.Bob.ConnectionId, b))
            let! (aliceEvents, _), (bobEvents, _) = flushViewsNow fixture
            equal 2 aliceEvents.Length
            equal 2 bobEvents.Length
            check (List.contains (PresenceEvent.Moved(a.Data.PlayerId, a.Location)) bobEvents) "Both move together and stay visible."
            check (List.contains (PresenceEvent.Moved(b.Data.PlayerId, b.Location)) aliceEvents) "Visibility does not depend on source iteration order."

            let temporary = { a with Location = ValueSome (location 101.0f) }
            do! changed fixture temporary
            let late = { fixture.Late with Snapshot = { fixture.Late.Snapshot with Location = b.Location } }
            do! post fixture.Presence (PresenceCommand.Join late)
            let! initial = receive fixture.LateEvents
            equal temporary (snapshot initial |> List.head)
            do! changed fixture a
            do! post fixture.Presence PresenceCommand.Flush
            let! restored = receive fixture.LateEvents
            equal (PresenceEvent.Moved(a.Data.PlayerId, a.Location)) restored
        }))

    case "zero radius and invalid configuration are handled before startup" (fun () -> task {
        let hostEvents = Channel.CreateUnbounded<SessionHostCommand>()
        use host = Agent.Start(AgentOptions.create "host", collect hostEvents)
        for radius in [-1.0f; Single.NaN; Single.PositiveInfinity; Single.NegativeInfinity] do
            Expect.isError (PresenceAgent.start { config with VisibilityDistance = radius } (host.Ref.TryReliable().Value)) "Invalid radius."
        do! withPositions { config with VisibilityDistance = 0.0f } (ValueSome (location 0.0f)) (ValueSome (location 0.0f)) (fun fixture -> task {
            let! _, initial = view fixture fixture.Bob fixture.BobEvents
            equal fixture.Alice.Snapshot (List.head initial)
            do! changed fixture { fixture.Alice.Snapshot with Location = ValueSome (location 0.001f) }
            let! _, (events, _) = flushViewsNow fixture
            equal [PresenceEvent.Moved(fixture.Alice.Snapshot.Data.PlayerId, ValueNone)] events
        })
    })


    case "tick combines latest metadata separately from movement and suppresses reverted components" (fun () ->
        withPositions config (ValueSome (location 0.0f)) (ValueSome (location 5.0f)) (fun fixture -> task {
            let initial = fixture.Alice.Snapshot
            let key = ActorValueKey.create 128 "skyrim:health" |> ok
            let entry = ActorValueInfo.create (ActorValueName.create 128 "Health" |> ok) (ActorValueState.scalar 0.0f |> ok)
            let details = PlayerDetails.create ValueNone (ValueSome 0u) PlayerActivity.unknown ValueNone ValueNone
            let first = { initial with ActorValues = Map.ofList [key, entry]; Location = ValueSome (location 1.0f) }
            let latest = { first with Details = details; Location = ValueSome (location 2.0f) }
            do! changed fixture first
            do! changed fixture latest
            let! (author, _), (observer, _) = flushViewsNow fixture
            let expected = [PresenceEvent.MetadataChanged(initial.Data.PlayerId, ValueSome latest.ActorValues, ValueSome details);
                            PresenceEvent.Moved(initial.Data.PlayerId, latest.Location)]
            equal expected author
            equal expected observer

            do! changed fixture { latest with Details = PlayerDetails.empty }
            do! changed fixture latest
            let! (author, _), (observer, _) = flushViewsNow fixture
            equal [] author
            equal [] observer

            do! changed fixture { latest with ActorValues = Map.empty }
            let! (author, _), (observer, _) = flushViewsNow fixture
            let cleared = [PresenceEvent.MetadataChanged(initial.Data.PlayerId, ValueSome Map.empty, ValueNone)]
            equal cleared author
            equal cleared observer
        }))

]
