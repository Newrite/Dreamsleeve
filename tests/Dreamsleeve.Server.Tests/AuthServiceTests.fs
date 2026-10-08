module Dreamsleeve.Server.Tests.AuthServiceTests

open System
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
let private username value = Username.create 32 value |> ok
let private display = DisplayName.create 64 "Persistent Player" |> ok
let private password = "password-with-spaces  "
// A consumed ticket carries the stored role; nobody was given one here.
let private player profile : AuthenticatedPlayer = { Profile = profile; Role = PlayerRole.Player; Mute = ValueNone; SignedInFrom = ValueNone }
let private settings = { AuthService.defaults with MailboxCapacity = 8; MaxConcurrentOperations = 2; MaxTickets = 2; TicketLifetimeSeconds = 10 }

// Expiry uses monotonic time. Advance never sleeps or changes machine time.
type private ManualClock() =
    inherit TimeProvider()
    let mutable ticks = 0L
    override _.TimestampFrequency = TimeSpan.TicksPerSecond
    override _.GetTimestamp() = Interlocked.Read &ticks
    member _.Advance(span: TimeSpan) = Interlocked.Add(&ticks, span.Ticks) |> ignore

let private start database options clock =
    AuthService.start options database NullLogger.Instance clock

let private access (service: Agent<AuthMessage>) command =
    service.TryAskAsync(fun reply -> AuthMessage.Access(command, reply)) |> awaitReply

let private register service = task {
    let! result = access service (AccountAccessCommand.Register(username "player", display, password, SignInOrigin.none))
    match result with
    | Ok (AccountAccessResult.Registered profile) -> return profile
    | other -> return failtestf "Registration failed: %A" other
}

let private login service = task {
    let! result = access service (AccountAccessCommand.Login(username "PLAYER", password, SignInOrigin.none))
    match result with
    | Ok (AccountAccessResult.SignedIn grant) -> return grant
    | other -> return failtestf "Login failed: %A" other
}

let private collect (completion: TaskCompletionSource<SessionAuthenticationReply>) _ reply = task {
    completion.TrySetResult reply |> ignore
}

let private consume service ticket = task {
    let completion = gate<SessionAuthenticationReply>()
    use receiver = Agent.Start(AgentOptions.create "auth-test-reply", collect completion)
    let operationId = Guid.NewGuid()
    let request = { OperationId = operationId; Ticket = ticket; ReplyTo = receiver.Ref.TryReliable().Value }
    let! admitted = (AuthService.authenticator service).Requests.PostAsync request
    equal AgentDeliveryResult.Posted admitted
    let! response = awaitResult completion.Task
    equal operationId response.OperationId
    receiver.Complete() |> ignore
    do! awaitUnit receiver.Completion
    return response.Result
}

let private stop (service: Agent<AuthMessage>) = task {
    let! admitted = service.PostAsync AuthMessage.Stop
    equal AgentPostResult.Posted admitted
    do! awaitUnit service.Completion
    equal (Some AgentStopReason.Completed) service.StopReason
}

let private remember service = task {
    let! result = access service (AccountAccessCommand.RememberLogin(username "player", password, SignInOrigin.none))
    match result with
    | Ok (AccountAccessResult.SignedIn grant) -> return grant
    | other -> return failtestf "Remember failed: %A" other
}

let tests = testList "Authentication service" [
    case "saved login survives service restart and logout revokes only its own tickets" (fun () -> task {
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        use first = start database.Config { settings with MaxTickets = 10 } TimeProvider.System
        let! profile = register first
        let! saved = remember first
        equal 43 saved.RememberToken.Length
        do! stop first

        use second = start database.Config { settings with MaxTickets = 10 } TimeProvider.System
        let! resumed = access second (AccountAccessCommand.Resume(saved.RememberToken, SignInOrigin.none))
        let grant = match resumed with Ok (AccountAccessResult.SignedIn value) -> value | other -> failtestf "%A" other
        equal profile grant.Profile
        let! independent = login second
        let! signedOut = access second (AccountAccessCommand.Logout saved.RememberToken)
        equal (Ok AccountAccessResult.Completed) signedOut
        let! denied = access second (AccountAccessCommand.Resume(saved.RememberToken, SignInOrigin.none))
        equal (Error AccountAccessError.InvalidCredentials) denied
        let! ticketDenied = consume second grant.SessionTicket
        equal (Error SessionAuthenticationError.InvalidTicket) ticketDenied
        let! otherTicket = consume second independent.SessionTicket
        equal (Ok (player profile)) otherTicket
        do! stop second
    })

    case "reset codes are one-use and revoke saved access and outstanding tickets" (fun () -> task {
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        use service = start database.Config { settings with MaxTickets = 10 } TimeProvider.System
        let! _ = register service
        let! saved = remember service
        let! issued = access service (AccountAccessCommand.CreatePasswordReset(username "player"))
        let code = match issued with Ok (AccountAccessResult.PasswordResetCreated code) -> code | other -> failtestf "%A" other
        let! oldTicket = consume service saved.SessionTicket
        equal (Error SessionAuthenticationError.InvalidTicket) oldTicket
        let! oldSaved = access service (AccountAccessCommand.Resume(saved.RememberToken, SignInOrigin.none))
        equal (Error AccountAccessError.InvalidCredentials) oldSaved
        let replacement = "replacement-password-2026"
        let! changed = access service (AccountAccessCommand.ResetPassword(code, replacement))
        equal (Ok AccountAccessResult.Completed) changed
        let! duplicate = access service (AccountAccessCommand.ResetPassword(code, password))
        equal (Error AccountAccessError.InvalidCredentials) duplicate
        let! oldPassword = access service (AccountAccessCommand.Login(username "player", password, SignInOrigin.none))
        equal (Error AccountAccessError.InvalidCredentials) oldPassword
        let! newPassword = access service (AccountAccessCommand.Login(username "player", replacement, SignInOrigin.none))
        match newPassword with Ok (AccountAccessResult.SignedIn _) -> () | other -> failtestf "%A" other
        do! stop service
    })

    case "reissued and expired reset codes cannot change a password" (fun () -> task {
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        use service = start database.Config settings TimeProvider.System
        let! _ = register service
        let! first = access service (AccountAccessCommand.CreatePasswordReset(username "player"))
        let! second = access service (AccountAccessCommand.CreatePasswordReset(username "player"))
        let code = function Ok (AccountAccessResult.PasswordResetCreated code) -> code | other -> failtestf "%A" other
        let! replaced = access service (AccountAccessCommand.ResetPassword(code first, "replacement-password"))
        equal (Error AccountAccessError.InvalidCredentials) replaced
        database.Execute "UPDATE auth_tokens SET expires_at=0 WHERE kind=1"
        let! expired = access service (AccountAccessCommand.ResetPassword(code second, "replacement-password"))
        equal (Error AccountAccessError.InvalidCredentials) expired
        let! _ = login service
        do! stop service
    })

    case "saved token expiry and per-account limit are enforced" (fun () -> task {
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        use service = start database.Config { settings with MaxSavedLogins = 1; MaxTickets = 10 } TimeProvider.System
        let! _ = register service
        let! old = remember service
        let! current = remember service
        let! evicted = access service (AccountAccessCommand.Resume(old.RememberToken, SignInOrigin.none))
        equal (Error AccountAccessError.InvalidCredentials) evicted
        database.Execute "UPDATE auth_tokens SET expires_at=0"
        let! expired = access service (AccountAccessCommand.Resume(current.RememberToken, SignInOrigin.none))
        equal (Error AccountAccessError.InvalidCredentials) expired
        do! stop service
    })

    case "password validation counts UTF-8 bytes and keeps whitespace significant" (fun () -> task {
        check (AuthService.validPassword " 1234567890 ") "Boundary spaces are part of the password."
        check (AuthService.validPassword (String.replicate 6 "я")) "Six Cyrillic characters are twelve UTF-8 bytes."
        check (AuthService.validPassword (String.replicate 64 "я")) "The maximum is 128 UTF-8 bytes."
        check (not (AuthService.validPassword (String.replicate 65 "я"))) "UTF-8 byte overflow must fail."
        check (not (AuthService.validPassword "12345678901")) "Short passwords must fail."
        check (not (AuthService.validPassword null)) "Null passwords must fail."
    })

    case "registration rejects duplicate names and login hides missing accounts and wrong passwords" (fun () -> task {
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        use service = start database.Config settings TimeProvider.System
        let! profile = register service
        let! duplicate = access service (AccountAccessCommand.Register(username "PLAYER", display, password, SignInOrigin.none))
        equal (Error AccountAccessError.UsernameTaken) duplicate
        let! wrong = access service (AccountAccessCommand.Login(username "player", "incorrect-password", SignInOrigin.none))
        let! absent = access service (AccountAccessCommand.Login(username "absent", password, SignInOrigin.none))
        let! trimmed = access service (AccountAccessCommand.Login(username "player", password.Trim(), SignInOrigin.none))
        equal (Error AccountAccessError.InvalidCredentials) wrong
        equal wrong absent
        equal wrong trimmed
        let! grant = login service
        equal profile grant.Profile
        equal 43 grant.SessionTicket.Length
        check (grant.SessionTicket |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '-' || c = '_')) "Ticket is unpadded base64url."
        do! stop service
    })

    case "concurrent consumption returns the stored profile exactly once" (fun () -> task {
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        use service = start database.Config settings TimeProvider.System
        let! profile = register service
        let! grant = login service
        let! results = [| consume service grant.SessionTicket; consume service grant.SessionTicket |] |> Task.WhenAll
        equal 1 (results |> Array.filter ((=) (Ok (player profile))) |> Array.length)
        equal 1 (results |> Array.filter ((=) (Error SessionAuthenticationError.InvalidTicket)) |> Array.length)
        let! malformed = consume service "not-a-ticket"
        equal (Error SessionAuthenticationError.InvalidTicket) malformed
        do! stop service
    })

    case "tickets expire at the exact monotonic deadline" (fun () -> task {
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        let clock = ManualClock()
        use service = start database.Config settings clock
        let! profile = register service
        let! early = login service
        clock.Advance(TimeSpan.FromMilliseconds 9999.)
        let! accepted = consume service early.SessionTicket
        equal (Ok (player profile)) accepted
        let! expired = login service
        equal 10 expired.ExpiresInSeconds
        clock.Advance(TimeSpan.FromSeconds 10.)
        let! rejected = consume service expired.SessionTicket
        equal (Error SessionAuthenticationError.InvalidTicket) rejected
        do! stop service
    })

    case "ticket capacity backpressures login and is released by consumption or expiration" (fun () -> task {
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        let clock = ManualClock()
        use service = start database.Config { settings with MaxTickets = 1 } clock
        let! profile = register service
        let! first = login service
        let! saturated = access service (AccountAccessCommand.Login(username "player", password, SignInOrigin.none))
        equal (Error AccountAccessError.Busy) saturated
        let! consumed = consume service first.SessionTicket
        equal (Ok (player profile)) consumed
        let! second = login service
        check (first.SessionTicket <> second.SessionTicket) "Login must issue a fresh random ticket."
        clock.Advance(TimeSpan.FromSeconds 10.)
        let! third = login service
        let! stale = consume service second.SessionTicket
        equal (Error SessionAuthenticationError.InvalidTicket) stale
        let! current = consume service third.SessionTicket
        equal (Ok (player profile)) current
        do! stop service
    })

    case "restart preserves account credentials and profile but invalidates old tickets" (fun () -> task {
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        use first = start database.Config settings TimeProvider.System
        let! profile = register first
        let! previous = login first
        do! stop first

        SqliteAccountStore.initialize database.Config |> ok
        use restarted = start database.Config settings TimeProvider.System
        let! rejected = consume restarted previous.SessionTicket
        equal (Error SessionAuthenticationError.InvalidTicket) rejected
        let! fresh = login restarted
        equal profile fresh.Profile
        check (fresh.SessionTicket <> previous.SessionTicket) "A restarted service must not reissue the old ticket."
        let! accepted = consume restarted fresh.SessionTicket
        equal (Ok (player profile)) accepted
        do! stop restarted
    })

    case "graceful stop settles work admitted before Stop and joins its workers" (fun () -> task {
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        use service = start database.Config settings TimeProvider.System
        let! _ = register service
        let first = service.TryAskAsync(fun reply -> AuthMessage.Access(AccountAccessCommand.Login(username "player", password, SignInOrigin.none), reply))
        let second = service.TryAskAsync(fun reply -> AuthMessage.Access(AccountAccessCommand.Login(username "player", password, SignInOrigin.none), reply))
        do! stop service
        let! replies = Task.WhenAll [| first; second |] |> awaitResult
        for response in replies do
            match response with
            | AgentAskResult.Replied(Ok (AccountAccessResult.SignedIn _))
            | AgentAskResult.Replied(Error AccountAccessError.Unavailable) -> ()
            | other -> failtestf "Accepted operation was not settled during graceful stop: %A" other
        let! late = service.TryAskAsync(fun reply -> AuthMessage.Access(AccountAccessCommand.Login(username "player", password, SignInOrigin.none), reply))
        equal AgentAskResult.Closed late
    })
    case "a ticket carries the stored role and a rename refreshes outstanding tickets" (fun () -> task {
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        use service = start database.Config { settings with MaxTickets = 10 } TimeProvider.System
        let! profile = register service
        database.Execute $"INSERT INTO player_roles(player_id, role, granted_by, granted_at) VALUES ({PlayerId.value profile.PlayerId}, 1, NULL, 0)"
        database.Execute "INSERT INTO admin_accounts(id, username, password_hash, created_at) VALUES (1, 'root', 'hash', 0)"
        let admin = AdminId.create 1L |> ok
        let! outstanding = login service
        let renamedTo = DisplayName.create 64 "Новое Имя" |> ok
        let! renamed = access service (AccountAccessCommand.RenamePlayer(profile.PlayerId, renamedTo, admin))
        let expected = PlayerData.withDisplayName renamedTo profile
        equal (Ok (AccountAccessResult.ProfileChanged expected)) renamed
        let! consumed = consume service outstanding.SessionTicket
        equal (Ok ({ Profile = expected; Role = PlayerRole.Moderator; Mute = ValueNone; SignedInFrom = ValueNone } : AuthenticatedPlayer)) consumed
        let! missing = access service (AccountAccessCommand.RenamePlayer(PlayerId.create 404UL |> ok, renamedTo, admin))
        equal (Error AccountAccessError.InvalidCredentials) missing
        do! stop service
    })
    case "a player's own display name change is stored, limited by the interval and recorded" (fun () -> task {
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        use service = start database.Config { settings with MaxTickets = 10 } TimeProvider.System
        let! profile = register service
        let replies = System.Threading.Channels.Channel.CreateUnbounded<ProfileChangeReply>()
        use receiver = Agent.Start(AgentOptions.create "name-replies", fun _ (reply: ProfileChangeReply) -> task {
            replies.Writer.TryWrite reply |> ignore
        })
        let change value = task {
            let request = {
                OperationId = Guid.NewGuid(); PlayerId = profile.PlayerId
                Change = ProfileChange.DisplayName(DisplayName.create 64 value |> ok, TimeSpan.FromHours 1.)
                ReplyTo = receiver.Ref.TryReliable().Value
            }
            let! admitted = (AuthService.authenticator service).Profiles.PostAsync request
            equal AgentDeliveryResult.Posted admitted
            let! reply = replies.Reader.ReadAsync().AsTask() |> awaitResult
            equal request.OperationId reply.OperationId
            return reply.Result
        }
        let! first = change "Своё Имя"
        equal (Ok (PlayerData.withDisplayName (DisplayName.create 64 "Своё Имя" |> ok) profile)) first
        let! second = change "Ещё Одно"
        match second with
        | Error (ProfileChangeError.TooSoon wait) -> check (wait > TimeSpan.FromMinutes 59.) $"About an hour to wait: {wait}"
        | other -> failtestf "%A" other
        let! login = login service
        equal "Своё Имя" (DisplayName.value login.Profile.DisplayName)
        equal 1L (database.Scalar "SELECT count(*) FROM display_name_changes WHERE changed_by IS NULL AND old_name='Persistent Player' AND new_name='Своё Имя'")
        do! stop service
    })

    case "registration follows the stored mode, which survives a restart and never blocks sign-in" (fun () -> task {
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        use first = start database.Config settings TimeProvider.System
        let! initial = access first AccountAccessCommand.ReadRegistration
        equal (Ok (AccountAccessResult.Registration RegistrationMode.Open)) initial
        let! _ = register first
        let! closed = access first (AccountAccessCommand.SetRegistration(RegistrationMode.Manual, ValueNone))
        equal (Ok (AccountAccessResult.Registration RegistrationMode.Manual)) closed
        let! refused = access first (AccountAccessCommand.Register(username "second", display, password, SignInOrigin.none))
        equal (Error (AccountAccessError.RegistrationClosed RegistrationMode.Manual)) refused
        let! _ = access first (AccountAccessCommand.SetRegistration(RegistrationMode.Steam, ValueNone))
        do! stop first

        use second = start database.Config settings TimeProvider.System
        let! stored = access second AccountAccessCommand.ReadRegistration
        equal (Ok (AccountAccessResult.Registration RegistrationMode.Steam)) stored
        let! steamOnly = access second (AccountAccessCommand.Register(username "second", display, password, SignInOrigin.none))
        equal (Error (AccountAccessError.RegistrationClosed RegistrationMode.Steam)) steamOnly
        let! _ = login second
        equal 1L (database.Scalar "SELECT count(*) FROM accounts")
        equal 1L (database.Scalar "SELECT count(*) FROM server_settings WHERE key='registration_mode' AND value='steam' AND changed_by IS NULL")
        do! stop second
    })

    case "a player created in the panel has no password until the setup code is redeemed, in any mode" (fun () -> task {
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        use service = start database.Config settings TimeProvider.System
        let! _ = access service (AccountAccessCommand.SetRegistration(RegistrationMode.Manual, ValueNone))
        let! created = access service (AccountAccessCommand.CreateAccount(username "Invited", display))
        let profile, code =
            match created with
            | Ok (AccountAccessResult.AccountCreated(profile, code)) -> profile, code
            | other -> failtestf "%A" other
        equal "invited" (Username.value profile.Username)
        equal 43 code.Length
        // A setup code lives SetupLifetimeHours, much longer than a reset code.
        let lifetime = database.Scalar "SELECT expires_at FROM auth_tokens WHERE kind=1" - DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        check (abs (lifetime - 72L * 3600L) < 300L) $"Setup code lifetime: {lifetime} s"
        let! duplicate = access service (AccountAccessCommand.CreateAccount(username "invited", display))
        equal (Error AccountAccessError.UsernameTaken) duplicate
        let! noPassword = access service (AccountAccessCommand.Login(username "invited", password, SignInOrigin.none))
        equal (Error AccountAccessError.InvalidCredentials) noPassword
        let! chosen = access service (AccountAccessCommand.ResetPassword(code, password))
        equal (Ok AccountAccessResult.Completed) chosen
        let! again = access service (AccountAccessCommand.ResetPassword(code, password))
        equal (Error AccountAccessError.InvalidCredentials) again
        let! signedIn = access service (AccountAccessCommand.Login(username "invited", password, SignInOrigin.none))
        match signedIn with
        | Ok (AccountAccessResult.SignedIn grant) -> equal profile grant.Profile
        | other -> failtestf "%A" other
        equal 1L (database.Scalar "SELECT count(*) FROM account_identities WHERE provider='password' AND subject='invited'")
        do! stop service
    })

    case "a banned range refuses public sign-in and registration, never trusted callers; addresses are recorded" (fun () -> task {
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        let root = SqliteAdminStore.createFirstAdmin database.Config (username "root") "hash" (fun id -> PanelSession.create "s" id DateTimeOffset.UtcNow (TimeSpan.FromHours 1.)) DateTimeOffset.UtcNow CancellationToken.None |> ok |> Option.get
        use service = start database.Config { settings with MaxTickets = 10 } TimeProvider.System
        let! profile = register service
        let from (text: string) = SignInOrigin.ofAddress (Net.IPAddress.Parse text)
        let signIn origin = access service (AccountAccessCommand.Login(username "player", password, origin))
        let! first = signIn (from "198.51.100.7")
        let grant = match first with Ok (AccountAccessResult.SignedIn grant) -> grant | other -> failtestf "%A" other
        // The ticket remembers where the player signed in from: a game connection
        // through a proxy of the server takes that address.
        let! consumed = consume service grant.SessionTicket
        equal (Ok (ValueSome (Net.IPAddress.Parse "198.51.100.7"))) (consumed |> Result.map _.SignedInFrom)
        let! history = access service (AccountAccessCommand.AddressHistory profile.PlayerId)
        match history with
        | Ok (AccountAccessResult.Addresses [ entry ]) -> equal ("198.51.100.7", 1L) (ClientAddress.text entry.Address, entry.SignIns)
        | other -> failtestf "%A" other
        let reason = SanctionReason.create "Рейд" |> ok
        let! banned = access service (AccountAccessCommand.BanAddresses(AddressRange.parse "198.51.100.0/24" |> ok, reason, SanctionTerm.UntilLifted, root.Id))
        let ban = match banned with Ok (AccountAccessResult.AddressesBanned ban) -> ban | other -> failtestf "%A" other
        let! refused = signIn (from "198.51.100.99")
        equal (Error (AccountAccessError.AddressBanned ban)) refused
        let! registering = access service (AccountAccessCommand.Register(username "second", display, password, from "::ffff:198.51.100.1"))
        equal (Error (AccountAccessError.AddressBanned ban)) registering
        let! elsewhere = signIn (from "203.0.113.1")
        match elsewhere with Ok (AccountAccessResult.SignedIn _) -> () | other -> failtestf "%A" other
        let! trusted = signIn SignInOrigin.none
        match trusted with Ok (AccountAccessResult.SignedIn _) -> () | other -> failtestf "%A" other
        let! listed = access service AccountAccessCommand.ListAddressBans
        equal (Ok (AccountAccessResult.AddressBans [ ban ])) listed
        let! lifted = access service (AccountAccessCommand.LiftAddressBan(ban.Id, root.Id))
        equal (Ok (AccountAccessResult.AddressBanLifted ban)) lifted
        let! again = signIn (from "198.51.100.99")
        match again with Ok (AccountAccessResult.SignedIn _) -> () | other -> failtestf "%A" other
        let! twice = access service (AccountAccessCommand.LiftAddressBan(ban.Id, root.Id))
        equal (Error (AccountAccessError.SanctionRefused SanctionError.NotActive)) twice
        do! stop service
    })

    case "a runtime learns the bans in force when it attaches, then each change, also after a service restart" (fun () -> task {
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        let root = SqliteAdminStore.createFirstAdmin database.Config (username "root") "hash" (fun id -> PanelSession.create "s" id DateTimeOffset.UtcNow (TimeSpan.FromHours 1.)) DateTimeOffset.UtcNow CancellationToken.None |> ok |> Option.get
        let reason = SanctionReason.create "Рейд" |> ok
        let order range = AccountAccessCommand.BanAddresses(AddressRange.parse range |> ok, reason, SanctionTerm.UntilLifted, root.Id)
        let attach (service: Agent<AuthMessage>) = task {
            let changes = System.Collections.Concurrent.ConcurrentQueue<AccountChange>()
            let runtime = Agent.Start(AgentOptions.create "ban-test-runtime", fun _ change -> task { changes.Enqueue change })
            let! targeted = service.PostAsync(AuthMessage.SetChangeTarget(runtime.Ref.TryReliable().Value))
            equal AgentPostResult.Posted targeted
            let next () = task {
                let deadline = Environment.TickCount64 + 5000L
                while changes.IsEmpty && Environment.TickCount64 < deadline do
                    do! Task.Delay 10
                match changes.TryDequeue() with
                | true, AccountChange.AddressBans bans -> return bans |> List.map (fun ban -> AddressRange.key ban.Range)
                | true, other -> return failtestf "Unexpected change: %A" other
                | false, _ -> return failtest "No change arrived."
            }
            return runtime, next
        }
        use first = start database.Config settings TimeProvider.System
        let! _ = access first (order "203.0.113.0/24")
        let! runtime, next = attach first
        let! initial = next ()
        equal [ "203.0.113.0/24" ] initial
        let! _ = access first (order "198.51.100.0/24")
        let! added = next ()
        equal [ "198.51.100.0/24"; "203.0.113.0/24" ] added
        let! listed = access first AccountAccessCommand.ListAddressBans
        let oldest = match listed with Ok (AccountAccessResult.AddressBans bans) -> bans |> List.minBy _.Id | other -> failtestf "%A" other
        let! _ = access first (AccountAccessCommand.LiftAddressBan(oldest.Id, root.Id))
        let! remaining = next ()
        equal [ "198.51.100.0/24" ] remaining
        do! stop first
        runtime.Complete() |> ignore

        use second = start database.Config settings TimeProvider.System
        let! restarted, nextAfterRestart = attach second
        let! loaded = nextAfterRestart ()
        equal [ "198.51.100.0/24" ] loaded
        do! stop second
        restarted.Complete() |> ignore
    })

    case "a ban with devices refuses every account on those devices, never a client that sends none" (fun () -> task {
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        let root = SqliteAdminStore.createFirstAdmin database.Config (username "root") "hash" (fun id -> PanelSession.create "s" id DateTimeOffset.UtcNow (TimeSpan.FromHours 1.)) DateTimeOffset.UtcNow CancellationToken.None |> ok |> Option.get
        use service = start database.Config { settings with MaxTickets = 10 } TimeProvider.System
        let device = DeviceId.create (String.replicate 64 "d") |> ok
        let onDevice = { SignInOrigin.none with Device = ValueSome device }
        let! cheater = access service (AccountAccessCommand.Register(username "cheater", display, password, onDevice))
        let cheater = match cheater with Ok (AccountAccessResult.Registered profile) -> profile | other -> failtestf "%A" other
        let! history = access service (AccountAccessCommand.DeviceHistory cheater.PlayerId)
        match history with
        | Ok (AccountAccessResult.Devices [ entry ]) -> equal device entry.Device
        | other -> failtestf "%A" other
        let order = { Target = cheater.PlayerId; Kind = SanctionKind.Ban; Term = SanctionTerm.UntilLifted; Reason = SanctionReason.create "Спам" |> ok
                      IssuedBy = SanctionIssuer.Admin root.Id; Devices = true }
        let! banned = access service (AccountAccessCommand.Sanction order)
        let ban = match banned with Ok (AccountAccessResult.Sanctioned ban) -> ban | other -> failtestf "%A" other
        let! fresh = access service (AccountAccessCommand.Register(username "fresh", display, password, onDevice))
        equal (Error (AccountAccessError.DeviceBanned ban)) fresh
        let! _ = register service
        let! other = access service (AccountAccessCommand.Login(username "player", password, onDevice))
        equal (Error (AccountAccessError.DeviceBanned ban)) other
        let! elsewhere = access service (AccountAccessCommand.Login(username "player", password, SignInOrigin.none))
        match elsewhere with Ok (AccountAccessResult.SignedIn _) -> () | other -> failtestf "%A" other
        let! lifted = access service (AccountAccessCommand.LiftSanction(cheater.PlayerId, SanctionKind.Ban, SanctionIssuer.Admin root.Id))
        match lifted with Ok (AccountAccessResult.SanctionLifted _) -> () | other -> failtestf "%A" other
        let! back = access service (AccountAccessCommand.Login(username "player", password, onDevice))
        match back with Ok (AccountAccessResult.SignedIn _) -> () | other -> failtestf "%A" other
        do! stop service
    })

    case "a Steam flow creates the account once, signs it in again and hands the grant to its client only" (fun () -> task {
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        use service = start database.Config { settings with MaxTickets = 10 } TimeProvider.System
        let steamId = 76561198000000042UL
        let profile = { SteamId = steamId; PersonaName = ValueSome "Довакин"; Created = ValueSome (DateTimeOffset(2015, 1, 1, 0, 0, 0, TimeSpan.Zero)) }
        let name = DisplayName.create 64 "Довакин" |> ok
        let startFlow () = task {
            let! started = access service (AccountAccessCommand.BeginSteam(SignInOrigin.none, true))
            match started with
            | Ok (AccountAccessResult.SteamStarted(flow, secret, seconds)) ->
                equal 600 seconds
                return flow, secret
            | other -> return failtestf "%A" other
        }
        let! flow, secret = startFlow ()
        let! pending = access service (AccountAccessCommand.PollSteam(flow, secret))
        equal (Ok AccountAccessResult.SteamPending) pending
        let! stranger = access service (AccountAccessCommand.PollSteam(flow, String('x', 43)))
        equal (Error AccountAccessError.FlowUnknown) stranger
        let! completed = access service (AccountAccessCommand.CompleteSteam(flow, profile, name))
        let grant = match completed with Ok (AccountAccessResult.SignedIn grant) -> grant | other -> failtestf "%A" other
        equal "steam.76561198000000042" (Username.value grant.Profile.Username)
        equal "Довакин" (DisplayName.value grant.Profile.DisplayName)
        equal 43 grant.RememberToken.Length
        let! twice = access service (AccountAccessCommand.CompleteSteam(flow, profile, name))
        equal (Error AccountAccessError.FlowUnknown) twice
        let! collected = access service (AccountAccessCommand.PollSteam(flow, secret))
        equal (Ok (AccountAccessResult.SignedIn grant)) collected
        let! gone = access service (AccountAccessCommand.PollSteam(flow, secret))
        equal (Error AccountAccessError.FlowUnknown) gone
        let! ticket = consume service grant.SessionTicket
        equal (Ok (player grant.Profile)) ticket
        // The same Steam account signs in again in manual mode; a new one is refused there.
        let! _ = access service (AccountAccessCommand.SetRegistration(RegistrationMode.Manual, ValueNone))
        let! again, againSecret = startFlow ()
        let! back = access service (AccountAccessCommand.CompleteSteam(again, profile, name))
        match back with
        | Ok (AccountAccessResult.SignedIn second) -> equal grant.Profile second.Profile
        | other -> failtestf "%A" other
        let! _ = access service (AccountAccessCommand.PollSteam(again, againSecret))
        let! newcomer, newcomerSecret = startFlow ()
        let! closed = access service (AccountAccessCommand.CompleteSteam(newcomer, { profile with SteamId = steamId + 1UL }, name))
        equal (Error (AccountAccessError.RegistrationClosed RegistrationMode.Manual)) closed
        let! told = access service (AccountAccessCommand.PollSteam(newcomer, newcomerSecret))
        equal (Error (AccountAccessError.RegistrationClosed RegistrationMode.Manual)) told
        equal 1L (database.Scalar "SELECT count(*) FROM account_identities WHERE provider='steam' AND subject='76561198000000042'")
        equal 0L (database.Scalar "SELECT count(*) FROM account_passwords")
        do! stop service
    })
]
