// Unnamed framework enum values are handled explicitly.
#nowarn "104"

namespace Dreamsleeve.Server.Infrastructure

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Threading
open Microsoft.AspNetCore.Identity
open Microsoft.Extensions.Logging
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

type AdminServiceOptions = {
    MailboxCapacity: int
    MaxConcurrentOperations: int
    /// Same hasher and cost as player passwords.
    PasswordIterations: int
    SessionHours: int
    CodeLifetimeMinutes: int
    /// Sign-in attempts per administrator name and minute.
    LoginAttemptsPerMinute: int
    /// Distinct names whose attempts are counted at once; more are refused as Busy.
    MaxTrackedLogins: int
}

[<RequireQualifiedAccess>]
type AdminServiceError =
    /// Wrong name, password, session, API token or one-time code; also an expired code.
    | InvalidCredentials
    | AlreadyConfigured
    | NotFound
    | RateLimited
    | Busy
    | Unavailable

[<RequireQualifiedAccess>]
type AdminCommand =
    /// Whether an administrator exists.
    | Status
    /// Console and startup only: a code for /setup while no administrator exists.
    | IssueSetupCode
    /// Console only: a code for /reset of this administrator.
    | IssueResetCode of Username
    | Setup of code: string * Username * password: string
    | ResetPassword of code: string * password: string
    | Login of Username * password: string
    | Logout of sessionToken: string
    | Authenticate of sessionToken: string
    | AuthenticateApi of apiToken: string
    | CreateApiToken of AdminAccount * ApiTokenLabel
    | ListApiTokens
    | RevokeApiToken of AdminAccount * tokenHash: string
    | SetRole of AdminAccount * PlayerId * PlayerRole
    /// Audit line for an action performed by another owner (rename, reset, revoke, announcement).
    | Record of AdminAccount * AuditRecord
    | RecentAudit of limit: int
    | SearchPlayers of query: string * page: int
    | FindPlayer of PlayerId
    /// The latest display name changes of a player, newest first.
    | NameHistory of PlayerId
    /// The sanctions in force on a player.
    | PlayerSanctions of PlayerId
    /// Every sanction in force, newest first.
    | ActiveSanctions

[<RequireQualifiedAccess>]
type AdminReply =
    | Configured of bool
    /// A one-time code or API token, shown once and never stored in clear.
    | Secret of string
    | SignedIn of AdminAccount * sessionToken: string * expiresAt: DateTimeOffset
    | Admin of AdminAccount
    | Completed
    | ApiTokens of ApiTokenInfo list
    | Player of PlayerRecord option
    | Players of PlayerPage
    | Audit of AuditEntry list
    | Names of NameChange list
    | Sanctions of Sanction list
    | ActiveSanctions of SanctionRecord list

[<RequireQualifiedAccess>]
type AdminWorkResult =
    | Reply of AdminReply
    | AdminsCounted of int64
    | AdminFound of AdminAccount

type AdminWorkReply = {
    OperationId: Guid
    Result: Result<AdminWorkResult, AdminServiceError>
}

type AdminWorkRequest = {
    OperationId: Guid
    Command: AdminCommand
    /// The purpose of the code already redeemed by the agent.
    Redeemed: AdminCodePurpose voption
    ReplyTo: ReliableAgentRef<AdminWorkReply>
}

[<RequireQualifiedAccess>]
type AdminMessage =
    | Start
    | Access of AdminCommand * ReplyChannel<Result<AdminReply, AdminServiceError>>
    | Finished of AdminWorkReply
    | WorkersStopped of Result<unit, exn>
    | Stop

/// The panel's owner of administrators, sessions, API tokens, roles, audit and
/// player lists. SQLite and password hashing run in bounded workers
/// (admin-storage); one-time codes and sign-in attempts live only in this agent.
[<RequireQualifiedAccess>]
module AdminService =
    type private Pending = {
        Command: AdminCommand
        Reply: ReplyChannel<Result<AdminReply, AdminServiceError>>
    }

    type private State = {
        Pending: Dictionary<Guid, Pending>
        Workers: ReliableAgent<AdminWorkRequest>
        Outbox: AgentOutbox<AdminWorkRequest>
        Attempts: Dictionary<string, struct (DateTimeOffset * int)>
        mutable Codes: AdminCodes
        mutable Exclusive: bool
        mutable Stopping: bool
        mutable WorkersStopped: bool
    }

    let defaults = {
        MailboxCapacity = 64
        MaxConcurrentOperations = 2
        PasswordIterations = Secrets.MinPasswordIterations

        SessionHours = 12
        CodeLifetimeMinutes = 15
        LoginAttemptsPerMinute = 10
        MaxTrackedLogins = 1024
    }

    let validate options = [
        if options.MailboxCapacity < 1 || options.MailboxCapacity > 65536 then "Admin mailbox capacity must be 1..65536."
        if options.MaxConcurrentOperations < 1 || options.MaxConcurrentOperations > 64 then "Admin workers must be 1..64."
        if options.PasswordIterations < Secrets.MinPasswordIterations || options.PasswordIterations > Secrets.MaxPasswordIterations then
            $"Admin.Service.PasswordIterations must be {Secrets.MinPasswordIterations}..{Secrets.MaxPasswordIterations}."
        if options.SessionHours < 1 || options.SessionHours > 720 then "Admin.Service.SessionHours must be 1..720."
        if options.CodeLifetimeMinutes < 1 || options.CodeLifetimeMinutes > 60 then "Admin.Service.CodeLifetimeMinutes must be 1..60."
        if options.LoginAttemptsPerMinute < 1 || options.LoginAttemptsPerMinute > 1000 then "Admin.Service.LoginAttemptsPerMinute must be 1..1000."
        if options.MaxTrackedLogins < 1 || options.MaxTrackedLogins > 100000 then "Tracked admin logins must be 1..100000."
    ]

    let private storageError (logger: ILogger) = function
        | AccountStoreError.InvalidCredential -> AdminServiceError.InvalidCredentials
        | AccountStoreError.UsernameTaken -> AdminServiceError.AlreadyConfigured
        | AccountStoreError.Canceled -> AdminServiceError.Unavailable
        | AccountStoreError.Failed error ->
            logger.LogError(error, "Admin storage operation failed")
            AdminServiceError.Unavailable

    let private session options now adminId =
        let secret = Secrets.newToken ()
        secret, PanelSession.create (Secrets.hash secret) adminId now (TimeSpan.FromHours(float options.SessionHours))

    let private signIn (logger: ILogger) (result: Result<AdminAccount option, AccountStoreError>) secret expiresAt missing =
        match result with
        | Ok (Some account) -> Ok (AdminWorkResult.Reply(AdminReply.SignedIn(account, secret, expiresAt)))
        | Ok None -> Error missing
        | Error error -> Error (storageError logger error)

    let private verify options database dummyHash logger token now username password =
        match SqliteAdminStore.findAdmin database username token with
        | Error error -> Error (storageError logger error)
        | Ok found ->
            let hasher = Secrets.hasher options.PasswordIterations
            let verified =
                let passwordHash = found |> Option.map _.PasswordHash |> Option.defaultValue dummyHash
                match found, hasher.VerifyHashedPassword(null, passwordHash, password) with
                | Some stored, PasswordVerificationResult.Success -> Ok stored.Account
                | Some stored, PasswordVerificationResult.SuccessRehashNeeded ->
                    SqliteAdminStore.rehashAdmin database stored.Account.Id stored.PasswordHash (hasher.HashPassword(null, password)) token
                    |> Result.map (fun () -> stored.Account)
                    |> Result.mapError (storageError logger)
                | None, _ | Some _, PasswordVerificationResult.Failed -> Error AdminServiceError.InvalidCredentials
                | Some _, _ -> Error AdminServiceError.Unavailable

            verified
            |> Result.bind (fun account ->
                let secret, created = session options now account.Id
                SqliteAdminStore.createSession database created token
                |> Result.map (fun () -> AdminWorkResult.Reply(AdminReply.SignedIn(account, secret, created.ExpiresAt)))
                |> Result.mapError (storageError logger))

    let private execute options database dummyHash (clock: TimeProvider) (logger: ILogger) (token: CancellationToken) (request: AdminWorkRequest) = task {
        let result =
            try
                token.ThrowIfCancellationRequested()
                let now = clock.GetUtcNow()
                let stored mapping result = result |> Result.map mapping |> Result.mapError (storageError logger)
                let reply = AdminWorkResult.Reply
                match request.Command, request.Redeemed with
                | AdminCommand.Status, _ | AdminCommand.IssueSetupCode, _ ->
                    SqliteAdminStore.countAdmins database token |> stored AdminWorkResult.AdminsCounted
                | AdminCommand.IssueResetCode username, _ ->
                    match SqliteAdminStore.findAdmin database username token with
                    | Ok (Some admin) -> Ok (AdminWorkResult.AdminFound admin.Account)
                    | Ok None -> Error AdminServiceError.NotFound
                    | Error error -> Error (storageError logger error)
                | AdminCommand.Setup(_, username, password), ValueSome AdminCodePurpose.Setup ->
                    let passwordHash = (Secrets.hasher options.PasswordIterations).HashPassword(null, password)
                    let secret = Secrets.newToken ()
                    let lifetime = TimeSpan.FromHours(float options.SessionHours)
                    let created id = PanelSession.create (Secrets.hash secret) id now lifetime
                    let result = SqliteAdminStore.createFirstAdmin database username passwordHash created now token
                    signIn logger result secret (now + lifetime) AdminServiceError.AlreadyConfigured
                | AdminCommand.ResetPassword(_, password), ValueSome (AdminCodePurpose.ResetPassword adminId) ->
                    let passwordHash = (Secrets.hasher options.PasswordIterations).HashPassword(null, password)
                    let secret, created = session options now adminId
                    signIn logger (SqliteAdminStore.setPassword database adminId passwordHash created now token) secret created.ExpiresAt AdminServiceError.NotFound
                | AdminCommand.Setup _, _ | AdminCommand.ResetPassword _, _ -> Error AdminServiceError.InvalidCredentials
                | AdminCommand.Login(username, password), _ -> verify options database dummyHash logger token now username password
                | AdminCommand.Logout secret, _ ->
                    SqliteAdminStore.deleteSession database (Secrets.hash secret) token |> stored (fun () -> reply AdminReply.Completed)
                | AdminCommand.Authenticate secret, _ ->
                    match SqliteAdminStore.findSession database (Secrets.hash secret) now token with
                    | Ok (Some admin) -> Ok (reply (AdminReply.Admin admin))
                    | Ok None -> Error AdminServiceError.InvalidCredentials
                    | Error error -> Error (storageError logger error)
                | AdminCommand.AuthenticateApi secret, _ ->
                    match SqliteAdminStore.findApiToken database (Secrets.hash secret) token with
                    | Ok (Some admin) -> Ok (reply (AdminReply.Admin admin))
                    | Ok None -> Error AdminServiceError.InvalidCredentials
                    | Error error -> Error (storageError logger error)
                | AdminCommand.CreateApiToken(admin, label), _ ->
                    let secret = Secrets.newToken ()
                    SqliteAdminStore.createApiToken database admin (Secrets.hash secret) label now token
                    |> stored (fun () -> reply (AdminReply.Secret secret))
                | AdminCommand.ListApiTokens, _ -> SqliteAdminStore.listApiTokens database token |> stored (AdminReply.ApiTokens >> reply)
                | AdminCommand.RevokeApiToken(admin, hash), _ ->
                    match SqliteAdminStore.revokeApiToken database admin hash now token with
                    | Ok true -> Ok (reply AdminReply.Completed)
                    | Ok false -> Error AdminServiceError.NotFound
                    | Error error -> Error (storageError logger error)
                | AdminCommand.SetRole(admin, playerId, role), _ ->
                    match SqliteAdminStore.setRole database admin playerId role now token with
                    | Ok (Some record) -> Ok (reply (AdminReply.Player(Some record)))
                    | Ok None -> Error AdminServiceError.NotFound
                    | Error error -> Error (storageError logger error)
                | AdminCommand.Record(admin, entry), _ ->
                    SqliteAdminStore.record database admin entry now token |> stored (fun () -> reply AdminReply.Completed)
                | AdminCommand.RecentAudit limit, _ -> SqliteAdminStore.recentAudit database limit token |> stored (AdminReply.Audit >> reply)
                | AdminCommand.SearchPlayers(query, page), _ ->
                    SqliteAdminStore.searchPlayers database query page token |> stored (AdminReply.Players >> reply)
                | AdminCommand.FindPlayer playerId, _ ->
                    SqliteAdminStore.findPlayer database playerId token |> stored (AdminReply.Player >> reply)
                | AdminCommand.NameHistory playerId, _ ->
                    SqliteAdminStore.nameHistory database playerId 50 token |> stored (AdminReply.Names >> reply)
                | AdminCommand.PlayerSanctions playerId, _ ->
                    SqliteSanctionStore.active database playerId now token |> stored (AdminReply.Sanctions >> reply)
                | AdminCommand.ActiveSanctions, _ ->
                    SqliteSanctionStore.listActive database now token |> stored (AdminReply.ActiveSanctions >> reply)
            with
            | :? OperationCanceledException when token.IsCancellationRequested -> Error AdminServiceError.Unavailable
            // Fresh request supervision: per-unit contexts/transactions have
            // disposed before this boundary; actor state changes only on completion.
            // The failed work is not retried and its original cause is logged.
            | error ->
                logger.LogError(error, "Admin operation failed")
                Error AdminServiceError.Unavailable
        return {
            OperationId = request.OperationId
            Result = result
        }
    }

    let private completeIfStopped state (context: ReliableAgentContext<AdminMessage>) =
        if state.Stopping && state.Pending.Count = 0 then
            if state.WorkersStopped then context.Complete() |> ignore
            else state.Workers.Complete() |> ignore

    // A new password or the first administrator must not race a sign-in that
    // verified the old state: they run alone, like account revocation.
    let private exclusive = function
        | AdminCommand.Setup _ | AdminCommand.ResetPassword _ -> true
        | AdminCommand.Status | AdminCommand.IssueSetupCode | AdminCommand.IssueResetCode _ | AdminCommand.Login _
        | AdminCommand.Logout _ | AdminCommand.Authenticate _ | AdminCommand.AuthenticateApi _ | AdminCommand.CreateApiToken _
        | AdminCommand.ListApiTokens | AdminCommand.RevokeApiToken _ | AdminCommand.SetRole _ | AdminCommand.Record _
        | AdminCommand.RecentAudit _ | AdminCommand.SearchPlayers _ | AdminCommand.FindPlayer _ | AdminCommand.NameHistory _
        | AdminCommand.PlayerSanctions _ | AdminCommand.ActiveSanctions -> false

    // Fixed one-minute window per canonical name, counted when an attempt is admitted.
    let private admitLogin options state (now: DateTimeOffset) (username: Username) =
        let key = Username.value username
        let window = TimeSpan.FromMinutes 1.
        if state.Attempts.Count >= options.MaxTrackedLogins && not (state.Attempts.ContainsKey key) then
            let expired =
                state.Attempts
                |> Seq.filter (fun entry ->
                    let struct (start, _) = entry.Value
                    now - start >= window)
                |> Seq.map _.Key
                |> Seq.toArray
            for name in expired do state.Attempts.Remove name |> ignore
        match state.Attempts.TryGetValue key with
        | true, struct (start, count) when now - start < window ->
            if count >= options.LoginAttemptsPerMinute then Error AdminServiceError.RateLimited
            else
                state.Attempts[key] <- struct (start, count + 1)
                Ok ()
        | true, _ | false, _ ->
            if state.Attempts.Count >= options.MaxTrackedLogins && not (state.Attempts.ContainsKey key) then Error AdminServiceError.Busy
            else
                state.Attempts[key] <- struct (now, 1)
                Ok ()

    let private codeLifetime options = TimeSpan.FromMinutes(float options.CodeLifetimeMinutes)

    // Codes are redeemed here, before any work: the first attempt spends them.
    let private prepare options (clock: TimeProvider) state command =
        let now = clock.GetUtcNow()
        let redeem expected code =
            if not (Secrets.validToken code) then Error AdminServiceError.InvalidCredentials
            else
                let result, remaining = AdminCodes.redeem expected (Secrets.hash code) now state.Codes
                state.Codes <- remaining
                result |> Result.map ValueSome |> Result.mapError (fun _ -> AdminServiceError.InvalidCredentials)
        match command with
        | AdminCommand.Setup(code, _, password) ->
            if not (Secrets.validPassword password) then Error AdminServiceError.InvalidCredentials
            else
                redeem
                    (function
                     | AdminCodePurpose.Setup -> true
                     | AdminCodePurpose.ResetPassword _ -> false)
                    code
        | AdminCommand.ResetPassword(code, password) ->
            if not (Secrets.validPassword password) then Error AdminServiceError.InvalidCredentials
            else
                redeem
                    (function
                     | AdminCodePurpose.ResetPassword _ -> true
                     | AdminCodePurpose.Setup -> false)
                    code
        | AdminCommand.Login(username, password) ->
            if not (Secrets.validPassword password) then Error AdminServiceError.InvalidCredentials
            else admitLogin options state now username |> Result.map (fun () -> ValueNone)
        | AdminCommand.Logout secret | AdminCommand.Authenticate secret | AdminCommand.AuthenticateApi secret ->
            if Secrets.validToken secret then Ok ValueNone else Error AdminServiceError.InvalidCredentials
        | AdminCommand.RevokeApiToken(_, hash) ->
            if not (isNull hash) && hash.Length = 64 && hash |> Seq.forall Uri.IsHexDigit then Ok ValueNone else Error AdminServiceError.NotFound
        | AdminCommand.Status | AdminCommand.IssueSetupCode | AdminCommand.IssueResetCode _ | AdminCommand.CreateApiToken _
        | AdminCommand.ListApiTokens | AdminCommand.SetRole _ | AdminCommand.Record _ | AdminCommand.RecentAudit _
        | AdminCommand.SearchPlayers _ | AdminCommand.FindPlayer _ | AdminCommand.NameHistory _
        | AdminCommand.PlayerSanctions _ | AdminCommand.ActiveSanctions -> Ok ValueNone

    let private access options clock state (context: ReliableAgentContext<AdminMessage>) command (reply: ReplyChannel<Result<AdminReply, AdminServiceError>>) =
        if state.Stopping then reply.Reply(Error AdminServiceError.Unavailable)
        elif state.Exclusive || (exclusive command && state.Pending.Count <> 0) || state.Pending.Count >= options.MaxConcurrentOperations then
            reply.Reply(Error AdminServiceError.Busy)
        else
            match prepare options clock state command with
            | Error error -> reply.Reply(Error error)
            | Ok redeemed ->
                let operationId = Guid.NewGuid()
                let request = {
                    OperationId = operationId
                    Command = command
                    Redeemed = redeemed
                    ReplyTo = context.Ref.Map AdminMessage.Finished
                }
                state.Exclusive <- exclusive command
                state.Pending.Add(
                    operationId,
                    {
                        Command = command
                        Reply = reply
                    })
                if not (state.Outbox.TrySend(context, request)) then
                    state.Exclusive <- false
                    state.Pending.Remove operationId |> ignore
                    reply.Reply(Error AdminServiceError.Busy)

    // The log names the administrator and the action; secrets never reach it.
    let private logAction (logger: ILogger) (admin: AdminAccount) action (target: string) =
        logger.LogInformation("Admin {Admin}: {Action} {Target}", Username.value admin.Username, AdminAction.key action, target)

    let private logCompletion (logger: ILogger) command reply =
        match command, reply with
        | AdminCommand.Setup _, AdminReply.SignedIn(admin, _, _) ->
            logAction logger admin AdminAction.CreatedAdmin (AuditTarget.key (AuditTarget.Admin admin.Id))
        | AdminCommand.ResetPassword _, AdminReply.SignedIn(admin, _, _) ->
            logAction logger admin AdminAction.ResetAdminPassword (AuditTarget.key (AuditTarget.Admin admin.Id))
        | AdminCommand.Login _, AdminReply.SignedIn(admin, _, _) ->
            logger.LogInformation("Admin {Admin} signed in", Username.value admin.Username)
        | AdminCommand.CreateApiToken(admin, label), _ ->
            logAction logger admin AdminAction.CreatedApiToken (ApiTokenLabel.value label)
        | AdminCommand.RevokeApiToken(admin, hash), _ ->
            logAction logger admin AdminAction.RevokedApiToken (AuditTarget.key (AuditTarget.ApiToken(hash.Substring(0, 8))))
        | AdminCommand.SetRole(admin, playerId, role), _ ->
            logAction logger admin AdminAction.SetRole $"{AuditTarget.key (AuditTarget.Player playerId)} {PlayerRole.key role}"
        | AdminCommand.Record(admin, entry), _ ->
            logAction logger admin entry.Action (AuditTarget.key entry.Target)
        | _ -> ()

    let private finished options (clock: TimeProvider) (logger: ILogger) state (context: ReliableAgentContext<AdminMessage>) (completion: AdminWorkReply) =
        match state.Pending.TryGetValue completion.OperationId with
        | false, _ -> ()
        | true, pending ->
            state.Exclusive <- false
            state.Pending.Remove completion.OperationId |> ignore

            let issue purpose =
                let code = Secrets.newToken ()
                state.Codes <- AdminCodes.issue purpose (Secrets.hash code) (clock.GetUtcNow()) (codeLifetime options) state.Codes
                Ok (AdminReply.Secret code)
            let result =
                match pending.Command, completion.Result with
                | AdminCommand.Status, Ok (AdminWorkResult.AdminsCounted count) -> Ok (AdminReply.Configured(count > 0L))
                | AdminCommand.IssueSetupCode, Ok (AdminWorkResult.AdminsCounted count) ->
                    if count > 0L then Error AdminServiceError.AlreadyConfigured else issue AdminCodePurpose.Setup
                | AdminCommand.IssueResetCode _, Ok (AdminWorkResult.AdminFound admin) ->
                    logger.LogInformation("Admin password reset code issued for {Admin}", Username.value admin.Username)
                    issue (AdminCodePurpose.ResetPassword admin.Id)
                | command, Ok (AdminWorkResult.Reply reply) ->
                    logCompletion logger command reply
                    Ok reply
                | _, Ok (AdminWorkResult.AdminsCounted _ | AdminWorkResult.AdminFound _) -> Error AdminServiceError.Unavailable
                | _, Error error -> Error error

            pending.Reply.Reply result
            completeIfStopped state context

    let private handle options clock (logger: ILogger) state (context: ReliableAgentContext<AdminMessage>) message = task {
        match message with
        | AdminMessage.Start -> context.Own(state.Workers, AdminMessage.WorkersStopped)
        | AdminMessage.Access(command, reply) -> access options clock state context command reply
        | AdminMessage.Finished reply -> finished options clock logger state context reply
        | AdminMessage.WorkersStopped outcome ->
            state.WorkersStopped <- true
            match outcome with
            | Error error ->
                logger.LogError(error, "Admin workers terminated")
                context.Abort()
            | Ok () when not state.Stopping ->
                logger.LogError("Admin workers stopped unexpectedly")
                context.Abort()
            | Ok () -> completeIfStopped state context
        | AdminMessage.Stop ->
            state.Stopping <- true
            state.Codes <- AdminCodes.empty
            completeIfStopped state context
    }

    let private isControl = function
        | AdminMessage.Start | AdminMessage.Finished _ | AdminMessage.WorkersStopped _ | AdminMessage.Stop -> true
        | AdminMessage.Access _ -> false

    /// Check both mailboxes and the outstanding operation budget before starting workers.
    let start options database (logger: ILogger) (clock: TimeProvider) =
        if int64 options.MaxConcurrentOperations + 2L > int64 Int32.MaxValue then
            Error (AgentStartError.CapacityOverflow(options.MaxConcurrentOperations, 2))
        else
            let workerOptions = {
                AgentOptions.create "admin-storage" with
                    Mailbox = AgentMailbox.boundedWait options.MaxConcurrentOperations
            }
            let settings = {
                AgentOptions.create "admin" with
                    Mailbox = AgentMailbox.boundedWithControl options.MailboxCapacity (options.MaxConcurrentOperations + 2)
            }
            match Agent<AdminWorkRequest>.TryCheckReliable workerOptions,
                  Agent<AdminMessage>.TryCheckReliable(settings, isControl = isControl),
                  AgentDeliveryCapacity.TryCreate options.MaxConcurrentOperations with
            | Error error, _, _ | _, Error error, _ | _, _, Error error -> Error error
            | Ok worker, Ok owner, Ok operations ->
                let dummyHash = (Secrets.hasher options.PasswordIterations).HashPassword(null, Convert.ToBase64String(RandomNumberGenerator.GetBytes 32))
                let work = AgentReplyDispatcher.createAsyncHandler operations (fun (request: AdminWorkRequest) -> request.ReplyTo)
                               (execute options database dummyHash clock logger)
                let workers = worker.Start work
                let state = {
                    Pending = Dictionary()
                    Workers = workers
                    Outbox = AgentOutbox.Create(operations, workers.Ref)

                    Attempts = Dictionary()
                    Codes = AdminCodes.empty

                    Exclusive = false
                    Stopping = false
                    WorkersStopped = false
                }
                let agent = owner.Start(handle options clock logger state)
                agent.TryPost AdminMessage.Start |> ignore
                Ok agent
