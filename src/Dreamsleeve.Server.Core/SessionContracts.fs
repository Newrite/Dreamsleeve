namespace Dreamsleeve.Server.Core

open System
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

/// A new ID per transport connection, independent of reusable ENet peer slots.
type SessionOpenRequest = {
    ConnectionId: Guid
    RequestId: uint64
    Username: Username
    DisplayName: DisplayName
}

[<RequireQualifiedAccess>]
type PlayerStateError =
    | NotReady
    | Closed
    | Busy

/// Validated domain input from an adapter; no wire telemetry contract is implied.
[<RequireQualifiedAccess>]
type PlayerUpdate =
    | BeginCharacter of CharacterName
    | RenameCharacter of CharacterName
    | SetLocation of PlayerLocation
    | ClearLocation
    | SetActorValues of (ActorValueKey * ActorValueInfo) list
    | LeaveGame

/// The adapter and the runtime share one sequential owner of every transport call.
[<RequireQualifiedAccess>]
type ServerTransportEvent =
    | Connected of Guid
    | Received of Guid * byte array
    | Disconnected of Guid

type ServerTransport = {
    Poll: unit -> Result<ServerTransportEvent list, string>
    Send: Guid * byte array -> Result<unit, string>
    Close: Guid -> unit
    Dispose: unit -> unit
}

[<RequireQualifiedAccess>]
type IdentityAdmission =
    | Reserved
    | AlreadyInUse
    | Closed

/// Control traffic only. Normal chat events travel directly to the session.
[<RequireQualifiedAccess>]
type SessionHostCommand =
    | Reserve of Guid * PlayerId * ReliableAgentRef<IdentityAdmission>
    | Activate of Guid * requestId: uint64 * ChatSessionOpened
    | Send of Guid * ChatResponse
    | Close of Guid * reason: string
    | SlowConsumer of Guid

type Subscription<'Event> = {
    ConnectionId: Guid
    Profile: PlayerData
    Events: ReliableAgentRef<'Event>
}

/// Completion is acknowledged after removal; posting this command is not cleanup.
type SessionDetach = {
    ConnectionId: Guid
    ReplyTo: ReliableAgentRef<Guid>
}

[<RequireQualifiedAccess>]
type ChatRoomEvent =
    | Joined of ChatSnapshot
    | JoinFailed of string
    | Accepted of requestId: uint64 * ChatMessage
    | Published of ChatMessage
    | Rejected of requestId: uint64 * ChatRequestRejected

type ChatSubmission = {
    ConnectionId: Guid
    RequestId: uint64
    Text: ChatMessageText
    /// Also available after membership disappears, so refusals settle the request.
    ReplyTo: ReliableAgentRef<ChatRoomEvent>
}

[<RequireQualifiedAccess>]
type ChatRoomCommand =
    | Join of Subscription<ChatRoomEvent>
    | Publish of ChatSubmission
    | Detach of SessionDetach
    | ReadHistory of ChatMessageId voption * int * ReplyChannel<Result<ChatHistoryPage, DomainError>>

[<RequireQualifiedAccess>]
type PresenceEvent =
    | Snapshot of PlayerData list
    | Joined of PlayerData
    | Left of PlayerId

[<RequireQualifiedAccess>]
type PresenceCommand =
    | Join of Subscription<PresenceEvent>
    | Detach of SessionDetach

type ChatRoomOptions = {
    MailboxCapacity: int
    ControlReserve: int
    HistoryCapacity: int
    MaxControlDeliveries: int
}

type PresenceOptions = {
    MailboxCapacity: int
    ControlReserve: int
    MaxControlDeliveries: int
}

type PlayerSessionOptions = {
    MailboxCapacity: int
    ControlReserve: int
    MaxPendingChat: int
    MaxBootstrapEvents: int
    MaxPendingOutput: int
}

type ServerRuntimeOptions = {
    MaxSessions: int
    MailboxCapacity: int
    ControlReserve: int
    OpenTimeoutMs: int
    ShutdownTimeoutMs: int
    PollIntervalMs: int
    Player: PlayerSessionOptions
    Chat: ChatRoomOptions
    Presence: PresenceOptions
}

[<RequireQualifiedAccess>]
module ServerRuntimeOptions =
    let defaults = {
        MaxSessions = 32
        MailboxCapacity = 256
        ControlReserve = 128
        OpenTimeoutMs = 10000
        ShutdownTimeoutMs = 1500
        PollIntervalMs = 1
        Player = { MailboxCapacity = 128; ControlReserve = 32; MaxPendingChat = 16;
                   MaxBootstrapEvents = 128; MaxPendingOutput = 128 }
        Chat = { MailboxCapacity = 256; ControlReserve = 64; HistoryCapacity = 512; MaxControlDeliveries = 128 }
        Presence = { MailboxCapacity = 128; ControlReserve = 64; MaxControlDeliveries = 128 }
    }
