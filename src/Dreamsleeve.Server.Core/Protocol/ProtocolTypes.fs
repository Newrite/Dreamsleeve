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
// Local handoff scheduling, never serialized or interpreted by native ENet.
[<RequireQualifiedAccess>]
type PacketSchedule = Ordered | ModelNotice of source: uint64 | LatestPose of source: uint64

type TransportPacket =
    { Lane: DeliveryLane; Bytes: byte array; Schedule: PacketSchedule }
    member this.PoseStream =
        match this.Schedule with PacketSchedule.LatestPose source -> ValueSome source | _ -> ValueNone


/// A client request to publish into the system channel. The requested
/// origin cannot be Server; kind admission belongs to the codec.
type AnnouncementRequest = {
    ChannelId: ChatChannelId
    Text: ChatMessageText
    Kind: AnnouncementKind
    Source: ClientAnnouncementSource
    Signature: AnnouncementSignature voption
}

/// A mark with its author's public identity for the wire: the pseudonym the
/// mark was placed under, else the author's current profile.
type GroundMarkRecord = {
    Mark: GroundMark
    Author: PublicIdentity
}

/// Reliable delta of one observer's visible marks; Clear starts a new baseline.
type GroundMarkView = {
    ViewRevision: uint64
    Added: GroundMarkRecord list
    Removed: GroundMarkId list
    Clear: bool
}

/// What a member asks of their guilds; the guild owner decides who may.
[<RequireQualifiedAccess>]
type GuildAction =
    /// Checked by the guild owner against [Guilds] and the word list.
    | Create of name: string
    /// An online player.
    | Invite of GuildId * PlayerId
    | Answer of GuildId * accept: bool
    | Leave of GuildId
    | Exclude of GuildId * PlayerId
    /// Member or Officer; only the master assigns them.
    | SetRole of GuildId * PlayerId * GuildRole
    | Transfer of GuildId * PlayerId
    | Mute of GuildId * PlayerId * SanctionTerm * SanctionReason
    | Unmute of GuildId * PlayerId
    | Disband of GuildId

/// A member as guildmates see them: the real profile after the word list,
/// never a pseudonym, and whether they are online.
type GuildMemberView = {
    Membership: GuildMember
    Profile: PlayerData
    Online: bool
}

type GuildView = {
    Guild: GuildId
    Name: GuildName
    ChannelId: ChatChannelId
    CreatedAt: DateTimeOffset
    Members: GuildMemberView list
    /// Retained chat history, oldest first; empty in a member change.
    Messages: ChatMessage list
}

/// An invitation as the invited player sees it: the inviter is named only by
/// ID, so a pseudonym the inviter hides behind stays one.
type GuildInviteView = {
    Invite: GuildInvite
    GuildName: GuildName
}

/// Every guild of a player and their invitations, with the server's limits.
type GuildState = {
    Guilds: GuildView list
    Invites: GuildInviteView list
    Limits: GuildLimits
}

[<RequireQualifiedAccess>]
type GuildRemoval =
    | Left
    | Excluded
    | Disbanded

[<RequireQualifiedAccess>]
type GuildChange =
    /// The player created or joined it.
    | Added of GuildView
    | Removed of GuildId * GuildRemoval
    /// A member joined, or their role, guild mute, online state or name changed.
    | MemberChanged of GuildId * GuildMemberView
    | MemberRemoved of GuildId * PlayerId * GuildRemoval
    | Invited of GuildInviteView
    /// Accepted, declined, expired or the guild disbanded.
    | InviteRemoved of GuildId

[<RequireQualifiedAccess>]
type ClientCommand =
    | OpenSession of sessionTicket: string * HiddenIdentity
    /// Stays connected without a session; the server counts the client online.
    | JoinAsGuest
    | SendChat of ChatChannelId * ChatMessageText
    | UpdatePlayer of PlayerUpdate
    | PostAnnouncement of AnnouncementRequest
    | PlaceGroundNote of GroundNoteText * GroundMarkPlacement * GameDate
    | ReportDeath of DeathMarkText * GroundMarkPlacement * GameDate
    | RemoveGroundMark of GroundMarkId
    | SetIdentityVisibility of HiddenIdentity
    /// The sender's own new display name, already accepted by DisplayName.create.
    | ChangeDisplayName of DisplayName
    /// The sender's own name color; the session decides whether it is readable.
    | SetNameColor of NameColor
    // A moderator's; the session and the account service decide who may.
    | SanctionPlayer of PlayerId * SanctionKind * SanctionTerm * SanctionReason * devices: bool
    | LiftSanction of PlayerId * SanctionKind
    | KickPlayer of PlayerId * SanctionReason
    | ListSanctions
    | ListPlayerMarks of PlayerId
    | ClearPlayerMarks of PlayerId * GroundMarkKind list
    | DeleteChatMessage of ChatChannelId * ChatMessageId
    | Guild of GuildAction

type ClientRequest = {
    RequestId: uint64
    Command: ClientCommand
}

/// A server-assigned number for one key and label of actor values. It lives
/// while some online player publishes that pair and is never given to another.
type ActorValueKind = { Id: uint64; Key: ActorValueKey; DisplayName: ActorValueName }

/// The kind numbers of one presence event, fixed when presence builds it, and
/// the kinds its recipient has not been told yet, ascending.
type ActorValueKinds = {
    Ids: Map<struct (ActorValueKey * ActorValueName), uint64>
    Defined: ActorValueKind list
}

[<RequireQualifiedAccess>]
module ActorValueKinds =
    let none = { Ids = Map.empty; Defined = [] }

/// What changed in one player's actor values and details since the last tick.
type MetadataPatch = {
    PlayerId: PlayerId
    ActorValues: ActorValuesPatch voption
    Details: DetailsPatch voption
}

/// What changed in the online list for one recipient: one per replication
/// tick and one per joining or leaving player. Parts apply in field order.
type PresenceChange = {
    Joined: PlayerSnapshot list
    /// Identity or character changes, without a position.
    Updated: PlayerSnapshot list
    Metadata: MetadataPatch list
    /// The recipient's own place: every baseline below is in it.
    Space: Location voption
    Visibility: VisibilityChange list
    Left: PlayerId list
}

[<RequireQualifiedAccess>]
module PresenceChange =
    let empty = { Joined = []; Updated = []; Metadata = []; Space = ValueNone; Visibility = []; Left = [] }

    let isEmpty change =
        change.Joined.IsEmpty && change.Updated.IsEmpty && change.Metadata.IsEmpty
        && change.Visibility.IsEmpty && change.Left.IsEmpty

/// A channel of the session with its retained tail, ascending message ID.
type WelcomeChannel = {
    ChannelId: ChatChannelId
    Kind: ChatChannelKind
    Messages: ChatMessage list
}

type SessionWelcome = {
    SelfPlayerId: PlayerId
    Players: PlayerSnapshot list
    /// Every kind the players use.
    Kinds: ActorValueKinds
    Channels: WelcomeChannel list
    /// Client announcement origins this server admits; limits come from ChatInput.
    AnnouncementSources: ClientAnnouncementSource list
    /// What the other players see instead of this player's names, if hidden.
    OwnPseudonym: Pseudonym voption
    /// Where this player's names are hidden in the session.
    Hiding: HiddenIdentity
    /// This player's mute in force when the session opens.
    Mute: Sanction voption
    /// A moderator gets the moderation commands.
    Role: PlayerRole
}

/// Why the server ends a session; the connection closes right after.
[<RequireQualifiedAccess>]
type SessionEnd =
    | AccessRevoked
    | Banned of Sanction
    | Kicked of SanctionReason
    /// The IP range of the connection was banned.
    | AddressBanned of AddressBan

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
    | PresenceChanged of PresenceChange * ActorValueKinds
    | PlayersMoved of MovementChange array
    | PlayerUpdateAccepted of requestId: uint64
    | GroundMarksChanged of GroundMarkView
    | GroundMarkPlaced of requestId: uint64 * GroundMarkRecord * evicted: GroundMarkId voption
    | GroundMarkRemoved of requestId: uint64 * GroundMarkId
    /// Full replacement of the player's own marks; no request ID.
    | OwnGroundMarks of GroundMarkRecord list
    /// Settles SetIdentityVisibility: where the names are hidden now and the pseudonym others see there.
    | IdentityVisibilityChanged of requestId: uint64 * Pseudonym voption * HiddenIdentity
    /// Settles ChangeDisplayName with the stored name.
    | DisplayNameChanged of requestId: uint64 * DisplayName
    /// Settles SetNameColor with the stored color.
    | NameColorChanged of requestId: uint64 * NameColor
    /// The player's own mute now, if any.
    | MuteChanged of Sanction voption
    | SessionEnded of SessionEnd
    | RoleChanged of PlayerRole
    | SanctionIssued of requestId: uint64 * Sanction
    | SanctionLifted of requestId: uint64 * PlayerId * SanctionKind
    | PlayerKicked of requestId: uint64 * PlayerId
    | SanctionList of requestId: uint64 * Sanction list
    | PlayerMarks of requestId: uint64 * PlayerId * GroundMarkRecord list
    | PlayerMarksCleared of requestId: uint64 * PlayerId * removed: int
    /// Every member drops the message; the request ID only in the requester's copy.
    | ChatMessageRemoved of requestId: uint64 voption * ChatChannelId * ChatMessageId
    /// After SessionOpened: the player's guilds and invitations, replacing what the client knew.
    | GuildsSnapshot of GuildState
    | GuildChanged of GuildChange
    /// Settles a guild command; the effect arrives as GuildChanged.
    | GuildCommandDone of requestId: uint64 * GuildId

/// How a response travels: its lane, the request it settles (none for a
/// notification) and whether it may leave while the session is still opening.
[<Struct>]
type ResponseDelivery = {
    Lane: DeliveryLane
    RequestId: uint64 voption
    WhileOpening: bool
}

