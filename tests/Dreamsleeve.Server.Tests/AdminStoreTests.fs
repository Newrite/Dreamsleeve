module Dreamsleeve.Server.Tests.AdminStoreTests

open System
open System.IO
open System.Threading
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure
open Expecto
open AgentTests
open SqliteAccountStoreTests

let private ok = function Ok value -> value | Error error -> failtestf "Unexpected error: %A" error
let private token = CancellationToken.None
let private name value = Username.create 32 value |> ok
let private now = DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero)
let private hours = TimeSpan.FromHours 12.

let private player (database: Database) username display =
    SqliteAccountStore.create database.Config (name username) (DisplayName.create 64 display |> ok) "hash" token |> ok

let private firstAdmin (database: Database) =
    let session id = PanelSession.create "session-1" id now hours
    SqliteAdminStore.createFirstAdmin database.Config (name "root") "admin-hash" session now token |> ok |> Option.get

let private admin5 = [ "admin_audit"; "player_roles"; "admin_api_tokens"; "admin_sessions"; "admin_accounts" ]

let private tableCount (database: Database) =
    let names = admin5 |> List.map (sprintf "'%s'") |> String.concat ","
    database.Scalar $"SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ({names})"

// The DOWN section of a checked-in migration, exactly as written.
let private downSql file =
    let rec find (directory: DirectoryInfo) =
        let candidate = Path.Combine(directory.FullName, "db", "migrations", file)
        if File.Exists candidate then candidate
        elif isNull directory.Parent then failtest "admin migration not found"
        else find directory.Parent
    let text = File.ReadAllText(find (DirectoryInfo AppContext.BaseDirectory))
    text.Substring(text.IndexOf("MIGRONDI:DOWN", StringComparison.Ordinal)).Split('\n', 2)[1]

let tests = testList "SQLite admin" [
    testCase "migrations 5 to 9 apply over schema 4, keep players and roll back" (fun () ->
        use database = new Database()
        SqliteAccountStore.initialize database.Config |> ok
        let alice = player database "alice" "Alice"
        database.Execute ((downSql "1791158400000_moderator_audit.sql") + "DELETE FROM __migrondi_migrations WHERE name LIKE '%moderator_audit%';"
                          + (downSql "1791072000000_sanctions.sql") + "DELETE FROM __migrondi_migrations WHERE name LIKE '%sanctions%';"
                          + (downSql "1790985600000_ground_mark_game_date.sql") + "DELETE FROM __migrondi_migrations WHERE name LIKE '%game_date%';"
                          + "DROP TABLE display_name_changes; DELETE FROM __migrondi_migrations WHERE name LIKE '%display_names%';"
                          + "DROP TABLE admin_audit; DROP TABLE player_roles; DROP TABLE admin_api_tokens; DROP TABLE admin_sessions; DROP TABLE admin_accounts;"
                          + "DELETE FROM __migrondi_migrations WHERE name LIKE '%admin%'; PRAGMA user_version = 4")
        equal 0L (tableCount database)
        SqliteAccountStore.initialize database.Config |> ok
        equal 9L (database.Scalar "PRAGMA user_version")
        equal 5L (tableCount database)
        equal (Some alice.PlayerId) (SqliteAccountStore.find database.Config (name "alice") token |> ok |> Option.map _.Profile.PlayerId)
        // The DOWN sections return to schemas 8, 7, 6, 5 and 4 without touching player data.
        database.Execute (downSql "1791158400000_moderator_audit.sql")
        equal 8L (database.Scalar "PRAGMA user_version")
        database.Execute (downSql "1791072000000_sanctions.sql")
        equal 7L (database.Scalar "PRAGMA user_version")
        database.Execute (downSql "1790985600000_ground_mark_game_date.sql")
        equal 6L (database.Scalar "PRAGMA user_version")
        database.Execute (downSql "1790899200000_display_names.sql")
        equal 5L (database.Scalar "PRAGMA user_version")
        database.Execute (downSql "1790812800000_admin.sql")
        equal 4L (database.Scalar "PRAGMA user_version")
        equal 0L (tableCount database)
        equal 1L (database.Scalar "SELECT count(*) FROM profiles"))

    testCase "the first administrator is created once, with its audit line and first session" (fun () ->
        use database = new Database()
        SqliteAccountStore.initialize database.Config |> ok
        equal 0L (SqliteAdminStore.countAdmins database.Config token |> ok)
        let root = firstAdmin database
        equal "root" (Username.value root.Username)
        let again = SqliteAdminStore.createFirstAdmin database.Config (name "second") "hash" (fun id -> PanelSession.create "session-2" id now hours) now token |> ok
        equal None again
        equal 1L (SqliteAdminStore.countAdmins database.Config token |> ok)
        equal (Some root) (SqliteAdminStore.findSession database.Config "session-1" now token |> ok)
        equal None (SqliteAdminStore.findSession database.Config "session-2" now token |> ok)
        let stored = SqliteAdminStore.findAdmin database.Config (name "root") token |> ok |> Option.get
        equal "admin-hash" stored.PasswordHash
        let audit = SqliteAdminStore.recentAudit database.Config 10 token |> ok
        equal [ AdminAction.CreatedAdmin ] (audit |> List.map _.Action)
        // A player name is not an administrator: the tables are separate.
        player database "root" "Root player" |> ignore
        equal 1L (SqliteAdminStore.countAdmins database.Config token |> ok))

    testCase "sessions expire, logout deletes one and a new password ends all of them" (fun () ->
        use database = new Database()
        SqliteAccountStore.initialize database.Config |> ok
        let root = firstAdmin database
        SqliteAdminStore.createSession database.Config (PanelSession.create "session-short" root.Id now (TimeSpan.FromMinutes 1.)) token |> ok
        equal (Some root) (SqliteAdminStore.findSession database.Config "session-short" (now + TimeSpan.FromSeconds 59.) token |> ok)
        equal None (SqliteAdminStore.findSession database.Config "session-short" (now + TimeSpan.FromMinutes 1.) token |> ok)
        SqliteAdminStore.deleteSession database.Config "session-1" token |> ok
        equal None (SqliteAdminStore.findSession database.Config "session-1" now token |> ok)
        SqliteAdminStore.createSession database.Config (PanelSession.create "session-3" root.Id now hours) token |> ok
        let changed = SqliteAdminStore.setPassword database.Config root.Id "new-hash" (PanelSession.create "session-4" root.Id now hours) now token |> ok
        equal (Some root) changed
        equal None (SqliteAdminStore.findSession database.Config "session-3" now token |> ok)
        equal (Some root) (SqliteAdminStore.findSession database.Config "session-4" now token |> ok)
        equal "new-hash" (SqliteAdminStore.findAdmin database.Config (name "root") token |> ok |> Option.get).PasswordHash
        equal None (SqliteAdminStore.setPassword database.Config (AdminId.create 99L |> ok) "x" (PanelSession.create "s" root.Id now hours) now token |> ok)
        equal AdminAction.ResetAdminPassword (SqliteAdminStore.recentAudit database.Config 1 token |> ok).Head.Action)

    testCase "API tokens are stored as hashes with a label and audited on creation and revocation" (fun () ->
        use database = new Database()
        SqliteAccountStore.initialize database.Config |> ok
        let root = firstAdmin database
        let hash = String('a', 64)
        SqliteAdminStore.createApiToken database.Config root hash (ApiTokenLabel.create "CI" |> ok) now token |> ok
        equal (Some root) (SqliteAdminStore.findApiToken database.Config hash token |> ok)
        let listed = SqliteAdminStore.listApiTokens database.Config token |> ok
        equal [ "CI" ] (listed |> List.map (_.Label >> ApiTokenLabel.value))
        equal [ "root" ] (listed |> List.map (_.Owner >> Username.value))
        check (SqliteAdminStore.revokeApiToken database.Config root hash now token |> ok) "revoked"
        check (not (SqliteAdminStore.revokeApiToken database.Config root hash now token |> ok)) "already gone"
        equal None (SqliteAdminStore.findApiToken database.Config hash token |> ok)
        let audit = SqliteAdminStore.recentAudit database.Config 10 token |> ok
        equal [ AdminAction.RevokedApiToken; AdminAction.CreatedApiToken; AdminAction.CreatedAdmin ] (audit |> List.map _.Action)
        equal "token:aaaaaaaa" audit.Head.Target)

    testCase "a role is stored only for a registered player and is read with the profile" (fun () ->
        use database = new Database()
        SqliteAccountStore.initialize database.Config |> ok
        let root = firstAdmin database
        let alice = player database "alice" "Alice"
        let missing = PlayerId.create 999UL |> ok
        equal None (SqliteAdminStore.setRole database.Config root missing PlayerRole.Moderator now token |> ok)
        let updated = SqliteAdminStore.setRole database.Config root alice.PlayerId PlayerRole.Moderator now token |> ok
        equal (Some PlayerRole.Moderator) (updated |> Option.map _.Role)
        equal PlayerRole.Moderator (SqliteAccountStore.find database.Config (name "alice") token |> ok |> Option.get).Role
        equal PlayerRole.Moderator (SqliteAccountStore.findAccount database.Config (name "alice") token |> ok |> Option.get).Role
        equal (Some PlayerRole.Moderator) (SqliteAdminStore.findPlayer database.Config alice.PlayerId token |> ok |> Option.map _.Role)
        SqliteAdminStore.setRole database.Config root alice.PlayerId PlayerRole.Player now token |> ok |> ignore
        equal PlayerRole.Player (SqliteAccountStore.find database.Config (name "alice") token |> ok |> Option.get).Role
        equal 1L (database.Scalar "SELECT count(*) FROM player_roles")
        let audit = SqliteAdminStore.recentAudit database.Config 10 token |> ok
        equal 2 (audit |> List.filter (fun entry -> entry.Action = AdminAction.SetRole) |> List.length)
        // Deleting the account removes the role with the profile.
        database.Execute "DELETE FROM accounts WHERE username='alice'"
        equal 0L (database.Scalar "SELECT count(*) FROM player_roles"))

    testCase "search treats LIKE wildcards literally, matches IDs exactly and pages by 50" (fun () ->
        use database = new Database()
        SqliteAccountStore.initialize database.Config |> ok
        let under = player database "a_b" "Under"
        player database "axb" "Other" |> ignore
        let percent = player database "percent" "100%"
        player database "hundred" "1000" |> ignore
        let search text = SqliteAdminStore.searchPlayers database.Config text 1 token |> ok
        equal [ under.PlayerId ] ((search "_").Players |> List.map _.Profile.PlayerId)
        equal [ percent.PlayerId ] ((search "%").Players |> List.map _.Profile.PlayerId)
        equal [ under.PlayerId ] ((search (string (PlayerId.value under.PlayerId))).Players |> List.map _.Profile.PlayerId |> List.filter ((=) under.PlayerId))
        equal 4 (search "").Total
        for index in 1 .. 50 do player database $"bulk{index}" $"Bulk {index}" |> ignore
        let first = search ""
        let second = SqliteAdminStore.searchPlayers database.Config "" 2 token |> ok
        equal 54 first.Total
        equal SqliteAdminStore.PageSize first.Players.Length
        equal 4 second.Players.Length
        equal 2 second.Page
        equal 50 (SqliteAdminStore.searchPlayers database.Config "bulk" 1 token |> ok).Total)

    testCase "a rename replaces the display name of an existing profile only" (fun () ->
        use database = new Database()
        SqliteAccountStore.initialize database.Config |> ok
        let alice = player database "alice" "Alice"
        let root = firstAdmin database
        let rename player value by interval at =
            SqliteAccountStore.rename database.Config player (DisplayName.create 64 value |> ok) by interval 20 at token |> ok
        match rename alice.PlayerId "Алиса" (ValueSome root.Id) TimeSpan.Zero now with
        | RenameOutcome.Renamed(previous, renamed) ->
            equal "Alice" (DisplayName.value previous)
            equal "Алиса" (DisplayName.value renamed.Profile.DisplayName)
            equal "alice" (Username.value renamed.Profile.Username)
        | other -> failtestf "%A" other
        equal "Алиса" (DisplayName.value (SqliteAccountStore.find database.Config (name "alice") token |> ok |> Option.get).Profile.DisplayName)
        equal RenameOutcome.NotFound (rename (PlayerId.create 404UL |> ok) "X" ValueNone TimeSpan.Zero now)
        // The player's own changes are limited by the interval; administrator changes are not counted.
        let day = TimeSpan.FromDays 1.
        match rename alice.PlayerId "Own One" ValueNone day now with
        | RenameOutcome.Renamed _ -> ()
        | other -> failtestf "%A" other
        equal (RenameOutcome.TooSoon(TimeSpan.FromHours 23.)) (rename alice.PlayerId "Own Two" ValueNone day (now + TimeSpan.FromHours 1.))
        match rename alice.PlayerId "By Admin" (ValueSome root.Id) day (now + TimeSpan.FromHours 1.) with
        | RenameOutcome.Renamed _ -> ()
        | other -> failtestf "%A" other
        match rename alice.PlayerId "Own Two" ValueNone day (now + day) with
        | RenameOutcome.Renamed _ -> ()
        | other -> failtestf "%A" other
        // The same name is not a change: no history line.
        match rename alice.PlayerId "Own Two" ValueNone TimeSpan.Zero (now + day) with
        | RenameOutcome.Renamed _ -> ()
        | other -> failtestf "%A" other
        let history = SqliteAdminStore.nameHistory database.Config alice.PlayerId 10 token |> ok
        equal [ "By Admin", "Own Two"; "Own One", "By Admin"; "Алиса", "Own One"; "Alice", "Алиса" ] (history |> List.map (fun change -> change.OldName, change.NewName))
        equal [ None; Some "root"; None; Some "root" ] (history |> List.map (_.ChangedBy >> Option.map Username.value))
        // Only the newest changes stay.
        let trimmed = SqliteAccountStore.rename database.Config alice.PlayerId (DisplayName.create 64 "Last" |> ok) (ValueSome root.Id) TimeSpan.Zero 2 (now + day) token |> ok
        match trimmed with
        | RenameOutcome.Renamed _ -> ()
        | other -> failtestf "%A" other
        let kept = SqliteAdminStore.nameHistory database.Config alice.PlayerId 10 token |> ok
        equal [ "Own Two", "Last"; "By Admin", "Own Two" ] (kept |> List.map (fun change -> change.OldName, change.NewName)))

    testCase "audit lines read back newest first with the administrator's name" (fun () ->
        use database = new Database()
        SqliteAccountStore.initialize database.Config |> ok
        let root = firstAdmin database
        let alice = player database "alice" "Alice"
        SqliteAdminStore.record database.Config root (AuditRecord.create AdminAction.RenamePlayer (AuditTarget.Player alice.PlayerId) "Alice -> Алиса") (now + TimeSpan.FromSeconds 1.) token |> ok
        SqliteAdminStore.record database.Config root (AuditRecord.create AdminAction.Announced AuditTarget.Server "admin: hi") (now + TimeSpan.FromSeconds 2.) token |> ok
        let entries = SqliteAdminStore.recentAudit database.Config 2 token |> ok
        equal [ AdminAction.Announced; AdminAction.RenamePlayer ] (entries |> List.map _.Action)
        equal [ "server"; $"player:{PlayerId.value alice.PlayerId}" ] (entries |> List.map _.Target)
        equal (ValueSome(AuditActor.Admin root.Id)) entries.Head.Actor
        equal (ValueSome "root") (entries.Head.ActorName |> ValueOption.map Username.value)
        equal (now + TimeSpan.FromSeconds 2.) entries.Head.At)
]
