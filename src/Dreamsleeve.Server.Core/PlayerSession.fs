namespace Dreamsleeve.Server.Core

open System
open System.Collections.Generic
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

[<RequireQualifiedAccess>]
type PlayerSessionMessage =
    | Begin
    | Authenticated of SessionAuthenticationReply
    | IdentityReplied of IdentityAdmission
    | ChatEvent of ChatRoomEvent
    | PresenceEvent of PresenceEvent
    | ChatDetached of Guid
    | SystemDetached of Guid
    | PresenceDetached of Guid
    | SendChat of requestId: uint64 * ChatChannelId * ChatMessageText
    | PostAnnouncement of requestId: uint64 * AnnouncementRequest
    | Update of requestId: uint64 * PlayerUpdate
    | SampleMovement of MovementSample
    | Read of ReplyChannel<Result<PlayerSnapshot, PlayerStateError>>
    | Stop

/// Owns one player's state, opening barrier and bounded request admission.
[<RequireQualifiedAccess>]
module PlayerSession =
    type private Opening = {
        Player: Player
        mutable Chat: ChatSnapshot option
        mutable System: ChatSnapshot option
        mutable Online: PlayerSnapshot list option
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
        mutable CloseSent: bool
        /// The current character name failed moderation and is not published.
        mutable CharacterWithheld: bool
        Moderation: ModerationRules
        Announcements: AnnouncementOptions
        Pending: HashSet<uint64>
        Authentication: AgentOutbox<SessionAuthenticationRequest>
        Chat: AgentOutbox<ChatRoomCommand>
        System: AgentOutbox<ChatRoomCommand>
        Presence: AgentOutbox<PresenceCommand>
        Host: AgentOutbox<SessionHostCommand>
    }

    let private reliable (context: AgentContext<PlayerSessionMessage>) = context.Ref.TryReliable()

    // Every outbound projection of this player passes here: a withheld game
    // name never reaches presence, bootstrap snapshots or chat messages.
    let private publicSnapshot state player =
        let snapshot = Player.snapshot player
        if state.CharacterWithheld then { snapshot with CharacterName = ValueNone; CharacterNameWithheld = true }
        else snapshot

    let private publicCharacterName state (player: Player) =
        if state.CharacterWithheld then ValueNone else player.CharacterName

    let private completeIfDetached state (context: AgentContext<PlayerSessionMessage>) =
        match state.Phase with
        | Closing when not state.ChatAttached && not state.SystemAttached && not state.PresenceAttached -> context.Complete() |> ignore
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

    let private reject (options: PlayerSessionOptions) (request: SessionOpenRequest) state context requestId code message =
        let rejection = { Code = code; Message = message; Field = "" }
        send options request state context (ServerResponse.RequestRejected(requestId, rejection))

    let private rejectChat (options: PlayerSessionOptions) (request: SessionOpenRequest) state context requestId code message =
        let rejection = { Code = code; Message = message; Field = "" }
        send options request state context (ServerResponse.ChatRejected(requestId, rejection))

    let private rejectOpening (options: PlayerSessionOptions) (request: SessionOpenRequest) state context code message =
        reject options request state context request.RequestId code message
        close request state context message

    let private beginResolve (request: SessionOpenRequest) state (context: AgentContext<PlayerSessionMessage>) =
        match state.Phase, reliable context with
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
                // Accounts created under older rules keep their stored names;
                // every copy leaving this session uses the moderated profile.
                let profile = Moderation.publicProfile state.Moderation stored
                state.Phase <- Reserving(Player.create profile)
                emit options request state context
                    (SessionHostCommand.Reserve(request.ConnectionId, profile.PlayerId,
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
            | IdentityAdmission.Reserved, Some address ->
                state.Phase <- Opening { Player = player; Chat = None; System = None; Online = None; Buffered = ResizeArray() }
                let chat = {
                    ConnectionId = request.ConnectionId
                    Profile = player.Data
                    Events = address.Map PlayerSessionMessage.ChatEvent
                }
                let presence = {
                    ConnectionId = request.ConnectionId
                    Snapshot = publicSnapshot state player
                    Events = address.Map PlayerSessionMessage.PresenceEvent
                }
                state.ChatAttached <- state.Chat.TrySend(context, ChatRoomCommand.Join chat)
                state.SystemAttached <- state.System.TrySend(context, ChatRoomCommand.Join chat)
                state.PresenceAttached <- state.Presence.TrySend(context, PresenceCommand.Join presence)

                if not state.ChatAttached || not state.SystemAttached || not state.PresenceAttached then
                    close request state context "Subscription admission failed."
            | IdentityAdmission.AlreadyInUse, _ ->
                rejectOpening options request state context RequestRejectionCode.SessionAlreadyOpen "Player already has a session."
            | IdentityAdmission.Closed, _ | IdentityAdmission.Reserved, None ->
                stop request state context
        | Starting | Resolving _ | Opening _ | Active _ | Closing -> ()

    let private activate (options: PlayerSessionOptions) (request: SessionOpenRequest) state context =
        match state.Phase with
        | Opening opening ->
            match opening.Chat, opening.System, opening.Online with
            | Some chat, Some system, Some online ->
                let channel (snapshot: ChatSnapshot) = { ChannelId = snapshot.ChannelId; Kind = snapshot.Kind; Messages = snapshot.Messages }
                let welcome = {
                    SelfPlayerId = opening.Player.Data.PlayerId
                    Players = online
                    Channels = [ channel chat; channel system ]
                    AnnouncementSources = AnnouncementOptions.allowedSources state.Announcements
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
                send options request state context (ServerResponse.ChatRejected(requestId, rejection))
            | Closing -> ()
            | Starting | Resolving _ | Reserving _ | Opening _ | Active _ ->
                close request state context "Unexpected chat rejection."

    let private presenceEvent (options: PlayerSessionOptions) (request: SessionOpenRequest) state context event =
        match event with
        | PresenceEvent.Snapshot players ->
            match state.Phase with
            | Opening opening when opening.Online.IsNone ->
                opening.Online <- Some players
                activate options request state context
            | Closing -> ()
            | Starting | Resolving _ | Reserving _ | Opening _ | Active _ ->
                close request state context "Unexpected presence snapshot."
        | PresenceEvent.Joined player -> publish options request state context (ServerResponse.PlayerJoined player)
        | PresenceEvent.Updated player -> publish options request state context (ServerResponse.PlayerUpdated player)
        | PresenceEvent.MetadataChanged(playerId, values, details) -> publish options request state context (ServerResponse.PlayerMetadataChanged(playerId, values, details))
        | PresenceEvent.VisibilityChanged change ->
            publish options request state context (ServerResponse.PlayerVisibilityChanged change)
        | PresenceEvent.Moved movements ->
            // Early realtime can be dropped: opening owns a reliable baseline and
            // the next period repeats all currently visible samples.
            match state.Phase with
            | Active _ when state.Host.Count < options.MaxPendingOutput ->
                state.Host.TrySend(context, SessionHostCommand.Send(request.ConnectionId, ServerResponse.PlayersMoved movements)) |> ignore
            | Starting | Resolving _ | Reserving _ | Opening _ | Active _ | Closing -> ()
        | PresenceEvent.Left playerId -> publish options request state context (ServerResponse.PlayerLeft playerId)

    let private sendChat (options: PlayerSessionOptions) (request: SessionOpenRequest) state context requestId channelId text =
        match state.Phase, reliable context with
        | Active player, Some address ->
            let kind = ChatChannelKind.tryOfChannelId channelId
            if requestId = 0UL || state.Pending.Contains requestId then
                // A second reply with this ID could settle the original request.
                close request state context "Request ID is invalid or already pending."
            elif kind.IsNone then
                rejectChat options request state context requestId RequestRejectionCode.ChannelNotFound "Channel does not exist."
            elif ChatChannelKind.carriesAnnouncements kind.Value then
                rejectChat options request state context requestId RequestRejectionCode.InvalidRequest "The system channel is read-only."
            elif state.Pending.Count >= options.MaxPendingChat then
                rejectChat options request state context requestId RequestRejectionCode.Overloaded "Too many pending chat requests."
            elif not (Moderation.allows state.Moderation (ChatMessageText.value text)) then
                // Refused before the channel sees it: nothing is stored or relayed.
                let rejection = { Code = RequestRejectionCode.TextNotAllowed; Message = "Message contains words that are not allowed."; Field = "text" }
                send options request state context (ServerResponse.ChatRejected(requestId, rejection))
            else
                let submission = {
                    ConnectionId = request.ConnectionId
                    RequestId = requestId
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
            send options request state context (ServerResponse.ChatRejected(requestId, { Code = code; Message = message; Field = field }))
        match state.Phase, reliable context with
        | Active player, Some address ->
            let text = ChatMessageText.value announcement.Text
            let signature = announcement.Signature |> ValueOption.map AnnouncementSignature.value
            if requestId = 0UL || state.Pending.Contains requestId then
                close request state context "Request ID is invalid or already pending."
            elif ChatChannelKind.tryOfChannelId announcement.ChannelId <> Some ChatChannelKind.System then
                refuse RequestRejectionCode.InvalidRequest "Announcements are published only in the system channel." "channel_id"
            else
                match AnnouncementOptions.admit state.Announcements announcement with
                | Error rejection -> send options request state context (ServerResponse.ChatRejected(requestId, rejection))
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
                let change = PresenceCommand.Update(request.ConnectionId, publicSnapshot state updated)
                if state.Presence.TrySend(context, change) then
                    state.Phase <- Active updated
                    // This settles the request only. Author and observers apply the
                    // same coalesced presence update to their local models later.
                    send options request state context (ServerResponse.PlayerUpdateAccepted requestId)
                else
                    state.CharacterWithheld <- previous
                    reject options request state context requestId RequestRejectionCode.Overloaded "Player update admission is full."
        | Closing -> ()
        | Starting | Resolving _ | Reserving _ | Opening _ ->
            reject options request state context requestId RequestRejectionCode.SessionNotReady "Session is not ready."

    let private sampleMovement (options: PlayerSessionOptions) (request: SessionOpenRequest) state context sample =
        match state.Phase with
        | Active player when state.Presence.Count < options.MaxPendingUpdates ->
            match Player.tryApplyMovement sample player with
            | ValueSome updated ->
                let change = PresenceCommand.Update(request.ConnectionId, publicSnapshot state updated)
                if state.Presence.TrySend(context, change) then
                    state.Phase <- Active updated
            | ValueNone -> ()
        | Starting | Resolving _ | Reserving _ | Opening _ | Active _ | Closing -> ()

    let private handle (options: PlayerSessionOptions) maxActorValues (request: SessionOpenRequest) state (context: AgentContext<PlayerSessionMessage>) message = task {
        match message with
        | PlayerSessionMessage.Begin -> beginResolve request state context
        | PlayerSessionMessage.Authenticated reply -> authenticated options request state context reply
        | PlayerSessionMessage.IdentityReplied reply -> identityReply options request state context reply
        | PlayerSessionMessage.ChatEvent event -> chatEvent options request state context event
        | PlayerSessionMessage.PresenceEvent event -> presenceEvent options request state context event
        | PlayerSessionMessage.ChatDetached connectionId ->
            if connectionId = request.ConnectionId then state.ChatAttached <- false
            completeIfDetached state context
        | PlayerSessionMessage.SystemDetached connectionId ->
            if connectionId = request.ConnectionId then state.SystemAttached <- false
            completeIfDetached state context
        | PlayerSessionMessage.PresenceDetached connectionId ->
            if connectionId = request.ConnectionId then state.PresenceAttached <- false
            completeIfDetached state context
        | PlayerSessionMessage.SendChat(requestId, channelId, text) ->
            sendChat options request state context requestId channelId text
        | PlayerSessionMessage.PostAnnouncement(requestId, announcement) ->
            postAnnouncement options request state context requestId announcement
        | PlayerSessionMessage.Update(requestId, command) ->
            update options maxActorValues request state context requestId command
        | PlayerSessionMessage.SampleMovement sample ->
            sampleMovement options request state context sample
        | PlayerSessionMessage.Read reply ->
            match state.Phase with
            | Active player -> reply.Reply(Ok (publicSnapshot state player))
            | Closing -> reply.Reply(Error PlayerStateError.Closed)
            | Starting | Resolving _ | Reserving _ | Opening _ -> reply.Reply(Error PlayerStateError.NotReady)
        | PlayerSessionMessage.Stop -> stop request state context
    }

    let private isControl = function
        | PlayerSessionMessage.Begin | PlayerSessionMessage.Authenticated _
        | PlayerSessionMessage.IdentityReplied _ | PlayerSessionMessage.ChatDetached _ | PlayerSessionMessage.SystemDetached _
        | PlayerSessionMessage.PresenceDetached _ | PlayerSessionMessage.Stop -> true
        | PlayerSessionMessage.ChatEvent (ChatRoomEvent.Joined _)
        | PlayerSessionMessage.ChatEvent (ChatRoomEvent.JoinFailed _)
        | PlayerSessionMessage.ChatEvent (ChatRoomEvent.Accepted _)
        | PlayerSessionMessage.ChatEvent (ChatRoomEvent.Rejected _)
        | PlayerSessionMessage.PresenceEvent (PresenceEvent.Snapshot _) -> true
        | PlayerSessionMessage.ChatEvent (ChatRoomEvent.Published _)
        | PlayerSessionMessage.PresenceEvent (PresenceEvent.Joined _ | PresenceEvent.Updated _ | PresenceEvent.Moved _ | PresenceEvent.VisibilityChanged _ | PresenceEvent.MetadataChanged _ | PresenceEvent.Left _)
        | PlayerSessionMessage.SendChat _ | PlayerSessionMessage.PostAnnouncement _
        | PlayerSessionMessage.Update _ | PlayerSessionMessage.SampleMovement _
        | PlayerSessionMessage.Read _ -> false

    /// chat and system are the owners of the global and the system channel.
    let start (options: PlayerSessionOptions) maxActorValues moderation announcements authentication chat system presence host (request: SessionOpenRequest) =
        let limits = [maxActorValues; options.MailboxCapacity; options.ControlReserve; options.MaxPendingChat; options.MaxPendingUpdates;
                      options.MaxBootstrapEvents; options.MaxPendingOutput]
        if limits |> List.exists (fun value -> value < 1) then
            Error "Session queue limits must be positive."
        elif int64 options.MailboxCapacity + int64 options.ControlReserve > int64 Int32.MaxValue
             || options.MaxPendingChat > Int32.MaxValue - 2 || options.MaxPendingUpdates > Int32.MaxValue - 2
             || options.MaxPendingOutput > Int32.MaxValue - 2 then
            Error "Session queue limits exceed Int32.MaxValue."
        else
            let state = {
                Phase = Starting
                ChatAttached = false
                SystemAttached = false
                PresenceAttached = false
                CloseSent = false
                CharacterWithheld = false
                Moderation = moderation
                Announcements = announcements
                Pending = HashSet()
                Authentication = AgentOutbox(1, authentication)
                Chat = AgentOutbox(options.MaxPendingChat + 2, chat)
                System = AgentOutbox(options.MaxPendingChat + 2, system)
                Presence = AgentOutbox(options.MaxPendingUpdates + 2, presence)
                Host = AgentOutbox(options.MaxPendingOutput + 2, host)
            }
            let settings = {
                AgentOptions.create $"player-{request.ConnectionId}" with
                    Mailbox = AgentMailbox.boundedWithControl options.MailboxCapacity options.ControlReserve
            }
            let agent = Agent.Start(settings, handle options maxActorValues request state, isControl = isControl)
            agent.TryPost PlayerSessionMessage.Begin |> ignore
            Ok agent
