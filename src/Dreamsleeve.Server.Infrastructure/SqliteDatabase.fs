namespace Dreamsleeve.Server.Infrastructure

open System
open System.IO
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Logging.Abstractions
open Migrondi.Core

type SqliteAccountStoreConfig = {
    DatabasePath: string
    BusyTimeoutSeconds: int
}

[<RequireQualifiedAccess>]
module internal SqliteDatabase =
    [<Literal>]
    let SchemaVersion = 4L

    [<Literal>]
    let ApplicationId = 1146309718L

    let connectionString config mode =
        SqliteConnectionStringBuilder(
            DataSource = Path.GetFullPath config.DatabasePath,
            Mode = mode,
            ForeignKeys = Nullable true,
            Pooling = true,
            DefaultTimeout = config.BusyTimeoutSeconds).ToString()

    let private scalar (connection: SqliteConnection) sql =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        command.ExecuteScalar()

    let private validateConfig config =
        if String.IsNullOrWhiteSpace config.DatabasePath || config.DatabasePath = ":memory:" then
            Error "Accounts.DatabasePath must name a persistent SQLite file."
        elif config.BusyTimeoutSeconds < 1 then
            Error "Accounts.BusyTimeoutSeconds must be positive."
        else
            Ok ()

    let private verifySchema (connection: SqliteConnection) =
        let version = scalar connection "PRAGMA user_version" :?> int64
        let application = scalar connection "PRAGMA application_id" :?> int64

        if version <> SchemaVersion || application <> ApplicationId then
            Error $"Unsupported account database schema: version={version}, application_id={application}."
        else
            // Preparing the actual projection catches missing/renamed columns before accepting clients.
            use command = connection.CreateCommand()
            command.CommandText <- "SELECT a.id, a.username, c.password_hash, p.player_id, p.account_id, p.display_name FROM accounts a LEFT JOIN account_passwords c ON c.account_id=a.id JOIN profiles p ON p.account_id = a.id LIMIT 0"
            use reader = command.ExecuteReader()
            reader.Close()
            command.CommandText <- "SELECT t.token_hash, t.account_id, t.kind, t.expires_at, i.provider, i.subject, i.account_id FROM auth_tokens t LEFT JOIN account_identities i ON i.account_id=t.account_id LIMIT 0"
            use credentials = command.ExecuteReader()
            credentials.Close()
            command.CommandText <- "SELECT m.id, m.author_id, m.kind, m.text, m.author_pseudonym, m.plugin_name, m.local_form_id, m.x, m.y, m.z, m.heading, m.created_at FROM ground_marks m JOIN profiles p ON p.player_id = m.author_id LIMIT 0"
            use marks = command.ExecuteReader()
            Ok ()

    /// Called before listeners start. SQLite and Migrondi execute synchronously;
    /// the application must offload this entire operation, like ordinary store calls.
    let initialize config migrationsDirectory =
        match validateConfig config with
        | Error error -> Error error
        | Ok () ->
            try
                let path = Path.GetFullPath config.DatabasePath
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore

                use connection = new SqliteConnection(connectionString config SqliteOpenMode.ReadWriteCreate)
                connection.Open()
                let version = scalar connection "PRAGMA user_version" :?> int64
                let application = scalar connection "PRAGMA application_id" :?> int64

                if version > SchemaVersion || version < 0L then
                    Error $"Account database version {version} is not supported by schema {SchemaVersion}."
                elif application <> 0L && application <> ApplicationId then
                    Error $"The SQLite file belongs to a different application ({application})."
                elif not (Directory.Exists migrationsDirectory) then
                    Error $"Account migrations directory is missing: {migrationsDirectory}"
                else
                    let migrationConfig = {
                        MigrondiConfig.Default with
                            connection = connection.ConnectionString
                            migrations = Path.GetFullPath migrationsDirectory
                    }
                    let migrations = Migrondi.MigrondiFactory(migrationConfig, AppContext.BaseDirectory, NullLogger.Instance)
                    migrations.Initialize()
                    migrations.RunUp() |> ignore

                    match verifySchema connection with
                    | Error error -> Error error
                    | Ok () ->
                        let mode = scalar connection "PRAGMA journal_mode=WAL" :?> string
                        if mode.Equals("wal", StringComparison.OrdinalIgnoreCase) then Ok ()
                        else Error $"SQLite did not enable WAL mode (returned {mode})."
            with error ->
                Error $"Could not initialize the account database: {error}"
