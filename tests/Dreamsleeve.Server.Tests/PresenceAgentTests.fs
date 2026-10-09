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

let private tick () =
    let now = System.Diagnostics.Stopwatch.GetTimestamp()
    PresenceCommand.Flush { DueTimestamp = now; QueuedTimestamp = now }

let private ok = function Ok value -> value | Error error -> failwithf "%A" error
let private config = { MailboxCapacity = 4; ControlReserve = 2; MaxControlDeliveries = 4; ReplicationIntervalMs = 60000; VisibilityDistance = 8192.0f }
let private profile number =
    PlayerData.create (PlayerId.create number |> ok)
        (Username.create 32 $"player{number}" |> ok) (DisplayName.create 64 $"Player {number}" |> ok) NameColor.unknown
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
    | PresenceEvent.Snapshot(profiles, _) -> profiles
    | other -> failwithf "Expected presence snapshot: %A" other

/// The reliable change of one tick or membership event, with its kinds.
let private changeOf = function
    | PresenceEvent.Changed(change, kinds) -> change, kinds
    | other -> failwithf "Expected a presence change: %A" other

let private changedBy change = PresenceEvent.Changed(change, ActorValueKinds.none)
let private joinedEvent player = changedBy { PresenceChange.empty with Joined = [ player ] }
let private updatedEvent player = changedBy { PresenceChange.empty with Updated = [ player ] }
let private leftEvent playerId = changedBy { PresenceChange.empty with Left = [ playerId ] }
let private detailsEvent playerId previous latest =
    changedBy { PresenceChange.empty with Metadata = [ { PlayerId = playerId; ActorValues = ValueNone; Details = DetailsPatch.between previous latest } ] }

let private actorKey value = ActorValueKey.create 128 value |> ok
let private actorName value = ActorValueName.create 64 value |> ok
let private reading name current = ActorValueInfo.create (actorName name) (ActorValueState.resource current 100)
let private kind id key name : ActorValueKind = { Id = id; Key = actorKey key; DisplayName = actorName name }
let private idsOf (kinds: ActorValueKind list) = ActorValueKindIndex.Create kinds
let private valuesPatch playerId removed set : MetadataPatch =
    { PlayerId = playerId; Details = ValueNone; ActorValues = ValueSome { Removed = removed; Set = set } }

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
    use host = TestAgent.Start(AgentOptions.create "host", collect hostEvents)
    use alice = TestAgent.Start(AgentOptions.create "alice", collect aliceEvents)
    use bob = TestAgent.Start(AgentOptions.create "bob", collect bobEvents)
    use late = TestAgent.Start(AgentOptions.create "late", collect lateEvents)
    use cleanup = TestAgent.Start(AgentOptions.create "cleanup", collect acknowledgments)
    use presence = PresenceAgent.start settings (host.Ref.TryReliable().Value) |> expectStarted |> fun owner -> owner.Owner
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

let private changedBob fixture value =
    post fixture.Presence (PresenceCommand.Update(fixture.Bob.ConnectionId, value))

/// A subscription's player publishing these readings.
let private publishes subscription (values: (string * string * int) list) =
    { subscription.Snapshot with ActorValues = values |> List.map (fun (key, name, current) -> actorKey key, reading name current) |> Map.ofList }

let private flushViews fixture expectedAuthor expectedObserver = task {
    do! post fixture.Presence (tick ())
    let! author = receive fixture.AliceEvents
    let! observer = receive fixture.BobEvents
    equal expectedAuthor author
    equal expectedObserver observer
}

let private flushBoth fixture expected = flushViews fixture expected expected

let private location x =
    let form = FormKey.create (PluginName.create 255 "Skyrim.esm" |> ok) (LocalFormId.create 60u |> ok)
    PlayerLocation.create (Location.create form (LocationName.create 128 "Whiterun" |> ok))
        (Position.create x 2.0f 3.0f |> ok) CameraDirection.zero

let private profileOf (snapshot: PlayerSnapshot) =
    match snapshot.Identity with
    | PublicIdentity.Profile profile -> profile
    | PublicIdentity.Pseudonymous _ -> failwith "Expected a profile."

let private character subscription =
    Player.create (profileOf subscription.Snapshot) |> Player.beginCharacter (CharacterName.create 128 "Nerevar" |> ok)

let private withPositions settings aliceLocation bobLocation run =
    let initialize (alice, bob) =
        let positioned subscription location =
            { subscription with Snapshot = character subscription |> Player.applyUpdate (PlayerUpdate.SetLocation(1UL, location)) |> Player.snapshot }
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
        | PresenceEvent.Snapshot(players, _) -> result <- Some players
        | PresenceEvent.Moved movements ->
            for movement in movements do changes.Add(PresenceEvent.Moved [|movement|])
        | other -> changes.Add other
    return List.ofSeq changes, result.Value
}

/// The reliable baselines and clears of one source among collected events.
let private visibilityOf playerId events =
    events |> List.collect (function
        | PresenceEvent.Changed(change, _) -> change.Visibility |> List.filter (fun (value: VisibilityChange) -> value.PlayerId = playerId)
        | PresenceEvent.Snapshot _ | PresenceEvent.Moved _ -> [])

/// The place of every collected change that carries visibility.
let private spaces events =
    events |> List.choose (function
        | PresenceEvent.Changed(change, _) when not change.Visibility.IsEmpty -> Some change.Space
        | PresenceEvent.Changed _ | PresenceEvent.Snapshot _ | PresenceEvent.Moved _ -> None)

let private changeCount events =
    events |> List.filter (function PresenceEvent.Changed _ -> true | PresenceEvent.Snapshot _ | PresenceEvent.Moved _ -> false) |> List.length

let private flushViewsNow fixture = task {
    do! post fixture.Presence (tick ())
    let! alice = view fixture fixture.Alice fixture.AliceEvents
    let! bob = view fixture fixture.Bob fixture.BobEvents
    return alice, bob
}

// Count the real producer output without retaining a dense history of events.
let private observationModeCase mode = case $"dense128 phantom observation mode {mode} preserves membership and bounds atomic batches" (fun () -> task {
    let mutable members, departed, views, hidden, closes = 0, 0, 0, 0, 0
    let mutable largestArray, largestTurn = 0, 0
    let count observation =
        let rec visit = function
            | PhantomObservation.Batch values ->
                largestArray <- max largestArray values.Length
                let mutable facts = 0
                for value in values do facts <- facts + visit value
                facts
            | PhantomObservation.Member _ -> members <- members + 1; 1
            | PhantomObservation.Departed _ -> departed <- departed + 1; 1
            | PhantomObservation.View _ -> views <- views + 1; 1
            | PhantomObservation.Hidden _ -> hidden <- hidden + 1; 1
        visit observation
    use host = TestAgent.Start(AgentOptions.create "phantom-observation-host", fun _ command ->
        match command with
        | SessionHostCommand.ObservePhantoms observation -> largestTurn <- max largestTurn (count observation)
        | SessionHostCommand.Close _ -> closes <- closes + 1
        | _ -> ()
        Task.FromResult())
    use events = TestAgent.Start(AgentOptions.create "dense-presence-events", fun _ (_: PresenceEvent) -> Task.FromResult())
    use cleanup = TestAgent.Start(AgentOptions.create "dense-presence-cleanup", fun _ (_: Guid) -> Task.FromResult())
    let settings = { config with MailboxCapacity = 512; ControlReserve = 128; MaxControlDeliveries = 1024 }
    use presence = PresenceAgent.startObserved mode settings (host.Ref.TryReliable().Value) |> expectStarted |> fun owner -> owner.Owner
    let subscriptions = Array.init 128 (fun index ->
        let value = subscription (uint64 index + 1UL) events
        { value with Snapshot = character value |> Player.applyUpdate (PlayerUpdate.SetLocation(1UL, ValueSome(location (float32 index)))) |> Player.snapshot })
    for value in subscriptions do do! post presence (PresenceCommand.Join value)
    do! post presence (tick())
    // Move every endpoint to exercise a full dense authority/distance turn.
    for index in 0 .. subscriptions.Length - 1 do
        let previous = subscriptions[index]
        let changed = { previous.Snapshot with Location = ValueSome(location (float32 index + 1.0f)); MovementContext = if index = 0 then 2UL else 1UL }
        do! post presence (PresenceCommand.Update(previous.ConnectionId, changed))
    do! post presence (tick())
    do! post presence (PresenceCommand.Detach { ConnectionId = subscriptions[0].ConnectionId; ReplyTo = cleanup.Ref.TryReliable().Value })
    do! stop presence
    do! stop host
    do! stop events
    do! stop cleanup
    equal 0 closes
    match mode with
    | PhantomObservationMode.Disabled ->
        equal 0 members; equal 0 departed; equal 0 views; equal 0 hidden; equal 0 largestArray
    | PhantomObservationMode.Membership ->
        equal 256 members; equal 1 departed; equal 0 views; equal 0 hidden
    | PhantomObservationMode.Full ->
        equal 256 members; equal 1 departed
        equal (2 * 128 * 127) views // Initial membership and one movement turn; idle ticks emit no duplicate views.
        check (largestTurn >= 128 * 127) "A dense authority turn remains one atomic host message."
        check (largestArray <= 4096) "Batch reference arrays stay off the large object heap."
})

let tests = testList "PresenceAgent" [
    observationModeCase PhantomObservationMode.Disabled
    observationModeCase PhantomObservationMode.Membership
    observationModeCase PhantomObservationMode.Full

    case "snapshot includes self and later joins and leaves stay in source order" (fun () -> task {
        let hostEvents, aliceEvents, bobEvents, acknowledgments =
            Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<PresenceEvent>(),
            Channel.CreateUnbounded<PresenceEvent>(), Channel.CreateUnbounded<Guid>()
        use host = TestAgent.Start(AgentOptions.create "host", collect hostEvents)
        use alice = TestAgent.Start(AgentOptions.create "alice", collect aliceEvents)
        use bob = TestAgent.Start(AgentOptions.create "bob", collect bobEvents)
        use cleanup = TestAgent.Start(AgentOptions.create "cleanup", collect acknowledgments)
        use presence = PresenceAgent.start config (host.Ref.TryReliable().Value) |> expectStarted |> fun owner -> owner.Owner
        let a, b = subscription 1UL alice, subscription 2UL bob
        let detach = { ConnectionId = a.ConnectionId; ReplyTo = cleanup.Ref.TryReliable().Value }
        do! post presence (PresenceCommand.Join a)
        let! first = receive aliceEvents
        equal [a.Snapshot] (snapshot first)
        do! post presence (PresenceCommand.Join b)
        let! second = receive bobEvents
        equal [a.Snapshot; b.Snapshot] (snapshot second)
        let! joined = receive aliceEvents
        equal (joinedEvent b.Snapshot) joined
        do! post presence (PresenceCommand.Detach detach)
        let! left = receive bobEvents
        equal (leftEvent a.Snapshot.Identity.PlayerId) left
        let! ack = receive acknowledgments
        equal a.ConnectionId ack
        do! post presence (PresenceCommand.Detach detach)
        let! _ = receive acknowledgments
        equal 0 bobEvents.Reader.Count
        do! stop presence
    })

    case "an old connection cannot detach the replacement with the same player ID" (fun () -> task {
        let hostEvents, events, acknowledgments = Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<PresenceEvent>(), Channel.CreateUnbounded<Guid>()
        use host = TestAgent.Start(AgentOptions.create "host", collect hostEvents)
        use player = TestAgent.Start(AgentOptions.create "player", collect events)
        use cleanup = TestAgent.Start(AgentOptions.create "cleanup", collect acknowledgments)
        use presence = PresenceAgent.start config (host.Ref.TryReliable().Value) |> expectStarted |> fun owner -> owner.Owner
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
        use host = TestAgent.Start(AgentOptions.create "host", collect hostEvents)
        use fast = TestAgent.Start(AgentOptions.create "fast", collect fastEvents)
        use receiver = TestAgent.Start(options "slow" (AgentMailbox.boundedWait 1), slow slowEvents)
        use newcomer = TestAgent.Start(AgentOptions.create "newcomer", collect nextEvents)
        use presence = PresenceAgent.start config (host.Ref.TryReliable().Value) |> expectStarted |> fun owner -> owner.Owner
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
        equal (joinedEvent n.Snapshot) joined
        let! leftFast = receive fastEvents
        let! leftNew = receive nextEvents
        equal (leftEvent s.Snapshot.Identity.PlayerId) leftFast
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
        use host = TestAgent.Start(AgentOptions.create "host", collect hostEvents)
        use healthy = TestAgent.Start(AgentOptions.create "healthy", collect events)
        use receiver = TestAgent.Start(options "blocked" (AgentMailbox.boundedWait 1), slow ignored)
        use cleanup = TestAgent.Start(AgentOptions.create "cleanup", collect acknowledgments)
        use presence = PresenceAgent.start config (host.Ref.TryReliable().Value) |> expectStarted |> fun owner -> owner.Owner
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
        use host = TestAgent.Start(AgentOptions.create "host", collect hostEvents)
        use cleanup = TestAgent.Start(options "blocked-cleanup" (AgentMailbox.boundedWait 1), slow ignored)
        let! release = block cleanup
        use presence = PresenceAgent.start config (host.Ref.TryReliable().Value) |> expectStarted |> fun owner -> owner.Owner
        do! post presence (PresenceCommand.Detach { ConnectionId = Guid.NewGuid(); ReplyTo = cleanup.Ref.TryReliable().Value.Map Value })
        let! _ = terminal presence.Completion
        check presence.Completion.IsCanceled "Cleanup failure was swallowed."
        release.SetResult()
        do! stop cleanup
    })
    case "detach clears dirty state and old connection cannot update its replacement" (fun () ->
        withPresence config (fun fixture -> task {
            let latest = character fixture.Alice |> Player.snapshot
            do! changed fixture latest
            do! post fixture.Presence (PresenceCommand.Detach { ConnectionId = fixture.Alice.ConnectionId; ReplyTo = fixture.Cleanup })
            let! _ = receive fixture.Acknowledgments
            let! departed = receive fixture.BobEvents
            equal (leftEvent latest.Identity.PlayerId) departed

            let replacement = { fixture.Alice with ConnectionId = Guid.NewGuid() }
            do! post fixture.Presence (PresenceCommand.Join replacement)
            let! initial = receive fixture.AliceEvents
            equal [replacement.Snapshot; fixture.Bob.Snapshot] (snapshot initial)
            let! joined = receive fixture.BobEvents
            equal (joinedEvent replacement.Snapshot) joined
            do! changed fixture latest
            do! post fixture.Presence (tick ())
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
            do! post fixture.Presence (tick ())
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
            equal (updatedEvent latest) author
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
            do! flushBoth fixture (updatedEvent original)
            let activity = PlayerActivity.create 256 64 ActivityKind.Menu ValueNone LockDifficulty.Unknown (ValueSome "inventory") |> ok
            let details = PlayerDetails.create ValueNone ValueNone activity ValueNone ValueNone
            let temporary = { original with Details = details }
            do! changed fixture temporary
            do! post fixture.Presence (PresenceCommand.Join fixture.Late)
            let! newcomer = receive fixture.LateEvents
            equal temporary (snapshot newcomer |> List.head)
            for events in [fixture.AliceEvents; fixture.BobEvents] do
                let! published = receive events
                equal (detailsEvent original.Identity.PlayerId original.Details details) published
                let! joined = receive events
                equal (joinedEvent fixture.Late.Snapshot) joined

            do! changed fixture original
            do! flushBoth fixture (detailsEvent original.Identity.PlayerId details original.Details)
            let! restored = receive fixture.LateEvents
            equal (detailsEvent original.Identity.PlayerId details original.Details) restored
        }))

    case "join flush removes a slow existing subscriber without resurrecting its stale snapshot" (fun () -> task {
        let hostEvents, fastEvents, slowEvents =
            Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<PresenceEvent>(), Channel.CreateUnbounded<PresenceEvent>()
        use host = TestAgent.Start(AgentOptions.create "host", collect hostEvents)
        use fast = TestAgent.Start(AgentOptions.create "fast", collect fastEvents)
        use receiver = TestAgent.Start(options "slow" (AgentMailbox.boundedWait 1), slow slowEvents)
        use presence = PresenceAgent.start config (host.Ref.TryReliable().Value) |> expectStarted |> fun owner -> owner.Owner
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
        equal (updatedEvent changed) updated
        equal (leftEvent s.Snapshot.Identity.PlayerId) left
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



    case "unchanged visible positions repeat every period with stable sequence and view" (fun () ->
        withPositions config (ValueSome (location 1.0f)) (ValueSome (location 2.0f)) (fun fixture -> task {
            let! (first, firstSnapshot), _ = flushViewsNow fixture
            let! (second, secondSnapshot), _ = flushViewsNow fixture
            equal first second
            equal firstSnapshot secondSnapshot
            equal 2 first.Length
            for event in second do
                match event with
                | PresenceEvent.Moved [|sample|] ->
                    check (sample.ViewRevision > 0UL) "Every periodic pose belongs to a reliable view."
                    equal 0UL sample.Sequence
                | other -> failwithf "Only repeated movement expected: %A" other
        }))

    case "leaving and reentering AOI uses reliable clear and a fresh baseline token" (fun () ->
        withPositions { config with VisibilityDistance = 10.0f } (ValueSome (location 0.0f)) (ValueSome (location 10.0f)) (fun fixture -> task {
            let! _, (_, initial) = flushViewsNow fixture
            let original = initial |> List.find (fun value -> value.Identity.PlayerId = fixture.Alice.Snapshot.Identity.PlayerId)
            check original.Location.IsSome "Radius boundary is inclusive."
            let moved = { fixture.Alice.Snapshot with Location = ValueSome (location -1.0f); MovementSequence = 1UL }
            do! changed fixture moved
            let! _, (events, hiddenSnapshot) = flushViewsNow fixture
            let clears = visibilityOf original.Identity.PlayerId events
            equal 1 clears.Length
            equal ValueNone clears.Head.Pose
            equal [ ValueNone ] (spaces events)
            check (clears.Head.ViewRevision > original.ViewRevision) "Clear advances the observer revision."
            equal ValueNone (hiddenSnapshot |> List.find (fun value -> value.Identity.PlayerId = original.Identity.PlayerId)).Location
            do! changed fixture { moved with Location = original.Location; MovementSequence = 2UL }
            let! _, (restoredEvents, _) = flushViewsNow fixture
            let restored = visibilityOf original.Identity.PlayerId restoredEvents |> List.head
            check (restored.ViewRevision > clears.Head.ViewRevision) "Reentry cannot accept stale packets from the previous view."
            equal (original.Location |> ValueOption.map MovementPose.ofLocation) restored.Pose
            equal [ fixture.Bob.Snapshot.Location |> ValueOption.map _.Location ] (spaces restoredEvents)
            equal 2UL restored.Sequence
        }))

    case "observer movement reveals a stationary source and space change clears it" (fun () ->
        withPositions { config with VisibilityDistance = 10.0f } (ValueSome (location 0.0f)) (ValueSome (location 20.0f)) (fun fixture -> task {
            let observer = { fixture.Bob.Snapshot with Location = ValueSome (location 5.0f); MovementSequence = 1UL }
            do! post fixture.Presence (PresenceCommand.Update(fixture.Bob.ConnectionId, observer))
            let! _, (events, _) = flushViewsNow fixture
            let baseline = visibilityOf fixture.Alice.Snapshot.Identity.PlayerId events |> List.head
            equal (fixture.Alice.Snapshot.Location |> ValueOption.map MovementPose.ofLocation) baseline.Pose
            equal [ observer.Location |> ValueOption.map _.Location ] (spaces events)
            let key = FormKey.create (PluginName.create 255 "Skyrim.esm" |> ok) (LocalFormId.create 61u |> ok)
            let elsewhere = PlayerLocation.create (Location.create key (LocationName.create 128 "Elsewhere" |> ok)) Position.zero CameraDirection.zero
            do! post fixture.Presence (PresenceCommand.Update(fixture.Bob.ConnectionId, { observer with Location = ValueSome elsewhere; MovementContext = 2UL; MovementSequence = 0UL }))
            let! _, (events, _) = flushViewsNow fixture
            check (visibilityOf baseline.PlayerId events |> List.exists _.Pose.IsNone) "Space change clears stationary remote players reliably."
            // The observer's own baseline in the new space travels with the clear.
            equal [ ValueSome elsewhere.Location ] (spaces events)
        }))

    case "character reset and teleport renew reliable view even at identical coordinates" (fun () ->
        withPositions config (ValueSome (location 1.0f)) (ValueSome (location 2.0f)) (fun fixture -> task {
            let! _, (_, initial) = flushViewsNow fixture
            let before = initial |> List.find (fun value -> value.Identity.PlayerId = fixture.Alice.Snapshot.Identity.PlayerId)
            let reset = { fixture.Alice.Snapshot with CharacterGeneration = fixture.Alice.Snapshot.CharacterGeneration + 1UL; MovementContext = 2UL }
            do! changed fixture reset
            let! _, (events, _) = flushViewsNow fixture
            let baseline = visibilityOf before.Identity.PlayerId events |> List.head
            check (baseline.ViewRevision > before.ViewRevision) "Same coordinates do not preserve the old character's view."
            let metadata = events |> List.pick (function PresenceEvent.Changed(change, _) when not change.Updated.IsEmpty -> Some change.Updated.Head | _ -> None)
            equal ValueNone metadata.Location
            equal 0UL metadata.ViewRevision
            equal 1 (changeCount events)
            do! changed fixture { reset with MovementContext = 3UL }
            let! _, (events, _) = flushViewsNow fixture
            let teleport = visibilityOf before.Identity.PlayerId events |> List.head
            check (teleport.ViewRevision > baseline.ViewRevision) "Explicit discontinuity resets interpolation even in the same space."
        }))

    case "loss of a stopped player's last packet repairs on the next complete period" (fun () ->
        withPositions config (ValueSome (location 1.0f)) (ValueSome (location 2.0f)) (fun fixture -> task {
            let stopped = { fixture.Alice.Snapshot with Location = ValueSome (location 7.0f); MovementSequence = 4UL }
            do! changed fixture stopped
            let! _ = flushViewsNow fixture // Deliberately lose all packets of this period.
            let moving = { fixture.Bob.Snapshot with Location = ValueSome (location 8.0f); MovementSequence = 5UL }
            do! post fixture.Presence (PresenceCommand.Update(fixture.Bob.ConnectionId, moving))
            let! _, (events, _) = flushViewsNow fixture
            let repaired = events |> List.pick (function PresenceEvent.Moved [|value|] when value.PlayerId = stopped.Identity.PlayerId -> Some value | _ -> None)
            equal 4UL repaired.Sequence
            equal (MovementPose.ofLocation stopped.Location.Value) repaired.Pose
        }))

    case "metadata changes do not reset an established movement view" (fun () ->
        withPositions config (ValueSome (location 1.0f)) (ValueSome (location 2.0f)) (fun fixture -> task {
            let! _, (_, initial) = flushViewsNow fixture
            let activity = PlayerActivity.create 256 64 ActivityKind.Menu ValueNone LockDifficulty.Unknown (ValueSome "inventory") |> ok
            let details = PlayerDetails.create ValueNone ValueNone activity ValueNone ValueNone
            do! changed fixture { fixture.Alice.Snapshot with Details = details }
            let! _, (events, latest) = flushViewsNow fixture
            check (events |> List.exists (function PresenceEvent.Changed(change, _) -> not change.Metadata.IsEmpty | _ -> false)) "Metadata is published reliably."
            check (events |> List.forall (function PresenceEvent.Changed(change, _) -> change.Visibility.IsEmpty | _ -> true)) "Metadata cannot reinstall stale coordinates."
            equal (initial |> List.map _.ViewRevision) (latest |> List.map _.ViewRevision)
        }))

    case "reconnected source with the same player ID gets a fresh observer token" (fun () ->
        withPositions config (ValueSome (location 1.0f)) (ValueSome (location 2.0f)) (fun fixture -> task {
            let! _, (_, initial) = flushViewsNow fixture
            let before = initial |> List.find (fun value -> value.Identity.PlayerId = fixture.Alice.Snapshot.Identity.PlayerId)
            do! post fixture.Presence (PresenceCommand.Detach { ConnectionId = fixture.Alice.ConnectionId; ReplyTo = fixture.Cleanup })
            let! _ = receive fixture.Acknowledgments
            let! _ = receive fixture.BobEvents
            let replacement = { fixture.Alice with ConnectionId = Guid.NewGuid() }
            do! post fixture.Presence (PresenceCommand.Join replacement)
            let! _ = receive fixture.AliceEvents
            let! joined = receive fixture.BobEvents
            match (fst (changeOf joined)).Joined with
            | [ value ] -> check (value.ViewRevision > before.ViewRevision) "Reconnect cannot reuse the old token."
            | other -> failwithf "%A" other
        }))
    case "a full realtime subscriber drops samples without losing membership" (fun () -> task {
        let hostEvents, events, acknowledgments = Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<PresenceEvent>(), Channel.CreateUnbounded<Guid>()
        use host = TestAgent.Start(AgentOptions.create "host", collect hostEvents)
        use receiver = TestAgent.Start(options "slow-realtime" (AgentMailbox.boundedWait 1), slow events)
        use cleanup = TestAgent.Start(AgentOptions.create "barrier", collect acknowledgments)
        use presence = PresenceAgent.start config (host.Ref.TryReliable().Value) |> expectStarted |> fun owner -> owner.Owner
        let positioned = Player.create (profile 1UL) |> Player.applyUpdate (PlayerUpdate.SetLocation(1UL, ValueSome (location 0.0f))) |> Player.snapshot
        let subscriber = { ConnectionId = Guid.NewGuid(); Snapshot = positioned; Events = receiver.Ref.TryReliable().Value.Map Value }
        do! post presence (PresenceCommand.Join subscriber)
        let! _ = receive events
        let! release = block receiver
        do! post presence (tick ())
        do! post presence (tick ())
        let barrier = Guid.NewGuid()
        do! post presence (PresenceCommand.Detach { ConnectionId = barrier; ReplyTo = cleanup.Ref.TryReliable().Value })
        let! _ = receive acknowledgments
        equal 0 hostEvents.Reader.Count
        check (not receiver.Completion.IsCompleted) "Realtime pressure cannot terminate a session."
        release.SetResult()
        // Wait for the blocked receiver to drain its filler before requesting a new period.
        let ready = gate<unit>()
        let finish = gate<unit>()
        do! post receiver (Hold(ready, finish))
        do! awaitResult ready.Task
        finish.SetResult()
        do! post presence (tick ())
        let! repeated = receive events
        match repeated with
        | PresenceEvent.Moved [|value|] -> equal positioned.Identity.PlayerId value.PlayerId
        | other -> failwithf "Membership and periodic delivery must survive: %A" other
        do! stop presence
        do! stop receiver
    })

    case "spatial candidates agree with exact visibility across cell and extreme coordinates" (fun () ->
        withPositions { config with VisibilityDistance = 10.0f } (ValueSome (location 0.0f)) (ValueSome (location 0.0f)) (fun fixture -> task {
            for x in [-Single.MaxValue; -20.0f; -10.0f; -0.001f; 0.0f; 9.999f; 10.0f; 10.001f; Single.MaxValue] do
                let moved = { fixture.Alice.Snapshot with Location = ValueSome (location x) }
                do! changed fixture moved
                let! _, (_, players) = flushViewsNow fixture
                let projected = players |> List.find (fun value -> value.Identity.PlayerId = moved.Identity.PlayerId)
                let expected = PlayerLocation.isWithinRadius (WorldUnit.create 10.0f |> ok) fixture.Bob.Snapshot.Location.Value moved.Location.Value |> ok
                equal expected projected.Location.IsSome
        }))

    case "zero radius includes only coincident positions and invalid radii fail the settings check" (fun () -> task {
        for radius in [-1.0f; Single.NaN; Single.PositiveInfinity] do
            let runtime = { ServerRuntimeOptions.defaults with Presence = { ServerRuntimeOptions.defaults.Presence with VisibilityDistance = radius } }
            let errors = Settings.errors ServerConfig.defaults runtime IdentityOptions.defaults AnnouncementOptions.defaults GroundMarkOptions.defaults
            check (errors |> List.exists (fun error -> error.Contains "VisibilityDistance")) $"Expected radius validation: {radius}"
        do! withPositions { config with VisibilityDistance = 0.0f } (ValueSome (location 0.0f)) (ValueSome (location 0.0f)) (fun fixture -> task {
            let! _, (_, players) = flushViewsNow fixture
            check (players |> List.forall (fun value -> value.Location.IsSome)) "Coincident peers are visible."
            do! changed fixture { fixture.Alice.Snapshot with Location = ValueSome (location 0.001f) }
            let! _, (events, players) = flushViewsNow fixture
            let remote = players |> List.find (fun value -> value.Identity.PlayerId = fixture.Alice.Snapshot.Identity.PlayerId)
            equal ValueNone remote.Location
            check (visibilityOf remote.Identity.PlayerId events |> List.exists _.Pose.IsNone) "Departure is reliable even at radius zero."
        })
    })

    case "a tick sends each observer one change with the metadata of every player" (fun () ->
        withPresence config (fun fixture -> task {
            let activity = PlayerActivity.create 256 64 ActivityKind.Menu ValueNone LockDifficulty.Unknown (ValueSome "inventory") |> ok
            let details = PlayerDetails.create ValueNone (ValueSome 3u) activity ValueNone ValueNone
            do! changed fixture { fixture.Alice.Snapshot with Details = details }
            do! changedBob fixture (publishes fixture.Bob [ "skyrim:health", "Health", -15 ])
            let! (alice, _), (bob, _) = flushViewsNow fixture
            let health = kind 1UL "skyrim:health" "Health"
            let aliceId, bobId = fixture.Alice.Snapshot.Identity.PlayerId, fixture.Bob.Snapshot.Identity.PlayerId
            let metadata = [
                ({ PlayerId = aliceId; ActorValues = ValueNone; Details = DetailsPatch.between PlayerDetails.empty details }: MetadataPatch)
                valuesPatch bobId [] [ health.Key, reading "Health" -15 ]
            ]
            let expected = PresenceEvent.Changed({ PresenceChange.empty with Metadata = metadata }, { Ids = idsOf [ health ]; Defined = [ health ] })
            equal [ expected ] alice
            equal [ expected ] bob
        }))

    case "a kind is defined once to each recipient and keeps its number" (fun () ->
        withPresence config (fun fixture -> task {
            let health = kind 1UL "skyrim:health" "Health"
            let set current = valuesPatch fixture.Alice.Snapshot.Identity.PlayerId [] [ health.Key, reading "Health" current ]
            do! changed fixture (publishes fixture.Alice [ "skyrim:health", "Health", 50 ])
            do! flushBoth fixture (PresenceEvent.Changed({ PresenceChange.empty with Metadata = [ set 50 ] }, { Ids = idsOf [ health ]; Defined = [ health ] }))
            do! changed fixture (publishes fixture.Alice [ "skyrim:health", "Health", 40 ])
            do! flushBoth fixture (PresenceEvent.Changed({ PresenceChange.empty with Metadata = [ set 40 ] }, { Ids = idsOf [ health ]; Defined = [] }))
        }))

    case "a key published later gets the next number and every member learns it in that tick" (fun () ->
        withPresence config (fun fixture -> task {
            let health, stamina = kind 1UL "skyrim:health" "Health", kind 2UL "skyrim:stamina" "Stamina"
            do! changed fixture (publishes fixture.Alice [ "skyrim:health", "Health", 50 ])
            do! post fixture.Presence (tick ())
            for events in [ fixture.AliceEvents; fixture.BobEvents ] do
                let! first = receive events
                equal [ health ] (snd (changeOf first)).Defined
            do! changedBob fixture (publishes fixture.Bob [ "skyrim:stamina", "Stamina", 70 ])
            do! post fixture.Presence (tick ())
            for events in [ fixture.AliceEvents; fixture.BobEvents ] do
                let! second = receive events
                let change, kinds = changeOf second
                equal [ fixture.Bob.Snapshot.Identity.PlayerId ] (change.Metadata |> List.map _.PlayerId)
                equal [ stamina ] kinds.Defined
                equal (idsOf [ health; stamina ]) kinds.Ids
        }))

    case "a joining member's snapshot defines every live kind and its own kinds arrive with it" (fun () ->
        withPresence config (fun fixture -> task {
            do! changed fixture (publishes fixture.Alice [ "skyrim:health", "Health", 50 ])
            do! changedBob fixture (publishes fixture.Bob [ "skyrim:stamina", "Stamina", 70 ])
            do! post fixture.Presence (tick ())
            for events in [ fixture.AliceEvents; fixture.BobEvents ] do
                let! published = receive events
                equal 2 (fst (changeOf published)).Metadata.Length
            let late = { fixture.Late with Snapshot = publishes fixture.Late [ "skyrim:magicka", "Magicka", 20 ] }
            do! post fixture.Presence (PresenceCommand.Join late)
            let magicka = kind 3UL "skyrim:magicka" "Magicka"
            let every = [ kind 1UL "skyrim:health" "Health"; kind 2UL "skyrim:stamina" "Stamina"; magicka ]
            let! opening = receive fixture.LateEvents
            match opening with
            | PresenceEvent.Snapshot(players, kinds) ->
                equal [ fixture.Alice.Snapshot.Identity.PlayerId; fixture.Bob.Snapshot.Identity.PlayerId; late.Snapshot.Identity.PlayerId ]
                      (players |> List.map _.Identity.PlayerId)
                equal { Ids = idsOf every; Defined = every } kinds
            | other -> failwithf "Expected presence snapshot: %A" other
            // The others already know the first two kinds.
            for events in [ fixture.AliceEvents; fixture.BobEvents ] do
                let! arrival = receive events
                equal (PresenceEvent.Changed({ PresenceChange.empty with Joined = [ late.Snapshot ] }, { Ids = idsOf every; Defined = [ magicka ] })) arrival
        }))

    case "a kind no player publishes is forgotten and its pair later gets a new number" (fun () ->
        withPresence config (fun fixture -> task {
            let alice = fixture.Alice.Snapshot.Identity.PlayerId
            let first, second = kind 1UL "skyrim:health" "Health", kind 2UL "skyrim:health" "Health"
            let set = valuesPatch alice [] [ first.Key, reading "Health" 50 ]
            do! changed fixture (publishes fixture.Alice [ "skyrim:health", "Health", 50 ])
            do! flushBoth fixture (PresenceEvent.Changed({ PresenceChange.empty with Metadata = [ set ] }, { Ids = idsOf [ first ]; Defined = [ first ] }))
            // The removal still names the kind by its number.
            do! changed fixture fixture.Alice.Snapshot
            let removal = valuesPatch alice [ struct (first.Key, first.DisplayName) ] []
            do! flushBoth fixture (PresenceEvent.Changed({ PresenceChange.empty with Metadata = [ removal ] }, { Ids = idsOf [ first ]; Defined = [] }))

            do! post fixture.Presence (PresenceCommand.Join fixture.Late)
            let! opening = receive fixture.LateEvents
            equal (PresenceEvent.Snapshot([ fixture.Alice.Snapshot; fixture.Bob.Snapshot; fixture.Late.Snapshot ], ActorValueKinds.none)) opening
            for events in [ fixture.AliceEvents; fixture.BobEvents ] do
                let! arrival = receive events
                equal (joinedEvent fixture.Late.Snapshot) arrival

            do! changed fixture (publishes fixture.Alice [ "skyrim:health", "Health", 50 ])
            do! post fixture.Presence (tick ())
            let renumbered = PresenceEvent.Changed({ PresenceChange.empty with Metadata = [ set ] }, { Ids = idsOf [ second ]; Defined = [ second ] })
            for events in [ fixture.AliceEvents; fixture.BobEvents; fixture.LateEvents ] do
                let! event = receive events
                equal renumbered event
        }))

    case "a changed label removes the old kind by its number and defines a new one" (fun () ->
        withPresence config (fun fixture -> task {
            let alice = fixture.Alice.Snapshot.Identity.PlayerId
            let health, relabelled = kind 1UL "skyrim:health" "Health", kind 2UL "skyrim:health" "Здоровье"
            do! changed fixture (publishes fixture.Alice [ "skyrim:health", "Health", 50 ])
            do! post fixture.Presence (tick ())
            for events in [ fixture.AliceEvents; fixture.BobEvents ] do
                let! published = receive events
                equal [ health ] (snd (changeOf published)).Defined
            do! changed fixture (publishes fixture.Alice [ "skyrim:health", "Здоровье", 50 ])
            do! post fixture.Presence (tick ())
            let change = { PresenceChange.empty with Metadata = [ valuesPatch alice [ struct (health.Key, health.DisplayName) ] [ relabelled.Key, reading "Здоровье" 50 ] ] }
            let kinds = { Ids = idsOf [ health; relabelled ]; Defined = [ relabelled ] }
            for events in [ fixture.AliceEvents; fixture.BobEvents ] do
                let! event = receive events
                equal (PresenceEvent.Changed(change, kinds)) event
            // Both numbers are known while the change is encoded.
            check (Packets.single (ProtocolCodec.create ServerConfig.defaults) (ServerResponse.PresenceChanged(change, kinds)) |> Result.isOk) "The relabelling is encodable."
            do! post fixture.Presence (PresenceCommand.Join fixture.Late)
            let! opening = receive fixture.LateEvents
            match opening with
            | PresenceEvent.Snapshot(_, kinds) -> equal { Ids = idsOf [ relabelled ]; Defined = [ relabelled ] } kinds
            | other -> failwithf "Expected presence snapshot: %A" other
        }))

    case "a detached member leaves without kinds and takes the kinds only it published" (fun () ->
        withPresence config (fun fixture -> task {
            let health, magicka = kind 1UL "skyrim:health" "Health", kind 2UL "skyrim:magicka" "Magicka"
            do! changed fixture (publishes fixture.Alice [ "skyrim:health", "Health", 50; "skyrim:magicka", "Magicka", 30 ])
            let bob = publishes fixture.Bob [ "skyrim:health", "Health", 90 ]
            do! changedBob fixture bob
            do! post fixture.Presence (tick ())
            for events in [ fixture.AliceEvents; fixture.BobEvents ] do
                let! published = receive events
                equal [ health; magicka ] (snd (changeOf published)).Defined
            do! post fixture.Presence (PresenceCommand.Detach { ConnectionId = fixture.Alice.ConnectionId; ReplyTo = fixture.Cleanup })
            let! _ = receive fixture.Acknowledgments
            let! departed = receive fixture.BobEvents
            equal (leftEvent fixture.Alice.Snapshot.Identity.PlayerId) departed
            do! post fixture.Presence (PresenceCommand.Join fixture.Late)
            let! opening = receive fixture.LateEvents
            equal (PresenceEvent.Snapshot([ bob; fixture.Late.Snapshot ], { Ids = idsOf [ health ]; Defined = [ health ] })) opening
        }))
]
