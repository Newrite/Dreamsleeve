namespace Dreamsleeve.Server

open System
open System.Net
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Threading.RateLimiting
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.RateLimiting
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.DependencyInjection
open Serilog
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure

[<RequireQualifiedAccess>]
module AuthenticationHttp =
    [<Literal>]
    let private MaxBodyBytes = 4096

    [<RequireQualifiedAccess>]
    type private Operation = Register | Login

    let private error (status: int) (code: string) (message: string) : IResult =
        Results.Json({| code = code; message = message |}, statusCode = status)

    let private unavailable () = error 503 "unavailable" "Authentication is temporarily unavailable."
    let private invalid () = error 400 "invalid_request" "Invalid authentication request."

    let private field (body: JsonElement) name =
        match body.TryGetProperty(name: string) with
        | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
        | true, _ | false, _ -> null

    let private command settings operation (body: JsonElement) =
        if body.ValueKind <> JsonValueKind.Object then Error (invalid ())
        else
            let username = Username.create settings.Server.ChatInput.Username (field body "username")
            let password = field body "password"
            if not (AuthService.validPassword password) then Error (invalid ())
            else
                match username, operation with
                | Error _, _ -> Error (invalid ())
                | Ok username, Operation.Login -> Ok (AccountAccessCommand.Login(username, password))
                | Ok username, Operation.Register ->
                    match DisplayName.create settings.Server.ChatInput.DisplayName (field body "displayName") with
                    | Ok displayName -> Ok (AccountAccessCommand.Register(username, displayName, password))
                    | Error _ -> Error (invalid ())

    let private read settings operation (context: HttpContext) token = task {
        if not (context.Request.HasJsonContentType()) then
            return Error (error 415 "unsupported_content_type" "Use application/json.")
        elif context.Request.ContentLength.HasValue && context.Request.ContentLength.Value > int64 MaxBodyBytes then
            return Error (error 413 "request_too_large" "Authentication request is too large.")
        else
            // A bounded read also covers chunked bodies and adapters without Kestrel.
            let bytes = Array.zeroCreate<byte> (MaxBodyBytes + 1)
            let mutable count = 0
            let mutable ended = false
            while not ended && count < bytes.Length do
                let! received = context.Request.Body.ReadAsync(bytes.AsMemory(count), token)
                if received = 0 then ended <- true
                else count <- count + received

            if count > MaxBodyBytes then
                return Error (error 413 "request_too_large" "Authentication request is too large.")
            else
                use body = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes, 0, count), JsonDocumentOptions(MaxDepth = 8))
                return command settings operation body.RootElement
    }

    let private response = function
        | Ok (AccountAccessResult.Registered profile) ->
            Results.Json({| playerId = PlayerId.value profile.PlayerId; username = Username.value profile.Username
                            displayName = DisplayName.value profile.DisplayName |}, statusCode = 201)
        | Ok (AccountAccessResult.SignedIn grant) ->
            Results.Json({| playerId = PlayerId.value grant.Profile.PlayerId; username = Username.value grant.Profile.Username
                            displayName = DisplayName.value grant.Profile.DisplayName; sessionTicket = grant.SessionTicket
                            expiresInSeconds = grant.ExpiresInSeconds |})
        | Error AccountAccessError.InvalidCredentials -> error 401 "invalid_credentials" "Invalid username or password."
        | Error AccountAccessError.UsernameTaken -> error 409 "username_taken" "Username is already registered."
        | Error AccountAccessError.Busy -> error 503 "busy" "Authentication is busy. Try again later."
        | Error AccountAccessError.Unavailable -> unavailable ()

    let private handle settings (auth: Agent<AuthMessage>) (logger: Serilog.ILogger) operation (context: HttpContext) : Task<IResult> = task {
        context.Response.Headers.CacheControl <- "no-store"
        use deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted)
        deadline.CancelAfter(TimeSpan.FromSeconds(float settings.Authentication.RequestTimeoutSeconds))
        try
            if operation = Operation.Register && not settings.Authentication.AllowRegistration then
                return error 403 "registration_disabled" "Registration is disabled."
            else
                let! input = read settings operation context deadline.Token
                match input with
                | Error failure -> return failure
                | Ok command ->
                    let! result = auth.TryAskAsync((fun reply -> AuthMessage.Access(command, reply)),
                                      timeout = TimeSpan.FromSeconds(float settings.Authentication.RequestTimeoutSeconds),
                                      cancellationToken = deadline.Token)
                    match result with
                    | AgentAskResult.Replied value -> return response value
                    | AgentAskResult.Full | AgentAskResult.Dropped -> return error 503 "busy" "Authentication is busy. Try again later."
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
        | :? Microsoft.AspNetCore.Http.BadHttpRequestException as failure ->
            if failure.StatusCode = 413 then return error 413 "request_too_large" "Authentication request is too large."
            else return invalid ()
        | :? JsonException -> return invalid ()
        | failure ->
            logger.Error(failure, "Authentication HTTP operation failed")
            return unavailable ()
    }

    let private configureRate settings (options: RateLimiterOptions) =
        let partition (context: HttpContext) =
            let remote = context.Connection.RemoteIpAddress
            let key = if isNull remote then "unknown" else remote.MapToIPv6().ToString()
            let limits (_: string) =
                FixedWindowRateLimiterOptions(PermitLimit = settings.Authentication.RequestsPerMinute,
                    Window = TimeSpan.FromMinutes 1., QueueLimit = 0, AutoReplenishment = true)
            RateLimitPartition.GetFixedWindowLimiter(key, Func<string, FixedWindowRateLimiterOptions>(limits))
        let rejected (context: OnRejectedContext) (_: CancellationToken) =
            context.HttpContext.Response.Headers.CacheControl <- "no-store"
            let result = error 429 "rate_limited" "Too many authentication requests. Try again later."
            ValueTask(result.ExecuteAsync context.HttpContext)
        options.GlobalLimiter <- PartitionedRateLimiter.Create<HttpContext, string>(Func<HttpContext, RateLimitPartition<string>>(partition))
        options.OnRejected <- Func<OnRejectedContext, CancellationToken, ValueTask>(rejected)

    let private configureEndpoint settings (endpoint: ListenOptions) =
        if Uri(settings.Authentication.ListenUrl).Scheme = Uri.UriSchemeHttps then
            if String.IsNullOrWhiteSpace settings.Authentication.CertificatePath then
                endpoint.UseHttps() |> ignore
            else
                endpoint.UseHttps(settings.Authentication.CertificatePath,
                    Environment.GetEnvironmentVariable "DREAMSLEEVE_AUTH_CERTIFICATE_PASSWORD") |> ignore

    let private configureServer settings (server: KestrelServerOptions) =
        server.Limits.MaxRequestBodySize <- Nullable(int64 MaxBodyBytes)
        server.Limits.MaxConcurrentConnections <- Nullable(int64 (2 * settings.Authentication.Service.MailboxCapacity + settings.Authentication.Service.MaxConcurrentOperations))
        server.Limits.RequestHeadersTimeout <- TimeSpan.FromSeconds(float settings.Authentication.RequestTimeoutSeconds)
        server.Limits.KeepAliveTimeout <- TimeSpan.FromSeconds(float settings.Authentication.RequestTimeoutSeconds)
        let address = Uri(settings.Authentication.ListenUrl)
        let configure = Action<ListenOptions>(configureEndpoint settings)
        match IPAddress.TryParse(address.Host.Trim('[', ']')) with
        | true, ip -> server.Listen(ip, address.Port, configure)
        | false, _ when address.Host = "localhost" -> server.ListenLocalhost(address.Port, configure)
        | false, _ -> server.ListenAnyIP(address.Port, configure)

    /// The caller starts/stops this host and owns the authentication agent separately.
    let build settings (auth: Agent<AuthMessage>) (logger: Serilog.ILogger) =
        let builder = WebApplication.CreateBuilder(WebApplicationOptions(Args = Array.empty))
        builder.Host.UseSerilog(logger, dispose = false) |> ignore
        builder.WebHost.ConfigureKestrel(Action<KestrelServerOptions>(configureServer settings)) |> ignore
        builder.Services.AddRateLimiter(Action<RateLimiterOptions>(configureRate settings)) |> ignore
        let app = builder.Build()
        app.UseRateLimiter() |> ignore
        app.MapPost("/auth/register", Func<HttpContext, Task<IResult>>(handle settings auth logger Operation.Register)) |> ignore
        app.MapPost("/auth/login", Func<HttpContext, Task<IResult>>(handle settings auth logger Operation.Login)) |> ignore
        app
