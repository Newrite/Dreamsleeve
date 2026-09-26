// Unnamed enum values are handled by an Enum.IsDefined guard below.
// Keep FS0025 enabled: every named case must still be listed explicitly.
#nowarn "104"

namespace Dreamsleeve.Server.Core

open System
open Google.Protobuf
open Dreamsleeve.Server.Domain

[<RequireQualifiedAccess>]
type ChatCodecFailure =
    | EmptyPacket
    | PacketTooLarge
    | MalformedPacket
    | UnsupportedVersion of uint32
    | InvalidEnvelope of string
    | InvalidPayload of string
    | InvalidDomain of DomainError

type ChatCodecError = {
    RequestId: uint64 option
    Failure: ChatCodecFailure
}

[<RequireQualifiedAccess>]
type ChatCommand =
    | OpenSession of Username * DisplayName
    | SendChat of ChatChannelId * ChatMessageText

type ChatRequest = {
    RequestId: uint64
    Command: ChatCommand
}

type ChatSessionOpened = {
    SelfPlayerId: PlayerId
    GlobalChannelId: ChatChannelId
    Players: PlayerData list
    RecentMessages: ChatMessage list
}

type RequestRejectionCode = Dreamsleeve.Protocol.Chat.RequestRejectionCode

type ChatRequestRejected = {
    Code: RequestRejectionCode
    Message: string
    Field: string
}

[<RequireQualifiedAccess>]
type ChatResponse =
    | SessionOpened of requestId: uint64 * session: ChatSessionOpened
    | ChatAccepted of requestId: uint64 * message: ChatMessage
    | ChatPublished of ChatMessage
    | RequestRejected of requestId: uint64 * rejection: ChatRequestRejected
    | PlayerJoined of PlayerData
    | PlayerLeft of PlayerId

/// Validated settings, held unchanged for the network owner's lifetime.
type ChatCodec = private { Config: ServerConfig }

[<RequireQualifiedAccess>]
module ChatCodec =
    [<Literal>]
    let Version = 1u

    let private fail requestId failure = Error { RequestId = requestId; Failure = failure }

    let create (config: ServerConfig) : Result<ChatCodec, string list> =
        match ServerConfig.protocolErrors config with
        | [] -> Ok { Config = config }
        | errors -> Error errors

    let private decodePayload limits (packet: Dreamsleeve.Protocol.Chat.ClientPacket) =
        let requestId = Some packet.RequestId
        let domainError error = { RequestId = requestId; Failure = ChatCodecFailure.InvalidDomain error }

        match packet.PayloadCase with
        | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.OpenSession ->
            let source = packet.OpenSession

            match Username.create limits.Username source.Username, DisplayName.create limits.DisplayName source.DisplayName with
            | Ok username, Ok displayName ->
                Ok { RequestId = packet.RequestId; Command = ChatCommand.OpenSession(username, displayName) }
            | Error error, _ | _, Error error -> Error(domainError error)

        | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.SendChat ->
            let source = packet.SendChat

            match ChatChannelId.create source.ChannelId, ChatMessageText.create limits.MessageText source.Text with
            | Ok channel, Ok text ->
                Ok { RequestId = packet.RequestId; Command = ChatCommand.SendChat(channel, text) }
            | Error error, _ | _, Error error -> Error(domainError error)

        | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.None ->
            fail requestId (ChatCodecFailure.InvalidPayload "payload")
        | unknown when not (Enum.IsDefined unknown) ->
            fail requestId (ChatCodecFailure.InvalidPayload "payload")

    // Parse/validate before entering any stateful owner. Domain failures retain
    // correlation so the runtime can send a rejection without applying anything.
    let decodeClient (codec: ChatCodec) (bytes: byte array) : Result<ChatRequest, ChatCodecError> =
        let config = codec.Config

        if isNull bytes || bytes.Length = 0 then
            fail None ChatCodecFailure.EmptyPacket
        elif bytes.Length > config.MaxPacketBytes then
            fail None ChatCodecFailure.PacketTooLarge
        else
            try
                let packet = Dreamsleeve.Protocol.Chat.ClientPacket.Parser.ParseFrom(bytes)
                let requestId = if packet.RequestId = 0UL then None else Some packet.RequestId

                if packet.ProtocolVersion <> Version then
                    fail requestId (ChatCodecFailure.UnsupportedVersion packet.ProtocolVersion)
                elif packet.RequestId = 0UL then
                    fail None (ChatCodecFailure.InvalidEnvelope "request_id")
                else
                    decodePayload config.ChatInput packet
            with :? InvalidProtocolBufferException -> fail None ChatCodecFailure.MalformedPacket

    let private profile (value: PlayerData) =
        Dreamsleeve.Protocol.Chat.PlayerProfile(
            PlayerId = PlayerId.value value.PlayerId,
            Username = Username.value value.Username,
            DisplayName = DisplayName.value value.DisplayName)

    let private message (value: ChatMessage) =
        Dreamsleeve.Protocol.Chat.ChatMessage(
            MessageId = ChatMessageId.value value.MessageId,
            ChannelId = ChatChannelId.value value.ChannelId,
            Author = profile value.Author,
            Text = ChatMessageText.value value.MessageText,
            SentAtUnixMs = Core.toUnixMilliseconds value.SentAt)

    // Domain factories establish scalar invariants; only cross-field consistency
    // and configured collection budgets remain to check here.
    let private validWelcome (config: ServerConfig) (value: ChatSessionOpened) =
        let ids = value.Players |> List.map (fun player -> player.PlayerId)

        let ordered =
            value.RecentMessages |> List.pairwise
            |> List.forall (fun (left, right) -> left.MessageId < right.MessageId)

        value.Players.Length <= config.MaxInitialPlayers && value.RecentMessages.Length <= config.MaxRecentMessages
        && Set.count (Set.ofList ids) = ids.Length && List.contains value.SelfPlayerId ids
        && ordered && (value.RecentMessages |> List.forall (fun msg -> msg.ChannelId = value.GlobalChannelId))

    // Optional only at the wire/error boundary; response cases carry their own
    // required correlation, while notifications cannot be given a request ID.
    let private responseRequestId = function
        | ChatResponse.SessionOpened(requestId, _)
        | ChatResponse.ChatAccepted(requestId, _)
        | ChatResponse.RequestRejected(requestId, _) -> Some requestId
        | ChatResponse.ChatPublished _
        | ChatResponse.PlayerJoined _
        | ChatResponse.PlayerLeft _ -> None

    let private validateResponse config response =
        if responseRequestId response = Some 0UL then
            Some(ChatCodecFailure.InvalidEnvelope "request_id")
        else
            match response with
            | ChatResponse.SessionOpened(_, value) ->
                if validWelcome config value then None
                else Some(ChatCodecFailure.InvalidPayload "session_opened")

            | ChatResponse.RequestRejected(_, value) ->
                if value.Code = RequestRejectionCode.Unspecified || not (Enum.IsDefined value.Code) then
                    Some(ChatCodecFailure.InvalidPayload "code")
                elif isNull value.Message || isNull value.Field then
                    Some(ChatCodecFailure.InvalidPayload "rejection")
                else None

            | ChatResponse.ChatAccepted _
            | ChatResponse.ChatPublished _
            | ChatResponse.PlayerJoined _
            | ChatResponse.PlayerLeft _ -> None

    // Inputs are validated domain values. The owner decides IDs, times, recipient
    // correlation, membership and session ordering; the codec does none of these.
    let encodeServer (codec: ChatCodec) (response: ChatResponse) : Result<byte array, ChatCodecError> =
        let config = codec.Config
        let requestId = responseRequestId response

        match validateResponse config response with
        | Some failure -> fail requestId failure
        | None ->
            let packet = Dreamsleeve.Protocol.Chat.ServerPacket(ProtocolVersion = Version)
            requestId |> Option.iter (fun id -> packet.RequestId <- id)

            match response with
            | ChatResponse.SessionOpened(_, value) ->
                let welcome = Dreamsleeve.Protocol.Chat.SessionOpened(
                    SelfPlayerId = PlayerId.value value.SelfPlayerId,
                    GlobalChannelId = ChatChannelId.value value.GlobalChannelId)

                welcome.Players.AddRange(value.Players |> Seq.map profile)
                welcome.RecentMessages.AddRange(value.RecentMessages |> Seq.map message)

                packet.SessionOpened <- welcome

            | ChatResponse.ChatAccepted(_, value)
            | ChatResponse.ChatPublished value ->
                packet.ChatPublished <- Dreamsleeve.Protocol.Chat.ChatPublished(Message = message value)
            | ChatResponse.PlayerJoined value ->
                packet.PlayerJoined <- Dreamsleeve.Protocol.Chat.PlayerJoined(Player = profile value)
            | ChatResponse.PlayerLeft value ->
                packet.PlayerLeft <- Dreamsleeve.Protocol.Chat.PlayerLeft(PlayerId = PlayerId.value value)

            | ChatResponse.RequestRejected(_, value) ->
                packet.RequestRejected <- Dreamsleeve.Protocol.Chat.RequestRejected(Code = value.Code, Message = value.Message, Field = value.Field)

            if packet.CalculateSize() > config.MaxPacketBytes then
                fail requestId ChatCodecFailure.PacketTooLarge
            else
                Ok(packet.ToByteArray())
