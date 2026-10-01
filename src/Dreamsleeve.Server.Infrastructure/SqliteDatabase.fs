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
module SqliteAccountStoreConfig =
    [<Literal>]
    let MaxBusyTimeoutSeconds = 30

    let defaults = { DatabasePath = "data/dreamsleeve.db"; BusyTimeoutSeconds = 5 }

    /// [Database], checked once with the rest of the configuration.
    let validate config = [
        if String.IsNullOrWhiteSpace config.DatabasePath || config.DatabasePath = ":memory:" then
            "Database.DatabasePath must name a persistent SQLite file."
        if config.BusyTimeoutSeconds < 1 || config.BusyTimeoutSeconds > MaxBusyTimeoutSeconds then
            $"Database.BusyTimeoutSeconds must be 1..{MaxBusyTimeoutSeconds}."
    ]

[<RequireQualifiedAccess>]
module internal SqliteDatabase =
    [<Literal>]
    let SchemaVersion = 11L

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
            command.CommandText <- "SELECT m.id, m.author_id, m.kind, m.text, m.author_pseudonym, m.plugin_name, m.local_form_id, m.x, m.y, m.z, m.heading, m.created_at, m.game_era, m.game_year, m.game_month, m.game_day, m.game_day_of_week, m.game_hour, m.game_minute FROM ground_marks m JOIN profiles p ON p.player_id = m.author_id LIMIT 0"
            use marks = command.ExecuteReader()
            marks.Close()
            command.CommandText <- "SELECT a.id, a.username, a.password_hash, a.created_at, s.token_hash, s.admin_id, s.created_at, s.expires_at FROM admin_accounts a LEFT JOIN admin_sessions s ON s.admin_id = a.id LIMIT 0"
            use admins = command.ExecuteReader()
            admins.Close()
            command.CommandText <- "SELECT t.token_hash, t.admin_id, t.label, t.created_at, r.player_id, r.role, r.granted_by, r.granted_at, u.id, u.admin_id, u.moderator_id, u.action, u.target, u.details, u.at, n.id, n.player_id, n.old_name, n.new_name, n.changed_by, n.at FROM admin_api_tokens t, player_roles r, admin_audit u, display_name_changes n LIMIT 0"
            use panel = command.ExecuteReader()
            panel.Close()
            command.CommandText <- "SELECT s.id, s.player_id, s.kind, s.reason, s.issued_by_admin, s.issued_by_player, s.issued_at, s.expires_at, s.lifted_at FROM sanctions s LIMIT 0"
            use sanctions = command.ExecuteReader()
            sanctions.Close()
            command.CommandText <- "SELECT s.key, s.value, s.changed_by, s.changed_at FROM server_settings s LIMIT 0"
            use settings = command.ExecuteReader()
            settings.Close()
            command.CommandText <- "SELECT a.player_id, a.address, a.first_seen, a.last_seen, a.sign_ins, b.id, b.network, b.prefix, b.reason, b.issued_by, b.issued_at, b.expires_at, b.lifted_at FROM sign_in_addresses a, address_bans b LIMIT 0"
            use addresses = command.ExecuteReader()
            Ok ()

    /// Called before listeners start. SQLite and Migrondi execute synchronously;
    /// the application must offload this entire operation, like ordinary store calls.
    let initialize config migrationsDirectory =
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
