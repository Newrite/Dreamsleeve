module Dreamsleeve.Server.Tests.SqliteBoundaryTests

open System
open System.Data
open System.Collections.Concurrent
open System.IO
open System.Threading
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Logging
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure
open Expecto
open AgentTests
open BackgroundTests

let private ok = function
    | Ok value -> value
    | Error error -> failtestf "Unexpected result: %A" error

let private token = CancellationToken.None
let private now = DateTimeOffset.FromUnixTimeMilliseconds 1_800_000_000_000L
let private invalid = function
    | Error(AccountStoreError.Failed (:? InvalidDataException)) -> ()
    | other -> failtestf "Expected invalid stored representation, received %A" other
let private initialize (database: SqliteAccountStoreTests.Database) = SqliteAccountStore.initialize database.Config |> ok
let private register (database: SqliteAccountStoreTests.Database) =
    SqliteAccountStore.create database.Config (Username.create 32 "player" |> ok) (DisplayName.create 64 "Player" |> ok) "hash" token |> ok
let private thrown action =
    try
        action () |> ignore
        None
    with error -> Some error

// Auth bootstrap calls once before the worker; admin preparation calls once
// before its worker. A second-call fault therefore targets the detached request,
// and subsequent calls prove a fresh request can run after that isolated failure.
type private FaultOnSecondClock(original: exn) =
    inherit TimeProvider()
    let mutable calls = 0
    override _.GetUtcNow() =
        if Interlocked.Increment(&calls) = 2 then raise original
        else now
    member _.Calls = Volatile.Read(&calls)

let private recordingLogger (errors: ConcurrentQueue<exn>) =
    { new ILogger with
        member _.BeginScope<'T>(_: 'T) = { new IDisposable with member _.Dispose() = () }
        member _.IsEnabled _ = true
        member _.Log<'T>(_, _, _: 'T, error, _: Func<'T, exn, string>) =
            if not (isNull error) then errors.Enqueue error }

let tests = testList "SQLite boundaries" [
    testCase "real provider coercion cannot turn non-integer roles into trusted values" (fun () ->
        use database = new SqliteAccountStoreTests.Database()
        use connection = database.Connect()
        use statement = connection.CreateCommand()
        statement.CommandText <- "SELECT 1.75 AS role, 'broken' AS text_role, 4294967297 AS wide_role"
        use reader = statement.ExecuteReader()
        Expect.isTrue (reader.Read()) "Fixture row exists."
        Expect.equal (reader.GetInt64 0) 1L "The actual provider truncates a REAL integer getter."
        Expect.equal (reader.GetInt64 1) 0L "The actual provider coerces nonnumeric text."
        Expect.isTrue (thrown (fun () -> reader.GetInt32 2) |> Option.exists (fun error -> error :? OverflowException)) "The actual Int32 getter throws on an oversized integer."
        Expect.isTrue (reader.GetValue 0 :? double) "Raw value retains the REAL representation."
        Expect.isTrue (reader.GetValue 1 :? string) "Raw value retains the TEXT representation.")

    testCase "account projections reject coerced roles and negative signed player identifiers" (fun () ->
        for role in [ "1.75"; "'broken'"; "4294967297" ] do
            use database = new SqliteAccountStoreTests.Database()
            initialize database
            let player = register database
            database.Execute $"PRAGMA ignore_check_constraints=ON; INSERT INTO player_roles(player_id,role,granted_at) VALUES({PlayerId.value player.PlayerId},{role},0)"
            SqliteAccountStore.find database.Config player.Username token |> invalid
            SqliteAccountStore.findAccount database.Config player.Username token |> invalid
            SqliteAccountStore.findIdentity database.Config "password" "player" token |> invalid
            SqliteAdminStore.findPlayer database.Config player.PlayerId token |> invalid
        use database = new SqliteAccountStoreTests.Database()
        initialize database
        let player = register database
        database.Execute "PRAGMA ignore_check_constraints=ON; UPDATE profiles SET player_id=-1"
        SqliteAccountStore.find database.Config player.Username token |> invalid
        SqliteAccountStore.findAccount database.Config player.Username token |> invalid)

    testCase "scalar absence remains distinct from malformed settings and name-change time" (fun () ->
        use database = new SqliteAccountStoreTests.Database()
        initialize database
        let player = register database
        Expect.equal (SqliteAccountStore.registrationMode database.Config token |> ok) RegistrationMode.initial "A genuinely absent setting uses its documented default."
        database.Execute "INSERT INTO server_settings(key,value,changed_at) VALUES('registration_mode',X'FF',0)"
        SqliteAccountStore.registrationMode database.Config token |> invalid
        database.Execute $"INSERT INTO display_name_changes(player_id,old_name,new_name,at) VALUES({PlayerId.value player.PlayerId},'Old','Player','broken')"
        SqliteAccountStore.rename database.Config player.PlayerId (DisplayName.create 64 "New" |> ok) ValueNone (TimeSpan.FromMinutes 1.) 50 now token |> invalid
        Expect.equal (SqliteAccountStore.findAccount database.Config player.Username token |> ok |> Option.get).Profile.DisplayName player.DisplayName "Malformed history cannot authorize an early rename.")

    testCase "saved credentials reject non-integer expiry without changing valid expired-token semantics" (fun () ->
        use database = new SqliteAccountStoreTests.Database()
        initialize database
        let player = register database
        let account = SqliteAccountStore.findAccount database.Config player.Username token |> ok |> Option.get
        for expires in [ "'broken'"; "X'FF'"; "9999999999.5"; "0.5" ] do
            database.Execute $"DELETE FROM auth_tokens; INSERT INTO auth_tokens(token_hash,account_id,kind,expires_at) VALUES('saved',{account.AccountId},0,{expires})"
            SqliteAccountStore.resume database.Config "saved" 100L token |> invalid
        database.Execute "UPDATE auth_tokens SET expires_at=99"
        Expect.equal (SqliteAccountStore.resume database.Config "saved" 100L token) (Error AccountStoreError.InvalidCredential) "A valid expired token remains invalid credentials."
        database.Execute "UPDATE auth_tokens SET expires_at=9223372036854775807"
        Expect.equal (SqliteAccountStore.resume database.Config "saved" 100L token |> ok) account "Seconds remain Int64, without a new DateTime conversion/range policy.")

    testCase "admin sessions reject malformed expiry while API tokens retain their separate projection" (fun () ->
        use database = new SqliteAccountStoreTests.Database()
        initialize database
        database.Execute "INSERT INTO admin_accounts(id,username,password_hash,created_at) VALUES(1,'root','hash',0); INSERT INTO admin_sessions(token_hash,admin_id,created_at,expires_at) VALUES('session',1,0,'broken')"
        SqliteAdminStore.findSession database.Config "session" now token |> invalid
        database.Execute "UPDATE admin_sessions SET expires_at=0.5"
        SqliteAdminStore.findSession database.Config "session" now token |> invalid
        database.Execute "UPDATE admin_sessions SET expires_at=0"
        Expect.isNone (SqliteAdminStore.findSession database.Config "session" now token |> ok) "A legitimately expired admin session remains absent."
        database.Execute "INSERT INTO admin_api_tokens(token_hash,admin_id,label,created_at) VALUES('api',1,'label',0)"
        Expect.isSome (SqliteAdminStore.findApiToken database.Config "api" token |> ok) "API tokens have no session expiry field.")

    testCase "device dates and required strings reject malformed representation" (fun () ->
        for firstSeen, device in [ "9223372036854775807", "'device'"; "0", "X'FF'" ] do
            use database = new SqliteAccountStoreTests.Database()
            initialize database
            let player = register database
            database.Execute $"PRAGMA ignore_check_constraints=ON; INSERT INTO player_devices(player_id,device,first_seen,last_seen,sign_ins) VALUES({PlayerId.value player.PlayerId},{device},{firstSeen},0,1)"
            SqliteDeviceStore.history database.Config player.PlayerId token |> invalid)

    testCase "supported timestamp endpoints and integer numeric projections remain valid" (fun () ->
        use database = new SqliteAccountStoreTests.Database()
        initialize database
        let player = register database
        let earliest = DateTimeOffset.MinValue.ToUnixTimeMilliseconds()
        let latest = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
        let device = DeviceId.create (String('a', DeviceId.Length)) |> ok |> DeviceId.value
        database.Execute $"INSERT INTO player_devices(player_id,device,first_seen,last_seen,sign_ins) VALUES({PlayerId.value player.PlayerId},'{device}',{earliest},{latest},1)"
        let entry = SqliteDeviceStore.history database.Config player.PlayerId token |> ok |> List.head
        Expect.equal (entry.FirstSeen.ToUnixTimeMilliseconds()) earliest "Earliest supported stored millisecond is retained."
        Expect.equal (entry.LastSeen.ToUnixTimeMilliseconds()) latest "Latest supported stored millisecond is retained."
        use connection = database.Connect()
        use statement = connection.CreateCommand()
        statement.CommandText <- "SELECT 10 AS coordinate"
        use reader = statement.ExecuteReader()
        Expect.isTrue (reader.Read()) "Integer numeric fixture exists."
        SqliteStored.validate reader 0 [| SqliteStored.Column.Number |] |> ok
        Expect.equal (reader.GetDouble 0) 10. "A legitimate SQLite integer numeric projection remains readable.")

    testCase "address prefixes cannot alias through Int32 and blob types stay explicit" (fun () ->
        for network, prefix, at in [ "X'00000000000000000000000000000000'", "4294967328", "0"; "'0000000000000000'", "32", "0"; "X'00000000000000000000000000000000'", "32", "9223372036854775807" ] do
            use database = new SqliteAccountStoreTests.Database()
            initialize database
            database.Execute $"PRAGMA ignore_check_constraints=ON; INSERT INTO address_bans(network,prefix,reason,issued_at) VALUES({network},{prefix},'reason',{at})"
            SqliteAddressStore.active database.Config now token |> invalid)

    testCase "guild member roles and invitation identifiers reject full-width and signed aliases" (fun () ->
        for role in [ "4294967296"; "1.75"; "'broken'" ] do
            use database = new SqliteAccountStoreTests.Database()
            initialize database
            let player = register database
            database.Execute $"PRAGMA ignore_check_constraints=ON; INSERT INTO guilds(id,name,name_key,created_at) VALUES(1,'Guild','guild',0); INSERT INTO guild_members(guild_id,player_id,role,joined_at) VALUES(1,{PlayerId.value player.PlayerId},{role},0)"
            SqliteGuildStore.loadAll database.Config token |> invalid
        use database = new SqliteAccountStoreTests.Database()
        initialize database
        let player = register database
        database.Execute $"INSERT INTO guilds(id,name,name_key,created_at) VALUES(1,'Guild','guild',0); INSERT INTO guild_invites(guild_id,player_id,invited_by,created_at,expires_at) VALUES(1,{PlayerId.value player.PlayerId},-1,0,100)"
        SqliteGuildStore.loadAll database.Config token |> invalid)

    testCase "ground mark projection preserves numeric positions but rejects text coercion" (fun () ->
        use database = new SqliteAccountStoreTests.Database()
        initialize database
        let player = register database
        database.Execute $"INSERT INTO ground_marks(id,author_id,kind,text,plugin_name,local_form_id,x,y,z,heading,created_at) VALUES(1,{PlayerId.value player.PlayerId},1,'note','skyrim.esm',1,0,1,2,0,0)"
        Expect.equal (SqliteGroundMarkStore.loadAll database.Config token |> ok).Marks.Length 1 "Legitimate numeric coordinates survive the new raw projection."
        database.Execute "UPDATE ground_marks SET x='broken' WHERE id=1"
        SqliteGroundMarkStore.loadAll database.Config token |> invalid)

    testCase "admin materialization rejects invalid dates and signed audit actors while deleted actors remain absent" (fun () ->
        use database = new SqliteAccountStoreTests.Database()
        initialize database
        database.Execute "PRAGMA foreign_keys=OFF; INSERT INTO admin_audit(moderator_id,action,target,details,at) VALUES(-1,'rename_player','player:1','',0)"
        SqliteAdminStore.recentAudit database.Config 10 token |> invalid
        database.Execute "UPDATE admin_audit SET moderator_id=NULL"
        let audit = SqliteAdminStore.recentAudit database.Config 10 token |> ok
        Expect.equal audit.Head.Actor ValueNone "Deleted actor absence remains legitimate."
        database.Execute "UPDATE admin_audit SET at=9223372036854775807"
        SqliteAdminStore.recentAudit database.Config 10 token |> invalid)

    testCase "an audit line cannot choose one of two contradictory actors" (fun () ->
        use database = new SqliteAccountStoreTests.Database()
        initialize database
        let player = register database
        database.Execute $"PRAGMA ignore_check_constraints=ON; INSERT INTO admin_accounts(id,username,password_hash,created_at) VALUES(1,'root','hash',0); INSERT INTO admin_audit(admin_id,moderator_id,action,target,details,at) VALUES(1,{PlayerId.value player.PlayerId},'rename_player','player:1','',0)"
        SqliteAdminStore.recentAudit database.Config 10 token |> invalid
        database.Execute "UPDATE admin_audit SET admin_id=NULL, moderator_id=NULL"
        let audit = SqliteAdminStore.recentAudit database.Config 10 token |> ok
        Expect.equal audit.Head.Actor ValueNone "Both deleted issuers remain legitimate absence.")

    testCase "ordinary SQLite lock and open failures remain typed original provider failures" (fun () ->
        use database = new SqliteAccountStoreTests.Database()
        initialize database
        use connection = database.Connect()
        use held = connection.BeginTransaction()
        use statement = connection.CreateCommand()
        statement.Transaction <- held
        statement.CommandText <- "INSERT INTO accounts(username) VALUES('held')"
        statement.ExecuteNonQuery() |> ignore
        match SqliteAccountStore.create database.Config (Username.create 32 "blocked" |> ok) (DisplayName.create 64 "Blocked" |> ok) "hash" token with
        | Error(AccountStoreError.Failed (:? SqliteException as error)) -> Expect.equal error.SqliteErrorCode 5 "Busy provider failure is preserved."
        | other -> failtestf "Expected SQLite busy, received %A" other

        let missing = { database.Config with DatabasePath = Path.Combine(Path.GetDirectoryName database.Config.DatabasePath, "missing.db") }
        match SqliteAccountStore.find missing (Username.create 32 "none" |> ok) token with
        | Error(AccountStoreError.Failed (:? SqliteException as error)) -> Expect.equal error.SqliteErrorCode 14 "ReadWrite open does not fabricate an empty database."
        | other -> failtestf "Expected SQLite open failure, received %A" other)

    testCase "unexpected unit fault escapes once after transaction rollback and connection release" (fun () ->
        use database = new SqliteAccountStoreTests.Database()
        initialize database
        let original = InvalidOperationException("unexpected-unit-work")
        let mutable attempts = 0
        let mutable owned = None
        let action () =
            SqliteAccountStore.withContext database.Config token (fun context ->
                attempts <- attempts + 1
                owned <- Some context.Connection
                SqliteStatements.transaction context (fun () ->
                    SqliteStatements.execute context "INSERT INTO accounts(username) VALUES('rolledback')" [] |> ignore
                    raise original))

        let actual =
            match thrown action with
            | Some error -> error
            | None -> failtest "Unexpected work fault was converted into a storage result."
        Expect.isTrue (obj.ReferenceEquals(original, actual)) "Original application fault crosses the provider adapter."
        Expect.equal attempts 1 "No automatic retry."
        Expect.equal owned.Value.State ConnectionState.Closed "The unit's connection was released."
        Expect.equal (database.Scalar "SELECT count(*) FROM accounts WHERE username='rolledback'") 0L "Uncommitted mutation was rolled back."
        Expect.equal (SqliteAccountStore.registrationMode database.Config token |> ok) RegistrationMode.initial "A fresh independent unit still opens normally.")

    testCase "provider exception identity and only owned cancellation survive shared adapter" (fun () ->
        use database = new SqliteAccountStoreTests.Database()
        initialize database
        let original = SqliteException("original-provider", 5)
        match SqliteAccountStore.withContext database.Config token (fun _ -> raise original) with
        | Error(AccountStoreError.Failed actual) -> Expect.isTrue (obj.ReferenceEquals(original, actual)) "Exact provider error is retained."
        | other -> failtestf "%A" other
        let unowned = OperationCanceledException("unowned")
        Expect.isTrue (thrown (fun () -> SqliteAccountStore.withContext database.Config token (fun _ -> raise unowned)) |> Option.exists (fun actual -> obj.ReferenceEquals(unowned, actual))) "Unowned cancellation remains a lifetime fault."
        use cancellation = new CancellationTokenSource()
        let result =
            SqliteAccountStore.withContext database.Config cancellation.Token (fun _ ->
                cancellation.Cancel()
                raise (OperationCanceledException(cancellation.Token)))
        match result with
        | Error AccountStoreError.Canceled -> ()
        | other -> failtestf "%A" other)

    case "auth and admin request supervisors preserve original faults and admit a fresh independent request" (fun () -> task {
        use database = new SqliteAccountStoreTests.Database()
        initialize database
        let original = InvalidOperationException("unexpected-auth-request")
        let errors = ConcurrentQueue<exn>()
        let clock = FaultOnSecondClock(original)
        use auth = AuthService.start AuthService.defaults database.Config (recordingLogger errors) clock |> ok
        let! failed = auth.TryAskAsync(fun reply -> AuthMessage.Access(AccountAccessCommand.ReadRegistration, reply)) |> awaitReply
        Expect.equal failed (Error AccountAccessError.Unavailable) "The fresh request supervisor isolates failed work."
        Expect.equal clock.Calls 2 "Failed work was not retried."
        Expect.equal errors.Count 1 "The failure has one original diagnostic."
        Expect.isTrue (obj.ReferenceEquals(errors.ToArray()[0], original)) "Auth keeps the original fault."

        let! fresh = auth.TryAskAsync(fun reply -> AuthMessage.Access(AccountAccessCommand.ReadRegistration, reply)) |> awaitReply
        Expect.equal fresh (Ok(AccountAccessResult.Registration RegistrationMode.initial)) "A fresh unit reads actual storage."

        let! _ = auth.PostAsync AuthMessage.Stop
        do! awaitUnit auth.Completion

        let original = InvalidOperationException("unexpected-admin-request")
        let errors = ConcurrentQueue<exn>()
        let clock = FaultOnSecondClock(original)
        use admin = AdminService.start AdminService.defaults database.Config (recordingLogger errors) clock |> expectStarted
        let! failed = admin.TryAskAsync(fun reply -> AdminMessage.Access(AdminCommand.Status, reply)) |> awaitReply
        Expect.equal failed (Error AdminServiceError.Unavailable) "Admin isolates only this detached work."
        Expect.equal clock.Calls 2 "Failed admin work was not retried."
        Expect.equal errors.Count 1 "Admin logs one original fault."
        Expect.isTrue (obj.ReferenceEquals(errors.ToArray()[0], original)) "Admin keeps the original fault."

        let! fresh = admin.TryAskAsync(fun reply -> AdminMessage.Access(AdminCommand.Status, reply)) |> awaitReply
        Expect.equal fresh (Ok(AdminReply.Configured false)) "Fresh work reads the actual empty admin table."

        let! _ = admin.PostAsync AdminMessage.Stop
        do! awaitUnit admin.Completion
    })

    testCase "known migration aggregates are adapted but mixed unexpected causes escape unchanged" (fun () ->
        let malformed = Migrondi.Core.MalformedSource("source", "content", "reason")
        let known = AggregateException("migration", [malformed])
        match SqliteDatabase.migrate (fun () -> raise known) with
        | Error message -> Expect.stringContains message "source" "Migration source diagnostic is retained."
        | Ok () -> failtest "Malformed migration was accepted."

        let mixed = AggregateException("mixed", [malformed; InvalidOperationException("unexpected") :> exn])
        Expect.isTrue (thrown (fun () -> SqliteDatabase.migrate (fun () -> raise mixed)) |> Option.exists (fun actual -> obj.ReferenceEquals(mixed, actual))) "Mixed aggregate retains the original unexpected fault.")

    testCase "startup filesystem and real malformed migration failures produce explicit errors" (fun () ->
        use database = new SqliteAccountStoreTests.Database()
        let directory = Path.GetDirectoryName database.Config.DatabasePath
        Directory.CreateDirectory directory |> ignore
        let blocked = Path.Combine(directory, "blocked")
        File.WriteAllText(blocked, "owned fixture")
        let config = { database.Config with DatabasePath = Path.Combine(blocked, "accounts.db") }
        Expect.isError (SqliteDatabase.initialize config directory) "A parent ordinary file prevents startup explicitly."
        let migrations = Path.Combine(directory, "migrations")
        Directory.CreateDirectory migrations |> ignore
        File.WriteAllText(Path.Combine(migrations, "1790467200000_broken.sql"), "malformed migration")
        match SqliteDatabase.initialize database.Config migrations with
        | Error message -> Expect.stringContains message "MalformedSource" "Real Migrondi source failure is adapted."
        | Ok () -> failtest "Malformed migration was accepted.")
]
