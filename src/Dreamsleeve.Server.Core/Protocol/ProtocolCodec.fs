#nowarn "104" // Unknown enum numbers are guarded; FS0025 still checks every named case.

namespace Dreamsleeve.Server.Core

open System
open Google.Protobuf
open Dreamsleeve.Server.Domain

/// Validated settings, held unchanged for the network owner's lifetime.
type ProtocolCodec = private { Config: ServerConfig }

[<RequireQualifiedAccess>]
module ProtocolCodec =
    [<Literal>]
    let Version = 26u

    let private fail requestId failure =
        Error {
            RequestId = requestId
            Failure = failure
        }

    /// The settings come checked by GameSettings.create.
    let create (config: ServerConfig) = { Config = config }

    let private decodePayload (config: ServerConfig) (packet: Dreamsleeve.Protocol.Chat.ClientPacket) =
        let decoded =
            match packet.PayloadCase with
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.OpenSession ->
                SessionCodec.decodeTicket packet.OpenSession
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.SendChat ->
                ChatCodec.decodeCommand config.ChatInput.MessageText packet.SendChat
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.UpdatePlayer ->
                PlayerCodec.decodeUpdate config.PlayerInput packet.UpdatePlayer |> Result.map ClientCommand.UpdatePlayer
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.PostAnnouncement ->
                ChatCodec.decodeAnnouncement config.ChatInput packet.PostAnnouncement
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.PlaceGroundNote ->
                GroundMarkCodec.decodeNote config packet.PlaceGroundNote
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.ReportDeath ->
                GroundMarkCodec.decodeDeath config packet.ReportDeath
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.RemoveGroundMark ->
                GroundMarkCodec.decodeRemove packet.RemoveGroundMark
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.SetIdentityVisibility ->
                SessionCodec.decodeHiding "hidden" packet.SetIdentityVisibility.Hidden |> Result.map ClientCommand.SetIdentityVisibility
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.ChangeDisplayName ->
                SessionCodec.decodeDisplayName config packet.ChangeDisplayName
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.SetNameColor ->
                NameColor.create packet.SetNameColor.NameColor
                |> Result.map ClientCommand.SetNameColor
                |> Result.mapError ProtocolCodecFailure.InvalidDomain
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.JoinAsGuest -> Ok ClientCommand.JoinAsGuest
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.SanctionPlayer -> ModerationCodec.decodeSanction packet.SanctionPlayer
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.LiftSanction -> ModerationCodec.decodeLift packet.LiftSanction
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.KickPlayer -> ModerationCodec.decodeKick packet.KickPlayer
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.ListSanctions -> Ok ClientCommand.ListSanctions
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.ListPlayerMarks -> ModerationCodec.decodeListMarks packet.ListPlayerMarks
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.ClearPlayerMarks -> ModerationCodec.decodeClearMarks packet.ClearPlayerMarks
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.DeleteChatMessage -> ModerationCodec.decodeDeleteMessage packet.DeleteChatMessage
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.GuildCommand -> GuildCodec.decode packet.GuildCommand
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.None ->
                Error(ProtocolCodecFailure.InvalidPayload "payload")
            | unknown when not (Enum.IsDefined unknown) -> Error(ProtocolCodecFailure.InvalidPayload "payload")
        decoded
        |> Result.map (fun command -> {
            RequestId = packet.RequestId
            Command = command
        })
        |> Result.mapError (fun failure -> {
            RequestId = Some packet.RequestId
            Failure = failure
        })

    // Parse/validate before entering any stateful owner. Domain failures retain
    // correlation so the runtime can send a rejection without applying anything.
    let decodeClient (codec: ProtocolCodec) (bytes: byte array) : Result<ClientRequest, ProtocolCodecError> =
        let config = codec.Config

        if isNull bytes || bytes.Length = 0 then
            fail None ProtocolCodecFailure.EmptyPacket
        elif bytes.Length > config.MaxPacketBytes then
            fail None ProtocolCodecFailure.PacketTooLarge
        else
            try
                let packet = Dreamsleeve.Protocol.Chat.ClientPacket.Parser.ParseFrom(bytes)
                let requestId = if packet.RequestId = 0UL then None else Some packet.RequestId

                if packet.ProtocolVersion <> Version then
                    fail requestId (ProtocolCodecFailure.UnsupportedVersion packet.ProtocolVersion)
                elif packet.RequestId = 0UL then
                    fail None (ProtocolCodecFailure.InvalidEnvelope "request_id")
                else
                    decodePayload config packet
            with :? InvalidProtocolBufferException -> fail None ProtocolCodecFailure.MalformedPacket

    let requestLane (request: ClientRequest) =
        match request.Command with
        | ClientCommand.SendChat _ | ClientCommand.PostAnnouncement _ | ClientCommand.DeleteChatMessage _ -> DeliveryLane.Chat
        | ClientCommand.OpenSession _ | ClientCommand.JoinAsGuest | ClientCommand.UpdatePlayer _ | ClientCommand.SetIdentityVisibility _
        | ClientCommand.ChangeDisplayName _ | ClientCommand.SetNameColor _ | ClientCommand.PlaceGroundNote _ | ClientCommand.ReportDeath _
        | ClientCommand.RemoveGroundMark _ | ClientCommand.SanctionPlayer _ | ClientCommand.LiftSanction _ | ClientCommand.KickPlayer _ | ClientCommand.ListSanctions
        | ClientCommand.ListPlayerMarks _ | ClientCommand.ClearPlayerMarks _ | ClientCommand.Guild _ -> DeliveryLane.Control

    /// What the client hears for a request this codec refused: the wire field
    /// that was wrong and why, worded for the player.
    let rejection failure : RequestRejection =
        let invalid field message = {
            Code = RequestRejectionCode.InvalidRequest
            Message = message
            Field = field
        }
        match failure with
        | ProtocolCodecFailure.InvalidDomain(DomainError.InvalidText("ChatMessageText", TextError.TooLong maximum)) ->
            invalid "text" $"Message exceeds {maximum} characters."
        | ProtocolCodecFailure.InvalidDomain(DomainError.InvalidText("AnnouncementText", TextError.TooLong maximum)) ->
            invalid "text" $"Announcement exceeds {maximum} characters."
        | ProtocolCodecFailure.InvalidDomain(DomainError.InvalidText("AnnouncementSignature", _)) ->
            invalid "source" "Announcement source is missing, too long or contains control characters."
        | ProtocolCodecFailure.InvalidDomain(DomainError.InvalidText("GroundNoteText", TextError.TooLong maximum)) ->
            invalid "text" $"Note exceeds {maximum} characters."
        | ProtocolCodecFailure.InvalidDomain(DomainError.InvalidText("DisplayName", TextError.TooLong maximum)) ->
            invalid "display_name" $"Display name exceeds {maximum} characters."
        | ProtocolCodecFailure.InvalidDomain(DomainError.InvalidText("DisplayName", (TextError.Missing | TextError.InvalidUnicode | TextError.InvalidCharacters | TextError.InvalidFormat))) ->
            invalid "display_name" "Display name is empty or contains control characters."
        | ProtocolCodecFailure.InvalidDomain(DomainError.InvalidText("DeathMarkText", _)) ->
            invalid "text" "Death label is too long or contains control characters."
        | ProtocolCodecFailure.InvalidDomain(DomainError.InvalidText("SanctionReason", _)) ->
            invalid "reason" $"A reason of one line up to {SanctionReason.MaxLength} characters is required."
        | ProtocolCodecFailure.InvalidDomain(DomainError.InvalidLimit("SanctionTerm", _)) ->
            invalid "minutes" "A term runs from a minute to ten years."
        | ProtocolCodecFailure.InvalidPayload "kinds" -> invalid "kinds" "Choose notes, death marks or both."
        | ProtocolCodecFailure.InvalidPayload "role" -> invalid "role" "A guild role is member or officer."
        | ProtocolCodecFailure.InvalidDomain(DomainError.InvalidId "GuildId") -> invalid "guild_id" "Invalid guild."
        | ProtocolCodecFailure.InvalidDomain(DomainError.InvalidColor _) -> invalid "name_color" "A color is 0xRRGGBB."
        | _ -> invalid "" "Invalid request."

    /// The refusal of a request, sent back on the lane the request came by.
    let refusal lane requestId rejection =
        if lane = DeliveryLane.Chat then
            ServerResponse.ChatRejected(requestId, rejection)
        else
            ServerResponse.RequestRejected(requestId, rejection)

    let private settles lane requestId = {
        Lane = lane
        RequestId = ValueSome requestId
        WhileOpening = false
    }

    let private notifies lane = {
        Lane = lane
        RequestId = ValueNone
        WhileOpening = false
    }

    // A guild's chat travels with its GuildChanged on the control lane: no
    // message overtakes the guild that holds it or trails the guild's removal.
    let private chatLane channelId =
        if ChatChannels.kindOf channelId = ValueSome ChatChannelKind.Guild then
            DeliveryLane.Control
        else
            DeliveryLane.Chat

    /// The one table of how each response travels. Only the refusal of the
    /// opening request may leave before SessionOpened.
    let delivery response : ResponseDelivery =
        match response with
        | ServerResponse.SessionOpened(requestId, _) | ServerResponse.PlayerUpdateAccepted requestId
        | ServerResponse.GroundMarkPlaced(requestId, _, _) | ServerResponse.GroundMarkRemoved(requestId, _)
        | ServerResponse.IdentityVisibilityChanged(requestId, _, _) | ServerResponse.DisplayNameChanged(requestId, _)
        | ServerResponse.NameColorChanged(requestId, _)
        | ServerResponse.SanctionIssued(requestId, _) | ServerResponse.SanctionLifted(requestId, _, _) | ServerResponse.PlayerKicked(requestId, _)
        | ServerResponse.SanctionList(requestId, _) | ServerResponse.PlayerMarks(requestId, _, _) | ServerResponse.PlayerMarksCleared(requestId, _, _)
        | ServerResponse.GuildCommandDone(requestId, _) ->
            settles DeliveryLane.Control requestId
        | ServerResponse.ChatMessageRemoved(ValueSome requestId, channel, _) -> settles (chatLane channel) requestId
        | ServerResponse.ChatMessageRemoved(ValueNone, channel, _) -> notifies (chatLane channel)
        | ServerResponse.RequestRejected(requestId, _) -> { settles DeliveryLane.Control requestId with WhileOpening = true }
        | ServerResponse.ChatAccepted(requestId, message) -> settles (chatLane message.ChannelId) requestId
        | ServerResponse.ChatRejected(requestId, _) -> settles DeliveryLane.Chat requestId
        | ServerResponse.ChatPublished message -> notifies (chatLane message.ChannelId)
        | ServerResponse.PlayersMoved _ -> notifies DeliveryLane.Realtime
        | ServerResponse.PresenceChanged _
        | ServerResponse.GroundMarksChanged _ | ServerResponse.OwnGroundMarks _ | ServerResponse.MuteChanged _
        | ServerResponse.RoleChanged _ | ServerResponse.GuildsSnapshot _ | ServerResponse.GuildChanged _ -> notifies DeliveryLane.Control
        // The last packet before the runtime closes the connection, whatever the phase.
        | ServerResponse.SessionEnded _ -> { notifies DeliveryLane.Control with WhileOpening = true }

    let decodeMovement (codec: ProtocolCodec) (bytes: byte array) =
        if isNull bytes || bytes.Length = 0 then
            fail None ProtocolCodecFailure.EmptyPacket
        elif bytes.Length > codec.Config.MaxPacketBytes then
            fail None ProtocolCodecFailure.PacketTooLarge
        else
            try
                let packet = Dreamsleeve.Protocol.Chat.ClientMovementPacket.Parser.ParseFrom(bytes)
                if packet.ProtocolVersion <> Version then
                    fail None (ProtocolCodecFailure.UnsupportedVersion packet.ProtocolVersion)
                else
                    PlayerCodec.decodeMovement packet.Sample
                    |> Result.mapError (fun error -> {
                        RequestId = None
                        Failure = error
                    })
            with :? InvalidProtocolBufferException -> fail None ProtocolCodecFailure.MalformedPacket

    /// Each realtime packet fits the negotiated payload budget. Entries are
    /// independent; loss of one part never prevents applying the other parts.
    let private encodeMovementPackets (codec: ProtocolCodec) maxUnfragmentedPayloadBytes (movements: MovementChange array) =
        let target =
            if codec.Config.MovementPacketTargetBytes = 0 then min codec.Config.MaxPacketBytes maxUnfragmentedPayloadBytes
            else min codec.Config.MaxPacketBytes (min codec.Config.MovementPacketTargetBytes maxUnfragmentedPayloadBytes)
        let packets = ResizeArray<byte array>()
        let mutable batch = Dreamsleeve.Protocol.Chat.PlayersMoved()
        let mutable payloadSize = 0
        let mutable tooLarge = false
        let headerSize =
            CodedOutputStream.ComputeTagSize(Dreamsleeve.Protocol.Chat.ServerMovementPacket.ProtocolVersionFieldNumber)
            + CodedOutputStream.ComputeUInt32Size(Version)
            + CodedOutputStream.ComputeTagSize(Dreamsleeve.Protocol.Chat.ServerMovementPacket.MovementsFieldNumber)
        let envelopeSize size = headerSize + CodedOutputStream.ComputeLengthSize(size) + size
        let flush () =
            if batch.Players.Count > 0 then
                let packet =
                    Dreamsleeve.Protocol.Chat.ServerMovementPacket(
                        ProtocolVersion = Version,
                        Movements = batch)
                packets.Add(packet.ToByteArray())
                batch <- Dreamsleeve.Protocol.Chat.PlayersMoved()
                payloadSize <- 0

        for movement in movements do
            let item = PlayerCodec.moved movement
            let size = CodedOutputStream.ComputeTagSize(Dreamsleeve.Protocol.Chat.PlayersMoved.PlayersFieldNumber)
                       + CodedOutputStream.ComputeMessageSize(item)
            if envelopeSize size > target then
                tooLarge <- true
            else
                if envelopeSize (payloadSize + size) > target then flush()
                batch.Players.Add item
                payloadSize <- payloadSize + size

        flush()

        if maxUnfragmentedPayloadBytes < 1 then
            fail None (ProtocolCodecFailure.InvalidPayload "transport_payload_budget")
        elif tooLarge then
            fail None ProtocolCodecFailure.PacketTooLarge
        elif packets.Count = 0 then
            fail None (ProtocolCodecFailure.InvalidPayload "players_moved")
        else
            Ok (List.ofSeq packets)

    let private packed (config: ServerConfig) requestId (message: IMessage) =
        if message.CalculateSize() > config.MaxPacketBytes then
            fail requestId ProtocolCodecFailure.PacketTooLarge
        else
            Ok [ message.ToByteArray() ]

    /// The packets that carry a response to one peer. Inputs are validated
    /// domain values; the owner decides IDs, times, recipient correlation,
    /// membership and session ordering, the codec none of these. One branch per
    /// response checks and encodes it; movement alone is split, to fit the
    /// peer's unfragmented payload.
    let encode (codec: ProtocolCodec) maxUnfragmentedPayloadBytes (response: ServerResponse) : Result<byte array list, ProtocolCodecError> =
        let config = codec.Config
        let requestId = (delivery response).RequestId |> ValueOption.toOption
        let invalid field = fail requestId (ProtocolCodecFailure.InvalidPayload field)
        let packet = Dreamsleeve.Protocol.Chat.ServerPacket(ProtocolVersion = Version)
        let envelope () = packed config requestId packet

        if requestId = Some 0UL then
            fail requestId (ProtocolCodecFailure.InvalidEnvelope "request_id")
        else
            requestId |> Option.iter (fun id -> packet.RequestId <- id)
            match response with
            | ServerResponse.SessionOpened(_, value) ->
                if not (SessionCodec.validWelcome config value) then invalid "session_opened"
                else
                    packet.SessionOpened <- SessionCodec.welcome config value
                    envelope ()
            | ServerResponse.ChatAccepted(_, value)
            | ServerResponse.ChatPublished value ->
                packet.ChatPublished <- Dreamsleeve.Protocol.Chat.ChatPublished(Message = ChatCodec.message value)
                envelope ()
            | ServerResponse.RequestRejected(_, value)
            | ServerResponse.ChatRejected(_, value) ->
                if value.Code = RequestRejectionCode.Unspecified || not (Enum.IsDefined value.Code) then invalid "code"
                elif isNull value.Message || isNull value.Field then invalid "rejection"
                else
                    packet.RequestRejected <-
                        Dreamsleeve.Protocol.Chat.RequestRejected(
                            Code = value.Code,
                            Message = value.Message,
                            Field = value.Field)
                    envelope ()
            | ServerResponse.PresenceChanged(change, kinds) ->
                if not (PlayerCodec.validPresence kinds change) then invalid "presence_changed"
                else
                    let bytes = PlayerCodec.presencePacket Version kinds change
                    if bytes.Length > config.MaxPacketBytes then fail requestId ProtocolCodecFailure.PacketTooLarge
                    else Ok [ bytes ]
            | ServerResponse.PlayerUpdateAccepted _ ->
                packet.PlayerUpdateAccepted <- Dreamsleeve.Protocol.Chat.PlayerUpdateAccepted()
                envelope ()
            | ServerResponse.PlayersMoved movements -> encodeMovementPackets codec maxUnfragmentedPayloadBytes movements
            | ServerResponse.GroundMarksChanged view ->
                if not (GroundMarkCodec.validView view) then invalid "ground_marks_changed"
                else
                    packet.GroundMarksChanged <- GroundMarkCodec.changed view
                    envelope ()
            | ServerResponse.GroundMarkPlaced(_, record, evicted) ->
                if record.Author.PlayerId <> record.Mark.Author then invalid "ground_mark_placed"
                else
                    packet.GroundMarkPlaced <- GroundMarkCodec.placed record evicted
                    envelope ()
            | ServerResponse.GroundMarkRemoved(_, id) ->
                packet.GroundMarkRemoved <- GroundMarkCodec.removed id
                envelope ()
            | ServerResponse.OwnGroundMarks records ->
                if not (GroundMarkCodec.validOwn records) then invalid "own_ground_marks"
                else
                    packet.OwnGroundMarks <- GroundMarkCodec.own records
                    envelope ()
            | ServerResponse.IdentityVisibilityChanged(_, pseudonym, hiding) ->
                let changed = Dreamsleeve.Protocol.Chat.IdentityVisibilityChanged(Hidden = SessionCodec.hiding hiding)
                pseudonym |> ValueOption.iter (fun name -> changed.Pseudonym <- Pseudonym.value name)
                packet.IdentityVisibilityChanged <- changed
                envelope ()
            | ServerResponse.DisplayNameChanged(_, name) ->
                packet.DisplayNameChanged <- Dreamsleeve.Protocol.Chat.DisplayNameChanged(DisplayName = DisplayName.value name)
                envelope ()
            | ServerResponse.NameColorChanged(_, color) ->
                packet.NameColorChanged <- Dreamsleeve.Protocol.Chat.NameColorChanged(NameColor = NameColor.value color)
                envelope ()
            | ServerResponse.MuteChanged mute ->
                let changed = Dreamsleeve.Protocol.Chat.MuteChanged()
                mute |> ValueOption.iter (fun sanction -> changed.Mute <- SessionCodec.mute sanction)
                packet.MuteChanged <- changed
                envelope ()
            | ServerResponse.SessionEnded value ->
                packet.SessionEnded <- SessionCodec.ended value
                envelope ()
            | ServerResponse.RoleChanged role ->
                packet.RoleChanged <- Dreamsleeve.Protocol.Chat.RoleChanged(Role = ModerationCodec.role role)
                envelope ()
            | ServerResponse.SanctionIssued(_, sanction) ->
                packet.SanctionIssued <- Dreamsleeve.Protocol.Chat.SanctionIssued(Sanction = ModerationCodec.entry sanction)
                envelope ()
            | ServerResponse.SanctionLifted(_, target, kind) ->
                packet.SanctionLifted <-
                    Dreamsleeve.Protocol.Chat.SanctionLifted(
                        PlayerId = PlayerId.value target,
                        Kind = ModerationCodec.kind kind)
                envelope ()
            | ServerResponse.PlayerKicked(_, target) ->
                packet.PlayerKicked <- Dreamsleeve.Protocol.Chat.PlayerKicked(PlayerId = PlayerId.value target)
                envelope ()
            | ServerResponse.SanctionList(_, sanctions) ->
                packet.SanctionList <- ModerationCodec.list sanctions
                envelope ()
            | ServerResponse.PlayerMarks(_, author, records) ->
                if records |> List.exists (fun record -> record.Mark.Author <> author || record.Author.PlayerId <> author) then invalid "player_marks"
                else
                    packet.PlayerMarks <- ModerationCodec.marks author records
                    envelope ()
            | ServerResponse.PlayerMarksCleared(_, target, removed) ->
                packet.PlayerMarksCleared <-
                    Dreamsleeve.Protocol.Chat.PlayerMarksCleared(
                        PlayerId = PlayerId.value target,
                        Removed = uint32 removed)
                envelope ()
            | ServerResponse.ChatMessageRemoved(_, channel, message) ->
                packet.ChatMessageRemoved <-
                    Dreamsleeve.Protocol.Chat.ChatMessageRemoved(
                        ChannelId = ChatChannelId.value channel,
                        MessageId = ChatMessageId.value message)
                envelope ()
            | ServerResponse.GuildsSnapshot state ->
                if not (GuildCodec.validSnapshot config state) then invalid "guilds_snapshot"
                else
                    packet.GuildsSnapshot <- GuildCodec.snapshot state
                    envelope ()
            | ServerResponse.GuildChanged change ->
                if not (GuildCodec.validChange config change) then invalid "guild_changed"
                else
                    packet.GuildChanged <- GuildCodec.changed change
                    envelope ()
            | ServerResponse.GuildCommandDone(_, guild) ->
                packet.GuildCommandDone <- Dreamsleeve.Protocol.Chat.GuildCommandDone(GuildId = GuildId.value guild)
                envelope ()
