namespace Dreamsleeve.Server.Core

open System
open System.Collections.Generic
open System.Threading
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

type SessionRegistryConfig = {
    MailboxCapacity: int
    MaxSessions: int
    PlayerMailboxCapacity: int
    MaxPendingPerPlayer: int
    MaxPendingChannelRequests: int
    MaxPendingOutput: int
}

[<RequireQualifiedAccess>]
type SessionRegistryFailure =
    | DependencyUnavailable
    | OperationFailed of exn
    | InvalidReply of string
    | Overloaded

[<RequireQualifiedAccess>]
type SessionOutput =
    | Send of connectionId: Guid * response: ChatResponse
    | Close of connectionId: Guid
    | PlayerFailed of connectionId: Guid * failure: PlayerFailure
    | Failed of SessionRegistryFailure

[<RequireQualifiedAccess>]
type SessionRegistryMessage =
    | BindChannel of ReliableAgentRef<ChannelRequest>
    | Open of SessionOpenRequest
    | Disconnect of connectionId: Guid
    | ChannelReplied of ChannelReply
    | PlayerReported of connectionId: Guid * event: PlayerEvent
    | PlayerStopped of connectionId: Guid * outcome: Result<unit, exn>
    | PlayerAdmissionFinished of connectionId: Guid * Result<AgentDeliveryResult, exn>
    | ChannelAdmissionFinished of Result<AgentDeliveryResult, exn>
    | OutputAdmissionFinished of Result<AgentDeliveryResult, exn>
    | UpdatePlayer of connectionId: Guid * PlayerUpdate
    | ReadPlayer of connectionId: Guid * ReplyChannel<Result<PlayerSnapshot, PlayerStateError>>
    /// Forwarded by the owner of the profile store/channel, not an operation failure.
    | DependencyStopped of SessionRegistryFailure
    | Stop

/// Owns routing, player identity reservations and the ordered global online view.
/// Personal opening state and all mutable Player data belong to PlayerAgent.
[<RequireQualifiedAccess>]
module SessionRegistry =
    type private Membership = {
        Profile: PlayerData
        mutable Announced: bool
        mutable Leaving: bool
    }

    type private Entry = {
        Request: SessionOpenRequest
        Agent: Agent<PlayerMessage>
        Output: AgentOutbox<PlayerMessage>
        Reads: ResizeArray<ReplyChannel<Result<PlayerSnapshot, PlayerStateError>>>
        mutable Connected: bool
        mutable Stopped: bool
        mutable PlayerId: PlayerId option
        mutable Member: Membership option
    }

    type private MembershipOperation =
        | Join of Guid
        | Leave of Guid

    type private State = {
        Entries: Dictionary<Guid, Entry>
        Players: Dictionary<PlayerId, Guid>
        Pending: Dictionary<Guid, MembershipOperation>
        MaxPendingChannelRequests: int
        Output: AgentOutbox<SessionOutput>
        mutable Channel: AgentOutbox<ChannelRequest> option
        mutable Stopping: bool
        mutable Failing: bool
    }

    let private emit state (context: AgentContext<SessionRegistryMessage>) value =
        if state.Output.TryEnqueue value then
            state.Output.Pump(context, SessionRegistryMessage.OutputAdmissionFinished)
        else
            // The lifecycle owner must close transports when registry Completion ends.
            context.Abort()

    let private closeReads entry =
        for reply in entry.Reads do
            reply.Reply(Error PlayerStateError.Closed)
        entry.Reads.Clear()

    let private close state context entry =
        closeReads entry
        if entry.Connected then
            entry.Connected <- false
            emit state context (SessionOutput.Close entry.Request.ConnectionId)

    let private fail state context reason =
        if not state.Failing then
            state.Failing <- true
            emit state context (SessionOutput.Failed reason)
            for entry in state.Entries.Values do
                close state context entry
                entry.Agent.Abort()

    let private failPlayer state context entry reason =
        emit state context (SessionOutput.PlayerFailed(entry.Request.ConnectionId, reason))
        close state context entry
        entry.Agent.Abort()

    let private sendPlayer state (context: AgentContext<SessionRegistryMessage>) entry message =
        if entry.Stopped then
            false
        elif entry.Output.TryEnqueue message then
            let completed result = SessionRegistryMessage.PlayerAdmissionFinished(entry.Request.ConnectionId, result)
            entry.Output.Pump(context, completed)
            true
        else
            failPlayer state context entry PlayerFailure.Overloaded
            false

    let private sendChannel state context operation command =
        match state.Channel with
        | None -> fail state context SessionRegistryFailure.DependencyUnavailable
        | Some _ when state.Pending.Count >= state.MaxPendingChannelRequests ->
            fail state context SessionRegistryFailure.Overloaded
        | Some output ->
            let id = Guid.NewGuid()
            if output.TryEnqueue { OperationId = id; Command = command } then
                state.Pending.Add(id, operation)
                output.Pump(context, SessionRegistryMessage.ChannelAdmissionFinished)
            else
                fail state context SessionRegistryFailure.Overloaded

    let private remove state entry =
        state.Entries.Remove entry.Request.ConnectionId |> ignore
        match entry.PlayerId with
        | Some playerId -> state.Players.Remove playerId |> ignore
        | None -> ()

    let private leave state context entry =
        match entry.Member with
        | Some memberState when not memberState.Leaving ->
            memberState.Leaving <- true
            sendChannel state context (Leave entry.Request.ConnectionId) (ChannelCommand.Leave memberState.Profile.PlayerId)
        | Some _ | None -> ()

    let private online state =
        state.Entries.Values
        |> Seq.choose (fun entry ->
            match entry.Member with
            | Some memberState when memberState.Announced -> Some memberState.Profile
            | Some _ | None -> None)
        |> Seq.sortBy _.PlayerId
        |> List.ofSeq

    let private broadcast state context exceptConnection response =
        for entry in state.Entries.Values do
            match entry.Member with
            | Some memberState when memberState.Announced && entry.Connected && entry.Request.ConnectionId <> exceptConnection ->
                emit state context (SessionOutput.Send(entry.Request.ConnectionId, response))
            | Some _ | None -> ()

    let private watchChild (child: Agent<PlayerMessage>) (token: CancellationToken) = task {
        // Cancellation aborts the child but never abandons its cleanup.
        use registration = token.Register(fun () -> child.Abort())
        do! child.Completion
    }

    let private reject state context request code message =
        let rejection = { Code = code; Message = message; Field = "" }
        emit state context (SessionOutput.Send(request.ConnectionId, ChatResponse.RequestRejected(request.RequestId, rejection)))

    let private openSession (config: SessionRegistryConfig) profiles state (context: AgentContext<SessionRegistryMessage>) request =
        if state.Stopping || state.Channel.IsNone then
            emit state context (SessionOutput.Close request.ConnectionId)
        elif state.Entries.ContainsKey request.ConnectionId then
            reject state context request RequestRejectionCode.SessionAlreadyOpen "Session is already opening or open."
        elif state.Entries.Count >= config.MaxSessions then
            emit state context (SessionOutput.Close request.ConnectionId)
        else
            match context.Ref.TryReliable() with
            | None -> context.Abort()
            | Some address ->
                let output = address.Map(fun event -> SessionRegistryMessage.PlayerReported(request.ConnectionId, event))
                match PlayerAgent.start config.PlayerMailboxCapacity config.MaxPendingPerPlayer request profiles output with
                | Error _ -> fail state context (SessionRegistryFailure.InvalidReply "player configuration")
                | Ok child ->
                    match child.Ref.TryReliable() with
                    | None ->
                        child.Abort()
                        fail state context (SessionRegistryFailure.InvalidReply "player mailbox")
                    | Some destination ->
                        let entry = {
                            Request = request
                            Agent = child
                            Output = AgentOutbox(config.MaxPendingPerPlayer, destination)
                            Reads = ResizeArray()
                            Connected = true
                            Stopped = false
                            PlayerId = None
                            Member = None
                        }
                        state.Entries.Add(request.ConnectionId, entry)
                        context.PipeToSelf(watchChild child, fun result -> SessionRegistryMessage.PlayerStopped(request.ConnectionId, result))

    let private disconnect state context connectionId =
        match state.Entries.TryGetValue connectionId with
        | true, entry when entry.Connected ->
            closeReads entry
            entry.Connected <- false
            sendPlayer state context entry PlayerMessage.Stop |> ignore
        | true, _ | false, _ -> ()

    let private playerEvent state context connectionId event =
        match state.Entries.TryGetValue connectionId with
        | false, _ -> ()
        | true, entry when entry.Stopped -> ()
        | true, entry ->
            match event with
            | PlayerEvent.Join profile ->
                match entry.Member with
                | Some _ -> failPlayer state context entry PlayerFailure.InvalidReply
                | None when state.Players.ContainsKey profile.PlayerId ->
                    reject state context entry.Request RequestRejectionCode.SessionAlreadyOpen "Player already has a session."
                    close state context entry
                    entry.Agent.Abort()
                | None ->
                    state.Players.Add(profile.PlayerId, connectionId)
                    entry.PlayerId <- Some profile.PlayerId
                    entry.Member <- Some { Profile = profile; Announced = false; Leaving = false }
                    sendChannel state context (Join connectionId) (ChannelCommand.Join profile.PlayerId)
            | PlayerEvent.Leave playerId ->
                match entry.Member with
                | Some memberState when memberState.Profile.PlayerId = playerId ->
                    close state context entry
                    leave state context entry
                | Some _ | None -> failPlayer state context entry PlayerFailure.InvalidReply
            | PlayerEvent.Failed error ->
                emit state context (SessionOutput.PlayerFailed(connectionId, error))
                close state context entry
                // The child drains its failure notification before completing normally.

    let private playerStopped state context connectionId (outcome: Result<unit, exn>) =
        match state.Entries.TryGetValue connectionId with
        | false, _ -> ()
        | true, entry ->
            match outcome with
            | Error (:? OperationCanceledException) when not entry.Connected -> ()
            | Error error -> emit state context (SessionOutput.PlayerFailed(connectionId, PlayerFailure.Unexpected error))
            | Ok () -> ()
            entry.Stopped <- true
            close state context entry
            match entry.Member with
            | None -> remove state entry
            | Some _ -> leave state context entry

    let private joined globalId state context entry snapshot =
        match entry.Member with
        | None -> fail state context (SessionRegistryFailure.InvalidReply "missing channel member")
        | Some memberState ->
            if entry.Connected && not entry.Stopped then
                memberState.Announced <- true
                let welcome = {
                    SelfPlayerId = memberState.Profile.PlayerId
                    GlobalChannelId = globalId
                    Players = online state
                    RecentMessages = snapshot.Messages
                }
                // Commit bootstrap here, in the channel's reply order, before another
                // child's event can overtake it. Player only receives the local transition.
                emit state context (SessionOutput.Send(entry.Request.ConnectionId,
                    ChatResponse.SessionOpened(entry.Request.RequestId, welcome)))
                broadcast state context entry.Request.ConnectionId (ChatResponse.PlayerJoined memberState.Profile)
            sendPlayer state context entry PlayerMessage.Joined |> ignore

    let private left state context entry =
        match entry.Member with
        | None -> fail state context (SessionRegistryFailure.InvalidReply "missing channel member")
        | Some memberState ->
            entry.Member <- None
            if memberState.Announced then
                broadcast state context entry.Request.ConnectionId (ChatResponse.PlayerLeft memberState.Profile.PlayerId)
            if entry.Stopped then
                remove state entry
            else
                sendPlayer state context entry PlayerMessage.Left |> ignore

    let private channelReply globalId state context (reply: ChannelReply) =
        match state.Pending.TryGetValue reply.OperationId with
        | false, _ -> ()
        | true, operation ->
            state.Pending.Remove reply.OperationId |> ignore
            let connectionId = match operation with Join id | Leave id -> id
            match state.Entries.TryGetValue connectionId with
            | false, _ -> ()
            | true, entry ->
                if reply.ChannelId <> globalId then
                    fail state context (SessionRegistryFailure.InvalidReply "channel identity")
                else
                    match operation, reply.Result with
                    | Join _, Ok (ChannelOutcome.Joined snapshot) -> joined globalId state context entry snapshot
                    | Leave _, Ok (ChannelOutcome.Left _) -> left state context entry
                    | Join _, Ok (ChannelOutcome.Left _)
                    | Leave _, Ok (ChannelOutcome.Joined _)
                    | (Join _ | Leave _), Ok (ChannelOutcome.Published _ | ChannelOutcome.History _)
                    | (Join _ | Leave _), Error _ -> fail state context (SessionRegistryFailure.InvalidReply "channel outcome")

    let private playerAdmission state context connectionId result =
        match state.Entries.TryGetValue connectionId with
        | false, _ -> ()
        | true, entry ->
            entry.Output.Acknowledge()
            match result with
            | Ok AgentDeliveryResult.Posted ->
                entry.Output.Pump(context, fun result -> SessionRegistryMessage.PlayerAdmissionFinished(connectionId, result))
            | Ok (AgentDeliveryResult.Closed | AgentDeliveryResult.Canceled) -> entry.Agent.Abort()
            | Error error -> failPlayer state context entry (PlayerFailure.Unexpected error)

    let private outputAdmission state context result =
        state.Output.Acknowledge()
        match result with
        | Ok AgentDeliveryResult.Posted -> state.Output.Pump(context, SessionRegistryMessage.OutputAdmissionFinished)
        | Ok (AgentDeliveryResult.Closed | AgentDeliveryResult.Canceled) | Error _ -> context.Abort()

    let private channelAdmission state context result =
        match state.Channel with
        | None -> fail state context (SessionRegistryFailure.InvalidReply "channel admission")
        | Some output ->
            output.Acknowledge()
            match result with
            | Ok AgentDeliveryResult.Posted -> output.Pump(context, SessionRegistryMessage.ChannelAdmissionFinished)
            | Ok (AgentDeliveryResult.Closed | AgentDeliveryResult.Canceled) -> fail state context SessionRegistryFailure.DependencyUnavailable
            | Error error -> fail state context (SessionRegistryFailure.OperationFailed error)

    let private readPlayer (config: SessionRegistryConfig) state context connectionId (reply: ReplyChannel<_>) =
        match state.Entries.TryGetValue connectionId with
        | true, entry when entry.Connected ->
            // A forwarded ordinary message has no Ask cleanup of its own. Retain the
            // reply until settled so disconnect/fault cannot leave the caller waiting.
            entry.Reads.RemoveAll(fun pending -> pending.IsCompleted) |> ignore
            if entry.Reads.Count >= config.MaxPendingPerPlayer then
                reply.Reply(Error PlayerStateError.Busy)
            else
                entry.Reads.Add reply
                if not (sendPlayer state context entry (PlayerMessage.Read reply)) then
                    reply.Reply(Error PlayerStateError.Closed)
        | true, _ | false, _ -> reply.Reply(Error PlayerStateError.Closed)

    let private stop state context =
        state.Stopping <- true
        for entry in state.Entries.Values do
            close state context entry
            sendPlayer state context entry PlayerMessage.Stop |> ignore

    let private handle (config: SessionRegistryConfig) globalId profiles state (context: AgentContext<SessionRegistryMessage>) message = task {
        match message with
        | SessionRegistryMessage.OutputAdmissionFinished result -> outputAdmission state context result
        | _ when state.Failing -> ()
        | SessionRegistryMessage.BindChannel channel ->
            match state.Channel with
            | None -> state.Channel <- Some (AgentOutbox(config.MaxPendingChannelRequests, channel))
            | Some _ -> fail state context (SessionRegistryFailure.InvalidReply "channel already bound")
        | SessionRegistryMessage.Open request -> openSession config profiles state context request
        | SessionRegistryMessage.Disconnect connectionId -> disconnect state context connectionId
        | SessionRegistryMessage.PlayerReported(connectionId, event) -> playerEvent state context connectionId event
        | SessionRegistryMessage.PlayerStopped(connectionId, outcome) -> playerStopped state context connectionId outcome
        | SessionRegistryMessage.ChannelReplied reply -> channelReply globalId state context reply
        | SessionRegistryMessage.PlayerAdmissionFinished(connectionId, result) -> playerAdmission state context connectionId result
        | SessionRegistryMessage.ChannelAdmissionFinished result -> channelAdmission state context result
        | SessionRegistryMessage.UpdatePlayer(connectionId, command) ->
            match state.Entries.TryGetValue connectionId with
            | true, entry when entry.Connected -> sendPlayer state context entry (PlayerMessage.Update command) |> ignore
            | true, _ | false, _ -> ()
        | SessionRegistryMessage.ReadPlayer(connectionId, reply) -> readPlayer config state context connectionId reply
        | SessionRegistryMessage.DependencyStopped error -> fail state context error
        | SessionRegistryMessage.Stop -> stop state context

        if state.Failing && state.Output.IsEmpty then
            context.Abort()
        elif state.Stopping && state.Entries.Count = 0 && state.Pending.Count = 0 && state.Output.IsEmpty then
            let channelDrained = state.Channel |> Option.forall _.IsEmpty
            if channelDrained then context.Complete() |> ignore
    }

    /// The registry owns player lifetimes. The caller owns the channel/profile store.
    /// Use Stop for draining or Abort for cancellation; Completion joins all children.
    let start (config: SessionRegistryConfig) globalId profiles output =
        let limits = [config.MailboxCapacity; config.MaxSessions; config.PlayerMailboxCapacity;
                      config.MaxPendingPerPlayer; config.MaxPendingChannelRequests; config.MaxPendingOutput]
        if limits |> List.exists (fun value -> value < 1) then
            Error "Session, player and pending queue limits must be positive."
        elif config.MaxPendingOutput <= config.MaxSessions then
            Error "MaxPendingOutput must allow a failure notification and one close per session."
        else
            let state = {
                Entries = Dictionary()
                Players = Dictionary()
                Pending = Dictionary()
                MaxPendingChannelRequests = config.MaxPendingChannelRequests
                Output = AgentOutbox(config.MaxPendingOutput, output)
                Channel = None
                Stopping = false
                Failing = false
            }
            let options = {
                AgentOptions.create "sessions" with
                    Mailbox = AgentMailbox.boundedWait config.MailboxCapacity
            }
            Ok (Agent.Start(options, handle config globalId profiles state))
