namespace Dreamsleeve.Server.Core

open System
open System.Net
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
    /// The peer's IP address; IPAddress.IPv6None when the transport cannot tell.
    | Connected of Guid * IPAddress
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
    /// The moderated profile after a rename; the names shown online
    /// (PseudonymBook) follow it. own: the player changed the name themself,
    /// so an earlier administrator rename no longer applies to later sessions.
    | UpdateProfile of Guid * PlayerData * own: bool
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
    /// A message left the history; the request ID only in the requester's copy.
    | Removed of requestId: uint64 voption * ChatMessage

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

/// What a moderator asks from the game; the session checks the role, the
/// account service the rank against the target.
[<RequireQualifiedAccess>]
type ModerationAction =
    | Sanction of PlayerId * SanctionKind * SanctionTerm * SanctionReason * devices: bool
    | Lift of PlayerId * SanctionKind
    | Kick of PlayerId * SanctionReason
    | ListSanctions
    | ListMarks of PlayerId
    | ClearMarks of PlayerId * GroundMarkKind list
    | DeleteMessage of ChatChannelId * ChatMessageId

/// A moderator's removal of one message; the session has checked the role.
type ChatRemoval = {
    ConnectionId: Guid
    RequestId: uint64
    MessageId: ChatMessageId
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
    | Remove of ChatRemoval

[<RequireQualifiedAccess>]
type PresenceEvent =
    | Snapshot of PlayerSnapshot list * ActorValueKinds
    | Changed of PresenceChange * ActorValueKinds
    | Moved of MovementChange array

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
    | Removed of requestId: uint64 * GroundMarkId * author: PlayerId
    | Rejected of requestId: uint64 * RequestRejection
    /// Every mark of the observer, wherever it stands: after Join and on each change of that set.
    | Own of GroundMarkRecord list
    /// Answers ListOf: the author's marks, newest first.
    | AuthorMarks of requestId: uint64 * author: PlayerId * GroundMarkRecord list
    /// Answers ClearOf with the IDs removed.
    | Cleared of requestId: uint64 * author: PlayerId * GroundMarkId list

/// A validated placement request; the session has already checked the word
/// list, computed the flags and verified the position against the player's own.
type GroundMarkSubmission = {
    ConnectionId: Guid
    RequestId: uint64
    Body: GroundMarkBody
    Placement: GroundMarkPlacement
    /// The author's in-game date, as the client reported it.
    GameDate: GameDate
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
    /// anyAuthor: a moderator's removal of someone else's mark; otherwise only
    /// the observer's own. The session decides it from the role.
    | Remove of connectionId: Guid * requestId: uint64 * GroundMarkId * anyAuthor: bool
    /// A moderator's look at one author's marks; the session has checked the role.
    | ListOf of connectionId: Guid * requestId: uint64 * author: PlayerId
    /// A moderator's removal of an author's marks of these kinds.
    | ClearOf of connectionId: Guid * requestId: uint64 * author: PlayerId * GroundMarkKind list
    /// The observer's moderated profile after a rename; marks sent from now on carry it.
    | Rename of connectionId: Guid * PlayerData
    | Expire of AgentTick
    | Detach of SessionDetach

[<RequireQualifiedAccess>]
type PresenceCommand =
    | Join of PresenceSubscription
    | Update of connectionId: Guid * PlayerSnapshot
    | Flush of AgentTick
    | Detach of SessionDetach

/// Admission per stable account: up to Burst attempts at once, then one more
/// per RefillMs; the same normalized text is refused within DuplicateWindowMs,
/// 0 disables that check. Reconnecting resets nothing.
type RateLimitOptions = {
    Burst: int
    RefillMs: int
    DuplicateWindowMs: int
}

[<RequireQualifiedAccess>]
module RateLimitOptions =
    /// section names the table, e.g. "Runtime.Chat.Rate".
    let validate section options = [
        if options.Burst < 1 || options.RefillMs < 1 then $"{section}.Burst and {section}.RefillMs must be positive."
        if options.DuplicateWindowMs < 0 then $"{section}.DuplicateWindowMs must be non-negative; 0 disables the check."
    ]

[<RequireQualifiedAccess>]
module Visibility =
    /// World units within which a client is told of players and marks, unless configured.
    [<Literal>]
    let DefaultDistance = 8192.0f

type ChatRoomOptions = {
    MailboxCapacity: int
    ControlReserve: int
    HistoryCapacity: int
    MaxControlDeliveries: int
    /// Messages per stable account.
    Rate: RateLimitOptions
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
    /// Note placements per stable account.
    NoteRate: RateLimitOptions
    /// Deaths reported closer together than this are refused.
    DeathMinIntervalMs: int
    /// A placement farther than this from the player's last known position is refused; 0 disables it.
    MaxPlacementDistance: float32
    /// How often expired marks are collected.
    ExpiryCheckIntervalMs: int
}

[<RequireQualifiedAccess>]
module GroundMarkOptions =
    [<Literal>]
    let MinExpiryCheckIntervalMs = 1000

    let defaults = {
        MailboxCapacity = 2048; ControlReserve = 64; MaxControlDeliveries = 128; MaxPendingWrites = 256
        VisibilityDistance = Visibility.DefaultDistance
        MaxNotesPerPlayer = 5; MaxDeathMarksPerPlayer = 10
        NoteTtlDays = 30; DeathMarkTtlDays = 7
        MaxPerIndexCell = 64
        NoteRate = { Burst = 3; RefillMs = 20000; DuplicateWindowMs = 300000 }
        DeathMinIntervalMs = 5000
        MaxPlacementDistance = 2048.0f
        ExpiryCheckIntervalMs = 60000
    }

    /// The domain rules of these options; errors name the offending key.
    let rules options =
        GroundMarkRules.create options.MaxNotesPerPlayer options.MaxDeathMarksPerPlayer options.NoteTtlDays
            options.DeathMarkTtlDays options.VisibilityDistance options.MaxPlacementDistance

    let validate options = [
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
        yield! RateLimitOptions.validate "GroundMarks.NoteRate" options.NoteRate
        if options.DeathMinIntervalMs < 0 then "GroundMarks.DeathMinIntervalMs must be non-negative."
        if options.ExpiryCheckIntervalMs < MinExpiryCheckIntervalMs then
            $"GroundMarks.ExpiryCheckIntervalMs must be at least {MinExpiryCheckIntervalMs}."
    ]

type PlayerSessionOptions = {
    MailboxCapacity: int
    ControlReserve: int
    MaxPendingChat: int
    MaxPendingUpdates: int
    MaxBootstrapEvents: int
    MaxPendingOutput: int
}

[<RequireQualifiedAccess>]
module PlayerSessionOptions =
    /// Each outbox of a session holds its pending budget plus these slots, so a
    /// join or detach and an identity or profile change always fit.
    [<Literal>]
    let OutboxReserve = 2

/// [Identity]: whether players may hide their names behind a server pseudonym
/// and how often one session may switch. Read at startup.
type IdentityOptions = {
    AllowHiddenIdentity: bool
    /// A switch sooner than this after the previous one is refused; 0 disables the limit.
    ToggleIntervalMs: int
    /// Separate TOML with the pseudonym dictionary, next to moderation.toml.
    PseudonymsPath: string
    /// false refuses a player's own display name change (DISPLAY_NAME_CHANGE_NOT_ALLOWED);
    /// the admin panel still renames.
    AllowDisplayNameChange: bool
    /// A player's own change may come no sooner than this after the previous
    /// one, across sessions and restarts (display_name_changes); 0 disables the limit.
    DisplayNameChangeIntervalMinutes: int
}

[<RequireQualifiedAccess>]
module IdentityOptions =
    /// A year of minutes.
    [<Literal>]
    let MaxDisplayNameChangeIntervalMinutes = 525600

    let defaults = {
        AllowHiddenIdentity = true; ToggleIntervalMs = 30000; PseudonymsPath = "pseudonyms.toml"
        AllowDisplayNameChange = true; DisplayNameChangeIntervalMinutes = 1
    }

    let validate options = [
        if options.ToggleIntervalMs < 0 then "Identity.ToggleIntervalMs must be non-negative."
        if options.DisplayNameChangeIntervalMinutes < 0 || options.DisplayNameChangeIntervalMinutes > MaxDisplayNameChangeIntervalMinutes then
            $"Identity.DisplayNameChangeIntervalMinutes must be 0..{MaxDisplayNameChangeIntervalMinutes}."
    ]

    let displayNameInterval options = TimeSpan.FromMinutes(float options.DisplayNameChangeIntervalMinutes)

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
        MaxSessions = 512
        MailboxCapacity = 32768
        ControlReserve = 2052
        OpenTimeoutMs = 10000
        ShutdownTimeoutMs = 5000
        PollIntervalMs = 1
        Player = { MailboxCapacity = 1152; ControlReserve = 32; MaxPendingChat = 16; MaxPendingUpdates = 16;
                   MaxBootstrapEvents = 512; MaxPendingOutput = 1152 }
        Chat = { MailboxCapacity = 1024; ControlReserve = 64; HistoryCapacity = 512; MaxControlDeliveries = 128
                 Rate = { Burst = 5; RefillMs = 2000; DuplicateWindowMs = 30000 } }
        Presence = { MailboxCapacity = 4096; ControlReserve = 128; MaxControlDeliveries = 512; ReplicationIntervalMs = 50
                     VisibilityDistance = Visibility.DefaultDistance }
    }
