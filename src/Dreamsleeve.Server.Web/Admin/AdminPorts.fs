namespace Dreamsleeve.Server.Web.Admin

open System
open System.Threading
open System.Threading.Tasks
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure

/// A read-only block of the configuration page.
type ConfigSection = {
    Title: string
    Text: string
}

/// Everything the panel may do, as functions. The composition root connects
/// them to AdminService, AuthService and the runtime; tests pass fakes, so the
/// panel needs neither ENet nor SQLite. The panel has no business rules of its
/// own: every change goes through the owner that already has them.
type AdminPorts = {
    Admin: AdminCommand -> TimeSpan -> CancellationToken -> Task<AgentAskResult<Result<AdminReply, AdminServiceError>>>
    /// Trusted account commands: CreatePasswordReset, RevokeAccount, RenamePlayer.
    Account: AccountAccessCommand -> TimeSpan -> CancellationToken -> Task<AgentAskResult<Result<AccountAccessResult, AccountAccessError>>>
    Snapshot: TimeSpan -> CancellationToken -> Task<AgentAskResult<ServerRuntimeSnapshot>>
    Sessions: TimeSpan -> CancellationToken -> Task<AgentAskResult<RuntimeSessionRow list>>
    /// Asks one session for its view; None when it did not answer within the timeout.
    Describe: TimeSpan -> RuntimeSessionRow -> Task<AdminPlayerView option>
    /// False when the runtime did not take it.
    Announce: ServerAnnouncement -> bool
    ApplyRole: PlayerId -> PlayerRole -> bool
    /// The stored profile after a rename.
    ApplyProfile: PlayerData -> bool
    /// Ends the player's live session with the reason; false when the runtime did not take it.
    Kick: PlayerId -> SanctionReason -> bool
    Configuration: unit -> ConfigSection list
}

type AdminRouteSettings = {
    SessionHours: int
    LoginAttemptsPerMinute: int
    RequestsPerMinute: int
    RequestTimeoutSeconds: int
    MaxConnections: int
    /// How long the online table waits for each session.
    DescribeTimeoutMs: int
    Input: ChatInputLimits
    Moderation: ModerationRules
}
