namespace Dreamsleeve.Server.Core

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

[<RequireQualifiedAccess>]
type SessionSource = Chat | System | Presence | Authentication

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
    | Tick of AgentTick
    | TransportReady
    | Host of SessionHostCommand
    | PlayerStopped of Guid * Result<unit, exn>
    | SourceStopped of SessionSource * Result<unit, exn>
    | Detached of SessionSource * Guid
    | CleanupFailed of AgentSendFailure
    | Read of ReplyChannel<ServerRuntimeSnapshot>
    | FindPlayer of Guid * ReplyChannel<AgentRef<PlayerSessionMessage> option>
    | RevokePlayer of PlayerId
    /// One-off server announcement from the administrator console.
    | Announce of ServerAnnouncement
    | Stop

/// Routes managed transport events without waiting for domain agents.
/// Native ENet servicing belongs to the transport worker.
[<RequireQualifiedAccess>]
module ServerRuntime =
    type private Sources = {
        Chat: Agent<ChatRoomCommand>
        /// Owner of the system channel: announcements.
        System: Agent<ChatRoomCommand>
        Presence: Agent<PresenceCommand>
        ChatCleanup: AgentOutbox<ChatRoomCommand>
        SystemCleanup: AgentOutbox<ChatRoomCommand>
        PresenceCleanup: AgentOutbox<PresenceCommand>
        mutable ChatStopped: bool
        mutable SystemStopped: bool
        mutable PresenceStopped: bool
    }

    type private State = {
        Table: SessionTable.State
        mutable RouteScratch: SessionTable.Entry array
        Codec: ProtocolCodec
        MaxActorValues: int
        Moderation: ModerationRules
        Announcements: AnnouncementOptions
        Schedule: AnnouncementSchedule
        Transport: ServerTransport
        Logger: ILogger
        mutable Sources: Sources option
        mutable Stopping: bool
        mutable StopDeadline: int64
        mutable SourcesStopping: bool
        mutable Ticker: AgentTicker option
        mutable LastTick: int64
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
            let system = sources.SystemCleanup.TrySend(context, ChatRoomCommand.Detach(detach SessionSource.System), ServerRuntimeMessage.CleanupFailed)
            let presence = sources.PresenceCleanup.TrySend(context, PresenceCommand.Detach(detach SessionSource.Presence), ServerRuntimeMessage.CleanupFailed)
            if not chat || not system || not presence then fail state context "Session cleanup capacity exhausted."
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
                entry.SystemDetached <- true
                entry.PresenceDetached <- true
                if SessionTable.clean entry then SessionTable.remove entry state.Table
            | Some child ->
                match child.TryPost PlayerSessionMessage.Stop with
                | AgentPostResult.Posted -> ()
                | AgentPostResult.Full | AgentPostResult.Closed | AgentPostResult.Canceled | AgentPostResult.Dropped -> child.Abort()

    let private transmit (options: ServerRuntimeOptions) state context (entry: SessionTable.Entry) lane encoded =
        match encoded with
        | Error error ->
            state.Logger.LogError("Cannot encode response for {ConnectionId}: {Failure}", entry.ConnectionId, error.Failure)
            close options state context entry
        | Ok packets ->
            for bytes in packets do
                if entry.Phase <> SessionTable.Closing then
                    match state.Transport.Send(entry.ConnectionId, { Lane = lane; Bytes = bytes }) with
                    | Ok () -> ()
                    | Error _ when lane = DeliveryLane.Realtime -> () // Next period repairs a dropped pose.
                    | Error reason ->
                        state.Logger.LogWarning("Closing {ConnectionId}: {Reason}", entry.ConnectionId, reason)
                        close options state context entry

    let private send options state context (entry: SessionTable.Entry) response =
        let encoded =
            match response with
            | ServerResponse.PlayersMoved movements ->
                let budget = state.Transport.MaxUnfragmentedPayloadBytes entry.ConnectionId
                ProtocolCodec.encodeMovementPackets state.Codec budget movements
            | ServerResponse.SessionOpened _ | ServerResponse.ChatAccepted _ | ServerResponse.ChatPublished _
            | ServerResponse.ChatRejected _ | ServerResponse.RequestRejected _ | ServerResponse.PlayerJoined _
            | ServerResponse.PlayerUpdated _ | ServerResponse.PlayerMetadataChanged _ | ServerResponse.PlayerVisibilityChanged _
            | ServerResponse.PlayerUpdateAccepted _ | ServerResponse.PlayerLeft _ ->
                ProtocolCodec.encodeServer state.Codec response |> Result.map List.singleton
        transmit options state context entry (ProtocolCodec.responseLane response) encoded

    let private reject options state context entry lane requestId code message =
        let rejection = { Code = code; Message = message; Field = "" }
        let response =
            if lane = DeliveryLane.Chat then ServerResponse.ChatRejected(requestId, rejection)
            else ServerResponse.RequestRejected(requestId, rejection)
        send options state context entry response

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
                    send options state context entry (ServerResponse.SessionOpened(requestId, welcome))
            | Some _ | None -> ()

        | SessionHostCommand.Send(connectionId, response) ->
            match SessionTable.find connectionId state.Table with
            | Some entry when entry.Phase = SessionTable.Ready -> send options state context entry response
            | Some entry when entry.Phase = SessionTable.Opening ->
                match response with
                | ServerResponse.RequestRejected _ -> send options state context entry response
                | ServerResponse.SessionOpened _ | ServerResponse.ChatAccepted _ | ServerResponse.ChatPublished _
                | ServerResponse.PlayerJoined _ | ServerResponse.PlayerUpdated _ | ServerResponse.PlayersMoved _ | ServerResponse.PlayerMetadataChanged _
                | ServerResponse.PlayerUpdateAccepted _ | ServerResponse.PlayerLeft _
                | ServerResponse.ChatRejected _ | ServerResponse.PlayerVisibilityChanged _ -> ()
            | Some _ | None -> ()

        | SessionHostCommand.Close(connectionId, reason) ->
            state.Logger.LogInformation("Session {ConnectionId}: {Reason}", connectionId, reason)
            SessionTable.find connectionId state.Table |> Option.iter (close options state context)
        | SessionHostCommand.SlowConsumer connectionId ->
            state.Logger.LogWarning("Slow consumer {ConnectionId}", connectionId)
            SessionTable.find connectionId state.Table |> Option.iter (close options state context)

    let private openSession (options: ServerRuntimeOptions) maxActorValues (authenticator: SessionAuthenticator) state (context: AgentContext<ServerRuntimeMessage>) (entry: SessionTable.Entry) requestId sessionTicket =
        match state.Sources, context.Ref.TryReliable() with
        | Some sources, Some self ->
            let request = { ConnectionId = entry.ConnectionId; RequestId = requestId; SessionTicket = sessionTicket }
            match PlayerSession.start options.Player maxActorValues state.Moderation state.Announcements (authenticator.Requests)
                      (sources.Chat.Ref.TryReliable().Value) (sources.System.Ref.TryReliable().Value) (sources.Presence.Ref.TryReliable().Value)
                      (self.Map ServerRuntimeMessage.Host) request with
            | Error reason -> fail state context reason
            | Ok child ->
                entry.Child <- Some child
                entry.Phase <- SessionTable.Opening
                context.Own(child, fun outcome -> ServerRuntimeMessage.PlayerStopped(entry.ConnectionId, outcome))
        | None, _ | _, None -> close options state context entry

    let private forward (options: ServerRuntimeOptions) state context (entry: SessionTable.Entry) lane requestId message =
        match entry.Child with
        | Some child ->
            match child.TryPost message with
            | AgentPostResult.Posted -> ()
            | AgentPostResult.Full -> reject options state context entry lane requestId RequestRejectionCode.Overloaded "Session input is full."
            | AgentPostResult.Closed | AgentPostResult.Canceled | AgentPostResult.Dropped -> close options state context entry
        | None -> close options state context entry

    let private receiveSample state (entry: SessionTable.Entry) bytes =
        if entry.Phase = SessionTable.Ready then
            match ProtocolCodec.decodeMovement state.Codec bytes, entry.Child with
            | Ok sample, Some child ->
                // A dropped sample is repaired by the next periodic absolute pose.
                child.TryPost(PlayerSessionMessage.SampleMovement sample) |> ignore
            | Error _, _ | _, None -> ()

    let private receive (options: ServerRuntimeOptions) authenticator state context entry lane bytes =
        if lane = DeliveryLane.Realtime then receiveSample state entry bytes
        else
            match ProtocolCodec.decodeClient state.Codec bytes with
            | Error error ->
                match error.RequestId, error.Failure with
                | None, _ -> close options state context entry
                | Some requestId, ProtocolCodecFailure.InvalidDomain(DomainError.InvalidText("ChatMessageText", TextError.TooLong maximum)) ->
                    let rejection = { Code = RequestRejectionCode.InvalidRequest; Message = $"Message exceeds {maximum} characters."; Field = "text" }
                    send options state context entry (ServerResponse.ChatRejected(requestId, rejection))
                | Some requestId, ProtocolCodecFailure.InvalidDomain(DomainError.InvalidText("AnnouncementText", TextError.TooLong maximum)) ->
                    let rejection = { Code = RequestRejectionCode.InvalidRequest; Message = $"Announcement exceeds {maximum} characters."; Field = "text" }
                    send options state context entry (ServerResponse.ChatRejected(requestId, rejection))
                | Some requestId, ProtocolCodecFailure.InvalidDomain(DomainError.InvalidText("AnnouncementSignature", _)) ->
                    let rejection = { Code = RequestRejectionCode.InvalidRequest; Message = "Announcement source is missing, too long or contains control characters."; Field = "source" }
                    send options state context entry (ServerResponse.ChatRejected(requestId, rejection))
                | Some requestId, _ -> reject options state context entry lane requestId RequestRejectionCode.InvalidRequest "Invalid request."
            | Ok request when ProtocolCodec.requestLane request <> lane -> close options state context entry
            | Ok request ->
                match request.Command, entry.Phase with
                | ClientCommand.OpenSession sessionTicket, SessionTable.Waiting ->
                    openSession options state.MaxActorValues authenticator state context entry request.RequestId sessionTicket
                | ClientCommand.OpenSession _, (SessionTable.Opening | SessionTable.Ready) ->
                    reject options state context entry lane request.RequestId RequestRejectionCode.SessionAlreadyOpen "Session is already opening or open."
                | ClientCommand.SendChat(channelId, text), SessionTable.Ready ->
                    forward options state context entry lane request.RequestId (PlayerSessionMessage.SendChat(request.RequestId, channelId, text))
                | ClientCommand.UpdatePlayer update, SessionTable.Ready ->
                    forward options state context entry lane request.RequestId (PlayerSessionMessage.Update(request.RequestId, update))
                | ClientCommand.PostAnnouncement announcement, SessionTable.Ready ->
                    forward options state context entry lane request.RequestId (PlayerSessionMessage.PostAnnouncement(request.RequestId, announcement))
                | (ClientCommand.SendChat _ | ClientCommand.UpdatePlayer _ | ClientCommand.PostAnnouncement _), (SessionTable.Waiting | SessionTable.Opening) ->
                    reject options state context entry lane request.RequestId RequestRejectionCode.SessionNotReady "Session is not ready."
                | (ClientCommand.OpenSession _ | ClientCommand.SendChat _ | ClientCommand.UpdatePlayer _ | ClientCommand.PostAnnouncement _), SessionTable.Closing -> ()

    let private disconnected options state context connectionId =
        match SessionTable.find connectionId state.Table with
        | None -> ()
        | Some entry ->
            if entry.Phase <> SessionTable.Closing then
                state.Logger.LogInformation("Transport disconnected {ConnectionId} during {Phase}", connectionId, entry.Phase)

            entry.TransportClosed <- true
            close options state context entry
            if SessionTable.clean entry then SessionTable.remove entry state.Table

    let private transportEvent (options: ServerRuntimeOptions) authenticator state context event =
        match event with
        | ServerTransportEvent.Connected connectionId ->
            if state.Stopping || state.Table.Connections.Count >= options.MaxSessions then
                state.Transport.Close connectionId
            elif not (state.Table.Connections.ContainsKey connectionId) then
                SessionTable.add connectionId (now () + int64 options.OpenTimeoutMs) state.Table |> ignore
        | ServerTransportEvent.Received(connectionId, lane, bytes) ->
            match SessionTable.find connectionId state.Table with
            | Some entry when entry.Phase <> SessionTable.Closing -> receive options authenticator state context entry lane bytes
            | Some _ | None -> ()
        | ServerTransportEvent.Failed(connectionId, reason) ->
            state.Logger.LogWarning("Transport failed for {ConnectionId}: {Reason}", connectionId, reason)
            disconnected options state context connectionId
        | ServerTransportEvent.Disconnected connectionId -> disconnected options state context connectionId

    let private schedule (options: ServerRuntimeOptions) state context =
        if state.Ticker.IsNone then
            state.Ticker <- Some (AgentTicker.start (TimeSpan.FromMilliseconds(int64 options.PollIntervalMs)) context ServerRuntimeMessage.Tick)

    let private initialize (options: ServerRuntimeOptions) authenticator state (context: AgentContext<ServerRuntimeMessage>) =
        match context.Ref.TryReliable() with
        | None -> fail state context "Runtime requires a reliable mailbox."
        | Some self ->
            let output = self.Map ServerRuntimeMessage.Host
            let system = ChatRoomAgent.start (AnnouncementOptions.channelOptions options.Chat state.Announcements) ChatChannelKind.System output
            match ChatRoomAgent.start options.Chat ChatChannelKind.Global output, system with
            | Error error, _ | _, Error error -> fail state context $"Chat startup failed: {error}"
            | Ok chat, Ok system ->
                context.Own(chat, fun outcome -> ServerRuntimeMessage.SourceStopped(SessionSource.Chat, outcome))
                context.Own(system, fun outcome -> ServerRuntimeMessage.SourceStopped(SessionSource.System, outcome))
                match PresenceAgent.start options.Presence output with
                | Error error -> fail state context $"Presence startup failed: {error}"
                | Ok presence ->
                    context.Own(presence, fun outcome -> ServerRuntimeMessage.SourceStopped(SessionSource.Presence, outcome))
                    context.Watch(authenticator.Completion, fun outcome -> ServerRuntimeMessage.SourceStopped(SessionSource.Authentication, outcome))
                    state.Sources <- Some {
                        Chat = chat; System = system; Presence = presence
                        ChatCleanup = AgentOutbox(options.MaxSessions, chat.Ref.TryReliable().Value)
                        SystemCleanup = AgentOutbox(options.MaxSessions, system.Ref.TryReliable().Value)
                        PresenceCleanup = AgentOutbox(options.MaxSessions, presence.Ref.TryReliable().Value)
                        ChatStopped = false; SystemStopped = false; PresenceStopped = false
                    }
                    state.Transport.SetReadyHandler(fun () ->
                        context.Ref.TryPost ServerRuntimeMessage.TransportReady = AgentPostResult.Posted)
                    schedule options state context

    let private stopped (options: ServerRuntimeOptions) state context connectionId (outcome: Result<unit, exn>) =
        match SessionTable.find connectionId state.Table with
        | None -> ()
        | Some entry when entry.ChildStopped -> ()
        | Some entry ->
            match outcome with
            | Error error when entry.Phase <> SessionTable.Closing -> state.Logger.LogError(error, "Player session {ConnectionId} terminated", connectionId)
            | Ok () when entry.Phase <> SessionTable.Closing ->
                state.Logger.LogWarning("Player session {ConnectionId} completed unexpectedly during {Phase}", connectionId, entry.Phase)
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
            | SessionSource.System -> entry.SystemDetached <- true
            | SessionSource.Presence -> entry.PresenceDetached <- true
            | SessionSource.Authentication -> ()

            if SessionTable.clean entry then
                SessionTable.remove entry state.Table

    // Callbacks may remove routes. Reuse the detached iteration buffer and release
    // references afterwards; never keep a live Dictionary enumerator across close/reset.
    let private visitRoutes state visit =
        let count = state.Table.Connections.Count
        if state.RouteScratch.Length < count then
            state.RouteScratch <- Array.zeroCreate (max count (state.RouteScratch.Length * 2))
        state.Table.Connections.Values.CopyTo(state.RouteScratch, 0)

        try
            for index in 0 .. count - 1 do visit state.RouteScratch[index]
        finally
            Array.Clear(state.RouteScratch, 0, count)

    let private tickRoute (options: ServerRuntimeOptions) time state context (entry: SessionTable.Entry) =
        match entry.Phase with
        | SessionTable.Waiting | SessionTable.Opening when time >= entry.Deadline -> close options state context entry
        | SessionTable.Closing when time >= entry.Deadline ->
            if SessionTable.domainClean entry then
                state.Transport.Reset entry.ConnectionId
                entry.TransportClosed <- true
                SessionTable.remove entry state.Table
            else
                fail state context $"Session cleanup timed out: {entry.ConnectionId}"
        | SessionTable.Ready | SessionTable.Waiting | SessionTable.Opening | SessionTable.Closing -> ()

    let private stop (options: ServerRuntimeOptions) state context =
        if not state.Stopping then
            state.Stopping <- true
            state.StopDeadline <- now () + int64 options.ShutdownTimeoutMs
            // Closing changes routes now; transport drain and domain cleanup finish independently.
            visitRoutes state (close options state context)

    let private drain (options: ServerRuntimeOptions) authenticator state context =
        match state.Transport.Poll() with
        | Error reason -> fail state context $"Transport failed: {reason}"
        | Ok events ->
            for event in events do
                transportEvent options authenticator state context event

    // Announcements are data for the system channel owner, not control: a full
    // mailbox drops one of them with a warning instead of stopping the runtime.
    let private announce state (announcement: ServerAnnouncement) =
        match state.Sources with
        | Some sources when not state.Stopping ->
            match sources.System.TryPost(ChatRoomCommand.Announce announcement) with
            | AgentPostResult.Posted ->
                state.Logger.LogInformation("Server announcement ({Kind}): {Text}", announcement.Kind, ChatMessageText.value announcement.Text)
            | AgentPostResult.Full | AgentPostResult.Closed | AgentPostResult.Canceled | AgentPostResult.Dropped ->
                state.Logger.LogWarning("Server announcement dropped: system channel admission is unavailable")
        | Some _ | None -> ()

    let private tick (options: ServerRuntimeOptions) authenticator state context =
        // Fallback polling also supports non-notifying test transports.
        drain options authenticator state context

        let time = now ()
        for announcement in AnnouncementSchedule.due time state.Schedule do announce state announcement
        visitRoutes state (tickRoute options time state context)

        let sourcesStopped =
            state.Sources |> Option.forall (fun sources -> sources.ChatStopped && sources.SystemStopped && sources.PresenceStopped)
        if state.Stopping && time >= state.StopDeadline && (state.Table.Connections.Count > 0 || not sourcesStopped) then
            fail state context "Server shutdown timed out."

    let private finish state (context: AgentContext<ServerRuntimeMessage>) =
        if state.Stopping && (state.Table.Connections.Values |> Seq.forall SessionTable.domainClean) then
            match state.Sources with
            | None -> context.Complete() |> ignore
            | Some sources ->
                if not state.SourcesStopping then
                    state.SourcesStopping <- true
                    sources.Chat.Complete() |> ignore
                    sources.System.Complete() |> ignore
                    sources.Presence.Complete() |> ignore

                if sources.ChatStopped && sources.SystemStopped && sources.PresenceStopped && state.Table.Connections.Count = 0 then
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
                | Some sources, SessionSource.System -> sources.SystemStopped <- true
                | Some sources, SessionSource.Presence -> sources.PresenceStopped <- true
                | Some _, SessionSource.Authentication | None, _ -> ()

    let private handle (options: ServerRuntimeOptions) authenticator state (context: AgentContext<ServerRuntimeMessage>) message = task {
        match message with
        | ServerRuntimeMessage.Start ->
            if state.Sources.IsNone && not state.Stopping then initialize options authenticator state context
        | ServerRuntimeMessage.TransportReady -> drain options authenticator state context
        | ServerRuntimeMessage.Tick notification ->
            let started = Stopwatch.GetTimestamp()
            if state.LastTick <> 0L then RuntimeMetrics.runtimeInterval.Record(Stopwatch.GetElapsedTime(state.LastTick, started).TotalMilliseconds)
            state.LastTick <- started
            RuntimeMetrics.runtimeTimerLateness.Record(Stopwatch.GetElapsedTime(notification.DueTimestamp, notification.QueuedTimestamp).TotalMilliseconds)
            RuntimeMetrics.runtimeQueueDelay.Record(Stopwatch.GetElapsedTime(notification.QueuedTimestamp).TotalMilliseconds)
            tick options authenticator state context
            RuntimeMetrics.runtimeTick.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds)
            state.Ticker |> Option.iter _.Acknowledge()
        | ServerRuntimeMessage.Host command -> host options state context command
        | ServerRuntimeMessage.PlayerStopped(connectionId, outcome) -> stopped options state context connectionId outcome
        | ServerRuntimeMessage.SourceStopped(source, outcome) -> sourceStopped state context source outcome
        | ServerRuntimeMessage.Detached(source, connectionId) -> detached state source connectionId
        | ServerRuntimeMessage.CleanupFailed failure -> fail state context $"Session cleanup delivery failed: {failure}"
        | ServerRuntimeMessage.RevokePlayer playerId ->
            // Close pending authentication too: a consumed ticket's reply may still be in flight.
            let affected = state.Table.Connections.Values |> Seq.filter (fun entry -> entry.PlayerId = Some playerId || entry.Phase = SessionTable.Opening) |> Seq.toArray
            for entry in affected do close options state context entry
        | ServerRuntimeMessage.Announce announcement -> announce state announcement
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
        | ServerRuntimeMessage.FindPlayer _ | ServerRuntimeMessage.TransportReady | ServerRuntimeMessage.Announce _ -> false
        | ServerRuntimeMessage.Start | ServerRuntimeMessage.Tick _ | ServerRuntimeMessage.Host _
        | ServerRuntimeMessage.PlayerStopped _ | ServerRuntimeMessage.SourceStopped _
        | ServerRuntimeMessage.Detached _ | ServerRuntimeMessage.CleanupFailed _ | ServerRuntimeMessage.RevokePlayer _ | ServerRuntimeMessage.Stop -> true

    /// The caller owns authentication separately and disposes the
    /// transport AFTER this agent's Completion, including Abort/fault paths.
    /// Moderation rules and announcements are fixed for the runtime lifetime, like the rest of config.
    let start (options: ServerRuntimeOptions) config (moderation: ModerationRules) (announcements: AnnouncementOptions) (authenticator: SessionAuthenticator) transport (logger: ILogger) =
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
            if options.Chat.HistoryCapacity > config.MaxRecentMessages || announcements.HistoryCapacity > config.MaxRecentMessages then
                "Chat and announcement histories must fit MaxRecentMessages."
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
        let scheduled = if errors.IsEmpty then AnnouncementOptions.resolve config.ChatInput announcements else Error errors
        match scheduled, ProtocolCodec.create config with
        | Ok entries, Ok codec ->
            let state = {
                Table = SessionTable.create(); RouteScratch = Array.empty; Codec = codec; MaxActorValues = config.PlayerInput.MaxActorValues
                Moderation = moderation
                Announcements = announcements
                Schedule = AnnouncementSchedule.create (now ()) entries
                Transport = transport; Logger = logger
                Sources = None; Stopping = false; SourcesStopping = false; Ticker = None; LastTick = 0L; StopDeadline = 0L
            }
            let agentOptions = { AgentOptions.create "server-runtime" with Mailbox = AgentMailbox.boundedWithControl options.MailboxCapacity options.ControlReserve }
            let agent = Agent.Start(agentOptions, handle options authenticator state, isControl = isControl)
            agent.TryPost ServerRuntimeMessage.Start |> ignore
            Ok agent
        | Error errors, _ -> Error errors
        | Ok _, Error _ -> Error ["Cannot create runtime codec."]
