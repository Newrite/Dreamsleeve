// Unnamed framework enum values are handled explicitly.
#nowarn "104"

namespace Dreamsleeve.Server.Infrastructure

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Identity
open Microsoft.Extensions.Logging
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain

type AccountServiceOptions = {
    MailboxCapacity: int
    MaxConcurrentOperations: int
    MaxTickets: int
    TicketLifetimeSeconds: int
    PasswordIterations: int
    SavedLoginDays: int
    MaxSavedLogins: int
    ResetLifetimeMinutes: int
}

[<RequireQualifiedAccess>]
type AccountAccessError =
    | InvalidCredentials
    | UsernameTaken
    | Busy
    | Unavailable

type SessionGrant = {
    Profile: PlayerData
    SessionTicket: string
    ExpiresInSeconds: int
    RememberToken: string
}

[<RequireQualifiedAccess>]
type AccountAccessCommand =
    | Register of Username * DisplayName * password: string
    | Login of Username * password: string
    | RememberLogin of Username * password: string
    | Resume of token: string
    | Logout of token: string
    | ResetPassword of code: string * password: string
    // Trusted server callers only. Never map these directly to public HTTP input.
    | CreatePasswordReset of Username
    | RevokeAccount of Username
    /// The caller has validated and moderated the new name.
    | RenamePlayer of PlayerId * DisplayName

[<RequireQualifiedAccess>]
type AccountAccessResult =
    | Registered of PlayerData
    | SignedIn of SessionGrant
    | Completed
    | PasswordResetCreated of code: string
    | Renamed of PlayerData

[<RequireQualifiedAccess>]
type AccountWorkResult =
    | Registered of PlayerData
    | Verified of AuthenticatedPlayer * rememberToken: string
    | LoggedOut of tokenHash: string
    | Revoked of PlayerData * resetCode: string
    | Renamed of PlayerData

type AccountWorkReply = {
    OperationId: Guid
    Result: Result<AccountWorkResult, AccountAccessError>
}

type AccountWorkRequest = {
    OperationId: Guid
    Command: AccountAccessCommand
    ReplyTo: ReliableAgentRef<AccountWorkReply>
}

[<RequireQualifiedAccess>]
type AuthMessage =
    | Start
    | Access of AccountAccessCommand * ReplyChannel<Result<AccountAccessResult, AccountAccessError>>
    | Finished of AccountWorkReply
    | ConsumeTicket of SessionAuthenticationRequest
    | WorkersStopped of Result<unit, exn>
    | SetRevocationTarget of ReliableAgentRef<PlayerId>
    | RevocationFailed of AgentSendFailure
    | Stop

/// Account I/O and password work run in bounded library workers. Only this
/// agent owns ticket issuance/consumption and request orchestration.
[<RequireQualifiedAccess>]
module AuthService =
    type private Ticket = { Player: AuthenticatedPlayer; CreatedAt: int64; RememberKey: string }

    type private State = {
        Tickets: Dictionary<string, Ticket>
        Pending: Dictionary<Guid, ReplyChannel<Result<AccountAccessResult, AccountAccessError>>>
        Workers: Agent<AccountWorkRequest>
        Outbox: AgentOutbox<AccountWorkRequest>
        mutable Exclusive: bool
        mutable Revocations: AgentOutbox<PlayerId> option
        mutable Stopping: bool
        mutable WorkersStopped: bool
    }

    let defaults = {
        MailboxCapacity = 64; MaxConcurrentOperations = 4; MaxTickets = 4096
        TicketLifetimeSeconds = 60; PasswordIterations = 210000
        SavedLoginDays = 30; MaxSavedLogins = 8; ResetLifetimeMinutes = 15
    }

    let validate options = [
        if options.SavedLoginDays < 1 || options.SavedLoginDays > 365 then "Saved login lifetime must be 1..365 days."
        if options.MaxSavedLogins < 1 || options.MaxSavedLogins > 32 then "Saved logins per account must be 1..32."
        if options.ResetLifetimeMinutes < 1 || options.ResetLifetimeMinutes > 60 then "Reset lifetime must be 1..60 minutes."
        if options.MailboxCapacity < 1 || options.MailboxCapacity > 65536 then "Account mailbox capacity must be 1..65536."
        if options.MaxConcurrentOperations < 1 || options.MaxConcurrentOperations > 64 then "Account workers must be 1..64."
        if options.MaxTickets < 1 || options.MaxTickets > 100000 then "Outstanding ticket capacity must be 1..100000."
        if options.TicketLifetimeSeconds < 1 || options.TicketLifetimeSeconds > 300 then "Session ticket lifetime must be 1..300 seconds."
        if options.PasswordIterations < 210000 || options.PasswordIterations > 2000000 then "Password iterations must be 210000..2000000."
    ]

    let validPassword password = Secrets.validPassword password

    let private hasher options = Secrets.hasher options.PasswordIterations

    let private storageError (logger: ILogger) = function
        | AccountStoreError.InvalidCredential -> AccountAccessError.InvalidCredentials
        | AccountStoreError.UsernameTaken -> AccountAccessError.UsernameTaken
        | AccountStoreError.Canceled -> AccountAccessError.Unavailable
        | AccountStoreError.Failed error ->
            logger.LogError(error, "Account storage operation failed")
            AccountAccessError.Unavailable

    let private identity (account: StoredAccount) : StoredIdentity =
        { AccountId = account.AccountId; Profile = account.Profile; Role = account.Role }

    let private player (account: StoredIdentity) : AuthenticatedPlayer =
        { Profile = account.Profile; Role = account.Role }

    let private verify options database dummyHash logger token username password =
        match SqliteAccountStore.find database username token with
        | Error error -> Error (storageError logger error)
        | Ok found ->
            let passwordHash = found |> Option.map _.PasswordHash |> Option.defaultValue dummyHash
            let verified = (hasher options).VerifyHashedPassword(null, passwordHash, password)
            match found, verified with
            | Some account, PasswordVerificationResult.Success -> Ok (identity account)
            | Some account, PasswordVerificationResult.SuccessRehashNeeded ->
                let replacement = (hasher options).HashPassword(null, password)
                SqliteAccountStore.rehash database username account.PasswordHash replacement token
                |> Result.map (fun () -> identity account)
                |> Result.mapError (storageError logger)
            | None, _ | Some _, PasswordVerificationResult.Failed -> Error AccountAccessError.InvalidCredentials
            | Some _, unknown when not (Enum.IsDefined unknown) -> Error AccountAccessError.Unavailable

    let private newToken () = Secrets.newToken ()

    let private ticketKey (ticket: string) = Secrets.hash ticket

    let validToken value = Secrets.validToken value

    let private savedLogin options database now logger token (account: StoredIdentity) =
        let secret = newToken ()
        let expires = now + int64 options.SavedLoginDays * 86400L
        SqliteAccountStore.remember database account.AccountId (ticketKey secret) now expires options.MaxSavedLogins token
        |> Result.map (fun () -> AccountWorkResult.Verified(player account, secret))
        |> Result.mapError (storageError logger)

    let private administer options database now logger token username reset =
        match SqliteAccountStore.findAccount database username token with
        | Error error -> Error (storageError logger error)
        | Ok None -> Error AccountAccessError.InvalidCredentials
        | Ok (Some account) ->
            let code = if reset then newToken () else ""
            let result =
                if reset then SqliteAccountStore.createReset database account.AccountId (ticketKey code) (now + int64 options.ResetLifetimeMinutes * 60L) token
                else SqliteAccountStore.revoke database account.AccountId token
            result |> Result.map (fun () -> AccountWorkResult.Revoked(account.Profile, code)) |> Result.mapError (storageError logger)

    let private execute options database dummyHash (clock: TimeProvider) (logger: ILogger) (token: CancellationToken) (request: AccountWorkRequest) = task {
        // createAsyncHandler invokes even this synchronous prefix in tracked work.
        let result =
            try
                token.ThrowIfCancellationRequested()
                let now = clock.GetUtcNow().ToUnixTimeSeconds()
                match request.Command with
                | AccountAccessCommand.Register(username, displayName, password) ->
                    if not (validPassword password) then Error AccountAccessError.InvalidCredentials
                    else
                        let passwordHash = (hasher options).HashPassword(null, password)
                        SqliteAccountStore.create database username displayName passwordHash token
                        |> Result.map AccountWorkResult.Registered
                        |> Result.mapError (storageError logger)
                | AccountAccessCommand.Login(username, password) ->
                    if not (validPassword password) then Error AccountAccessError.InvalidCredentials
                    else verify options database dummyHash logger token username password
                         |> Result.map (fun account -> AccountWorkResult.Verified(player account, ""))
                | AccountAccessCommand.RememberLogin(username, password) ->
                    if not (validPassword password) then Error AccountAccessError.InvalidCredentials
                    else verify options database dummyHash logger token username password
                         |> Result.bind (savedLogin options database now logger token)
                | AccountAccessCommand.Resume secret ->
                    if not (validToken secret) then Error AccountAccessError.InvalidCredentials
                    else SqliteAccountStore.resume database (ticketKey secret) now token
                         |> Result.map (fun account -> AccountWorkResult.Verified(player account, secret))
                         |> Result.mapError (storageError logger)
                | AccountAccessCommand.Logout secret ->
                    if not (validToken secret) then Error AccountAccessError.InvalidCredentials
                    else SqliteAccountStore.logout database (ticketKey secret) token
                         |> Result.map (fun () -> AccountWorkResult.LoggedOut(ticketKey secret))
                         |> Result.mapError (storageError logger)
                | AccountAccessCommand.ResetPassword(code, password) ->
                    if not (validToken code && validPassword password) then Error AccountAccessError.InvalidCredentials
                    else
                        let hash = (hasher options).HashPassword(null, password)
                        SqliteAccountStore.resetPassword database (ticketKey code) now hash token
                        |> Result.map (fun profile -> AccountWorkResult.Revoked(profile, ""))
                        |> Result.mapError (storageError logger)
                | AccountAccessCommand.CreatePasswordReset username -> administer options database now logger token username true
                | AccountAccessCommand.RevokeAccount username -> administer options database now logger token username false
                | AccountAccessCommand.RenamePlayer(playerId, displayName) ->
                    match SqliteAccountStore.rename database playerId displayName token with
                    | Ok (Some account) -> Ok (AccountWorkResult.Renamed account.Profile)
                    | Ok None -> Error AccountAccessError.InvalidCredentials
                    | Error error -> Error (storageError logger error)
            with
            | :? OperationCanceledException -> Error AccountAccessError.Unavailable
            | error ->
                logger.LogError(error, "Account operation failed")
                Error AccountAccessError.Unavailable
        return { OperationId = request.OperationId; Result = result }
    }

    let private expire options (clock: TimeProvider) state =
        let now = clock.GetTimestamp()
        let expired =
            state.Tickets
            |> Seq.filter (fun entry -> clock.GetElapsedTime(entry.Value.CreatedAt, now).TotalSeconds >= float options.TicketLifetimeSeconds)
            |> Seq.map _.Key
            |> Seq.toArray
        for key in expired do state.Tickets.Remove key |> ignore

    let private issue options (clock: TimeProvider) state (player: AuthenticatedPlayer) (rememberToken: string) =
        expire options clock state
        if state.Tickets.Count >= options.MaxTickets then Error AccountAccessError.Busy
        else
            let ticket = newToken ()
            state.Tickets.Add(ticketKey ticket, { Player = player; CreatedAt = clock.GetTimestamp(); RememberKey = if rememberToken.Length = 0 then "" else ticketKey rememberToken })
            Ok (AccountAccessResult.SignedIn { Profile = player.Profile; SessionTicket = ticket; ExpiresInSeconds = options.TicketLifetimeSeconds; RememberToken = rememberToken })

    let private consume options clock state (request: SessionAuthenticationRequest) : SessionAuthenticationReply =
        expire options clock state
        let result =
            if state.Stopping || state.Exclusive then Error SessionAuthenticationError.Unavailable
            elif isNull request.Ticket || request.Ticket.Length <> 43 then Error SessionAuthenticationError.InvalidTicket
            else
                match state.Tickets.TryGetValue(ticketKey request.Ticket) with
                | false, _ -> Error SessionAuthenticationError.InvalidTicket
                | true, ticket ->
                    state.Tickets.Remove(ticketKey request.Ticket) |> ignore
                    Ok ticket.Player
        { OperationId = request.OperationId; Result = result }

    let private completeIfStopped state (context: AgentContext<AuthMessage>) =
        if state.Stopping && state.Pending.Count = 0 then
            if state.WorkersStopped then context.Complete() |> ignore
            else state.Workers.Complete() |> ignore

    let private exclusive = function
        | AccountAccessCommand.ResetPassword _ | AccountAccessCommand.CreatePasswordReset _
        | AccountAccessCommand.RevokeAccount _ | AccountAccessCommand.Logout _ | AccountAccessCommand.RenamePlayer _ -> true
        | AccountAccessCommand.Register _ | AccountAccessCommand.Login _ | AccountAccessCommand.RememberLogin _
        | AccountAccessCommand.Resume _ -> false

    let private access options state (context: AgentContext<AuthMessage>) command (reply: ReplyChannel<Result<AccountAccessResult, AccountAccessError>>) =
        if state.Stopping then reply.Reply(Error AccountAccessError.Unavailable)
        elif state.Exclusive || (exclusive command && state.Pending.Count <> 0) || state.Pending.Count >= options.MaxConcurrentOperations then reply.Reply(Error AccountAccessError.Busy)
        else
            let operationId = Guid.NewGuid()
            let request = {
                OperationId = operationId; Command = command
                ReplyTo = context.Ref.TryReliable().Value.Map AuthMessage.Finished
            }
            state.Exclusive <- exclusive command
            state.Pending.Add(operationId, reply)
            if not (state.Outbox.TrySend(context, request)) then
                state.Exclusive <- false
                state.Pending.Remove operationId |> ignore
                reply.Reply(Error AccountAccessError.Busy)

    let private finished options clock (logger: ILogger) state (context: AgentContext<AuthMessage>) (completion: AccountWorkReply) =
        match state.Pending.TryGetValue completion.OperationId with
        | false, _ -> ()
        | true, reply ->
            state.Exclusive <- false
            state.Pending.Remove completion.OperationId |> ignore
            let result =
                match completion.Result with
                | Ok (AccountWorkResult.Registered profile) ->
                    logger.LogInformation("Account registered for player {PlayerId}", PlayerId.value profile.PlayerId)
                    Ok (AccountAccessResult.Registered profile)
                | Ok (AccountWorkResult.Verified _) when state.Stopping || reply.IsCompleted -> Error AccountAccessError.Unavailable
                | Ok (AccountWorkResult.Verified(player, rememberToken)) ->
                    logger.LogDebug("Account authenticated for player {PlayerId}", PlayerId.value player.Profile.PlayerId)
                    issue options clock state player rememberToken
                | Ok (AccountWorkResult.Renamed profile) ->
                    // Exclusive: no login was pending, so outstanding tickets are the only stale copies.
                    let keys = state.Tickets |> Seq.filter (fun entry -> entry.Value.Player.Profile.PlayerId = profile.PlayerId) |> Seq.map _.Key |> Seq.toArray
                    for key in keys do state.Tickets[key] <- { state.Tickets[key] with Player = { state.Tickets[key].Player with Profile = profile } }
                    logger.LogInformation("Display name changed for player {PlayerId}", PlayerId.value profile.PlayerId)
                    Ok (AccountAccessResult.Renamed profile)
                | Ok (AccountWorkResult.LoggedOut key) ->
                    // Logout is exclusive: no pending resume can issue a late ticket.
                    let keys = state.Tickets |> Seq.filter (fun entry -> entry.Value.RememberKey = key) |> Seq.map _.Key |> Seq.toArray
                    for key in keys do state.Tickets.Remove key |> ignore
                    Ok AccountAccessResult.Completed
                | Ok (AccountWorkResult.Revoked(profile, code)) ->
                    let keys = state.Tickets |> Seq.filter (fun entry -> entry.Value.Player.Profile.PlayerId = profile.PlayerId) |> Seq.map _.Key |> Seq.toArray
                    for key in keys do state.Tickets.Remove key |> ignore
                    logger.LogInformation("Account access revoked for player {PlayerId}", PlayerId.value profile.PlayerId)
                    let delivered =
                        match state.Revocations with
                        | None -> true // Service can run without a game runtime (tools/tests).
                        | Some outbox -> outbox.TrySend(context, profile.PlayerId, AuthMessage.RevocationFailed)
                    if not delivered then
                        context.Abort()
                        Error AccountAccessError.Unavailable
                    elif code.Length = 0 then Ok AccountAccessResult.Completed
                    else Ok (AccountAccessResult.PasswordResetCreated code)
                | Error error -> Error error
            reply.Reply result
            completeIfStopped state context

    let private handle options clock (logger: ILogger) state consumeRequest (context: AgentContext<AuthMessage>) message = task {
        match message with
        | AuthMessage.SetRevocationTarget target -> state.Revocations <- Some (AgentOutbox(options.MailboxCapacity, target))
        | AuthMessage.RevocationFailed failure ->
            logger.LogError("Session revocation delivery failed: {Failure}", failure)
            context.Abort()
        | AuthMessage.Start -> context.Own(state.Workers, AuthMessage.WorkersStopped)
        | AuthMessage.Access(command, reply) -> access options state context command reply
        | AuthMessage.Finished reply -> finished options clock logger state context reply
        | AuthMessage.ConsumeTicket request -> do! consumeRequest context request
        | AuthMessage.WorkersStopped outcome ->
            state.WorkersStopped <- true
            match outcome with
            | Error error ->
                logger.LogError(error, "Account workers terminated")
                context.Abort()
            | Ok () when not state.Stopping ->
                logger.LogError("Account workers stopped unexpectedly")
                context.Abort()
            | Ok () -> completeIfStopped state context
        | AuthMessage.Stop ->
            state.Stopping <- true
            state.Tickets.Clear()
            completeIfStopped state context
    }

    let private isControl = function
        | AuthMessage.Start | AuthMessage.Finished _ | AuthMessage.WorkersStopped _ | AuthMessage.Stop
        | AuthMessage.SetRevocationTarget _ | AuthMessage.RevocationFailed _ -> true
        | AuthMessage.Access _ | AuthMessage.ConsumeTicket _ -> false

    let start options database (logger: ILogger) (clock: TimeProvider) =
        match validate options with
        | errors when not errors.IsEmpty -> Error (String.concat " " errors)
        | _ ->
            let dummyHash = (hasher options).HashPassword(null, Convert.ToBase64String(RandomNumberGenerator.GetBytes 32))
            let workerOptions = { AgentOptions.create "account-storage" with Mailbox = AgentMailbox.boundedWait options.MaxConcurrentOperations }
            let work = AgentReplyDispatcher.createAsyncHandler options.MaxConcurrentOperations (fun (request: AccountWorkRequest) -> request.ReplyTo)
                           (execute options database dummyHash clock logger)
            let workers = Agent.Start(workerOptions, work)
            let state = {
                Tickets = Dictionary(); Pending = Dictionary(); Workers = workers
                Outbox = AgentOutbox(options.MaxConcurrentOperations, workers.Ref.TryReliable().Value)
                Exclusive = false; Revocations = None; Stopping = false; WorkersStopped = false
            }
            let consumeRequest = AgentReplyDispatcher.createHandler options.MailboxCapacity
                                     (fun (request: SessionAuthenticationRequest) -> request.ReplyTo) (consume options clock state)
            let settings = {
                AgentOptions.create "authentication" with
                    Mailbox = AgentMailbox.boundedWithControl options.MailboxCapacity (options.MaxConcurrentOperations + 2)
            }
            let agent = Agent.Start(settings, handle options clock logger state consumeRequest, isControl = isControl)
            agent.TryPost AuthMessage.Start |> ignore
            Ok agent

    let authenticator (agent: Agent<AuthMessage>) = {
        Requests = agent.Ref.TryReliable().Value.Map AuthMessage.ConsumeTicket
        Completion = agent.Completion
    }
