namespace Dreamsleeve.Server.Core

open System
open System.Collections.Generic
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

/// The online snapshot and all later deltas have one sequential owner.
[<RequireQualifiedAccess>]
module PresenceAgent =
    type private State = {
        Members: Dictionary<Guid, Subscription<PresenceEvent>>
        Players: Dictionary<PlayerId, Guid>
        Host: AgentOutbox<SessionHostCommand>
    }

    let private notifyHost state context command =
        if not (state.Host.TrySend(context, command)) then
            context.Abort()

    let private remove state connectionId =
        match state.Members.TryGetValue connectionId with
        | false, _ -> None
        | true, memberState ->
            state.Members.Remove connectionId |> ignore
            state.Players.Remove memberState.Profile.PlayerId |> ignore
            Some memberState.Profile.PlayerId

    let private deliver state context (subscriber: Subscription<PresenceEvent>) event =
        match subscriber.Events.TryPost event with
        | AgentTryDeliveryResult.Posted -> None
        | AgentTryDeliveryResult.Closed -> remove state subscriber.ConnectionId
        | AgentTryDeliveryResult.Full ->
            let removed = remove state subscriber.ConnectionId
            notifyHost state context (SessionHostCommand.SlowConsumer subscriber.ConnectionId)
            removed

    let private broadcast state (context: AgentContext<PresenceCommand>) exceptConnection event =
        // A failed recipient can cause another Left. Process the finite cascade
        // iteratively; each removed subscription contributes at most one delta.
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

    let private snapshot state =
        state.Members.Values
        |> Seq.map _.Profile
        |> Seq.sortBy _.PlayerId
        |> List.ofSeq

    let private join state context (subscription: Subscription<PresenceEvent>) =
        let existing = state.Members.ContainsKey subscription.ConnectionId
        let sameConnection =
            match state.Members.TryGetValue subscription.ConnectionId with
            | true, memberState -> memberState.Profile = subscription.Profile
            | false, _ -> true
        let samePlayer =
            match state.Players.TryGetValue subscription.Profile.PlayerId with
            | true, owner -> owner = subscription.ConnectionId
            | false, _ -> true

        if not sameConnection || not samePlayer then
            notifyHost state context (SessionHostCommand.Close(subscription.ConnectionId, "presence_identity_conflict"))
        else
            state.Members[subscription.ConnectionId] <- subscription
            state.Players[subscription.Profile.PlayerId] <- subscription.ConnectionId

            match deliver state context subscription (PresenceEvent.Snapshot(snapshot state)) with
            | Some playerId when existing -> broadcast state context None (PresenceEvent.Left playerId)
            | Some _ -> ()
            | None when not existing ->
                broadcast state context (Some subscription.ConnectionId) (PresenceEvent.Joined subscription.Profile)
            | None -> ()

    let private detach state (context: AgentContext<PresenceCommand>) (request: SessionDetach) =
        match remove state request.ConnectionId with
        | Some playerId -> broadcast state context None (PresenceEvent.Left playerId)
        | None -> ()

        match request.ReplyTo.TryPost request.ConnectionId with
        | AgentTryDeliveryResult.Posted | AgentTryDeliveryResult.Closed -> ()
        | AgentTryDeliveryResult.Full -> context.Abort()

    let private handle state (context: AgentContext<PresenceCommand>) command = task {
        match command with
        | PresenceCommand.Join subscription -> join state context subscription
        | PresenceCommand.Detach request -> detach state context request
    }

    let private isControl = function
        | PresenceCommand.Join _ | PresenceCommand.Detach _ -> true

    let start (config: PresenceOptions) (host: ReliableAgentRef<SessionHostCommand>) =
        if config.MailboxCapacity < 1 then
            Error (DomainError.InvalidLimit("mailboxCapacity", config.MailboxCapacity))
        elif config.ControlReserve < 1 || int64 config.MailboxCapacity + int64 config.ControlReserve > int64 Int32.MaxValue then
            Error (DomainError.InvalidLimit("controlReserve", config.ControlReserve))
        elif config.MaxControlDeliveries < 1 then
            Error (DomainError.InvalidLimit("maxControlDeliveries", config.MaxControlDeliveries))
        else
            let state = { Members = Dictionary(); Players = Dictionary(); Host = AgentOutbox(config.MaxControlDeliveries, host) }
            let options = {
                AgentOptions.create "presence" with
                    Mailbox = AgentMailbox.boundedWithControl config.MailboxCapacity config.ControlReserve
            }
            Ok (Agent.Start(options, handle state, isControl = isControl))
