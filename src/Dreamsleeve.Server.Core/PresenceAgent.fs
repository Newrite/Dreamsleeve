namespace Dreamsleeve.Server.Core

open System
open System.Collections.Generic
open System.Threading.Tasks
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

/// Owns online membership and the latest detached state used by snapshot and deltas.
[<RequireQualifiedAccess>]
module PresenceAgent =
    type private Member = {
        ConnectionId: Guid
        Events: ReliableAgentRef<PresenceEvent>
        mutable Latest: PlayerSnapshot
        mutable Published: PlayerSnapshot
    }

    type private State = {
        Members: Dictionary<Guid, Member>
        Players: Dictionary<PlayerId, Guid>
        Dirty: HashSet<PlayerId>
        mutable FlushScheduled: bool
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

    let private project state observer source =
        { source with Location = visibleLocation state observer source }

    let private snapshot state observer =
        state.Members.Values
        |> Seq.map (fun memberState -> project state observer memberState.Latest)
        |> Seq.sortBy _.Data.PlayerId
        |> List.ofSeq

    let private identityEqual (previous: PlayerSnapshot) (latest: PlayerSnapshot) =
        previous.Data = latest.Data && previous.CharacterName = latest.CharacterName
        && previous.CharacterGeneration = latest.CharacterGeneration

    let private deliverDelta state context subscriber event =
        match deliver state context subscriber event with
        | Some playerId -> broadcast state context None (PresenceEvent.Left playerId)
        | None -> ()

    let private publishDirty state context =
        if state.Dirty.Count > 0 then
            // Freeze both sides of every comparison for the whole batch. Advancing a
            // source baseline early would lose visibility changes when both peers move.
            let members = state.Members.Values |> Seq.toArray
            let changed =
                members
                |> Array.filter (fun memberState -> state.Dirty.Contains memberState.Latest.Data.PlayerId)
            state.Dirty.Clear()

            for observer in members do
                let sources =
                    if observer.Published.Location <> observer.Latest.Location then members
                    else changed

                for source in sources do
                    // Delivery may remove a slow member and publish Left immediately.
                    // Never follow that Left with a stale update from this frozen batch.
                    if state.Members.ContainsKey observer.ConnectionId && state.Members.ContainsKey source.ConnectionId then
                        let previous = visibleLocation state observer.Published source.Published
                        let latest = visibleLocation state observer.Latest source.Latest

                        if not (identityEqual source.Published source.Latest) then
                            deliverDelta state context observer (PresenceEvent.Updated { source.Latest with Location = latest })
                        else
                            let values =
                                if source.Published.ActorValues = source.Latest.ActorValues then ValueNone
                                else ValueSome source.Latest.ActorValues
                            let details =
                                if source.Published.Details = source.Latest.Details then ValueNone
                                else ValueSome source.Latest.Details
                            if values.IsSome || details.IsSome then
                                deliverDelta state context observer (PresenceEvent.MetadataChanged(source.Latest.Data.PlayerId, values, details))
                            if previous <> latest && state.Members.ContainsKey observer.ConnectionId && state.Members.ContainsKey source.ConnectionId then
                                deliverDelta state context observer (PresenceEvent.Moved(source.Latest.Data.PlayerId, latest))

            for memberState in members do
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
            publishDirty state context
            // A slow existing subscriber removed by this flush must stay removed.
            if not existedBefore || state.Members.ContainsKey subscription.ConnectionId then
                let existing = state.Members.ContainsKey subscription.ConnectionId
                let memberState =
                    match state.Members.TryGetValue subscription.ConnectionId with
                    | true, current -> { current with Events = subscription.Events }
                    | false, _ -> {
                        ConnectionId = subscription.ConnectionId; Events = subscription.Events
                        Latest = subscription.Snapshot; Published = subscription.Snapshot
                      }
                state.Members[subscription.ConnectionId] <- memberState
                state.Players[subscription.Snapshot.Data.PlayerId] <- subscription.ConnectionId

                match deliver state context memberState (PresenceEvent.Snapshot(snapshot state memberState.Latest)) with
                | Some playerId when existing -> broadcast state context None (PresenceEvent.Left playerId)
                | Some _ -> ()
                | None when not existing ->
                    let recipients = state.Members.Values |> Seq.toArray
                    for recipient in recipients do
                        if recipient.ConnectionId <> subscription.ConnectionId
                           && state.Members.ContainsKey recipient.ConnectionId
                           && state.Members.ContainsKey subscription.ConnectionId then
                            let joined = project state recipient.Latest memberState.Latest
                            deliverDelta state context recipient (PresenceEvent.Joined joined)
                | None -> ()

    let private schedule (config: PresenceOptions) state (context: AgentContext<PresenceCommand>) =
        if not state.FlushScheduled then
            state.FlushScheduled <- true
            // One one-shot observation, rearmed only by a later update. Watch detaches
            // when dispatch ends, so Complete never waits for the replication interval.
            context.Watch(Task.Delay(config.ReplicationIntervalMs, context.CancellationToken), fun _ -> PresenceCommand.Flush)

    let private update config state context connectionId (value: PlayerSnapshot) =
        match state.Members.TryGetValue connectionId with
        | false, _ -> ()
        | true, memberState when memberState.Latest.Data <> value.Data ->
            notifyHost state context (SessionHostCommand.Close(connectionId, "presence_identity_conflict"))
        | true, memberState ->
            memberState.Latest <- value
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
        | PresenceCommand.Join subscription -> join state context subscription
        | PresenceCommand.Update(connectionId, value) -> update config state context connectionId value
        | PresenceCommand.Flush ->
            state.FlushScheduled <- false
            publishDirty state context
        | PresenceCommand.Detach request -> detach state context request
    }

    let private isControl = function
        | PresenceCommand.Join _ | PresenceCommand.Flush | PresenceCommand.Detach _ -> true
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
                VisibilityDistanceSquared = double config.VisibilityDistance * double config.VisibilityDistance
                FlushScheduled = false; Host = AgentOutbox(config.MaxControlDeliveries, host)
            }
            let options = {
                AgentOptions.create "presence" with
                    Mailbox = AgentMailbox.boundedWithControl config.MailboxCapacity config.ControlReserve
            }
            Ok (Agent.Start(options, handle config state, isControl = isControl))
