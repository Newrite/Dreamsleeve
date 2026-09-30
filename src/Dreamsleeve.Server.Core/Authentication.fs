namespace Dreamsleeve.Server.Core

open System
open System.Threading.Tasks
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

[<RequireQualifiedAccess>]
type SessionAuthenticationError =
    | InvalidTicket
    | Unavailable

/// The stored profile and role behind a consumed ticket.
type AuthenticatedPlayer = {
    Profile: PlayerData
    Role: PlayerRole
}

type SessionAuthenticationReply = {
    OperationId: Guid
    Result: Result<AuthenticatedPlayer, SessionAuthenticationError>
}

/// An opaque, short-lived credential; never include it in diagnostics.
type SessionAuthenticationRequest = {
    OperationId: Guid
    Ticket: string
    ReplyTo: ReliableAgentRef<SessionAuthenticationReply>
}

/// The runtime observes this dependency but does not own the account service.
type SessionAuthenticator = {
    Requests: ReliableAgentRef<SessionAuthenticationRequest>
    Completion: Task
}
