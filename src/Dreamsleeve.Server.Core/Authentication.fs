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

[<RequireQualifiedAccess>]
type DisplayNameChangeError =
    /// The previous own change was too recent; the name stays.
    | TooSoon of retryAfter: TimeSpan
    | Busy
    | Unavailable

type DisplayNameChangeReply = {
    OperationId: Guid
    /// The stored profile with the new name.
    Result: Result<PlayerData, DisplayNameChangeError>
}

/// A player's own display name change. The session has already validated the
/// name and checked the word list; the account service stores it and limits
/// how often it may happen.
type DisplayNameChangeRequest = {
    OperationId: Guid
    PlayerId: PlayerId
    DisplayName: DisplayName
    /// TimeSpan.Zero: no limit.
    MinInterval: TimeSpan
    ReplyTo: ReliableAgentRef<DisplayNameChangeReply>
}

/// The runtime observes this dependency but does not own the account service.
type SessionAuthenticator = {
    Requests: ReliableAgentRef<SessionAuthenticationRequest>
    DisplayNames: ReliableAgentRef<DisplayNameChangeRequest>
    Moderation: ReliableAgentRef<ModerationRequest>
    Completion: Task
}
