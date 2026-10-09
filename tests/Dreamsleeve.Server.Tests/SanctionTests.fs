module Dreamsleeve.Server.Tests.SanctionTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure
open Expecto
open AgentTests
open BackgroundTests

let private ok = function Ok value -> value | Error error -> failtestf "Unexpected result: %A" error
let private token = CancellationToken.None
let private reason text = SanctionReason.create text |> ok
let private now = DateTimeOffset.FromUnixTimeMilliseconds 1_800_000_000_000L
let private password = "password-with-spaces  "

let private order target kind term issuer : SanctionOrder =
    { Target = target; Kind = kind; Term = term; Reason = reason "Флуд"; IssuedBy = issuer; Devices = false }

let private register (database: SqliteAccountStoreTests.Database) name =
    SqliteAccountStore.create database.Config (Username.create 32 name |> ok) (DisplayName.create 64 $"Display {name}" |> ok) "hash" token |> ok

// The panel's administrator and a moderator role, written as the admin store would.
let private admin (database: SqliteAccountStoreTests.Database) =
    database.Execute "INSERT INTO admin_accounts(username, password_hash, created_at) VALUES ('root', 'hash', 0)"
    AdminId.create (database.Scalar "SELECT id FROM admin_accounts WHERE username='root'") |> ok

let private moderator (database: SqliteAccountStoreTests.Database) (player: PlayerData) =
    database.Execute $"INSERT INTO player_roles(player_id, role, granted_at) VALUES ({PlayerId.value player.PlayerId}, 1, 0)"

let private applied = function
    | SanctionOutcome.Applied sanction -> sanction
    | SanctionOutcome.Refused refused -> failtestf "Refused: %A" refused

let private domainTests = testList "Sanction rules" [
    testCase "terms run from a minute to ten years, or until lifted" <| fun _ ->
        equal (Ok SanctionTerm.UntilLifted) (SanctionTerm.create ValueNone)
        equal (Ok(SanctionTerm.For(TimeSpan.FromMinutes 15.))) (SanctionTerm.create (ValueSome 15))
        check (SanctionTerm.create (ValueSome 0) |> Result.isError) "no zero term"
        check (SanctionTerm.create (ValueSome (SanctionTerm.MaxMinutes + 1)) |> Result.isError) "no term beyond the longest"

    testCase "a reason is required, one line, trimmed and bounded" <| fun _ ->
        equal "Флуд" (SanctionReason.value (reason "  Флуд "))
        for invalid in [ ""; "   "; "two\nlines"; String('x', SanctionReason.MaxLength + 1) ] do
            check (SanctionReason.create invalid |> Result.isError) $"refused: {invalid.Length} characters"

    testCase "a sanction holds until its end and the one in force is found by kind" <| fun _ ->
        let target = PlayerId.create 7UL |> ok
        let issuer = SanctionIssuer.Admin(AdminId.create 1L |> ok)
        let mute = Sanction.issue (SanctionId.create 1L |> ok) now (order target SanctionKind.Mute (SanctionTerm.For(TimeSpan.FromHours 1.)) issuer)
        let ban = Sanction.issue (SanctionId.create 2L |> ok) now (order target SanctionKind.Ban SanctionTerm.UntilLifted issuer)
        equal (ValueSome(now.AddHours 1.)) mute.Expires
        check (Sanction.activeAt (now.AddMinutes 59.) mute) "in force before its end"
        check (not (Sanction.activeAt (now.AddHours 1.) mute)) "over at its end"
        check (Sanction.activeAt (now.AddYears 50) ban) "until lifted"
        equal (ValueSome ban) (Sanction.find SanctionKind.Ban now [ mute; ban ])
        equal ValueNone (Sanction.find SanctionKind.Mute (now.AddHours 2.) [ mute; ban ])

    testCase "a moderator outranks players only, never another moderator or itself" <| fun _ ->
        check (PlayerRole.outranks PlayerRole.Moderator PlayerRole.Player) "moderator over player"
        check (not (PlayerRole.outranks PlayerRole.Moderator PlayerRole.Moderator)) "moderators are equal"
        check (not (PlayerRole.outranks PlayerRole.Player PlayerRole.Player)) "players act on nobody"
        check (not (PlayerRole.outranks PlayerRole.Player PlayerRole.Moderator)) "players act on nobody"
]

let private storeTests = testList "SQLite sanctions" [
    testCase "corrupt full-width role cannot authorize or disappear as a missing player" (fun () ->
        for role in [ "4294967297"; "-4294967295"; "'broken'" ] do
            use database = new SqliteAccountStoreTests.Database()
            SqliteAccountStore.initialize database.Config |> ok
            let alice = register database "alice"
            let bob = register database "bob"
            database.Execute $"PRAGMA ignore_check_constraints=ON; INSERT INTO player_roles(player_id,role,granted_at) VALUES({PlayerId.value alice.PlayerId},{role},0)"
            let assertInvalid result =
                match result with
                | Error(AccountStoreError.Failed (:? IO.InvalidDataException)) -> ()
                | other -> failtestf "Corrupt role must be invalid stored data, got %A" other
            SqliteSanctionStore.issue database.Config
                (order bob.PlayerId SanctionKind.Mute SanctionTerm.UntilLifted (SanctionIssuer.Moderator alice.PlayerId)) now token
            |> assertInvalid
            SqliteSanctionStore.issue database.Config
                (order alice.PlayerId SanctionKind.Mute SanctionTerm.UntilLifted (SanctionIssuer.Admin(admin database))) now token
            |> assertInvalid
            equal 0L (database.Scalar "SELECT count(*) FROM sanctions")
            equal 0L (database.Scalar "SELECT count(*) FROM admin_audit"))

    testCase "corrupt sanction kind cannot alias a known kind" (fun () ->
        for kind in [ "4294967296"; "4294967297"; "-4294967295"; "'broken'" ] do
            use database = new SqliteAccountStoreTests.Database()
            SqliteAccountStore.initialize database.Config |> ok
            let alice = register database "alice"
            database.Execute $"PRAGMA ignore_check_constraints=ON; INSERT INTO sanctions(player_id,kind,reason,issued_at) VALUES({PlayerId.value alice.PlayerId},{kind},'reason',0)"
            match SqliteSanctionStore.active database.Config alice.PlayerId now token with
            | Error(AccountStoreError.Failed (:? IO.InvalidDataException)) -> ()
            | other -> failtestf "Corrupt kind must be rejected before publication: %A" other)

    testCase "corrupt sanction issuer cannot wrap a signed identifier or select one of two issuers" (fun () ->
        for adminId, moderatorId in [ "NULL", "-1"; "1", "1" ] do
            use database = new SqliteAccountStoreTests.Database()
            SqliteAccountStore.initialize database.Config |> ok
            let alice = register database "alice"
            database.Execute $"PRAGMA foreign_keys=OFF; PRAGMA ignore_check_constraints=ON; INSERT INTO sanctions(player_id,kind,reason,issued_by_admin,issued_by_player,issued_at) VALUES({PlayerId.value alice.PlayerId},0,'reason',{adminId},{moderatorId},0)"
            match SqliteSanctionStore.active database.Config alice.PlayerId now token with
            | Error(AccountStoreError.Failed (:? IO.InvalidDataException)) -> ()
            | other -> failtestf "Corrupt issuer must be rejected before publication: %A" other)

    testCase "a corrupt signed target is not published as a large unsigned player" (fun () ->
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        database.Execute "PRAGMA foreign_keys=OFF; INSERT INTO sanctions(player_id,kind,reason,issued_at) VALUES(-1,0,'reason',0)"
        let target = PlayerId.create UInt64.MaxValue |> ok
        match SqliteSanctionStore.active database.Config target now token with
        | Error(AccountStoreError.Failed (:? IO.InvalidDataException)) -> ()
        | other -> failtestf "Stored target must retain the signed storage invariant: %A" other)

    testCase "corrupt sanction timestamp reports stored data failure while deleted issuer stays absent" (fun () ->
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        let alice = register database "alice"
        database.Execute $"INSERT INTO sanctions(player_id,kind,reason,issued_at) VALUES({PlayerId.value alice.PlayerId},0,'reason',0)"
        let restored = SqliteSanctionStore.active database.Config alice.PlayerId now token |> ok
        equal ValueNone restored.Head.IssuedBy
        database.Execute "UPDATE sanctions SET issued_at=9223372036854775807"
        match SqliteSanctionStore.active database.Config alice.PlayerId now token with
        | Error(AccountStoreError.Failed (:? IO.InvalidDataException)) -> ()
        | other -> failtestf "Corrupt timestamp must be classified at stored decoding: %A" other)

    testCase "a new sanction replaces the one of its kind in force and a lift ends it" (fun () ->
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        let alice = register database "alice"
        let issuer = SanctionIssuer.Admin(admin database)
        let first = SqliteSanctionStore.issue database.Config (order alice.PlayerId SanctionKind.Mute (SanctionTerm.For(TimeSpan.FromHours 1.)) issuer) now token |> ok |> applied
        let ban = SqliteSanctionStore.issue database.Config (order alice.PlayerId SanctionKind.Ban SanctionTerm.UntilLifted issuer) now token |> ok |> applied
        let second = SqliteSanctionStore.issue database.Config (order alice.PlayerId SanctionKind.Mute SanctionTerm.UntilLifted issuer) (now.AddMinutes 1.) token |> ok |> applied
        check (first.Id <> second.Id) "a new row"
        let active = SqliteSanctionStore.active database.Config alice.PlayerId (now.AddMinutes 2.) token |> ok
        equal (set [ ban; second ]) (set active)
        equal (ValueSome issuer) second.IssuedBy
        let lifted = SqliteSanctionStore.lift database.Config alice.PlayerId SanctionKind.Mute issuer (now.AddMinutes 3.) token |> ok |> applied
        equal second lifted
        equal [ ban ] (SqliteSanctionStore.active database.Config alice.PlayerId (now.AddMinutes 4.) token |> ok)
        equal (SanctionOutcome.Refused SanctionError.NotActive)
              (SqliteSanctionStore.lift database.Config alice.PlayerId SanctionKind.Mute issuer (now.AddMinutes 5.) token |> ok))

    testCase "a sanction past its end is not in force nor listed" (fun () ->
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        let alice = register database "alice"
        let issuer = SanctionIssuer.Admin(admin database)
        SqliteSanctionStore.issue database.Config (order alice.PlayerId SanctionKind.Ban (SanctionTerm.For(TimeSpan.FromMinutes 15.)) issuer) now token |> ok |> applied |> ignore
        equal 1 (SqliteSanctionStore.listActive database.Config (now.AddMinutes 14.) token |> ok).Length
        equal [] (SqliteSanctionStore.active database.Config alice.PlayerId (now.AddMinutes 15.) token |> ok)
        equal [] (SqliteSanctionStore.listActive database.Config (now.AddMinutes 15.) token |> ok))

    testCase "a moderator sanctions and lifts only players it outranks; the list names them newest first" (fun () ->
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        let alice = register database "alice"
        let bob = register database "bob"
        let carol = register database "carol"
        moderator database alice
        moderator database carol
        let by (player: PlayerData) = SanctionIssuer.Moderator player.PlayerId
        let issue target issuer at = SqliteSanctionStore.issue database.Config (order target SanctionKind.Mute SanctionTerm.UntilLifted issuer) at token |> ok
        let muted = issue bob.PlayerId (by alice) now |> applied
        equal (ValueSome(by alice)) muted.IssuedBy
        equal (SanctionOutcome.Refused SanctionError.NotAllowed) (issue carol.PlayerId (by alice) now)
        equal (SanctionOutcome.Refused SanctionError.NotAllowed) (issue alice.PlayerId (by alice) now)
        equal (SanctionOutcome.Refused SanctionError.NotAllowed) (issue carol.PlayerId (by bob) now)
        equal (SanctionOutcome.Refused SanctionError.PlayerNotFound) (issue (PlayerId.create 404UL |> ok) (by alice) now)
        // Moderators are equal: another one lifts it.
        let admin = SanctionIssuer.Admin(admin database)
        let banned = SqliteSanctionStore.issue database.Config (order carol.PlayerId SanctionKind.Ban SanctionTerm.UntilLifted admin) (now.AddMinutes 1.) token |> ok |> applied
        let listed = SqliteSanctionStore.listActive database.Config (now.AddMinutes 2.) token |> ok
        equal [ carol.PlayerId; bob.PlayerId ] (listed |> List.map _.Target.PlayerId)
        equal [ banned; muted ] (listed |> List.map _.Sanction)
        equal "Display carol" (DisplayName.value listed.Head.Target.DisplayName)
        equal muted (SqliteSanctionStore.lift database.Config bob.PlayerId SanctionKind.Mute (by carol) (now.AddMinutes 3.) token |> ok |> applied)
        // A moderator cannot lift what holds on another moderator.
        equal (SanctionOutcome.Refused SanctionError.NotAllowed)
              (SqliteSanctionStore.lift database.Config carol.PlayerId SanctionKind.Ban (by alice) (now.AddMinutes 4.) token |> ok))

    testCase "a kick is authorized like a sanction; every action leaves an audit line under its issuer" (fun () ->
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        let alice = register database "alice"
        let bob = register database "bob"
        moderator database alice
        let by = SanctionIssuer.Moderator alice.PlayerId
        SqliteSanctionStore.issue database.Config (order bob.PlayerId SanctionKind.Mute (SanctionTerm.For(TimeSpan.FromHours 1.)) by) now token |> ok |> applied |> ignore
        SqliteSanctionStore.lift database.Config bob.PlayerId SanctionKind.Mute by (now.AddMinutes 1.) token |> ok |> applied |> ignore
        equal (Ok()) (SqliteSanctionStore.kick database.Config bob.PlayerId (reason "Остынь") by (now.AddMinutes 2.) token |> ok)
        // Refusals write nothing.
        equal (Error SanctionError.NotAllowed) (SqliteSanctionStore.kick database.Config alice.PlayerId (reason "Сам") by (now.AddMinutes 3.) token |> ok)
        equal (Error SanctionError.PlayerNotFound)
              (SqliteSanctionStore.kick database.Config (PlayerId.create 404UL |> ok) (reason "Никто") by (now.AddMinutes 3.) token |> ok)
        let audit = SqliteAdminStore.recentAudit database.Config 10 token |> ok
        equal [ AdminAction.KickedPlayer; AdminAction.LiftedSanction; AdminAction.SanctionedPlayer ] (audit |> List.map _.Action)
        for entry in audit do
            equal (ValueSome(AuditActor.Moderator alice.PlayerId)) entry.Actor
            equal (ValueSome "alice") (entry.ActorName |> ValueOption.map Username.value)
            equal $"player:{PlayerId.value bob.PlayerId}" entry.Target
        equal [ "Остынь"; "mute"; "mute until 2027-01-15 09:00 UTC: Флуд" ] (audit |> List.map _.Details)
        // The panel's own lines name the administrator.
        let root = admin database
        equal (Ok()) (SqliteSanctionStore.kick database.Config bob.PlayerId (reason "Проверка") (SanctionIssuer.Admin root) (now.AddMinutes 4.) token |> ok)
        let panel = (SqliteAdminStore.recentAudit database.Config 1 token |> ok).Head
        equal (ValueSome(AuditActor.Admin root), ValueSome "root") (panel.Actor, panel.ActorName |> ValueOption.map Username.value)
        // A moderator's lines keep their text once the profile is gone.
        database.Execute $"DELETE FROM profiles WHERE player_id={PlayerId.value alice.PlayerId}"
        let orphaned = SqliteAdminStore.recentAudit database.Config 10 token |> ok |> List.tail
        equal 3 orphaned.Length
        check (orphaned |> List.forall (fun entry -> entry.Actor.IsNone && entry.ActorName.IsNone)) "The actor is gone, the line stays.")

    testCase "a moderator's removal of content is recorded under the moderator" (fun () ->
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        let alice = register database "alice"
        let bob = register database "bob"
        let record = AuditRecord.create AdminAction.DeletedChatMessage (AuditTarget.Player bob.PlayerId) "spam"
        SqliteAdminStore.recordModeration database.Config alice.PlayerId record now token |> ok
        let line = (SqliteAdminStore.recentAudit database.Config 1 token |> ok).Head
        equal (ValueSome(AuditActor.Moderator alice.PlayerId)) line.Actor
        equal (AdminAction.DeletedChatMessage, "spam", now) (line.Action, line.Details, line.At))
]

let private access (service: ReliableAgent<AuthMessage>) command =
    service.TryAskAsync(fun reply -> AuthMessage.Access(command, reply)) |> awaitReply

let private consume service ticket = task {
    let completion = gate<SessionAuthenticationReply>()
    use receiver = TestAgent.Start(AgentOptions.create "sanction-test-reply", fun _ reply -> task { completion.TrySetResult reply |> ignore })
    let! admitted = (AuthService.authenticator service).Requests.PostAsync { OperationId = Guid.NewGuid(); Ticket = ticket; ReplyTo = receiver.Ref.TryReliable().Value }
    equal AgentDeliveryResult.Posted admitted
    let! response = awaitResult completion.Task
    return response.Result
}

let private grant result =
    match result with
    | Ok (AccountAccessResult.SignedIn grant) -> grant
    | other -> failtestf "Expected a grant: %A" other

// The runtime's side of the account service: every change it was told.
let private withService run = task {
    use database = new SqliteAccountStoreTests.Database()
    SqliteAccountStore.initialize database.Config |> ok
    let changes = ConcurrentQueue<AccountChange>()
    use runtime = TestAgent.Start(AgentOptions.create "sanction-test-runtime", fun _ change -> task { changes.Enqueue change })
    use service = AuthService.start { AuthService.defaults with MaxTickets = 16 } database.Config NullLogger.Instance TimeProvider.System |> ok
    let! targeted = service.PostAsync(AuthMessage.SetChangeTarget(runtime.Ref.TryReliable().Value))
    equal AgentPostResult.Posted targeted
    // A runtime first learns the IP range bans in force: none here.
    let settled = Environment.TickCount64 + 5000L
    while changes.IsEmpty && Environment.TickCount64 < settled do
        do! Task.Delay 10
    equal (true, AccountChange.AddressBans []) (changes.TryDequeue())
    let! registered = access service (AccountAccessCommand.Register(Username.create 32 "player" |> ok, DisplayName.create 64 "Player" |> ok, password, SignInOrigin.none))
    let profile = match registered with Ok (AccountAccessResult.Registered profile) -> profile | other -> failtestf "%A" other
    // The runtime takes them asynchronously, after the reply.
    let received count = task {
        let deadline = Environment.TickCount64 + 5000L
        while changes.Count < count && Environment.TickCount64 < deadline do
            do! Task.Delay 10
        return changes.ToArray() |> List.ofArray
    }
    do! run database service profile received
    let! stopped = service.PostAsync AuthMessage.Stop
    equal AgentPostResult.Posted stopped
    do! awaitUnit service.Completion
}

let private login service = access service (AccountAccessCommand.Login(Username.create 32 "player" |> ok, password, SignInOrigin.none))

let private serviceTests = testList "Account service sanctions" [
    case "a ban refuses sign-in and resume, drops outstanding tickets and ends the live session; lifting it lets the player in" (fun () ->
        withService (fun database service profile changes -> task {
            let! remembered = access service (AccountAccessCommand.RememberLogin(Username.create 32 "player" |> ok, password, SignInOrigin.none))
            let saved = grant remembered
            let issuer = SanctionIssuer.Admin(admin database)
            let! banned = access service (AccountAccessCommand.Sanction(order profile.PlayerId SanctionKind.Ban (SanctionTerm.For(TimeSpan.FromDays 1.)) issuer))
            let ban = match banned with Ok (AccountAccessResult.Sanctioned ban) -> ban | other -> failtestf "%A" other
            let! outstanding = consume service saved.SessionTicket
            equal (Error SessionAuthenticationError.InvalidTicket) outstanding
            let! refused = login service
            equal (Error (AccountAccessError.Banned ban)) refused
            let! resumed = access service (AccountAccessCommand.Resume(saved.RememberToken, SignInOrigin.none))
            equal (Error (AccountAccessError.Banned ban)) resumed
            let! told = changes 1
            equal [ AccountChange.Banned ban ] told
            let! lifted = access service (AccountAccessCommand.LiftSanction(profile.PlayerId, SanctionKind.Ban, issuer))
            equal (Ok (AccountAccessResult.SanctionLifted ban)) lifted
            // The saved login was kept: it works again.
            let! back = access service (AccountAccessCommand.Resume(saved.RememberToken, SignInOrigin.none))
            grant back |> ignore
        }))

    // Between runtime restarts a change has no live session to reach: it stays in
    // storage, and the service keeps serving for the runtime that comes next.
    case "a stopped runtime does not stop the service; the next runtime receives later changes" (fun () -> task {
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        let restartedChanges = ConcurrentQueue<AccountChange>()
        let stopped = TestAgent.Start(AgentOptions.create "stopped-runtime", fun _ (_: AccountChange) -> task { () })
        use restarted = TestAgent.Start(AgentOptions.create "restarted-runtime", fun _ change -> task { restartedChanges.Enqueue change })
        use service = AuthService.start { AuthService.defaults with MaxTickets = 16 } database.Config NullLogger.Instance TimeProvider.System |> ok
        let! targeted = service.PostAsync(AuthMessage.SetChangeTarget(stopped.Ref.TryReliable().Value))
        equal AgentPostResult.Posted targeted
        let! registered = access service (AccountAccessCommand.Register(Username.create 32 "player" |> ok, DisplayName.create 64 "Player" |> ok, password, SignInOrigin.none))
        let profile = match registered with Ok (AccountAccessResult.Registered profile) -> profile | other -> failtestf "%A" other
        stopped.Complete() |> ignore
        do! awaitUnit stopped.Completion
        let issuer = SanctionIssuer.Admin(admin database)
        let! muted = access service (AccountAccessCommand.Sanction(order profile.PlayerId SanctionKind.Mute SanctionTerm.UntilLifted issuer))
        let mute = match muted with Ok (AccountAccessResult.Sanctioned mute) -> mute | other -> failtestf "%A" other
        // The failed delivery comes back asynchronously; the service keeps answering.
        do! Task.Delay 100
        let! signedIn = login service
        grant signedIn |> ignore
        check (not service.Completion.IsCompleted) "the account service survives a stopped runtime"
        let! retargeted = service.PostAsync(AuthMessage.SetChangeTarget(restarted.Ref.TryReliable().Value))
        equal AgentPostResult.Posted retargeted
        let! lifted = access service (AccountAccessCommand.LiftSanction(profile.PlayerId, SanctionKind.Mute, issuer))
        equal (Ok (AccountAccessResult.SanctionLifted mute)) lifted
        do! eventually (fun () -> restartedChanges |> Seq.contains (AccountChange.MuteChanged(profile.PlayerId, ValueNone)))
        let! stopping = service.PostAsync AuthMessage.Stop
        equal AgentPostResult.Posted stopping
        do! awaitUnit service.Completion
    })

    case "a mute goes into new and outstanding tickets and reaches the runtime; a lift clears it" (fun () ->
        withService (fun database service profile changes -> task {
            let! before = login service
            let issuer = SanctionIssuer.Admin(admin database)
            let! muted = access service (AccountAccessCommand.Sanction(order profile.PlayerId SanctionKind.Mute SanctionTerm.UntilLifted issuer))
            let mute = match muted with Ok (AccountAccessResult.Sanctioned mute) -> mute | other -> failtestf "%A" other
            let! outstanding = consume service (grant before).SessionTicket
            equal (ValueSome mute) (outstanding |> ok).Mute
            let! after = login service
            let! fresh = consume service (grant after).SessionTicket
            equal (ValueSome mute) (fresh |> ok).Mute
            let! lifted = access service (AccountAccessCommand.LiftSanction(profile.PlayerId, SanctionKind.Mute, issuer))
            equal (Ok (AccountAccessResult.SanctionLifted mute)) lifted
            let! cleared = login service
            let! unmuted = consume service (grant cleared).SessionTicket
            equal ValueNone (unmuted |> ok).Mute
            let! told = changes 2
            equal [ AccountChange.MuteChanged(profile.PlayerId, ValueSome mute); AccountChange.MuteChanged(profile.PlayerId, ValueNone) ] told
            let! nothing = access service (AccountAccessCommand.LiftSanction(profile.PlayerId, SanctionKind.Mute, issuer))
            equal (Error (AccountAccessError.SanctionRefused SanctionError.NotActive)) nothing
        }))
]

let private moderationTests = testList "Account service moderation" [
    case "a moderator's kick, list and audit line go through the account service; a kick reaches the runtime" (fun () ->
        withService (fun database service profile changes -> task {
            let! registered = access service (AccountAccessCommand.Register(Username.create 32 "moderator" |> ok, DisplayName.create 64 "Mod" |> ok, password, SignInOrigin.none))
            let mod' = match registered with Ok (AccountAccessResult.Registered mod') -> mod' | other -> failtestf "%A" other
            database.Execute $"INSERT INTO player_roles(player_id, role, granted_at) VALUES ({PlayerId.value mod'.PlayerId}, 1, 0)"
            let replies = Channel.CreateUnbounded<ModerationReply>()
            use receiver = TestAgent.Start(AgentOptions.create "moderation-reply", fun _ (reply: ModerationReply) -> task { replies.Writer.TryWrite reply |> ignore })
            let moderation = (AuthService.authenticator service).Moderation
            let ask command = task {
                let request: ModerationRequest = { OperationId = Guid.NewGuid(); Command = command; ReplyTo = receiver.Ref.TryReliable().Value }
                let! admitted = moderation.PostAsync request
                equal AgentDeliveryResult.Posted admitted
                let! reply = replies.Reader.ReadAsync().AsTask().WaitAsync guard
                equal request.OperationId reply.OperationId
                return reply.Result
            }
            let! refused = ask (ModerationCommand.Kick(mod'.PlayerId, reason "Сам", mod'.PlayerId))
            equal (Error(ModerationError.Refused SanctionError.NotAllowed)) refused
            let! kicked = ask (ModerationCommand.Kick(profile.PlayerId, reason "Остынь", mod'.PlayerId))
            equal (Ok ModerationResult.Kicked) kicked
            let! told = changes 1
            equal [ AccountChange.Kicked(profile.PlayerId, reason "Остынь") ] told
            let! muted = ask (ModerationCommand.Sanction(order profile.PlayerId SanctionKind.Mute SanctionTerm.UntilLifted (SanctionIssuer.Moderator mod'.PlayerId)))
            let mute = match muted with Ok (ModerationResult.Sanctioned mute) -> mute | other -> failtestf "%A" other
            let! listed = ask ModerationCommand.ListSanctions
            equal (Ok(ModerationResult.Sanctions [ mute ])) listed
            let! lifted = ask (ModerationCommand.Lift(profile.PlayerId, SanctionKind.Mute, mod'.PlayerId))
            equal (Ok(ModerationResult.Lifted mute)) lifted
            let line = AuditRecord.create AdminAction.RemovedGroundMark (AuditTarget.Player profile.PlayerId) "mark 3"
            let! recorded = ask (ModerationCommand.Record(mod'.PlayerId, line))
            equal (Ok ModerationResult.Recorded) recorded
            let audit = SqliteAdminStore.recentAudit database.Config 10 token |> ok
            equal [ AdminAction.RemovedGroundMark; AdminAction.LiftedSanction; AdminAction.SanctionedPlayer; AdminAction.KickedPlayer ]
                  (audit |> List.map _.Action)
        }))
]

let tests = testList "Sanctions" [ domainTests; storeTests; serviceTests; moderationTests ]
