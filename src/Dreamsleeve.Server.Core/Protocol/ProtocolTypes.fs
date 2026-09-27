namespace Dreamsleeve.Server.Core

open System
open Google.Protobuf
open Dreamsleeve.Server.Domain

[<RequireQualifiedAccess>]
type ProtocolCodecFailure =
    | EmptyPacket
    | PacketTooLarge
    | MalformedPacket
    | UnsupportedVersion of uint32
    | InvalidEnvelope of string
    | InvalidPayload of string
    | InvalidDomain of DomainError

type ProtocolCodecError = {
    RequestId: uint64 option
    Failure: ProtocolCodecFailure
}

[<RequireQualifiedAccess>]
type ClientCommand =
    | OpenSession of sessionTicket: string
    | SendChat of ChatChannelId * ChatMessageText
    | UpdatePlayer of PlayerUpdate

type ClientRequest = {
    RequestId: uint64
    Command: ClientCommand
}

type SessionWelcome = {
    SelfPlayerId: PlayerId
    GlobalChannelId: ChatChannelId
    Players: PlayerSnapshot list
    RecentMessages: ChatMessage list
}

type RequestRejectionCode = Dreamsleeve.Protocol.Chat.RequestRejectionCode

type RequestRejection = {
    Code: RequestRejectionCode
    Message: string
    Field: string
}

[<RequireQualifiedAccess>]
type ServerResponse =
    | SessionOpened of requestId: uint64 * session: SessionWelcome
    | ChatAccepted of requestId: uint64 * message: ChatMessage
    | ChatPublished of ChatMessage
    | RequestRejected of requestId: uint64 * rejection: RequestRejection
    | PlayerJoined of PlayerSnapshot
    | PlayerLeft of PlayerId
    | PlayerUpdated of PlayerSnapshot
    | PlayersMoved of MovementChange array
    | PlayerMetadataChanged of PlayerId * Map<ActorValueKey, ActorValueInfo> voption * PlayerDetails voption
    | PlayerUpdateAccepted of requestId: uint64

