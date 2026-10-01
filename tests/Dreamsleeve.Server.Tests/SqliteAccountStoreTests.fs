module Dreamsleeve.Server.Tests.SqliteAccountStoreTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure
open Expecto

let private ok = function Ok value -> value | Error error -> failtestf "Unexpected error: %A" error
let private username value = Username.create 32 value |> ok
let private display value = DisplayName.create 64 value |> ok
let private token = CancellationToken.None

type Database() =
    let directory = Path.Combine(Path.GetTempPath(), "Dreamsleeve.AccountStoreTests", Guid.NewGuid().ToString("N"))
    let config = { DatabasePath = Path.Combine(directory, "accounts.db"); BusyTimeoutSeconds = 1 }

    member _.Config = config

    member _.Connect() =
        let builder = SqliteConnectionStringBuilder(
            DataSource = config.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = Nullable true, Pooling = false, DefaultTimeout = config.BusyTimeoutSeconds)
        Directory.CreateDirectory directory |> ignore
        let connection = new SqliteConnection(builder.ToString())
        connection.Open()
        connection

    member this.Execute sql =
        use connection = this.Connect()
        use command = connection.CreateCommand()
        command.CommandText <- sql
        command.ExecuteNonQuery() |> ignore

    member this.Scalar sql =
        use connection = this.Connect()
        use command = connection.CreateCommand()
        command.CommandText <- sql
        command.ExecuteScalar() :?> int64

    interface IDisposable with
        member _.Dispose() =
            let builder = SqliteConnectionStringBuilder(
                DataSource = config.DatabasePath, Mode = SqliteOpenMode.ReadWrite,
                ForeignKeys = Nullable true, Pooling = true, DefaultTimeout = config.BusyTimeoutSeconds)
            use pooled = new SqliteConnection(builder.ToString())
            SqliteConnection.ClearPool pooled
            builder.Mode <- SqliteOpenMode.ReadWriteCreate
            use bootstrap = new SqliteConnection(builder.ToString())
            SqliteConnection.ClearPool bootstrap
            if Directory.Exists directory then Directory.Delete(directory, true)

let tests = testList "SQLite accounts" [
    testCase "provider identity and saved session do not require a password credential" (fun () ->
        use database = new Database()
        SqliteAccountStore.initialize database.Config |> ok
        database.Execute "INSERT INTO accounts(id,username) VALUES(17,'external'); INSERT INTO profiles VALUES(23,17,'External'); INSERT INTO account_identities VALUES('steam','verified-subject',17)"
        let identity = SqliteAccountStore.findIdentity database.Config "steam" "verified-subject" token |> ok |> Option.get
        Expect.equal identity.AccountId 17L "Provider resolves an account, not a display name."
        Expect.isNone (SqliteAccountStore.find database.Config (username "external") token |> ok) "No implicit password login."
        SqliteAccountStore.remember database.Config identity.AccountId "test-token-hash" 0L 100L 8 token |> ok
        let resumed = SqliteAccountStore.resume database.Config "test-token-hash" 1L token |> ok
        Expect.equal resumed identity "Provider-independent token restores the same identity.")

    testCase "version one database migrates without changing credentials or player identity" (fun () ->
        use database = new Database()
        // Reconstruct the deployed v1 schema and migration marker, then run normal startup.
        database.Execute "CREATE TABLE accounts(id INTEGER PRIMARY KEY AUTOINCREMENT, username TEXT NOT NULL UNIQUE, password_hash TEXT NOT NULL); CREATE TABLE profiles(player_id INTEGER PRIMARY KEY AUTOINCREMENT, account_id INTEGER NOT NULL UNIQUE REFERENCES accounts(id), display_name TEXT NOT NULL); CREATE TABLE __migrondi_migrations(id INTEGER PRIMARY KEY, name VARCHAR(255) NOT NULL, timestamp BIGINT NOT NULL); INSERT INTO __migrondi_migrations VALUES (1, '1790467200000_accounts', 1790467200000); INSERT INTO accounts VALUES (17, 'legacy', 'legacy-hash'); INSERT INTO profiles VALUES (23,17,'Legacy'); PRAGMA application_id=1146309718; PRAGMA user_version=1"
        SqliteAccountStore.initialize database.Config |> ok
        let restored = SqliteAccountStore.find database.Config (username "legacy") token |> ok |> Option.get
        Expect.equal restored.AccountId 17L "Account identity survives migration."
        Expect.equal (PlayerId.value restored.Profile.PlayerId) 23UL "Player identity survives migration."
        Expect.equal restored.PasswordHash "legacy-hash" "Password credential survives migration."
        Expect.equal (database.Scalar "SELECT count(*) FROM account_identities WHERE provider='password' AND subject='legacy'") 1L "Local identity migrated.")

    testCase "restart preserves profile identity display name and password hash" (fun () ->
        use database = new Database()
        SqliteAccountStore.initialize database.Config |> ok
        let name = username "Player"
        let profile = SqliteAccountStore.create database.Config name (display "First Name") "stored-password-hash" token |> ok

        // Each call opens a new connection; bootstrap runs again as on process restart.
        SqliteAccountStore.initialize database.Config |> ok
        let restored = SqliteAccountStore.find database.Config (username "PLAYER") token |> ok |> Option.get

        Expect.equal restored.Profile profile "Persisted profile is unchanged."
        Expect.equal restored.PasswordHash "stored-password-hash" "The opaque password hash survives restart."
        Expect.isGreaterThan restored.AccountId 0L "Account IDs are positive."
        Expect.equal (database.Scalar "PRAGMA user_version") 10L "The applied schema is recorded."
        Expect.equal (database.Scalar "SELECT count(*) FROM __migrondi_migrations") 10L "Repeated startup does not reapply migration.")

    testCase "duplicate canonical username does not create an orphan profile or change its hash" (fun () ->
        use database = new Database()
        SqliteAccountStore.initialize database.Config |> ok
        let first = SqliteAccountStore.create database.Config (username "PLAYER") (display "First") "first-hash" token |> ok

        match SqliteAccountStore.create database.Config (username "player") (display "Second") "second-hash" token with
        | Error AccountStoreError.UsernameTaken -> ()
        | other -> failtestf "Expected UsernameTaken: %A" other

        Expect.equal (database.Scalar "SELECT count(*) FROM accounts") 1L "No duplicate account."
        Expect.equal (database.Scalar "SELECT count(*) FROM profiles") 1L "No orphan profile."
        let stored = SqliteAccountStore.find database.Config (username "player") token |> ok |> Option.get
        Expect.equal stored.Profile first "Duplicate registration preserves the original profile."
        Expect.equal stored.PasswordHash "first-hash" "Duplicate registration preserves the original credentials.")

    testCase "profile insert failure rolls back account registration" (fun () ->
        use database = new Database()
        SqliteAccountStore.initialize database.Config |> ok
        database.Execute "CREATE TRIGGER reject_profile BEFORE INSERT ON profiles BEGIN SELECT RAISE(ABORT, 'profile fixture failure'); END"

        match SqliteAccountStore.create database.Config (username "player") (display "Player") "hash" token with
        | Error(AccountStoreError.Failed error) -> Expect.stringContains error.Message "profile fixture failure" "Failure retains provider diagnostics."
        | other -> failtestf "Expected provider failure: %A" other

        Expect.equal (database.Scalar "SELECT count(*) FROM accounts") 0L "The account insert rolled back."
        Expect.equal (database.Scalar "SELECT count(*) FROM profiles") 0L "The profile insert rolled back."
        database.Execute "DROP TRIGGER reject_profile"
        SqliteAccountStore.create database.Config (username "player") (display "Player") "hash" token |> ok |> ignore)

    testCase "rehash uses compare-and-swap and survives subsequent initialization" (fun () ->
        use database = new Database()
        SqliteAccountStore.initialize database.Config |> ok
        let name = username "player"
        SqliteAccountStore.create database.Config name (display "Player") "old-hash" token |> ok |> ignore

        SqliteAccountStore.rehash database.Config name "old-hash" "new-hash" token |> ok
        SqliteAccountStore.rehash database.Config name "old-hash" "stale-rehash" token |> ok
        SqliteAccountStore.initialize database.Config |> ok

        let stored = SqliteAccountStore.find database.Config name token |> ok |> Option.get
        Expect.equal stored.PasswordHash "new-hash" "A stale rehash cannot overwrite newer credentials.")

    testCase "missing users remain absent and canceled calls do not mutate" (fun () ->
        use database = new Database()
        SqliteAccountStore.initialize database.Config |> ok
        let name = username "missing"
        Expect.isNone (SqliteAccountStore.find database.Config name token |> ok) "Find never registers an account."
        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()

        match SqliteAccountStore.create database.Config name (display "Missing") "hash" cancellation.Token with
        | Error AccountStoreError.Canceled -> ()
        | other -> failtestf "Expected cancellation: %A" other

        Expect.equal (database.Scalar "SELECT count(*) FROM accounts") 0L "Canceled work did not write.")

    testCase "a newer schema is rejected before changing the database" (fun () ->
        use database = new Database()
        database.Execute "PRAGMA user_version = 11"

        Expect.isError (SqliteAccountStore.initialize database.Config) "Older binaries must not open a newer schema."
        Expect.equal (database.Scalar "PRAGMA user_version") 11L "The version is preserved."
        Expect.equal (database.Scalar "SELECT count(*) FROM sqlite_master WHERE type = 'table'") 0L "No migrations were applied.")

    testCase "another application database and damaged schema are rejected" (fun () ->
        use foreign = new Database()
        foreign.Execute "PRAGMA application_id = 42"
        Expect.isError (SqliteAccountStore.initialize foreign.Config) "A foreign database is not migrated."

        use damaged = new Database()
        SqliteAccountStore.initialize damaged.Config |> ok
        damaged.Execute "ALTER TABLE profiles RENAME COLUMN display_name TO old_name"
        Expect.isError (SqliteAccountStore.initialize damaged.Config) "An incompatible schema fails startup.")

    testCase "committed account and player identifiers are not reused after deletion" (fun () ->
        use database = new Database()
        SqliteAccountStore.initialize database.Config |> ok
        let first = SqliteAccountStore.create database.Config (username "first") (display "First") "hash" token |> ok
        let account = SqliteAccountStore.find database.Config (username "first") token |> ok |> Option.get
        database.Execute "DELETE FROM accounts"

        let second = SqliteAccountStore.create database.Config (username "second") (display "Second") "hash" token |> ok
        let next = SqliteAccountStore.find database.Config (username "second") token |> ok |> Option.get
        Expect.isGreaterThan (PlayerId.value second.PlayerId) (PlayerId.value first.PlayerId) "Player IDs are never reused."
        Expect.isGreaterThan next.AccountId account.AccountId "Account IDs are never reused."
        Expect.equal (database.Scalar "SELECT count(*) FROM profiles") 1L "Foreign key cascades removed the deleted profile.")

    testCaseAsync "concurrent registration resolves uniqueness in the database" (async {
        use database = new Database()
        SqliteAccountStore.initialize database.Config |> ok
        let create () = SqliteAccountStore.create database.Config (username "same") (display "Same") "hash" token
        let! results = [| for _ in 1..8 -> Task.Run(Func<_>(create)) |] |> Task.WhenAll |> Async.AwaitTask
        let successes = results |> Array.filter Result.isOk
        let duplicates = results |> Array.filter (function Error AccountStoreError.UsernameTaken -> true | _ -> false)

        Expect.equal successes.Length 1 "Only one transaction creates the username."
        Expect.equal duplicates.Length 7 "Other attempts receive the expected conflict."
        Expect.equal (database.Scalar "SELECT count(*) FROM accounts") 1L "One account exists."
        Expect.equal (database.Scalar "SELECT count(*) FROM profiles") 1L "One profile exists."
    })
    testCase "account and player identifiers have independent sequences" (fun () ->
        use database = new Database()
        SqliteAccountStore.initialize database.Config |> ok
        database.Execute "INSERT INTO sqlite_sequence(name, seq) VALUES ('profiles', 10)"

        let profile = SqliteAccountStore.create database.Config (username "player") (display "Player") "hash" token |> ok
        let stored = SqliteAccountStore.find database.Config (username "player") token |> ok |> Option.get
        Expect.equal stored.AccountId 1L "Account identity uses the accounts sequence."
        Expect.equal (PlayerId.value profile.PlayerId) 11UL "Player identity uses the profiles sequence."
        Expect.equal stored.Profile profile "Read joins the separate identities correctly.")

    testCase "exhausted profile identity allocation rolls back account registration" (fun () ->
        use database = new Database()
        SqliteAccountStore.initialize database.Config |> ok
        database.Execute "INSERT INTO sqlite_sequence(name, seq) VALUES ('profiles', 9223372036854775807)"

        match SqliteAccountStore.create database.Config (username "player") (display "Player") "hash" token with
        | Error(AccountStoreError.Failed (:? SqliteException as error)) ->
            Expect.equal error.SqliteErrorCode 13 "SQLite reports SQLITE_FULL instead of wrapping the ID."
        | other -> failtestf "Expected SQLite identity exhaustion: %A" other

        Expect.equal (database.Scalar "SELECT count(*) FROM accounts") 0L "No account was committed without a profile."
        Expect.equal (database.Scalar "SELECT count(*) FROM profiles") 0L "No wrapped player identity was stored.")
]
