namespace Dreamsleeve.Server.Web.Admin

open System
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure

// View models shared by the pages and /api/v1. Plain values only: JSON gets
// camelCase names and null for what is unknown; pages encode every string.

type StatusModel = {
    Connections: int
    /// Connected without signing in; counted in Connections.
    Guests: int
    Ready: int
    Reservations: int
    Closing: int
    Stopping: bool
}

/// One connection of the online table. The names are the real ones, also for a
/// player who hides them; see AdminPlayerView.
type OnlineModel = {
    ConnectionId: string
    /// The connection's IP address (ClientAddress.text); the panel alone shows it.
    /// Through a proxy of the server, the address the player signed in from.
    Address: string
    /// The proxy of the server the player connects through; null: directly.
    Proxy: string
    PlayerId: Nullable<uint64>
    Phase: string
    ConnectedAt: DateTimeOffset
    /// Kept for API compatibility; true exactly when DescriptionStatus is available.
    Described: bool
    /// available, not_open, or unavailable; absence and request failure are distinct.
    DescriptionStatus: string
    Username: string
    DisplayName: string
    CharacterName: string
    CharacterWithheld: bool
    /// none, everywhere or except_ground_marks.
    Hidden: string
    Pseudonym: string
    Role: string
    Location: string
    Level: Nullable<uint32>
}

type PlayerModel = {
    PlayerId: uint64
    Username: string
    DisplayName: string
    Role: string
    Online: bool
}

type PlayerPageModel = {
    Query: string
    Page: int
    PageSize: int
    Total: int
    Players: PlayerModel list
}

/// A display name change; changedBy is null when the player changed it.
type NameChangeModel = {
    OldName: string
    NewName: string
    ChangedBy: string
    At: DateTimeOffset
}

/// A sanction in force: mute or ban, the reason, when it ends (null: until
/// lifted) and who issued it ("admin:3", "player:42"; null once that account is gone).
type SanctionModel = {
    Kind: string
    Reason: string
    IssuedAt: DateTimeOffset
    Expires: Nullable<DateTimeOffset>
    IssuedBy: string
}

/// A sanction in force with its player, for the sanctions page and REST.
type SanctionEntryModel = {
    PlayerId: uint64
    Username: string
    DisplayName: string
    Sanction: SanctionModel
}

/// An address the player signed in from; Range is what a ban of it suggests.
type SignInAddressModel = {
    Address: string
    FirstSeen: DateTimeOffset
    LastSeen: DateTimeOffset
    SignIns: int64
    Range: string
}

/// A device the player signed in from, by the start of its hash.
type SignInDeviceModel = {
    Device: string
    FirstSeen: DateTimeOffset
    LastSeen: DateTimeOffset
    SignIns: int64
}

/// A guild in a list. The master is null when the guild has none (the account
/// is gone): the panel appoints one from the members.
type GuildModel = {
    GuildId: uint64
    Name: string
    CreatedAt: DateTimeOffset
    Members: int
    MasterId: Nullable<uint64>
    Master: string
}

type GuildPageModel = {
    Query: string
    Page: int
    PageSize: int
    Total: int
    Guilds: GuildModel list
}

/// A member with the real names; role is member, officer or master. The mute
/// fields are null unless the guild's master or an officer muted the member
/// (mutedBy "player:42"; muteExpires null: until lifted).
type GuildMemberModel = {
    PlayerId: uint64
    Username: string
    DisplayName: string
    Role: string
    Online: bool
    JoinedAt: DateTimeOffset
    Muted: bool
    MuteReason: string
    MuteExpires: Nullable<DateTimeOffset>
    MutedBy: string
}

/// An invitation waiting for the player's answer; the names are null once
/// the account is gone.
type GuildInviteModel = {
    PlayerId: uint64
    Username: string
    DisplayName: string
    InvitedBy: string
    CreatedAt: DateTimeOffset
    Expires: DateTimeOffset
}

type GuildCardModel = {
    Guild: GuildModel
    Members: GuildMemberModel list
    Invites: GuildInviteModel list
}

/// A guild of one player, for the player card.
type PlayerGuildModel = {
    Guild: GuildModel
    Role: string
}

type PlayerCardModel = {
    Player: PlayerModel
    Sessions: OnlineModel list
    Names: NameChangeModel list
    Sanctions: SanctionModel list
    Addresses: SignInAddressModel list
    Devices: SignInDeviceModel list
    Guilds: PlayerGuildModel list
}

/// An IP range ban in force; issuedBy is "admin:3", null once that account is gone.
type AddressBanModel = {
    Id: int64
    Range: string
    Reason: string
    IssuedAt: DateTimeOffset
    Expires: Nullable<DateTimeOffset>
    IssuedBy: string
}

/// A player who signed in from a range being checked.
type AddressMatchModel = {
    PlayerId: uint64
    Username: string
    DisplayName: string
    Address: string
    LastSeen: DateTimeOffset
}

/// What a ban of Range would hit, shown before it is confirmed; the form
/// fields come back as they were sent.
type RangeCheckModel = {
    Range: string
    Reason: string
    Term: string
    Minutes: string
    Online: OnlineModel list
    Players: AddressMatchModel list
}

/// A player just created on the registration page, with the setup code shown once.
type CreatedPlayerModel = {
    PlayerId: uint64
    Username: string
    SetupCode: string
}

type AuditModel = {
    /// "администратор root" or "модератор alice".
    Actor: string
    Action: string
    Target: string
    Details: string
    At: DateTimeOffset
}

type TokenModel = {
    /// The stored hash; it identifies the token and cannot be used as one.
    Id: string
    Prefix: string
    Label: string
    Owner: string
    CreatedAt: DateTimeOffset
}

[<RequireQualifiedAccess>]
module AdminModels =
    let status (snapshot: ServerRuntimeSnapshot) : StatusModel =
        { Connections = snapshot.Connections; Guests = snapshot.Guests; Ready = snapshot.Ready; Reservations = snapshot.Reservations
          Closing = snapshot.Closing; Stopping = snapshot.Stopping }

    let phase = function
        | RuntimeSessionPhase.Waiting -> "waiting"
        | RuntimeSessionPhase.Guest -> "guest"
        | RuntimeSessionPhase.Opening -> "opening"
        | RuntimeSessionPhase.Ready -> "ready"
        | RuntimeSessionPhase.Closing -> "closing"

    /// The phase of a connection that has not signed in; its row has no names.
    let guestPhase = phase RuntimeSessionPhase.Guest

    /// Terms the panel offers, in minutes; absent: until lifted.
    let sanctionTerms = [
        "15 минут", ValueSome 15
        "1 час", ValueSome 60
        "1 день", ValueSome 1440
        "7 дней", ValueSome 10080
        "Бессрочно", ValueNone
    ]

    let sanction (value: Sanction) : SanctionModel =
        { Kind = SanctionKind.key value.Kind; Reason = SanctionReason.value value.Reason; IssuedAt = value.IssuedAt
          Expires = value.Expires |> ValueOption.toNullable
          IssuedBy =
            match value.IssuedBy with
            | ValueSome(SanctionIssuer.Admin admin) -> AuditTarget.key (AuditTarget.Admin admin)
            | ValueSome(SanctionIssuer.Moderator moderator) -> AuditTarget.key (AuditTarget.Player moderator)
            | ValueNone -> null }

    /// Who did an audited action, as the page names them.
    let actor (entry: AuditEntry) =
        let name = entry.ActorName |> ValueOption.map Username.value |> ValueOption.defaultValue "?"
        match entry.Actor with
        | ValueSome(AuditActor.Admin _) -> $"администратор {name}"
        | ValueSome(AuditActor.Moderator _) -> $"модератор {name}"
        | ValueNone -> "удалённый модератор"

    let sanctionEntry (record: SanctionRecord) : SanctionEntryModel =
        { PlayerId = PlayerId.value record.Target.PlayerId; Username = Username.value record.Target.Username
          DisplayName = DisplayName.value record.Target.DisplayName; Sanction = sanction record.Sanction }

    let hidden = function
        | HiddenIdentity.Shown -> "none"
        | HiddenIdentity.Everywhere -> "everywhere"
        | HiddenIdentity.ExceptGroundMarks -> "except_ground_marks"

    let private location (value: PlayerLocation voption) =
        match value with
        | ValueNone -> null
        | ValueSome value ->
            let name = LocationName.value value.Location.LocationName
            if String.IsNullOrWhiteSpace name then
                let id = value.Location.LocationId
                $"{PluginName.value id.PluginName}|{LocalFormId.value id.LocalFormId:X6}"
            else name

    let private proxy (row: RuntimeSessionRow) =
        match row.Proxy with
        | Some address -> ClientAddress.text address
        | None -> null

    let online (row: RuntimeSessionRow) (view: Result<AdminPlayerView option, SessionDescribeError>) : OnlineModel =
        let playerId = row.PlayerId |> Option.map PlayerId.value |> Option.toNullable
        match view with
        | Ok None | Error _ ->
            let status = match view with Ok None -> "not_open" | Error _ -> "unavailable" | Ok (Some _) -> "available"
            { ConnectionId = string row.ConnectionId; Address = ClientAddress.text row.Address; Proxy = proxy row; PlayerId = playerId; Phase = phase row.Phase
              ConnectedAt = row.ConnectedAt; Described = false; DescriptionStatus = status; Username = null; DisplayName = null; CharacterName = null; CharacterWithheld = false
              Hidden = null; Pseudonym = null; Role = null; Location = null; Level = Nullable() }
        | Ok (Some view) ->
            { ConnectionId = string row.ConnectionId; Address = ClientAddress.text row.Address; Proxy = proxy row
              PlayerId = Nullable(PlayerId.value view.PlayerId); Phase = phase row.Phase
              ConnectedAt = row.ConnectedAt; Described = true; DescriptionStatus = "available"
              Username = Username.value view.Username; DisplayName = DisplayName.value view.DisplayName
              CharacterName = view.CharacterName |> ValueOption.map CharacterName.value |> ValueOption.defaultValue null
              CharacterWithheld = view.CharacterWithheld
              Hidden = hidden view.Hiding
              Pseudonym = view.Pseudonym |> ValueOption.map Pseudonym.value |> ValueOption.defaultValue null
              Role = PlayerRole.key view.Role
              Location = location view.Location
              Level = view.Details.Level |> ValueOption.toNullable }

    let player online (record: PlayerRecord) : PlayerModel =
        { PlayerId = PlayerId.value record.Profile.PlayerId; Username = Username.value record.Profile.Username
          DisplayName = DisplayName.value record.Profile.DisplayName; Role = PlayerRole.key record.Role
          Online = online record.Profile.PlayerId }

    let page query (online: PlayerId -> bool) (page: PlayerPage) : PlayerPageModel =
        { Query = query; Page = page.Page; PageSize = SqliteAdminStore.PageSize; Total = page.Total
          Players = page.Players |> List.map (player online) }

    let nameChange (change: NameChange) : NameChangeModel =
        { OldName = change.OldName; NewName = change.NewName
          ChangedBy = change.ChangedBy |> Option.map Username.value |> Option.defaultValue null; At = change.At }

    let audit (entry: AuditEntry) : AuditModel =
        { Actor = actor entry; Action = AdminAction.key entry.Action; Target = entry.Target
          Details = entry.Details; At = entry.At }

    let signInAddress (entry: SignInAddress) : SignInAddressModel =
        { Address = ClientAddress.text entry.Address; FirstSeen = entry.FirstSeen; LastSeen = entry.LastSeen; SignIns = entry.SignIns
          Range = AddressRange.key (AddressRange.around entry.Address) }

    let signInDevice (entry: SignInDevice) : SignInDeviceModel =
        { Device = DeviceId.short entry.Device; FirstSeen = entry.FirstSeen; LastSeen = entry.LastSeen; SignIns = entry.SignIns }

    let addressBan (ban: AddressBan) : AddressBanModel =
        { Id = ban.Id; Range = AddressRange.key ban.Range; Reason = SanctionReason.value ban.Reason; IssuedAt = ban.IssuedAt
          Expires = ban.Expires |> ValueOption.toNullable
          IssuedBy = ban.IssuedBy |> ValueOption.map (fun admin -> AuditTarget.key (AuditTarget.Admin admin)) |> ValueOption.defaultValue null }

    let addressMatch (entry: AddressMatch) : AddressMatchModel =
        { PlayerId = PlayerId.value entry.Player.PlayerId; Username = Username.value entry.Player.Username
          DisplayName = DisplayName.value entry.Player.DisplayName; Address = ClientAddress.text entry.Address.Address
          LastSeen = entry.Address.LastSeen }

    let guild (summary: GuildSummary) : GuildModel =
        { GuildId = GuildId.value summary.Guild; Name = GuildName.value summary.Name; CreatedAt = summary.CreatedAt; Members = summary.Members
          MasterId = summary.Master |> ValueOption.map (fun master -> PlayerId.value master.PlayerId) |> ValueOption.toNullable
          Master = summary.Master |> ValueOption.map (fun master -> DisplayName.value master.DisplayName) |> ValueOption.defaultValue null }

    let guildPage query (page: GuildPage) : GuildPageModel =
        { Query = query; Page = page.Page; PageSize = GuildPage.Size; Total = page.Total; Guilds = page.Guilds |> List.map guild }

    let private guildMember (view: GuildMemberView) : GuildMemberModel =
        { PlayerId = PlayerId.value view.Profile.PlayerId; Username = Username.value view.Profile.Username
          DisplayName = DisplayName.value view.Profile.DisplayName; Role = GuildRole.key view.Membership.Role; Online = view.Online
          JoinedAt = view.Membership.JoinedAt
          Muted = view.Membership.Mute.IsSome
          MuteReason = view.Membership.Mute |> ValueOption.map (fun mute -> SanctionReason.value mute.Reason) |> ValueOption.defaultValue null
          MuteExpires = view.Membership.Mute |> ValueOption.bind _.Expires |> ValueOption.toNullable
          MutedBy = view.Membership.Mute |> ValueOption.map (fun mute -> AuditTarget.key (AuditTarget.Player mute.IssuedBy)) |> ValueOption.defaultValue null }

    let private guildInvite (invite: GuildInvite, profile: PlayerData voption) : GuildInviteModel =
        { PlayerId = PlayerId.value invite.Player
          Username = profile |> ValueOption.map (fun profile -> Username.value profile.Username) |> ValueOption.defaultValue null
          DisplayName = profile |> ValueOption.map (fun profile -> DisplayName.value profile.DisplayName) |> ValueOption.defaultValue null
          InvitedBy = AuditTarget.key (AuditTarget.Player invite.InvitedBy); CreatedAt = invite.CreatedAt; Expires = invite.Expires }

    let guildCard (card: GuildCard) : GuildCardModel =
        { Guild = guild card.Summary; Members = card.Members |> List.map guildMember; Invites = card.Invites |> List.map guildInvite }

    let playerGuild (summary: GuildSummary, role: GuildRole) : PlayerGuildModel = { Guild = guild summary; Role = GuildRole.key role }

    let token (info: ApiTokenInfo) : TokenModel =
        { Id = info.TokenHash; Prefix = info.TokenHash.Substring(0, min 8 info.TokenHash.Length); Label = ApiTokenLabel.value info.Label
          Owner = Username.value info.Owner; CreatedAt = info.CreatedAt }
