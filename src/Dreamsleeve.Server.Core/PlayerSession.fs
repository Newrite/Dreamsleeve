namespace Dreamsleeve.Server.Core

open System
open System.Collections.Generic
open Microsoft.Extensions.Logging
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

[<RequireQualifiedAccess>]
type PlayerSessionMessage =
    | Begin
    | Authenticated of SessionAuthenticationReply
    | IdentityReplied of IdentityAdmission
    /// The runtime switched this player's public identity for the request.
    | IdentityChanged of requestId: uint64 * Pseudonym voption
    | ChatEvent of ChatRoomEvent
    | PresenceEvent of PresenceEvent
    | GroundMarkEvent of GroundMarkEvent
    | ChatDetached of Guid
    | SystemDetached of Guid
    | PresenceDetached of Guid
    | GroundMarksDetached of Guid
    | SendChat of requestId: uint64 * ChatChannelId * ChatMessageText
    | PostAnnouncement of requestId: uint64 * AnnouncementRequest
    | PlaceGroundNote of requestId: uint64 * GroundNoteText * GroundMarkPlacement * GameDate
    | ReportDeath of requestId: uint64 * DeathMarkText * GroundMarkPlacement * GameDate
    | RemoveGroundMark of requestId: uint64 * GroundMarkId
    | Update of requestId: uint64 * PlayerUpdate
    | SetIdentityVisibility of requestId: uint64 * HiddenIdentity
    /// The player's own new display name, from the client.
    | ChangeDisplayName of requestId: uint64 * DisplayName
    /// The account service stored the name, or refused it.
    | DisplayNameReplied of DisplayNameChangeReply
    | SampleMovement of MovementSample
    | Read of ReplyChannel<Result<PlayerSnapshot, PlayerStateError>>
    /// An administrator changed the role; applied without reconnecting.
    | RoleChanged of PlayerRole
    /// The player's mute now, if any; applied without reconnecting.
    | MuteChanged of Sanction voption
    /// A moderator's request from the client.
    | Moderate of requestId: uint64 * ModerationAction
    /// The account service answered a moderation request.
    | ModerationReplied of ModerationReply
    /// An administrator renamed the player: the stored profile, before moderation.
    | ProfileChanged of PlayerData
    /// The panel's view of this player; None before the profile is known.
    | Describe of ReplyChannel<AdminPlayerView option>
    | Stop

/// Owns one player's state, opening barrier and bounded request admission.
[<RequireQualifiedAccess>]
module PlayerSession =
    type private Opening = {
        Player: Player
        mutable Chat: ChatSnapshot option
        mutable System: ChatSnapshot option
        mutable Online: (PlayerSnapshot list * ActorValueKinds) option
        Buffered: ResizeArray<ServerResponse>
    }

    type private Phase =
        | Starting
        | Resolving of Guid
        | Reserving of Player
        | Opening of Opening
        | Active of Player
        | Closing

    type private State = {
        mutable Phase: Phase
        mutable ChatAttached: bool
        mutable SystemAttached: bool
        mutable PresenceAttached: bool
        mutable GroundMarksAttached: bool
        mutable CloseSent: bool
        /// The current character name failed moderation and is not published.
        mutable CharacterWithheld: bool
        /// Shown to others instead of every real name while the player hides them.
        mutable Pseudonym: Pseudonym voption
        /// Where the names are hidden; Pseudonym is present exactly when they are.
        mutable Hiding: HiddenIdentity
        /// The one identity switch waiting for the runtime, with the requested hiding.
        mutable IdentityRequest: struct (uint64 * HiddenIdentity) voption
        /// Environment.TickCount64 of the last accepted switch in this session.
        mutable LastIdentitySwitch: int64 voption
        /// The stored profile before moderation placeholders; only Describe shows it.
        mutable Account: PlayerData voption
        mutable Role: PlayerRole
        /// It ends by itself once its term passes (Sanction.activeAt).
        mutable Mute: Sanction voption
        OpenedAt: DateTimeOffset
        /// The one own display name change waiting for the account service.
        mutable NameRequest: struct (uint64 * Guid) voption
        Moderation: ModerationRules
        Settings: GameSettings
        Pending: HashSet<uint64>
        Authentication: AgentOutbox<SessionAuthenticationRequest>
        DisplayNames: AgentOutbox<DisplayNameChangeRequest>
        /// Sanctions, kicks, their list and the audit of removed content.
        AccountModeration: AgentOutbox<ModerationRequest>
        /// Moderation requests the account service still has to answer.
        ModerationRequests: Dictionary<Guid, struct (uint64 * ModerationAction)>
        Chat: AgentOutbox<ChatRoomCommand>
        System: AgentOutbox<ChatRoomCommand>
        Presence: AgentOutbox<PresenceCommand>
        GroundMarks: AgentOutbox<GroundMarkCommand>
        Host: AgentOutbox<SessionHostCommand>
        Logger: ILogger
    }

    let private reliable (context: AgentContext<PlayerSessionMessage>) = context.Ref.TryReliable()

    // Every outbound projection of this player passes here: a withheld game
    // name never reaches presence, bootstrap snapshots or chat messages, and
    // while the player hides their names only the pseudonym leaves this owner.
    let private publicSnapshot state player =
        let snapshot = Player.snapshot player
        let moderated =
            if state.CharacterWithheld then { snapshot with CharacterName = ValueNone; CharacterNameWithheld = true }
            else snapshot
        match state.Pseudonym with
        | ValueSome name -> PlayerSnapshot.withPseudonym name moderated
        | ValueNone -> moderated

    let private publicCharacterName state (player: Player) =
        if state.CharacterWithheld || state.Pseudonym.IsSome then ValueNone else player.CharacterName

    let private publicIdentity state (player: Player) =
        PublicIdentity.ofProfile state.Pseudonym player.Data

    // Ground marks may keep the real profile while presence and chat hide it.
    let private markPseudonym state =
        if HiddenIdentity.coversGroundMarks state.Hiding then state.Pseudonym else ValueNone

    let private markCharacterName state (player: Player) =
        if state.CharacterWithheld || (markPseudonym state).IsSome then ValueNone else player.CharacterName

    // The player always sees their own real profile: presence carries the copy
    // meant for others, so a pseudonymous entry about self is restored here,
    // with the character name only while it still names the same character.
    let private ownView state (player: Player) (snapshot: PlayerSnapshot) =
        match snapshot.Identity with
        | PublicIdentity.Pseudonymous(playerId, _) when playerId = player.Data.PlayerId ->
            let sameCharacter = snapshot.CharacterGeneration = player.CharacterGeneration
            { snapshot with
                Identity = PublicIdentity.Profile player.Data
                CharacterName = if sameCharacter && not state.CharacterWithheld then player.CharacterName else ValueNone }
        | PublicIdentity.Pseudonymous _ | PublicIdentity.Profile _ -> snapshot

    let private restoreOwn state snapshot =
        match state.Phase with
        | Opening opening -> ownView state opening.Player snapshot
        | Active player -> ownView state player snapshot
        | Starting | Resolving _ | Reserving _ | Closing -> snapshot

    let private completeIfDetached state (context: AgentContext<PlayerSessionMessage>) =
        match state.Phase with
        | Closing when not state.ChatAttached && not state.SystemAttached && not state.PresenceAttached && not state.GroundMarksAttached ->
            context.Complete() |> ignore
        | Starting | Resolving _ | Reserving _ | Opening _ | Active _ | Closing -> ()

    let private stop (request: SessionOpenRequest) state (context: AgentContext<PlayerSessionMessage>) =
        match state.Phase with
        | Closing -> ()
        | Starting | Resolving _ | Reserving _ ->
            state.Phase <- Closing
            // Do not wait for a profile/identity response, or its blocked admission.
            context.Abort()
        | Opening _ | Active _ ->
            state.Phase <- Closing
            state.Pending.Clear()

            match reliable context with
            | None -> context.Abort()
            | Some address ->
                let detach reply = { ConnectionId = request.ConnectionId; ReplyTo = reply }
                if state.ChatAttached then
                    let command = ChatRoomCommand.Detach(detach (address.Map PlayerSessionMessage.ChatDetached))
                    if not (state.Chat.TrySend(context, command)) then context.Abort()

                if state.SystemAttached then
                    let command = ChatRoomCommand.Detach(detach (address.Map PlayerSessionMessage.SystemDetached))
                    if not (state.System.TrySend(context, command)) then context.Abort()

                if state.PresenceAttached then
                    let command = PresenceCommand.Detach(detach (address.Map PlayerSessionMessage.PresenceDetached))
                    if not (state.Presence.TrySend(context, command)) then context.Abort()

                if state.GroundMarksAttached then
                    let command = GroundMarkCommand.Detach(detach (address.Map PlayerSessionMessage.GroundMarksDetached))
                    if not (state.GroundMarks.TrySend(context, command)) then context.Abort()

                completeIfDetached state context

    let private close (request: SessionOpenRequest) state (context: AgentContext<PlayerSessionMessage>) reason =
        if not state.CloseSent then
            state.CloseSent <- true
            // Same FIFO as rejection/Activate/data: close cannot overtake them.
            if not (state.Host.TrySend(context, SessionHostCommand.Close(request.ConnectionId, reason))) then
                context.Abort()

        match state.Phase with
        | Starting | Resolving _ | Reserving _ ->
            state.Phase <- Closing
            // Flush the failure report, then cancel any unrelated blocked delivery.
            state.Host.AbortAfterDrain context
        | Opening _ | Active _ | Closing -> stop request state context

    let private emit (options: PlayerSessionOptions) (request: SessionOpenRequest) state context command =
        if state.Host.Count >= options.MaxPendingOutput || not (state.Host.TrySend(context, command)) then
            close request state context "Session output is full."
            false
        else
            true

    let private send (options: PlayerSessionOptions) (request: SessionOpenRequest) state context response =
        emit options request state context (SessionHostCommand.Send(request.ConnectionId, response)) |> ignore

    // The account for the log; 0 until the ticket is resolved.
    let private accountId state =
        state.Account |> ValueOption.map (fun profile -> PlayerId.value profile.PlayerId) |> ValueOption.defaultValue 0UL

    // Every refusal of this session passes here. The level says who should
    // notice: words and frequency concern moderators, overload the operator.
    let private sendRefusal (options: PlayerSessionOptions) (request: SessionOpenRequest) state context lane requestId (rejection: RequestRejection) =
        let level =
            match rejection.Code with
            | RequestRejectionCode.TextNotAllowed | RequestRejectionCode.RateLimited -> LogLevel.Information
            | RequestRejectionCode.Overloaded -> LogLevel.Warning
            | _ -> LogLevel.Debug
        state.Logger.Log(level, "Player {PlayerId} (session {ConnectionId}) refused request {RequestId}: {Code} {Field} {Message}",
                         accountId state, request.ConnectionId, requestId, rejection.Code, rejection.Field, rejection.Message)
        send options request state context (ProtocolCodec.refusal lane requestId rejection)

    let private reject (options: PlayerSessionOptions) (request: SessionOpenRequest) state context requestId code message =
        sendRefusal options request state context DeliveryLane.Control requestId { Code = code; Message = message; Field = "" }

    let private muted state = state.Mute |> ValueOption.exists (Sanction.activeAt DateTimeOffset.UtcNow)

    let private selfId state =
        match state.Account with
        | ValueSome account -> ValueSome account.PlayerId
        | ValueNone -> ValueNone

    // The audit line of content a moderator removed through another owner.
    // It settles nothing: a full admission loses the line with a warning.
    let private audit state context action target details =
        match selfId state, reliable context with
        | ValueSome moderator, Some address ->
            let request = {
                OperationId = Guid.NewGuid()
                Command = ModerationCommand.Record(moderator, AuditRecord.create action (AuditTarget.Player target) details)
                ReplyTo = address.Map PlayerSessionMessage.ModerationReplied
            }
            if not (state.AccountModeration.TrySend(context, request)) then
                state.Logger.LogWarning("Audit line {Action} of moderator {PlayerId} was not admitted", AdminAction.key action, PlayerId.value moderator)
        | ValueNone, _ | _, None -> ()

    // A muted player's writing is refused whole; the client shows the mute it was told.
    let private mutedRejection = { Code = RequestRejectionCode.Muted; Message = "The player is muted."; Field = "" }

    let private rejectChat (options: PlayerSessionOptions) (request: SessionOpenRequest) state context requestId code message =
        sendRefusal options request state context DeliveryLane.Chat requestId { Code = code; Message = message; Field = "" }

    let private rejectOpening (options: PlayerSessionOptions) (request: SessionOpenRequest) state context code message =
        reject options request state context request.RequestId code message
        close request state context message

    let private beginResolve (options: PlayerSessionOptions) (request: SessionOpenRequest) state (context: AgentContext<PlayerSessionMessage>) =
        match state.Phase, reliable context with
        | Starting, Some _ when HiddenIdentity.isHidden request.Hiding && not state.Settings.Identity.AllowHiddenIdentity ->
            // Refused before the ticket is spent: the player may reconnect with names shown.
            rejectOpening options request state context RequestRejectionCode.HiddenIdentityNotAllowed "The server does not let players hide their names."
        | Starting, Some address ->
            let operationId = Guid.NewGuid()
            state.Phase <- Resolving operationId
            let query = {
                OperationId = operationId
                Ticket = request.SessionTicket
                ReplyTo = address.Map PlayerSessionMessage.Authenticated
            }
            if not (state.Authentication.TrySend(context, query)) then context.Abort()
        | Starting, None -> context.Abort()
        | Resolving _, _ | Reserving _, _ | Opening _, _ | Active _, _ | Closing, _ -> ()

    let private authenticated (options: PlayerSessionOptions) (request: SessionOpenRequest) state context (reply: SessionAuthenticationReply) =
        match state.Phase with
        | Resolving operationId when reply.OperationId = operationId ->
            match reply.Result, reliable context with
            | Ok stored, Some address ->
                state.Account <- ValueSome stored.Profile
                state.Role <- stored.Role
                state.Mute <- stored.Mute
                state.Logger.LogDebug("Session {ConnectionId} authenticated as player {PlayerId} ({Role})",
                                      request.ConnectionId, PlayerId.value stored.Profile.PlayerId, stored.Role)
                // Accounts created under older rules keep their stored names;
                // every copy leaving this session uses the moderated profile.
                let profile = Moderation.publicProfile state.Moderation stored.Profile
                state.Phase <- Reserving(Player.create profile)
                emit options request state context
                    (SessionHostCommand.Reserve(request.ConnectionId, profile, request.Hiding,
                        address.Map PlayerSessionMessage.IdentityReplied)) |> ignore

            | Error SessionAuthenticationError.InvalidTicket, _ ->
                rejectOpening options request state context RequestRejectionCode.AuthenticationFailed "Session ticket is invalid or expired."
            | Error SessionAuthenticationError.Unavailable, _ ->
                rejectOpening options request state context RequestRejectionCode.Overloaded "Authentication is temporarily unavailable."
            | Ok _, None ->
                close request state context "Authentication reply cannot be applied."
        | Starting | Resolving _ | Reserving _ | Opening _ | Active _ | Closing -> ()

    let private identityReply (options: PlayerSessionOptions) (request: SessionOpenRequest) state (context: AgentContext<PlayerSessionMessage>) reply =
        match state.Phase with
        | Reserving player ->
            match reply, reliable context with
            | IdentityAdmission.Reserved pseudonym, Some address ->
                // Decided before anyone hears of this player: the first Joined already hides the names.
                state.Pseudonym <- pseudonym
                state.Hiding <- if pseudonym.IsSome then request.Hiding else HiddenIdentity.Shown
                state.Phase <- Opening { Player = player; Chat = None; System = None; Online = None; Buffered = ResizeArray() }
                let chat = {
                    ConnectionId = request.ConnectionId
                    Profile = player.Data
                    Events = address.Map PlayerSessionMessage.ChatEvent
                }
                let marks = {
                    ConnectionId = request.ConnectionId
                    Profile = player.Data
                    Events = address.Map PlayerSessionMessage.GroundMarkEvent
                }
                let presence = {
                    ConnectionId = request.ConnectionId
                    Snapshot = publicSnapshot state player
                    Events = address.Map PlayerSessionMessage.PresenceEvent
                }
                state.ChatAttached <- state.Chat.TrySend(context, ChatRoomCommand.Join chat)
                state.SystemAttached <- state.System.TrySend(context, ChatRoomCommand.Join chat)
                state.PresenceAttached <- state.Presence.TrySend(context, PresenceCommand.Join presence)
                state.GroundMarksAttached <- state.GroundMarks.TrySend(context, GroundMarkCommand.Join marks)

                if not state.ChatAttached || not state.SystemAttached || not state.PresenceAttached || not state.GroundMarksAttached then
                    close request state context "Subscription admission failed."
            | IdentityAdmission.AlreadyInUse, _ ->
                rejectOpening options request state context RequestRejectionCode.SessionAlreadyOpen "Player already has a session."
            | IdentityAdmission.Closed, _ | IdentityAdmission.Reserved _, None ->
                state.Logger.LogDebug("Session {ConnectionId} of player {PlayerId} lost its reservation; stopping", request.ConnectionId, accountId state)
                stop request state context
        | Starting | Resolving _ | Opening _ | Active _ | Closing -> ()

    let private activate (options: PlayerSessionOptions) (request: SessionOpenRequest) state context =
        match state.Phase with
        | Opening opening ->
            match opening.Chat, opening.System, opening.Online with
            | Some chat, Some system, Some (online, kinds) ->
                let channel (snapshot: ChatSnapshot) = { ChannelId = snapshot.ChannelId; Kind = snapshot.Kind; Messages = snapshot.Messages }
                let welcome = {
                    SelfPlayerId = opening.Player.Data.PlayerId
                    Players = online
                    Kinds = kinds
                    Channels = [ channel chat; channel system ]
                    AnnouncementSources = AnnouncementOptions.allowedSources state.Settings.Announcements
                    OwnPseudonym = state.Pseudonym
                    Hiding = state.Hiding
                    Mute = state.Mute |> ValueOption.filter (Sanction.activeAt DateTimeOffset.UtcNow)
                    Role = state.Role
                }
                if emit options request state context (SessionHostCommand.Activate(request.ConnectionId, request.RequestId, welcome)) then
                    state.Phase <- Active opening.Player
                    for response in opening.Buffered do
                        match state.Phase with
                        | Active _ -> send options request state context response
                        | Starting | Resolving _ | Reserving _ | Opening _ | Closing -> ()
            | None, _, _ | _, None, _ | _, _, None -> ()
        | Starting | Resolving _ | Reserving _ | Active _ | Closing -> ()

    let private publish (options: PlayerSessionOptions) (request: SessionOpenRequest) state context response =
        match state.Phase with
        | Opening opening ->
            if opening.Buffered.Count >= options.MaxBootstrapEvents then
                close request state context "Opening event buffer is full."
            else
                opening.Buffered.Add response
        | Active _ -> send options request state context response
        | Starting | Resolving _ | Reserving _ | Closing -> ()

    let private chatEvent (options: PlayerSessionOptions) (request: SessionOpenRequest) state context event =
        match event with
        | ChatRoomEvent.Joined snapshot ->
            match state.Phase, snapshot.Kind with
            | Opening opening, ChatChannelKind.Global when opening.Chat.IsNone ->
                opening.Chat <- Some snapshot
                activate options request state context
            | Opening opening, ChatChannelKind.System when opening.System.IsNone ->
                opening.System <- Some snapshot
                activate options request state context
            | Closing, _ -> ()
            | (Starting | Resolving _ | Reserving _ | Opening _ | Active _), _ ->
                close request state context "Unexpected chat snapshot."
        | ChatRoomEvent.JoinFailed reason ->
            match state.Phase with
            | Closing -> ()
            | Starting | Resolving _ | Reserving _ | Opening _ | Active _ ->
                rejectOpening options request state context RequestRejectionCode.SessionNotReady reason
        | ChatRoomEvent.Published message ->
            publish options request state context (ServerResponse.ChatPublished message)
        | ChatRoomEvent.Accepted(requestId, message) ->
            match state.Phase with
            | Active _ when state.Pending.Remove requestId ->
                send options request state context (ServerResponse.ChatAccepted(requestId, message))
            | Closing -> ()
            | Starting | Resolving _ | Reserving _ | Opening _ | Active _ ->
                close request state context "Unexpected chat acceptance."
        | ChatRoomEvent.Rejected(requestId, rejection) ->
            match state.Phase with
            | Active _ when state.Pending.Remove requestId ->
                sendRefusal options request state context DeliveryLane.Chat requestId rejection
            | Closing -> ()
            | Starting | Resolving _ | Reserving _ | Opening _ | Active _ ->
                close request state context "Unexpected chat rejection."
        | ChatRoomEvent.Removed(ValueNone, message) ->
            publish options request state context (ServerResponse.ChatMessageRemoved(ValueNone, message.ChannelId, message.MessageId))
        | ChatRoomEvent.Removed(ValueSome requestId, message) ->
            match state.Phase with
            | Active _ when state.Pending.Remove requestId ->
                send options request state context (ServerResponse.ChatMessageRemoved(ValueSome requestId, message.ChannelId, message.MessageId))
                message.Author |> ValueOption.iter (fun author ->
                    audit state context AdminAction.DeletedChatMessage author.PlayerId (ChatMessageText.value message.MessageText))
            | Closing -> ()
            | Starting | Resolving _ | Reserving _ | Opening _ | Active _ ->
                close request state context "Unexpected chat removal."

    let private presenceEvent (options: PlayerSessionOptions) (request: SessionOpenRequest) state context event =
        match event with
        | PresenceEvent.Snapshot(players, kinds) ->
            match state.Phase with
            | Opening opening when opening.Online.IsNone ->
                opening.Online <- Some (players |> List.map (ownView state opening.Player), kinds)
                activate options request state context
            | Closing -> ()
            | Starting | Resolving _ | Reserving _ | Opening _ | Active _ ->
                close request state context "Unexpected presence snapshot."
        | PresenceEvent.Changed(change, kinds) ->
            let own = restoreOwn state
            let change = { change with Joined = List.map own change.Joined; Updated = List.map own change.Updated }
            publish options request state context (ServerResponse.PresenceChanged(change, kinds))
        | PresenceEvent.Moved movements ->
            // Early realtime can be dropped: opening owns a reliable baseline and
            // the next period repeats all currently visible samples.
            match state.Phase with
            | Active _ when state.Host.Count < options.MaxPendingOutput ->
                state.Host.TrySend(context, SessionHostCommand.Send(request.ConnectionId, ServerResponse.PlayersMoved movements)) |> ignore
            | Starting | Resolving _ | Reserving _ | Opening _ | Active _ | Closing -> ()

    let private groundMarkEvent (options: PlayerSessionOptions) (request: SessionOpenRequest) state context event =
        let settle requestId reply =
            match state.Phase with
            | Active _ when state.Pending.Remove requestId -> reply ()
            | Closing -> ()
            | Starting | Resolving _ | Reserving _ | Opening _ | Active _ ->
                close request state context "Unexpected ground mark reply."
        match event with
        | GroundMarkEvent.Changed view -> publish options request state context (ServerResponse.GroundMarksChanged view)
        | GroundMarkEvent.Own records -> publish options request state context (ServerResponse.OwnGroundMarks records)
        | GroundMarkEvent.Placed(requestId, record, evicted) ->
            settle requestId (fun () -> send options request state context (ServerResponse.GroundMarkPlaced(requestId, record, evicted)))
        | GroundMarkEvent.Removed(requestId, id, author) ->
            settle requestId (fun () ->
                send options request state context (ServerResponse.GroundMarkRemoved(requestId, id))
                if selfId state <> ValueSome author then
                    audit state context AdminAction.RemovedGroundMark author $"mark {GroundMarkId.value id}")
        | GroundMarkEvent.Rejected(requestId, rejection) ->
            settle requestId (fun () -> sendRefusal options request state context DeliveryLane.Control requestId rejection)
        | GroundMarkEvent.AuthorMarks(requestId, author, records) ->
            settle requestId (fun () -> send options request state context (ServerResponse.PlayerMarks(requestId, author, records)))
        | GroundMarkEvent.Cleared(requestId, author, ids) ->
            settle requestId (fun () ->
                send options request state context (ServerResponse.PlayerMarksCleared(requestId, author, ids.Length))
                if not ids.IsEmpty then audit state context AdminAction.ClearedGroundMarks author $"{ids.Length} marks")

    // Visibility of marks follows the player's own position; a lost update is
    // repaired by the next one, since the owner compares with what it last saw.
    let private observeMarks (options: PlayerSessionOptions) (request: SessionOpenRequest) state context (player: Player) =
        if state.GroundMarks.Count < options.MaxPendingUpdates then
            state.GroundMarks.TrySend(context, GroundMarkCommand.Observe(request.ConnectionId, player.CharacterGeneration, player.Location)) |> ignore

    /// Position rule (soft), word list and flags here; quotas, frequency,
    /// density and the ID belong to the mark owner, which also answers.
    let private placeMark (options: PlayerSessionOptions) (request: SessionOpenRequest) state context requestId (body: GroundMarkBody) placement gameDate =
        let refuse code message field =
            sendRefusal options request state context DeliveryLane.Control requestId { Code = code; Message = message; Field = field }
        match state.Phase with
        | Active player ->
            let text = GroundMarkBody.text body
            if requestId = 0UL || state.Pending.Contains requestId then
                close request state context "Request ID is invalid or already pending."
            // A death mark is the game's own text: a mute does not stop it.
            elif GroundMarkBody.kind body = GroundMarkKind.Note && muted state then
                sendRefusal options request state context DeliveryLane.Control requestId mutedRejection
            elif state.Pending.Count >= options.MaxPendingChat then
                refuse RequestRejectionCode.Overloaded "Too many pending requests." ""
            elif not (GroundMarkPlacement.isNear state.Settings.GroundMarkRules.MaxPlacementDistance player.Location placement) then
                refuse RequestRejectionCode.InvalidRequest "The mark is not where the player is." "placement"
            elif not (Moderation.allows state.Moderation text) then
                refuse RequestRejectionCode.TextNotAllowed "Mark contains words that are not allowed." "text"
            else
                let submission = {
                    ConnectionId = request.ConnectionId
                    RequestId = requestId
                    Body = body
                    Placement = placement
                    GameDate = gameDate
                    CharacterName = markCharacterName state player
                    Pseudonym = markPseudonym state
                    Fingerprint = Moderation.normalize text
                    Flagged = Moderation.flag state.Moderation text
                }
                if state.GroundMarks.TrySend(context, GroundMarkCommand.Place submission) then
                    state.Pending.Add requestId |> ignore
                else
                    refuse RequestRejectionCode.Overloaded "Ground mark admission is full." ""
        | Closing -> ()
        | Starting | Resolving _ | Reserving _ | Opening _ ->
            refuse RequestRejectionCode.SessionNotReady "Session is not ready." ""

    let private removeMark (options: PlayerSessionOptions) (request: SessionOpenRequest) state context requestId id =
        match state.Phase with
        | Active _ ->
            if requestId = 0UL || state.Pending.Contains requestId then
                close request state context "Request ID is invalid or already pending."
            elif state.Pending.Count >= options.MaxPendingChat then
                reject options request state context requestId RequestRejectionCode.Overloaded "Too many pending requests."
            // A moderator removes any mark; the owner answers with its author for the audit.
            elif state.GroundMarks.TrySend(context, GroundMarkCommand.Remove(request.ConnectionId, requestId, id, state.Role = PlayerRole.Moderator)) then
                state.Pending.Add requestId |> ignore
            else
                reject options request state context requestId RequestRejectionCode.Overloaded "Ground mark admission is full."
        | Closing -> ()
        | Starting | Resolving _ | Reserving _ | Opening _ ->
            reject options request state context requestId RequestRejectionCode.SessionNotReady "Session is not ready."

    let private sendChat (options: PlayerSessionOptions) (request: SessionOpenRequest) state context requestId channelId text =
        match state.Phase, reliable context with
        | Active player, Some address ->
            let kind = ChatChannelKind.tryOfChannelId channelId
            if requestId = 0UL || state.Pending.Contains requestId then
                // A second reply with this ID could settle the original request.
                close request state context "Request ID is invalid or already pending."
            elif muted state then
                sendRefusal options request state context DeliveryLane.Chat requestId mutedRejection
            elif kind.IsNone then
                rejectChat options request state context requestId RequestRejectionCode.ChannelNotFound "Channel does not exist."
            elif ChatChannelKind.carriesAnnouncements kind.Value then
                rejectChat options request state context requestId RequestRejectionCode.InvalidRequest "The system channel is read-only."
            elif state.Pending.Count >= options.MaxPendingChat then
                rejectChat options request state context requestId RequestRejectionCode.Overloaded "Too many pending chat requests."
            elif not (Moderation.allows state.Moderation (ChatMessageText.value text)) then
                // Refused before the channel sees it: nothing is stored or relayed.
                let rejection = { Code = RequestRejectionCode.TextNotAllowed; Message = "Message contains words that are not allowed."; Field = "text" }
                sendRefusal options request state context DeliveryLane.Chat requestId rejection
            else
                let submission = {
                    ConnectionId = request.ConnectionId
                    RequestId = requestId
                    Author = publicIdentity state player
                    Text = text
                    CharacterName = publicCharacterName state player
                    Fingerprint = Moderation.normalize (ChatMessageText.value text)
                    Flagged = Moderation.flag state.Moderation (ChatMessageText.value text)
                    Announcement = ValueNone
                    ReplyTo = address.Map PlayerSessionMessage.ChatEvent
                }
                if state.Chat.TrySend(context, ChatRoomCommand.Publish submission) then
                    state.Pending.Add requestId |> ignore
                else
                    rejectChat options request state context requestId RequestRejectionCode.Overloaded "Channel admission is full."
        | Closing, _ -> ()
        | Starting, _ | Resolving _, _ | Reserving _, _ | Opening _, _ | Active _, None ->
            rejectChat options request state context requestId RequestRejectionCode.SessionNotReady "Session is not ready."

    /// Same path as chat into the system channel: origin admission here, then the
    /// word list before the channel sees anything; the channel applies its rate limit.
    let private postAnnouncement (options: PlayerSessionOptions) (request: SessionOpenRequest) state context requestId (announcement: AnnouncementRequest) =
        let refuse code message field =
            sendRefusal options request state context DeliveryLane.Chat requestId { Code = code; Message = message; Field = field }
        match state.Phase, reliable context with
        | Active player, Some address ->
            let text = ChatMessageText.value announcement.Text
            let signature = announcement.Signature |> ValueOption.map AnnouncementSignature.value
            if requestId = 0UL || state.Pending.Contains requestId then
                close request state context "Request ID is invalid or already pending."
            elif muted state then
                sendRefusal options request state context DeliveryLane.Chat requestId mutedRejection
            elif ChatChannelKind.tryOfChannelId announcement.ChannelId <> Some ChatChannelKind.System then
                refuse RequestRejectionCode.InvalidRequest "Announcements are published only in the system channel." "channel_id"
            else
                match AnnouncementOptions.admit state.Settings.Announcements announcement with
                | Error rejection -> sendRefusal options request state context DeliveryLane.Chat requestId rejection
                | Ok () ->
                    if state.Pending.Count >= options.MaxPendingChat then
                        refuse RequestRejectionCode.Overloaded "Too many pending chat requests." ""
                    elif not (Moderation.allows state.Moderation text) then
                        refuse RequestRejectionCode.TextNotAllowed "Announcement contains words that are not allowed." "text"
                    elif signature |> ValueOption.exists (fun label -> not (Moderation.allows state.Moderation label)) then
                        refuse RequestRejectionCode.TextNotAllowed "Announcement source contains words that are not allowed." "source"
                    else
                        let submission = {
                            ConnectionId = request.ConnectionId
                            RequestId = requestId
                            Author = publicIdentity state player
                            Text = announcement.Text
                            CharacterName = publicCharacterName state player
                            Fingerprint = Moderation.normalize text
                            Flagged = Moderation.flag state.Moderation text
                            Announcement = ValueSome(Announcement.fromClient announcement.Source announcement.Kind announcement.Signature)
                            ReplyTo = address.Map PlayerSessionMessage.ChatEvent
                        }
                        if state.System.TrySend(context, ChatRoomCommand.Publish submission) then
                            state.Pending.Add requestId |> ignore
                        else
                            refuse RequestRejectionCode.Overloaded "Channel admission is full." ""
        | Closing, _ -> ()
        | Starting, _ | Resolving _, _ | Reserving _, _ | Opening _, _ | Active _, None ->
            refuse RequestRejectionCode.SessionNotReady "Session is not ready." ""

    let private validUpdate maxActorValues command (player: Player) =
        match command with
        | PlayerUpdate.BeginCharacter _ | PlayerUpdate.LeaveGame -> true
        | PlayerUpdate.RenameCharacter _ -> player.CharacterName.IsSome
        | PlayerUpdate.SetDetails details ->
            player.CharacterName.IsSome || (details.Race.IsNone && details.Level.IsNone)
        | PlayerUpdate.SetLocation(revision, _) ->
            player.CharacterName.IsSome && revision > player.MovementHighWater
        | PlayerUpdate.SetActorValues values ->
            player.CharacterName.IsSome && values.Count <= maxActorValues

    let private update (options: PlayerSessionOptions) maxActorValues (request: SessionOpenRequest) state context requestId command =
        match state.Phase with
        | Active player ->
            if requestId = 0UL || state.Pending.Contains requestId then
                close request state context "Request ID is invalid or already pending."
            elif not (validUpdate maxActorValues command player) then
                reject options request state context requestId RequestRejectionCode.InvalidRequest "Player update is invalid for the current state."
            elif state.Presence.Count >= options.MaxPendingUpdates then
                reject options request state context requestId RequestRejectionCode.Overloaded "Player update admission is full."
            else
                let updated = Player.applyUpdate command player
                let withheld =
                    match command with
                    | PlayerUpdate.BeginCharacter name | PlayerUpdate.RenameCharacter name ->
                        // The game save keeps its name; only publication is refused.
                        not (Moderation.allows state.Moderation (CharacterName.value name))
                    | PlayerUpdate.LeaveGame -> false
                    | PlayerUpdate.SetLocation _ | PlayerUpdate.SetActorValues _ | PlayerUpdate.SetDetails _ -> state.CharacterWithheld
                let previous = state.CharacterWithheld
                state.CharacterWithheld <- withheld
                if withheld && not previous then
                    state.Logger.LogInformation("Player {PlayerId}: the character name is withheld by the word list", accountId state)
                let change = PresenceCommand.Update(request.ConnectionId, publicSnapshot state updated)
                if state.Presence.TrySend(context, change) then
                    state.Phase <- Active updated
                    match command with
                    | PlayerUpdate.BeginCharacter _ | PlayerUpdate.LeaveGame | PlayerUpdate.SetLocation _ ->
                        observeMarks options request state context updated
                    | PlayerUpdate.RenameCharacter _ | PlayerUpdate.SetActorValues _ | PlayerUpdate.SetDetails _ -> ()
                    // This settles the request only. Author and observers apply the
                    // same coalesced presence update to their local models later.
                    send options request state context (ServerResponse.PlayerUpdateAccepted requestId)
                else
                    state.CharacterWithheld <- previous
                    reject options request state context requestId RequestRejectionCode.Overloaded "Player update admission is full."
        | Closing -> ()
        | Starting | Resolving _ | Reserving _ | Opening _ ->
            reject options request state context requestId RequestRejectionCode.SessionNotReady "Session is not ready."

    /// Only the choice comes from the client; the runtime picks the pseudonym.
    /// Asking for the current state settles at once and is not a switch.
    let private setIdentity (options: PlayerSessionOptions) (request: SessionOpenRequest) state context requestId hiding =
        let refuse code message =
            sendRefusal options request state context DeliveryLane.Control requestId { Code = code; Message = message; Field = "hidden" }
        match state.Phase, reliable context with
        | Active _, Some address ->
            let now = Environment.TickCount64
            if requestId = 0UL || state.Pending.Contains requestId then
                close request state context "Request ID is invalid or already pending."
            elif HiddenIdentity.isHidden hiding && not state.Settings.Identity.AllowHiddenIdentity then
                refuse RequestRejectionCode.HiddenIdentityNotAllowed "The server does not let players hide their names."
            elif state.IdentityRequest.IsSome then
                refuse RequestRejectionCode.Overloaded "An identity change is already pending."
            elif hiding = state.Hiding then
                send options request state context (ServerResponse.IdentityVisibilityChanged(requestId, state.Pseudonym, state.Hiding))
            elif state.LastIdentitySwitch |> ValueOption.exists (fun last -> now - last < int64 state.Settings.Identity.ToggleIntervalMs) then
                refuse RequestRejectionCode.RateLimited "Identity visibility was changed too recently."
            else
                let reply = address.Map(fun pseudonym -> PlayerSessionMessage.IdentityChanged(requestId, pseudonym))
                if emit options request state context (SessionHostCommand.ChangeIdentity(request.ConnectionId, hiding, reply)) then
                    state.Pending.Add requestId |> ignore
                    state.IdentityRequest <- ValueSome(struct (requestId, hiding))
                    state.LastIdentitySwitch <- ValueSome now
        | Closing, _ -> ()
        | Starting, _ | Resolving _, _ | Reserving _, _ | Opening _, _ | Active _, None ->
            refuse RequestRejectionCode.SessionNotReady "Session is not ready."

    /// Messages and marks sent from now on carry the new identity; presence
    /// spreads it like a rename. The presence outbox keeps two slots beyond
    /// the update budget, so this send only fails when the owner is gone.
    let private identityChanged (options: PlayerSessionOptions) (request: SessionOpenRequest) state context requestId pseudonym =
        match state.Phase with
        | Active player when (match state.IdentityRequest with ValueSome(struct (id, _)) -> id = requestId | ValueNone -> false)
                             && state.Pending.Remove requestId ->
            let struct (_, hiding) = state.IdentityRequest.Value
            state.IdentityRequest <- ValueNone
            state.Pseudonym <- pseudonym
            state.Hiding <- if pseudonym.IsSome then hiding else HiddenIdentity.Shown
            if state.Presence.TrySend(context, PresenceCommand.Update(request.ConnectionId, publicSnapshot state player)) then
                send options request state context (ServerResponse.IdentityVisibilityChanged(requestId, pseudonym, state.Hiding))
            else
                close request state context "Presence admission failed."
        | Closing -> ()
        | Starting | Resolving _ | Reserving _ | Opening _ | Active _ ->
            close request state context "Unexpected identity change."

    /// Same path as a pseudonym switch: the host updates the names shown online,
    /// presence spreads the new identity as an updated player. own: the player's own
    /// change rather than an administrator's.
    let private profileChanged (options: PlayerSessionOptions) (request: SessionOpenRequest) state context own (stored: PlayerData) =
        let apply (player: Player) =
            match Player.withProfile (Moderation.publicProfile state.Moderation stored) player with
            | Error error ->
                state.Logger.LogWarning("Player {PlayerId}: the changed profile cannot be applied ({Error})", accountId state, error)
                None
            | Ok updated ->
                state.Account <- ValueSome stored
                if emit options request state context (SessionHostCommand.UpdateProfile(request.ConnectionId, updated.Data, own)) then
                    if not (state.Presence.TrySend(context, PresenceCommand.Update(request.ConnectionId, publicSnapshot state updated))) then
                        close request state context "Presence admission failed."
                    // Marks without a pseudonym show the author's current profile.
                    state.GroundMarks.TrySend(context, GroundMarkCommand.Rename(request.ConnectionId, updated.Data)) |> ignore
                Some updated
        match state.Phase with
        | Opening opening when state.Account <> ValueSome stored ->
            apply opening.Player |> Option.iter (fun updated ->
                match state.Phase with
                | Opening current -> state.Phase <- Opening { current with Player = updated }
                | Starting | Resolving _ | Reserving _ | Active _ | Closing -> ())
        | Active player when state.Account <> ValueSome stored ->
            apply player |> Option.iter (fun updated ->
                match state.Phase with
                | Active _ -> state.Phase <- Active updated
                | Starting | Resolving _ | Reserving _ | Opening _ | Closing -> ())
        | Starting | Resolving _ | Reserving _ | Opening _ | Active _ | Closing -> ()

    /// Only the new name comes from the client. The word list is checked here,
    /// like chat text; storage and the change interval belong to the account service.
    let private changeDisplayName (options: PlayerSessionOptions) (request: SessionOpenRequest) state context requestId (name: DisplayName) =
        let refuse code message =
            sendRefusal options request state context DeliveryLane.Control requestId { Code = code; Message = message; Field = "display_name" }
        match state.Phase, reliable context with
        | Active player, Some address ->
            if requestId = 0UL || state.Pending.Contains requestId then
                close request state context "Request ID is invalid or already pending."
            elif muted state then
                sendRefusal options request state context DeliveryLane.Control requestId mutedRejection
            elif not state.Settings.Identity.AllowDisplayNameChange then
                refuse RequestRejectionCode.DisplayNameChangeNotAllowed "The server does not let players change their display name."
            elif state.NameRequest.IsSome then
                refuse RequestRejectionCode.Overloaded "A display name change is already pending."
            elif state.Account |> ValueOption.exists (fun stored -> stored.DisplayName = name) then
                // Asking for the current name settles at once and is not a change.
                send options request state context (ServerResponse.DisplayNameChanged(requestId, name))
            elif not (Moderation.allows state.Moderation (DisplayName.value name)) then
                refuse RequestRejectionCode.TextNotAllowed "Name contains words that are not allowed."
            else
                let operationId = Guid.NewGuid()
                let change = {
                    OperationId = operationId
                    PlayerId = player.Data.PlayerId
                    DisplayName = name
                    MinInterval = IdentityOptions.displayNameInterval state.Settings.Identity
                    ReplyTo = address.Map PlayerSessionMessage.DisplayNameReplied
                }
                if state.DisplayNames.TrySend(context, change) then
                    state.Pending.Add requestId |> ignore
                    state.NameRequest <- ValueSome(struct (requestId, operationId))
                else
                    refuse RequestRejectionCode.Overloaded "Display name admission is full."
        | Closing, _ -> ()
        | Starting, _ | Resolving _, _ | Reserving _, _ | Opening _, _ | Active _, None ->
            refuse RequestRejectionCode.SessionNotReady "Session is not ready."

    /// The role is checked here; whether a moderator outranks the target is the
    /// account service's to decide against the stored roles.
    let private moderate (options: PlayerSessionOptions) (request: SessionOpenRequest) state context requestId (action: ModerationAction) =
        let lane =
            match action with
            | ModerationAction.DeleteMessage _ -> DeliveryLane.Chat
            | ModerationAction.Sanction _ | ModerationAction.Lift _ | ModerationAction.Kick _ | ModerationAction.ListSanctions
            | ModerationAction.ListMarks _ | ModerationAction.ClearMarks _ -> DeliveryLane.Control
        let refuse code message = sendRefusal options request state context lane requestId { Code = code; Message = message; Field = "" }
        let admitted sent = if sent then state.Pending.Add requestId |> ignore else refuse RequestRejectionCode.Overloaded "Moderation admission is full."
        match state.Phase, reliable context with
        | Active player, Some address ->
            let self = player.Data.PlayerId
            let ask command =
                let operationId = Guid.NewGuid()
                let sent = state.AccountModeration.TrySend(context, { OperationId = operationId; Command = command; ReplyTo = address.Map PlayerSessionMessage.ModerationReplied })
                if sent then state.ModerationRequests[operationId] <- struct (requestId, action)
                admitted sent
            if requestId = 0UL || state.Pending.Contains requestId then
                close request state context "Request ID is invalid or already pending."
            elif state.Role <> PlayerRole.Moderator then
                refuse RequestRejectionCode.NotPermitted "Only a moderator may do this."
            elif state.Pending.Count >= options.MaxPendingChat then
                refuse RequestRejectionCode.Overloaded "Too many pending requests."
            else
                match action with
                | ModerationAction.Sanction(target, kind, term, reason) ->
                    ask (ModerationCommand.Sanction { Target = target; Kind = kind; Term = term; Reason = reason; IssuedBy = SanctionIssuer.Moderator self })
                | ModerationAction.Lift(target, kind) -> ask (ModerationCommand.Lift(target, kind, self))
                | ModerationAction.Kick(target, reason) -> ask (ModerationCommand.Kick(target, reason, self))
                | ModerationAction.ListSanctions -> ask ModerationCommand.ListSanctions
                | ModerationAction.ListMarks target ->
                    admitted (state.GroundMarks.TrySend(context, GroundMarkCommand.ListOf(request.ConnectionId, requestId, target)))
                | ModerationAction.ClearMarks(target, kinds) ->
                    admitted (state.GroundMarks.TrySend(context, GroundMarkCommand.ClearOf(request.ConnectionId, requestId, target, kinds)))
                | ModerationAction.DeleteMessage(channel, message) ->
                    let removal = { ConnectionId = request.ConnectionId; RequestId = requestId; MessageId = message; ReplyTo = address.Map PlayerSessionMessage.ChatEvent }
                    match ChatChannelKind.tryOfChannelId channel with
                    | Some ChatChannelKind.Global -> admitted (state.Chat.TrySend(context, ChatRoomCommand.Remove removal))
                    | Some ChatChannelKind.System -> admitted (state.System.TrySend(context, ChatRoomCommand.Remove removal))
                    | None -> refuse RequestRejectionCode.ChannelNotFound "Channel does not exist."
        | Closing, _ -> ()
        | Starting, _ | Resolving _, _ | Reserving _, _ | Opening _, _ | Active _, None ->
            refuse RequestRejectionCode.SessionNotReady "Session is not ready."

    let private moderationReplied (options: PlayerSessionOptions) (request: SessionOpenRequest) state context (reply: ModerationReply) =
        match state.ModerationRequests.TryGetValue reply.OperationId with
        | false, _ -> () // An audit line: nothing waits for it.
        | true, struct (requestId, action) ->
            state.ModerationRequests.Remove reply.OperationId |> ignore
            match state.Phase with
            | Active _ when state.Pending.Remove requestId ->
                let refuse code message =
                    sendRefusal options request state context DeliveryLane.Control requestId { Code = code; Message = message; Field = "" }
                match reply.Result, action with
                | Ok(ModerationResult.Sanctioned sanction), _ -> send options request state context (ServerResponse.SanctionIssued(requestId, sanction))
                | Ok(ModerationResult.Lifted sanction), _ ->
                    send options request state context (ServerResponse.SanctionLifted(requestId, sanction.Target, sanction.Kind))
                | Ok ModerationResult.Kicked, ModerationAction.Kick(target, _) -> send options request state context (ServerResponse.PlayerKicked(requestId, target))
                | Ok(ModerationResult.Sanctions sanctions), _ -> send options request state context (ServerResponse.SanctionList(requestId, sanctions))
                | Error(ModerationError.Refused SanctionError.NotAllowed), _ ->
                    refuse RequestRejectionCode.NotPermitted "A moderator acts only on players, not on moderators or themselves."
                | Error(ModerationError.Refused SanctionError.PlayerNotFound), _ -> refuse RequestRejectionCode.TargetNotFound "No such player."
                | Error(ModerationError.Refused SanctionError.NotActive), _ -> refuse RequestRejectionCode.TargetNotFound "No such sanction in force."
                | Error(ModerationError.Busy | ModerationError.Unavailable), _ -> refuse RequestRejectionCode.Overloaded "Moderation is busy; try again."
                | Ok(ModerationResult.Kicked | ModerationResult.Recorded), _ -> close request state context "Unexpected moderation reply."
            | Closing -> ()
            | Starting | Resolving _ | Reserving _ | Opening _ | Active _ -> close request state context "Unexpected moderation reply."

    let private displayNameReplied (options: PlayerSessionOptions) (request: SessionOpenRequest) state context (reply: DisplayNameChangeReply) =
        match state.NameRequest with
        | ValueSome(struct (requestId, operationId)) when operationId = reply.OperationId ->
            state.NameRequest <- ValueNone
            state.Pending.Remove requestId |> ignore
            let refuse code message =
                sendRefusal options request state context DeliveryLane.Control requestId { Code = code; Message = message; Field = "display_name" }
            match state.Phase, reply.Result with
            | Active _, Ok stored ->
                profileChanged options request state context true stored
                match state.Phase with
                | Active _ -> send options request state context (ServerResponse.DisplayNameChanged(requestId, stored.DisplayName))
                | Starting | Resolving _ | Reserving _ | Opening _ | Closing -> ()
            | Active _, Error (DisplayNameChangeError.TooSoon wait) ->
                let minutes = max 1 (int (ceil wait.TotalMinutes))
                refuse RequestRejectionCode.RateLimited $"The display name can be changed again in {minutes} min."
            | Active _, Error DisplayNameChangeError.Busy ->
                refuse RequestRejectionCode.Overloaded "The account service is busy. Try again later."
            | Active _, Error DisplayNameChangeError.Unavailable ->
                refuse RequestRejectionCode.Overloaded "The account service is unavailable. Try again later."
            | (Starting | Resolving _ | Reserving _ | Opening _ | Closing), _ -> ()
        | ValueSome _ | ValueNone -> ()

    let private describe state =
        let view (player: Player) phase =
            state.Account |> ValueOption.map (fun account ->
                AdminPlayerView.create account player state.CharacterWithheld state.Pseudonym state.Hiding state.Role phase state.OpenedAt)
            |> ValueOption.toOption
        match state.Phase with
        | Opening opening -> view opening.Player AdminSessionPhase.Opening
        | Active player -> view player AdminSessionPhase.Active
        | Starting | Resolving _ | Reserving _ | Closing -> None

    let private sampleMovement (options: PlayerSessionOptions) (request: SessionOpenRequest) state context sample =
        match state.Phase with
        | Active player when state.Presence.Count < options.MaxPendingUpdates ->
            match Player.tryApplyMovement sample player with
            | ValueSome updated ->
                let change = PresenceCommand.Update(request.ConnectionId, publicSnapshot state updated)
                if state.Presence.TrySend(context, change) then
                    state.Phase <- Active updated
                    observeMarks options request state context updated
            | ValueNone -> ()
        | Starting | Resolving _ | Reserving _ | Opening _ | Active _ | Closing -> ()

    let private handle (options: PlayerSessionOptions) maxActorValues (request: SessionOpenRequest) state (context: AgentContext<PlayerSessionMessage>) message = task {
        match message with
        | PlayerSessionMessage.Begin -> beginResolve options request state context
        | PlayerSessionMessage.Authenticated reply -> authenticated options request state context reply
        | PlayerSessionMessage.IdentityReplied reply -> identityReply options request state context reply
        | PlayerSessionMessage.IdentityChanged(requestId, pseudonym) -> identityChanged options request state context requestId pseudonym
        | PlayerSessionMessage.SetIdentityVisibility(requestId, hiding) -> setIdentity options request state context requestId hiding
        | PlayerSessionMessage.ChatEvent event -> chatEvent options request state context event
        | PlayerSessionMessage.PresenceEvent event -> presenceEvent options request state context event
        | PlayerSessionMessage.GroundMarkEvent event -> groundMarkEvent options request state context event
        | PlayerSessionMessage.ChatDetached connectionId ->
            if connectionId = request.ConnectionId then state.ChatAttached <- false
            completeIfDetached state context
        | PlayerSessionMessage.SystemDetached connectionId ->
            if connectionId = request.ConnectionId then state.SystemAttached <- false
            completeIfDetached state context
        | PlayerSessionMessage.PresenceDetached connectionId ->
            if connectionId = request.ConnectionId then state.PresenceAttached <- false
            completeIfDetached state context
        | PlayerSessionMessage.GroundMarksDetached connectionId ->
            if connectionId = request.ConnectionId then state.GroundMarksAttached <- false
            completeIfDetached state context
        | PlayerSessionMessage.SendChat(requestId, channelId, text) ->
            sendChat options request state context requestId channelId text
        | PlayerSessionMessage.PostAnnouncement(requestId, announcement) ->
            postAnnouncement options request state context requestId announcement
        | PlayerSessionMessage.PlaceGroundNote(requestId, text, placement, gameDate) ->
            placeMark options request state context requestId (GroundMarkBody.Note text) placement gameDate
        | PlayerSessionMessage.ReportDeath(requestId, label, placement, gameDate) ->
            placeMark options request state context requestId (GroundMarkBody.Death label) placement gameDate
        | PlayerSessionMessage.RemoveGroundMark(requestId, id) ->
            removeMark options request state context requestId id
        | PlayerSessionMessage.Update(requestId, command) ->
            update options maxActorValues request state context requestId command
        | PlayerSessionMessage.SampleMovement sample ->
            sampleMovement options request state context sample
        | PlayerSessionMessage.Read reply ->
            match state.Phase with
            | Active player -> reply.Reply(Ok (publicSnapshot state player))
            | Closing -> reply.Reply(Error PlayerStateError.Closed)
            | Starting | Resolving _ | Reserving _ | Opening _ -> reply.Reply(Error PlayerStateError.NotReady)
        | PlayerSessionMessage.RoleChanged role ->
            match state.Phase with
            // The welcome carries the role of an opening session.
            | Opening _ ->
                state.Logger.LogDebug("Player {PlayerId} (session {ConnectionId}) now has role {Role}", accountId state, request.ConnectionId, role)
                state.Role <- role
            | Active _ ->
                state.Logger.LogDebug("Player {PlayerId} (session {ConnectionId}) now has role {Role}", accountId state, request.ConnectionId, role)
                if state.Role <> role then
                    state.Role <- role
                    send options request state context (ServerResponse.RoleChanged role)
            | Starting | Resolving _ | Reserving _ | Closing -> ()
        | PlayerSessionMessage.Moderate(requestId, action) -> moderate options request state context requestId action
        | PlayerSessionMessage.ModerationReplied reply -> moderationReplied options request state context reply
        | PlayerSessionMessage.MuteChanged mute ->
            state.Logger.LogDebug("Player {PlayerId} (session {ConnectionId}) is muted: {Muted}", accountId state, request.ConnectionId, mute.IsSome)
            match state.Phase with
            // The welcome carries the mute of an opening session.
            | Resolving _ | Reserving _ | Opening _ -> state.Mute <- mute
            | Active _ ->
                state.Mute <- mute
                send options request state context (ServerResponse.MuteChanged mute)
            | Starting | Closing -> ()
        | PlayerSessionMessage.ProfileChanged stored -> profileChanged options request state context false stored
        | PlayerSessionMessage.ChangeDisplayName(requestId, name) -> changeDisplayName options request state context requestId name
        | PlayerSessionMessage.DisplayNameReplied reply -> displayNameReplied options request state context reply
        | PlayerSessionMessage.Describe reply -> reply.Reply(describe state)
        | PlayerSessionMessage.Stop -> stop request state context
    }

    let private isControl = function
        | PlayerSessionMessage.Begin | PlayerSessionMessage.Authenticated _
        | PlayerSessionMessage.IdentityReplied _ | PlayerSessionMessage.IdentityChanged _
        | PlayerSessionMessage.ChatDetached _ | PlayerSessionMessage.SystemDetached _
        | PlayerSessionMessage.PresenceDetached _ | PlayerSessionMessage.GroundMarksDetached _ | PlayerSessionMessage.Stop -> true
        // Rare administrator changes use the reserve so a busy session still applies them.
        | PlayerSessionMessage.RoleChanged _ | PlayerSessionMessage.ProfileChanged _ | PlayerSessionMessage.MuteChanged _ -> true
        // The account service must be able to settle a pending change.
        | PlayerSessionMessage.DisplayNameReplied _ | PlayerSessionMessage.ModerationReplied _ -> true
        | PlayerSessionMessage.GroundMarkEvent (GroundMarkEvent.Placed _ | GroundMarkEvent.Removed _ | GroundMarkEvent.Rejected _) -> true
        | PlayerSessionMessage.GroundMarkEvent (GroundMarkEvent.AuthorMarks _ | GroundMarkEvent.Cleared _) -> true
        | PlayerSessionMessage.GroundMarkEvent (GroundMarkEvent.Changed _ | GroundMarkEvent.Own _) -> false
        | PlayerSessionMessage.ChatEvent (ChatRoomEvent.Joined _)
        | PlayerSessionMessage.ChatEvent (ChatRoomEvent.JoinFailed _)
        | PlayerSessionMessage.ChatEvent (ChatRoomEvent.Accepted _)
        | PlayerSessionMessage.ChatEvent (ChatRoomEvent.Rejected _)
        | PlayerSessionMessage.ChatEvent (ChatRoomEvent.Removed(ValueSome _, _))
        | PlayerSessionMessage.PresenceEvent (PresenceEvent.Snapshot _) -> true
        | PlayerSessionMessage.ChatEvent (ChatRoomEvent.Published _)
        | PlayerSessionMessage.ChatEvent (ChatRoomEvent.Removed(ValueNone, _))
        | PlayerSessionMessage.PresenceEvent (PresenceEvent.Changed _ | PresenceEvent.Moved _)
        | PlayerSessionMessage.SendChat _ | PlayerSessionMessage.PostAnnouncement _
        | PlayerSessionMessage.PlaceGroundNote _ | PlayerSessionMessage.ReportDeath _ | PlayerSessionMessage.RemoveGroundMark _
        | PlayerSessionMessage.Update _ | PlayerSessionMessage.SampleMovement _ | PlayerSessionMessage.SetIdentityVisibility _
        | PlayerSessionMessage.Read _ | PlayerSessionMessage.Describe _ | PlayerSessionMessage.ChangeDisplayName _
        | PlayerSessionMessage.Moderate _ -> false

    /// chat and system are the owners of the global and the system channel; marks owns the ground marks.
    let start (settings: GameSettings) moderation authentication displayNames accountModeration chat system presence marks host (logger: ILogger) (request: SessionOpenRequest) =
        let options = settings.Runtime.Player
        let reserve = PlayerSessionOptions.OutboxReserve
        let state = {
            Phase = Starting
            ChatAttached = false
            SystemAttached = false
            PresenceAttached = false
            GroundMarksAttached = false
            CloseSent = false
            CharacterWithheld = false
            Pseudonym = ValueNone
            Hiding = HiddenIdentity.Shown
            IdentityRequest = ValueNone
            LastIdentitySwitch = ValueNone
            Account = ValueNone
            Role = PlayerRole.Player
            Mute = ValueNone
            OpenedAt = DateTimeOffset.UtcNow
            NameRequest = ValueNone
            Moderation = moderation
            Settings = settings
            Pending = HashSet()
            Authentication = AgentOutbox(1, authentication)
            DisplayNames = AgentOutbox(1, displayNames)
            AccountModeration = AgentOutbox(options.MaxPendingChat + reserve, accountModeration)
            ModerationRequests = Dictionary()
            Chat = AgentOutbox(options.MaxPendingChat + reserve, chat)
            System = AgentOutbox(options.MaxPendingChat + reserve, system)
            Presence = AgentOutbox(options.MaxPendingUpdates + reserve, presence)
            GroundMarks = AgentOutbox(options.MaxPendingUpdates + options.MaxPendingChat + reserve, marks)
            Host = AgentOutbox(options.MaxPendingOutput + reserve, host)
            Logger = logger
        }
        let agentOptions = {
            AgentOptions.create $"player-{request.ConnectionId}" with
                Mailbox = AgentMailbox.boundedWithControl options.MailboxCapacity options.ControlReserve
        }
        let agent = Agent.Start(agentOptions, handle options settings.Server.PlayerInput.MaxActorValues request state, isControl = isControl)
        agent.TryPost PlayerSessionMessage.Begin |> ignore
        agent
