namespace Dreamsleeve.Server.Web.Authentication

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Falco
open Falco.Routing
open Serilog
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure
open Dreamsleeve.Server.Web

/// The account service as the public routes see it. The composition root
/// connects it to the AuthService agent; tests pass functions.
type AuthPorts = {
    Access: AccountAccessCommand -> TimeSpan -> CancellationToken -> Task<AgentAskResult<Result<AccountAccessResult, AccountAccessError>>>
}

type AuthRouteSettings = {
    RequestsPerMinute: int
    RequestTimeoutSeconds: int
    /// Service admission bounds the useful number of open connections.
    MaxConnections: int
    Input: ChatInputLimits
}

/// The public contract of docs/AuthenticationRu.md, «HTTP-контракт и
/// ограничения»: five JSON routes, {code, message} errors, 4096-byte bodies.
[<RequireQualifiedAccess>]
module AuthRoutes =
    [<Literal>]
    let MaxBodyBytes = 4096

    [<RequireQualifiedAccess>]
    type private Operation = Register | Login | Resume | Logout | ResetPassword

    let private unavailable () = WebHost.error 503 "unavailable" "Authentication is temporarily unavailable."
    let private invalid () = WebHost.error 400 "invalid_request" "Invalid authentication request."
    let private tooLarge () = WebHost.error 413 "request_too_large" "Authentication request is too large."
    let private busy () = WebHost.error 503 "busy" "Authentication is busy. Try again later."

    // Separate codes let the client say which field to change.
    let private nameNotAllowed field = WebHost.error 400 $"{field}_not_allowed" "Name contains words that are not allowed."

    let private field (body: JsonElement) name =
        match body.TryGetProperty(name: string) with
        | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
        | true, _ | false, _ -> null

    let private command settings moderation operation (body: JsonElement) =
        if body.ValueKind <> JsonValueKind.Object then Error (invalid ())
        else
            match operation with
            | Operation.Resume | Operation.Logout ->
                let secret = field body "token"
                if not (AuthService.validToken secret) then Error (invalid ())
                elif operation = Operation.Resume then Ok (AccountAccessCommand.Resume secret)
                else Ok (AccountAccessCommand.Logout secret)
            | Operation.ResetPassword ->
                let code, password = field body "code", field body "password"
                if AuthService.validToken code && AuthService.validPassword password then
                    Ok (AccountAccessCommand.ResetPassword(code, password))
                else Error (invalid ())
            | Operation.Register | Operation.Login ->
                let username = Username.create settings.Input.Username (field body "username")
                let password = field body "password"
                if not (AuthService.validPassword password) then Error (invalid ())
                else
                    match username with
                    | Error _ -> Error (invalid ())
                    | Ok username ->
                        if operation = Operation.Register then
                            match DisplayName.create settings.Input.DisplayName (field body "displayName") with
                            | Error _ -> Error (invalid ())
                            | Ok _ when not (Moderation.allowsUsername moderation username) -> Error (nameNotAllowed "username")
                            | Ok displayName when not (Moderation.allows moderation (DisplayName.value displayName)) ->
                                Error (nameNotAllowed "display_name")
                            | Ok displayName -> Ok (AccountAccessCommand.Register(username, displayName, password))
                        else
                            match body.TryGetProperty "rememberMe" with
                            | false, _ -> Ok (AccountAccessCommand.Login(username, password))
                            | true, value when value.ValueKind = JsonValueKind.False -> Ok (AccountAccessCommand.Login(username, password))
                            | true, value when value.ValueKind = JsonValueKind.True -> Ok (AccountAccessCommand.RememberLogin(username, password))
                            | true, _ -> Error (invalid ())

    let private read settings moderation operation (context: HttpContext) token = task {
        if not (context.Request.HasJsonContentType()) then
            return Error (WebHost.error 415 "unsupported_content_type" "Use application/json.")
        else
            match! WebHost.readBody context MaxBodyBytes token with
            | Error WebHost.BodyError.TooLarge | Error WebHost.BodyError.UnsupportedType -> return Error (tooLarge ())
            | Ok bytes ->
                use body = JsonDocument.Parse(bytes, JsonDocumentOptions(MaxDepth = 8))
                return command settings moderation operation body.RootElement
    }

    let private response = function
        | Ok (AccountAccessResult.Registered profile) ->
            WebHost.json 201 {| playerId = PlayerId.value profile.PlayerId; username = Username.value profile.Username
                                displayName = DisplayName.value profile.DisplayName |}
        | Ok (AccountAccessResult.SignedIn grant) ->
            WebHost.json 200 {| playerId = PlayerId.value grant.Profile.PlayerId; username = Username.value grant.Profile.Username
                                displayName = DisplayName.value grant.Profile.DisplayName; sessionTicket = grant.SessionTicket
                                expiresInSeconds = grant.ExpiresInSeconds; rememberToken = grant.RememberToken |}
        | Ok AccountAccessResult.Completed -> Results.NoContent()
        // Trusted results never come from a public route.
        | Ok (AccountAccessResult.PasswordResetCreated _) | Ok (AccountAccessResult.Renamed _)
        | Ok (AccountAccessResult.Sanctioned _) | Ok (AccountAccessResult.SanctionLifted _) | Ok AccountAccessResult.Kicked
        | Ok (AccountAccessResult.ActiveSanctions _) | Ok (AccountAccessResult.AccountCreated _) | Ok (AccountAccessResult.Registration _) -> unavailable ()
        // 403, not 401: a saved login stays saved and works again once the ban ends.
        | Error (AccountAccessError.Banned ban) ->
            WebHost.json 403 {| code = "banned"; message = "The account is banned."; reason = SanctionReason.value ban.Reason
                                untilUnixMs = ban.Expires |> ValueOption.map _.ToUnixTimeMilliseconds() |> ValueOption.toNullable |}
        | Error AccountAccessError.InvalidCredentials -> WebHost.error 401 "invalid_credentials" "Invalid or expired credentials."
        | Error AccountAccessError.UsernameTaken -> WebHost.error 409 "username_taken" "Username is already registered."
        | Error (AccountAccessError.RegistrationClosed RegistrationMode.Steam) ->
            WebHost.error 403 "registration_steam_only" "New accounts are created by signing in through Steam."
        | Error (AccountAccessError.RegistrationClosed _) ->
            WebHost.error 403 "registration_closed" "Registration is closed; an administrator creates accounts."
        | Error AccountAccessError.Busy -> busy ()
        // Only a game session can be refused as too soon, only a trusted caller's sanction; never a public route.
        | Error AccountAccessError.Unavailable | Error (AccountAccessError.TooSoon _) | Error (AccountAccessError.SanctionRefused _) -> unavailable ()

    let private handle settings moderation ports (logger: ILogger) operation : HttpHandler = fun context -> task {
        WebHost.noStore context
        let timeout = TimeSpan.FromSeconds(float settings.RequestTimeoutSeconds)
        use deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted)
        deadline.CancelAfter timeout
        let! result = task {
            try
                match! read settings moderation operation context deadline.Token with
                | Error failure -> return failure
                | Ok command ->
                    match! ports.Access command timeout deadline.Token with
                    | AgentAskResult.Replied value -> return response value
                    | AgentAskResult.Full | AgentAskResult.Dropped -> return busy ()
                    | AgentAskResult.Closed | AgentAskResult.TimedOut -> return unavailable ()
                    | AgentAskResult.Canceled ->
                        if context.RequestAborted.IsCancellationRequested then return Results.StatusCode 499
                        else return unavailable ()
                    | AgentAskResult.Faulted failure ->
                        logger.Error(failure, "Authentication request failed")
                        return unavailable ()
            with
            | :? OperationCanceledException when context.RequestAborted.IsCancellationRequested -> return Results.StatusCode 499
            | :? OperationCanceledException when deadline.IsCancellationRequested -> return unavailable ()
            | :? BadHttpRequestException as failure ->
                if failure.StatusCode = 413 then return tooLarge ()
                else return invalid ()
            | :? JsonException -> return invalid ()
            | failure ->
                logger.Error(failure, "Authentication HTTP operation failed")
                return unavailable ()
        }
        do! WebHost.write context result
    }

    let endpoints settings moderation ports logger = [
        post "/auth/register" (handle settings moderation ports logger Operation.Register)
        post "/auth/login" (handle settings moderation ports logger Operation.Login)
        post "/auth/resume" (handle settings moderation ports logger Operation.Resume)
        post "/auth/logout" (handle settings moderation ports logger Operation.Logout)
        post "/auth/reset-password" (handle settings moderation ports logger Operation.ResetPassword)
    ]

    /// The caller starts and stops this host and owns the account service.
    let build listener settings moderation ports (logger: ILogger) =
        let limits = { MaxBodyBytes = MaxBodyBytes; MaxConnections = settings.MaxConnections; RequestTimeoutSeconds = settings.RequestTimeoutSeconds }
        let rule (_: HttpContext) = { Bucket = "auth"; PermitsPerMinute = settings.RequestsPerMinute }
        let rejected (_: HttpContext) = WebHost.error 429 "rate_limited" "Too many authentication requests. Try again later."
        let app = WebHost.create listener limits rule rejected logger
        app.UseFalco(endpoints settings moderation ports logger) |> ignore
        app
