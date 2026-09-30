module Dreamsleeve.Server.Tests.AdminServiceTests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure
open Expecto
open AgentTests
open BackgroundTests
open SqliteAccountStoreTests

let private ok = function Ok value -> value | Error error -> failtestf "Unexpected result: %A" error
let private name value = Username.create 32 value |> ok
let private password = "Admin-Password-2026"
let private options = { AdminService.defaults with MaxConcurrentOperations = 2; LoginAttemptsPerMinute = 2 }

// Codes and sessions use wall time; Advance never changes machine time.
type private WallClock() =
    inherit TimeProvider()
    let mutable now = DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero)
    override _.GetUtcNow() = now
    member _.Advance(span: TimeSpan) = now <- now + span

let private access (service: Agent<AdminMessage>) command =
    service.AskAsync(fun reply -> AdminMessage.Access(command, reply)) |> awaitResult

let private secret = function
    | Ok (AdminReply.Secret value) -> value
    | other -> failtestf "Expected a secret: %A" other

let private signedIn = function
    | Ok (AdminReply.SignedIn(admin, token, _)) -> admin, token
    | other -> failtestf "Expected a sign-in: %A" other

let private stop (service: Agent<AdminMessage>) = task {
    let! admitted = service.PostAsync AdminMessage.Stop
    equal AgentPostResult.Posted admitted
    do! awaitUnit service.Completion
    equal (Some AgentStopReason.Completed) service.StopReason
}

let private withService options run = task {
    use database = new Database()
    SqliteAccountStore.initialize database.Config |> ok
    let clock = WallClock()
    let service = AdminService.start options database.Config NullLogger.Instance clock |> ok
    do! run service clock
    do! stop service
}

let private setup service = task {
    let! code = access service AdminCommand.IssueSetupCode
    let! reply = access service (AdminCommand.Setup(secret code, name "root", password))
    return signedIn reply
}

let tests = testSequenced (testList "Admin service" [
    case "a setup code exists only without administrators, is one-time and replaced by the next" (fun () ->
        withService options (fun service _ -> task {
            let! status = access service AdminCommand.Status
            equal (Ok (AdminReply.Configured false)) status
            let! first = access service AdminCommand.IssueSetupCode
            let! second = access service AdminCommand.IssueSetupCode
            let first, second = secret first, secret second
            check (first <> second) "a new code each time"
            let! replaced = access service (AdminCommand.Setup(first, name "root", password))
            equal (Error AdminServiceError.InvalidCredentials) replaced
            let! created = access service (AdminCommand.Setup(second, name "root", password))
            let admin, token = signedIn created
            equal "root" (Username.value admin.Username)
            let! reused = access service (AdminCommand.Setup(second, name "other", password))
            equal (Error AdminServiceError.InvalidCredentials) reused
            let! configured = access service AdminCommand.IssueSetupCode
            equal (Error AdminServiceError.AlreadyConfigured) configured
            let! status = access service AdminCommand.Status
            equal (Ok (AdminReply.Configured true)) status
            let! session = access service (AdminCommand.Authenticate token)
            equal (Ok (AdminReply.Admin admin)) session
        }))

    case "a code expires after its lifetime and an expired attempt spends it" (fun () ->
        withService options (fun service clock -> task {
            let! code = access service AdminCommand.IssueSetupCode
            clock.Advance(TimeSpan.FromMinutes(float options.CodeLifetimeMinutes))
            let! late = access service (AdminCommand.Setup(secret code, name "root", password))
            equal (Error AdminServiceError.InvalidCredentials) late
            let! malformed = access service (AdminCommand.Setup("short", name "root", password))
            equal (Error AdminServiceError.InvalidCredentials) malformed
            let! status = access service AdminCommand.Status
            equal (Ok (AdminReply.Configured false)) status
        }))

    case "sign-in attempts are limited per name and minute" (fun () ->
        withService options (fun service clock -> task {
            let! _ = setup service
            for _ in 1 .. options.LoginAttemptsPerMinute do
                let! wrong = access service (AdminCommand.Login(name "root", "Wrong-Password-2026"))
                equal (Error AdminServiceError.InvalidCredentials) wrong
            let! limited = access service (AdminCommand.Login(name "root", password))
            equal (Error AdminServiceError.RateLimited) limited
            let! other = access service (AdminCommand.Login(name "someone", password))
            equal (Error AdminServiceError.InvalidCredentials) other
            clock.Advance(TimeSpan.FromMinutes 1.)
            let! allowed = access service (AdminCommand.Login(name "root", password))
            let admin, _ = signedIn allowed
            equal "root" (Username.value admin.Username)
        }))

    case "a reset code sets a new password, ends every session and logout ends one" (fun () ->
        withService { options with LoginAttemptsPerMinute = 10 } (fun service _ -> task {
            let! admin, first = setup service
            let! login = access service (AdminCommand.Login(name "root", password))
            let _, second = signedIn login
            let! unknown = access service (AdminCommand.IssueResetCode(name "nobody"))
            equal (Error AdminServiceError.NotFound) unknown
            let! code = access service (AdminCommand.IssueResetCode(name "root"))
            let! reset = access service (AdminCommand.ResetPassword(secret code, "Admin-Password-2027"))
            let _, third = signedIn reset
            for old in [ first; second ] do
                let! ended = access service (AdminCommand.Authenticate old)
                equal (Error AdminServiceError.InvalidCredentials) ended
            let! current = access service (AdminCommand.Authenticate third)
            equal (Ok (AdminReply.Admin admin)) current
            let! oldPassword = access service (AdminCommand.Login(name "root", password))
            equal (Error AdminServiceError.InvalidCredentials) oldPassword
            let! loggedOut = access service (AdminCommand.Logout third)
            equal (Ok AdminReply.Completed) loggedOut
            let! ended = access service (AdminCommand.Authenticate third)
            equal (Error AdminServiceError.InvalidCredentials) ended
        }))

    case "API tokens authenticate until revoked; malformed secrets never reach storage" (fun () ->
        withService options (fun service _ -> task {
            let! admin, _ = setup service
            let! created = access service (AdminCommand.CreateApiToken(admin, ApiTokenLabel.create "CI" |> ok))
            let token = secret created
            let! authenticated = access service (AdminCommand.AuthenticateApi token)
            equal (Ok (AdminReply.Admin admin)) authenticated
            let! listed = access service AdminCommand.ListApiTokens
            let hash =
                match listed with
                | Ok (AdminReply.ApiTokens [ info ]) -> info.TokenHash
                | other -> failtestf "%A" other
            equal (Secrets.hash token) hash
            let! revoked = access service (AdminCommand.RevokeApiToken(admin, hash))
            equal (Ok AdminReply.Completed) revoked
            let! refused = access service (AdminCommand.AuthenticateApi token)
            equal (Error AdminServiceError.InvalidCredentials) refused
            let! malformed = access service (AdminCommand.AuthenticateApi "x")
            equal (Error AdminServiceError.InvalidCredentials) malformed
            let! wrongHash = access service (AdminCommand.RevokeApiToken(admin, "not-a-hash"))
            equal (Error AdminServiceError.NotFound) wrongHash
        }))

    case "the service answers Busy while its workers are occupied" (fun () ->
        withService { options with MaxConcurrentOperations = 1 } (fun service _ -> task {
            let! _ = setup service
            // Password hashing keeps the only worker busy; the second request is refused at once.
            let slow = service.AskAsync(fun reply -> AdminMessage.Access(AdminCommand.Login(name "root", password), reply))
            let! busy = access service AdminCommand.Status
            equal (Error AdminServiceError.Busy) busy
            let! finished = awaitResult slow
            signedIn finished |> ignore
        }))
])
