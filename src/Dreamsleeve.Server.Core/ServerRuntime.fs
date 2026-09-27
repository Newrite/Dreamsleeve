namespace Dreamsleeve.Server.Core

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

[<RequireQualifiedAccess>]
type SessionSource = Chat | Presence | Authentication

type ServerRuntimeSnapshot = {
    Connections: int
    Ready: int
    Reservations: int
    Closing: int
    Stopping: bool
}

[<RequireQualifiedAccess>]
type ServerRuntimeMessage =
    | Start
    | Tick
    | Host of SessionHostCommand
    | PlayerStopped of Guid * Result<unit, exn>
    | SourceStopped of SessionSource * Result<unit, exn>
    | Detached of SessionSource * Guid
    | CleanupFailed of AgentSendFailure
    | Read of ReplyChannel<ServerRuntimeSnapshot>
    | FindPlayer of Guid * ReplyChannel<AgentRef<PlayerSessionMessage> option>
    | Stop

/// One transport owner. Packet dispatch does not wait for domain agents or route
/// chat through another shared mailbox. SessionTable contains lifecycle only.
[<RequireQualifiedAccess>]
module ServerRuntime =
    type private Sources = {
        Chat: Agent<ChatRoomCommand>
        Presence: Agent<PresenceCommand>
        ChatCleanup: AgentOutbox<ChatRoomCommand>
        PresenceCleanup: AgentOutbox<PresenceCommand>
        mutable ChatStopped: bool
        mutable PresenceStopped: bool
    }

    type private State = {
        Table: SessionTable.State
        Codec: ChatCodec
        MaxActorValues: int
        Transport: ServerTransport
        Logger: ILogger
        mutable Sources: Sources option
        mutable Stopping: bool
        mutable StopDeadline: int64
        mutable SourcesStopping: bool
    }

    let private now () = Environment.TickCount64

    let private fail state (context: AgentContext<ServerRuntimeMessage>) (reason: string) =
        state.Logger.LogError("Server runtime failed: {Reason}", reason)
        context.Abort()

    let private cleanup state (context: AgentContext<ServerRuntimeMessage>) (entry: SessionTable.Entry) =
        match state.Sources, context.Ref.TryReliable() with
        | Some sources, Some self ->
            let detach source = {
                ConnectionId = entry.ConnectionId
                ReplyTo = self.Map(fun id -> ServerRuntimeMessage.Detached(source, id))
            }
            // Called only after actual child Completion: no late child Join can
            // appear behind these commands in either source mailbox.
            let chat = sources.ChatCleanup.TrySend(context, ChatRoomCommand.Detach(detach SessionSource.Chat), ServerRuntimeMessage.CleanupFailed)
            let presence = sources.PresenceCleanup.TrySend(context, PresenceCommand.Detach(detach SessionSource.Presence), ServerRuntimeMessage.CleanupFailed)
            if not chat || not presence then fail state context "Session cleanup capacity exhausted."
        | None, _ | _, None -> fail state context "Session cleanup has no sources."

    let private close (options: ServerRuntimeOptions) state (context: AgentContext<ServerRuntimeMessage>) (entry: SessionTable.Entry) =
        if entry.Phase <> SessionTable.Closing then
            entry.Phase <- SessionTable.Closing
            let deadline = now () + int64 options.ShutdownTimeoutMs
            entry.Deadline <- if state.Stopping then min deadline state.StopDeadline else deadline
            if not entry.TransportClosed then state.Transport.Close entry.ConnectionId

            match entry.Child with
            | None ->
                entry.ChildStopped <- true
                entry.ChatDetached <- true
                entry.PresenceDetached <- true
                if SessionTable.clean entry then SessionTable.remove entry state.Table
            | Some child ->
                match child.TryPost PlayerSessionMessage.Stop with
                | AgentPostResult.Posted -> ()
                | AgentPostResult.Full | AgentPostResult.Closed | AgentPostResult.Canceled | AgentPostResult.Dropped -> child.Abort()

    let private send (options: ServerRuntimeOptions) state context (entry: SessionTable.Entry) response =
        match ChatCodec.encodeServer state.Codec response with
        | Error error ->
            state.Logger.LogError("Cannot encode response for {ConnectionId}: {Failure}", entry.ConnectionId, error.Failure)
            close options state context entry
        | Ok bytes ->
            match state.Transport.Send(entry.ConnectionId, bytes) with
            | Ok () -> ()
            | Error reason ->
                state.Logger.LogWarning("Closing {ConnectionId}: {Reason}", entry.ConnectionId, reason)
                close options state context entry

    let private reject (options: ServerRuntimeOptions) state context entry requestId code message =
        send options state context entry (ChatResponse.RequestRejected(requestId, { Code = code; Message = message; Field = "" }))

    let private host (options: ServerRuntimeOptions) state context command =
        match command with
        | SessionHostCommand.Reserve(connectionId, playerId, reply) ->
            let answer =
                match SessionTable.find connectionId state.Table with
                | Some entry when not state.Stopping -> SessionTable.reserve playerId entry state.Table
                | Some _ | None -> IdentityAdmission.Closed

            match reply.TryPost answer with
            | AgentTryDeliveryResult.Posted -> ()
            | AgentTryDeliveryResult.Full | AgentTryDeliveryResult.Closed ->
                SessionTable.find connectionId state.Table |> Option.iter (close options state context)

        | SessionHostCommand.Activate(connectionId, requestId, welcome) ->
            match SessionTable.find connectionId state.Table with
            | Some entry when entry.Phase = SessionTable.Opening && not state.Stopping ->
                if now () >= entry.Deadline || entry.PlayerId <> Some welcome.SelfPlayerId then
                    close options state context entry
                else
                    // Mark ready in the same handler that queues the welcome packet.
                    entry.Phase <- SessionTable.Ready
                    send options state context entry (ChatResponse.SessionOpened(requestId, welcome))
            | Some _ | None -> ()

        | SessionHostCommand.Send(connectionId, response) ->
            match SessionTable.find connectionId state.Table with
            | Some entry when entry.Phase = SessionTable.Ready -> send options state context entry response
            | Some entry when entry.Phase = SessionTable.Opening ->
                match response with
                | ChatResponse.RequestRejected _ -> send options state context entry response
                | ChatResponse.SessionOpened _ | ChatResponse.ChatAccepted _ | ChatResponse.ChatPublished _
                | ChatResponse.PlayerJoined _ | ChatResponse.PlayerUpdated _ | ChatResponse.PlayerMoved _ | ChatResponse.PlayerMetadataChanged _
                | ChatResponse.PlayerUpdateAccepted _ | ChatResponse.PlayerLeft _ -> ()
            | Some _ | None -> ()

        | SessionHostCommand.Close(connectionId, reason) ->
            state.Logger.LogInformation("Session {ConnectionId}: {Reason}", connectionId, reason)
            SessionTable.find connectionId state.Table |> Option.iter (close options state context)
        | SessionHostCommand.SlowConsumer connectionId ->
            state.Logger.LogWarning("Slow consumer {ConnectionId}", connectionId)
            SessionTable.find connectionId state.Table |> Option.iter (close options state context)

    let private openSession (options: ServerRuntimeOptions) maxActorValues globalId (authenticator: SessionAuthenticator) state (context: AgentContext<ServerRuntimeMessage>) (entry: SessionTable.Entry) requestId sessionTicket =
        match state.Sources, context.Ref.TryReliable() with
        | Some sources, Some self ->
            let request = { ConnectionId = entry.ConnectionId; RequestId = requestId; SessionTicket = sessionTicket }
            match PlayerSession.start options.Player maxActorValues globalId (authenticator.Requests)
                      (sources.Chat.Ref.TryReliable().Value) (sources.Presence.Ref.TryReliable().Value)
                      (self.Map ServerRuntimeMessage.Host) request with
            | Error reason -> fail state context reason
            | Ok child ->
                entry.Child <- Some child
                entry.Phase <- SessionTable.Opening
                context.Own(child, fun outcome -> ServerRuntimeMessage.PlayerStopped(entry.ConnectionId, outcome))
        | None, _ | _, None -> close options state context entry

    let private forward (options: ServerRuntimeOptions) state context (entry: SessionTable.Entry) requestId message =
        match entry.Child with
        | Some child ->
            match child.TryPost message with
            | AgentPostResult.Posted -> ()
            | AgentPostResult.Full -> reject options state context entry requestId RequestRejectionCode.Overloaded "Session input is full."
            | AgentPostResult.Closed | AgentPostResult.Canceled | AgentPostResult.Dropped -> close options state context entry
        | None -> close options state context entry

    let private receive (options: ServerRuntimeOptions) globalId authenticator state context entry bytes =
        match ChatCodec.decodeClient state.Codec bytes with
        | Error error ->
            match error.RequestId with
            | None -> close options state context entry
            | Some requestId -> reject options state context entry requestId RequestRejectionCode.InvalidRequest "Invalid request."
        | Ok request ->
            match request.Command, entry.Phase with
            | ChatCommand.OpenSession sessionTicket, SessionTable.Waiting ->
                openSession options state.MaxActorValues globalId authenticator state context entry request.RequestId sessionTicket
            | ChatCommand.OpenSession _, (SessionTable.Opening | SessionTable.Ready) ->
                reject options state context entry request.RequestId RequestRejectionCode.SessionAlreadyOpen "Session is already opening or open."
            | ChatCommand.SendChat(channelId, text), SessionTable.Ready ->
                forward options state context entry request.RequestId (PlayerSessionMessage.SendChat(request.RequestId, channelId, text))
            | ChatCommand.UpdatePlayer update, SessionTable.Ready ->
                forward options state context entry request.RequestId (PlayerSessionMessage.Update(request.RequestId, update))
            | (ChatCommand.SendChat _ | ChatCommand.UpdatePlayer _), (SessionTable.Waiting | SessionTable.Opening) ->
                reject options state context entry request.RequestId RequestRejectionCode.SessionNotReady "Session is not ready."
            | (ChatCommand.OpenSession _ | ChatCommand.SendChat _ | ChatCommand.UpdatePlayer _), SessionTable.Closing -> ()

    let private transportEvent (options: ServerRuntimeOptions) globalId authenticator state context event =
        match event with
        | ServerTransportEvent.Connected connectionId ->
            if state.Stopping || state.Table.Connections.Count >= options.MaxSessions then
                state.Transport.Close connectionId
            elif not (state.Table.Connections.ContainsKey connectionId) then
                SessionTable.add connectionId (now () + int64 options.OpenTimeoutMs) state.Table |> ignore
        | ServerTransportEvent.Received(connectionId, bytes) ->
            match SessionTable.find connectionId state.Table with
            | Some entry when entry.Phase <> SessionTable.Closing -> receive options globalId authenticator state context entry bytes
            | Some _ | None -> ()
        | ServerTransportEvent.Disconnected connectionId ->
            match SessionTable.find connectionId state.Table with
            | None -> ()
            | Some entry ->
                entry.TransportClosed <- true
                close options state context entry
                if SessionTable.clean entry then SessionTable.remove entry state.Table

    let private schedule (options: ServerRuntimeOptions) (context: AgentContext<ServerRuntimeMessage>) =
        let wait (token: CancellationToken) = task { do! Task.Delay(options.PollIntervalMs, token) }
        context.PipeToSelf(wait, fun _ -> ServerRuntimeMessage.Tick)

    let private initialize (options: ServerRuntimeOptions) globalId authenticator state (context: AgentContext<ServerRuntimeMessage>) =
        match context.Ref.TryReliable() with
        | None -> fail state context "Runtime requires a reliable mailbox."
        | Some self ->
            let output = self.Map ServerRuntimeMessage.Host
            match ChatRoomAgent.start options.Chat globalId output with
            | Error error -> fail state context $"Chat startup failed: {error}"
            | Ok chat ->
                context.Own(chat, fun outcome -> ServerRuntimeMessage.SourceStopped(SessionSource.Chat, outcome))
                match PresenceAgent.start options.Presence output with
                | Error error -> fail state context $"Presence startup failed: {error}"
                | Ok presence ->
                    context.Own(presence, fun outcome -> ServerRuntimeMessage.SourceStopped(SessionSource.Presence, outcome))
                    context.Watch(authenticator.Completion, fun outcome -> ServerRuntimeMessage.SourceStopped(SessionSource.Authentication, outcome))
                    state.Sources <- Some {
                        Chat = chat; Presence = presence
                        ChatCleanup = AgentOutbox(options.MaxSessions, chat.Ref.TryReliable().Value)
                        PresenceCleanup = AgentOutbox(options.MaxSessions, presence.Ref.TryReliable().Value)
                        ChatStopped = false; PresenceStopped = false
                    }
                    schedule options context

    let private stopped (options: ServerRuntimeOptions) state context connectionId (outcome: Result<unit, exn>) =
        match SessionTable.find connectionId state.Table with
        | None -> ()
        | Some entry when entry.ChildStopped -> ()
        | Some entry ->
            match outcome with
            | Error error when entry.Phase <> SessionTable.Closing -> state.Logger.LogError(error, "Player session {ConnectionId} terminated", connectionId)
            | Error _ | Ok () -> ()

            close options state context entry
            entry.ChildStopped <- true
            cleanup state context entry

    let private detached state source connectionId =
        match SessionTable.find connectionId state.Table with
        | None -> ()
        | Some entry ->
            match source with
            | SessionSource.Chat -> entry.ChatDetached <- true
            | SessionSource.Presence -> entry.PresenceDetached <- true
            | SessionSource.Authentication -> ()

            if SessionTable.clean entry then
                SessionTable.remove entry state.Table

    let private stop (options: ServerRuntimeOptions) state context =
        if not state.Stopping then
            state.Stopping <- true
            state.StopDeadline <- now () + int64 options.ShutdownTimeoutMs
            // Closing changes routes now; transport drain and domain cleanup finish independently.
            for entry in state.Table.Connections.Values |> Seq.toArray do
                close options state context entry

    let private tick (options: ServerRuntimeOptions) globalId authenticator state context =
        match state.Transport.Poll() with
        | Error reason -> fail state context $"Transport failed: {reason}"
        | Ok events ->
            for event in events do
                transportEvent options globalId authenticator state context event

            let time = now ()
            for entry in state.Table.Connections.Values |> Seq.toArray do
                match entry.Phase with
                | SessionTable.Waiting | SessionTable.Opening when time >= entry.Deadline -> close options state context entry
                | SessionTable.Closing when time >= entry.Deadline ->
                    if SessionTable.domainClean entry then
                        // A non-acknowledging peer cannot keep the route forever.
                        // Domain cleanup is already confirmed; reset only its transport.
                        state.Transport.Reset entry.ConnectionId
                        entry.TransportClosed <- true
                        SessionTable.remove entry state.Table
                    else
                        // A stuck source still requires containment of its lifetime.
                        fail state context $"Session cleanup timed out: {entry.ConnectionId}"
                | SessionTable.Waiting | SessionTable.Opening | SessionTable.Ready | SessionTable.Closing -> ()

            let sourcesStopped =
                state.Sources |> Option.forall (fun sources -> sources.ChatStopped && sources.PresenceStopped)
            if state.Stopping && time >= state.StopDeadline && (state.Table.Connections.Count > 0 || not sourcesStopped) then
                fail state context "Server shutdown timed out."
            else
                schedule options context

    let private finish state (context: AgentContext<ServerRuntimeMessage>) =
        if state.Stopping && (state.Table.Connections.Values |> Seq.forall SessionTable.domainClean) then
            match state.Sources with
            | None -> context.Complete() |> ignore
            | Some sources ->
                if not state.SourcesStopping then
                    state.SourcesStopping <- true
                    sources.Chat.Complete() |> ignore
                    sources.Presence.Complete() |> ignore

                if sources.ChatStopped && sources.PresenceStopped && state.Table.Connections.Count = 0 then
                    context.Complete() |> ignore

    let private sourceStopped state context source (outcome: Result<unit, exn>) =
        if not state.SourcesStopping then
            fail state context $"Source {source} terminated: {outcome}"
        else
            match outcome with
            | Error error -> fail state context $"Source {source} failed during shutdown: {error.Message}"
            | Ok () ->
                match state.Sources, source with
                | Some sources, SessionSource.Chat -> sources.ChatStopped <- true
                | Some sources, SessionSource.Presence -> sources.PresenceStopped <- true
                | Some _, SessionSource.Authentication | None, _ -> ()

    let private handle (options: ServerRuntimeOptions) globalId authenticator state (context: AgentContext<ServerRuntimeMessage>) message = task {
        match message with
        | ServerRuntimeMessage.Start ->
            if state.Sources.IsNone && not state.Stopping then initialize options globalId authenticator state context
        | ServerRuntimeMessage.Tick -> tick options globalId authenticator state context
        | ServerRuntimeMessage.Host command -> host options state context command
        | ServerRuntimeMessage.PlayerStopped(connectionId, outcome) -> stopped options state context connectionId outcome
        | ServerRuntimeMessage.SourceStopped(source, outcome) -> sourceStopped state context source outcome
        | ServerRuntimeMessage.Detached(source, connectionId) -> detached state source connectionId
        | ServerRuntimeMessage.CleanupFailed failure -> fail state context $"Session cleanup delivery failed: {failure}"
        | ServerRuntimeMessage.Stop -> stop options state context
        | ServerRuntimeMessage.Read reply ->
            reply.Reply {
                Connections = state.Table.Connections.Count
                Ready = state.Table.Connections.Values |> Seq.filter (fun entry -> entry.Phase = SessionTable.Ready) |> Seq.length
                Reservations = state.Table.Players.Count
                Closing = state.Table.Connections.Values |> Seq.filter (fun entry -> entry.Phase = SessionTable.Closing) |> Seq.length
                Stopping = state.Stopping
            }
        | ServerRuntimeMessage.FindPlayer(connectionId, reply) ->
            let player =
                SessionTable.find connectionId state.Table
                |> Option.bind (fun entry -> if entry.Phase = SessionTable.Ready then entry.Child |> Option.map _.Ref else None)
            reply.Reply player

        finish state context
    }

    let private isControl = function
        | ServerRuntimeMessage.Host(SessionHostCommand.Send _) | ServerRuntimeMessage.Read _
        | ServerRuntimeMessage.FindPlayer _ -> false
        | ServerRuntimeMessage.Start | ServerRuntimeMessage.Tick | ServerRuntimeMessage.Host _
        | ServerRuntimeMessage.PlayerStopped _ | ServerRuntimeMessage.SourceStopped _
        | ServerRuntimeMessage.Detached _ | ServerRuntimeMessage.CleanupFailed _ | ServerRuntimeMessage.Stop -> true

    /// The caller owns authentication separately and disposes the
    /// transport AFTER this agent's Completion, including Abort/fault paths.
    let start (options: ServerRuntimeOptions) config (authenticator: SessionAuthenticator) transport (logger: ILogger) =
        let limits = [ options.MaxSessions; options.MailboxCapacity; options.ControlReserve; options.OpenTimeoutMs
                       options.ShutdownTimeoutMs; options.PollIntervalMs; options.Player.MailboxCapacity
                       options.Player.ControlReserve; options.Player.MaxPendingChat; options.Player.MaxPendingUpdates; options.Player.MaxBootstrapEvents
                       options.Player.MaxPendingOutput; options.Chat.MailboxCapacity; options.Chat.ControlReserve
                       options.Chat.HistoryCapacity; options.Chat.MaxControlDeliveries; options.Presence.MailboxCapacity
                       options.Presence.ControlReserve; options.Presence.MaxControlDeliveries; options.Presence.ReplicationIntervalMs ]
        let errors = [
            if not (System.Single.IsFinite options.Presence.VisibilityDistance) || options.Presence.VisibilityDistance < 0.0f then
                "Presence.VisibilityDistance must be finite and non-negative."
            if limits |> List.exists (fun value -> value < 1) then "Runtime capacities and deadlines must be positive."
            if int64 options.ControlReserve < 3L * int64 options.MaxSessions + 4L then
                "Runtime ControlReserve must allow 3 * MaxSessions + 4 lifecycle messages."
            if options.MaxSessions > config.PeerLimit || options.MaxSessions > config.MaxInitialPlayers then
                "Runtime MaxSessions must fit transport PeerLimit and protocol MaxInitialPlayers."
            if options.Chat.HistoryCapacity > config.MaxRecentMessages then
                "Chat history must fit MaxRecentMessages."
            if int64 config.ServiceTimeoutMs + int64 options.PollIntervalMs > int64 (min options.OpenTimeoutMs options.ShutdownTimeoutMs) then
                "Transport service wait plus poll interval must fit runtime deadlines."
            if options.Player.MaxPendingChat > Int32.MaxValue - 2 || options.Player.MaxPendingUpdates > Int32.MaxValue - 2
               || options.Player.MaxPendingOutput > Int32.MaxValue - 2 then
                "Session pending capacity plus cleanup reserve overflows."
            for ordinary, reserve in [options.MailboxCapacity, options.ControlReserve; options.Player.MailboxCapacity, options.Player.ControlReserve
                                      options.Chat.MailboxCapacity, options.Chat.ControlReserve; options.Presence.MailboxCapacity, options.Presence.ControlReserve] do
                if int64 ordinary + int64 reserve > int64 Int32.MaxValue then "Mailbox capacity and control reserve overflow."
            match ServerConfig.validate config with Ok _ -> () | Error errors -> yield! errors
        ]
        match errors, ChatCodec.create config, ChatChannelId.create 1UL with
        | [], Ok codec, Ok globalId ->
            let state = {
                Table = SessionTable.create(); Codec = codec; MaxActorValues = config.PlayerInput.MaxActorValues
                Transport = transport; Logger = logger
                Sources = None; Stopping = false; SourcesStopping = false; StopDeadline = 0L
            }
            let agentOptions = { AgentOptions.create "server-runtime" with Mailbox = AgentMailbox.boundedWithControl options.MailboxCapacity options.ControlReserve }
            let agent = Agent.Start(agentOptions, handle options globalId authenticator state, isControl = isControl)
            agent.TryPost ServerRuntimeMessage.Start |> ignore
            Ok agent
        | errors, _, _ -> Error (if errors.IsEmpty then ["Cannot create runtime codec or global channel."] else errors)
