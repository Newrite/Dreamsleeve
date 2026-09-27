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
    let Version = 4u

    let private fail requestId failure = Error { RequestId = requestId; Failure = failure }

    let create (config: ServerConfig) : Result<ProtocolCodec, string list> =
        match ServerConfig.protocolErrors config with
        | [] -> Ok { Config = config }
        | errors -> Error errors

    let private decodePayload (config: ServerConfig) (packet: Dreamsleeve.Protocol.Chat.ClientPacket) =
        let decoded =
            match packet.PayloadCase with
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.OpenSession ->
                SessionCodec.decodeTicket packet.OpenSession
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.SendChat ->
                ChatCodec.decodeCommand config.ChatInput.MessageText packet.SendChat
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.UpdatePlayer ->
                PlayerCodec.decodeUpdate config.PlayerInput packet.UpdatePlayer |> Result.map ClientCommand.UpdatePlayer
            | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.None ->
                Error(ProtocolCodecFailure.InvalidPayload "payload")
            | unknown when not (Enum.IsDefined unknown) -> Error(ProtocolCodecFailure.InvalidPayload "payload")
        decoded
        |> Result.map (fun command -> { RequestId = packet.RequestId; Command = command })
        |> Result.mapError (fun failure -> { RequestId = Some packet.RequestId; Failure = failure })

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

    // Optional only at the wire/error boundary; response cases carry their own
    // required correlation, while notifications cannot be given a request ID.
    let private responseRequestId = function
        | ServerResponse.SessionOpened(requestId, _)
        | ServerResponse.ChatAccepted(requestId, _)
        | ServerResponse.PlayerUpdateAccepted requestId
        | ServerResponse.RequestRejected(requestId, _) -> Some requestId
        | ServerResponse.ChatPublished _
        | ServerResponse.PlayerJoined _
        | ServerResponse.PlayerUpdated _
        | ServerResponse.PlayerMetadataChanged _
        | ServerResponse.PlayerMoved _
        | ServerResponse.PlayerLeft _ -> None

    let private validateResponse config response =
        if responseRequestId response = Some 0UL then
            Some(ProtocolCodecFailure.InvalidEnvelope "request_id")
        else
            match response with
            | ServerResponse.SessionOpened(_, value) ->
                if SessionCodec.validWelcome config value then None
                else Some(ProtocolCodecFailure.InvalidPayload "session_opened")

            | ServerResponse.RequestRejected(_, value) ->
                if value.Code = RequestRejectionCode.Unspecified || not (Enum.IsDefined value.Code) then
                    Some(ProtocolCodecFailure.InvalidPayload "code")
                elif isNull value.Message || isNull value.Field then
                    Some(ProtocolCodecFailure.InvalidPayload "rejection")
                else None

            | ServerResponse.PlayerMetadataChanged(_, values, details) ->
                if values.IsNone && details.IsNone then Some(ProtocolCodecFailure.InvalidPayload "player_metadata_changed")
                else None

            | ServerResponse.ChatAccepted _
            | ServerResponse.ChatPublished _
            | ServerResponse.PlayerJoined _
            | ServerResponse.PlayerUpdated _
            | ServerResponse.PlayerMoved _
            | ServerResponse.PlayerUpdateAccepted _
            | ServerResponse.PlayerLeft _ -> None

    // Inputs are validated domain values. The owner decides IDs, times, recipient
    // correlation, membership and session ordering; the codec does none of these.
    let encodeServer (codec: ProtocolCodec) (response: ServerResponse) : Result<byte array, ProtocolCodecError> =
        let config = codec.Config
        let requestId = responseRequestId response

        match validateResponse config response with
        | Some failure -> fail requestId failure
        | None ->
            let packet = Dreamsleeve.Protocol.Chat.ServerPacket(ProtocolVersion = Version)
            requestId |> Option.iter (fun id -> packet.RequestId <- id)

            match response with
            | ServerResponse.SessionOpened(_, value) ->
                packet.SessionOpened <- SessionCodec.welcome value

            | ServerResponse.ChatAccepted(_, value)
            | ServerResponse.ChatPublished value ->
                packet.ChatPublished <- Dreamsleeve.Protocol.Chat.ChatPublished(Message = ChatCodec.message value)
            | ServerResponse.PlayerJoined value ->
                packet.PlayerJoined <- Dreamsleeve.Protocol.Chat.PlayerJoined(Player = PlayerCodec.player value)
            | ServerResponse.PlayerUpdated value ->
                packet.PlayerUpdated <- Dreamsleeve.Protocol.Chat.PlayerUpdated(Player = PlayerCodec.player value)
            | ServerResponse.PlayerMetadataChanged(playerId, values, metadata) ->
                packet.PlayerMetadataChanged <- PlayerCodec.metadataChanged playerId values metadata
            | ServerResponse.PlayerMoved(playerId, place) ->
                packet.PlayerMoved <- PlayerCodec.moved playerId place
            | ServerResponse.PlayerUpdateAccepted _ ->
                packet.PlayerUpdateAccepted <- Dreamsleeve.Protocol.Chat.PlayerUpdateAccepted()
            | ServerResponse.PlayerLeft value ->
                packet.PlayerLeft <- Dreamsleeve.Protocol.Chat.PlayerLeft(PlayerId = PlayerId.value value)

            | ServerResponse.RequestRejected(_, value) ->
                packet.RequestRejected <- Dreamsleeve.Protocol.Chat.RequestRejected(Code = value.Code, Message = value.Message, Field = value.Field)

            if packet.CalculateSize() > config.MaxPacketBytes then
                fail requestId ProtocolCodecFailure.PacketTooLarge
            else
                Ok(packet.ToByteArray())
