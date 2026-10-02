namespace Dreamsleeve.Server.Core

open System
open System.Threading.Tasks
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

[<RequireQualifiedAccess>]
type SessionAuthenticationError =
    | InvalidTicket
    | Unavailable

/// The stored profile, role and mute behind a consumed ticket.
type AuthenticatedPlayer = {
    Profile: PlayerData
    Role: PlayerRole
    /// The mute in force when the ticket was issued; later changes come as AccountChange.
    Mute: Sanction voption
    /// The address of the sign-in that issued the ticket, as the authentication
    /// host saw it (behind a trusted proxy, the forwarded client). A game
    /// connection from a proxy of the server is that player.
    SignedInFrom: System.Net.IPAddress voption
}

/// What the account service changed that live sessions must follow. The
/// service knows accounts, the runtime connections: it applies these.
[<RequireQualifiedAccess>]
type AccountChange =
    /// Saved logins are revoked: the player's sessions end.
    | AccessRevoked of PlayerId
    /// The player's sessions end; none opens while the ban holds.
    | Banned of Sanction
    /// The mute now in force, if any.
    | MuteChanged of PlayerId * Sanction voption
    /// The player's session ends now; nothing stops the next one.
    | Kicked of PlayerId * SanctionReason
    /// Every IP range ban in force, sent whole after each change and to a
    /// restarted runtime: connections from these ranges end, new ones are refused.
    | AddressBans of AddressBan list

/// What a moderator in the game asks of the account service, which checks the
/// rank against the stored roles and writes the audit line.
[<RequireQualifiedAccess>]
type ModerationCommand =
    | Sanction of SanctionOrder
    | Lift of PlayerId * SanctionKind * issuer: PlayerId
    | Kick of PlayerId * SanctionReason * issuer: PlayerId
    | ListSanctions
    /// The audit line of content the moderator removed through another owner.
    | Record of moderator: PlayerId * AuditRecord

[<RequireQualifiedAccess>]
type ModerationResult =
    | Sanctioned of Sanction
    | Lifted of Sanction
    | Kicked
    | Sanctions of Sanction list
    | Recorded

[<RequireQualifiedAccess>]
type ModerationError =
    | Refused of SanctionError
    | Busy
    | Unavailable

type ModerationReply = {
    OperationId: Guid
    Result: Result<ModerationResult, ModerationError>
}

type ModerationRequest = {
    OperationId: Guid
    Command: ModerationCommand
    ReplyTo: ReliableAgentRef<ModerationReply>
}

type SessionAuthenticationReply = {
    OperationId: Guid
    Result: Result<AuthenticatedPlayer, SessionAuthenticationError>
}

/// An opaque, short-lived credential; never include it in diagnostics.
type SessionAuthenticationRequest = {
    OperationId: Guid
    Ticket: string
    ReplyTo: ReliableAgentRef<SessionAuthenticationReply>
}

/// What a player changes in their own profile from the game.
[<RequireQualifiedAccess>]
type ProfileChange =
    /// The session has validated the name and checked the word list; the account
    /// service limits how often it may change (TimeSpan.Zero: no limit).
    | DisplayName of DisplayName * minInterval: TimeSpan
    /// The session has checked that the player may choose it, and how often.
    | NameColor of NameColor

[<RequireQualifiedAccess>]
type ProfileChangeError =
    /// The previous own display name change was too recent; the name stays.
    | TooSoon of retryAfter: TimeSpan
    | Busy
    | Unavailable

type ProfileChangeReply = {
    OperationId: Guid
    /// The stored profile with the change.
    Result: Result<PlayerData, ProfileChangeError>
}

/// A player's own profile change; the account service stores it.
type ProfileChangeRequest = {
    OperationId: Guid
    PlayerId: PlayerId
    Change: ProfileChange
    ReplyTo: ReliableAgentRef<ProfileChangeReply>
}

/// The runtime observes this dependency but does not own the account service.
type SessionAuthenticator = {
    Requests: ReliableAgentRef<SessionAuthenticationRequest>
    Profiles: ReliableAgentRef<ProfileChangeRequest>
    Moderation: ReliableAgentRef<ModerationRequest>
    Completion: Task
}
