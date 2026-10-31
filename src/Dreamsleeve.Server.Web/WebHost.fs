namespace Dreamsleeve.Server.Web

open System
open System.Net
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Threading.RateLimiting
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.RateLimiting
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Primitives
open Serilog
open Dreamsleeve.Server.Domain

/// Where one HTTP host listens. The configuration layer has already checked the
/// URL (scheme, host and port only) and whether plain HTTP is allowed there.
type ListenerSettings = {
    ListenUrl: string
    CertificatePath: string
    /// Environment variable with the certificate password; never a setting.
    CertificatePasswordVariable: string
    /// Accept X-Forwarded-For/Proto from a loopback proxy.
    TrustForwardedHeaders: bool
    /// And from these proxies of the server ([Proxies]); the admin panel has none.
    TrustedProxies: AddressRange list
}

/// Who sent a request: the client as the trusted hops report it, and the
/// server's proxy it came through. Client is absent when a proxy sent the
/// request without saying for whom.
type ForwardedClient = {
    Client: IPAddress voption
    Proxy: IPAddress voption
}

/// X-Forwarded-For, read from the right while the hop is trusted: a loopback
/// proxy (TrustForwardedHeaders) or a proxy of the server. Entries left of
/// the first untrusted hop are the client's own words and never count.
[<RequireQualifiedAccess>]
module Forwarding =
    let private plain (address: IPAddress) = if address.IsIPv4MappedToIPv6 then address.MapToIPv4() else address

    let private hop (text: string) =
        match IPAddress.TryParse(text.Trim().Trim('[', ']')) with
        | true, address -> ValueSome (plain address)
        | false, _ -> ValueNone

    let resolve trustLoopback (proxies: AddressRange list) (peer: IPAddress) (forwardedFor: string seq) =
        let isProxy address = proxies |> List.exists (fun range -> AddressRange.contains range address)
        let trusted address = (trustLoopback && IPAddress.IsLoopback address) || isProxy address
        let hops =
            forwardedFor
            |> Seq.collect (fun value -> if isNull value then Array.empty else value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            |> Seq.map hop
            |> Seq.rev
            |> List.ofSeq
        let rec walk current proxy hops =
            let proxy = if isProxy current then ValueSome current else proxy
            match hops with
            | ValueSome next :: rest when trusted current -> walk next proxy rest
            | (ValueSome _ | ValueNone) :: _ | [] ->
                // A proxy that names no client leaves the client unknown.
                let client = if isProxy current then ValueNone else ValueSome current
                {
                    Client = client
                    Proxy = proxy
                }
        walk (plain peer) ValueNone hops


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
        Results.Json(
            {|
                code = code
                message = message
            |},
            statusCode = status)

    let write (context: HttpContext) (result: IResult) = result.ExecuteAsync context

    let noStore (context: HttpContext) = context.Response.Headers.CacheControl <- "no-store"

    [<Literal>]
    let private ForwardedKey = "dreamsleeve.forwarded"

    /// The client and the proxy of the request; without forwarding, the socket peer.
    let forwarded (context: HttpContext) =
        match context.Items.TryGetValue ForwardedKey with
        | true, (:? ForwardedClient as value) -> value
        | true, _ | false, _ ->
            let remote = context.Connection.RemoteIpAddress
            {
                Client = (if isNull remote then ValueNone else ValueSome remote)
                Proxy = ValueNone
            }

    // Trusted hops speak for the client: the connection takes its address
    // (rate limits, sign-in history and bans follow the player) and the scheme
    // its proxy reported.
    let private forward trustLoopback proxies (next: RequestDelegate) (context: HttpContext) : Task =
        let peer = context.Connection.RemoteIpAddress
        if not (isNull peer) then
            let headers = context.Request.Headers
            let found = Forwarding.resolve trustLoopback proxies peer (headers["X-Forwarded-For"] :> string seq)
            let trustedPeer = (trustLoopback && IPAddress.IsLoopback peer) || found.Proxy.IsSome
            if trustedPeer then
                match (headers["X-Forwarded-Proto"].ToString().Split(',') |> Array.last).Trim().ToLowerInvariant() with
                | "http" | "https" as scheme -> context.Request.Scheme <- scheme
                | _ -> ()
            found.Client |> ValueOption.iter (fun client -> context.Connection.RemoteIpAddress <- client)
            context.Items[ForwardedKey] <- found
        next.Invoke context

    /// After the forwarding middleware, when it is enabled, this is the
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
        | BadRequest
        | CallerCanceled
        | Deadline
        | Io of IOException

    // Only the request-stream dependency is adapted. Unknown exceptions leave
    // the request lifetime and remain Kestrel failures.
    let private readChunk (context: HttpContext) (buffer: Memory<byte>) (token: CancellationToken) = task {
        try
            let! received = context.Request.Body.ReadAsync(buffer, token)
            return Ok received
        with
        | :? OperationCanceledException when context.RequestAborted.IsCancellationRequested -> return Error BodyError.CallerCanceled
        | :? OperationCanceledException when token.IsCancellationRequested -> return Error BodyError.Deadline
        | :? Microsoft.AspNetCore.Http.BadHttpRequestException as error ->
            return Error (if error.StatusCode = 413 then BodyError.TooLarge else BodyError.BadRequest)
        | :? IOException as error -> return Error (BodyError.Io error)
    }

    /// Reads at most maxBytes; a larger declared or chunked body is refused.
    let readBody (context: HttpContext) maxBytes (token: CancellationToken) = task {
        if context.Request.ContentLength.HasValue && context.Request.ContentLength.Value > int64 maxBytes then
            return Error BodyError.TooLarge
        else
            // A bounded read also covers chunked bodies and adapters without Kestrel.
            let bytes = Array.zeroCreate<byte> (maxBytes + 1)
            let mutable count = 0
            let mutable ended = false
            let mutable failure = None
            while not ended && failure.IsNone && count < bytes.Length do
                match! readChunk context (bytes.AsMemory(count)) token with
                | Error error -> failure <- Some error
                | Ok 0 -> ended <- true
                | Ok received -> count <- count + received

            match failure with
            | Some error -> return Error error
            | None when count > maxBytes -> return Error BodyError.TooLarge
            | None -> return Ok (ReadOnlyMemory<byte>(bytes, 0, count))
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
        if listener.TrustForwardedHeaders || not listener.TrustedProxies.IsEmpty then
            // Only a proxy on this machine or a proxy of the server may speak for the client.
            app.Use(Func<RequestDelegate, RequestDelegate>(fun next ->
                RequestDelegate(forward listener.TrustForwardedHeaders listener.TrustedProxies next))) |> ignore
        app.Use(Func<RequestDelegate, RequestDelegate>(fun next -> RequestDelegate(secure next))) |> ignore
        app.UseRateLimiter() |> ignore
        // Falco maps its endpoints with UseEndpoints, which needs routing before it.
        app.UseRouting() |> ignore
        app
