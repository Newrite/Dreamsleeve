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
    Address: string
    PlayerId: Nullable<uint64>
    Phase: string
    ConnectedAt: DateTimeOffset
    /// False when the session did not answer: the row shows "нет данных".
    Described: bool
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

type PlayerCardModel = {
    Player: PlayerModel
    Sessions: OnlineModel list
    Names: NameChangeModel list
    Sanctions: SanctionModel list
    Addresses: SignInAddressModel list
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

    let online (row: RuntimeSessionRow) (view: AdminPlayerView option) : OnlineModel =
        let playerId = row.PlayerId |> Option.map PlayerId.value |> Option.toNullable
        match view with
        | None ->
            { ConnectionId = string row.ConnectionId; Address = ClientAddress.text row.Address; PlayerId = playerId; Phase = phase row.Phase
              ConnectedAt = row.ConnectedAt; Described = false; Username = null; DisplayName = null; CharacterName = null; CharacterWithheld = false
              Hidden = null; Pseudonym = null; Role = null; Location = null; Level = Nullable() }
        | Some view ->
            { ConnectionId = string row.ConnectionId; Address = ClientAddress.text row.Address
              PlayerId = Nullable(PlayerId.value view.PlayerId); Phase = phase row.Phase
              ConnectedAt = row.ConnectedAt; Described = true
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

    let addressBan (ban: AddressBan) : AddressBanModel =
        { Id = ban.Id; Range = AddressRange.key ban.Range; Reason = SanctionReason.value ban.Reason; IssuedAt = ban.IssuedAt
          Expires = ban.Expires |> ValueOption.toNullable
          IssuedBy = ban.IssuedBy |> ValueOption.map (fun admin -> AuditTarget.key (AuditTarget.Admin admin)) |> ValueOption.defaultValue null }

    let addressMatch (entry: AddressMatch) : AddressMatchModel =
        { PlayerId = PlayerId.value entry.Player.PlayerId; Username = Username.value entry.Player.Username
          DisplayName = DisplayName.value entry.Player.DisplayName; Address = ClientAddress.text entry.Address.Address
          LastSeen = entry.Address.LastSeen }

    let token (info: ApiTokenInfo) : TokenModel =
        { Id = info.TokenHash; Prefix = info.TokenHash.Substring(0, min 8 info.TokenHash.Length); Label = ApiTokenLabel.value info.Label
          Owner = Username.value info.Owner; CreatedAt = info.CreatedAt }
