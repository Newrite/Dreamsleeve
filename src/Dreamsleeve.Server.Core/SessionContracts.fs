namespace Dreamsleeve.Server.Core

open System
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

/// A new ID per transport connection, independent of reusable ENet peer slots.
type SessionOpenRequest = {
    ConnectionId: Guid
    RequestId: uint64
    SessionTicket: string
    /// Where others see a pseudonym, from the first packet about this player.
    Hiding: HiddenIdentity
}

[<RequireQualifiedAccess>]
type PlayerStateError =
    | NotReady
    | Closed
    | Busy

/// Managed events handed off by the transport owner.
[<RequireQualifiedAccess>]
type ServerTransportEvent =
    | Connected of Guid
    | Received of Guid * DeliveryLane * byte array
    | Disconnected of Guid
    | Failed of Guid * reason: string

type ServerTransport = {
    Poll: unit -> Result<ServerTransportEvent list, string>
    /// Nonblocking wakeup; false means consumer admission failed and notification must be retried.
    SetReadyHandler: (unit -> bool) -> unit
    /// Success transfers immutable payload ownership to the handoff queue; callers
    /// must not mutate/reuse Bytes. Admission is not acknowledgement of delivery.
    Send: Guid * TransportPacket -> Result<unit, string>
    /// Current transport payload budget before fragmentation; zero for unavailable connections.
    MaxUnfragmentedPayloadBytes: Guid -> int
    /// Stop new sends and drain accepted reliable packets; Poll eventually reports Disconnected.
    Close: Guid -> unit
    /// Force local removal on the runtime deadline; no Disconnected event is required.
    Reset: Guid -> unit
    Dispose: unit -> unit
}

[<RequireQualifiedAccess>]
type IdentityAdmission =
    /// The pseudonym the player is shown under, when the session asked to hide.
    | Reserved of Pseudonym voption
    | AlreadyInUse
    | Closed

/// Session-to-runtime boundary: transport output and route lifecycle, never chat execution.
[<RequireQualifiedAccess>]
type SessionHostCommand =
    /// Reserves the PlayerId and the names the moderated profile is shown under,
    /// with a new pseudonym when the names are hidden.
    | Reserve of Guid * PlayerData * HiddenIdentity * ReliableAgentRef<IdentityAdmission>
    /// Shows the reserved profile again or hides it; the pseudonym is new only
    /// when the names were shown everywhere before.
    | ChangeIdentity of Guid * HiddenIdentity * ReliableAgentRef<Pseudonym voption>
    /// The moderated profile after an administrator renamed the player; the
    /// names shown online (PseudonymBook) follow it.
    | UpdateProfile of Guid * PlayerData
    | Activate of Guid * requestId: uint64 * SessionWelcome
    | Send of Guid * ServerResponse
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
    | Rejected of requestId: uint64 * RequestRejection

type ChatSubmission = {
    ConnectionId: Guid
    RequestId: uint64
    /// The author's public identity at sending, decided by the session.
    Author: PublicIdentity
    Text: ChatMessageText
    /// Published character name at sending, already moderated by the session.
    CharacterName: CharacterName voption
    /// Normalized projection used for the repeated-message check; never published.
    Fingerprint: string
    /// Flag-tier ranges of Text, computed by the session outside the channel owner.
    Flagged: TextSpan list
    /// Present for a client announcement, already admitted by origin; only the
    /// system channel accepts it, and it accepts nothing else.
    Announcement: Announcement voption
    /// Also available after membership disappears, so refusals settle the request.
    ReplyTo: ReliableAgentRef<ChatRoomEvent>
}

/// A server-authored announcement handed to the system channel owner.
type ServerAnnouncement = {
    Text: ChatMessageText
    Kind: AnnouncementKind
}

[<RequireQualifiedAccess>]
type ChatRoomCommand =
    | Join of Subscription<ChatRoomEvent>
    | Publish of ChatSubmission
    /// Server-authored; no membership, rate limit or reply.
    | Announce of ServerAnnouncement
    | Detach of SessionDetach
    | ReadHistory of ChatMessageId voption * int * ReplyChannel<Result<ChatHistoryPage, DomainError>>

[<RequireQualifiedAccess>]
type PresenceEvent =
    | Snapshot of PlayerSnapshot list
    | Joined of PlayerSnapshot
    | Updated of PlayerSnapshot
    | Moved of MovementChange array
    | VisibilityChanged of VisibilityChange
    | MetadataChanged of PlayerId * Map<ActorValueKey, ActorValueInfo> voption * PlayerDetails voption
    | Left of PlayerId

type PresenceSubscription = {
    ConnectionId: Guid
    Snapshot: PlayerSnapshot
    Events: ReliableAgentRef<PresenceEvent>
}

/// Persistence of marks, executed off the owner by a bounded writer.
[<RequireQualifiedAccess>]
type GroundMarkWrite =
    | Insert of GroundMark
    | Delete of GroundMarkId list

[<RequireQualifiedAccess>]
type GroundMarkEvent =
    | Changed of GroundMarkView
    | Placed of requestId: uint64 * GroundMarkRecord * evicted: GroundMarkId voption
    | Removed of requestId: uint64 * GroundMarkId
    | Rejected of requestId: uint64 * RequestRejection
    /// Every mark of the observer, wherever it stands: after Join and on each change of that set.
    | Own of GroundMarkRecord list

/// A validated placement request; the session has already checked the word
/// list, computed the flags and verified the position against the player's own.
type GroundMarkSubmission = {
    ConnectionId: Guid
    RequestId: uint64
    Body: GroundMarkBody
    Placement: GroundMarkPlacement
    /// Published character name at placement, already moderated by the session.
    CharacterName: CharacterName voption
    /// The author's pseudonym at placement; the mark keeps it for its lifetime.
    Pseudonym: Pseudonym voption
    /// Normalized projection of a note for the repeated-text check; never published.
    Fingerprint: string
    Flagged: TextSpan list
}

/// A stored mark with its author's current profile, already moderated.
type StoredGroundMark = {
    Mark: GroundMark
    Author: PlayerData
}

/// Stored marks with their authors' current profiles, the storage high-water
/// mark and the writer that keeps storage current; supplied at runtime start.
type GroundMarkPersistence = {
    Loaded: StoredGroundMark list
    /// One above the highest ID storage ever issued, so IDs never repeat across runs.
    NextId: uint64
    Writer: ReliableAgentRef<GroundMarkWrite>
}

[<RequireQualifiedAccess>]
type GroundMarkCommand =
    | Join of Subscription<GroundMarkEvent>
    /// The observer's latest position and character generation; visibility follows it.
    | Observe of connectionId: Guid * characterGeneration: uint64 * PlayerLocation voption
    | Place of GroundMarkSubmission
    | Remove of connectionId: Guid * requestId: uint64 * GroundMarkId
    | Expire of AgentTick
    | Detach of SessionDetach

[<RequireQualifiedAccess>]
type PresenceCommand =
    | Join of PresenceSubscription
    | Update of connectionId: Guid * PlayerSnapshot
    | Flush of AgentTick
    | Detach of SessionDetach

type ChatRoomOptions = {
    MailboxCapacity: int
    ControlReserve: int
    HistoryCapacity: int
    MaxControlDeliveries: int
    /// Messages an account may send at once before the refill rate applies.
    RateBurst: int
    /// One more message is allowed per this interval, up to RateBurst.
    RateRefillMs: int
    /// The same normalized text is refused within this window; 0 disables the check.
    DuplicateWindowMs: int
}

type PresenceOptions = {
    MailboxCapacity: int
    ControlReserve: int
    MaxControlDeliveries: int
    ReplicationIntervalMs: int
    VisibilityDistance: float32
}

/// [GroundMarks]: the mark owner, its quotas, lifetimes, density, rate limits
/// and delivery radius. Read at startup like the rest of the configuration.
type GroundMarkOptions = {
    MailboxCapacity: int
    ControlReserve: int
    MaxControlDeliveries: int
    /// Pending persistence writes before the owner treats storage as broken.
    MaxPendingWrites: int
    /// Delivery radius in world units; a client cannot draw farther than this.
    VisibilityDistance: float32
    MaxNotesPerPlayer: int
    MaxDeathMarksPerPlayer: int
    /// Days a note lives; 0 keeps it forever.
    NoteTtlDays: int
    /// Days a death mark lives; 0 keeps it forever.
    DeathMarkTtlDays: int
    /// Marks one spatial index cell may hold; more is refused, nothing is evicted.
    MaxPerIndexCell: int
    /// Note placements an account may make at once; then one per RateRefillMs.
    RateBurst: int
    RateRefillMs: int
    /// The same normalized note text is refused within this window; 0 disables it.
    DuplicateWindowMs: int
    /// Deaths reported closer together than this are refused.
    DeathMinIntervalMs: int
    /// A placement farther than this from the player's last known position is refused; 0 disables it.
    MaxPlacementDistance: float32
    /// How often expired marks are collected.
    ExpiryCheckIntervalMs: int
}

[<RequireQualifiedAccess>]
module GroundMarkOptions =
    let defaults = {
        MailboxCapacity = 256; ControlReserve = 64; MaxControlDeliveries = 128; MaxPendingWrites = 256
        VisibilityDistance = 8192.0f
        MaxNotesPerPlayer = 5; MaxDeathMarksPerPlayer = 10
        NoteTtlDays = 30; DeathMarkTtlDays = 7
        MaxPerIndexCell = 64
        RateBurst = 3; RateRefillMs = 20000; DuplicateWindowMs = 300000
        DeathMinIntervalMs = 5000
        MaxPlacementDistance = 2048.0f
        ExpiryCheckIntervalMs = 60000
    }

    /// The domain rules of these options; errors name the offending key.
    let rules options =
        GroundMarkRules.create options.MaxNotesPerPlayer options.MaxDeathMarksPerPlayer options.NoteTtlDays
            options.DeathMarkTtlDays options.VisibilityDistance options.MaxPlacementDistance

    let validate options = [
        if isNull (box options) then "GroundMarks section cannot be null."
        else
            if options.MailboxCapacity < 1 || options.ControlReserve < 1 || options.MaxControlDeliveries < 1 || options.MaxPendingWrites < 1 then
                "GroundMarks queue capacities must be positive."
            if int64 options.MailboxCapacity + int64 options.ControlReserve > int64 Int32.MaxValue then
                "GroundMarks mailbox capacity and control reserve overflow."
            if not (Single.IsFinite options.VisibilityDistance) || options.VisibilityDistance < 0.0f then
                "GroundMarks.VisibilityDistance must be finite and non-negative."
            if not (Single.IsFinite options.MaxPlacementDistance) || options.MaxPlacementDistance < 0.0f then
                "GroundMarks.MaxPlacementDistance must be finite and non-negative."
            if options.MaxNotesPerPlayer < 1 || options.MaxDeathMarksPerPlayer < 1 then
                "GroundMarks.MaxNotesPerPlayer and MaxDeathMarksPerPlayer must be positive."
            if options.NoteTtlDays < 0 || options.DeathMarkTtlDays < 0 then
                "GroundMarks lifetimes must be non-negative days; 0 means no expiry."
            if options.MaxPerIndexCell < 1 then "GroundMarks.MaxPerIndexCell must be positive."
            if options.RateBurst < 1 || options.RateRefillMs < 1 || options.DuplicateWindowMs < 0 then
                "GroundMarks note rate limits must be positive; DuplicateWindowMs non-negative."
            if options.DeathMinIntervalMs < 0 then "GroundMarks.DeathMinIntervalMs must be non-negative."
            if options.ExpiryCheckIntervalMs < 1000 then "GroundMarks.ExpiryCheckIntervalMs must be at least 1000."
    ]

type PlayerSessionOptions = {
    MailboxCapacity: int
    ControlReserve: int
    MaxPendingChat: int
    MaxPendingUpdates: int
    MaxBootstrapEvents: int
    MaxPendingOutput: int
}

/// [Identity]: whether players may hide their names behind a server pseudonym
/// and how often one session may switch. Read at startup.
type IdentityOptions = {
    AllowHiddenIdentity: bool
    /// A switch sooner than this after the previous one is refused; 0 disables the limit.
    ToggleIntervalMs: int
    /// Separate TOML with the pseudonym dictionary, next to moderation.toml.
    PseudonymsPath: string
}

[<RequireQualifiedAccess>]
module IdentityOptions =
    let defaults = { AllowHiddenIdentity = true; ToggleIntervalMs = 30000; PseudonymsPath = "pseudonyms.toml" }

    let validate options = [
        if isNull (box options) then "Identity section cannot be null."
        else
            if options.ToggleIntervalMs < 0 then "Identity.ToggleIntervalMs must be non-negative."
            if isNull options.PseudonymsPath then "Identity.PseudonymsPath cannot be null."
    ]

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
        ControlReserve = 160
        OpenTimeoutMs = 10000
        ShutdownTimeoutMs = 1500
        PollIntervalMs = 1
        Player = { MailboxCapacity = 128; ControlReserve = 32; MaxPendingChat = 16; MaxPendingUpdates = 16;
                   MaxBootstrapEvents = 128; MaxPendingOutput = 128 }
        Chat = { MailboxCapacity = 256; ControlReserve = 64; HistoryCapacity = 512; MaxControlDeliveries = 128
                 RateBurst = 5; RateRefillMs = 2000; DuplicateWindowMs = 30000 }
        Presence = { MailboxCapacity = 128; ControlReserve = 64; MaxControlDeliveries = 128; ReplicationIntervalMs = 50; VisibilityDistance = 8192.0f }
    }
