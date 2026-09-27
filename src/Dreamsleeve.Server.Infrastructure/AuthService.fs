// Unnamed framework enum values are handled explicitly.
#nowarn "104"

namespace Dreamsleeve.Server.Infrastructure

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Identity
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain

type AccountServiceOptions = {
    MailboxCapacity: int
    MaxConcurrentOperations: int
    MaxTickets: int
    TicketLifetimeSeconds: int
    PasswordIterations: int
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
}

[<RequireQualifiedAccess>]
type AccountAccessCommand =
    | Register of Username * DisplayName * password: string
    | Login of Username * password: string

[<RequireQualifiedAccess>]
type AccountAccessResult =
    | Registered of PlayerData
    | SignedIn of SessionGrant

[<RequireQualifiedAccess>]
type AccountWorkResult =
    | Registered of PlayerData
    | Verified of PlayerData

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
    | Stop

/// Account I/O and password work run in bounded library workers. Only this
/// agent owns ticket issuance/consumption and request orchestration.
[<RequireQualifiedAccess>]
module AuthService =
    type private Ticket = { Profile: PlayerData; CreatedAt: int64 }

    type private State = {
        Tickets: Dictionary<string, Ticket>
        Pending: Dictionary<Guid, ReplyChannel<Result<AccountAccessResult, AccountAccessError>>>
        Workers: Agent<AccountWorkRequest>
        Outbox: AgentOutbox<AccountWorkRequest>
        mutable Stopping: bool
        mutable WorkersStopped: bool
    }

    let defaults = {
        MailboxCapacity = 64; MaxConcurrentOperations = 4; MaxTickets = 4096
        TicketLifetimeSeconds = 60; PasswordIterations = 210000
    }

    let validate options = [
        if options.MailboxCapacity < 1 || options.MailboxCapacity > 65536 then "Account mailbox capacity must be 1..65536."
        if options.MaxConcurrentOperations < 1 || options.MaxConcurrentOperations > 64 then "Account workers must be 1..64."
        if options.MaxTickets < 1 || options.MaxTickets > 100000 then "Outstanding ticket capacity must be 1..100000."
        if options.TicketLifetimeSeconds < 1 || options.TicketLifetimeSeconds > 300 then "Session ticket lifetime must be 1..300 seconds."
        if options.PasswordIterations < 210000 || options.PasswordIterations > 2000000 then "Password iterations must be 210000..2000000."
    ]

    let validPassword (password: string) =
        if isNull password || password.Length > 128 then false
        else
            let bytes = Encoding.UTF8.GetByteCount password
            bytes >= 12 && bytes <= 128

    let private hasher options =
        PasswordHasher<obj>(Options.Create(PasswordHasherOptions(IterationCount = options.PasswordIterations)))

    let private storageError (logger: ILogger) = function
        | AccountStoreError.UsernameTaken -> AccountAccessError.UsernameTaken
        | AccountStoreError.Canceled -> AccountAccessError.Unavailable
        | AccountStoreError.Failed error ->
            logger.LogError(error, "Account storage operation failed")
            AccountAccessError.Unavailable

    let private verify options database dummyHash logger token username password =
        match SqliteAccountStore.find database username token with
        | Error error -> Error (storageError logger error)
        | Ok found ->
            let passwordHash = found |> Option.map _.PasswordHash |> Option.defaultValue dummyHash
            let verified = (hasher options).VerifyHashedPassword(null, passwordHash, password)
            match found, verified with
            | Some account, PasswordVerificationResult.Success -> Ok (AccountWorkResult.Verified account.Profile)
            | Some account, PasswordVerificationResult.SuccessRehashNeeded ->
                let replacement = (hasher options).HashPassword(null, password)
                SqliteAccountStore.rehash database username account.PasswordHash replacement token
                |> Result.map (fun () -> AccountWorkResult.Verified account.Profile)
                |> Result.mapError (storageError logger)
            | None, _ | Some _, PasswordVerificationResult.Failed -> Error AccountAccessError.InvalidCredentials
            | Some _, unknown when not (Enum.IsDefined unknown) -> Error AccountAccessError.Unavailable

    let private execute options database dummyHash (logger: ILogger) (token: CancellationToken) (request: AccountWorkRequest) = task {
        // createAsyncHandler invokes even this synchronous prefix in tracked work.
        let result =
            try
                token.ThrowIfCancellationRequested()
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
            with
            | :? OperationCanceledException -> Error AccountAccessError.Unavailable
            | error ->
                logger.LogError(error, "Account operation failed")
                Error AccountAccessError.Unavailable
        return { OperationId = request.OperationId; Result = result }
    }

    let private ticketKey (ticket: string) =
        Encoding.ASCII.GetBytes ticket |> SHA256.HashData |> Convert.ToHexString

    let private expire options (clock: TimeProvider) state =
        let now = clock.GetTimestamp()
        let expired =
            state.Tickets
            |> Seq.filter (fun entry -> clock.GetElapsedTime(entry.Value.CreatedAt, now).TotalSeconds >= float options.TicketLifetimeSeconds)
            |> Seq.map _.Key
            |> Seq.toArray
        for key in expired do state.Tickets.Remove key |> ignore

    let private issue options (clock: TimeProvider) state profile =
        expire options clock state
        if state.Tickets.Count >= options.MaxTickets then Error AccountAccessError.Busy
        else
            let ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes 32).TrimEnd('=').Replace('+', '-').Replace('/', '_')
            state.Tickets.Add(ticketKey ticket, { Profile = profile; CreatedAt = clock.GetTimestamp() })
            Ok (AccountAccessResult.SignedIn { Profile = profile; SessionTicket = ticket; ExpiresInSeconds = options.TicketLifetimeSeconds })

    let private consume options clock state (request: SessionAuthenticationRequest) : SessionAuthenticationReply =
        expire options clock state
        let result =
            if state.Stopping then Error SessionAuthenticationError.Unavailable
            elif isNull request.Ticket || request.Ticket.Length <> 43 then Error SessionAuthenticationError.InvalidTicket
            else
                match state.Tickets.TryGetValue(ticketKey request.Ticket) with
                | false, _ -> Error SessionAuthenticationError.InvalidTicket
                | true, ticket ->
                    state.Tickets.Remove(ticketKey request.Ticket) |> ignore
                    Ok ticket.Profile
        { OperationId = request.OperationId; Result = result }

    let private completeIfStopped state (context: AgentContext<AuthMessage>) =
        if state.Stopping && state.Pending.Count = 0 then
            if state.WorkersStopped then context.Complete() |> ignore
            else state.Workers.Complete() |> ignore

    let private access options state (context: AgentContext<AuthMessage>) command (reply: ReplyChannel<Result<AccountAccessResult, AccountAccessError>>) =
        if state.Stopping then reply.Reply(Error AccountAccessError.Unavailable)
        elif state.Pending.Count >= options.MaxConcurrentOperations then reply.Reply(Error AccountAccessError.Busy)
        else
            let operationId = Guid.NewGuid()
            let request = {
                OperationId = operationId; Command = command
                ReplyTo = context.Ref.TryReliable().Value.Map AuthMessage.Finished
            }
            state.Pending.Add(operationId, reply)
            if not (state.Outbox.TrySend(context, request)) then
                state.Pending.Remove operationId |> ignore
                reply.Reply(Error AccountAccessError.Busy)

    let private finished options clock (logger: ILogger) state context (completion: AccountWorkReply) =
        match state.Pending.TryGetValue completion.OperationId with
        | false, _ -> ()
        | true, reply ->
            state.Pending.Remove completion.OperationId |> ignore
            let result =
                match completion.Result with
                | Ok (AccountWorkResult.Registered profile) ->
                    logger.LogInformation("Account registered for player {PlayerId}", PlayerId.value profile.PlayerId)
                    Ok (AccountAccessResult.Registered profile)
                | Ok (AccountWorkResult.Verified _) when state.Stopping || reply.IsCompleted -> Error AccountAccessError.Unavailable
                | Ok (AccountWorkResult.Verified profile) ->
                    logger.LogDebug("Account authenticated for player {PlayerId}", PlayerId.value profile.PlayerId)
                    issue options clock state profile
                | Error error -> Error error
            reply.Reply result
            completeIfStopped state context

    let private handle options clock logger state consumeRequest (context: AgentContext<AuthMessage>) message = task {
        match message with
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
        | AuthMessage.Start | AuthMessage.Finished _ | AuthMessage.WorkersStopped _ | AuthMessage.Stop -> true
        | AuthMessage.Access _ | AuthMessage.ConsumeTicket _ -> false

    let start options database (logger: ILogger) (clock: TimeProvider) =
        match validate options with
        | errors when not errors.IsEmpty -> Error (String.concat " " errors)
        | _ ->
            let dummyHash = (hasher options).HashPassword(null, Convert.ToBase64String(RandomNumberGenerator.GetBytes 32))
            let workerOptions = { AgentOptions.create "account-storage" with Mailbox = AgentMailbox.boundedWait options.MaxConcurrentOperations }
            let work = AgentReplyDispatcher.createAsyncHandler options.MaxConcurrentOperations (fun (request: AccountWorkRequest) -> request.ReplyTo)
                           (execute options database dummyHash logger)
            let workers = Agent.Start(workerOptions, work)
            let state = {
                Tickets = Dictionary(); Pending = Dictionary(); Workers = workers
                Outbox = AgentOutbox(options.MaxConcurrentOperations, workers.Ref.TryReliable().Value)
                Stopping = false; WorkersStopped = false
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
