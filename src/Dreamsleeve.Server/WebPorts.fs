namespace Dreamsleeve.Server

open System
open System.Net.Http
open System.Threading.Tasks
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure
open Dreamsleeve.Server.Web
open Dreamsleeve.Server.Web.Admin
open Dreamsleeve.Server.Web.Authentication

/// Connects the HTTP hosts of Dreamsleeve.Server.Web to the agents. The routes
/// see only these records of functions.
[<RequireQualifiedAccess>]
module WebPorts =
    /// How long the online table waits for one session.
    [<Literal>]
    let DescribeTimeoutMs = 1000

    let private listener passwordVariable proxies (settings: HttpListenerSettings) : ListenerSettings =
        { ListenUrl = settings.ListenUrl; CertificatePath = settings.CertificatePath
          CertificatePasswordVariable = passwordVariable; TrustForwardedHeaders = settings.TrustForwardedHeaders
          TrustedProxies = proxies }

    /// Players reach it through the server's proxies too; the admin panel never.
    let authListener (settings: ApplicationConfig) =
        listener "DREAMSLEEVE_AUTH_CERTIFICATE_PASSWORD" (Configuration.trustedProxies settings) settings.Authentication.Listener

    // The server's own origin first, then its proxies'.
    let private steamUrls (settings: ApplicationConfig) =
        let steam = settings.Authentication.Steam
        if steam.Enabled then (steam.PublicUrl :: steam.ProxyUrls) |> List.map (fun url -> url.TrimEnd('/')) else []

    /// The optional Steam Web API key: an environment variable, never a setting.
    [<Literal>]
    let SteamKeyVariable = "DREAMSLEEVE_STEAM_WEB_API_KEY"

    let authRoutes (settings: ApplicationConfig) : AuthRouteSettings =
        let authentication = settings.Authentication
        { SteamPublicUrls = steamUrls settings
          RequestsPerMinute = authentication.Listener.RequestsPerMinute
          RequestTimeoutSeconds = authentication.Listener.RequestTimeoutSeconds
          MaxConnections = 2 * authentication.Service.MailboxCapacity + authentication.Service.MaxConcurrentOperations
          Input = settings.Server.ChatInput }

    let adminListener (settings: ApplicationConfig) = listener "DREAMSLEEVE_ADMIN_CERTIFICATE_PASSWORD" [] settings.Admin.Listener

    let adminRoutes (settings: ApplicationConfig) moderation : AdminRouteSettings =
        let admin = settings.Admin
        { SessionHours = admin.Service.SessionHours; LoginAttemptsPerMinute = admin.Service.LoginAttemptsPerMinute
          RequestsPerMinute = admin.Listener.RequestsPerMinute; RequestTimeoutSeconds = admin.Listener.RequestTimeoutSeconds
          MaxConnections = admin.MaxConnections; DescribeTimeoutMs = DescribeTimeoutMs
          Input = settings.Server.ChatInput; Moderation = moderation
          SetupCodeHours = settings.Authentication.Service.SetupLifetimeHours
          AddressHistoryDays = settings.Authentication.Service.SignInHistoryDays }

    // One client for every Steam call; each call has its own deadline as well.
    let private steamHttp = lazy (new HttpClient(Timeout = TimeSpan.FromSeconds 15.))

    let steam (settings: ApplicationConfig) : SteamPorts =
        let publicUrls = steamUrls settings
        let key = Environment.GetEnvironmentVariable SteamKeyVariable
        { Verify = fun flow fields token -> SteamOpenId.verify steamHttp.Value publicUrls flow fields token
          Profile = fun steamId token ->
            if String.IsNullOrWhiteSpace key then Task.FromResult (Ok { SteamId = steamId; PersonaName = ValueNone; Created = ValueNone })
            else SteamOpenId.profile steamHttp.Value key steamId token }

    let auth (settings: ApplicationConfig) (authentication: ReliableAgent<AuthMessage>) : AuthPorts =
        { Access = fun command timeout token ->
            authentication.TryAskAsync((fun reply -> AuthMessage.Access(command, reply)), timeout, token)
          Steam = steam settings }

    let private posted result =
        match result with
        | AgentPostResult.Posted -> true
        | AgentPostResult.Full | AgentPostResult.Closed | AgentPostResult.Canceled | AgentPostResult.Dropped -> false

    /// runtime is the game runtime now serving, none while it restarts: the panel
    /// then reports it unavailable, and stored changes apply at the next sign-in.
    let admin (service: ReliableAgent<AdminMessage>) (authentication: ReliableAgent<AuthMessage>) (runtime: unit -> ReliableAgent<ServerRuntimeMessage> option)
              (describer: Agent<DescribeRequest>) configuration : AdminPorts =
        let ask message timeout token =
            match runtime () with
            | Some agent -> agent.TryAskAsync(message, timeout, token)
            | None -> Task.FromResult AgentAskResult.Closed
        let tell message = runtime () |> Option.exists (fun agent -> agent.TryPost message |> posted)
        { Admin = fun command timeout token -> service.TryAskAsync((fun reply -> AdminMessage.Access(command, reply)), timeout, token)
          Account = fun command timeout token -> authentication.TryAskAsync((fun reply -> AuthMessage.Access(command, reply)), timeout, token)
          Snapshot = ask ServerRuntimeMessage.Read
          Guilds = fun command -> ask (fun reply -> ServerRuntimeMessage.Guilds(command, reply))
          Sessions = ask ServerRuntimeMessage.ListSessions
          Describe = fun timeout row ->
            match row.Session with
            | Some session -> SessionDescriber.describe describer timeout session
            | None -> Task.FromResult(Ok None)
          Announce = fun announcement -> tell (ServerRuntimeMessage.Announce announcement)
          ApplyRole = fun playerId role -> tell (ServerRuntimeMessage.SetPlayerRole(playerId, role))
          ApplyProfile = fun profile -> tell (ServerRuntimeMessage.RenamePlayer profile)
          Configuration = configuration }
