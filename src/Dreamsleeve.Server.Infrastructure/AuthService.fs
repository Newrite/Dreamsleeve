// Unnamed framework enum values are handled explicitly.
#nowarn "104"

namespace Dreamsleeve.Server.Infrastructure

open System
open System.Collections.Generic
open System.Net
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
    /// How long the one-time code of an account created in the panel lets the player choose a password.
    SetupLifetimeHours: int
    /// Days a sign-in address stays in the player's history after the last sign-in from it.
    SignInHistoryDays: int
    /// How long a started Steam sign-in waits for the browser and then for the client.
    SteamFlowSeconds: int
}

/// Where a public request came from, as the HTTP host saw it (behind a
/// trusted proxy, the forwarded address). Trusted callers have none.
type SignInOrigin = {
    Address: IPAddress voption
    /// The device the client reported, if it could tell.
    Device: DeviceId voption
    /// The proxy of the server ([Proxies]) the request came through.
    Proxy: IPAddress voption
}

[<RequireQualifiedAccess>]
module SignInOrigin =
    let none = { Address = ValueNone; Device = ValueNone; Proxy = ValueNone }

    let ofAddress (address: IPAddress) = { none with Address = if isNull address then ValueNone else ValueSome address }

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
    /// A password registration while the mode allows none.
    | RegistrationClosed of RegistrationMode
    /// A public request from a banned IP range.
    | AddressBanned of AddressBan
    /// A sign-in or registration from a device an account ban covers.
    | DeviceBanned of Sanction
    /// A Steam sign-in that does not exist, expired or was already collected.
    | FlowUnknown

type SessionGrant = {
    Profile: PlayerData
    SessionTicket: string
    ExpiresInSeconds: int
    RememberToken: string
}

[<RequireQualifiedAccess>]
type AccountAccessCommand =
    | Register of Username * DisplayName * password: string * SignInOrigin
    | Login of Username * password: string * SignInOrigin
    | RememberLogin of Username * password: string * SignInOrigin
    | Resume of token: string * SignInOrigin
    | Logout of token: string
    | ResetPassword of code: string * password: string
    // Trusted server callers only. Never map these directly to public HTTP input.
    | CreatePasswordReset of Username
    | RevokeAccount of Username
    /// An administrator's change; the caller has validated and moderated the new name.
    | RenamePlayer of PlayerId * DisplayName * changedBy: AdminId
    /// The player's own change from the game session, limited by minInterval.
    | ChangeOwnDisplayName of PlayerId * DisplayName * minInterval: TimeSpan
    /// The player's own name color from the game session, which checked it.
    | ChangeOwnNameColor of PlayerId * NameColor
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
    /// An administrator's new player: an account without a password and a one-time setup code.
    | CreateAccount of Username * DisplayName
    /// The registration mode in force.
    | ReadRegistration
    /// Changes who may create accounts; changedBy is absent for the console.
    | SetRegistration of RegistrationMode * changedBy: AdminId voption
    /// Bans an IP range for a term; the audit line is written with it.
    | BanAddresses of AddressRange * SanctionReason * SanctionTerm * AdminId
    | LiftAddressBan of banId: int64 * AdminId
    /// The IP range bans in force, newest first.
    | ListAddressBans
    /// Where the player signed in from recently.
    | AddressHistory of PlayerId
    /// Players who signed in from the range recently: whom a ban of it would also hit.
    | PlayersInRange of AddressRange
    /// The devices the player signed in from recently.
    | DeviceHistory of PlayerId
    /// Starts a Steam sign-in: a flow the browser completes and the client polls.
    | BeginSteam of SignInOrigin * remember: bool
    /// The browser came back with this account, verified with Steam, and the
    /// display name a new account takes (the caller moderated it).
    | CompleteSteam of flow: string * SteamProfile * DisplayName
    /// How the Steam sign-in ended, asked by the client that began it.
    | PollSteam of flow: string * secret: string
    /// The work of CompleteSteam with the origin and the choice the flow began
    /// with; the service makes it, callers never send it.
    | SignInSteam of SteamProfile * DisplayName * SignInOrigin * remember: bool

[<RequireQualifiedAccess>]
type AccountAccessResult =
    | Registered of PlayerData
    | SignedIn of SessionGrant
    | Completed
    | PasswordResetCreated of code: string
    /// The stored profile after a rename or a new name color.
    | ProfileChanged of PlayerData
    | Sanctioned of Sanction
    | SanctionLifted of Sanction
    | Kicked
    | ActiveSanctions of Sanction list
    | AccountCreated of PlayerData * setupCode: string
    | Registration of RegistrationMode
    | AddressesBanned of AddressBan
    | AddressBanLifted of AddressBan
    | AddressBans of AddressBan list
    | Addresses of SignInAddress list
    | PlayersAt of AddressMatch list
    | Devices of SignInDevice list
    /// flow goes into the browser's URL; secret stays with the client for polling.
    | SteamStarted of flow: string * secret: string * expiresInSeconds: int
    /// The browser has not come back yet.
    | SteamPending

[<RequireQualifiedAccess>]
type AccountWorkResult =
    | Registered of PlayerData
    | Verified of AuthenticatedPlayer * rememberToken: string
    | LoggedOut of tokenHash: string
    | Revoked of PlayerData * resetCode: string
    | Renamed of previous: DisplayName * PlayerData * changedBy: AdminId voption
    | Recolored of PlayerData
    | Sanctioned of Sanction
    | SanctionLifted of Sanction
    | Kicked of PlayerId * SanctionReason
    | ActiveSanctions of Sanction list
    | Recorded
    | Created of PlayerData * setupCode: string
    | Registration of RegistrationMode
    | AddressesBanned of AddressBan
    | AddressBanLifted of AddressBan
    | AddressBans of AddressBan list
    | Addresses of SignInAddress list
    | PlayersAt of AddressMatch list
    | Devices of SignInDevice list

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
    /// From a game session; settled by a ProfileChangeReply.
    | ChangeProfile of ProfileChangeRequest
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
        | Session of ProfileChangeRequest
        | Moderator of ModerationRequest

    type private Pending = { Command: AccountAccessCommand; Requester: Requester }

    /// A Steam sign-in between its start and the client collecting how it ended.
    type private SteamFlow = {
        SecretKey: string
        Origin: SignInOrigin
        Remember: bool
        Started: int64
        mutable Completing: bool
        mutable Outcome: Result<AccountAccessResult, AccountAccessError> voption
    }

    /// What a request comes to before the workers: an answer, or work for them.
    [<RequireQualifiedAccess>]
    type private Admission =
        | Settled of Result<AccountAccessResult, AccountAccessError>
        | Work of AccountAccessCommand

    type private State = {
        Tickets: Dictionary<string, Ticket>
        /// Steam sign-ins by flow; bounded by MaxTickets and SteamFlowSeconds.
        Flows: Dictionary<string, SteamFlow>
        Pending: Dictionary<Guid, Pending>
        Workers: ReliableAgent<AccountWorkRequest>
        ChangesCapacity: AgentDeliveryCapacity
        Outbox: AgentOutbox<AccountWorkRequest>
        mutable Exclusive: bool
        mutable Changes: AgentOutbox<AccountChange> option
        /// Counts SetChangeTarget, so a late failure of a replaced target is told apart.
        mutable ChangeTarget: int
        mutable Stopping: bool
        mutable WorkersStopped: bool
        /// IP range bans in force, read at startup and kept with every change;
        /// public requests are checked against them before any work.
        mutable AddressBans: AddressBan list
    }

    let defaults = {
        MailboxCapacity = 256; MaxConcurrentOperations = 4; MaxTickets = 4096
        TicketLifetimeSeconds = 60; PasswordIterations = 210000
        SavedLoginDays = 30; MaxSavedLogins = 8; ResetLifetimeMinutes = 15; DisplayNameHistory = 20
        SetupLifetimeHours = 72; SignInHistoryDays = 30; SteamFlowSeconds = 600
    }

    let validate options = [
        if options.SavedLoginDays < 1 || options.SavedLoginDays > 365 then "Saved login lifetime must be 1..365 days."
        if options.MaxSavedLogins < 1 || options.MaxSavedLogins > 32 then "Saved logins per account must be 1..32."
        if options.ResetLifetimeMinutes < 1 || options.ResetLifetimeMinutes > 60 then "Reset lifetime must be 1..60 minutes."
        if options.SetupLifetimeHours < 1 || options.SetupLifetimeHours > 720 then "Authentication.Service.SetupLifetimeHours must be 1..720."
        if options.SignInHistoryDays < 1 || options.SignInHistoryDays > 3650 then "Authentication.Service.SignInHistoryDays must be 1..3650."
        if options.SteamFlowSeconds < 60 || options.SteamFlowSeconds > 3600 then "Authentication.Service.SteamFlowSeconds must be 60..3600."
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
            | ValueNone ->
                Ok { Profile = account.Profile; Role = account.Role; Mute = Sanction.find SanctionKind.Mute now sanctions; SignedInFrom = ValueNone }

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

    // The address of a sign-in or registration that succeeded; failing to keep
    // it is logged and never fails the sign-in.
    let private noteAddress options database (logger: ILogger) (clock: TimeProvider) token (origin: SignInOrigin) (playerId: PlayerId) =
        match origin.Address with
        | ValueNone -> ()
        | ValueSome address ->
            match SqliteAddressStore.record database playerId address (clock.GetUtcNow()) options.SignInHistoryDays token with
            | Ok () -> ()
            | Error error -> logger.LogWarning("Sign-in address of player {PlayerId} not recorded: {Error}", PlayerId.value playerId, error)

    let private noteDevice options database (logger: ILogger) (clock: TimeProvider) token (origin: SignInOrigin) (playerId: PlayerId) =
        match origin.Device with
        | ValueNone -> ()
        | ValueSome device ->
            match SqliteDeviceStore.record database playerId device (clock.GetUtcNow()) options.SignInHistoryDays token with
            | Ok () -> ()
            | Error error -> logger.LogWarning("Device of player {PlayerId} not recorded: {Error}", PlayerId.value playerId, error)

    let private signedIn options database logger clock token origin (result: Result<AccountWorkResult, AccountAccessError>) =
        let note playerId =
            noteAddress options database logger clock token origin playerId
            noteDevice options database logger clock token origin playerId
        match result with
        | Ok (AccountWorkResult.Verified(player, secret)) ->
            note player.Profile.PlayerId
            // The ticket remembers where the player signed in from.
            Ok (AccountWorkResult.Verified({ player with SignedInFrom = origin.Address }, secret))
        | Ok (AccountWorkResult.Registered profile) ->
            note profile.PlayerId
            result
        | Ok _ | Error _ -> result

    // A device that an account ban in force covers refuses every account.
    let private deviceRefusal database (clock: TimeProvider) logger token (origin: SignInOrigin) =
        match origin.Device with
        | ValueNone -> Ok ()
        | ValueSome device ->
            match SqliteSanctionStore.bannedDevice database device (clock.GetUtcNow()) token with
            | Ok ValueNone -> Ok ()
            | Ok (ValueSome ban) -> Error (AccountAccessError.DeviceBanned ban)
            | Error error -> Error (storageError logger error)

    /// The provider name of Steam accounts in account_identities.
    [<Literal>]
    let SteamProvider = "steam"

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
                | AccountAccessCommand.Register(username, displayName, password, origin) ->
                    if not (validPassword password) then Error AccountAccessError.InvalidCredentials
                    else
                        // The mode is checked before the costly hash; changing it runs alone.
                        match SqliteAccountStore.registrationMode database token with
                        | Error error -> Error (storageError logger error)
                        | Ok mode when not (RegistrationMode.allowsPassword mode) -> Error (AccountAccessError.RegistrationClosed mode)
                        | Ok _ ->
                            deviceRefusal database clock logger token origin
                            |> Result.bind (fun () ->
                                let passwordHash = (hasher options).HashPassword(null, password)
                                SqliteAccountStore.create database username displayName passwordHash token
                                |> Result.map AccountWorkResult.Registered
                                |> Result.mapError (storageError logger))
                            |> signedIn options database logger clock token origin
                | AccountAccessCommand.Login(username, password, origin) ->
                    if not (validPassword password) then Error AccountAccessError.InvalidCredentials
                    else deviceRefusal database clock logger token origin
                         |> Result.bind (fun () -> verify options database dummyHash logger token username password)
                         |> Result.bind (admit database clock logger token)
                         |> Result.map (fun player -> AccountWorkResult.Verified(player, ""))
                         |> signedIn options database logger clock token origin
                | AccountAccessCommand.RememberLogin(username, password, origin) ->
                    if not (validPassword password) then Error AccountAccessError.InvalidCredentials
                    else deviceRefusal database clock logger token origin
                         |> Result.bind (fun () -> verify options database dummyHash logger token username password)
                         |> Result.bind (fun account ->
                             admit database clock logger token account |> Result.bind (savedLogin options database now logger token account))
                         |> signedIn options database logger clock token origin
                | AccountAccessCommand.Resume(secret, origin) ->
                    if not (validToken secret) then Error AccountAccessError.InvalidCredentials
                    else deviceRefusal database clock logger token origin
                         |> Result.bind (fun () -> SqliteAccountStore.resume database (ticketKey secret) now token |> Result.mapError (storageError logger))
                         |> Result.bind (admit database clock logger token)
                         |> Result.map (fun player -> AccountWorkResult.Verified(player, secret))
                         |> signedIn options database logger clock token origin
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
                | AccountAccessCommand.ChangeOwnNameColor(playerId, color) ->
                    match SqliteAccountStore.setNameColor database playerId color token with
                    | Ok (Some stored) -> Ok (AccountWorkResult.Recolored stored.Profile)
                    | Ok None -> Error AccountAccessError.InvalidCredentials
                    | Error error -> Error (storageError logger error)
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
                | AccountAccessCommand.CreateAccount(username, displayName) ->
                    let code = newToken ()
                    let expires = now + int64 options.SetupLifetimeHours * 3600L
                    SqliteAccountStore.createInvited database username displayName (ticketKey code) expires token
                    |> Result.map (fun profile -> AccountWorkResult.Created(profile, code))
                    |> Result.mapError (storageError logger)
                | AccountAccessCommand.ReadRegistration ->
                    SqliteAccountStore.registrationMode database token
                    |> Result.map AccountWorkResult.Registration
                    |> Result.mapError (storageError logger)
                | AccountAccessCommand.SetRegistration(mode, changedBy) ->
                    SqliteAccountStore.setRegistrationMode database mode changedBy (clock.GetUtcNow()) token
                    |> Result.map AccountWorkResult.Registration
                    |> Result.mapError (storageError logger)
                | AccountAccessCommand.BanAddresses(range, reason, term, admin) ->
                    SqliteAddressStore.ban database range reason term admin (clock.GetUtcNow()) token
                    |> Result.map AccountWorkResult.AddressesBanned
                    |> Result.mapError (storageError logger)
                | AccountAccessCommand.LiftAddressBan(banId, admin) ->
                    match SqliteAddressStore.lift database banId admin (clock.GetUtcNow()) token with
                    | Ok (Some ban) -> Ok (AccountWorkResult.AddressBanLifted ban)
                    | Ok None -> Error (AccountAccessError.SanctionRefused SanctionError.NotActive)
                    | Error error -> Error (storageError logger error)
                | AccountAccessCommand.ListAddressBans ->
                    SqliteAddressStore.active database (clock.GetUtcNow()) token
                    |> Result.map AccountWorkResult.AddressBans
                    |> Result.mapError (storageError logger)
                | AccountAccessCommand.AddressHistory playerId ->
                    SqliteAddressStore.history database playerId token
                    |> Result.map AccountWorkResult.Addresses
                    |> Result.mapError (storageError logger)
                | AccountAccessCommand.PlayersInRange range ->
                    SqliteAddressStore.playersIn database range token
                    |> Result.map AccountWorkResult.PlayersAt
                    |> Result.mapError (storageError logger)
                | AccountAccessCommand.DeviceHistory playerId ->
                    SqliteDeviceStore.history database playerId token
                    |> Result.map AccountWorkResult.Devices
                    |> Result.mapError (storageError logger)
                | AccountAccessCommand.SignInSteam(profile, name, origin, remember) ->
                    let subject = string profile.SteamId
                    let signIn account =
                        admit database clock logger token account
                        |> Result.bind (fun player ->
                            if remember then savedLogin options database now logger token account player
                            else Ok (AccountWorkResult.Verified(player, "")))
                    // An existing Steam account signs in in any mode; a new one only where the mode allows it.
                    deviceRefusal database clock logger token origin
                    |> Result.bind (fun () ->
                        match SqliteAccountStore.findIdentity database SteamProvider subject token with
                        | Error error -> Error (storageError logger error)
                        | Ok (Some account) -> signIn account
                        | Ok None ->
                            match SqliteAccountStore.registrationMode database token with
                            | Error error -> Error (storageError logger error)
                            | Ok mode when not (RegistrationMode.allowsSteam mode) -> Error (AccountAccessError.RegistrationClosed mode)
                            | Ok _ ->
                                match Username.create Int32.MaxValue (Moderation.SteamUsernamePrefix + subject) with
                                | Error _ -> Error AccountAccessError.Unavailable
                                | Ok username ->
                                    match SqliteAccountStore.createExternal database username name SteamProvider subject token with
                                    | Error error -> Error (storageError logger error)
                                    | Ok account ->
                                        let created = profile.Created |> ValueOption.map (fun time -> time.ToString("yyyy-MM-dd")) |> ValueOption.defaultValue "unknown"
                                        logger.LogInformation("Account registered through Steam: player {PlayerId} {Username} ({DisplayName}), Steam account created {Created}",
                                                              PlayerId.value account.Profile.PlayerId, Username.value username, DisplayName.value name, created)
                                        signIn account)
                    |> signedIn options database logger clock token origin
                // Answered by the agent itself; they never reach a worker.
                | AccountAccessCommand.BeginSteam _ | AccountAccessCommand.CompleteSteam _ | AccountAccessCommand.PollSteam _ ->
                    Error AccountAccessError.Unavailable
            with
            | :? OperationCanceledException when token.IsCancellationRequested -> Error AccountAccessError.Unavailable
            // Fresh request supervision: per-unit contexts/transactions have
            // disposed before this boundary; actor state changes only on completion.
            // The failed work is not retried and its original cause is logged.
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

    let private completeIfStopped state (context: ReliableAgentContext<AuthMessage>) =
        if state.Stopping && state.Pending.Count = 0 then
            if state.WorkersStopped then context.Complete() |> ignore
            else state.Workers.Complete() |> ignore

    // A sanction runs alone: no sign-in in flight can issue a ticket that misses it.
    let private exclusive = function
        | AccountAccessCommand.ResetPassword _ | AccountAccessCommand.CreatePasswordReset _
        | AccountAccessCommand.RevokeAccount _ | AccountAccessCommand.Logout _ | AccountAccessCommand.RenamePlayer _
        | AccountAccessCommand.Sanction _ | AccountAccessCommand.LiftSanction _
        // No registration in flight passes under the mode it replaces, and no
        // sign-in under the bans a change replaces.
        | AccountAccessCommand.SetRegistration _ | AccountAccessCommand.BanAddresses _ | AccountAccessCommand.LiftAddressBan _ -> true
        // The player is online: no second session of theirs can open with a stale
        // ticket, and outstanding tickets are updated when the change settles.
        | AccountAccessCommand.Register _ | AccountAccessCommand.Login _ | AccountAccessCommand.RememberLogin _
        | AccountAccessCommand.Resume _ | AccountAccessCommand.ChangeOwnDisplayName _ | AccountAccessCommand.ChangeOwnNameColor _
        // A kick issues no ticket; a list and an audit line change no account.
        | AccountAccessCommand.Kick _ | AccountAccessCommand.ListSanctions | AccountAccessCommand.RecordModeration _
        | AccountAccessCommand.CreateAccount _ | AccountAccessCommand.ReadRegistration
        | AccountAccessCommand.ListAddressBans | AccountAccessCommand.AddressHistory _ | AccountAccessCommand.PlayersInRange _
        | AccountAccessCommand.DeviceHistory _ | AccountAccessCommand.SignInSteam _
        | AccountAccessCommand.BeginSteam _ | AccountAccessCommand.CompleteSteam _ | AccountAccessCommand.PollSteam _ -> false

    let private settle (logger: ILogger) (requester: Requester) (result: Result<AccountAccessResult, AccountAccessError>) =
        match requester with
        | Requester.Caller reply -> reply.Reply result
        | Requester.Session request ->
            let answer =
                match result with
                | Ok (AccountAccessResult.ProfileChanged profile) -> Ok profile
                | Error (AccountAccessError.TooSoon wait) -> Error (ProfileChangeError.TooSoon wait)
                | Error AccountAccessError.Busy -> Error ProfileChangeError.Busy
                | Ok _ | Error _ -> Error ProfileChangeError.Unavailable
            // A control message of the session: it has room even when the session is busy.
            match request.ReplyTo.TryPost { OperationId = request.OperationId; Result = answer } with
            | AgentTryDeliveryResult.Posted | AgentTryDeliveryResult.Closed -> ()
            | AgentTryDeliveryResult.Full ->
                logger.LogWarning("Profile change reply for player {PlayerId} was not delivered: the session is full", PlayerId.value request.PlayerId)
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

    // The origin of a public request; trusted commands have none.
    let private originOf = function
        | AccountAccessCommand.Register(_, _, _, origin) | AccountAccessCommand.Login(_, _, origin)
        | AccountAccessCommand.RememberLogin(_, _, origin) | AccountAccessCommand.Resume(_, origin)
        | AccountAccessCommand.BeginSteam(origin, _) | AccountAccessCommand.SignInSteam(_, _, origin, _) -> origin
        | AccountAccessCommand.Logout _ | AccountAccessCommand.ResetPassword _ | AccountAccessCommand.CreatePasswordReset _
        | AccountAccessCommand.RevokeAccount _ | AccountAccessCommand.RenamePlayer _ | AccountAccessCommand.ChangeOwnDisplayName _
        | AccountAccessCommand.ChangeOwnNameColor _
        | AccountAccessCommand.Sanction _ | AccountAccessCommand.LiftSanction _ | AccountAccessCommand.Kick _
        | AccountAccessCommand.ListSanctions | AccountAccessCommand.RecordModeration _ | AccountAccessCommand.CreateAccount _
        | AccountAccessCommand.ReadRegistration | AccountAccessCommand.SetRegistration _ | AccountAccessCommand.BanAddresses _
        | AccountAccessCommand.LiftAddressBan _ | AccountAccessCommand.ListAddressBans | AccountAccessCommand.AddressHistory _
        | AccountAccessCommand.PlayersInRange _ | AccountAccessCommand.DeviceHistory _
        | AccountAccessCommand.CompleteSteam _ | AccountAccessCommand.PollSteam _ -> SignInOrigin.none

    let private isSteam = function
        | AccountAccessCommand.CompleteSteam _ -> true
        | _ -> false

    // How the work of a Steam sign-in ended is kept for its client, whoever else
    // waits for it. A refused repeat of the completion did no work and keeps nothing.
    let private settleCommand logger state command requester (result: Result<AccountAccessResult, AccountAccessError>) =
        match command with
        | AccountAccessCommand.CompleteSteam(flow, _, _) ->
            match state.Flows.TryGetValue flow with
            | true, entry ->
                entry.Completing <- false
                entry.Outcome <- ValueSome result
            | false, _ -> ()
        | _ -> ()
        settle logger requester result

    let private pruneFlows options (clock: TimeProvider) state =
        let expired =
            state.Flows
            |> Seq.filter (fun entry -> clock.GetElapsedTime(entry.Value.Started).TotalSeconds >= float options.SteamFlowSeconds)
            |> Seq.map _.Key
            |> Seq.toArray
        for key in expired do state.Flows.Remove key |> ignore

    // Steam sign-ins live in this agent: starting and polling need no storage;
    // completing becomes SignInSteam with the origin the flow began with.
    let private steam options (clock: TimeProvider) state command =
        match command with
        | AccountAccessCommand.BeginSteam(origin, remember) ->
            pruneFlows options clock state
            if state.Flows.Count >= options.MaxTickets then Admission.Settled(Error AccountAccessError.Busy)
            else
                let flow, secret = newToken (), newToken ()
                state.Flows.Add(flow, { SecretKey = ticketKey secret; Origin = origin; Remember = remember; Started = clock.GetTimestamp()
                                        Completing = false; Outcome = ValueNone })
                Admission.Settled(Ok (AccountAccessResult.SteamStarted(flow, secret, options.SteamFlowSeconds)))
        | AccountAccessCommand.PollSteam(flow, secret) ->
            pruneFlows options clock state
            match state.Flows.TryGetValue flow with
            | true, entry when entry.SecretKey = ticketKey secret ->
                match entry.Outcome with
                | ValueNone -> Admission.Settled(Ok AccountAccessResult.SteamPending)
                | ValueSome outcome ->
                    state.Flows.Remove flow |> ignore
                    Admission.Settled outcome
            | true, _ | false, _ -> Admission.Settled(Error AccountAccessError.FlowUnknown)
        | AccountAccessCommand.CompleteSteam(flow, profile, name) ->
            pruneFlows options clock state
            match state.Flows.TryGetValue flow with
            | true, entry when not entry.Completing && entry.Outcome.IsNone ->
                entry.Completing <- true
                Admission.Work(AccountAccessCommand.SignInSteam(profile, name, entry.Origin, entry.Remember))
            | true, _ | false, _ -> Admission.Settled(Error AccountAccessError.FlowUnknown)
        | other -> Admission.Work other

    // The ban in force on the address of a public request, logged.
    let private bannedAddress (clock: TimeProvider) (logger: ILogger) state command =
        match (originOf command).Address with
        | ValueNone -> ValueNone
        | ValueSome address ->
            let ban = AddressBan.find (clock.GetUtcNow()) address state.AddressBans
            ban |> ValueOption.iter (fun ban ->
                logger.LogInformation("Request from {Address} refused: the range {Range} is banned", ClientAddress.text address, AddressRange.key ban.Range))
            ban

    let private access options (clock: TimeProvider) (logger: ILogger) state (context: ReliableAgentContext<AuthMessage>) command (requester: Requester) =
        let settleWith = settle logger requester
        if state.Stopping then settleWith (Error AccountAccessError.Unavailable)
        else
            match bannedAddress clock logger state command with
            | ValueSome ban -> settleWith (Error (AccountAccessError.AddressBanned ban))
            | ValueNone ->
                match steam options clock state command with
                | Admission.Settled result -> settleWith result
                | Admission.Work work ->
                    let settleWith = settleCommand logger state command requester
                    match bannedAddress clock logger state work with
                    | ValueSome ban -> settleWith (Error (AccountAccessError.AddressBanned ban))
                    | ValueNone when state.Exclusive || (exclusive work && state.Pending.Count <> 0) || state.Pending.Count >= options.MaxConcurrentOperations ->
                        settleWith (Error AccountAccessError.Busy)
                    | ValueNone ->
                        let operationId = Guid.NewGuid()
                        let request = {
                            OperationId = operationId; Command = work
                            ReplyTo = context.Ref.Map AuthMessage.Finished
                        }
                        state.Exclusive <- exclusive work
                        state.Pending.Add(operationId, { Command = command; Requester = requester })
                        if not (state.Outbox.TrySend(context, request)) then
                            state.Exclusive <- false
                            state.Pending.Remove operationId |> ignore
                            settleWith (Error AccountAccessError.Busy)

    let private signInMethod = function
        | AccountAccessCommand.Login _ -> "password"
        | AccountAccessCommand.RememberLogin _ -> "password, remembered"
        | AccountAccessCommand.Resume _ -> "saved login"
        | AccountAccessCommand.Register _ | AccountAccessCommand.Logout _ | AccountAccessCommand.ResetPassword _
        | AccountAccessCommand.CreatePasswordReset _ | AccountAccessCommand.RevokeAccount _ | AccountAccessCommand.RenamePlayer _
        | AccountAccessCommand.ChangeOwnDisplayName _ | AccountAccessCommand.ChangeOwnNameColor _
        | AccountAccessCommand.Sanction _ | AccountAccessCommand.LiftSanction _
        | AccountAccessCommand.Kick _ | AccountAccessCommand.ListSanctions | AccountAccessCommand.RecordModeration _
        | AccountAccessCommand.CreateAccount _ | AccountAccessCommand.ReadRegistration | AccountAccessCommand.SetRegistration _
        | AccountAccessCommand.BanAddresses _ | AccountAccessCommand.LiftAddressBan _ | AccountAccessCommand.ListAddressBans
        | AccountAccessCommand.AddressHistory _ | AccountAccessCommand.PlayersInRange _ | AccountAccessCommand.DeviceHistory _
        | AccountAccessCommand.BeginSteam _ | AccountAccessCommand.PollSteam _ | AccountAccessCommand.SignInSteam _ -> "other"
        | AccountAccessCommand.CompleteSteam _ -> "Steam"

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
        | (AccountAccessCommand.Login(username, _, _) | AccountAccessCommand.RememberLogin(username, _, _)), Error (AccountAccessError.Banned ban) ->
            logger.LogInformation("Sign-in refused for {Username}: banned until {Until}", Username.value username, until ban)
        | (AccountAccessCommand.Login(username, _, _) | AccountAccessCommand.RememberLogin(username, _, _)), Error (AccountAccessError.DeviceBanned ban) ->
            logger.LogInformation("Sign-in refused for {Username}: the device is banned with player {PlayerId} until {Until}",
                                  Username.value username, PlayerId.value ban.Target, until ban)
        | AccountAccessCommand.Register(username, _, _, _), Error (AccountAccessError.DeviceBanned ban) ->
            logger.LogInformation("Registration of {Username} refused: the device is banned with player {PlayerId} until {Until}",
                                  Username.value username, PlayerId.value ban.Target, until ban)
        | AccountAccessCommand.Resume _, Error (AccountAccessError.DeviceBanned ban) ->
            logger.LogInformation("Saved login refused: the device is banned with player {PlayerId} until {Until}", PlayerId.value ban.Target, until ban)
        | AccountAccessCommand.CompleteSteam(_, profile, _), Error error ->
            logger.LogInformation("Steam sign-in of {SteamId} refused: {Error}", profile.SteamId, error)
        | AccountAccessCommand.Resume _, Error (AccountAccessError.Banned ban) ->
            logger.LogInformation("Saved login of player {PlayerId} refused: banned until {Until}", PlayerId.value ban.Target, until ban)
        | (AccountAccessCommand.Sanction { Target = target } | AccountAccessCommand.LiftSanction(target, _, _) | AccountAccessCommand.Kick(target, _, _)),
          Error (AccountAccessError.SanctionRefused refused) ->
            logger.LogInformation("Sanction on player {PlayerId} refused: {Refusal}", PlayerId.value target, refused)
        | (AccountAccessCommand.Login(username, _, _) | AccountAccessCommand.RememberLogin(username, _, _)), Error AccountAccessError.InvalidCredentials ->
            logger.LogInformation("Sign-in refused for {Username}: wrong username or password", Username.value username)
        | AccountAccessCommand.Resume _, Error AccountAccessError.InvalidCredentials ->
            logger.LogInformation("Saved login refused: unknown, revoked or expired token")
        | AccountAccessCommand.ResetPassword _, Error AccountAccessError.InvalidCredentials ->
            logger.LogInformation("Password reset refused: unknown, used or expired code")
        | AccountAccessCommand.Register(username, _, _, _), Error AccountAccessError.UsernameTaken ->
            logger.LogInformation("Registration refused: {Username} is taken", Username.value username)
        | AccountAccessCommand.Register(username, _, _, _), Error (AccountAccessError.RegistrationClosed mode) ->
            logger.LogInformation("Registration of {Username} refused: the registration mode is {Mode}", Username.value username, RegistrationMode.key mode)
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
    let private delivered state (context: ReliableAgentContext<AuthMessage>) change result =
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

    let private finished options clock (logger: ILogger) state (context: ReliableAgentContext<AuthMessage>) (completion: AccountWorkReply) =
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
                // A Steam sign-in keeps its outcome for the client even when the browser left.
                | Ok (AccountWorkResult.Verified _) when state.Stopping || (abandoned pending.Requester && not (isSteam pending.Command)) ->
                    Error AccountAccessError.Unavailable
                | Ok (AccountWorkResult.Verified(player, rememberToken)) ->
                    match (originOf pending.Command).Proxy with
                    | ValueSome proxy ->
                        logger.LogInformation("Player {PlayerId} {Username} signed in ({Method}) through proxy {Proxy}", PlayerId.value player.Profile.PlayerId,
                                              Username.value player.Profile.Username, signInMethod pending.Command, ClientAddress.text proxy)
                    | ValueNone ->
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
                    Ok (AccountAccessResult.ProfileChanged profile)
                | Ok (AccountWorkResult.Recolored profile) ->
                    for key in ticketsOf state profile.PlayerId do state.Tickets[key] <- { state.Tickets[key] with Player = { state.Tickets[key].Player with Profile = profile } }
                    logger.LogInformation("Player {PlayerId} {Username} changed name color to #{Color:X6}", PlayerId.value profile.PlayerId,
                                          Username.value profile.Username, NameColor.value profile.NameColor)
                    Ok (AccountAccessResult.ProfileChanged profile)
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
                | Ok (AccountWorkResult.Created(profile, code)) ->
                    logger.LogInformation("Account created in the panel: player {PlayerId} {Username} ({DisplayName}), setup code issued",
                                          PlayerId.value profile.PlayerId, Username.value profile.Username, DisplayName.value profile.DisplayName)
                    Ok (AccountAccessResult.AccountCreated(profile, code))
                | Ok (AccountWorkResult.AddressesBanned ban) ->
                    let now = clock.GetUtcNow()
                    state.AddressBans <- ban :: state.AddressBans |> List.filter (AddressBan.activeAt now)
                    logger.LogInformation("IP range {Range} banned until {Until}: {Reason}", AddressRange.key ban.Range,
                                          (match ban.Expires with ValueSome expires -> expires.ToString("u") | ValueNone -> "lifted"),
                                          SanctionReason.value ban.Reason)
                    delivered state context (AccountChange.AddressBans state.AddressBans) (Ok (AccountAccessResult.AddressesBanned ban))
                | Ok (AccountWorkResult.AddressBanLifted ban) ->
                    let now = clock.GetUtcNow()
                    state.AddressBans <- state.AddressBans |> List.filter (fun held -> held.Id <> ban.Id && AddressBan.activeAt now held)
                    logger.LogInformation("Ban of IP range {Range} lifted", AddressRange.key ban.Range)
                    delivered state context (AccountChange.AddressBans state.AddressBans) (Ok (AccountAccessResult.AddressBanLifted ban))
                | Ok (AccountWorkResult.AddressBans bans) -> Ok (AccountAccessResult.AddressBans bans)
                | Ok (AccountWorkResult.Addresses addresses) -> Ok (AccountAccessResult.Addresses addresses)
                | Ok (AccountWorkResult.PlayersAt players) -> Ok (AccountAccessResult.PlayersAt players)
                | Ok (AccountWorkResult.Devices devices) -> Ok (AccountAccessResult.Devices devices)
                | Ok (AccountWorkResult.Registration mode) ->
                    match pending.Command with
                    | AccountAccessCommand.SetRegistration(_, changedBy) ->
                        let who = match changedBy with ValueSome admin -> $"admin {AdminId.value admin}" | ValueNone -> "the console"
                        logger.LogInformation("Registration mode set to {Mode} by {Who}", RegistrationMode.key mode, who)
                    | _ -> ()
                    Ok (AccountAccessResult.Registration mode)
                | Ok (AccountWorkResult.SanctionLifted sanction) ->
                    logger.LogInformation("The {Kind} of player {PlayerId} was lifted", SanctionKind.key sanction.Kind, PlayerId.value sanction.Target)
                    match sanction.Kind with
                    | SanctionKind.Ban -> Ok (AccountAccessResult.SanctionLifted sanction)
                    | SanctionKind.Mute ->
                        withMute ValueNone state sanction.Target
                        delivered state context (AccountChange.MuteChanged(sanction.Target, ValueNone)) (Ok (AccountAccessResult.SanctionLifted sanction))
                | Error error -> Error error
            settleCommand logger state pending.Command pending.Requester result
            completeIfStopped state context

    let private handle options clock (logger: ILogger) state consumeRequest (context: ReliableAgentContext<AuthMessage>) message = task {
        match message with
        | AuthMessage.SetChangeTarget target ->
            state.ChangeTarget <- state.ChangeTarget + 1
            let outbox = AgentOutbox.Create(state.ChangesCapacity, target)
            state.Changes <- Some outbox
            // A runtime starts without bans: it learns the ones in force first.
            let generation = state.ChangeTarget
            if not (outbox.TrySend(context, AccountChange.AddressBans state.AddressBans, fun failure -> AuthMessage.ChangeFailed(generation, failure))) then
                logger.LogError("The game runtime did not take the IP range bans in force")
                context.Abort()
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
        | AuthMessage.Access(command, reply) -> access options clock logger state context command (Requester.Caller reply)
        | AuthMessage.ChangeProfile request ->
            let command =
                match request.Change with
                | ProfileChange.DisplayName(name, interval) -> AccountAccessCommand.ChangeOwnDisplayName(request.PlayerId, name, interval)
                | ProfileChange.NameColor color -> AccountAccessCommand.ChangeOwnNameColor(request.PlayerId, color)
            access options clock logger state context command (Requester.Session request)
        | AuthMessage.Moderate request ->
            let command =
                match request.Command with
                | ModerationCommand.Sanction order -> AccountAccessCommand.Sanction order
                | ModerationCommand.Lift(target, kind, moderator) -> AccountAccessCommand.LiftSanction(target, kind, SanctionIssuer.Moderator moderator)
                | ModerationCommand.Kick(target, reason, moderator) -> AccountAccessCommand.Kick(target, reason, SanctionIssuer.Moderator moderator)
                | ModerationCommand.ListSanctions -> AccountAccessCommand.ListSanctions
                | ModerationCommand.Record(moderator, record) -> AccountAccessCommand.RecordModeration(moderator, record)
            access options clock logger state context command (Requester.Moderator request)
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
            state.Flows.Clear()
            completeIfStopped state context
    }

    let private isControl = function
        | AuthMessage.Start | AuthMessage.Finished _ | AuthMessage.WorkersStopped _ | AuthMessage.Stop
        | AuthMessage.SetChangeTarget _ | AuthMessage.ChangeFailed _ -> true
        | AuthMessage.Access _ | AuthMessage.ConsumeTicket _ | AuthMessage.ChangeProfile _ | AuthMessage.Moderate _ -> false

    [<RequireQualifiedAccess>]
    type StartError =
        | Storage of AccountStoreError
        | Agent of AgentStartError

    /// Validate all derived Agent budgets before storage, hashing or worker startup.
    let private preflight options =
        if int64 options.MaxConcurrentOperations + 2L > int64 Int32.MaxValue then
            Error (AgentStartError.CapacityOverflow(options.MaxConcurrentOperations, 2))
        else
            let workerOptions = { AgentOptions.create "account-storage" with Mailbox = AgentMailbox.boundedWait options.MaxConcurrentOperations }
            let settings = {
                AgentOptions.create "authentication" with
                    Mailbox = AgentMailbox.boundedWithControl options.MailboxCapacity (options.MaxConcurrentOperations + 2)
            }
            match Agent<AccountWorkRequest>.TryCheckReliable workerOptions,
                  Agent<AuthMessage>.TryCheckReliable(settings, isControl = isControl),
                  AgentDeliveryCapacity.TryCreate options.MaxConcurrentOperations,
                  AgentDeliveryCapacity.TryCreate options.MailboxCapacity with
            | Error error, _, _, _ | _, Error error, _, _ | _, _, Error error, _ | _, _, _, Error error -> Error error
            | Ok worker, Ok owner, Ok operations, Ok changes -> Ok (worker, owner, operations, changes)

    let private startWithBans options database (logger: ILogger) (clock: TimeProvider) bans
                              (worker: ReliableAgentConfiguration<AccountWorkRequest>, owner: ReliableAgentConfiguration<AuthMessage>,
                               operations: AgentDeliveryCapacity, changes: AgentDeliveryCapacity) =
        let dummyHash = (hasher options).HashPassword(null, Convert.ToBase64String(RandomNumberGenerator.GetBytes 32))
        let work = AgentReplyDispatcher.createAsyncHandler operations (fun (request: AccountWorkRequest) -> request.ReplyTo)
                       (execute options database dummyHash clock logger)
        let workers = worker.Start work
        let state = {
            Tickets = Dictionary(); Flows = Dictionary(); Pending = Dictionary(); Workers = workers
            Outbox = AgentOutbox.Create(operations, workers.Ref)
            ChangesCapacity = changes
            Exclusive = false; Changes = None; ChangeTarget = 0; Stopping = false; WorkersStopped = false
            AddressBans = bans
        }
        let consumeRequest = AgentReplyDispatcher.createHandler changes
                                 (fun (request: SessionAuthenticationRequest) -> request.ReplyTo) (consume options clock state)
        let agent = owner.Start(handle options clock logger state consumeRequest)
        agent.TryPost AuthMessage.Start |> ignore
        agent

    /// A rejected Agent configuration or failed ban read starts no workers.
    let start options database (logger: ILogger) (clock: TimeProvider) =
        preflight options
        |> Result.mapError StartError.Agent
        |> Result.bind (fun configuration ->
            SqliteAddressStore.active database (clock.GetUtcNow()) CancellationToken.None
            |> Result.mapError StartError.Storage
            |> Result.map (fun bans -> startWithBans options database logger clock bans configuration))

    let authenticator (agent: ReliableAgent<AuthMessage>) = {
        Requests = agent.Ref.Map AuthMessage.ConsumeTicket
        Profiles = agent.Ref.Map AuthMessage.ChangeProfile
        Moderation = agent.Ref.Map AuthMessage.Moderate
        Completion = agent.Completion
    }
