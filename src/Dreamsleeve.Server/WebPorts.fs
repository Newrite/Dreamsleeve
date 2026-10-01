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

    let private listener passwordVariable (settings: HttpListenerSettings) : ListenerSettings =
        { ListenUrl = settings.ListenUrl; CertificatePath = settings.CertificatePath
          CertificatePasswordVariable = passwordVariable; TrustForwardedHeaders = settings.TrustForwardedHeaders }

    let authListener (settings: ApplicationConfig) = listener "DREAMSLEEVE_AUTH_CERTIFICATE_PASSWORD" settings.Authentication.Listener

    /// The optional Steam Web API key: an environment variable, never a setting.
    [<Literal>]
    let SteamKeyVariable = "DREAMSLEEVE_STEAM_WEB_API_KEY"

    let authRoutes (settings: ApplicationConfig) : AuthRouteSettings =
        let authentication = settings.Authentication
        { SteamPublicUrl = if authentication.Steam.Enabled then ValueSome (authentication.Steam.PublicUrl.TrimEnd('/')) else ValueNone
          RequestsPerMinute = authentication.Listener.RequestsPerMinute
          RequestTimeoutSeconds = authentication.Listener.RequestTimeoutSeconds
          MaxConnections = 2 * authentication.Service.MailboxCapacity + authentication.Service.MaxConcurrentOperations
          Input = settings.Server.ChatInput }

    let adminListener (settings: ApplicationConfig) = listener "DREAMSLEEVE_ADMIN_CERTIFICATE_PASSWORD" settings.Admin.Listener

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
        let publicUrl = settings.Authentication.Steam.PublicUrl.TrimEnd('/')
        let key = Environment.GetEnvironmentVariable SteamKeyVariable
        { Verify = fun flow fields token -> SteamOpenId.verify steamHttp.Value publicUrl flow fields token
          Profile = fun steamId token ->
            if String.IsNullOrWhiteSpace key then Task.FromResult { SteamId = steamId; PersonaName = ValueNone; Created = ValueNone }
            else SteamOpenId.profile steamHttp.Value key steamId token }

    let auth (settings: ApplicationConfig) (authentication: Agent<AuthMessage>) : AuthPorts =
        { Access = fun command timeout token ->
            authentication.TryAskAsync((fun reply -> AuthMessage.Access(command, reply)), timeout, token)
          Steam = steam settings }

    let private posted result =
        match result with
        | AgentPostResult.Posted -> true
        | AgentPostResult.Full | AgentPostResult.Closed | AgentPostResult.Canceled | AgentPostResult.Dropped -> false

    /// runtime is the game runtime now serving, none while it restarts: the panel
    /// then reports it unavailable, and stored changes apply at the next sign-in.
    let admin (service: Agent<AdminMessage>) (authentication: Agent<AuthMessage>) (runtime: unit -> Agent<ServerRuntimeMessage> option)
              (describer: Agent<DescribeRequest>) configuration : AdminPorts =
        let ask message timeout token =
            match runtime () with
            | Some agent -> agent.TryAskAsync(message, timeout, token)
            | None -> Task.FromResult AgentAskResult.Closed
        let tell message = runtime () |> Option.exists (fun agent -> agent.TryPost message |> posted)
        { Admin = fun command timeout token -> service.TryAskAsync((fun reply -> AdminMessage.Access(command, reply)), timeout, token)
          Account = fun command timeout token -> authentication.TryAskAsync((fun reply -> AuthMessage.Access(command, reply)), timeout, token)
          Snapshot = ask ServerRuntimeMessage.Read
          Sessions = ask ServerRuntimeMessage.ListSessions
          Describe = fun timeout row ->
            match row.Session with
            | Some session -> SessionDescriber.describe describer timeout session
            | None -> Task.FromResult None
          Announce = fun announcement -> tell (ServerRuntimeMessage.Announce announcement)
          ApplyRole = fun playerId role -> tell (ServerRuntimeMessage.SetPlayerRole(playerId, role))
          ApplyProfile = fun profile -> tell (ServerRuntimeMessage.RenamePlayer profile)
          Configuration = configuration }
