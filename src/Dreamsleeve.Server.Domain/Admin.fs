namespace Dreamsleeve.Server.Domain

open System
open FSharp.UMX

/// Rules of the server web panel. Administrators are not players: their
/// accounts, sessions and API tokens are separate from player accounts, so a
/// stolen game account never opens the panel. Storage, hashing and HTTP live
/// outside the domain; these functions only decide.
[<AutoOpen>]
module AdminUMX =
    [<Measure>]
    type adminId
    [<Measure>]
    type apiTokenLabel

type AdminId = int64<adminId>
type ApiTokenLabel = string<apiTokenLabel>

[<RequireQualifiedAccess>]
module AdminId =
    let value (id: AdminId) : int64 = UMX.untag id

    /// Storage issues positive IDs, like account IDs.
    let create raw : Result<AdminId, DomainError> =
        if raw <= 0L then Error(DomainError.InvalidId "AdminId") else Ok(UMX.tag<adminId> raw)

/// A signed-in administrator; the login reuses the player Username rules.
type AdminAccount = {
    Id: AdminId
    Username: Username
}

[<RequireQualifiedAccess>]
type AdminError =
    | PlayerNotFound
    | CodeInvalid
    | CodeExpired

/// What a player may do beyond playing. Stored as a number. A moderator
/// disciplines players (Sanctions.fs); moderators are equal to each other.
[<RequireQualifiedAccess>]
type PlayerRole =
    | Player
    | Moderator

type RoleAssignment = {
    PlayerId: PlayerId
    Role: PlayerRole
}

[<RequireQualifiedAccess>]
module PlayerRole =
    let all = [ PlayerRole.Player; PlayerRole.Moderator ]

    let toInt role =
        match role with
        | PlayerRole.Player -> 0
        | PlayerRole.Moderator -> 1

    let ofInt value =
        match value with
        | 0 -> ValueSome PlayerRole.Player
        | 1 -> ValueSome PlayerRole.Moderator
        | _ -> ValueNone

    /// Stable form and API key.
    let key role =
        match role with
        | PlayerRole.Player -> "player"
        | PlayerRole.Moderator -> "moderator"

    let ofKey (text: string) = all |> List.tryFind (fun role -> key role = text)

    /// Whether actor may discipline target: a moderator acts on players only,
    /// never on another moderator or on themselves. Administrators act from
    /// the panel and are not ranked here.
    let outranks actor target =
        match actor, target with
        | PlayerRole.Moderator, PlayerRole.Player -> true
        | (PlayerRole.Player | PlayerRole.Moderator), (PlayerRole.Player | PlayerRole.Moderator) -> false

    /// A role belongs to a registered player: the stored profile must exist.
    let assign (profile: PlayerData voption) role =
        match profile with
        | ValueSome profile -> Ok { PlayerId = profile.PlayerId; Role = role }
        | ValueNone -> Error AdminError.PlayerNotFound

/// Every mutation made from the panel, with a key that is stored in the audit
/// table and never changes once written.
[<RequireQualifiedAccess>]
type AdminAction =
    | SetRole
    | RenamePlayer
    | ResetPlayerPassword
    | RevokePlayerAccess
    | Announced
    | CreatedApiToken
    | RevokedApiToken
    | ResetAdminPassword
    | CreatedAdmin
    | SanctionedPlayer
    | LiftedSanction
    | KickedPlayer
    // Content a moderator removed in the game.
    | RemovedGroundMark
    | ClearedGroundMarks
    | DeletedChatMessage

[<RequireQualifiedAccess>]
module AdminAction =
    let all = [
        AdminAction.SetRole; AdminAction.RenamePlayer; AdminAction.ResetPlayerPassword; AdminAction.RevokePlayerAccess
        AdminAction.Announced; AdminAction.CreatedApiToken; AdminAction.RevokedApiToken
        AdminAction.ResetAdminPassword; AdminAction.CreatedAdmin
        AdminAction.SanctionedPlayer; AdminAction.LiftedSanction; AdminAction.KickedPlayer
        AdminAction.RemovedGroundMark; AdminAction.ClearedGroundMarks; AdminAction.DeletedChatMessage
    ]

    let key action =
        match action with
        | AdminAction.SetRole -> "set_role"
        | AdminAction.RenamePlayer -> "rename_player"
        | AdminAction.ResetPlayerPassword -> "reset_player_password"
        | AdminAction.RevokePlayerAccess -> "revoke_player_access"
        | AdminAction.Announced -> "announced"
        | AdminAction.CreatedApiToken -> "created_api_token"
        | AdminAction.RevokedApiToken -> "revoked_api_token"
        | AdminAction.ResetAdminPassword -> "reset_admin_password"
        | AdminAction.CreatedAdmin -> "created_admin"
        | AdminAction.SanctionedPlayer -> "sanction_player"
        | AdminAction.LiftedSanction -> "lift_sanction"
        | AdminAction.KickedPlayer -> "kick_player"
        | AdminAction.RemovedGroundMark -> "remove_ground_mark"
        | AdminAction.ClearedGroundMarks -> "clear_ground_marks"
        | AdminAction.DeletedChatMessage -> "delete_chat_message"

    let ofKey (text: string) = all |> List.tryFind (fun action -> key action = text)

/// What an action was applied to, written as "player:42", "admin:3",
/// "token:<hash prefix>" or "server".
[<RequireQualifiedAccess>]
type AuditTarget =
    | Player of PlayerId
    | Admin of AdminId
    | ApiToken of hashPrefix: string
    | Server

[<RequireQualifiedAccess>]
module AuditTarget =
    let key target =
        match target with
        | AuditTarget.Player id -> $"player:{PlayerId.value id}"
        | AuditTarget.Admin id -> $"admin:{AdminId.value id}"
        | AuditTarget.ApiToken prefix -> $"token:{prefix}"
        | AuditTarget.Server -> "server"

/// Details never contain passwords, codes or tokens; the caller passes only
/// public facts (a role, a new display name, announcement text).
type AuditRecord = {
    Action: AdminAction
    Target: AuditTarget
    Details: string
}

/// Who did an audited action: a panel administrator or a moderator in the game.
[<RequireQualifiedAccess>]
type AuditActor =
    | Admin of AdminId
    | Moderator of PlayerId

/// One stored audit line, with the actor's name at reading time; absent once
/// a moderator's profile is gone.
type AuditEntry = {
    Actor: AuditActor voption
    ActorName: Username voption
    Action: AdminAction
    Target: string
    Details: string
    At: DateTimeOffset
}

[<RequireQualifiedAccess>]
module AuditRecord =
    /// Longer details are cut: the audit line records the action, not a copy of data.
    [<Literal>]
    let MaxDetails = 512

    let create action target (details: string) =
        let text = if isNull details then "" else details
        let text = if text.Length > MaxDetails then text.Substring(0, MaxDetails) else text
        { Action = action; Target = target; Details = text }

/// What a one-time code opens: the first setup, or a new password for one administrator.
[<RequireQualifiedAccess>]
type AdminCodePurpose =
    | Setup
    | ResetPassword of AdminId

/// Outstanding one-time codes, kept only in memory by their owner. At most one
/// code per purpose: issuing replaces the previous one. Only hashes are kept.
type AdminCodes = private { codes: Map<AdminCodePurpose, struct (string * DateTimeOffset)> }

[<RequireQualifiedAccess>]
module AdminCodes =
    let empty = { codes = Map.empty }

    let count (codes: AdminCodes) = codes.codes.Count

    /// Issuing drops expired codes and replaces the one with the same purpose.
    let issue purpose hash (now: DateTimeOffset) (lifetime: TimeSpan) (codes: AdminCodes) =
        let live = codes.codes |> Map.filter (fun _ (struct (_, expires)) -> now < expires)
        { codes = live |> Map.add purpose (struct (hash, now + lifetime)) }

    /// A code is spent by the first attempt that names it, whatever happens next,
    /// so it can never be used twice. expected tells which purposes this page accepts.
    let redeem (expected: AdminCodePurpose -> bool) hash (now: DateTimeOffset) (codes: AdminCodes) =
        match codes.codes |> Map.tryFindKey (fun purpose (struct (stored, _)) -> stored = hash && expected purpose) with
        | None -> Error AdminError.CodeInvalid, codes
        | Some purpose ->
            let remaining = { codes = codes.codes |> Map.remove purpose }
            let struct (_, expires) = codes.codes[purpose]
            if now < expires then Ok purpose, remaining
            else Error AdminError.CodeExpired, remaining

/// A signed-in browser of the panel. The cookie carries a random token; storage
/// keeps only its hash, like saved logins of players.
type PanelSession = {
    TokenHash: string
    Admin: AdminId
    CreatedAt: DateTimeOffset
    ExpiresAt: DateTimeOffset
}

[<RequireQualifiedAccess>]
module PanelSession =
    let create tokenHash admin (now: DateTimeOffset) (lifetime: TimeSpan) =
        { TokenHash = tokenHash; Admin = admin; CreatedAt = now; ExpiresAt = now + lifetime }

    let isActive (now: DateTimeOffset) (session: PanelSession) = now < session.ExpiresAt

    /// A new password ends every session of that administrator.
    let revokedBy (admin: AdminId) (session: PanelSession) = session.Admin = admin

[<RequireQualifiedAccess>]
module ApiTokenLabel =
    [<Literal>]
    let MaxLength = 64

    let value (label: ApiTokenLabel) : string = UMX.untag label

    /// One line of 1..64 characters without control characters, trimmed.
    let create raw : Result<ApiTokenLabel, DomainError> =
        PrimitiveValidation.text "ApiTokenLabel" MaxLength PrimitiveValidation.nfcTrim false PrimitiveValidation.unrestricted raw
        |> Result.map UMX.tag

[<RequireQualifiedAccess>]
module AdminAnnouncement =
    /// Kinds an administrator publishes by hand; Periodic belongs to the schedule.
    let kinds = [ AnnouncementKind.Admin; AnnouncementKind.Announcement; AnnouncementKind.Event ]

    let kindKey kind =
        match kind with
        | AnnouncementKind.Admin -> "admin"
        | AnnouncementKind.Announcement -> "announcement"
        | AnnouncementKind.Event -> "event"
        | AnnouncementKind.Periodic -> "periodic"

    /// Text under the chat limit, like the console command, and one of the manual kinds.
    let create maxLength (text: string) (kind: string) =
        match ChatMessageText.create maxLength text, kinds |> List.tryFind (fun candidate -> kindKey candidate = kind) with
        | Ok text, Some kind -> Ok(text, kind)
        | Error error, _ -> Error error
        | Ok _, None -> Error(DomainError.InvalidText("AnnouncementKind", TextError.InvalidFormat))

/// The runtime's view of a session when the panel asked it.
[<RequireQualifiedAccess>]
type AdminSessionPhase =
    | Opening
    | Active

/// One online player as the panel shows it. See docs/ModerationAndNamesRu.md,
/// «Скрытое имя», paragraph «Админка.»: the panel is a server surface, so it sees
/// the real username, display name and character name of a hidden player next
/// to the pseudonym. This is the only place where that real identity is put
/// together for display; it never enters a game packet.
type AdminPlayerView = {
    PlayerId: PlayerId
    /// As stored, before moderation placeholders.
    Username: Username
    DisplayName: DisplayName
    /// The character name the game reported, also while it is withheld from others.
    CharacterName: CharacterName voption
    CharacterWithheld: bool
    Hiding: HiddenIdentity
    Pseudonym: Pseudonym voption
    Role: PlayerRole
    Location: PlayerLocation voption
    Details: PlayerDetails
    Phase: AdminSessionPhase
    OpenedAt: DateTimeOffset
}

[<RequireQualifiedAccess>]
module AdminPlayerView =
    /// account is the stored profile; player carries the game state of the session.
    let create (account: PlayerData) (player: Player) characterWithheld (pseudonym: Pseudonym voption) hiding role phase openedAt =
        { PlayerId = account.PlayerId
          Username = account.Username
          DisplayName = account.DisplayName
          CharacterName = player.CharacterName
          CharacterWithheld = characterWithheld
          Hiding = if pseudonym.IsSome then hiding else HiddenIdentity.Shown
          Pseudonym = pseudonym
          Role = role
          Location = player.Location
          Details = player.Details
          Phase = phase
          OpenedAt = openedAt }
