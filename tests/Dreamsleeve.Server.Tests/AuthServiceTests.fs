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

let tests = testList "Authentication service" [
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
        equal 1 (results |> Array.filter ((=) (Ok profile)) |> Array.length)
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
        equal (Ok profile) accepted
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
        equal (Ok profile) consumed
        let! second = login service
        check (first.SessionTicket <> second.SessionTicket) "Login must issue a fresh random ticket."
        clock.Advance(TimeSpan.FromSeconds 10.)
        let! third = login service
        let! stale = consume service second.SessionTicket
        equal (Error SessionAuthenticationError.InvalidTicket) stale
        let! current = consume service third.SessionTicket
        equal (Ok profile) current
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
        equal (Ok profile) accepted
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
]
