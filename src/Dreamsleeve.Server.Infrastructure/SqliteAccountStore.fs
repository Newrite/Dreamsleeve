namespace Dreamsleeve.Server.Infrastructure

open System
open System.IO
open System.Threading
open Microsoft.Data.Sqlite
open SqlHydra.Query
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure.AccountSchema
open Dreamsleeve.Server.Infrastructure.SqliteStatements

type StoredIdentity = {
    AccountId: int64
    Profile: PlayerData
    /// Read with the profile (player_roles); a player without a row plays.
    Role: PlayerRole
}

type StoredAccount = {
    AccountId: int64
    Profile: PlayerData
    Role: PlayerRole
    PasswordHash: string
}

[<RequireQualifiedAccess>]
type RenameOutcome =
    | Renamed of previous: DisplayName * StoredIdentity
    | NotFound
    /// The player's own change came sooner than the interval allows.
    | TooSoon of retryAfter: TimeSpan

[<RequireQualifiedAccess>]
type AccountStoreError =
    | UsernameTaken
    | InvalidCredential
    | Canceled
    | Failed of exn

/// Synchronous units of work. The caller admits them to a bounded worker outside
/// its agent handler; SQLite's async methods do not provide asynchronous I/O.
[<RequireQualifiedAccess>]
module SqliteAccountStore =
    let initialize config =
        SqliteDatabase.initialize config (Path.Combine(AppContext.BaseDirectory, "db", "migrations"))

    let private invalidData message =
        Error(AccountStoreError.Failed(InvalidDataException message))

    /// Shared by the account and ground mark stores: one connection and query context per unit of work.
    let internal withContext config (token: CancellationToken) action =
        if token.IsCancellationRequested then
            Error AccountStoreError.Canceled
        else
            try
                use connection = new SqliteConnection(SqliteDatabase.connectionString config SqliteOpenMode.ReadWrite)
                connection.Open()
                use context = new QueryContext(connection, SqliteEmitter())

                if token.IsCancellationRequested then Error AccountStoreError.Canceled
                else action context
            with error ->
                Error(AccountStoreError.Failed error)

    let private toProfile username (row: main.profiles) =
        if row.player_id <= 0L || row.account_id <= 0L then
            invalidData "The stored account/profile identifier is outside the positive Int64 range."
        else
            match PlayerId.create (uint64 row.player_id), DisplayName.create Int32.MaxValue row.display_name with
            | Ok playerId, Ok displayName when DisplayName.value displayName = row.display_name ->
                Ok(PlayerData.create playerId username displayName)
            | _ -> invalidData "The stored profile contains an invalid identifier or display name."

    let private toRole (value: int64) =
        match PlayerRole.ofInt (int value) with
        | ValueSome role when value >= 0L && value <= 1L -> Ok role
        | ValueSome _ | ValueNone -> invalidData "The stored player role is unknown."

    let find config (username: Username) token =
        withContext config token (fun context ->
            let name = Username.value username
            let query = select {
                for account in main.accounts do
                join profile in main.profiles on (account.id = profile.account_id)
                join password in main.account_passwords on (account.id = password.account_id)
                leftJoin role in main.player_roles on (profile.player_id = role.Value.player_id)
                where (account.username = name)
                select (account, profile, password, role)
            }

            match context.SelectOne query with
            | None -> Ok None
            | Some (account, profile, password, role) ->
                let role = role |> Option.map (fun row -> toRole row.role) |> Option.defaultValue (Ok PlayerRole.Player)
                match toProfile username profile, role with
                | Ok data, Ok role -> Ok (Some { AccountId = account.id; Profile = data; Role = role; PasswordHash = password.password_hash })
                | Error error, _ | _, Error error -> Error error)

    let private insertAccount (context: QueryContext) username =
        let row: main.accounts = { id = 0L; username = Username.value username }
        let query = insert {
            for account in main.accounts do
            entity row
            getId account.id
        }

        try
            context.Insert query |> Ok
        with :? SqliteException as error when error.SqliteExtendedErrorCode = 2067 ->
            Error AccountStoreError.UsernameTaken

    let create config (username: Username) (displayName: DisplayName) passwordHash (token: CancellationToken) =
        withContext config token (fun context ->
            use transaction = context.Connection.BeginTransaction()
            context.Transaction <- Some transaction

            match insertAccount context username with
            | Error error -> Error error
            | Ok accountId ->
                let password: main.account_passwords = { account_id = accountId; password_hash = passwordHash }
                let credential = insert {
                    for row in main.account_passwords do
                    entity password
                }
                context.Insert credential |> ignore

                let row: main.profiles = {
                    player_id = 0L
                    account_id = accountId
                    display_name = DisplayName.value displayName
                }
                let query = insert {
                    for profile in main.profiles do
                    entity row
                    getId profile.player_id
                }
                let playerId = context.Insert query

                match toProfile username { row with player_id = playerId } with
                | Error error -> Error error
                | Ok profile ->
                    if token.IsCancellationRequested then
                        Error AccountStoreError.Canceled
                    else
                        use identity = context.Connection.CreateCommand()
                        identity.Transaction <- transaction
                        identity.CommandText <- "INSERT INTO account_identities(provider, subject, account_id) VALUES ('password', @name, @id)"
                        identity.Parameters.Add(SqliteParameter("@name", Username.value username)) |> ignore
                        identity.Parameters.Add(SqliteParameter("@id", accountId)) |> ignore
                        identity.ExecuteNonQuery() |> ignore
                        transaction.Commit()
                        Ok profile)

    /// Compare-and-swap prevents a stale rehash from overwriting changed credentials.
    let rehash config (username: Username) expectedHash replacementHash token =
        withContext config token (fun context ->
            execute context "UPDATE account_passwords SET password_hash=@replacement WHERE password_hash=@expected AND account_id=(SELECT id FROM accounts WHERE username=@name)"
                [ "@replacement", box replacementHash; "@expected", box expectedHash; "@name", box (Username.value username) ] |> ignore
            Ok ())

    /// Every identity query selects these columns; the role row is optional.
    [<Literal>]
    let private IdentityColumns = "a.id, a.username, p.player_id, p.display_name, COALESCE(r.role, 0)"

    [<Literal>]
    let private RoleJoin = "LEFT JOIN player_roles r ON r.player_id=p.player_id"

    let private readIdentity (reader: System.Data.Common.DbDataReader) =
        if not (reader.Read()) then Ok None
        else
            match Username.create Int32.MaxValue (reader.GetString 1), PlayerId.create (uint64 (reader.GetInt64 2)),
                  DisplayName.create Int32.MaxValue (reader.GetString 3), toRole (reader.GetInt64 4) with
            | Ok username, Ok playerId, Ok displayName, Ok role when reader.GetInt64 0 > 0L ->
                Ok (Some { AccountId = reader.GetInt64 0; Profile = PlayerData.create playerId username displayName; Role = role })
            | _ -> invalidData "Invalid stored account identity."

    let findAccount config (username: Username) token =
        withContext config token (fun context ->
            use statement = command context
                                $"SELECT {IdentityColumns} FROM accounts a JOIN profiles p ON p.account_id=a.id {RoleJoin} WHERE a.username=@name"
                                [ "@name", box (Username.value username) ]
            use reader = statement.ExecuteReader()
            readIdentity reader)

    /// The provider adapter must verify its proof before resolving this mapping.
    let findIdentity config provider subject token =
        withContext config token (fun context ->
            use statement = command context
                                $"SELECT {IdentityColumns} FROM account_identities i JOIN accounts a ON a.id=i.account_id JOIN profiles p ON p.account_id=a.id {RoleJoin} WHERE i.provider=@provider AND i.subject=@subject"
                                [ "@provider", box provider; "@subject", box subject ]
            use reader = statement.ExecuteReader()
            readIdentity reader)

    let private accountForToken context kind hash now =
        use statement = command context
                            $"SELECT {IdentityColumns} FROM auth_tokens t JOIN accounts a ON a.id=t.account_id JOIN profiles p ON p.account_id=a.id {RoleJoin} WHERE t.token_hash=@hash AND t.kind=@kind AND t.expires_at>@now"
                            [ "@hash", box hash; "@kind", box kind; "@now", box now ]
        use reader = statement.ExecuteReader()
        readIdentity reader |> Result.bind (function Some account -> Ok account | None -> Error AccountStoreError.InvalidCredential)

    let private insertToken context accountId kind hash expires =
        execute context "INSERT INTO auth_tokens(token_hash, account_id, kind, expires_at) VALUES (@hash, @id, @kind, @expires)"
            [ "@hash", box hash; "@id", box accountId; "@kind", box kind; "@expires", box expires ] |> ignore

    let remember config accountId hash now expires maxTokens token =
        withContext config token (fun context ->
            use transaction = context.Connection.BeginTransaction()
            context.Transaction <- Some transaction
            execute context "DELETE FROM auth_tokens WHERE expires_at<=@now" [ "@now", box now ] |> ignore
            // Keep storage bounded per account. Oldest remembered devices expire first.
            execute context "DELETE FROM auth_tokens WHERE token_hash IN (SELECT token_hash FROM auth_tokens WHERE account_id=@id AND kind=0 ORDER BY expires_at DESC LIMIT -1 OFFSET @keep)"
                [ "@id", box accountId; "@keep", box (maxTokens - 1) ] |> ignore
            insertToken context accountId 0 hash expires
            transaction.Commit()
            Ok ())

    let resume config hash now token =
        withContext config token (fun context -> accountForToken context 0 hash now)

    let logout config hash token =
        withContext config token (fun context ->
            execute context "DELETE FROM auth_tokens WHERE token_hash=@hash AND kind=0" [ "@hash", box hash ] |> ignore
            Ok ())

    let revoke config accountId token =
        withContext config token (fun context ->
            execute context "DELETE FROM auth_tokens WHERE account_id=@id" [ "@id", box accountId ] |> ignore
            Ok ())

    let createReset config accountId hash expires token =
        withContext config token (fun context ->
            use transaction = context.Connection.BeginTransaction()
            context.Transaction <- Some transaction
            execute context "DELETE FROM auth_tokens WHERE account_id=@id" [ "@id", box accountId ] |> ignore
            insertToken context accountId 1 hash expires
            transaction.Commit()
            Ok ())

    let resetPassword config hash now passwordHash token =
        withContext config token (fun context ->
            use transaction = context.Connection.BeginTransaction()
            context.Transaction <- Some transaction
            match accountForToken context 1 hash now with
            | Error error -> Error error
            | Ok account ->
                execute context "INSERT INTO account_passwords(account_id, password_hash) VALUES (@id, @password) ON CONFLICT(account_id) DO UPDATE SET password_hash=excluded.password_hash"
                    [ "@password", box passwordHash; "@id", box account.AccountId ] |> ignore
                execute context "INSERT INTO account_identities(provider, subject, account_id) VALUES ('password', @name, @id) ON CONFLICT(provider, subject) DO NOTHING"
                    [ "@name", box (Username.value account.Profile.Username); "@id", box account.AccountId ] |> ignore
                execute context "DELETE FROM auth_tokens WHERE account_id=@id" [ "@id", box account.AccountId ] |> ignore
                transaction.Commit()
                Ok account.Profile)

    let private scalar (context: QueryContext) sql parameters =
        use statement = command context sql parameters
        statement.ExecuteScalar()

    /// Replaces the display name of an existing profile and records the change
    /// in display_name_changes, in one transaction; the name was validated and
    /// moderated by the caller. changedBy is the administrator, or ValueNone for
    /// the player's own change, which may come only minInterval after the
    /// previous own change (TimeSpan.Zero disables the limit). The same name is
    /// not a change: nothing is written. Only the newest keepHistory changes of
    /// the player stay in display_name_changes.
    let rename config (playerId: PlayerId) (displayName: DisplayName) (changedBy: AdminId voption) (minInterval: TimeSpan) (keepHistory: int) (now: DateTimeOffset) token =
        withContext config token (fun context ->
            let id = PlayerId.value playerId
            if id > uint64 Int64.MaxValue then Ok RenameOutcome.NotFound
            else
                use transaction = context.Connection.BeginTransaction()
                context.Transaction <- Some transaction
                let parameters = [ "@id", box (int64 id) ]
                match scalar context "SELECT display_name FROM profiles WHERE player_id=@id" parameters with
                | :? string as current ->
                    let previous = DisplayName.create Int32.MaxValue current
                    let last =
                        if changedBy.IsSome || minInterval <= TimeSpan.Zero then None
                        else
                            match scalar context "SELECT MAX(at) FROM display_name_changes WHERE player_id=@id AND changed_by IS NULL" parameters with
                            | :? int64 as at -> Some (DateTimeOffset.FromUnixTimeMilliseconds at)
                            | _ -> None
                    match previous, last with
                    | Error _, _ -> invalidData "The stored display name is invalid."
                    | Ok _, Some at when at + minInterval > now -> Ok (RenameOutcome.TooSoon(at + minInterval - now))
                    | Ok previous, _ ->
                        if previous <> displayName then
                            execute context "UPDATE profiles SET display_name=@name WHERE player_id=@id"
                                [ "@name", box (DisplayName.value displayName); "@id", box (int64 id) ] |> ignore
                            execute context "INSERT INTO display_name_changes(player_id, old_name, new_name, changed_by, at) VALUES (@id, @old, @new, @by, @at)"
                                [ "@id", box (int64 id); "@old", box current; "@new", box (DisplayName.value displayName)
                                  "@by", (match changedBy with ValueSome admin -> box (AdminId.value admin) | ValueNone -> box DBNull.Value)
                                  "@at", box (now.ToUnixTimeMilliseconds()) ] |> ignore
                            execute context "DELETE FROM display_name_changes WHERE player_id=@id AND id NOT IN (SELECT id FROM display_name_changes WHERE player_id=@id ORDER BY at DESC, id DESC LIMIT @keep)"
                                [ "@id", box (int64 id); "@keep", box (max 1 keepHistory) ] |> ignore
                        use statement = command context
                                            $"SELECT {IdentityColumns} FROM profiles p JOIN accounts a ON a.id=p.account_id {RoleJoin} WHERE p.player_id=@id"
                                            parameters
                        use reader = statement.ExecuteReader()
                        let identity = readIdentity reader
                        reader.Close()
                        match identity with
                        | Ok (Some stored) ->
                            transaction.Commit()
                            Ok (RenameOutcome.Renamed(previous, stored))
                        | Ok None -> Ok RenameOutcome.NotFound
                        | Error error -> Error error
                | _ -> Ok RenameOutcome.NotFound)
