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
    /// Display name changes kept per player (display_name_changes); older ones are deleted.
    DisplayNameHistory: int
}

[<RequireQualifiedAccess>]
type AccountAccessError =
    | InvalidCredentials
    | UsernameTaken
    | Busy
    | Unavailable
    /// A player's own display name change sooner than the interval allows.
    | TooSoon of retryAfter: TimeSpan
    /// Sign-in and resume while a ban holds.
    | Banned of Sanction
    | SanctionRefused of SanctionError

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
    /// An administrator's change; the caller has validated and moderated the new name.
    | RenamePlayer of PlayerId * DisplayName * changedBy: AdminId
    /// The player's own change from the game session, limited by minInterval.
    | ChangeOwnDisplayName of PlayerId * DisplayName * minInterval: TimeSpan
    /// An administrator's or a moderator's order, validated by the caller.
    | Sanction of SanctionOrder
    /// Lifts the sanction of a kind in force on a player.
    | LiftSanction of PlayerId * SanctionKind * SanctionIssuer
    /// Ends the player's session now, when the issuer may act on them.
    | Kick of PlayerId * SanctionReason * SanctionIssuer
    /// Every sanction in force, newest first.
    | ListSanctions
    /// The audit line of content a moderator removed in the game.
    | RecordModeration of moderator: PlayerId * AuditRecord

[<RequireQualifiedAccess>]
type AccountAccessResult =
    | Registered of PlayerData
    | SignedIn of SessionGrant
    | Completed
    | PasswordResetCreated of code: string
    | Renamed of PlayerData
    | Sanctioned of Sanction
    | SanctionLifted of Sanction
    | Kicked
    | ActiveSanctions of Sanction list

[<RequireQualifiedAccess>]
type AccountWorkResult =
    | Registered of PlayerData
    | Verified of AuthenticatedPlayer * rememberToken: string
    | LoggedOut of tokenHash: string
    | Revoked of PlayerData * resetCode: string
    | Renamed of previous: DisplayName * PlayerData * changedBy: AdminId voption
    | Sanctioned of Sanction
    | SanctionLifted of Sanction
    | Kicked of PlayerId * SanctionReason
    | ActiveSanctions of Sanction list
    | Recorded

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
    /// Where live sessions learn of revocations and sanctions: the game runtime,
    /// again after each of its restarts.
    | SetChangeTarget of ReliableAgentRef<AccountChange>
    /// A change did not reach the target of that generation.
    | ChangeFailed of target: int * AgentSendFailure
    /// From a game session; settled by a DisplayNameChangeReply.
    | ChangeDisplayName of DisplayNameChangeRequest
    /// A moderator's request from a game session; settled by a ModerationReply.
    | Moderate of ModerationRequest
    | Stop

/// Account I/O and password work run in bounded library workers. Only this
/// agent owns ticket issuance/consumption and request orchestration.
[<RequireQualifiedAccess>]
module AuthService =
    type private Ticket = { Player: AuthenticatedPlayer; CreatedAt: int64; RememberKey: string }

    /// Who waits for an admitted operation: an HTTP or trusted caller, or a game session.
    [<RequireQualifiedAccess>]
    type private Requester =
        | Caller of ReplyChannel<Result<AccountAccessResult, AccountAccessError>>
        | Session of DisplayNameChangeRequest
        | Moderator of ModerationRequest

    type private Pending = { Command: AccountAccessCommand; Requester: Requester }

    type private State = {
        Tickets: Dictionary<string, Ticket>
        Pending: Dictionary<Guid, Pending>
        Workers: Agent<AccountWorkRequest>
        Outbox: AgentOutbox<AccountWorkRequest>
        mutable Exclusive: bool
        mutable Changes: AgentOutbox<AccountChange> option
        /// Counts SetChangeTarget, so a late failure of a replaced target is told apart.
        mutable ChangeTarget: int
        mutable Stopping: bool
        mutable WorkersStopped: bool
    }

    let defaults = {
        MailboxCapacity = 64; MaxConcurrentOperations = 4; MaxTickets = 4096
        TicketLifetimeSeconds = 60; PasswordIterations = 210000
        SavedLoginDays = 30; MaxSavedLogins = 8; ResetLifetimeMinutes = 15; DisplayNameHistory = 20
    }

    let validate options = [
        if options.SavedLoginDays < 1 || options.SavedLoginDays > 365 then "Saved login lifetime must be 1..365 days."
        if options.MaxSavedLogins < 1 || options.MaxSavedLogins > 32 then "Saved logins per account must be 1..32."
        if options.ResetLifetimeMinutes < 1 || options.ResetLifetimeMinutes > 60 then "Reset lifetime must be 1..60 minutes."
        if options.DisplayNameHistory < 1 || options.DisplayNameHistory > 1000 then "Display name history per player must be 1..1000."
        if options.MailboxCapacity < 1 || options.MailboxCapacity > 65536 then "Account mailbox capacity must be 1..65536."
        if options.MaxConcurrentOperations < 1 || options.MaxConcurrentOperations > 64 then "Account workers must be 1..64."
        if options.MaxTickets < 1 || options.MaxTickets > 100000 then "Outstanding ticket capacity must be 1..100000."
        if options.TicketLifetimeSeconds < 1 || options.TicketLifetimeSeconds > 300 then "Session ticket lifetime must be 1..300 seconds."
        if options.PasswordIterations < Secrets.MinPasswordIterations || options.PasswordIterations > Secrets.MaxPasswordIterations then
            $"Authentication.Service.PasswordIterations must be {Secrets.MinPasswordIterations}..{Secrets.MaxPasswordIterations}."
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

    // No ticket while a ban holds; the mute in force goes into the ticket.
    let private admit database (clock: TimeProvider) logger token (account: StoredIdentity) =
        let now = clock.GetUtcNow()
        match SqliteSanctionStore.active database account.Profile.PlayerId now token with
        | Error error -> Error (storageError logger error)
        | Ok sanctions ->
            match Sanction.find SanctionKind.Ban now sanctions with
            | ValueSome ban -> Error (AccountAccessError.Banned ban)
            | ValueNone -> Ok { Profile = account.Profile; Role = account.Role; Mute = Sanction.find SanctionKind.Mute now sanctions }

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

    let private savedLogin options database now logger token (account: StoredIdentity) (player: AuthenticatedPlayer) =
        let secret = newToken ()
        let expires = now + int64 options.SavedLoginDays * 86400L
        SqliteAccountStore.remember database account.AccountId (ticketKey secret) now expires options.MaxSavedLogins token
        |> Result.map (fun () -> AccountWorkResult.Verified(player, secret))
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

    let private rename options database logger token (clock: TimeProvider) playerId displayName changedBy interval =
        match SqliteAccountStore.rename database playerId displayName changedBy interval options.DisplayNameHistory (clock.GetUtcNow()) token with
        | Ok (RenameOutcome.Renamed(previous, stored)) -> Ok (AccountWorkResult.Renamed(previous, stored.Profile, changedBy))
        | Ok RenameOutcome.NotFound -> Error AccountAccessError.InvalidCredentials
        | Ok (RenameOutcome.TooSoon wait) -> Error (AccountAccessError.TooSoon wait)
        | Error error -> Error (storageError logger error)

    let private sanctioned logger result outcome =
        match outcome with
        | Ok (SanctionOutcome.Applied sanction) -> Ok (result sanction)
        | Ok (SanctionOutcome.Refused refused) -> Error (AccountAccessError.SanctionRefused refused)
        | Error error -> Error (storageError logger error)

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
                         |> Result.bind (admit database clock logger token)
                         |> Result.map (fun player -> AccountWorkResult.Verified(player, ""))
                | AccountAccessCommand.RememberLogin(username, password) ->
                    if not (validPassword password) then Error AccountAccessError.InvalidCredentials
                    else verify options database dummyHash logger token username password
                         |> Result.bind (fun account ->
                             admit database clock logger token account |> Result.bind (savedLogin options database now logger token account))
                | AccountAccessCommand.Resume secret ->
                    if not (validToken secret) then Error AccountAccessError.InvalidCredentials
                    else SqliteAccountStore.resume database (ticketKey secret) now token
                         |> Result.mapError (storageError logger)
                         |> Result.bind (admit database clock logger token)
                         |> Result.map (fun player -> AccountWorkResult.Verified(player, secret))
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
                | AccountAccessCommand.RenamePlayer(playerId, displayName, admin) ->
                    rename options database logger token clock playerId displayName (ValueSome admin) TimeSpan.Zero
                | AccountAccessCommand.ChangeOwnDisplayName(playerId, displayName, interval) ->
                    rename options database logger token clock playerId displayName ValueNone interval
                | AccountAccessCommand.Sanction order ->
                    SqliteSanctionStore.issue database order (clock.GetUtcNow()) token |> sanctioned logger AccountWorkResult.Sanctioned
                | AccountAccessCommand.LiftSanction(target, kind, issuer) ->
                    SqliteSanctionStore.lift database target kind issuer (clock.GetUtcNow()) token
                    |> sanctioned logger AccountWorkResult.SanctionLifted
                | AccountAccessCommand.Kick(target, reason, issuer) ->
                    match SqliteSanctionStore.kick database target reason issuer (clock.GetUtcNow()) token with
                    | Ok(Ok()) -> Ok (AccountWorkResult.Kicked(target, reason))
                    | Ok(Error refused) -> Error (AccountAccessError.SanctionRefused refused)
                    | Error error -> Error (storageError logger error)
                | AccountAccessCommand.ListSanctions ->
                    SqliteSanctionStore.listActive database (clock.GetUtcNow()) token
                    |> Result.map (fun records -> AccountWorkResult.ActiveSanctions(records |> List.map _.Sanction))
                    |> Result.mapError (storageError logger)
                | AccountAccessCommand.RecordModeration(moderator, record) ->
                    SqliteAdminStore.recordModeration database moderator record (clock.GetUtcNow()) token
                    |> Result.map (fun () -> AccountWorkResult.Recorded)
                    |> Result.mapError (storageError logger)
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

    // A sanction runs alone: no sign-in in flight can issue a ticket that misses it.
    let private exclusive = function
        | AccountAccessCommand.ResetPassword _ | AccountAccessCommand.CreatePasswordReset _
        | AccountAccessCommand.RevokeAccount _ | AccountAccessCommand.Logout _ | AccountAccessCommand.RenamePlayer _
        | AccountAccessCommand.Sanction _ | AccountAccessCommand.LiftSanction _ -> true
        // The player is online: no second session of theirs can open with a stale
        // ticket, and outstanding tickets are updated when the change settles.
        | AccountAccessCommand.Register _ | AccountAccessCommand.Login _ | AccountAccessCommand.RememberLogin _
        | AccountAccessCommand.Resume _ | AccountAccessCommand.ChangeOwnDisplayName _
        // A kick issues no ticket; a list and an audit line change no account.
        | AccountAccessCommand.Kick _ | AccountAccessCommand.ListSanctions | AccountAccessCommand.RecordModeration _ -> false

    let private settle (logger: ILogger) (requester: Requester) (result: Result<AccountAccessResult, AccountAccessError>) =
        match requester with
        | Requester.Caller reply -> reply.Reply result
        | Requester.Session request ->
            let answer =
                match result with
                | Ok (AccountAccessResult.Renamed profile) -> Ok profile
                | Error (AccountAccessError.TooSoon wait) -> Error (DisplayNameChangeError.TooSoon wait)
                | Error AccountAccessError.Busy -> Error DisplayNameChangeError.Busy
                | Ok _ | Error _ -> Error DisplayNameChangeError.Unavailable
            // A control message of the session: it has room even when the session is busy.
            match request.ReplyTo.TryPost { OperationId = request.OperationId; Result = answer } with
            | AgentTryDeliveryResult.Posted | AgentTryDeliveryResult.Closed -> ()
            | AgentTryDeliveryResult.Full ->
                logger.LogWarning("Display name reply for player {PlayerId} was not delivered: the session is full", PlayerId.value request.PlayerId)
        | Requester.Moderator request ->
            let answer =
                match result with
                | Ok (AccountAccessResult.Sanctioned sanction) -> Ok (ModerationResult.Sanctioned sanction)
                | Ok (AccountAccessResult.SanctionLifted sanction) -> Ok (ModerationResult.Lifted sanction)
                | Ok AccountAccessResult.Kicked -> Ok ModerationResult.Kicked
                | Ok (AccountAccessResult.ActiveSanctions sanctions) -> Ok (ModerationResult.Sanctions sanctions)
                | Ok AccountAccessResult.Completed -> Ok ModerationResult.Recorded
                | Error (AccountAccessError.SanctionRefused refused) -> Error (ModerationError.Refused refused)
                | Error AccountAccessError.Busy -> Error ModerationError.Busy
                | Ok _ | Error _ -> Error ModerationError.Unavailable
            match request.Command, answer with
            | ModerationCommand.Record(moderator, record), Error _ ->
                logger.LogWarning("Audit line {Action} of moderator {PlayerId} was lost: the account service was busy",
                                  AdminAction.key record.Action, PlayerId.value moderator)
            | _, (Ok _ | Error _) -> ()
            match request.ReplyTo.TryPost { OperationId = request.OperationId; Result = answer } with
            | AgentTryDeliveryResult.Posted | AgentTryDeliveryResult.Closed -> ()
            | AgentTryDeliveryResult.Full -> logger.LogWarning("A moderation reply was not delivered: the session is full")

    let private abandoned = function
        | Requester.Caller reply -> reply.IsCompleted
        | Requester.Session _ | Requester.Moderator _ -> false

    let private access options (logger: ILogger) state (context: AgentContext<AuthMessage>) command (requester: Requester) =
        if state.Stopping then settle logger requester (Error AccountAccessError.Unavailable)
        elif state.Exclusive || (exclusive command && state.Pending.Count <> 0) || state.Pending.Count >= options.MaxConcurrentOperations then
            settle logger requester (Error AccountAccessError.Busy)
        else
            let operationId = Guid.NewGuid()
            let request = {
                OperationId = operationId; Command = command
                ReplyTo = context.Ref.TryReliable().Value.Map AuthMessage.Finished
            }
            state.Exclusive <- exclusive command
            state.Pending.Add(operationId, { Command = command; Requester = requester })
            if not (state.Outbox.TrySend(context, request)) then
                state.Exclusive <- false
                state.Pending.Remove operationId |> ignore
                settle logger requester (Error AccountAccessError.Busy)

    let private signInMethod = function
        | AccountAccessCommand.Login _ -> "password"
        | AccountAccessCommand.RememberLogin _ -> "password, remembered"
        | AccountAccessCommand.Resume _ -> "saved login"
        | AccountAccessCommand.Register _ | AccountAccessCommand.Logout _ | AccountAccessCommand.ResetPassword _
        | AccountAccessCommand.CreatePasswordReset _ | AccountAccessCommand.RevokeAccount _ | AccountAccessCommand.RenamePlayer _
        | AccountAccessCommand.ChangeOwnDisplayName _ | AccountAccessCommand.Sanction _ | AccountAccessCommand.LiftSanction _
        | AccountAccessCommand.Kick _ | AccountAccessCommand.ListSanctions | AccountAccessCommand.RecordModeration _ -> "other"

    let private until (sanction: Sanction) =
        match sanction.Expires with
        | ValueSome expires -> expires.ToString("u")
        | ValueNone -> "lifted"

    let private issuerName = function
        | ValueSome (SanctionIssuer.Admin admin) -> $"admin {AdminId.value admin}"
        | ValueSome (SanctionIssuer.Moderator moderator) -> $"moderator {PlayerId.value moderator}"
        | ValueNone -> "a removed account"

    // Names are logged, secrets never: no password, token, code or ticket.
    let private logOutcome (logger: ILogger) (command: AccountAccessCommand) (result: Result<AccountWorkResult, AccountAccessError>) =
        match command, result with
        | (AccountAccessCommand.Login(username, _) | AccountAccessCommand.RememberLogin(username, _)), Error (AccountAccessError.Banned ban) ->
            logger.LogInformation("Sign-in refused for {Username}: banned until {Until}", Username.value username, until ban)
        | AccountAccessCommand.Resume _, Error (AccountAccessError.Banned ban) ->
            logger.LogInformation("Saved login of player {PlayerId} refused: banned until {Until}", PlayerId.value ban.Target, until ban)
        | (AccountAccessCommand.Sanction { Target = target } | AccountAccessCommand.LiftSanction(target, _, _) | AccountAccessCommand.Kick(target, _, _)),
          Error (AccountAccessError.SanctionRefused refused) ->
            logger.LogInformation("Sanction on player {PlayerId} refused: {Refusal}", PlayerId.value target, refused)
        | (AccountAccessCommand.Login(username, _) | AccountAccessCommand.RememberLogin(username, _)), Error AccountAccessError.InvalidCredentials ->
            logger.LogInformation("Sign-in refused for {Username}: wrong username or password", Username.value username)
        | AccountAccessCommand.Resume _, Error AccountAccessError.InvalidCredentials ->
            logger.LogInformation("Saved login refused: unknown, revoked or expired token")
        | AccountAccessCommand.ResetPassword _, Error AccountAccessError.InvalidCredentials ->
            logger.LogInformation("Password reset refused: unknown, used or expired code")
        | AccountAccessCommand.Register(username, _, _), Error AccountAccessError.UsernameTaken ->
            logger.LogInformation("Registration refused: {Username} is taken", Username.value username)
        | AccountAccessCommand.ChangeOwnDisplayName(playerId, displayName, _), Error (AccountAccessError.TooSoon wait) ->
            logger.LogInformation("Display name change of player {PlayerId} to {DisplayName} refused: allowed again in {Minutes} min",
                                  PlayerId.value playerId, DisplayName.value displayName, int (ceil wait.TotalMinutes))
        | _, (Ok _ | Error _) -> ()

    let private ticketsOf state playerId =
        state.Tickets |> Seq.filter (fun entry -> entry.Value.Player.Profile.PlayerId = playerId) |> Seq.map _.Key |> Seq.toArray

    let private withMute mute state playerId =
        for key in ticketsOf state playerId do
            state.Tickets[key] <- { state.Tickets[key] with Player = { state.Tickets[key].Player with Mute = mute } }

    // Live sessions must follow: an undelivered change stops the service rather
    // than leave a banned or muted player playing. A stopped runtime has no
    // sessions left; that failure is handled with ChangeFailed.
    let private delivered state (context: AgentContext<AuthMessage>) change result =
        let posted =
            match state.Changes with
            | None -> true // No game runtime: tools, tests, or between its restarts.
            | Some outbox ->
                let target = state.ChangeTarget
                outbox.TrySend(context, change, fun failure -> AuthMessage.ChangeFailed(target, failure))
        if posted then result
        else
            context.Abort()
            Error AccountAccessError.Unavailable

    let private finished options clock (logger: ILogger) state (context: AgentContext<AuthMessage>) (completion: AccountWorkReply) =
        match state.Pending.TryGetValue completion.OperationId with
        | false, _ -> ()
        | true, pending ->
            state.Exclusive <- false
            state.Pending.Remove completion.OperationId |> ignore
            logOutcome logger pending.Command completion.Result
            let result =
                match completion.Result with
                | Ok (AccountWorkResult.Registered profile) ->
                    logger.LogInformation("Account registered: player {PlayerId} {Username} ({DisplayName})", PlayerId.value profile.PlayerId,
                                          Username.value profile.Username, DisplayName.value profile.DisplayName)
                    Ok (AccountAccessResult.Registered profile)
                | Ok (AccountWorkResult.Verified _) when state.Stopping || abandoned pending.Requester -> Error AccountAccessError.Unavailable
                | Ok (AccountWorkResult.Verified(player, rememberToken)) ->
                    logger.LogInformation("Player {PlayerId} {Username} signed in ({Method})", PlayerId.value player.Profile.PlayerId,
                                          Username.value player.Profile.Username, signInMethod pending.Command)
                    issue options clock state player rememberToken
                | Ok (AccountWorkResult.Renamed(previous, profile, changedBy)) ->
                    // Outstanding tickets would open a session with the old name.
                    for key in ticketsOf state profile.PlayerId do state.Tickets[key] <- { state.Tickets[key] with Player = { state.Tickets[key].Player with Profile = profile } }
                    if previous <> profile.DisplayName then
                        match changedBy with
                        | ValueSome admin ->
                            logger.LogInformation("Display name of player {PlayerId} {Username} changed by admin {AdminId}: {Previous} -> {DisplayName}",
                                                  PlayerId.value profile.PlayerId, Username.value profile.Username, AdminId.value admin,
                                                  DisplayName.value previous, DisplayName.value profile.DisplayName)
                        | ValueNone ->
                            logger.LogInformation("Player {PlayerId} {Username} changed display name: {Previous} -> {DisplayName}",
                                                  PlayerId.value profile.PlayerId, Username.value profile.Username,
                                                  DisplayName.value previous, DisplayName.value profile.DisplayName)
                    Ok (AccountAccessResult.Renamed profile)
                | Ok (AccountWorkResult.LoggedOut key) ->
                    // Logout is exclusive: no pending resume can issue a late ticket.
                    let keys = state.Tickets |> Seq.filter (fun entry -> entry.Value.RememberKey = key) |> Seq.map _.Key |> Seq.toArray
                    for key in keys do state.Tickets.Remove key |> ignore
                    Ok AccountAccessResult.Completed
                | Ok (AccountWorkResult.Revoked(profile, code)) ->
                    for key in ticketsOf state profile.PlayerId do state.Tickets.Remove key |> ignore
                    logger.LogInformation("Account access revoked for player {PlayerId}", PlayerId.value profile.PlayerId)
                    delivered state context (AccountChange.AccessRevoked profile.PlayerId)
                        (if code.Length = 0 then Ok AccountAccessResult.Completed else Ok (AccountAccessResult.PasswordResetCreated code))
                | Ok (AccountWorkResult.Sanctioned sanction) ->
                    logger.LogInformation("Player {PlayerId} got a {Kind} until {Until} from {Issuer}: {Reason}", PlayerId.value sanction.Target,
                                          SanctionKind.key sanction.Kind, until sanction, issuerName sanction.IssuedBy, SanctionReason.value sanction.Reason)
                    let change =
                        match sanction.Kind with
                        | SanctionKind.Ban ->
                            for key in ticketsOf state sanction.Target do state.Tickets.Remove key |> ignore
                            AccountChange.Banned sanction
                        | SanctionKind.Mute ->
                            withMute (ValueSome sanction) state sanction.Target
                            AccountChange.MuteChanged(sanction.Target, ValueSome sanction)
                    delivered state context change (Ok (AccountAccessResult.Sanctioned sanction))
                | Ok (AccountWorkResult.Kicked(target, reason)) ->
                    logger.LogInformation("Player {PlayerId} kicked: {Reason}", PlayerId.value target, SanctionReason.value reason)
                    delivered state context (AccountChange.Kicked(target, reason)) (Ok AccountAccessResult.Kicked)
                | Ok (AccountWorkResult.ActiveSanctions sanctions) -> Ok (AccountAccessResult.ActiveSanctions sanctions)
                | Ok AccountWorkResult.Recorded -> Ok AccountAccessResult.Completed
                | Ok (AccountWorkResult.SanctionLifted sanction) ->
                    logger.LogInformation("The {Kind} of player {PlayerId} was lifted", SanctionKind.key sanction.Kind, PlayerId.value sanction.Target)
                    match sanction.Kind with
                    | SanctionKind.Ban -> Ok (AccountAccessResult.SanctionLifted sanction)
                    | SanctionKind.Mute ->
                        withMute ValueNone state sanction.Target
                        delivered state context (AccountChange.MuteChanged(sanction.Target, ValueNone)) (Ok (AccountAccessResult.SanctionLifted sanction))
                | Error error -> Error error
            settle logger pending.Requester result
            completeIfStopped state context

    let private handle options clock (logger: ILogger) state consumeRequest (context: AgentContext<AuthMessage>) message = task {
        match message with
        | AuthMessage.SetChangeTarget target ->
            state.ChangeTarget <- state.ChangeTarget + 1
            state.Changes <- Some (AgentOutbox(options.MailboxCapacity, target))
        // The runtime stopped and its sessions with it; the change is stored and
        // applies at the next sign-in. Its restart sets a new target.
        | AuthMessage.ChangeFailed(target, AgentSendFailure.Closed) ->
            if target = state.ChangeTarget then
                logger.LogWarning("The game runtime stopped before an account change reached it; the change applies at the next sign-in")
                state.Changes <- None
        | AuthMessage.ChangeFailed(_, failure) ->
            logger.LogError("Account change delivery failed: {Failure}", failure)
            context.Abort()
        | AuthMessage.Start -> context.Own(state.Workers, AuthMessage.WorkersStopped)
        | AuthMessage.Access(command, reply) -> access options logger state context command (Requester.Caller reply)
        | AuthMessage.ChangeDisplayName request ->
            let command = AccountAccessCommand.ChangeOwnDisplayName(request.PlayerId, request.DisplayName, request.MinInterval)
            access options logger state context command (Requester.Session request)
        | AuthMessage.Moderate request ->
            let command =
                match request.Command with
                | ModerationCommand.Sanction order -> AccountAccessCommand.Sanction order
                | ModerationCommand.Lift(target, kind, moderator) -> AccountAccessCommand.LiftSanction(target, kind, SanctionIssuer.Moderator moderator)
                | ModerationCommand.Kick(target, reason, moderator) -> AccountAccessCommand.Kick(target, reason, SanctionIssuer.Moderator moderator)
                | ModerationCommand.ListSanctions -> AccountAccessCommand.ListSanctions
                | ModerationCommand.Record(moderator, record) -> AccountAccessCommand.RecordModeration(moderator, record)
            access options logger state context command (Requester.Moderator request)
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
        | AuthMessage.SetChangeTarget _ | AuthMessage.ChangeFailed _ -> true
        | AuthMessage.Access _ | AuthMessage.ConsumeTicket _ | AuthMessage.ChangeDisplayName _ | AuthMessage.Moderate _ -> false

    /// The options come checked with the configuration.
    let start options database (logger: ILogger) (clock: TimeProvider) =
        let dummyHash = (hasher options).HashPassword(null, Convert.ToBase64String(RandomNumberGenerator.GetBytes 32))
        let workerOptions = { AgentOptions.create "account-storage" with Mailbox = AgentMailbox.boundedWait options.MaxConcurrentOperations }
        let work = AgentReplyDispatcher.createAsyncHandler options.MaxConcurrentOperations (fun (request: AccountWorkRequest) -> request.ReplyTo)
                       (execute options database dummyHash clock logger)
        let workers = Agent.Start(workerOptions, work)
        let state = {
            Tickets = Dictionary(); Pending = Dictionary(); Workers = workers
            Outbox = AgentOutbox(options.MaxConcurrentOperations, workers.Ref.TryReliable().Value)
            Exclusive = false; Changes = None; ChangeTarget = 0; Stopping = false; WorkersStopped = false
        }
        let consumeRequest = AgentReplyDispatcher.createHandler options.MailboxCapacity
                                 (fun (request: SessionAuthenticationRequest) -> request.ReplyTo) (consume options clock state)
        let settings = {
            AgentOptions.create "authentication" with
                Mailbox = AgentMailbox.boundedWithControl options.MailboxCapacity (options.MaxConcurrentOperations + 2)
        }
        let agent = Agent.Start(settings, handle options clock logger state consumeRequest, isControl = isControl)
        agent.TryPost AuthMessage.Start |> ignore
        agent

    let authenticator (agent: Agent<AuthMessage>) = {
        Requests = agent.Ref.TryReliable().Value.Map AuthMessage.ConsumeTicket
        DisplayNames = agent.Ref.TryReliable().Value.Map AuthMessage.ChangeDisplayName
        Moderation = agent.Ref.TryReliable().Value.Map AuthMessage.Moderate
        Completion = agent.Completion
    }
