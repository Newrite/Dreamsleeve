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
    }

    type private State = {
        Members: Dictionary<Guid, Member>
        Players: Dictionary<PlayerId, Guid>
        Dirty: HashSet<PlayerId>
        Candidates: HashSet<Guid>
        Movements: ResizeArray<MovementChange>
        LatestIndex: SpatialIndex.State
        mutable Ticker: AgentTicker option
        mutable LastFlush: int64
        VisibilityDistanceSquared: double
        Host: AgentOutbox<SessionHostCommand>
    }

    let private notifyHost state context command =
        if not (state.Host.TrySend(context, command)) then
            context.Abort()

    let private remove state connectionId =
        match state.Members.TryGetValue connectionId with
        | false, _ -> None
        | true, memberState ->
            let playerId = memberState.Latest.Data.PlayerId
            state.Members.Remove connectionId |> ignore
            state.Players.Remove playerId |> ignore
            state.Dirty.Remove playerId |> ignore
            SpatialIndex.remove connectionId state.LatestIndex
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

    let private broadcast state (context: AgentContext<PresenceCommand>) exceptConnection event =
        // A failed recipient can cause another Left. Each removed member contributes
        // at most one delta, and no healthy recipient waits for the slow one.
        let pending = Queue<Guid option * PresenceEvent>()
        pending.Enqueue(exceptConnection, event)
        while pending.Count > 0 && not context.CancellationToken.IsCancellationRequested do
            let exceptConnection, event = pending.Dequeue()
            let recipients = state.Members.Values |> Seq.toArray
            for recipient in recipients do
                if Some recipient.ConnectionId <> exceptConnection then
                    match deliver state context recipient event with
                    | Some playerId -> pending.Enqueue(None, PresenceEvent.Left playerId)
                    | None -> ()

    // Visibility is symmetric in space, but self always receives its own telemetry.
    // Radius is validated once at startup; double arithmetic avoids float overflow.
    let private visibleLocation state (observer: PlayerSnapshot) (source: PlayerSnapshot) =
        if observer.Data.PlayerId = source.Data.PlayerId then source.Location
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
        let playerId = source.Latest.Data.PlayerId
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
            { source.Latest with Location = ValueSome location; ViewRevision = view.Revision }

    let private snapshot state observer =
        state.Members.Values
        |> Seq.map (project state observer)
        |> Seq.sortBy _.Data.PlayerId
        |> List.ofSeq

    let private identityEqual (previous: PlayerSnapshot) (latest: PlayerSnapshot) =
        previous.Data = latest.Data && previous.CharacterName = latest.CharacterName
        && previous.CharacterGeneration = latest.CharacterGeneration

    let private deliverDelta state context subscriber event =
        match deliver state context subscriber event with
        | Some playerId -> broadcast state context None (PresenceEvent.Left playerId)
        | None -> ()

    let private publishGlobal state context observer source =
        if state.Members.ContainsKey observer.ConnectionId && state.Members.ContainsKey source.ConnectionId then
            if not (identityEqual source.Published source.Latest) then
                deliverDelta state context observer (PresenceEvent.Updated { source.Latest with Location = ValueNone; ViewRevision = 0UL })
            else
                let values =
                    if source.Published.ActorValues = source.Latest.ActorValues then ValueNone
                    else ValueSome source.Latest.ActorValues
                let details =
                    if source.Published.Details = source.Latest.Details then ValueNone
                    else ValueSome source.Latest.Details
                deliverDelta state context observer (PresenceEvent.MetadataChanged(source.Latest.Data.PlayerId, values, details))

    let private publishMovement state context sendSamples observer =
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
        for id in candidates do
            match state.Members.TryGetValue id with
            | true, source when state.Members.ContainsKey observer.ConnectionId ->
                let playerId = source.Latest.Data.PlayerId
                match visibleLocation state observer.Latest source.Latest with
                | ValueSome location ->
                    let view, changed = establishView observer source
                    if changed then
                        let baseline = {
                            PlayerId = playerId; ViewRevision = view.Revision
                            Sequence = source.Latest.MovementSequence; Location = ValueSome location
                        }
                        deliverDelta state context observer (PresenceEvent.VisibilityChanged baseline)

                    if sendSamples then
                        movements.Add {
                            PlayerId = playerId; ViewRevision = view.Revision
                            Sequence = source.Latest.MovementSequence; Pose = MovementPose.ofLocation location
                        }
                | ValueNone ->
                    if observer.Views.Remove playerId then
                        let cleared = {
                            PlayerId = playerId; ViewRevision = nextRevision observer
                            Sequence = 0UL; Location = ValueNone
                        }
                        deliverDelta state context observer (PresenceEvent.VisibilityChanged cleared)
            | true, _ | false, _ -> ()

        if movements.Count > 0 && state.Members.ContainsKey observer.ConnectionId then
            let ordered = movements.ToArray()
            Array.sortInPlaceBy (fun (change: MovementChange) -> change.PlayerId) ordered
            match observer.Events.TryPost (PresenceEvent.Moved ordered) with
            | AgentTryDeliveryResult.Posted | AgentTryDeliveryResult.Full -> ()
            | AgentTryDeliveryResult.Closed ->
                match remove state observer.ConnectionId with
                | Some playerId -> broadcast state context None (PresenceEvent.Left playerId)
                | None -> ()

        movements.Clear()
        candidates.Clear()

    let private publishDirty state context sendSamples =
        // A single agent turn freezes latest state for all observers. Dirty only
        // controls metadata; positions repeat even when every source has stopped.
        let members = state.Members.Values |> Seq.toArray
        let changed = members |> Array.filter (fun memberState -> state.Dirty.Contains memberState.Latest.Data.PlayerId)
        state.Dirty.Clear()
        let globalChanges =
            changed |> Array.filter (fun source ->
                not (identityEqual source.Published source.Latest)
                || source.Published.ActorValues <> source.Latest.ActorValues
                || source.Published.Details <> source.Latest.Details)

        for observer in members do
            for source in globalChanges do publishGlobal state context observer source
            if state.Members.ContainsKey observer.ConnectionId then
                publishMovement state context sendSamples observer

        for memberState in changed do
            if state.Members.ContainsKey memberState.ConnectionId then
                memberState.Published <- memberState.Latest

    let private join state context (subscription: PresenceSubscription) =
        let existedBefore = state.Members.ContainsKey subscription.ConnectionId
        let sameConnection =
            match state.Members.TryGetValue subscription.ConnectionId with
            | true, memberState -> memberState.Latest.Data = subscription.Snapshot.Data
            | false, _ -> true
        let samePlayer =
            match state.Players.TryGetValue subscription.Snapshot.Data.PlayerId with
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
                        Views = Dictionary(); NextRevision = 0UL
                      }
                state.Members[subscription.ConnectionId] <- memberState
                state.Players[subscription.Snapshot.Data.PlayerId] <- subscription.ConnectionId
                SpatialIndex.set memberState.ConnectionId memberState.Latest.Location state.LatestIndex

                match deliver state context memberState (PresenceEvent.Snapshot(snapshot state memberState)) with
                | Some playerId when existing -> broadcast state context None (PresenceEvent.Left playerId)
                | Some _ -> ()
                | None when not existing ->
                    let recipients = state.Members.Values |> Seq.toArray
                    for recipient in recipients do
                        if recipient.ConnectionId <> subscription.ConnectionId
                           && state.Members.ContainsKey recipient.ConnectionId
                           && state.Members.ContainsKey subscription.ConnectionId then
                            let joined = project state recipient memberState
                            deliverDelta state context recipient (PresenceEvent.Joined joined)
                | None -> ()

    let private schedule (config: PresenceOptions) state context =
        if state.Ticker.IsNone then
            state.Ticker <- Some (AgentTicker.start (TimeSpan.FromMilliseconds(int64 config.ReplicationIntervalMs)) context PresenceCommand.Flush)

    let private update config state context connectionId (value: PlayerSnapshot) =
        match state.Members.TryGetValue connectionId with
        | false, _ -> ()
        | true, memberState when memberState.Latest.Data <> value.Data ->
            notifyHost state context (SessionHostCommand.Close(connectionId, "presence_identity_conflict"))
        | true, memberState ->
            memberState.Latest <- value
            SpatialIndex.set connectionId value.Location state.LatestIndex
            state.Dirty.Add value.Data.PlayerId |> ignore
            schedule config state context

    let private detach state (context: AgentContext<PresenceCommand>) (request: SessionDetach) =
        match remove state request.ConnectionId with
        | Some playerId -> broadcast state context None (PresenceEvent.Left playerId)
        | None -> ()

        match request.ReplyTo.TryPost request.ConnectionId with
        | AgentTryDeliveryResult.Posted | AgentTryDeliveryResult.Closed -> ()
        | AgentTryDeliveryResult.Full -> context.Abort()

    let private handle config state (context: AgentContext<PresenceCommand>) command = task {
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
    }

    let private isControl = function
        | PresenceCommand.Join _ | PresenceCommand.Flush _ | PresenceCommand.Detach _ -> true
        | PresenceCommand.Update _ -> false

    let start (config: PresenceOptions) (host: ReliableAgentRef<SessionHostCommand>) =
        if config.MailboxCapacity < 1 then
            Error (DomainError.InvalidLimit("mailboxCapacity", config.MailboxCapacity))
        elif config.ControlReserve < 1 || int64 config.MailboxCapacity + int64 config.ControlReserve > int64 Int32.MaxValue then
            Error (DomainError.InvalidLimit("controlReserve", config.ControlReserve))
        elif config.MaxControlDeliveries < 1 then
            Error (DomainError.InvalidLimit("maxControlDeliveries", config.MaxControlDeliveries))
        elif config.ReplicationIntervalMs < 1 then
            Error (DomainError.InvalidLimit("replicationIntervalMs", config.ReplicationIntervalMs))
        elif not (Single.IsFinite config.VisibilityDistance) || config.VisibilityDistance < 0.0f then
            Error DomainError.InvalidRadius
        else
            let state = {
                Members = Dictionary(); Players = Dictionary(); Dirty = HashSet()
                Candidates = HashSet(); Movements = ResizeArray()
                LatestIndex = SpatialIndex.create (double config.VisibilityDistance)
                VisibilityDistanceSquared = double config.VisibilityDistance * double config.VisibilityDistance
                Ticker = None; LastFlush = 0L; Host = AgentOutbox(config.MaxControlDeliveries, host)
            }
            let options = {
                AgentOptions.create "presence" with
                    Mailbox = AgentMailbox.boundedWithControl config.MailboxCapacity config.ControlReserve
            }
            Ok (Agent.Start(options, handle config state, isControl = isControl))
