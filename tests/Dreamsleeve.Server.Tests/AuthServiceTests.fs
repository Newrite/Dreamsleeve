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
let private player profile : AuthenticatedPlayer = { Profile = profile; Role = PlayerRole.Player }
let private settings = { AuthService.defaults with MailboxCapacity = 8; MaxConcurrentOperations = 2; MaxTickets = 2; TicketLifetimeSeconds = 10 }

// Expiry uses monotonic time. Advance never sleeps or changes machine time.
type private ManualClock() =
    inherit TimeProvider()
    let mutable ticks = 0L
    override _.TimestampFrequency = TimeSpan.TicksPerSecond
    override _.GetTimestamp() = Interlocked.Read &ticks
    member _.Advance(span: TimeSpan) = Interlocked.Add(&ticks, span.Ticks) |> ignore

let private start database options clock =
    AuthService.start options database NullLogger.Instance clock |> ok

let private access (service: Agent<AuthMessage>) command =
    service.AskAsync(fun reply -> AuthMessage.Access(command, reply)) |> awaitResult

let private register service = task {
    let! result = access service (AccountAccessCommand.Register(username "player", display, password))
    match result with
    | Ok (AccountAccessResult.Registered profile) -> return profile
    | other -> return failtestf "Registration failed: %A" other
}

let private login service = task {
    let! result = access service (AccountAccessCommand.Login(username "PLAYER", password))
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
    let! result = access service (AccountAccessCommand.RememberLogin(username "player", password))
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
        let! resumed = access second (AccountAccessCommand.Resume saved.RememberToken)
        let grant = match resumed with Ok (AccountAccessResult.SignedIn value) -> value | other -> failtestf "%A" other
        equal profile grant.Profile
        let! independent = login second
        let! signedOut = access second (AccountAccessCommand.Logout saved.RememberToken)
        equal (Ok AccountAccessResult.Completed) signedOut
        let! denied = access second (AccountAccessCommand.Resume saved.RememberToken)
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
        let! oldSaved = access service (AccountAccessCommand.Resume saved.RememberToken)
        equal (Error AccountAccessError.InvalidCredentials) oldSaved
        let replacement = "replacement-password-2026"
        let! changed = access service (AccountAccessCommand.ResetPassword(code, replacement))
        equal (Ok AccountAccessResult.Completed) changed
        let! duplicate = access service (AccountAccessCommand.ResetPassword(code, password))
        equal (Error AccountAccessError.InvalidCredentials) duplicate
        let! oldPassword = access service (AccountAccessCommand.Login(username "player", password))
        equal (Error AccountAccessError.InvalidCredentials) oldPassword
        let! newPassword = access service (AccountAccessCommand.Login(username "player", replacement))
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
        let! evicted = access service (AccountAccessCommand.Resume old.RememberToken)
        equal (Error AccountAccessError.InvalidCredentials) evicted
        database.Execute "UPDATE auth_tokens SET expires_at=0"
        let! expired = access service (AccountAccessCommand.Resume current.RememberToken)
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
        let! duplicate = access service (AccountAccessCommand.Register(username "PLAYER", display, password))
        equal (Error AccountAccessError.UsernameTaken) duplicate
        let! wrong = access service (AccountAccessCommand.Login(username "player", "incorrect-password"))
        let! absent = access service (AccountAccessCommand.Login(username "absent", password))
        let! trimmed = access service (AccountAccessCommand.Login(username "player", password.Trim()))
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
        let! saturated = access service (AccountAccessCommand.Login(username "player", password))
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
        let first = service.TryAskAsync(fun reply -> AuthMessage.Access(AccountAccessCommand.Login(username "player", password), reply))
        let second = service.TryAskAsync(fun reply -> AuthMessage.Access(AccountAccessCommand.Login(username "player", password), reply))
        do! stop service
        let! replies = Task.WhenAll [| first; second |] |> awaitResult
        for response in replies do
            match response with
            | AgentAskResult.Replied(Ok (AccountAccessResult.SignedIn _))
            | AgentAskResult.Replied(Error AccountAccessError.Unavailable) -> ()
            | other -> failtestf "Accepted operation was not settled during graceful stop: %A" other
        let! late = service.TryAskAsync(fun reply -> AuthMessage.Access(AccountAccessCommand.Login(username "player", password), reply))
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
        equal (Ok (AccountAccessResult.Renamed expected)) renamed
        let! consumed = consume service outstanding.SessionTicket
        equal (Ok ({ Profile = expected; Role = PlayerRole.Moderator } : AuthenticatedPlayer)) consumed
        let! missing = access service (AccountAccessCommand.RenamePlayer(PlayerId.create 404UL |> ok, renamedTo, admin))
        equal (Error AccountAccessError.InvalidCredentials) missing
        do! stop service
    })
    case "a player's own display name change is stored, limited by the interval and recorded" (fun () -> task {
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        use service = start database.Config { settings with MaxTickets = 10 } TimeProvider.System
        let! profile = register service
        let replies = System.Threading.Channels.Channel.CreateUnbounded<DisplayNameChangeReply>()
        use receiver = Agent.Start(AgentOptions.create "name-replies", fun _ (reply: DisplayNameChangeReply) -> task {
            replies.Writer.TryWrite reply |> ignore
        })
        let change value = task {
            let request = {
                OperationId = Guid.NewGuid(); PlayerId = profile.PlayerId; DisplayName = DisplayName.create 64 value |> ok
                MinInterval = TimeSpan.FromHours 1.; ReplyTo = receiver.Ref.TryReliable().Value
            }
            let! admitted = (AuthService.authenticator service).DisplayNames.PostAsync request
            equal AgentDeliveryResult.Posted admitted
            let! reply = replies.Reader.ReadAsync().AsTask() |> awaitResult
            equal request.OperationId reply.OperationId
            return reply.Result
        }
        let! first = change "Своё Имя"
        equal (Ok (PlayerData.withDisplayName (DisplayName.create 64 "Своё Имя" |> ok) profile)) first
        let! second = change "Ещё Одно"
        match second with
        | Error (DisplayNameChangeError.TooSoon wait) -> check (wait > TimeSpan.FromMinutes 59.) $"About an hour to wait: {wait}"
        | other -> failtestf "%A" other
        let! login = login service
        equal "Своё Имя" (DisplayName.value login.Profile.DisplayName)
        equal 1L (database.Scalar "SELECT count(*) FROM display_name_changes WHERE changed_by IS NULL AND old_name='Persistent Player' AND new_name='Своё Имя'")
        do! stop service
    })
]
