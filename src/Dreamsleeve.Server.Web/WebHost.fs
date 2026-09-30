namespace Dreamsleeve.Server.Web

open System
open System.Net
open System.Threading
open System.Threading.Tasks
open System.Threading.RateLimiting
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.HttpOverrides
open Microsoft.AspNetCore.RateLimiting
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.DependencyInjection
open Serilog

/// Where one HTTP host listens. The configuration layer has already checked the
/// URL (scheme, host and port only) and whether plain HTTP is allowed there.
type ListenerSettings = {
    ListenUrl: string
    CertificatePath: string
    /// Environment variable with the certificate password; never a setting.
    CertificatePasswordVariable: string
    /// Accept X-Forwarded-For/Proto, and only from a loopback proxy.
    TrustForwardedHeaders: bool
}

type HostLimits = {
    MaxBodyBytes: int
    MaxConnections: int
    RequestTimeoutSeconds: int
}

/// A fixed one-minute window per client address; the rule chooses the bucket.
type RateRule = {
    Bucket: string
    PermitsPerMinute: int
}

/// HTTP plumbing shared by the public authentication host and the admin panel:
/// Kestrel limits, client address, rate limiting, safe headers, JSON errors and
/// bounded body reads. Routes live in their own modules and depend on ports.
[<RequireQualifiedAccess>]
module WebHost =
    let json (status: int) (value: obj) : IResult = Results.Json(value, statusCode = status)

    /// The one error shape of both hosts: {code, message}.
    let error (status: int) (code: string) (message: string) : IResult =
        Results.Json({| code = code; message = message |}, statusCode = status)

    let write (context: HttpContext) (result: IResult) = result.ExecuteAsync context

    let noStore (context: HttpContext) = context.Response.Headers.CacheControl <- "no-store"

    /// After the forwarded-headers middleware, when it is enabled, this is the
    /// address the trusted proxy reported; otherwise the socket peer.
    let clientKey (context: HttpContext) =
        let remote = context.Connection.RemoteIpAddress
        if isNull remote then "unknown" else remote.MapToIPv6().ToString()

    let isLoopback (context: HttpContext) =
        let remote = context.Connection.RemoteIpAddress
        not (isNull remote) && IPAddress.IsLoopback remote

    [<RequireQualifiedAccess>]
    type BodyError =
        | UnsupportedType
        | TooLarge

    /// Reads at most maxBytes; a larger declared or chunked body is refused.
    let readBody (context: HttpContext) maxBytes (token: CancellationToken) = task {
        if context.Request.ContentLength.HasValue && context.Request.ContentLength.Value > int64 maxBytes then
            return Error BodyError.TooLarge
        else
            // A bounded read also covers chunked bodies and adapters without Kestrel.
            let bytes = Array.zeroCreate<byte> (maxBytes + 1)
            let mutable count = 0
            let mutable ended = false
            while not ended && count < bytes.Length do
                let! received = context.Request.Body.ReadAsync(bytes.AsMemory(count), token)
                if received = 0 then ended <- true
                else count <- count + received
            if count > maxBytes then return Error BodyError.TooLarge
            else return Ok (ReadOnlyMemory<byte>(bytes, 0, count))
    }

    /// Browsers get no inline script or style, no framing and no referrer; the
    /// JSON API shares the same headers.
    let private secure (next: RequestDelegate) (context: HttpContext) : Task =
        let headers = context.Response.Headers
        headers["X-Content-Type-Options"] <- "nosniff"
        headers["Referrer-Policy"] <- "no-referrer"
        headers["X-Frame-Options"] <- "DENY"
        headers["Content-Security-Policy"] <- "default-src 'self'; frame-ancestors 'none'; form-action 'self'; base-uri 'none'"
        next.Invoke context

    let private configureEndpoint listener (endpoint: ListenOptions) =
        if Uri(listener.ListenUrl).Scheme = Uri.UriSchemeHttps then
            if String.IsNullOrWhiteSpace listener.CertificatePath then
                endpoint.UseHttps() |> ignore
            else
                endpoint.UseHttps(listener.CertificatePath, Environment.GetEnvironmentVariable listener.CertificatePasswordVariable) |> ignore

    let private configureServer listener limits (server: KestrelServerOptions) =
        server.AddServerHeader <- false
        server.Limits.MaxRequestBodySize <- Nullable(int64 limits.MaxBodyBytes)
        server.Limits.MaxConcurrentConnections <- Nullable(int64 limits.MaxConnections)
        server.Limits.RequestHeadersTimeout <- TimeSpan.FromSeconds(float limits.RequestTimeoutSeconds)
        server.Limits.KeepAliveTimeout <- TimeSpan.FromSeconds(float limits.RequestTimeoutSeconds)
        let address = Uri(listener.ListenUrl)
        let configure = Action<ListenOptions>(configureEndpoint listener)
        match IPAddress.TryParse(address.Host.Trim('[', ']')) with
        | true, ip -> server.Listen(ip, address.Port, configure)
        | false, _ when address.Host = "localhost" -> server.ListenLocalhost(address.Port, configure)
        | false, _ -> server.ListenAnyIP(address.Port, configure)

    let private configureRate (rule: HttpContext -> RateRule) (rejected: HttpContext -> IResult) (options: RateLimiterOptions) =
        let partition (context: HttpContext) =
            let chosen = rule context
            let limits (_: string) =
                FixedWindowRateLimiterOptions(PermitLimit = chosen.PermitsPerMinute,
                    Window = TimeSpan.FromMinutes 1., QueueLimit = 0, AutoReplenishment = true)
            RateLimitPartition.GetFixedWindowLimiter(chosen.Bucket + "|" + clientKey context, Func<string, FixedWindowRateLimiterOptions>(limits))
        let onRejected (context: OnRejectedContext) (_: CancellationToken) =
            noStore context.HttpContext
            ValueTask((rejected context.HttpContext).ExecuteAsync context.HttpContext)
        options.GlobalLimiter <- PartitionedRateLimiter.Create<HttpContext, string>(Func<HttpContext, RateLimitPartition<string>>(partition))
        options.OnRejected <- Func<OnRejectedContext, CancellationToken, ValueTask>(onRejected)

    /// Builds an unstarted host. The caller maps routes, starts and stops it and
    /// owns every agent behind the ports separately.
    let create listener limits (rule: HttpContext -> RateRule) (rejected: HttpContext -> IResult) (logger: ILogger) =
        let builder = WebApplication.CreateBuilder(WebApplicationOptions(Args = Array.empty))
        builder.Host.UseSerilog(logger, dispose = false) |> ignore
        builder.WebHost.ConfigureKestrel(Action<KestrelServerOptions>(configureServer listener limits)) |> ignore
        builder.Services.AddRateLimiter(Action<RateLimiterOptions>(configureRate rule rejected)) |> ignore
        let app = builder.Build()
        if listener.TrustForwardedHeaders then
            // Only a proxy on this machine may speak for the client.
            let forwarded = ForwardedHeadersOptions(ForwardedHeaders = (ForwardedHeaders.XForwardedFor ||| ForwardedHeaders.XForwardedProto), ForwardLimit = Nullable 1)
            forwarded.KnownIPNetworks.Clear()
            forwarded.KnownProxies.Clear()
            forwarded.KnownProxies.Add IPAddress.Loopback
            forwarded.KnownProxies.Add IPAddress.IPv6Loopback
            app.UseForwardedHeaders forwarded |> ignore
        app.Use(Func<RequestDelegate, RequestDelegate>(fun next -> RequestDelegate(secure next))) |> ignore
        app.UseRateLimiter() |> ignore
        // Falco maps its endpoints with UseEndpoints, which needs routing before it.
        app.UseRouting() |> ignore
        app
