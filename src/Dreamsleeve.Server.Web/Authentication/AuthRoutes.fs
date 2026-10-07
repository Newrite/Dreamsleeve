namespace Dreamsleeve.Server.Web.Authentication

open System
open System.Net
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

/// Steam's side of a sign-in. The composition root connects it to SteamOpenId
/// with one HttpClient; tests pass functions.
type SteamPorts = {
    /// The SteamID of the answer the browser brought back for a flow, confirmed by Steam.
    Verify: string -> (string * string) list -> CancellationToken -> Task<Result<uint64, string>>
    /// The public profile with a Web API key; unknown fields without one.
    Profile: uint64 -> CancellationToken -> Task<SteamProfile>
}

/// The account service as the public routes see it. The composition root
/// connects it to the AuthService agent; tests pass functions.
type AuthPorts = {
    Access: AccountAccessCommand -> TimeSpan -> CancellationToken -> Task<AgentAskResult<Result<AccountAccessResult, AccountAccessError>>>
    Steam: SteamPorts
}

type AuthRouteSettings = {
    /// The public origins Steam sends browsers back to, the server's own first,
    /// then its proxies'; empty: Steam sign-in is off.
    SteamPublicUrls: string list
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
    type private Operation = Register | Login | Resume | Logout | ResetPassword | SteamBegin | SteamPoll

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

    let private request settings moderation origin operation (body: JsonElement) =
        if body.ValueKind <> JsonValueKind.Object then Error (invalid ())
        else
            match operation with
            | Operation.SteamBegin ->
                // A Steam sign-in is remembered unless the client says otherwise.
                match body.TryGetProperty "rememberMe" with
                | false, _ -> Ok (AccountAccessCommand.BeginSteam(origin, true))
                | true, value when value.ValueKind = JsonValueKind.True || value.ValueKind = JsonValueKind.False ->
                    Ok (AccountAccessCommand.BeginSteam(origin, value.ValueKind = JsonValueKind.True))
                | true, _ -> Error (invalid ())
            | Operation.SteamPoll ->
                let flow, secret = field body "flow", field body "secret"
                if AuthService.validToken flow && AuthService.validToken secret then Ok (AccountAccessCommand.PollSteam(flow, secret))
                else Error (invalid ())
            | Operation.Resume | Operation.Logout ->
                let secret = field body "token"
                if not (AuthService.validToken secret) then Error (invalid ())
                elif operation = Operation.Resume then Ok (AccountAccessCommand.Resume(secret, origin))
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
                            | Ok displayName -> Ok (AccountAccessCommand.Register(username, displayName, password, origin))
                        else
                            match body.TryGetProperty "rememberMe" with
                            | false, _ -> Ok (AccountAccessCommand.Login(username, password, origin))
                            | true, value when value.ValueKind = JsonValueKind.False -> Ok (AccountAccessCommand.Login(username, password, origin))
                            | true, value when value.ValueKind = JsonValueKind.True -> Ok (AccountAccessCommand.RememberLogin(username, password, origin))
                            | true, _ -> Error (invalid ())

    // "device" is optional; when present it must be a device hash.
    let private device (body: JsonElement) (origin: SignInOrigin) =
        if body.ValueKind <> JsonValueKind.Object then Ok origin
        else
            match body.TryGetProperty "device" with
            | false, _ -> Ok origin
            | true, value when value.ValueKind = JsonValueKind.String ->
                DeviceId.create (value.GetString()) |> Result.map (fun device -> { origin with Device = ValueSome device })
            | true, _ -> Error (DomainError.InvalidText("DeviceId", TextError.InvalidFormat))

    let private command settings moderation origin operation (body: JsonElement) =
        match device body origin with
        | Error _ -> Error (invalid ())
        | Ok origin -> request settings moderation origin operation body

    let private read settings moderation operation (context: HttpContext) token = task {
        if not (context.Request.HasJsonContentType()) then
            return Error (WebHost.error 415 "unsupported_content_type" "Use application/json.")
        else
            match! WebHost.readBody context MaxBodyBytes token with
            | Error WebHost.BodyError.TooLarge | Error WebHost.BodyError.UnsupportedType -> return Error (tooLarge ())
            | Ok bytes ->
                use body = JsonDocument.Parse(bytes, JsonDocumentOptions(MaxDepth = 8))
                // After the forwarding middleware: a trusted proxy's client and the proxy.
                let forwarded = WebHost.forwarded context
                let origin = { SignInOrigin.none with Address = forwarded.Client; Proxy = forwarded.Proxy }
                return command settings moderation origin operation body.RootElement
    }

    /// The public origin of the host the client asked through: a proxy's when the
    /// request came through it, else the server's own.
    let private steamUrlFor settings (context: HttpContext) =
        let host = context.Request.Host.Value
        settings.SteamPublicUrls
        |> List.tryFind (fun url -> String.Equals(Uri(url).Authority, host, StringComparison.OrdinalIgnoreCase))
        |> Option.orElse (List.tryHead settings.SteamPublicUrls)

    let private response settings (context: HttpContext) = function
        | Ok (AccountAccessResult.SteamStarted(flow, secret, seconds)) ->
            match steamUrlFor settings context with
            | Some publicUrl ->
                WebHost.json 200 {| flow = flow; secret = secret; browserUrl = SteamOpenId.loginUrl publicUrl flow; expiresInSeconds = seconds |}
            | None -> unavailable ()
        | Ok AccountAccessResult.SteamPending -> WebHost.json 202 {| status = "pending" |}
        | Ok (AccountAccessResult.Registered profile) ->
            WebHost.json 201 {| playerId = PlayerId.value profile.PlayerId; username = Username.value profile.Username
                                displayName = DisplayName.value profile.DisplayName |}
        | Ok (AccountAccessResult.SignedIn grant) ->
            WebHost.json 200 {| playerId = PlayerId.value grant.Profile.PlayerId; username = Username.value grant.Profile.Username
                                displayName = DisplayName.value grant.Profile.DisplayName; sessionTicket = grant.SessionTicket
                                expiresInSeconds = grant.ExpiresInSeconds; rememberToken = grant.RememberToken |}
        | Ok AccountAccessResult.Completed -> Results.NoContent()
        // Trusted results never come from a public route.
        | Ok (AccountAccessResult.PasswordResetCreated _) | Ok (AccountAccessResult.ProfileChanged _)
        | Ok (AccountAccessResult.Sanctioned _) | Ok (AccountAccessResult.SanctionLifted _) | Ok AccountAccessResult.Kicked
        | Ok (AccountAccessResult.ActiveSanctions _) | Ok (AccountAccessResult.AccountCreated _) | Ok (AccountAccessResult.Registration _)
        | Ok (AccountAccessResult.AddressesBanned _) | Ok (AccountAccessResult.AddressBanLifted _) | Ok (AccountAccessResult.AddressBans _)
        | Ok (AccountAccessResult.Addresses _) | Ok (AccountAccessResult.PlayersAt _) | Ok (AccountAccessResult.Devices _) -> unavailable ()
        // 403, not 401: a saved login stays saved and works again once the ban ends.
        | Error (AccountAccessError.Banned ban) ->
            WebHost.json 403 {| code = "banned"; message = "The account is banned."; reason = SanctionReason.value ban.Reason
                                untilUnixMs = ban.Expires |> ValueOption.map _.ToUnixTimeMilliseconds() |> ValueOption.toNullable |}
        | Error (AccountAccessError.DeviceBanned ban) ->
            WebHost.json 403 {| code = "device_banned"; message = "This device is banned."; reason = SanctionReason.value ban.Reason
                                untilUnixMs = ban.Expires |> ValueOption.map _.ToUnixTimeMilliseconds() |> ValueOption.toNullable |}
        // Like an account ban: the client shows the reason and the end and stops retrying.
        | Error (AccountAccessError.AddressBanned ban) ->
            WebHost.json 403 {| code = "address_banned"; message = "Connections from this address are banned."; reason = SanctionReason.value ban.Reason
                                untilUnixMs = ban.Expires |> ValueOption.map _.ToUnixTimeMilliseconds() |> ValueOption.toNullable |}
        | Error AccountAccessError.InvalidCredentials -> WebHost.error 401 "invalid_credentials" "Invalid or expired credentials."
        | Error AccountAccessError.UsernameTaken -> WebHost.error 409 "username_taken" "Username is already registered."
        | Error (AccountAccessError.RegistrationClosed RegistrationMode.Steam) ->
            WebHost.error 403 "registration_steam_only" "New accounts are created by signing in through Steam."
        | Error (AccountAccessError.RegistrationClosed _) ->
            WebHost.error 403 "registration_closed" "Registration is closed; an administrator creates accounts."
        | Error AccountAccessError.Busy -> busy ()
        | Error AccountAccessError.FlowUnknown -> WebHost.error 410 "steam_flow_unknown" "The Steam sign-in expired or was already collected."
        // Only a game session can be refused as too soon, only a trusted caller's sanction; never a public route.
        | Error AccountAccessError.Unavailable | Error (AccountAccessError.TooSoon _) | Error (AccountAccessError.SanctionRefused _) -> unavailable ()

    let private handle settings moderation ports (logger: ILogger) operation : HttpHandler = fun context -> task {
        WebHost.noStore context
        let timeout = TimeSpan.FromSeconds(float settings.RequestTimeoutSeconds)
        use deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted)
        deadline.CancelAfter timeout
        let! result = task {
            try
                let steamOff = (operation = Operation.SteamBegin || operation = Operation.SteamPoll) && settings.SteamPublicUrls.IsEmpty
                match! (if steamOff then Task.FromResult(Error (WebHost.error 404 "steam_disabled" "Steam sign-in is not enabled on this server."))
                        else read settings moderation operation context deadline.Token) with
                | Error failure -> return failure
                | Ok command ->
                    match! ports.Access command timeout deadline.Token with
                    | AgentAskResult.Replied value -> return response settings context value
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

    // The page the browser shows after Steam: plain text, no script or style.
    let private page (status: int) (message: string) : IResult =
        let html = $"<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><title>Dreamsleeve</title></head><body><p>{WebUtility.HtmlEncode message}</p></body></html>"
        Results.Content(html, "text/html; charset=utf-8", Text.Encoding.UTF8, Nullable status)

    let private steamOutcome = function
        | Ok (AccountAccessResult.SignedIn _) -> 200, "Вход через Steam выполнен. Вернитесь в игру, это окно можно закрыть."
        | Error (AccountAccessError.Banned ban) | Error (AccountAccessError.DeviceBanned ban) ->
            403, $"Вход запрещён баном: {SanctionReason.value ban.Reason}"
        | Error (AccountAccessError.AddressBanned ban) -> 403, $"IP-адрес заблокирован: {SanctionReason.value ban.Reason}"
        | Error (AccountAccessError.RegistrationClosed _) -> 403, "Регистрация на сервере закрыта: аккаунт создаёт администратор."
        | Error AccountAccessError.FlowUnknown -> 410, "Ссылка входа устарела или уже использована. Начните вход из игры заново."
        | Error AccountAccessError.Busy -> 503, "Сервер занят. Начните вход из игры ещё раз."
        | Ok _ | Error _ -> 503, "Вход через Steam сейчас недоступен."

    // A new Steam account is named after its persona when the word list allows it.
    let private steamName settings moderation (profile: SteamProfile) =
        let fallback () = DisplayName.create settings.Input.DisplayName $"Steam {profile.SteamId % 10000UL:D4}" |> Result.toOption |> Option.get
        match profile.PersonaName |> ValueOption.map (DisplayName.create settings.Input.DisplayName) with
        | ValueSome (Ok name) when Moderation.allows moderation (DisplayName.value name) -> name
        | ValueSome _ | ValueNone -> fallback ()

    /// Steam sends the browser here; the client that began the flow learns the
    /// outcome by polling, the browser shows it as text.
    let private steamReturn settings moderation ports (logger: ILogger) : HttpHandler = fun context -> task {
        WebHost.noStore context
        let timeout = TimeSpan.FromSeconds(float settings.RequestTimeoutSeconds)
        use deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted)
        deadline.CancelAfter timeout
        let! status, message = task {
            let flow = context.Request.Query["flow"].ToString()
            if settings.SteamPublicUrls.IsEmpty then return 404, "Вход через Steam на этом сервере выключен."
            elif not (AuthService.validToken flow) then return 400, "Ссылка входа повреждена. Начните вход из игры заново."
            else
                try
                    let fields = [ for pair in context.Request.Query do if pair.Key.StartsWith("openid.", StringComparison.Ordinal) then pair.Key, pair.Value.ToString() ]
                    match! ports.Steam.Verify flow fields deadline.Token with
                    | Error "canceled" -> return 200, "Вход через Steam отменён. Вернитесь в игру."
                    | Error reason ->
                        logger.Information("Steam sign-in not verified: {Reason}", reason)
                        return 400, "Steam не подтвердил вход. Начните вход из игры заново."
                    | Ok steamId ->
                        let! profile = ports.Steam.Profile steamId deadline.Token
                        match! ports.Access (AccountAccessCommand.CompleteSteam(flow, profile, steamName settings moderation profile)) timeout deadline.Token with
                        | AgentAskResult.Replied result -> return steamOutcome result
                        | AgentAskResult.Full | AgentAskResult.Dropped -> return steamOutcome (Error AccountAccessError.Busy)
                        | AgentAskResult.Closed | AgentAskResult.TimedOut | AgentAskResult.Canceled -> return steamOutcome (Error AccountAccessError.Unavailable)
                        | AgentAskResult.Faulted failure ->
                            logger.Error(failure, "Steam sign-in failed")
                            return steamOutcome (Error AccountAccessError.Unavailable)
                with
                | :? OperationCanceledException -> return 504, "Steam не ответил вовремя. Начните вход из игры заново."
                | :? Net.Http.HttpRequestException -> return 502, "Steam недоступен. Попробуйте позже."
        }
        do! WebHost.write context (page status message)
    }

    /// What the client offers: who may register here and whether Steam is on.
    let private methods settings ports (logger: ILogger) : HttpHandler = fun context -> task {
        WebHost.noStore context
        let timeout = TimeSpan.FromSeconds(float settings.RequestTimeoutSeconds)
        let! result = ports.Access AccountAccessCommand.ReadRegistration timeout context.RequestAborted
        let answer =
            match result with
            | AgentAskResult.Replied (Ok (AccountAccessResult.Registration mode)) ->
                WebHost.json 200 {| registration = RegistrationMode.key mode; steam = not settings.SteamPublicUrls.IsEmpty |}
            | AgentAskResult.Faulted failure ->
                logger.Error(failure, "Authentication methods request failed")
                unavailable ()
            | AgentAskResult.Replied _ | AgentAskResult.Full | AgentAskResult.Dropped | AgentAskResult.Closed
            | AgentAskResult.TimedOut | AgentAskResult.Canceled -> unavailable ()
        do! WebHost.write context answer
    }

    let endpoints settings moderation ports logger = [
        post "/auth/register" (handle settings moderation ports logger Operation.Register)
        post "/auth/login" (handle settings moderation ports logger Operation.Login)
        post "/auth/resume" (handle settings moderation ports logger Operation.Resume)
        post "/auth/logout" (handle settings moderation ports logger Operation.Logout)
        post "/auth/reset-password" (handle settings moderation ports logger Operation.ResetPassword)
        get "/auth/methods" (methods settings ports logger)
        post "/auth/steam/begin" (handle settings moderation ports logger Operation.SteamBegin)
        post "/auth/steam/poll" (handle settings moderation ports logger Operation.SteamPoll)
        get "/auth/steam/return" (steamReturn settings moderation ports logger)
    ]

    /// The caller starts and stops this host and owns the account service.
    let private buildWithContent content httpRequestsPerMinute listener settings moderation ports (logger: ILogger) =
        let limits = { MaxBodyBytes = MaxBodyBytes; MaxConnections = settings.MaxConnections; RequestTimeoutSeconds = settings.RequestTimeoutSeconds }
        let rule (context: HttpContext) =
            if context.Request.Path = PathString("/phantoms/content") then { Bucket = "phantoms"; PermitsPerMinute = httpRequestsPerMinute }
            else { Bucket = "auth"; PermitsPerMinute = settings.RequestsPerMinute }
        let rejected (_: HttpContext) = WebHost.error 429 "rate_limited" "Too many authentication requests. Try again later."
        let app = WebHost.create listener limits rule rejected logger
        app.UseFalco(content @ endpoints settings moderation ports logger) |> ignore
        app

    let build listener settings moderation ports logger = buildWithContent [] 128 listener settings moderation ports logger
    let buildWithPhantoms current httpRequestsPerMinute listener settings moderation ports logger =
        buildWithContent (PhantomRoutes.endpoints current) httpRequestsPerMinute listener settings moderation ports logger
