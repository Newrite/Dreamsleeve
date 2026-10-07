namespace Dreamsleeve.Server.Core

open System
open System.Diagnostics
open System.Net
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

[<RequireQualifiedAccess>]
type SessionSource = Chat | System | Presence | GroundMarks | Guilds | GuildStorage | Authentication

type ServerRuntimeSnapshot = {
    Connections: int
    /// Connected without signing in; counted in Connections.
    Guests: int
    Ready: int
    Reservations: int
    Closing: int
    Stopping: bool
}

/// One connection for the panel's online table. Session is the address to ask
/// for details (Describe); the row itself carries no names.
type RuntimeSessionRow = {
    ConnectionId: Guid
    /// For the panel only.
    Address: IPAddress
    /// The proxy of the server the player connects through.
    Proxy: IPAddress option
    PlayerId: PlayerId option
    Phase: RuntimeSessionPhase
    ConnectedAt: DateTimeOffset
    Session: AgentRef<PlayerSessionMessage> option
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
    /// From the account service: revocations, sanctions and kicks live sessions follow.
    | AccountChanged of AccountChange
    /// One-off server announcement from the administrator console or panel.
    | Announce of ServerAnnouncement
    /// Every connection with its phase, for the panel.
    | ListSessions of ReplyChannel<RuntimeSessionRow list>
    /// The role is already stored; the live session applies it without reconnecting.
    | SetPlayerRole of PlayerId * PlayerRole
    /// The stored profile after an administrator renamed the player.
    | RenamePlayer of PlayerData
    /// The panel's request to the guild owner; unanswered while the game is restarting.
    | Guilds of GuildAdminCommand * ReplyChannel<GuildAdminResult>
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
        /// Owner of the marks on the ground.
        GroundMarks: Agent<GroundMarkCommand>
        /// Owner of the guilds and their channels.
        Guilds: Agent<GuildCommand>
        ChatCleanup: AgentOutbox<ChatRoomCommand>
        SystemCleanup: AgentOutbox<ChatRoomCommand>
        PresenceCleanup: AgentOutbox<PresenceCommand>
        GroundMarksCleanup: AgentOutbox<GroundMarkCommand>
        GuildsCleanup: AgentOutbox<GuildCommand>
        mutable ChatStopped: bool
        mutable SystemStopped: bool
        mutable PresenceStopped: bool
        mutable GroundMarksStopped: bool
        mutable GuildsStopped: bool
    }

    type private State = {
        Table: SessionTable.State
        mutable RouteScratch: SessionTable.Entry array
        Settings: GameSettings
        Moderation: ModerationRules
        Persistence: GroundMarkPersistence
        Guilds: GuildPersistence
        Schedule: AnnouncementSchedule
        Transport: ServerTransport
        Logger: ILogger
        mutable Sources: Sources option
        mutable Stopping: bool
        mutable StopDeadline: int64
        mutable SourcesStopping: bool
        mutable Ticker: AgentTicker option
        mutable LastTick: int64
        /// From the account service (AccountChange.AddressBans); empty until it sends them.
        mutable AddressBans: AddressBan list
        Phantoms: PhantomAgent.State option
    }

    // TickCount64 advances in ~15.6 ms steps on Windows, turning a 5 MiB/s
    // budget into ~80 KiB bursts despite a 1 ms service loop. Keep the uptime
    // epoch for existing deadlines, but measure elapsed time with QPC.
    let private clockOrigin = Stopwatch.GetTimestamp()
    let private uptimeOrigin = Environment.TickCount64
    let private now () = uptimeOrigin + int64 (Stopwatch.GetElapsedTime(clockOrigin).TotalMilliseconds)

    // A pseudonym is chosen at random, never from a name.
    let private pick count = Random.Shared.Next count

    let private isProxy state (address: IPAddress) =
        state.Settings.TrustedProxies |> List.exists (fun range -> AddressRange.contains range address)

    /// A session through a proxy of the server is the player who signed in
    /// through it: it takes that address, and the bans in force on it. true when
    /// such a ban refuses the session (the connection closes).
    let private throughProxy state (entry: SessionTable.Entry) (signedInFrom: IPAddress voption) =
        match signedInFrom with
        | ValueSome player when entry.Proxy.IsNone && isProxy state entry.Address ->
            entry.Proxy <- Some entry.Address
            entry.Address <- player
            match AddressBan.find DateTimeOffset.UtcNow player state.AddressBans with
            | ValueSome ban ->
                state.Logger.LogInformation("Closing {ConnectionId} from {Address} through proxy {Proxy}: the range {Range} is banned",
                                            entry.ConnectionId, ClientAddress.text player, ClientAddress.text entry.Proxy.Value,
                                            AddressRange.key ban.Range)
                state.Transport.Close entry.ConnectionId
                true
            | ValueNone ->
                state.Logger.LogDebug("Session {ConnectionId} comes through proxy {Proxy} from {Address}",
                                      entry.ConnectionId, ClientAddress.text entry.Proxy.Value, ClientAddress.text player)
                false
        | ValueSome _ | ValueNone -> false

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
            let marks = sources.GroundMarksCleanup.TrySend(context, GroundMarkCommand.Detach(detach SessionSource.GroundMarks), ServerRuntimeMessage.CleanupFailed)
            let guilds = sources.GuildsCleanup.TrySend(context, GuildCommand.Detach(detach SessionSource.Guilds), ServerRuntimeMessage.CleanupFailed)
            if not chat || not system || not presence || not marks || not guilds then fail state context "Session cleanup capacity exhausted."
        | None, _ | _, None -> fail state context "Session cleanup has no sources."

    // The public (moderated) username of a reserved player, for the log.
    let private username state playerId =
        match PseudonymBook.tryProfile playerId state.Table.Names with
        | ValueSome profile -> Username.value profile.Username
        | ValueNone -> "?"

    let private close (options: ServerRuntimeOptions) state (context: AgentContext<ServerRuntimeMessage>) (entry: SessionTable.Entry) =
        if entry.Phase <> RuntimeSessionPhase.Closing then
            match entry.Phase, entry.PlayerId with
            | RuntimeSessionPhase.Ready, Some playerId ->
                let duration = DateTimeOffset.UtcNow - entry.ConnectedAt
                state.Logger.LogInformation("Player {PlayerId} {Username} left (session {ConnectionId}, {Duration})",
                                            PlayerId.value playerId, username state playerId, entry.ConnectionId,
                                            duration.ToString(if duration.TotalDays >= 1.0 then @"d\.hh\:mm\:ss" else @"hh\:mm\:ss"))
            | (RuntimeSessionPhase.Waiting | RuntimeSessionPhase.Guest | RuntimeSessionPhase.Opening | RuntimeSessionPhase.Ready | RuntimeSessionPhase.Closing), _ -> ()
            entry.Phase <- RuntimeSessionPhase.Closing
            state.Phantoms |> Option.iter (fun phantoms -> PhantomAgent.detach phantoms entry.ConnectionId)
            let deadline = now () + int64 options.ShutdownTimeoutMs
            entry.Deadline <- if state.Stopping then min deadline state.StopDeadline else deadline
            if not entry.TransportClosed then state.Transport.Close entry.ConnectionId

            match entry.Child with
            | None ->
                entry.ChildStopped <- true
                entry.ChatDetached <- true
                entry.SystemDetached <- true
                entry.PresenceDetached <- true
                entry.GroundMarksDetached <- true
                entry.GuildsDetached <- true
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
                if entry.Phase <> RuntimeSessionPhase.Closing then
                    match state.Transport.Send(entry.ConnectionId, { Schedule = PacketSchedule.Ordered; Lane = lane; Bytes = bytes }) with
                    | Ok () -> ()
                    | Error _ when lane = DeliveryLane.Realtime -> () // Next period repairs a dropped pose.
                    | Error reason ->
                        state.Logger.LogWarning("Closing {ConnectionId}: {Reason}", entry.ConnectionId, reason)
                        close options state context entry

    let private send options state context (entry: SessionTable.Entry) response =
        let budget = state.Transport.MaxUnfragmentedPayloadBytes entry.ConnectionId
        transmit options state context entry (ProtocolCodec.delivery response).Lane (ProtocolCodec.encode state.Settings.Codec budget response)

    // The rejection goes back on the lane of the request it answers.
    let private refuse options state context (entry: SessionTable.Entry) lane requestId (rejection: RequestRejection) =
        state.Logger.LogDebug("Refused request {RequestId} of {ConnectionId}: {Code} {Field} {Message}",
                              requestId, entry.ConnectionId, rejection.Code, rejection.Field, rejection.Message)
        send options state context entry (ProtocolCodec.refusal lane requestId rejection)

    let private reject options state context entry lane requestId code message =
        refuse options state context entry lane requestId { Code = code; Message = message; Field = "" }

    let private tell state (entry: SessionTable.Entry) message =
        match entry.Child with
        | Some child ->
            match child.TryPost message with
            | AgentPostResult.Posted -> ()
            | AgentPostResult.Full | AgentPostResult.Closed | AgentPostResult.Canceled | AgentPostResult.Dropped ->
                state.Logger.LogWarning("Session {ConnectionId} did not take an account change", entry.ConnectionId)
        | None -> ()

    // The session hears of account changes after its reservation reply, in its own FIFO.
    let private panelChanges state (entry: SessionTable.Entry) playerId =
        match state.Table.Roles.TryGetValue playerId with
        | true, role -> tell state entry (PlayerSessionMessage.RoleChanged role)
        | false, _ -> ()
        match state.Table.Profiles.TryGetValue playerId with
        | true, profile -> tell state entry (PlayerSessionMessage.ProfileChanged profile)
        | false, _ -> ()
        match state.Table.Mutes.TryGetValue playerId with
        | true, mute -> tell state entry (PlayerSessionMessage.MuteChanged mute)
        | false, _ -> ()

    let private online state playerId =
        match state.Table.Players.TryGetValue playerId with
        | true, connectionId -> SessionTable.find connectionId state.Table
        | false, _ -> None

    let private host (options: ServerRuntimeOptions) state context command =
        match command with
        | SessionHostCommand.Reserve(connectionId, profile, hiding, signedInFrom, reply) ->
            let answer =
                match SessionTable.find connectionId state.Table with
                | Some entry when not state.Stopping && not (throughProxy state entry signedInFrom) ->
                    SessionTable.reserve profile hiding pick entry state.Table
                | Some _ | None -> IdentityAdmission.Closed

            match answer with
            | IdentityAdmission.AlreadyInUse ->
                state.Logger.LogInformation("Player {PlayerId} {Username} is already online; session {ConnectionId} is refused",
                                            PlayerId.value profile.PlayerId, Username.value profile.Username, connectionId)
            | IdentityAdmission.Reserved _ | IdentityAdmission.Closed -> ()
            match reply.TryPost answer with
            | AgentTryDeliveryResult.Posted ->
                match answer, SessionTable.find connectionId state.Table with
                | IdentityAdmission.Reserved _, Some entry -> panelChanges state entry profile.PlayerId
                | (IdentityAdmission.Reserved _ | IdentityAdmission.AlreadyInUse | IdentityAdmission.Closed), _ -> ()
            | AgentTryDeliveryResult.Full | AgentTryDeliveryResult.Closed ->
                state.Logger.LogWarning("Session {ConnectionId} did not take its reservation reply; closing", connectionId)
                SessionTable.find connectionId state.Table |> Option.iter (close options state context)

        | SessionHostCommand.ChangeIdentity(connectionId, hiding, reply) ->
            match SessionTable.find connectionId state.Table with
            | Some entry ->
                match SessionTable.changeIdentity hiding pick entry state.Table with
                | Some pseudonym ->
                    entry.PlayerId |> Option.iter (fun playerId ->
                        state.Logger.LogInformation("Player {PlayerId} {Username} now hides names: {Hiding}, pseudonym {Pseudonym}",
                                                    PlayerId.value playerId, username state playerId, hiding,
                                                    (match pseudonym with ValueSome name -> Pseudonym.value name | ValueNone -> "-")))
                    match reply.TryPost pseudonym with
                    | AgentTryDeliveryResult.Posted -> ()
                    | AgentTryDeliveryResult.Full | AgentTryDeliveryResult.Closed -> close options state context entry
                // A session that is not ready cannot be told a new identity; it is closing already.
                | None ->
                    state.Logger.LogDebug("Session {ConnectionId} changed identity outside Ready during {Phase}; closing", connectionId, entry.Phase)
                    close options state context entry
            | None -> ()

        | SessionHostCommand.UpdateProfile(connectionId, profile, own) ->
            match SessionTable.find connectionId state.Table with
            | Some entry -> SessionTable.updateProfile profile own entry state.Table |> ignore
            | None -> ()

        | SessionHostCommand.Activate(connectionId, requestId, welcome) ->
            match SessionTable.find connectionId state.Table with
            | Some entry when entry.Phase = RuntimeSessionPhase.Opening && not state.Stopping ->
                if now () >= entry.Deadline then
                    state.Logger.LogWarning("Session {ConnectionId} opened after its deadline; closing", connectionId)
                    close options state context entry
                elif entry.PlayerId <> Some welcome.SelfPlayerId then
                    state.Logger.LogError("Session {ConnectionId} opened for player {PlayerId} it did not reserve; closing",
                                          connectionId, PlayerId.value welcome.SelfPlayerId)
                    close options state context entry
                else
                    // Mark ready in the same handler that queues the welcome packet.
                    entry.Phase <- RuntimeSessionPhase.Ready
                    state.Logger.LogInformation("Player {PlayerId} {Username} joined (session {ConnectionId}, hides names: {Hiding}, {Online} online)",
                                                PlayerId.value welcome.SelfPlayerId, username state welcome.SelfPlayerId, connectionId, welcome.Hiding,
                                                state.Table.Players.Count)
                    send options state context entry (ServerResponse.SessionOpened(requestId, welcome))
                    state.Phantoms |> Option.iter (fun phantoms -> PhantomAgent.activate phantoms connectionId)
            | Some _ | None -> state.Logger.LogDebug("Late activation of {ConnectionId} ignored", connectionId)

        | SessionHostCommand.Send(connectionId, response) ->
            match SessionTable.find connectionId state.Table with
            | Some entry when entry.Phase = RuntimeSessionPhase.Ready -> send options state context entry response
            | Some entry when entry.Phase = RuntimeSessionPhase.Opening ->
                if (ProtocolCodec.delivery response).WhileOpening then send options state context entry response
                else state.Logger.LogWarning("Session {ConnectionId} sent {Response} before it opened; dropped", connectionId, response.GetType().Name)
            | Some _ | None -> ()

        | SessionHostCommand.ObservePhantoms observation ->
            // Presence is the only producer. Reject late membership from closed
            // sessions before it can create a new phantom source.
            match state.Phantoms with
            | Some phantoms ->
                let rec apply observation =
                    match observation with
                    | PhantomObservation.Batch values -> for value in values do apply value
                    | PhantomObservation.Member(id, _) ->
                        match SessionTable.find id state.Table with
                        | Some entry when entry.Phase = RuntimeSessionPhase.Opening || entry.Phase = RuntimeSessionPhase.Ready ->
                            PhantomAgent.observe phantoms observation
                            if entry.Phase = RuntimeSessionPhase.Ready then PhantomAgent.activate phantoms id
                        | _ -> ()
                    | _ -> PhantomAgent.observe phantoms observation
                apply observation
            | None -> ()

        | SessionHostCommand.Close(connectionId, reason) ->
            state.Logger.LogInformation("Session {ConnectionId}: {Reason}", connectionId, reason)
            SessionTable.find connectionId state.Table |> Option.iter (close options state context)
        | SessionHostCommand.SlowConsumer connectionId ->
            state.Logger.LogWarning("Slow consumer {ConnectionId}", connectionId)
            SessionTable.find connectionId state.Table |> Option.iter (close options state context)

    let private openSession (options: ServerRuntimeOptions) (authenticator: SessionAuthenticator) state (context: AgentContext<ServerRuntimeMessage>) (entry: SessionTable.Entry) requestId sessionTicket hiding =
        match state.Sources, context.Ref.TryReliable() with
        | Some sources, Some self ->
            let request = { ConnectionId = entry.ConnectionId; RequestId = requestId; SessionTicket = sessionTicket; Hiding = hiding }
            let child =
                PlayerSession.start state.Settings state.Moderation authenticator.Requests authenticator.Profiles authenticator.Moderation
                    (sources.Chat.Ref.TryReliable().Value) (sources.System.Ref.TryReliable().Value) (sources.Presence.Ref.TryReliable().Value)
                    (sources.GroundMarks.Ref.TryReliable().Value) (sources.Guilds.Ref.TryReliable().Value)
                    (self.Map ServerRuntimeMessage.Host) state.Logger request
            entry.Child <- Some child
            entry.Phase <- RuntimeSessionPhase.Opening
            // Authentication has its own time from the request: a guest may have
            // been connected for hours.
            entry.Deadline <- now () + int64 options.OpenTimeoutMs
            context.Own(child, fun outcome -> ServerRuntimeMessage.PlayerStopped(entry.ConnectionId, outcome))
            state.Logger.LogDebug("Session {ConnectionId} is opening (request {RequestId}, hides names: {Hiding})", entry.ConnectionId, requestId, hiding)
        | None, _ | _, None ->
            state.Logger.LogWarning("Cannot open session {ConnectionId}: runtime sources are not running", entry.ConnectionId)
            close options state context entry

    let private forward (options: ServerRuntimeOptions) state context (entry: SessionTable.Entry) lane requestId message =
        match entry.Child with
        | Some child ->
            match child.TryPost message with
            | AgentPostResult.Posted -> ()
            | AgentPostResult.Full ->
                state.Logger.LogWarning("Session {ConnectionId} input is full; request {RequestId} is refused", entry.ConnectionId, requestId)
                reject options state context entry lane requestId RequestRejectionCode.Overloaded "Session input is full."
            | AgentPostResult.Closed | AgentPostResult.Canceled | AgentPostResult.Dropped as result ->
                state.Logger.LogDebug("Session {ConnectionId} no longer takes input ({Result}); closing", entry.ConnectionId, result)
                close options state context entry
        | None ->
            state.Logger.LogWarning("Ready connection {ConnectionId} has no session; closing", entry.ConnectionId)
            close options state context entry

    /// Where a decoded command goes: it opens this connection's session, keeps
    /// the connection as a guest or is a message to the open session. The phase
    /// rules for all of them live in receive.
    [<RequireQualifiedAccess>]
    type private CommandRoute =
        | Open of sessionTicket: string * HiddenIdentity
        | Guest
        | Session of PlayerSessionMessage

    let private route requestId = function
        | ClientCommand.OpenSession(sessionTicket, hiding) -> CommandRoute.Open(sessionTicket, hiding)
        | ClientCommand.JoinAsGuest -> CommandRoute.Guest
        | ClientCommand.SendChat(channelId, text) -> CommandRoute.Session(PlayerSessionMessage.SendChat(requestId, channelId, text))
        | ClientCommand.UpdatePlayer update -> CommandRoute.Session(PlayerSessionMessage.Update(requestId, update))
        | ClientCommand.PostAnnouncement announcement -> CommandRoute.Session(PlayerSessionMessage.PostAnnouncement(requestId, announcement))
        | ClientCommand.PlaceGroundNote(text, placement, gameDate) ->
            CommandRoute.Session(PlayerSessionMessage.PlaceGroundNote(requestId, text, placement, gameDate))
        | ClientCommand.ReportDeath(label, placement, gameDate) ->
            CommandRoute.Session(PlayerSessionMessage.ReportDeath(requestId, label, placement, gameDate))
        | ClientCommand.RemoveGroundMark id -> CommandRoute.Session(PlayerSessionMessage.RemoveGroundMark(requestId, id))
        | ClientCommand.SetIdentityVisibility hiding -> CommandRoute.Session(PlayerSessionMessage.SetIdentityVisibility(requestId, hiding))
        | ClientCommand.ChangeDisplayName name -> CommandRoute.Session(PlayerSessionMessage.ChangeDisplayName(requestId, name))
        | ClientCommand.SetNameColor color -> CommandRoute.Session(PlayerSessionMessage.SetNameColor(requestId, color))
        | ClientCommand.SanctionPlayer(target, kind, term, reason, devices) ->
            CommandRoute.Session(PlayerSessionMessage.Moderate(requestId, ModerationAction.Sanction(target, kind, term, reason, devices)))
        | ClientCommand.LiftSanction(target, kind) -> CommandRoute.Session(PlayerSessionMessage.Moderate(requestId, ModerationAction.Lift(target, kind)))
        | ClientCommand.KickPlayer(target, reason) -> CommandRoute.Session(PlayerSessionMessage.Moderate(requestId, ModerationAction.Kick(target, reason)))
        | ClientCommand.ListSanctions -> CommandRoute.Session(PlayerSessionMessage.Moderate(requestId, ModerationAction.ListSanctions))
        | ClientCommand.ListPlayerMarks target -> CommandRoute.Session(PlayerSessionMessage.Moderate(requestId, ModerationAction.ListMarks target))
        | ClientCommand.ClearPlayerMarks(target, kinds) ->
            CommandRoute.Session(PlayerSessionMessage.Moderate(requestId, ModerationAction.ClearMarks(target, kinds)))
        | ClientCommand.DeleteChatMessage(channel, message) ->
            CommandRoute.Session(PlayerSessionMessage.Moderate(requestId, ModerationAction.DeleteMessage(channel, message)))
        | ClientCommand.Guild action -> CommandRoute.Session(PlayerSessionMessage.Guild(requestId, action))

    let private receiveSample state (entry: SessionTable.Entry) bytes =
        if entry.Phase = RuntimeSessionPhase.Ready then
            match ProtocolCodec.decodeMovement state.Settings.Codec bytes, entry.Child with
            | Ok sample, Some child ->
                // A dropped sample is repaired by the next periodic absolute pose.
                child.TryPost(PlayerSessionMessage.SampleMovement sample) |> ignore
            | Error _, _ | _, None -> ()

    let private receive (options: ServerRuntimeOptions) authenticator state context (entry: SessionTable.Entry) lane bytes =
        if lane = DeliveryLane.Models || lane = DeliveryLane.Poses then
            if entry.Phase = RuntimeSessionPhase.Ready then
                state.Phantoms |> Option.iter (fun phantoms -> PhantomAgent.receive phantoms (now()) entry.ConnectionId lane bytes)
        elif lane = DeliveryLane.Realtime then receiveSample state entry bytes
        else
            match ProtocolCodec.decodeClient state.Settings.Codec bytes with
            | Error error ->
                match error.RequestId with
                | None ->
                    state.Logger.LogWarning("Closing {ConnectionId}: undecodable packet on {Lane} ({Failure})", entry.ConnectionId, lane, error.Failure)
                    close options state context entry
                | Some requestId -> refuse options state context entry lane requestId (ProtocolCodec.rejection error.Failure)
            | Ok request when ProtocolCodec.requestLane request <> lane ->
                state.Logger.LogWarning("Closing {ConnectionId}: request {RequestId} arrived on {Lane} instead of {Expected}",
                                        entry.ConnectionId, request.RequestId, lane, ProtocolCodec.requestLane request)
                close options state context entry
            | Ok request ->
                match route request.RequestId request.Command, entry.Phase with
                | CommandRoute.Open(sessionTicket, hiding), (RuntimeSessionPhase.Waiting | RuntimeSessionPhase.Guest) ->
                    openSession options authenticator state context entry request.RequestId sessionTicket hiding
                | CommandRoute.Open _, (RuntimeSessionPhase.Opening | RuntimeSessionPhase.Ready) ->
                    reject options state context entry lane request.RequestId RequestRejectionCode.SessionAlreadyOpen "Session is already opening or open."
                | CommandRoute.Guest, RuntimeSessionPhase.Waiting ->
                    entry.Phase <- RuntimeSessionPhase.Guest
                    state.Logger.LogDebug("Connection {ConnectionId} stays as a guest", entry.ConnectionId)
                | CommandRoute.Guest, (RuntimeSessionPhase.Guest | RuntimeSessionPhase.Opening | RuntimeSessionPhase.Ready) ->
                    reject options state context entry lane request.RequestId RequestRejectionCode.InvalidRequest "The connection is not waiting."
                | CommandRoute.Session message, RuntimeSessionPhase.Ready -> forward options state context entry lane request.RequestId message
                | CommandRoute.Session _, (RuntimeSessionPhase.Waiting | RuntimeSessionPhase.Guest | RuntimeSessionPhase.Opening) ->
                    reject options state context entry lane request.RequestId RequestRejectionCode.SessionNotReady "Session is not ready."
                | (CommandRoute.Open _ | CommandRoute.Guest | CommandRoute.Session _), RuntimeSessionPhase.Closing -> ()

    let private disconnected options state context connectionId =
        match SessionTable.find connectionId state.Table with
        | None -> ()
        | Some entry ->
            // A ready player's departure is logged by close together with the name;
            // guests come and go with every game start and sign-in.
            match entry.Phase with
            | RuntimeSessionPhase.Waiting | RuntimeSessionPhase.Opening ->
                state.Logger.LogInformation("Transport disconnected {ConnectionId} during {Phase}", connectionId, entry.Phase)
            | RuntimeSessionPhase.Guest | RuntimeSessionPhase.Ready -> state.Logger.LogDebug("Transport disconnected {ConnectionId}", connectionId)
            | RuntimeSessionPhase.Closing -> ()

            entry.TransportClosed <- true
            close options state context entry
            if SessionTable.clean entry then SessionTable.remove entry state.Table

    let private transportEvent (options: ServerRuntimeOptions) authenticator state context event =
        match event with
        | ServerTransportEvent.Connected(connectionId, address) ->
            // A proxy of the server is never banned; its players are, once they sign in.
            let banned = if isProxy state address then ValueNone else AddressBan.find DateTimeOffset.UtcNow address state.AddressBans
            if state.Stopping then
                state.Logger.LogDebug("Refusing connection {ConnectionId}: the server is stopping", connectionId)
                state.Transport.Close connectionId
            elif state.Table.Connections.Count >= options.MaxSessions then
                state.Logger.LogWarning("Refusing connection {ConnectionId}: the server is full ({MaxSessions} connections)", connectionId, options.MaxSessions)
                state.Transport.Close connectionId
            elif state.Table.Connections.ContainsKey connectionId then
                state.Logger.LogWarning("Transport reported connection {ConnectionId} twice", connectionId)
            elif banned.IsSome then
                state.Logger.LogInformation("Refusing connection {ConnectionId} from {Address}: the range {Range} is banned",
                                            connectionId, ClientAddress.text address, AddressRange.key banned.Value.Range)
                state.Transport.Close connectionId
            else
                SessionTable.add connectionId address DateTimeOffset.UtcNow (now () + int64 options.OpenTimeoutMs) state.Table |> ignore
                state.Logger.LogDebug("Connection {ConnectionId} accepted ({Connections} connections)", connectionId, state.Table.Connections.Count)
        | ServerTransportEvent.Received(connectionId, lane, bytes) ->
            match SessionTable.find connectionId state.Table with
            | Some entry when entry.Phase <> RuntimeSessionPhase.Closing -> receive options authenticator state context entry lane bytes
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
            let game = state.Settings
            let system = ChatRoomAgent.start (AnnouncementOptions.channelOptions options.Chat game.Announcements) ChatChannelKind.System output
            match ChatRoomAgent.start options.Chat ChatChannelKind.Global output, system with
            | Error error, _ | _, Error error -> fail state context $"Chat startup failed: {error}"
            | Ok chat, Ok system ->
                context.Own(chat, fun outcome -> ServerRuntimeMessage.SourceStopped(SessionSource.Chat, outcome))
                context.Own(system, fun outcome -> ServerRuntimeMessage.SourceStopped(SessionSource.System, outcome))
                let marks = GroundMarksAgent.start game.GroundMarks game.GroundMarkRules state.Persistence.Loaded state.Persistence.NextId state.Persistence.Writer output state.Logger
                let guilds = GuildsAgent.start game.Guilds game.GuildLimits state.Moderation options.Chat.Rate state.Guilds output state.Logger
                match marks, guilds with
                | Error error, _ -> fail state context $"Ground marks startup failed: {error}"
                | Ok marks, Error error ->
                    marks.Abort()
                    fail state context $"Guilds startup failed: {error}"
                | Ok marks, Ok guilds ->
                    let observation = state.Phantoms |> Option.map PhantomAgent.observationMode |> Option.defaultValue PhantomObservationMode.Disabled
                    let presence = PresenceAgent.startObserved observation options.Presence output
                    context.Own(presence, fun outcome -> ServerRuntimeMessage.SourceStopped(SessionSource.Presence, outcome))
                    context.Own(marks, fun outcome -> ServerRuntimeMessage.SourceStopped(SessionSource.GroundMarks, outcome))
                    context.Own(guilds, fun outcome -> ServerRuntimeMessage.SourceStopped(SessionSource.Guilds, outcome))
                    // A failed guild write stops the writer: the supervisor restarts the game from storage.
                    context.Watch(state.Guilds.WriterStopped, fun outcome -> ServerRuntimeMessage.SourceStopped(SessionSource.GuildStorage, outcome))
                    context.Watch(authenticator.Completion, fun outcome -> ServerRuntimeMessage.SourceStopped(SessionSource.Authentication, outcome))
                    state.Sources <- Some {
                        Chat = chat; System = system; Presence = presence; GroundMarks = marks; Guilds = guilds
                        ChatCleanup = AgentOutbox(options.MaxSessions, chat.Ref.TryReliable().Value)
                        SystemCleanup = AgentOutbox(options.MaxSessions, system.Ref.TryReliable().Value)
                        PresenceCleanup = AgentOutbox(options.MaxSessions, presence.Ref.TryReliable().Value)
                        GroundMarksCleanup = AgentOutbox(options.MaxSessions, marks.Ref.TryReliable().Value)
                        GuildsCleanup = AgentOutbox(options.MaxSessions, guilds.Ref.TryReliable().Value)
                        ChatStopped = false; SystemStopped = false; PresenceStopped = false; GroundMarksStopped = false; GuildsStopped = false
                    }
                    state.Transport.SetReadyHandler(fun () ->
                        context.Ref.TryPost ServerRuntimeMessage.TransportReady = AgentPostResult.Posted)
                    schedule options state context
                    state.Logger.LogDebug("Server runtime started: up to {MaxSessions} connections, open timeout {OpenTimeoutMs} ms",
                                          options.MaxSessions, options.OpenTimeoutMs)

    let private stopped (options: ServerRuntimeOptions) state context connectionId (outcome: Result<unit, exn>) =
        match SessionTable.find connectionId state.Table with
        | None -> ()
        | Some entry when entry.ChildStopped -> ()
        | Some entry ->
            match outcome with
            | Error error when entry.Phase <> RuntimeSessionPhase.Closing -> state.Logger.LogError(error, "Player session {ConnectionId} terminated", connectionId)
            | Ok () when entry.Phase <> RuntimeSessionPhase.Closing ->
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
            | SessionSource.GroundMarks -> entry.GroundMarksDetached <- true
            | SessionSource.Guilds -> entry.GuildsDetached <- true
            | SessionSource.GuildStorage | SessionSource.Authentication -> ()

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
        | RuntimeSessionPhase.Waiting | RuntimeSessionPhase.Opening when time >= entry.Deadline ->
            state.Logger.LogInformation("Closing {ConnectionId}: no session opened in time (during {Phase})", entry.ConnectionId, entry.Phase)
            close options state context entry
        | RuntimeSessionPhase.Closing when time >= entry.Deadline ->
            if SessionTable.domainClean entry then
                state.Logger.LogInformation("Transport of {ConnectionId} did not confirm its close; resetting", entry.ConnectionId)
                state.Transport.Reset entry.ConnectionId
                entry.TransportClosed <- true
                SessionTable.remove entry state.Table
            else
                fail state context $"Session cleanup timed out: {entry.ConnectionId}"
        // A guest has no deadline: ENet's timeout drops a dead peer.
        | RuntimeSessionPhase.Guest | RuntimeSessionPhase.Ready | RuntimeSessionPhase.Waiting | RuntimeSessionPhase.Opening
        | RuntimeSessionPhase.Closing -> ()

    /// Ends the player's sessions, telling each why before the close. A
    /// revocation or a ban also closes every opening session: a ticket consumed
    /// just before may still be on its way. A kick ends only the player's own.
    let private endSessions (options: ServerRuntimeOptions) state context playerId reason =
        let tickets =
            match reason with
            | SessionEnd.AccessRevoked | SessionEnd.Banned _ | SessionEnd.AddressBanned _ -> true
            | SessionEnd.Kicked _ -> false
        let affected =
            state.Table.Connections.Values
            |> Seq.filter (fun entry -> entry.PlayerId = Some playerId || (tickets && entry.Phase = RuntimeSessionPhase.Opening))
            |> Seq.toArray
        state.Logger.LogInformation("Ending the sessions of player {PlayerId} ({Reason}): closing {Count} connections, sessions still opening included",
                                    PlayerId.value playerId, reason, affected.Length)
        for entry in affected do
            if entry.PlayerId = Some playerId && entry.Phase <> RuntimeSessionPhase.Closing then
                send options state context entry (ServerResponse.SessionEnded reason)
            close options state context entry

    /// The bans in force from now on. Every connection they cover ends; a
    /// session first hears why, like an account ban. A guest of a proxy of the
    /// server stays: the proxy itself is never banned.
    let private banAddresses (options: ServerRuntimeOptions) state context (bans: AddressBan list) =
        state.AddressBans <- bans
        let time = DateTimeOffset.UtcNow
        visitRoutes state (fun entry ->
            let proxyGuest = entry.Proxy.IsNone && isProxy state entry.Address
            match (if proxyGuest then ValueNone else AddressBan.find time entry.Address bans) with
            | ValueSome ban when entry.Phase <> RuntimeSessionPhase.Closing ->
                state.Logger.LogInformation("Closing {ConnectionId} from {Address}: the range {Range} is banned",
                                            entry.ConnectionId, ClientAddress.text entry.Address, AddressRange.key ban.Range)
                if entry.PlayerId.IsSome then send options state context entry (ServerResponse.SessionEnded(SessionEnd.AddressBanned ban))
                close options state context entry
            | ValueSome _ | ValueNone -> ())

    let private stop (options: ServerRuntimeOptions) state context =
        if not state.Stopping then
            state.Logger.LogInformation("Server runtime stopping: closing {Connections} connections", state.Table.Connections.Count)
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
            state.Sources |> Option.forall (fun sources ->
                sources.ChatStopped && sources.SystemStopped && sources.PresenceStopped && sources.GroundMarksStopped && sources.GuildsStopped)
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
                    sources.GroundMarks.Complete() |> ignore
                    sources.Guilds.Complete() |> ignore

                if sources.ChatStopped && sources.SystemStopped && sources.PresenceStopped && sources.GroundMarksStopped && sources.GuildsStopped
                   && state.Table.Connections.Count = 0 then
                    context.Complete() |> ignore

    let private sourceStopped state context source (outcome: Result<unit, exn>) =
        if not state.SourcesStopping then
            let reason = match outcome with Ok () -> "completed on its own" | Error error -> error.Message
            fail state context $"Source {source} terminated: {reason}"
        else
            match outcome with
            | Error error -> fail state context $"Source {source} failed during shutdown: {error.Message}"
            | Ok () ->
                match state.Sources, source with
                | Some sources, SessionSource.Chat -> sources.ChatStopped <- true
                | Some sources, SessionSource.System -> sources.SystemStopped <- true
                | Some sources, SessionSource.Presence -> sources.PresenceStopped <- true
                | Some sources, SessionSource.GroundMarks -> sources.GroundMarksStopped <- true
                | Some sources, SessionSource.Guilds -> sources.GuildsStopped <- true
                | Some _, (SessionSource.GuildStorage | SessionSource.Authentication) | None, _ -> ()

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
            state.Phantoms |> Option.iter (fun phantoms -> PhantomAgent.tick phantoms (now()))
            RuntimeMetrics.runtimeTick.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds)
            state.Ticker |> Option.iter _.Acknowledge()
        | ServerRuntimeMessage.Host command -> host options state context command
        | ServerRuntimeMessage.PlayerStopped(connectionId, outcome) -> stopped options state context connectionId outcome
        | ServerRuntimeMessage.SourceStopped(source, outcome) -> sourceStopped state context source outcome
        | ServerRuntimeMessage.Detached(source, connectionId) -> detached state source connectionId
        | ServerRuntimeMessage.CleanupFailed failure -> fail state context $"Session cleanup delivery failed: {failure}"
        | ServerRuntimeMessage.AccountChanged change ->
            match change with
            | AccountChange.AccessRevoked playerId -> endSessions options state context playerId SessionEnd.AccessRevoked
            | AccountChange.Banned ban -> endSessions options state context ban.Target (SessionEnd.Banned ban)
            | AccountChange.MuteChanged(playerId, mute) ->
                state.Table.Mutes[playerId] <- mute
                online state playerId |> Option.iter (fun entry -> tell state entry (PlayerSessionMessage.MuteChanged mute))
            | AccountChange.Kicked(playerId, reason) -> endSessions options state context playerId (SessionEnd.Kicked reason)
            | AccountChange.AddressBans bans -> banAddresses options state context bans
        | ServerRuntimeMessage.Announce announcement -> announce state announcement
        | ServerRuntimeMessage.SetPlayerRole(playerId, role) ->
            state.Logger.LogDebug("Role of player {PlayerId} is now {Role}; online: {Online}", PlayerId.value playerId, role, state.Table.Players.ContainsKey playerId)
            state.Table.Roles[playerId] <- role
            online state playerId |> Option.iter (fun entry -> panelChanges state entry playerId)
        | ServerRuntimeMessage.RenamePlayer profile ->
            state.Logger.LogDebug("Player {PlayerId} renamed by an administrator; online: {Online}",
                                  PlayerId.value profile.PlayerId, state.Table.Players.ContainsKey profile.PlayerId)
            state.Table.Profiles[profile.PlayerId] <- profile
            online state profile.PlayerId |> Option.iter (fun entry -> panelChanges state entry profile.PlayerId)
            // Guildmates of an offline player see the new name too.
            match state.Sources with
            | Some sources ->
                match sources.Guilds.TryPost(GuildCommand.Rename(Moderation.publicProfile state.Moderation profile)) with
                | AgentPostResult.Posted -> ()
                | AgentPostResult.Full | AgentPostResult.Closed | AgentPostResult.Canceled | AgentPostResult.Dropped ->
                    state.Logger.LogWarning("The guild owner did not take the rename of player {PlayerId}", PlayerId.value profile.PlayerId)
            | None -> ()
        | ServerRuntimeMessage.Guilds(command, reply) ->
            match state.Sources with
            | Some sources when not state.Stopping ->
                match sources.Guilds.TryPost(GuildCommand.Admin(command, reply)) with
                | AgentPostResult.Posted -> ()
                | AgentPostResult.Full | AgentPostResult.Closed | AgentPostResult.Canceled | AgentPostResult.Dropped ->
                    state.Logger.LogWarning("The guild owner did not take a panel request")
            | Some _ | None -> ()
        | ServerRuntimeMessage.ListSessions reply ->
            reply.Reply [
                for entry in state.Table.Connections.Values do
                    { ConnectionId = entry.ConnectionId; Address = entry.Address; Proxy = entry.Proxy; PlayerId = entry.PlayerId; Phase = entry.Phase
                      ConnectedAt = entry.ConnectedAt; Session = entry.Child |> Option.map _.Ref }
            ]
        | ServerRuntimeMessage.Stop -> stop options state context
        | ServerRuntimeMessage.Read reply ->
            let count phase = state.Table.Connections.Values |> Seq.filter (fun entry -> entry.Phase = phase) |> Seq.length
            reply.Reply {
                Connections = state.Table.Connections.Count
                Guests = count RuntimeSessionPhase.Guest
                Ready = count RuntimeSessionPhase.Ready
                Reservations = state.Table.Players.Count
                Closing = count RuntimeSessionPhase.Closing
                Stopping = state.Stopping
            }
        | ServerRuntimeMessage.FindPlayer(connectionId, reply) ->
            let player =
                SessionTable.find connectionId state.Table
                |> Option.bind (fun entry -> if entry.Phase = RuntimeSessionPhase.Ready then entry.Child |> Option.map _.Ref else None)
            reply.Reply player

        finish state context
    }

    let private isControl = function
        | ServerRuntimeMessage.Host(SessionHostCommand.Send _) | ServerRuntimeMessage.Read _
        | ServerRuntimeMessage.Host(SessionHostCommand.ObservePhantoms _)
        | ServerRuntimeMessage.FindPlayer _ | ServerRuntimeMessage.TransportReady | ServerRuntimeMessage.Announce _
        | ServerRuntimeMessage.ListSessions _ | ServerRuntimeMessage.SetPlayerRole _ | ServerRuntimeMessage.RenamePlayer _
        | ServerRuntimeMessage.Guilds _ -> false
        | ServerRuntimeMessage.Start | ServerRuntimeMessage.Tick _ | ServerRuntimeMessage.Host _
        | ServerRuntimeMessage.PlayerStopped _ | ServerRuntimeMessage.SourceStopped _
        | ServerRuntimeMessage.Detached _ | ServerRuntimeMessage.CleanupFailed _ | ServerRuntimeMessage.AccountChanged _
        | ServerRuntimeMessage.Stop -> true

    /// The caller owns authentication separately and disposes the
    /// transport AFTER this agent's Completion, including Abort/fault paths.
    /// The checked settings, moderation rules and pseudonym dictionary are
    /// fixed for the runtime lifetime.
    let private startWithStorage storage (settings: GameSettings) (moderation: ModerationRules) (pseudonyms: PseudonymDictionary) (persistence: GroundMarkPersistence)
              (guilds: GuildPersistence) (authenticator: SessionAuthenticator) transport (logger: ILogger) =
        let options = settings.Runtime
        let state = {
            Table = SessionTable.create pseudonyms; RouteScratch = Array.empty
            Settings = settings; Moderation = moderation; Persistence = persistence; Guilds = guilds
            Schedule = AnnouncementSchedule.create (now ()) settings.Schedule
            Transport = transport; Logger = logger
            Sources = None; Stopping = false; SourcesStopping = false; Ticker = None; LastTick = 0L; StopDeadline = 0L
            AddressBans = []
            Phantoms = storage |> Option.map (fun storage -> PhantomAgent.create settings.Phantoms storage transport.Send)
        }
        let agentOptions = { AgentOptions.create "server-runtime" with Mailbox = AgentMailbox.boundedWithControl options.MailboxCapacity options.ControlReserve }
        let agent = Agent.Start(agentOptions, handle options authenticator state, isControl = isControl)
        agent.TryPost ServerRuntimeMessage.Start |> ignore
        agent

    let start settings moderation pseudonyms persistence guilds authenticator transport logger =
        startWithStorage None settings moderation pseudonyms persistence guilds authenticator transport logger

    let startWithPhantoms storage settings moderation pseudonyms persistence guilds authenticator transport logger =
        startWithStorage (Some storage) settings moderation pseudonyms persistence guilds authenticator transport logger
