module Dreamsleeve.Server.Tests.SanctionTests

open System
open System.Collections.Concurrent
open System.Threading
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
    { Target = target; Kind = kind; Term = term; Reason = reason "Флуд"; IssuedBy = issuer }

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
]

let private access (service: Agent<AuthMessage>) command =
    service.AskAsync(fun reply -> AuthMessage.Access(command, reply)) |> awaitResult

let private consume service ticket = task {
    let completion = gate<SessionAuthenticationReply>()
    use receiver = Agent.Start(AgentOptions.create "sanction-test-reply", fun _ reply -> task { completion.TrySetResult reply |> ignore })
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
    use runtime = Agent.Start(AgentOptions.create "sanction-test-runtime", fun _ change -> task { changes.Enqueue change })
    use service = AuthService.start { AuthService.defaults with MaxTickets = 16 } database.Config NullLogger.Instance TimeProvider.System
    let! targeted = service.PostAsync(AuthMessage.SetChangeTarget(runtime.Ref.TryReliable().Value))
    equal AgentPostResult.Posted targeted
    let! registered = access service (AccountAccessCommand.Register(Username.create 32 "player" |> ok, DisplayName.create 64 "Player" |> ok, password))
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

let private login service = access service (AccountAccessCommand.Login(Username.create 32 "player" |> ok, password))

let private serviceTests = testList "Account service sanctions" [
    case "a ban refuses sign-in and resume, drops outstanding tickets and ends the live session; lifting it lets the player in" (fun () ->
        withService (fun database service profile changes -> task {
            let! remembered = access service (AccountAccessCommand.RememberLogin(Username.create 32 "player" |> ok, password))
            let saved = grant remembered
            let issuer = SanctionIssuer.Admin(admin database)
            let! banned = access service (AccountAccessCommand.Sanction(order profile.PlayerId SanctionKind.Ban (SanctionTerm.For(TimeSpan.FromDays 1.)) issuer))
            let ban = match banned with Ok (AccountAccessResult.Sanctioned ban) -> ban | other -> failtestf "%A" other
            let! outstanding = consume service saved.SessionTicket
            equal (Error SessionAuthenticationError.InvalidTicket) outstanding
            let! refused = login service
            equal (Error (AccountAccessError.Banned ban)) refused
            let! resumed = access service (AccountAccessCommand.Resume saved.RememberToken)
            equal (Error (AccountAccessError.Banned ban)) resumed
            let! told = changes 1
            equal [ AccountChange.Banned ban ] told
            let! lifted = access service (AccountAccessCommand.LiftSanction(profile.PlayerId, SanctionKind.Ban, issuer))
            equal (Ok (AccountAccessResult.SanctionLifted ban)) lifted
            // The saved login was kept: it works again.
            let! back = access service (AccountAccessCommand.Resume saved.RememberToken)
            grant back |> ignore
        }))

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

let tests = testList "Sanctions" [ domainTests; storeTests; serviceTests ]
