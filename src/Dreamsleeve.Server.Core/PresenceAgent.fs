namespace Dreamsleeve.Server.Core

open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading.Tasks
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

/// Owns online membership and the latest detached state used by snapshot and deltas.
[<RequireQualifiedAccess>]
module PresenceAgent =
    type private View = {
        SourceConnection: Guid
        SourceCharacter: uint64
        SourceContext: uint64
        ObserverCharacter: uint64
        ObserverContext: uint64
        Revision: uint64
    }

    type private Member = {
        ConnectionId: Guid
        Events: ReliableAgentRef<PresenceEvent>
        mutable Latest: PlayerSnapshot
        mutable Published: PlayerSnapshot
        Views: Dictionary<PlayerId, View>
        mutable NextRevision: uint64
        /// The highest actor value kind number this member has been told.
        mutable KnownKind: uint64
    }

    type private KindEntry = { Kind: ActorValueKind; mutable Uses: int }

    type private State = {
        Members: Dictionary<Guid, Member>
        Players: Dictionary<PlayerId, Guid>
        Dirty: HashSet<PlayerId>
        Candidates: HashSet<Guid>
        Movements: ResizeArray<MovementChange>
        LatestIndex: SpatialIndex.State<Guid>
        /// Kinds of the published readings, each counted by the members publishing it.
        Kinds: Dictionary<struct (ActorValueKey * ActorValueName), KindEntry>
        /// Kinds whose count reached zero in this turn; forgotten at its end.
        Unused: HashSet<struct (ActorValueKey * ActorValueName)>
        /// Detached event snapshot, rebuilt only after registry membership changes.
        mutable KindIds: ActorValueKindIndex option
        /// Never reused: even a client renaming every label each tick cannot exhaust it.
        mutable LastKind: uint64
        /// Members removed so far; a tick filters its shared parts only after one.
        mutable Removals: int64
        TickInterval: AgentTickerInterval
        mutable Ticker: AgentTicker option
        mutable LastFlush: int64
        VisibilityDistanceSquared: double
        Host: AgentOutbox<SessionHostCommand>
        PhantomObservation: PhantomObservationMode
        PhantomObservations: ResizeArray<PhantomObservation>
    }

    let private notifyHost state context command =
        if not (state.Host.TrySend(context, command)) then
            context.Abort()

    let private observeMember state id value =
        match state.PhantomObservation with
        | PhantomObservationMode.Disabled -> ()
        | PhantomObservationMode.Membership | PhantomObservationMode.Full ->
            state.PhantomObservations.Add(PhantomObservation.Member(id, value))

    let private observeDeparted state id =
        match state.PhantomObservation with
        | PhantomObservationMode.Disabled -> ()
        | PhantomObservationMode.Membership | PhantomObservationMode.Full ->
            state.PhantomObservations.Add(PhantomObservation.Departed id)

    let private observeHidden state observer player revision =
        if state.PhantomObservation = PhantomObservationMode.Full then
            state.PhantomObservations.Add(PhantomObservation.Hidden(observer, player, revision))

    let private observeView state (observer: Member) (source: Member) revision =
        if state.PhantomObservation = PhantomObservationMode.Full && observer.ConnectionId <> source.ConnectionId then
            match observer.Latest.Location, source.Latest.Location with
            | ValueSome origin, ValueSome target ->
                state.PhantomObservations.Add(PhantomObservation.View(observer.ConnectionId, source.ConnectionId, source.Latest.Identity.PlayerId, revision,
                                                       double (Position.distanceSquared origin.Position target.Position), PhantomPolicy.facing origin target))
            | _ -> ()

    let private flushObservations state context =
        if state.PhantomObservations.Count > 0 then
            // Preserve one atomic authority turn without allocating a dense AOI
            // reference array on the large object heap every replication tick.
            let leafSize = 4096
            let batch =
                if state.PhantomObservations.Count <= leafSize then state.PhantomObservations.ToArray()
                else
                    Array.init ((state.PhantomObservations.Count + leafSize - 1) / leafSize) (fun index ->
                        let offset = index * leafSize
                        let leaf = Array.zeroCreate (min leafSize (state.PhantomObservations.Count - offset))
                        state.PhantomObservations.CopyTo(offset, leaf, 0, leaf.Length)
                        PhantomObservation.Batch leaf)
            state.PhantomObservations.Clear()
            notifyHost state context (SessionHostCommand.ObservePhantoms(PhantomObservation.Batch batch))

    let private kindOf (key: ActorValueKey) (info: ActorValueInfo) = struct (key, info.DisplayName)

    /// Numbers the kinds of these readings that have none yet.
    let private register state (values: Map<ActorValueKey, ActorValueInfo>) =
        for KeyValue(key, info) in values do
            let kind = kindOf key info
            if not (state.Kinds.ContainsKey kind) then
                state.LastKind <- state.LastKind + 1UL
                state.Kinds[kind] <- { Kind = { Id = state.LastKind; Key = key; DisplayName = info.DisplayName }; Uses = 0 }
                state.KindIds <- None
                state.Unused.Add kind |> ignore

    /// Counts one member's published readings in (+1) or out (-1).
    let private count state delta (values: Map<ActorValueKey, ActorValueInfo>) =
        for KeyValue(key, info) in values do
            let kind = kindOf key info
            match state.Kinds.TryGetValue kind with
            | true, entry ->
                entry.Uses <- entry.Uses + delta
                if entry.Uses = 0 then state.Unused.Add kind |> ignore
            | false, _ -> ()

    /// A kind nobody publishes any more is never sent again: the same pair
    /// published later gets a new number. Clients may drop it the same way.
    let private forgetUnused state =
        for kind in state.Unused do
            match state.Kinds.TryGetValue kind with
            | true, entry when entry.Uses = 0 ->
                state.Kinds.Remove kind |> ignore
                state.KindIds <- None
            | true, _ | false, _ -> ()
        state.Unused.Clear()

    /// The numbers for one event and the kinds its recipient has not been told.
    /// A snapshot replaces whatever the recipient knew, so it gets every kind.
    let private kindsFor state (recipient: Member) everything =
        let known = if everything then 0UL else recipient.KnownKind
        let defined =
            if state.LastKind = known then []
            else
                state.Kinds.Values
                |> Seq.filter (fun entry -> entry.Kind.Id > known)
                |> Seq.map _.Kind
                |> Seq.sortBy _.Id
                |> List.ofSeq
        recipient.KnownKind <- state.LastKind
        let ids =
            match state.KindIds with
            | Some snapshot -> snapshot
            | None ->
                let snapshot = ActorValueKindIndex.Create(state.Kinds.Values |> Seq.map _.Kind)
                state.KindIds <- Some snapshot
                snapshot
        { Ids = ids; Defined = defined }

    let private remove state connectionId =
        match state.Members.TryGetValue connectionId with
        | false, _ -> None
        | true, memberState ->
            let playerId = memberState.Latest.Identity.PlayerId
            state.Members.Remove connectionId |> ignore
            observeDeparted state connectionId
            state.Players.Remove playerId |> ignore
            state.Dirty.Remove playerId |> ignore
            SpatialIndex.remove connectionId state.LatestIndex
            count state -1 memberState.Published.ActorValues
            state.Removals <- state.Removals + 1L
            for observer in state.Members.Values do observer.Views.Remove playerId |> ignore
            Some playerId

    let private deliver state context (subscriber: Member) event =
        match subscriber.Events.TryPost event with
        | AgentTryDeliveryResult.Posted -> None
        | AgentTryDeliveryResult.Closed -> remove state subscriber.ConnectionId
        | AgentTryDeliveryResult.Full ->
            let removed = remove state subscriber.ConnectionId
            notifyHost state context (SessionHostCommand.SlowConsumer subscriber.ConnectionId)
            removed

    let private broadcastLeft state (context: ReliableAgentContext<PresenceCommand>) playerId =
        // A failed recipient can cause another Left. Each removed member contributes
        // at most one delta, and no healthy recipient waits for the slow one.
        let pending = Queue<PlayerId>()
        pending.Enqueue playerId
        while pending.Count > 0 && not context.CancellationToken.IsCancellationRequested do
            let left = PresenceEvent.Changed({ PresenceChange.empty with Left = [ pending.Dequeue() ] }, ActorValueKinds.none)
            let recipients = state.Members.Values |> Seq.toArray
            for recipient in recipients do
                match deliver state context recipient left with
                | Some removed -> pending.Enqueue removed
                | None -> ()

    let private deliverDelta state context subscriber event =
        match deliver state context subscriber event with
        | Some playerId -> broadcastLeft state context playerId
        | None -> ()

    // Visibility is symmetric in space, but self always receives its own telemetry.
    // Radius is validated once at startup; double arithmetic avoids float overflow.
    let private visibleLocation state (observer: PlayerSnapshot) (source: PlayerSnapshot) =
        if observer.Identity.PlayerId = source.Identity.PlayerId then source.Location
        else
            match observer.Location, source.Location with
            | ValueSome origin, ValueSome target ->
                match PlayerLocation.tryDistanceSquared origin target with
                | ValueSome squared when float squared <= state.VisibilityDistanceSquared -> source.Location
                | ValueSome _ | ValueNone -> ValueNone
            | ValueNone, _ | _, ValueNone -> ValueNone

    let private nextRevision observer =
        observer.NextRevision <- observer.NextRevision + 1UL
        observer.NextRevision

    let private sameView observer source view =
        view.SourceConnection = source.ConnectionId
        && view.SourceCharacter = source.Latest.CharacterGeneration
        && view.SourceContext = source.Latest.MovementContext
        && view.ObserverCharacter = observer.Latest.CharacterGeneration
        && view.ObserverContext = observer.Latest.MovementContext

    let private establishView observer source =
        let playerId = source.Latest.Identity.PlayerId
        match observer.Views.TryGetValue playerId with
        | true, view when sameView observer source view -> view, false
        | true, _ | false, _ ->
            let view = {
                SourceConnection = source.ConnectionId
                SourceCharacter = source.Latest.CharacterGeneration
                SourceContext = source.Latest.MovementContext
                ObserverCharacter = observer.Latest.CharacterGeneration
                ObserverContext = observer.Latest.MovementContext
                Revision = nextRevision observer
            }
            observer.Views[playerId] <- view
            view, true

    let private project state observer source =
        match visibleLocation state observer.Latest source.Latest with
        | ValueNone -> { source.Latest with Location = ValueNone; ViewRevision = 0UL }
        | ValueSome location ->
            let view, _ = establishView observer source
            observeView state observer source view.Revision
            { source.Latest with Location = ValueSome location; ViewRevision = view.Revision }

    let private snapshot state observer =
        state.Members.Values
        |> Seq.map (project state observer)
        |> Seq.sortBy _.Identity.PlayerId
        |> List.ofSeq

    // A switch between the profile and a pseudonym is a change of identity: it
    // travels as the whole player again, like a rename.
    let private identityEqual (previous: PlayerSnapshot) (latest: PlayerSnapshot) =
        previous.Identity = latest.Identity && previous.CharacterName = latest.CharacterName
        && previous.CharacterNameWithheld = latest.CharacterNameWithheld
        && previous.CharacterGeneration = latest.CharacterGeneration

    /// The global changes of a tick, shared by every recipient, and their sources.
    type private TickChanges = {
        Updated: (Guid * PlayerSnapshot) list
        Metadata: (Guid * MetadataPatch) list
        AllUpdated: PlayerSnapshot list
        AllMetadata: MetadataPatch list
        Removals: int64
    }

    /// One observer's turn of a tick: the global changes of the tick with its own
    /// visibility baselines and clears as one reliable batch, then its poses.
    let private publishTo state context sendSamples (observer: Member) (tick: TickChanges) =
        let candidates = state.Candidates
        candidates.Clear()
        SpatialIndex.neighbors observer.Latest.Location state.LatestIndex candidates
        candidates.Add observer.ConnectionId |> ignore
        // Include old visible pairs so leaving the radius clears their baselines.
        for playerId in observer.Views.Keys do
            match state.Players.TryGetValue playerId with
            | true, connectionId -> candidates.Add connectionId |> ignore
            | false, _ -> ()

        let movements = state.Movements
        movements.Clear()
        let visibility = ResizeArray<VisibilityChange>()
        for id in candidates do
            match state.Members.TryGetValue id with
            | true, source ->
                let playerId = source.Latest.Identity.PlayerId
                match visibleLocation state observer.Latest source.Latest with
                | ValueSome location ->
                    let view, changed = establishView observer source
                    // The consumer retains authority and distance. Republish only
                    // when that authority or either endpoint's location changes.
                    if state.PhantomObservation = PhantomObservationMode.Full
                       && (changed || observer.Latest.Location <> observer.Published.Location || source.Latest.Location <> source.Published.Location) then
                        observeView state observer source view.Revision
                    let pose = MovementPose.ofLocation location
                    if changed then
                        visibility.Add {
                            PlayerId = playerId; ViewRevision = view.Revision
                            Sequence = source.Latest.MovementSequence; Pose = ValueSome pose
                        }
                    if sendSamples then
                        movements.Add {
                            PlayerId = playerId; ViewRevision = view.Revision
                            Sequence = source.Latest.MovementSequence; Pose = pose
                        }
                | ValueNone ->
                    if observer.Views.Remove playerId then
                        let revision = nextRevision observer
                        visibility.Add { PlayerId = playerId; ViewRevision = revision; Sequence = 0UL; Pose = ValueNone }
                        observeHidden state observer.ConnectionId playerId revision
            | false, _ -> ()
        candidates.Clear()

        // A player removed earlier in this tick has already been announced as left.
        let present (connectionId, _) = state.Members.ContainsKey connectionId
        let unchanged = state.Removals = tick.Removals
        let change = {
            PresenceChange.empty with
                Updated = if unchanged then tick.AllUpdated else tick.Updated |> List.filter present |> List.map snd
                Metadata = if unchanged then tick.AllMetadata else tick.Metadata |> List.filter present |> List.map snd
                Space =
                    if visibility |> Seq.exists (fun change -> change.Pose.IsSome) then
                        observer.Latest.Location |> ValueOption.map _.Location
                    else ValueNone
                Visibility = List.ofSeq visibility
        }
        if not (PresenceChange.isEmpty change) then
            deliverDelta state context observer (PresenceEvent.Changed(change, kindsFor state observer false))

        if movements.Count > 0 && state.Members.ContainsKey observer.ConnectionId then
            let ordered = movements.ToArray()
            Array.sortInPlaceBy (fun (change: MovementChange) -> change.PlayerId) ordered
            match observer.Events.TryPost (PresenceEvent.Moved ordered) with
            | AgentTryDeliveryResult.Posted | AgentTryDeliveryResult.Full -> ()
            | AgentTryDeliveryResult.Closed ->
                match remove state observer.ConnectionId with
                | Some playerId -> broadcastLeft state context playerId
                | None -> ()

        movements.Clear()

    let private publishDirty state context sendSamples =
        // A single agent turn freezes latest state for all observers. Dirty only
        // controls metadata; positions repeat even when every source has stopped.
        let members = state.Members.Values |> Seq.toArray
        let changed = members |> Array.filter (fun memberState -> state.Dirty.Contains memberState.Latest.Identity.PlayerId)
        state.Dirty.Clear()
        let updated = [
            for source in changed do
                if not (identityEqual source.Published source.Latest) then
                    register state source.Latest.ActorValues
                    source.ConnectionId, { source.Latest with Location = ValueNone; ViewRevision = 0UL }
        ]
        let metadata = [
            for source in changed do
                if identityEqual source.Published source.Latest then
                    let values = ActorValuesPatch.between source.Published.ActorValues source.Latest.ActorValues
                    let details = DetailsPatch.between source.Published.Details source.Latest.Details
                    if values.IsSome || details.IsSome then
                        register state source.Latest.ActorValues
                        source.ConnectionId, { PlayerId = source.Latest.Identity.PlayerId; ActorValues = values; Details = details }
        ]

        let tick = {
            Updated = updated; Metadata = metadata
            AllUpdated = List.map snd updated; AllMetadata = List.map snd metadata
            Removals = state.Removals
        }
        for observer in members do
            if state.Members.ContainsKey observer.ConnectionId then
                publishTo state context sendSamples observer tick

        for memberState in changed do
            if state.Members.ContainsKey memberState.ConnectionId then
                if memberState.Published.ActorValues <> memberState.Latest.ActorValues then
                    count state 1 memberState.Latest.ActorValues
                    count state -1 memberState.Published.ActorValues
                memberState.Published <- memberState.Latest
        flushObservations state context

    let private join state context (subscription: PresenceSubscription) =
        let existedBefore = state.Members.ContainsKey subscription.ConnectionId
        let sameConnection =
            match state.Members.TryGetValue subscription.ConnectionId with
            | true, memberState -> memberState.Latest.Identity.PlayerId = subscription.Snapshot.Identity.PlayerId
            | false, _ -> true
        let samePlayer =
            match state.Players.TryGetValue subscription.Snapshot.Identity.PlayerId with
            | true, owner -> owner = subscription.ConnectionId
            | false, _ -> true

        if not sameConnection || not samePlayer then
            notifyHost state context (SessionHostCommand.Close(subscription.ConnectionId, "presence_identity_conflict"))
        else
            // Bring existing recipients to the same baseline before handing a new
            // subscriber the latest snapshot. Otherwise B -> join -> A could leave
            // the new subscriber at B when A equals the old published baseline.
            publishDirty state context false
            // A slow existing subscriber removed by this flush must stay removed.
            if not existedBefore || state.Members.ContainsKey subscription.ConnectionId then
                let existing = state.Members.ContainsKey subscription.ConnectionId
                let memberState =
                    match state.Members.TryGetValue subscription.ConnectionId with
                    | true, current -> { current with Events = subscription.Events }
                    | false, _ -> {
                        ConnectionId = subscription.ConnectionId; Events = subscription.Events
                        Latest = subscription.Snapshot; Published = subscription.Snapshot
                        Views = Dictionary(); NextRevision = 0UL; KnownKind = 0UL
                      }
                if not existing then
                    register state memberState.Published.ActorValues
                    count state 1 memberState.Published.ActorValues
                state.Members[subscription.ConnectionId] <- memberState
                observeMember state subscription.ConnectionId memberState.Latest
                state.Players[subscription.Snapshot.Identity.PlayerId] <- subscription.ConnectionId
                SpatialIndex.set memberState.ConnectionId memberState.Latest.Location state.LatestIndex

                let opening = PresenceEvent.Snapshot(snapshot state memberState, kindsFor state memberState true)
                // Establish authenticated membership before the session can
                // activate from its welcome snapshot on the other actor.
                flushObservations state context
                match deliver state context memberState opening with
                | Some playerId when existing -> broadcastLeft state context playerId
                | Some _ -> ()
                | None when not existing ->
                    let recipients = state.Members.Values |> Seq.toArray
                    for recipient in recipients do
                        if recipient.ConnectionId <> subscription.ConnectionId
                           && state.Members.ContainsKey recipient.ConnectionId
                           && state.Members.ContainsKey subscription.ConnectionId then
                            let joined = { PresenceChange.empty with Joined = [ project state recipient memberState ] }
                            deliverDelta state context recipient (PresenceEvent.Changed(joined, kindsFor state recipient false))
                | None -> ()

    let private schedule (config: PresenceOptions) state context =
        if state.Ticker.IsNone then
            state.Ticker <- Some (AgentTicker.start state.TickInterval context PresenceCommand.Flush)

    let private update config state context connectionId (value: PlayerSnapshot) =
        match state.Members.TryGetValue connectionId with
        | false, _ -> ()
        | true, memberState when memberState.Latest.Identity.PlayerId <> value.Identity.PlayerId ->
            notifyHost state context (SessionHostCommand.Close(connectionId, "presence_identity_conflict"))
        | true, memberState ->
            memberState.Latest <- value
            observeMember state connectionId value
            SpatialIndex.set connectionId value.Location state.LatestIndex
            state.Dirty.Add value.Identity.PlayerId |> ignore
            schedule config state context

    let private detach state (context: ReliableAgentContext<PresenceCommand>) (request: SessionDetach) =
        match remove state request.ConnectionId with
        | Some playerId -> broadcastLeft state context playerId
        | None -> ()

        match request.ReplyTo.TryPost request.ConnectionId with
        | AgentTryDeliveryResult.Posted | AgentTryDeliveryResult.Closed -> ()
        | AgentTryDeliveryResult.Full -> context.Abort()

    let private handle config state (context: ReliableAgentContext<PresenceCommand>) command = task {
        match command with
        | PresenceCommand.Join subscription ->
            join state context subscription
            schedule config state context
        | PresenceCommand.Update(connectionId, value) -> update config state context connectionId value
        | PresenceCommand.Flush notification ->
            let time = Environment.TickCount64
            if state.Members.Count > 0 then
                if state.LastFlush <> 0L then RuntimeMetrics.presenceInterval.Record(float (time - state.LastFlush))
                state.LastFlush <- time
            else
                state.LastFlush <- 0L

            RuntimeMetrics.presenceLateness.Record(Stopwatch.GetElapsedTime(notification.DueTimestamp).TotalMilliseconds)
            RuntimeMetrics.presenceTimerLateness.Record(Stopwatch.GetElapsedTime(notification.DueTimestamp, notification.QueuedTimestamp).TotalMilliseconds)
            RuntimeMetrics.presenceQueueDelay.Record(Stopwatch.GetElapsedTime(notification.QueuedTimestamp).TotalMilliseconds)
            let started = Stopwatch.GetTimestamp()
            publishDirty state context true
            RuntimeMetrics.presenceFlush.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds)
            state.Ticker |> Option.iter _.Acknowledge()
        | PresenceCommand.Detach request -> detach state context request
        forgetUnused state
        flushObservations state context
    }

    let private isControl = function
        | PresenceCommand.Join _ | PresenceCommand.Flush _ | PresenceCommand.Detach _ -> true
        | PresenceCommand.Update _ -> false

    /// The options come checked by GameSettings.create.
    let private startWithObservation mode (config: PresenceOptions) (host: ReliableAgentRef<SessionHostCommand>) =
        match AgentTickerInterval.TryCreate(TimeSpan.FromMilliseconds(int64 config.ReplicationIntervalMs)),
              AgentOutbox<SessionHostCommand>.TryCreate(config.MaxControlDeliveries, host) with
        | Error error, _ | _, Error error -> Error error
        | Ok interval, Ok hostOutbox ->
            let state = {
                Members = Dictionary(); Players = Dictionary(); Dirty = HashSet()
                Candidates = HashSet(); Movements = ResizeArray()
                LatestIndex = SpatialIndex.create (double config.VisibilityDistance)
                Kinds = Dictionary(); Unused = HashSet(); KindIds = None; LastKind = 0UL; Removals = 0L
                VisibilityDistanceSquared = double config.VisibilityDistance * double config.VisibilityDistance
                Ticker = None; TickInterval = interval; LastFlush = 0L; Host = hostOutbox
                PhantomObservation = mode; PhantomObservations = ResizeArray()
            }
            let options = {
                AgentOptions.create "presence" with
                    Mailbox = AgentMailbox.boundedWithControl config.MailboxCapacity config.ControlReserve
            }
            Agent.TryStartReliable(options, handle config state, isControl = isControl)

    let start config host = startWithObservation PhantomObservationMode.Disabled config host
    let startObserved mode config host = startWithObservation mode config host
