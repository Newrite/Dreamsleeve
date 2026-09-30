namespace Dreamsleeve.Server.Web.Admin

open System
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure

// View models shared by the pages and /api/v1. Plain values only: JSON gets
// camelCase names and null for what is unknown; pages encode every string.

type StatusModel = {
    Connections: int
    Ready: int
    Reservations: int
    Closing: int
    Stopping: bool
}

/// One connection of the online table. The names are the real ones, also for a
/// player who hides them; see AdminPlayerView.
type OnlineModel = {
    ConnectionId: string
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

type PlayerCardModel = {
    Player: PlayerModel
    Sessions: OnlineModel list
}

type AuditModel = {
    Admin: string
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
        { Connections = snapshot.Connections; Ready = snapshot.Ready; Reservations = snapshot.Reservations
          Closing = snapshot.Closing; Stopping = snapshot.Stopping }

    let phase = function
        | RuntimeSessionPhase.Waiting -> "waiting"
        | RuntimeSessionPhase.Opening -> "opening"
        | RuntimeSessionPhase.Ready -> "ready"
        | RuntimeSessionPhase.Closing -> "closing"

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
            { ConnectionId = string row.ConnectionId; PlayerId = playerId; Phase = phase row.Phase; ConnectedAt = row.ConnectedAt
              Described = false; Username = null; DisplayName = null; CharacterName = null; CharacterWithheld = false
              Hidden = null; Pseudonym = null; Role = null; Location = null; Level = Nullable() }
        | Some view ->
            { ConnectionId = string row.ConnectionId; PlayerId = Nullable(PlayerId.value view.PlayerId); Phase = phase row.Phase
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

    let audit (entry: AuditEntry) : AuditModel =
        { Admin = Username.value entry.Admin.Username; Action = AdminAction.key entry.Action; Target = entry.Target
          Details = entry.Details; At = entry.At }

    let token (info: ApiTokenInfo) : TokenModel =
        { Id = info.TokenHash; Prefix = info.TokenHash.Substring(0, min 8 info.TokenHash.Length); Label = ApiTokenLabel.value info.Label
          Owner = Username.value info.Owner; CreatedAt = info.CreatedAt }
