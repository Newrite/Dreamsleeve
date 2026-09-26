namespace Dreamsleeve.Server.Core

open System
open System.Collections.Generic
open System.Threading
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

/// A fresh ID for each transport connection; never reuse an ENet peer slot as this ID.
type SessionOpenRequest = {
    ConnectionId: Guid
    RequestId: uint64
    Username: Username
    DisplayName: DisplayName
}

type SessionRegistryConfig = {
    MailboxCapacity: int
    MaxSessions: int
}

[<RequireQualifiedAccess>]
type SessionRegistryFailure =
    | DependencyUnavailable
    | OperationFailed of exn
    | InvalidReply of string

[<RequireQualifiedAccess>]
type SessionOutput =
    | Send of connectionId: Guid * response: ChatResponse
    | Close of connectionId: Guid
    | Failed of SessionRegistryFailure

[<RequireQualifiedAccess>]
type SessionRegistryMessage =
    | BindChannel of ReliableAgentRef<ChannelRequest>
    | Open of SessionOpenRequest
    | Disconnect of connectionId: Guid
    | ProfileReplied of ProfileReply
    | ChannelReplied of ChannelReply
    /// Posted internally by PipeToSelf; callers submit only Open/Disconnect after binding.
    | AdmissionFinished of Result<AgentDeliveryResult, exn>
    /// The lifecycle owner forwards dependency termination, including failure after admission.
    | DependencyStopped of SessionRegistryFailure
    | Stop

[<RequireQualifiedAccess>]
module SessionRegistry =
    type private Phase =
        | Finding
        | Creating
        | Joining of PlayerData
        | Online of PlayerData
        | Leaving of PlayerData * announced: bool

    type private Session = {
        Request: SessionOpenRequest
        mutable Connected: bool
        mutable Phase: Phase
    }

    type private Dispatch =
        | Profile of ProfileRequest
        | Channel of ChannelRequest

    type private State = {
        Sessions: Dictionary<Guid, Session>
        Names: Dictionary<Username, Guid>
        Pending: Dictionary<Guid, Session>
        Outbox: Queue<Dispatch>
        mutable Sending: bool
        mutable Stopping: bool
        mutable Channel: ReliableAgentRef<ChannelRequest> option
    }

    let private emit (output: ReliableAgentRef<SessionOutput>) (context: AgentContext<SessionRegistryMessage>) value = task {
        let! result = output.PostAsync(value, cancellationToken = context.CancellationToken)
        match result with
        | AgentDeliveryResult.Posted -> ()
        | AgentDeliveryResult.Closed -> context.Abort()
        | AgentDeliveryResult.Canceled -> ()
    }

    let private fail state output context reason = task {
        do! emit output context (SessionOutput.Failed reason)
        for session in state.Sessions.Values do
            if session.Connected then
                do! emit output context (SessionOutput.Close session.Request.ConnectionId)
        context.Abort()
    }

    let private remove state session =
        state.Sessions.Remove session.Request.ConnectionId |> ignore
        state.Names.Remove session.Request.Username |> ignore

    let private reject output context request code message =
        let rejection = { Code = code; Message = message; Field = "" }
        emit output context (SessionOutput.Send(request.ConnectionId,
            ChatResponse.RequestRejected(request.RequestId, rejection)))

    let private deliver (profiles: ReliableAgentRef<ProfileRequest>)
                        (channel: ReliableAgentRef<ChannelRequest>) dispatch (token: CancellationToken) = task {
        match dispatch with
        | Profile request -> return! profiles.PostAsync(request, cancellationToken = token)
        | Channel request -> return! channel.PostAsync(request, cancellationToken = token)
    }

    // One admission at a time preserves command order, even while a dependency is full.
    // Waiting happens outside the handler; at most one operation per session is pending.
    let private pump state profiles (context: AgentContext<SessionRegistryMessage>) =
        match state.Channel with
        | Some channel when not state.Sending && state.Outbox.Count > 0 ->
            let dispatch = state.Outbox.Dequeue()
            state.Sending <- true
            context.PipeToSelf(deliver profiles channel dispatch, SessionRegistryMessage.AdmissionFinished)
        | Some _ | None -> ()

    let private enqueue state session dispatch =
        let operationId = Guid.NewGuid()
        state.Pending.Add(operationId, session)
        state.Outbox.Enqueue(dispatch operationId)

    let private profile state replyTo session command =
        enqueue state session (fun id -> Profile { OperationId = id; Command = command; ReplyTo = replyTo })

    let private channel state session command =
        enqueue state session (fun id -> Channel { OperationId = id; Command = command })

    let private leave state session player announced =
        session.Phase <- Leaving(player, announced)
        channel state session (ChannelCommand.Leave player.PlayerId)

    let private onlineProfiles state =
        state.Sessions.Values
        |> Seq.choose (fun session ->
            match session.Phase with
            | Online player | Leaving(player, true) -> Some player
            | Finding | Creating | Joining _ | Leaving(_, false) -> None)
        |> Seq.sortBy _.PlayerId
        |> List.ofSeq

    let private broadcast state output context exceptConnection response = task {
        for session in state.Sessions.Values do
            match session.Phase with
            | Online _ when session.Connected && session.Request.ConnectionId <> exceptConnection ->
                do! emit output context (SessionOutput.Send(session.Request.ConnectionId, response))
            | Finding | Creating | Joining _ | Online _ | Leaving _ -> ()
    }

    let private openSession config state replyTo output context request = task {
        if state.Stopping then
            do! emit output context (SessionOutput.Close request.ConnectionId)
        elif state.Sessions.ContainsKey request.ConnectionId then
            do! reject output context request RequestRejectionCode.SessionAlreadyOpen "Session is already opening or open."
        elif state.Names.ContainsKey request.Username then
            do! reject output context request RequestRejectionCode.UsernameTaken "Username is already connected or opening."
        elif state.Channel.IsNone || state.Sessions.Count >= config.MaxSessions then
            do! emit output context (SessionOutput.Close request.ConnectionId)
        else
            let session = { Request = request; Connected = true; Phase = Finding }
            state.Sessions.Add(request.ConnectionId, session)
            state.Names.Add(request.Username, request.ConnectionId)
            profile state replyTo session (ProfileCommand.FindByUsername request.Username)
    }

    let private disconnect state connectionId =
        match state.Sessions.TryGetValue connectionId with
        | false, _ -> ()
        | true, session ->
            session.Connected <- false
            match session.Phase with
            | Online player -> leave state session player true
            | Finding | Creating | Joining _ | Leaving _ -> ()

    let private useProfile state session player =
        if session.Connected then
            session.Phase <- Joining player
            channel state session (ChannelCommand.Join player.PlayerId)
        else
            remove state session

    let private profileReply state replyTo output context (reply: ProfileReply) = task {
        match state.Pending.TryGetValue reply.OperationId with
        | false, _ -> () // A duplicate or reply from a previous operation cannot reopen a session.
        | true, session ->
            state.Pending.Remove reply.OperationId |> ignore
            match session.Phase, reply.Result with
            | Finding, Ok (ProfileOutcome.Found (Some player))
            | Creating, Ok (ProfileOutcome.Created player) ->
                if player.Username <> session.Request.Username then
                    do! fail state output context (SessionRegistryFailure.InvalidReply "profile identity")
                else
                    useProfile state session player
            | Finding, Ok (ProfileOutcome.Found None) ->
                if session.Connected then
                    session.Phase <- Creating
                    profile state replyTo session (ProfileCommand.Create(session.Request.Username, session.Request.DisplayName))
                else
                    remove state session
            | Creating, Error ProfileStoreError.UsernameTaken ->
                // Another storage consumer may have created the profile after our lookup.
                // Report the conflict; do not retry an unbounded lookup/create cycle.
                if session.Connected then
                    do! reject output context session.Request RequestRejectionCode.UsernameTaken "Username was reserved during opening."
                remove state session
            | (Finding | Creating), Error (ProfileStoreError.Failed error) ->
                do! fail state output context (SessionRegistryFailure.OperationFailed error)
            | (Finding | Creating), Error (ProfileStoreError.Canceled | ProfileStoreError.IdExhausted | ProfileStoreError.UsernameTaken) ->
                do! fail state output context SessionRegistryFailure.DependencyUnavailable
            | Finding, Ok (ProfileOutcome.Created _)
            | Creating, Ok (ProfileOutcome.Found _)
            | (Joining _ | Online _ | Leaving _), (Ok _ | Error _) ->
                do! fail state output context (SessionRegistryFailure.InvalidReply "profile outcome")
    }

    let private channelReply globalId state output context (reply: ChannelReply) = task {
        match state.Pending.TryGetValue reply.OperationId with
        | false, _ -> ()
        | true, session ->
            state.Pending.Remove reply.OperationId |> ignore
            if reply.ChannelId <> globalId then
                do! fail state output context (SessionRegistryFailure.InvalidReply "channel identity")
            else
                match session.Phase, reply.Result with
                | Joining player, Ok (ChannelOutcome.Joined snapshot) ->
                    if session.Connected then
                        session.Phase <- Online player
                        let welcome = {
                            SelfPlayerId = player.PlayerId
                            GlobalChannelId = globalId
                            Players = onlineProfiles state
                            RecentMessages = snapshot.Messages
                        }
                        do! emit output context (SessionOutput.Send(session.Request.ConnectionId,
                            ChatResponse.SessionOpened(session.Request.RequestId, welcome)))
                        do! broadcast state output context session.Request.ConnectionId (ChatResponse.PlayerJoined player)
                    else
                        leave state session player false
                | Leaving(player, announced), Ok (ChannelOutcome.Left _) ->
                    remove state session
                    if announced then
                        do! broadcast state output context session.Request.ConnectionId (ChatResponse.PlayerLeft player.PlayerId)
                | (Finding | Creating | Joining _ | Online _ | Leaving _), Error _
                | (Finding | Creating | Online _ | Leaving _), Ok (ChannelOutcome.Joined _)
                | (Finding | Creating | Joining _ | Online _), Ok (ChannelOutcome.Left _)
                | (Finding | Creating | Joining _ | Online _ | Leaving _), Ok (ChannelOutcome.Published _ | ChannelOutcome.History _) ->
                    do! fail state output context (SessionRegistryFailure.InvalidReply "channel outcome")
    }

    let private handle config globalId profiles output state (context: AgentContext<SessionRegistryMessage>) message = task {
        match context.Ref.TryReliable() with
        | None -> context.Abort()
        | Some address ->
            let replyTo = address.Map SessionRegistryMessage.ProfileReplied
            match message with
            | SessionRegistryMessage.BindChannel channel ->
                match state.Channel with
                | None -> state.Channel <- Some channel
                | Some _ -> do! fail state output context (SessionRegistryFailure.InvalidReply "channel already bound")
            | SessionRegistryMessage.Open request -> do! openSession config state replyTo output context request
            | SessionRegistryMessage.Disconnect connectionId -> disconnect state connectionId
            | SessionRegistryMessage.ProfileReplied reply -> do! profileReply state replyTo output context reply
            | SessionRegistryMessage.ChannelReplied reply -> do! channelReply globalId state output context reply
            | SessionRegistryMessage.AdmissionFinished result ->
                state.Sending <- false
                match result with
                | Ok AgentDeliveryResult.Posted -> ()
                | Ok (AgentDeliveryResult.Closed | AgentDeliveryResult.Canceled) ->
                    do! fail state output context SessionRegistryFailure.DependencyUnavailable
                | Error error -> do! fail state output context (SessionRegistryFailure.OperationFailed error)
            | SessionRegistryMessage.DependencyStopped reason -> do! fail state output context reason
            | SessionRegistryMessage.Stop ->
                state.Stopping <- true
                for session in state.Sessions.Values do
                    if session.Connected then
                        do! emit output context (SessionOutput.Close session.Request.ConnectionId)
                    disconnect state session.Request.ConnectionId

            if not context.CancellationToken.IsCancellationRequested then
                pump state profiles context
                if state.Stopping && not state.Sending && state.Pending.Count = 0 then
                    context.Complete() |> ignore
    }

    /// Bind one channel before admitting connections. The lifecycle owner retains both
    /// agents and forwards dependency failures; this registry does not own their lifetime.
    let start config globalId profiles output =
        if config.MailboxCapacity < 1 || config.MaxSessions < 1 then
            Error "Session mailbox capacity and MaxSessions must be positive."
        else
            let state = {
                Sessions = Dictionary()
                Names = Dictionary()
                Pending = Dictionary()
                Outbox = Queue()
                Sending = false
                Stopping = false
                Channel = None
            }
            let options = {
                AgentOptions.create "sessions" with
                    Mailbox = AgentMailbox.boundedWait config.MailboxCapacity
            }
            Ok (Agent.Start(options, handle config globalId profiles output state))
