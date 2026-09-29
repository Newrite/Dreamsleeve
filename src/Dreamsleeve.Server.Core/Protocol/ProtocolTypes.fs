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

type DeliveryLane = Dreamsleeve.Protocol.Network.DeliveryLane

/// A detached encoded packet. Only the transport adapter chooses native flags.
type TransportPacket = { Lane: DeliveryLane; Bytes: byte array }

/// A client request to publish into the system channel. The requested
/// origin cannot be Server; kind admission belongs to the codec.
type AnnouncementRequest = {
    ChannelId: ChatChannelId
    Text: ChatMessageText
    Kind: AnnouncementKind
    Source: ClientAnnouncementSource
    Signature: AnnouncementSignature voption
}

/// A mark with the author's profile for the wire; the mark itself stores only the ID.
type GroundMarkRecord = {
    Mark: GroundMark
    Author: PlayerData
}

/// Reliable delta of one observer's visible marks; Clear starts a new baseline.
type GroundMarkView = {
    ViewRevision: uint64
    Added: GroundMarkRecord list
    Removed: GroundMarkId list
    Clear: bool
}

[<RequireQualifiedAccess>]
type ClientCommand =
    | OpenSession of sessionTicket: string
    | SendChat of ChatChannelId * ChatMessageText
    | UpdatePlayer of PlayerUpdate
    | PostAnnouncement of AnnouncementRequest
    | PlaceGroundNote of GroundNoteText * GroundMarkPlacement
    | ReportDeath of DeathMarkText * GroundMarkPlacement
    | RemoveGroundMark of GroundMarkId

type ClientRequest = {
    RequestId: uint64
    Command: ClientCommand
}

/// A channel of the session with its retained tail, ascending message ID.
type WelcomeChannel = {
    ChannelId: ChatChannelId
    Kind: ChatChannelKind
    Messages: ChatMessage list
}

type SessionWelcome = {
    SelfPlayerId: PlayerId
    Players: PlayerSnapshot list
    Channels: WelcomeChannel list
    /// Client announcement origins this server admits; limits come from ChatInput.
    AnnouncementSources: ClientAnnouncementSource list
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
    | ChatRejected of requestId: uint64 * rejection: RequestRejection
    | RequestRejected of requestId: uint64 * rejection: RequestRejection
    | PlayerJoined of PlayerSnapshot
    | PlayerLeft of PlayerId
    | PlayerUpdated of PlayerSnapshot
    | PlayersMoved of MovementChange array
    | PlayerVisibilityChanged of VisibilityChange
    | PlayerMetadataChanged of PlayerId * Map<ActorValueKey, ActorValueInfo> voption * PlayerDetails voption
    | PlayerUpdateAccepted of requestId: uint64
    | GroundMarksChanged of GroundMarkView
    | GroundMarkPlaced of requestId: uint64 * GroundMarkRecord * evicted: GroundMarkId voption
    | GroundMarkRemoved of requestId: uint64 * GroundMarkId
    /// Full replacement of the player's own marks; no request ID.
    | OwnGroundMarks of GroundMarkRecord list

