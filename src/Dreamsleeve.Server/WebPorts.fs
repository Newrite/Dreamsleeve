namespace Dreamsleeve.Server

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

    let authRoutes (settings: ApplicationConfig) : AuthRouteSettings =
        let authentication = settings.Authentication
        { AllowRegistration = authentication.AllowRegistration
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
          Input = settings.Server.ChatInput; Moderation = moderation }

    let auth (authentication: Agent<AuthMessage>) : AuthPorts =
        { Access = fun command timeout token ->
            authentication.TryAskAsync((fun reply -> AuthMessage.Access(command, reply)), timeout, token) }

    let private posted result =
        match result with
        | AgentPostResult.Posted -> true
        | AgentPostResult.Full | AgentPostResult.Closed | AgentPostResult.Canceled | AgentPostResult.Dropped -> false

    let admin (service: Agent<AdminMessage>) (authentication: Agent<AuthMessage>) (runtime: Agent<ServerRuntimeMessage>)
              (describer: Agent<DescribeRequest>) configuration : AdminPorts =
        { Admin = fun command timeout token -> service.TryAskAsync((fun reply -> AdminMessage.Access(command, reply)), timeout, token)
          Account = fun command timeout token -> authentication.TryAskAsync((fun reply -> AuthMessage.Access(command, reply)), timeout, token)
          Snapshot = fun timeout token -> runtime.TryAskAsync(ServerRuntimeMessage.Read, timeout, token)
          Sessions = fun timeout token -> runtime.TryAskAsync(ServerRuntimeMessage.ListSessions, timeout, token)
          Describe = fun timeout row ->
            match row.Session with
            | Some session -> SessionDescriber.describe describer timeout session
            | None -> Task.FromResult None
          Announce = fun announcement -> runtime.TryPost(ServerRuntimeMessage.Announce announcement) |> posted
          ApplyRole = fun playerId role -> runtime.TryPost(ServerRuntimeMessage.SetPlayerRole(playerId, role)) |> posted
          Kick = fun playerId reason -> runtime.TryPost(ServerRuntimeMessage.KickPlayer(playerId, reason)) |> posted
          ApplyProfile = fun profile -> runtime.TryPost(ServerRuntimeMessage.RenamePlayer profile) |> posted
          Configuration = configuration }
