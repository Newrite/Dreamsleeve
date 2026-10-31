namespace Dreamsleeve.Server.Infrastructure

open System
open System.Data.Common
open System.IO
open Microsoft.Data.Sqlite
open SqlHydra.Query
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure.AccountSchema
open Dreamsleeve.Server.Infrastructure.SqliteStatements

type StoredAdmin = {
    Account: AdminAccount
    PasswordHash: string
}

/// A player as the panel lists it: the stored profile and its role.
type PlayerRecord = {
    Profile: PlayerData
    Role: PlayerRole
}

type PlayerPage = {
    Players: PlayerRecord list
    Total: int
    /// 1-based.
    Page: int
}

/// One display name change of a player, newest first when listed.
type NameChange = {
    OldName: string
    NewName: string
    /// The administrator, or None when the player changed it.
    ChangedBy: Username option
    At: DateTimeOffset
}

type ApiTokenInfo = {
    /// The stored SHA-256 hash identifies the token in forms; it cannot be used as the token.
    TokenHash: string
    Label: ApiTokenLabel
    Owner: Username
    CreatedAt: DateTimeOffset
}

/// Synchronous units of work over the admin tables, run by the admin service's
/// bounded workers like the account store. Times are Unix milliseconds. Every
/// mutation that the panel makes writes its audit line in the same transaction.
[<RequireQualifiedAccess>]
module SqliteAdminStore =
    [<Literal>]
    let PageSize = 50

    let private invalidData message =
        Error(AccountStoreError.Failed(InvalidDataException message))

    let private milliseconds (time: DateTimeOffset) = time.ToUnixTimeMilliseconds()

    let private admin (id: int64) (name: string) =
        match AdminId.create id, Username.create Int32.MaxValue name with
        | Ok id, Ok username ->
            Ok {
                Id = id
                Username = username
            }
        | _ -> invalidData "A stored administrator is invalid."

    /// The one writer of audit lines: the panel's actions and the moderators'.
    let internal audit (context: QueryContext) (actor: AuditActor) (record: AuditRecord) now =
        let admin, moderator =
            match actor with
            | AuditActor.Admin id -> box (AdminId.value id), box DBNull.Value
            | AuditActor.Moderator id -> box DBNull.Value, box (int64 (PlayerId.value id))
        execute context "INSERT INTO admin_audit(admin_id, moderator_id, action, target, details, at) VALUES (@admin, @moderator, @action, @target, @details, @at)"
            [ "@admin", admin; "@moderator", moderator; "@action", box (AdminAction.key record.Action)
              "@target", box (AuditTarget.key record.Target); "@details", box record.Details; "@at", box (milliseconds now) ] |> ignore

    let private insertSession (context: QueryContext) (actor: AdminId) (session: PanelSession) =
        execute context "DELETE FROM admin_sessions WHERE expires_at<=@now" [ "@now", box (milliseconds session.CreatedAt) ] |> ignore
        let row: main.admin_sessions = {
            token_hash = session.TokenHash
            admin_id = AdminId.value actor
            created_at = milliseconds session.CreatedAt
            expires_at = milliseconds session.ExpiresAt
        }
        let query = insert {
            for stored in main.admin_sessions do
            entity row
        }
        context.Insert query |> ignore

    let countAdmins config token =
        SqliteAccountStore.withContext config token (fun context ->
            Ok (scalar context "SELECT COUNT(*) FROM admin_accounts" [] :?> int64))

    /// Creates the first administrator, its audit line and its first session in
    /// one transaction. None when an administrator already exists.
    let createFirstAdmin config (username: Username) passwordHash (session: AdminId -> PanelSession) (now: DateTimeOffset) token =
        SqliteAccountStore.withContext config token (fun context ->
            transaction context (fun () ->
                if scalar context "SELECT COUNT(*) FROM admin_accounts" [] :?> int64 <> 0L then Ok None
                else
                    let row: main.admin_accounts = {
                        id = 0L
                        username = Username.value username
                        password_hash = passwordHash
                        created_at = milliseconds now
                    }
                    let query = insert {
                        for stored in main.admin_accounts do
                        entity row
                        getId stored.id
                    }
                    let id = context.Insert query
                    match admin id row.username with
                    | Error error -> Error error
                    | Ok account ->
                        audit context (AuditActor.Admin account.Id) (AuditRecord.create AdminAction.CreatedAdmin (AuditTarget.Admin account.Id) (Username.value username)) now
                        insertSession context account.Id (session account.Id)
                        Ok (Some account)))

    let private AdminProjection =
        [|
            SqliteStored.Column.PositiveInteger
            SqliteStored.Column.Text
        |]

    let private SessionProjection =
        [|
            SqliteStored.Column.PositiveInteger
            SqliteStored.Column.Text
            SqliteStored.Column.UnixMilliseconds
        |]

    let private CredentialProjection =
        [|
            SqliteStored.Column.PositiveInteger
            SqliteStored.Column.Text
            SqliteStored.Column.Text
        |]

    let private TokenProjection =
        [|
            SqliteStored.Column.Text
            SqliteStored.Column.Text
            SqliteStored.Column.Text
            SqliteStored.Column.UnixMilliseconds
        |]

    let private PlayerProjection =
        [|
            SqliteStored.Column.PositiveInteger
            SqliteStored.Column.Text
            SqliteStored.Column.Text
            SqliteStored.Column.Int32
            SqliteStored.Column.Integer
        |]

    let private NameProjection =
        [|
            SqliteStored.Column.Text
            SqliteStored.Column.Text
            (SqliteStored.Column.Nullable SqliteStored.Column.Text)
            SqliteStored.Column.UnixMilliseconds
        |]

    let private AuditProjection =
        [|
            (SqliteStored.Column.Nullable SqliteStored.Column.PositiveInteger)
            (SqliteStored.Column.Nullable SqliteStored.Column.Text)
            (SqliteStored.Column.Nullable SqliteStored.Column.PositiveInteger)
            (SqliteStored.Column.Nullable SqliteStored.Column.Text)
            SqliteStored.Column.Text
            SqliteStored.Column.Text
            SqliteStored.Column.Text
            SqliteStored.Column.UnixMilliseconds
        |]

    let findAdmin config (username: Username) token =
        SqliteAccountStore.withContext config token (fun context ->
            use statement = command context "SELECT id, username, password_hash FROM admin_accounts WHERE username=@name" [ "@name", box (Username.value username) ]
            use reader = statement.ExecuteReader()
            if not (reader.Read()) then Ok None
            else
                SqliteAccountStore.storedRow reader 0 CredentialProjection (fun () ->
                    admin (reader.GetInt64 0) (reader.GetString 1)
                    |> Result.map (fun account -> Some {
                        Account = account
                        PasswordHash = reader.GetString 2
                    })))

    /// Compare-and-swap, like player rehashing.
    let rehashAdmin config (id: AdminId) expectedHash replacementHash token =
        SqliteAccountStore.withContext config token (fun context ->
            execute context "UPDATE admin_accounts SET password_hash=@replacement WHERE id=@id AND password_hash=@expected"
                [ "@replacement", box replacementHash; "@id", box (AdminId.value id); "@expected", box expectedHash ] |> ignore
            Ok ())

    /// A new password ends every session of the administrator and opens a new one.
    /// None when the administrator no longer exists.
    let setPassword config (id: AdminId) passwordHash (session: PanelSession) (now: DateTimeOffset) token =
        SqliteAccountStore.withContext config token (fun context ->
            transaction context (fun () ->
                use statement = command context "SELECT id, username FROM admin_accounts WHERE id=@id" [ "@id", box (AdminId.value id) ]
                use reader = statement.ExecuteReader()
                let found =
                    if reader.Read() then
                        SqliteAccountStore.storedRow reader 0 AdminProjection (fun () -> admin (reader.GetInt64 0) (reader.GetString 1) |> Result.map Some)
                    else Ok None
                reader.Close()

                match found with
                | Error error -> Error error
                | Ok None -> Ok None
                | Ok (Some account) ->
                    execute context "UPDATE admin_accounts SET password_hash=@hash WHERE id=@id" [ "@hash", box passwordHash; "@id", box (AdminId.value id) ] |> ignore
                    execute context "DELETE FROM admin_sessions WHERE admin_id=@id" [ "@id", box (AdminId.value id) ] |> ignore

                    audit context (AuditActor.Admin account.Id) (AuditRecord.create AdminAction.ResetAdminPassword (AuditTarget.Admin account.Id) "") now
                    insertSession context account.Id session
                    Ok (Some account)))

    let createSession config (session: PanelSession) token =
        SqliteAccountStore.withContext config token (fun context ->
            transaction context (fun () ->
                insertSession context session.Admin session
                Ok ()))

    let private adminFor context sql hash (now: DateTimeOffset voption) =
        use statement = command context sql [ "@hash", box hash ]
        use reader = statement.ExecuteReader()
        let projection = if now.IsSome then SessionProjection else AdminProjection
        if reader.Read() then
            SqliteAccountStore.storedRow reader 0 projection (fun () ->
                match now with
                | ValueSome time when reader.GetInt64 2 <= milliseconds time -> Ok None
                | ValueSome _ | ValueNone -> admin (reader.GetInt64 0) (reader.GetString 1) |> Result.map Some)
        else Ok None

    let findSession config hash (now: DateTimeOffset) token =
        SqliteAccountStore.withContext config token (fun context ->
            adminFor context "SELECT a.id, a.username, s.expires_at FROM admin_sessions s JOIN admin_accounts a ON a.id=s.admin_id WHERE s.token_hash=@hash" hash (ValueSome now))

    let deleteSession config hash token =
        SqliteAccountStore.withContext config token (fun context ->
            execute context "DELETE FROM admin_sessions WHERE token_hash=@hash" [ "@hash", box hash ] |> ignore
            Ok ())

    let createApiToken config (actor: AdminAccount) (hash: string) (label: ApiTokenLabel) (now: DateTimeOffset) token =
        SqliteAccountStore.withContext config token (fun context ->
            transaction context (fun () ->
                let row: main.admin_api_tokens = {
                    token_hash = hash
                    admin_id = AdminId.value actor.Id
                    label = ApiTokenLabel.value label
                    created_at = milliseconds now
                }
                let query = insert {
                    for stored in main.admin_api_tokens do
                    entity row
                }
                context.Insert query |> ignore

                audit context (AuditActor.Admin actor.Id) (AuditRecord.create AdminAction.CreatedApiToken (AuditTarget.ApiToken (hash.Substring(0, 8))) (ApiTokenLabel.value label)) now
                Ok ()))

    let findApiToken config hash token =
        SqliteAccountStore.withContext config token (fun context ->
            adminFor context "SELECT a.id, a.username FROM admin_api_tokens t JOIN admin_accounts a ON a.id=t.admin_id WHERE t.token_hash=@hash" hash ValueNone)

    let listApiTokens config token =
        SqliteAccountStore.withContext config token (fun context ->
            use statement = command context "SELECT t.token_hash, t.label, a.username, t.created_at FROM admin_api_tokens t JOIN admin_accounts a ON a.id=t.admin_id ORDER BY t.created_at, t.token_hash" []
            use reader = statement.ExecuteReader()
            let rows = ResizeArray()
            let mutable failure = None
            while failure.IsNone && reader.Read() do
                match SqliteStored.validate reader 0 TokenProjection with
                | Error error -> failure <- Some(AccountStoreError.Failed error)
                | Ok () ->
                    match ApiTokenLabel.create (reader.GetString 1), Username.create Int32.MaxValue (reader.GetString 2) with
                    | Ok label, Ok owner ->
                        rows.Add {
                            TokenHash = reader.GetString 0
                            Label = label
                            Owner = owner
                            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64 3)
                        }
                    | _ -> failure <- Some(AccountStoreError.Failed(InvalidDataException "A stored API token is invalid."))

            match failure with
            | Some error -> Error error
            | None -> Ok (List.ofSeq rows))

    /// False when no such token exists; nothing is audited then.
    let revokeApiToken config (actor: AdminAccount) (hash: string) (now: DateTimeOffset) token =
        SqliteAccountStore.withContext config token (fun context ->
            transaction context (fun () ->
                let removed = execute context "DELETE FROM admin_api_tokens WHERE token_hash=@hash" [ "@hash", box hash ]
                if removed > 0 then
                    audit context (AuditActor.Admin actor.Id) (AuditRecord.create AdminAction.RevokedApiToken (AuditTarget.ApiToken (hash.Substring(0, min 8 hash.Length))) "") now
                Ok (removed > 0)))

    let private readPlayer (reader: DbDataReader) =
        SqliteAccountStore.storedRow reader 0 PlayerProjection (fun () ->
            match PlayerId.create (uint64 (reader.GetInt64 0)), Username.create Int32.MaxValue (reader.GetString 1),
                  DisplayName.create Int32.MaxValue (reader.GetString 2), PlayerRole.ofInt (int (reader.GetInt64 3)), nameColor (reader.GetInt64 4) with
            | Ok playerId, Ok username, Ok displayName, ValueSome role, Ok color when reader.GetInt64 0 > 0L ->
                Ok {
                    Profile = PlayerData.create playerId username displayName color
                    Role = role
                }
            | _ -> invalidData "A stored player is invalid.")

    let private readPlayers (reader: DbDataReader) =
        let rows = ResizeArray()
        let mutable failure = None
        while failure.IsNone && reader.Read() do
            match readPlayer reader with
            | Ok row -> rows.Add row
            | Error error -> failure <- Some error

        match failure with
        | Some error -> Error error
        | None -> Ok (List.ofSeq rows)

    [<Literal>]
    let private PlayerColumns =
        "p.player_id, a.username, p.display_name, COALESCE(r.role, 0), p.name_color FROM profiles p JOIN accounts a ON a.id=p.account_id LEFT JOIN player_roles r ON r.player_id=p.player_id"

    let findPlayer config (playerId: PlayerId) token =
        SqliteAccountStore.withContext config token (fun context ->
            let id = PlayerId.value playerId
            if id > uint64 Int64.MaxValue then Ok None
            else
                use statement = command context $"SELECT {PlayerColumns} WHERE p.player_id=@id" [ "@id", box (int64 id) ]
                use reader = statement.ExecuteReader()
                readPlayers reader |> Result.map List.tryHead)

    /// LIKE wildcards typed by the administrator are literal characters.
    let escapeLike (text: string) =
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")

    /// Username or display name containing the text, or the exact PlayerId;
    /// an empty query lists everyone. Pages of PageSize, ordered by PlayerId.
    let searchPlayers config (query: string) page token =
        SqliteAccountStore.withContext config token (fun context ->
            let text = if isNull query then "" else query.Trim()
            let page = max 1 page
            let exact =
                match UInt64.TryParse text with
                | true, value when value <= uint64 Int64.MaxValue -> int64 value
                | _ -> -1L
            let filter = "WHERE (@text='' OR a.username LIKE @pattern ESCAPE '\\' OR p.display_name LIKE @pattern ESCAPE '\\' OR p.player_id=@exact)"
            let parameters = [ "@text", box text; "@pattern", box ("%" + escapeLike text + "%"); "@exact", box exact ]
            let total = scalar context $"SELECT COUNT(*) FROM profiles p JOIN accounts a ON a.id=p.account_id {filter}" parameters :?> int64
            use statement = command context $"SELECT {PlayerColumns} {filter} ORDER BY p.player_id LIMIT @limit OFFSET @offset"
                                (parameters @ [ "@limit", box PageSize; "@offset", box (int64 (page - 1) * int64 PageSize) ])
            use reader = statement.ExecuteReader()
            readPlayers reader
            |> Result.map (fun players -> {
                Players = players
                Total = int total
                Page = page
            }))

    /// Stores the role with its audit line. None when the player is not
    /// registered (PlayerRole.assign); the foreign key guards the same rule.
    let setRole config (actor: AdminAccount) (playerId: PlayerId) role (now: DateTimeOffset) token =
        SqliteAccountStore.withContext config token (fun context ->
            transaction context (fun () ->
                let id = PlayerId.value playerId
                let found =
                    if id > uint64 Int64.MaxValue then Ok None
                    else
                        use statement = command context $"SELECT {PlayerColumns} WHERE p.player_id=@id" [ "@id", box (int64 id) ]
                        use reader = statement.ExecuteReader()
                        readPlayers reader |> Result.map List.tryHead
                match found with
                | Error error -> Error error
                | Ok stored ->
                    match PlayerRole.assign (stored |> Option.map _.Profile |> ValueOption.ofOption) role with
                    | Error AdminError.PlayerNotFound | Error AdminError.CodeInvalid | Error AdminError.CodeExpired -> Ok None
                    | Ok assignment ->
                        execute context "INSERT INTO player_roles(player_id, role, granted_by, granted_at) VALUES (@id, @role, @admin, @at) ON CONFLICT(player_id) DO UPDATE SET role=excluded.role, granted_by=excluded.granted_by, granted_at=excluded.granted_at"
                            [ "@id", box (int64 id); "@role", box (PlayerRole.toInt assignment.Role); "@admin", box (AdminId.value actor.Id); "@at", box (milliseconds now) ] |> ignore

                        audit context (AuditActor.Admin actor.Id) (AuditRecord.create AdminAction.SetRole (AuditTarget.Player playerId) (PlayerRole.key role)) now
                        Ok (stored |> Option.map (fun record -> { record with Role = assignment.Role }))))

    /// The latest display name changes of a player, by the player and by administrators.
    let nameHistory config (playerId: PlayerId) limit token =
        SqliteAccountStore.withContext config token (fun context ->
            let id = PlayerId.value playerId
            if id > uint64 Int64.MaxValue then Ok []
            else
                use statement = command context "SELECT n.old_name, n.new_name, a.username, n.at FROM display_name_changes n LEFT JOIN admin_accounts a ON a.id=n.changed_by WHERE n.player_id=@id ORDER BY n.at DESC, n.id DESC LIMIT @limit"
                                    [ "@id", box (int64 id); "@limit", box (max 1 limit) ]
                use reader = statement.ExecuteReader()
                let rows = ResizeArray()
                let mutable failure = None
                while failure.IsNone && reader.Read() do
                    match SqliteStored.validate reader 0 NameProjection with
                    | Error error -> failure <- Some(AccountStoreError.Failed error)
                    | Ok () ->
                        let changedBy =
                            if reader.IsDBNull 2 then Ok None
                            else Username.create Int32.MaxValue (reader.GetString 2) |> Result.map Some
                        match changedBy with
                        | Ok changedBy ->
                            rows.Add {
                                OldName = reader.GetString 0
                                NewName = reader.GetString 1
                                ChangedBy = changedBy
                                At = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64 3)
                            }
                        | Error _ -> failure <- Some(AccountStoreError.Failed(InvalidDataException "A stored administrator name is invalid."))

                match failure with
                | Some error -> Error error
                | None -> Ok (List.ofSeq rows))

    /// An action that another owner performed (rename, reset, revoke, announcement).
    let record config (actor: AdminAccount) (entry: AuditRecord) (now: DateTimeOffset) token =
        SqliteAccountStore.withContext config token (fun context ->
            audit context (AuditActor.Admin actor.Id) entry now
            Ok ())

    /// Content a moderator removed in the game through another owner.
    let recordModeration config (moderator: PlayerId) (entry: AuditRecord) (now: DateTimeOffset) token =
        SqliteAccountStore.withContext config token (fun context ->
            audit context (AuditActor.Moderator moderator) entry now
            Ok ())

    let recentAudit config limit token =
        SqliteAccountStore.withContext config token (fun context ->
            use statement =
                command context
                    ("SELECT u.admin_id, a.username, u.moderator_id, m.username, u.action, u.target, u.details, u.at FROM admin_audit u "
                     + "LEFT JOIN admin_accounts a ON a.id=u.admin_id LEFT JOIN profiles p ON p.player_id=u.moderator_id "
                     + "LEFT JOIN accounts m ON m.id=p.account_id ORDER BY u.at DESC, u.id DESC LIMIT @limit")
                    [ "@limit", box (max 1 limit) ]
            use reader = statement.ExecuteReader()
            let rows = ResizeArray()
            let mutable failure = None
            let name index =
                if reader.IsDBNull index then Ok ValueNone
                else Username.create Int32.MaxValue (reader.GetString index) |> Result.map ValueSome
            let actor =
                fun () ->
                    if not (reader.IsDBNull 0) then AdminId.create (reader.GetInt64 0) |> Result.map (AuditActor.Admin >> ValueSome)
                    elif not (reader.IsDBNull 2) then PlayerId.create (uint64 (reader.GetInt64 2)) |> Result.map (AuditActor.Moderator >> ValueSome)
                    else Ok ValueNone
            while failure.IsNone && reader.Read() do
                match SqliteStored.validate reader 0 AuditProjection with
                | Error error -> failure <- Some(AccountStoreError.Failed error)
                | Ok () when not (reader.IsDBNull 0) && not (reader.IsDBNull 2) ->
                    failure <- Some(AccountStoreError.Failed(InvalidDataException "An audit line names two actors."))
                | Ok () ->
                    let actorName = if reader.IsDBNull 0 then name 3 else name 1
                    match actor (), actorName, AdminAction.ofKey (reader.GetString 4) with
                    | Ok actor, Ok actorName, Some action ->
                        rows.Add {
                            Actor = actor
                            ActorName = actorName
                            Action = action
                            Target = reader.GetString 5
                            Details = reader.GetString 6
                            At = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64 7)
                        }
                    | Error _, _, _ | _, Error _, _ -> failure <- Some (AccountStoreError.Failed(InvalidDataException "An audit line names an invalid actor."))
                    | Ok _, Ok _, None -> failure <- Some (AccountStoreError.Failed(InvalidDataException "An audit line has an unknown action."))

            match failure with
            | Some error -> Error error
            | None -> Ok (List.ofSeq rows))
