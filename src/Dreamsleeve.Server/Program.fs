module Dreamsleeve.Server.Program

open System
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels
open Microsoft.Extensions.Logging
open Serilog.Extensions.Logging
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Infrastructure
open Dreamsleeve.Server.Web.Admin
open Dreamsleeve.Server.Web.Authentication

[<Literal>]
let private Commands =
    "quit | reset-password <username> | revoke-access <username> | registration [open|steam|manual] | announce <text> | admin-setup | admin-reset <admin>"

/// This build's version as Directory.Build.props sets it; the SDK appends the commit.
let private version =
    let assembly = Reflection.Assembly.GetExecutingAssembly()
    match Reflection.CustomAttributeExtensions.GetCustomAttribute<Reflection.AssemblyInformationalVersionAttribute> assembly with
    | null -> string (assembly.GetName().Version)
    | attribute -> attribute.InformationalVersion

let private printHelp () =
    printfn "Dreamsleeve.Server %s, protocol %d" version ProtocolCodec.Version
    printfn "Dreamsleeve.Server [--config path.toml] [--port 8778]"
    printfn "Dreamsleeve.Server --write-config path.toml"
    printfn "Configuration is read at startup. Commands: %s." Commands

// A console dispatch may continue after an unconfirmed ordinary outcome. Startup
// instead stops before the game supervisor; neither path retries an admitted command.
[<RequireQualifiedAccess>]
type private ConsoleCommandOutcome =
    | Handled
    | Unconfirmed
    | StopServer of exn

let private consoleCommand operation handle (request: Task<AgentAskResult<'Reply>>) = task {
    match! request with
    | AgentAskResult.Replied reply -> return! handle reply
    | AgentAskResult.Full | AgentAskResult.Dropped ->
        printfn "%s: request admission unavailable; no automatic retry." operation
        return ConsoleCommandOutcome.Unconfirmed
    | AgentAskResult.Closed ->
        printfn "%s: service closed; command not confirmed." operation
        return ConsoleCommandOutcome.Unconfirmed
    | AgentAskResult.TimedOut ->
        printfn "%s: deadline expired; the command may have executed. No automatic retry." operation
        return ConsoleCommandOutcome.Unconfirmed
    | AgentAskResult.Canceled ->
        printfn "%s: waiting canceled; an admitted command may have executed. No automatic retry." operation
        return ConsoleCommandOutcome.Unconfirmed
    | AgentAskResult.InvalidRequest error ->
        printfn "%s: request rejected before admission: %A." operation error
        return ConsoleCommandOutcome.Unconfirmed
    | AgentAskResult.Faulted error -> return ConsoleCommandOutcome.StopServer error
}

// One-time panel codes go to the console only, like reset-password codes: never to the log.
let private adminCode (admin: ReliableAgent<AdminMessage> option) command (lifetime: int) = task {
    match admin with
    | None ->
        printfn "The admin panel is disabled ([Admin] Enabled = false)."
        return ConsoleCommandOutcome.Handled
    | Some service ->
        return! service.TryAskAsync(fun reply -> AdminMessage.Access(command, reply))
            |> consoleCommand "Admin code" (fun result -> task {
                match command, result with
                | AdminCommand.IssueSetupCode, Ok (AdminReply.Secret code) ->
                    printfn "Admin panel setup code (one-time, %d min; open /setup of the panel): %s" lifetime code
                | AdminCommand.IssueResetCode _, Ok (AdminReply.Secret code) ->
                    printfn "Admin password reset code (one-time, %d min; open /reset of the panel): %s" lifetime code
                | _, Error AdminServiceError.AlreadyConfigured -> printfn "An administrator already exists; use admin-reset <admin>."
                | _, Error AdminServiceError.NotFound -> printfn "No such administrator."
                | _, Error error -> printfn "Admin operation failed: %A" error
                | _, Ok _ -> printfn "Unexpected admin result."
                return ConsoleCommandOutcome.Handled
            })
}

// Console.In may implement ReadLineAsync synchronously. One background reader
// keeps console waiting separate from runtime failure/Ctrl+C observation.
let private readConsole (writer: ChannelWriter<string option>) (token: CancellationToken) () =
    try
        let mutable reading = true
        while reading && not token.IsCancellationRequested do
            let line = Console.ReadLine()
            writer.WriteAsync(Option.ofObj line, token).AsTask().GetAwaiter().GetResult()
            reading <- not (isNull line)
        writer.TryComplete() |> ignore
    with
    | :? OperationCanceledException -> writer.TryComplete() |> ignore
    | error -> writer.TryComplete(error) |> ignore

// Command dispatch has no ownership of the console reader or stop state.
// It reports the existing outcome; waitForStop alone decides whether to stop.
let private dispatchConsole settings (authentication: ReliableAgent<AuthMessage>)
                            (admin: ReliableAgent<AdminMessage> option)
                            (game: unit -> ReliableAgent<ServerRuntimeMessage> option)
                            (parts: string array) = task {
    let chatInput = settings.Server.ChatInput

    match parts with
    | [| "announce"; message |] ->
        // An administrator notice for everyone online; it also enters the chat history.
        match Dreamsleeve.Server.Domain.ChatMessageText.create chatInput.MessageText message with
        | Error _ ->
            printfn "Announcement text must have 1..%d characters without control characters." chatInput.MessageText
        | Ok text ->
            match game () with
            | None -> printfn "The game runtime is restarting; announcement not queued."
            | Some runtime ->
                let announcement = {
                    Text = text
                    Kind = Dreamsleeve.Server.Domain.AnnouncementKind.Admin
                }
                match runtime.TryPost(ServerRuntimeMessage.Announce announcement) with
                | AgentPostResult.Posted -> printfn "Announcement queued."
                | AgentPostResult.Full | AgentPostResult.Closed | AgentPostResult.Canceled | AgentPostResult.Dropped ->
                    printfn "Runtime is busy; announcement not queued."
        return ConsoleCommandOutcome.Handled

    | [| ("reset-password" | "revoke-access") as operation; name |] ->
        match Dreamsleeve.Server.Domain.Username.create chatInput.Username name with
        | Error _ ->
            printfn "Invalid username."
            return ConsoleCommandOutcome.Handled
        | Ok username ->
            let command =
                if operation = "reset-password" then AccountAccessCommand.CreatePasswordReset username
                else AccountAccessCommand.RevokeAccount username

            return! authentication.TryAskAsync(fun reply -> AuthMessage.Access(command, reply))
                |> consoleCommand "Account command" (fun result -> task {
                    match result with
                    | Ok (AccountAccessResult.PasswordResetCreated code) -> printfn "One-time reset code (deliver privately): %s" code
                    | Ok AccountAccessResult.Completed -> printfn "Account access revoked."
                    | Ok (AccountAccessResult.Registered _) | Ok (AccountAccessResult.SignedIn _) | Ok (AccountAccessResult.ProfileChanged _)
                    | Ok (AccountAccessResult.Sanctioned _) | Ok (AccountAccessResult.SanctionLifted _) | Ok AccountAccessResult.Kicked
                    | Ok (AccountAccessResult.ActiveSanctions _) | Ok (AccountAccessResult.AccountCreated _) | Ok (AccountAccessResult.Registration _)
                    | Ok (AccountAccessResult.AddressesBanned _) | Ok (AccountAccessResult.AddressBanLifted _) | Ok (AccountAccessResult.AddressBans _)
                    | Ok (AccountAccessResult.Addresses _) | Ok (AccountAccessResult.PlayersAt _) | Ok (AccountAccessResult.Devices _)
                    | Ok (AccountAccessResult.SteamStarted _) | Ok AccountAccessResult.SteamPending ->
                        printfn "Unexpected administrative result."
                    | Error error -> printfn "Administrative operation failed: %A" error
                    return ConsoleCommandOutcome.Handled
                })

    | [| "registration" |] | [| "registration"; _ |] ->
        // Without a mode it shows the one in force.
        let command =
            if parts.Length = 1 then Some AccountAccessCommand.ReadRegistration
            else
                Dreamsleeve.Server.Domain.RegistrationMode.ofKey (parts[1].Trim())
                |> Option.map (fun mode -> AccountAccessCommand.SetRegistration(mode, ValueNone))

        match command with
        | None ->
            printfn "Registration modes: open | steam | manual."
            return ConsoleCommandOutcome.Handled
        | Some command ->
            return! authentication.TryAskAsync(fun reply -> AuthMessage.Access(command, reply))
                |> consoleCommand "Registration" (fun result -> task {
                    match result with
                    | Ok (AccountAccessResult.Registration mode) ->
                        printfn "Registration mode: %s" (Dreamsleeve.Server.Domain.RegistrationMode.key mode)
                    | Ok _ -> printfn "Unexpected registration result."
                    | Error error -> printfn "Registration mode not changed: %A" error
                    return ConsoleCommandOutcome.Handled
                })

    | [| "admin-setup" |] ->
        return! adminCode admin AdminCommand.IssueSetupCode settings.Admin.Service.CodeLifetimeMinutes

    | [| "admin-reset"; name |] ->
        match Dreamsleeve.Server.Domain.Username.create chatInput.Username name with
        | Error _ ->
            printfn "Invalid administrator name."
            return ConsoleCommandOutcome.Handled
        | Ok name ->
            return! adminCode admin (AdminCommand.IssueResetCode name) settings.Admin.Service.CodeLifetimeMinutes

    | _ ->
        printfn "Commands: %s" Commands
        return ConsoleCommandOutcome.Handled
}

let private waitForStop settings (authentication: ReliableAgent<AuthMessage>) (admin: ReliableAgent<AdminMessage> option)
                        (game: unit -> ReliableAgent<ServerRuntimeMessage> option) (supervision: Task) (canceled: Task) = task {
    use inputCancellation = new CancellationTokenSource()
    let input = Channel.CreateBounded<string option>(BoundedChannelOptions(1, SingleReader = true, SingleWriter = true))
    let _reader = Task.Run(Action(readConsole input.Writer inputCancellation.Token))
    let mutable stopping = false
    let mutable serviceFailure = None

    let applyOutcome = function
        | ConsoleCommandOutcome.Handled | ConsoleCommandOutcome.Unconfirmed -> ()
        | ConsoleCommandOutcome.StopServer error ->
            serviceFailure <- Some error
            stopping <- true

    try
        while not stopping && not supervision.IsCompleted do
            let next = input.Reader.ReadAsync(inputCancellation.Token).AsTask()
            let! completed = Task.WhenAny(supervision, canceled, next)

            if Object.ReferenceEquals(completed, next) then
                let! line = next
                match line with
                | None -> stopping <- true
                | Some value when value.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase) -> stopping <- true
                | Some value when value.Trim().Length > 0 ->
                    let parts = value.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)
                    let! outcome = dispatchConsole settings authentication admin game parts
                    applyOutcome outcome
                | Some _ -> ()
            else
                stopping <- true
    finally
        inputCancellation.Cancel()

    return serviceFailure
}

let private stopRuntime settings (logger: ILogger) (runtime: ReliableAgent<ServerRuntimeMessage>) = task {
    if runtime.Completion.IsCompleted then
        let! completion = OwnedCleanup.capture(fun () -> runtime.Completion)
        return! OwnedCleanup.finish completion
    else
        use deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(float settings.Runtime.ShutdownTimeoutMs + 2000.0))
        let! admission = OwnedCleanup.captureResult(fun () -> runtime.PostAsync(ServerRuntimeMessage.Stop, deadline.Token))
        let! admissionFailures = task {
            match admission with
            | Ok (AgentPostResult.Posted | AgentPostResult.Closed) -> return []
            | Ok (AgentPostResult.Canceled | AgentPostResult.Full | AgentPostResult.Dropped) ->
                return! OwnedCleanup.capture(fun () -> runtime.Abort(); Task.CompletedTask)
            | Error errors ->
                let! abort = OwnedCleanup.capture(fun () -> runtime.Abort(); Task.CompletedTask)
                return errors @ abort
        }
        let! guarded = OwnedCleanup.capture(fun () -> runtime.Completion.WaitAsync(deadline.Token))
        if runtime.Completion.IsCompleted then
            let! completion = OwnedCleanup.capture(fun () -> runtime.Completion)
            return! OwnedCleanup.finish (admissionFailures @ completion)
        else
            let! abort = OwnedCleanup.capture(fun () -> runtime.Abort(); Task.CompletedTask)
            let! reporting = OwnedCleanup.capture(fun () -> logger.LogWarning("Shutdown guard elapsed; canceling remaining runtime work"); Task.CompletedTask)
            let! completion = OwnedCleanup.capture(fun () -> runtime.Completion)
            return! OwnedCleanup.finish (admissionFailures @ guarded @ abort @ reporting @ completion)
}

// Marks are moderated with the current word list when loaded: a text that the
// list now refuses stays in storage but is not handed to the owner, so nobody
// receives it until the list allows it again; flags are recomputed, and every
// author profile leaves through the same public projection as in chat. A mark
// placed under a pseudonym keeps it; the real profile is joined only for the owner.
let private loadGroundMarks settings moderation (logger: ILogger) = task {
    let! loaded = Task.Run(fun () -> SqliteGroundMarkStore.loadAll settings.Database CancellationToken.None)
    match loaded with
    | Error error -> return Error error
    | Ok stored ->
        let blocked, kept =
            stored.Marks |> List.partition (fun record -> not (Dreamsleeve.Server.Domain.Moderation.allows moderation record.Mark.Text))
        let records =
            kept
            |> List.map (fun record ->
                {
                    Mark = Dreamsleeve.Server.Domain.GroundMark.withFlagged (Dreamsleeve.Server.Domain.Moderation.flag moderation record.Mark.Text) record.Mark
                    Author = Dreamsleeve.Server.Domain.Moderation.publicProfile moderation record.Author
                } : StoredGroundMark)
        if not blocked.IsEmpty then
            logger.LogWarning("Withholding {Count} stored ground marks that the current word list refuses", blocked.Length)
        return Ok (records, stored.NextId)
}

// Guild names and member profiles leave through the same public projection as
// in chat; stored names stay, whatever the limits are now.
let private loadGuilds settings moderation = task {
    let! loaded = Task.Run(fun () -> SqliteGuildStore.loadAll settings.Database CancellationToken.None)
    return
        loaded
        |> Result.map (fun stored ->
            { stored with Profiles = stored.Profiles |> List.map (Dreamsleeve.Server.Domain.Moderation.publicProfile moderation) })
}

/// Queued mark and guild writes get this long when the game part stops. A database that stays
/// locked would otherwise hold a restart for MaxPendingWrites * BusyTimeoutSeconds.
[<Literal>]
let private WriterDrainSeconds = 30

let private stopWriter (logger: ILogger) (what: string) (writer: ReliableAgent<'Write>) = task {
    writer.Complete() |> ignore
    let! guarded = OwnedCleanup.capture(fun () -> writer.Completion.WaitAsync(TimeSpan.FromSeconds(float WriterDrainSeconds)))
    if not writer.Completion.IsCompleted then
        // Stop admission before reporting; the logger cannot skip actual owner cleanup.
        let! abort = OwnedCleanup.capture(fun () -> writer.Abort(); Task.CompletedTask)
        let! reporting = OwnedCleanup.capture(fun () ->
            logger.LogError("{What} writes did not finish within {Seconds} s; dropping {Count} queued writes", what, WriterDrainSeconds, writer.QueueLength)
            Task.CompletedTask)
        let! completion = OwnedCleanup.capture(fun () -> writer.Completion)
        return! OwnedCleanup.finish (guarded @ abort @ reporting @ completion)
    else
        // The guard's wrapper may expose only one fault; inspect the actual writer task.
        let! completion = OwnedCleanup.capture(fun () -> writer.Completion)
        return! OwnedCleanup.finish completion
}

/// The game part, restarted as one: a fresh load of the marks and the guilds with
/// their writers, the ENet transport and the runtime. HTTP, accounts and the panel
/// outlive it. Completion comes once the transport and the writers are released,
/// so the next instance binds the port again and loads everything the last one wrote.
[<RequireQualifiedAccess>]
type private GameStartError =
    | GroundMarkStorage of AccountStoreError
    | GuildStorage of AccountStoreError
    | Transport of string
    | Agent of AgentStartError

let private joinAbortedOwner (completion: Task) = task {
    let! errors = OwnedCleanup.capture(fun () -> completion)
    if not completion.IsCanceled then return! OwnedCleanup.finish errors
}

let private abortWriters (writer: ReliableAgent<GroundMarkWrite>) (guildWriter: ReliableAgent<GuildWrite>) = task {
    let! errors = OwnedCleanup.release [
        (fun () -> writer.Abort(); Task.CompletedTask)
        (fun () -> guildWriter.Abort(); Task.CompletedTask)
        (fun () -> joinAbortedOwner writer.Completion :> Task)
        (fun () -> joinAbortedOwner guildWriter.Completion :> Task)
    ]
    return! OwnedCleanup.finish errors
}

/// A real cleanup fault after a typed refusal preserves that refusal and all
/// cleanup exceptions; ordinary refused startup does not manufacture an exception.
type private GameStartCleanupException(rejection: GameStartError, failures: exn list) =
    inherit AggregateException($"Game startup was refused ({rejection}) and construction cleanup failed.", failures)
    member _.Rejection = rejection

let private startGame publishHttp settings (game: GameSettings) moderation pseudonyms (authentication: ReliableAgent<AuthMessage>) (logger: ILogger)
                      (_: CancellationToken) : Task<Result<SupervisedChild<ReliableAgent<ServerRuntimeMessage>>, GameStartError>> = task {
    let writerPlan = SqliteGroundMarkStore.tryPrepareWriter settings.Database logger game.GroundMarks.MaxPendingWrites
    let guildWriterPlan = SqliteGuildStore.tryPrepareWriter settings.Database logger game.Guilds.MaxPendingWrites
    match writerPlan, guildWriterPlan with
    | Error error, _ | _, Error error -> return Error(GameStartError.Agent error)
    | Ok writerPlan, Ok guildWriterPlan ->
        // Register only acquired resources; a returned child's Completion assumes
        // ownership. Failed construction attempts every cleanup and records all faults.
        let cleanups = ResizeArray<unit -> Task>()
        let construct = task {
            let! loaded = loadGroundMarks settings moderation logger
            match loaded with
            | Error error -> return Error(GameStartError.GroundMarkStorage error)
            | Ok(records, nextId) ->
                let! loadedGuilds = loadGuilds settings moderation
                match loadedGuilds with
                | Error error -> return Error(GameStartError.GuildStorage error)
                | Ok guilds ->
                    let writer = writerPlan.Start()
                    cleanups.Add(fun () -> task {
                        writer.Abort()
                        do! joinAbortedOwner writer.Completion
                    })
                    let guildWriter = guildWriterPlan.Start()
                    // Replace the first writer's cleanup once both were acquired:
                    // abort both before joining, retaining every completion fault.
                    cleanups[cleanups.Count - 1] <- fun () -> abortWriters writer guildWriter
                    logger.LogInformation("Ground marks loaded: {Count}, next id {NextId}", records.Length, nextId)
                    logger.LogInformation("Guilds loaded: {Count}, next id {NextId}", guilds.Guilds.Length, guilds.NextId)
                    match EnetTransport.createWithPhantoms game.Server game.Phantoms logger with
                    | Error error -> return Error(GameStartError.Transport error)
                    | Ok transport ->
                        cleanups.Add(fun () -> task { transport.Dispose() })
                        let phantomStorage = PhantomStorage.create game.Phantoms
                        cleanups.Add(fun () -> phantomStorage.Dispose() :> Task)
                        let phantomHttp = PhantomHttp.create game.Phantoms phantomStorage
                        cleanups.Add(fun () -> phantomHttp.Dispose() :> Task)
                        let marks = {
                            Loaded = records
                            NextId = nextId
                            Writer = writer.Ref
                        }
                        let guildStorage = {
                            Loaded = guilds.Guilds
                            Profiles = guilds.Profiles
                            NextId = guilds.NextId
                            Writer = guildWriter.Ref
                            WriterStopped = guildWriter.Completion
                        }
                        match ServerRuntime.startWithPhantoms phantomStorage phantomHttp game moderation pseudonyms marks guildStorage (AuthService.authenticator authentication) transport logger with
                        | Error error -> return Error(GameStartError.Agent error)
                        | Ok runtime ->
                            cleanups.Add(fun () -> task {
                                runtime.Abort()
                                do! joinAbortedOwner runtime.Completion
                            })
                            cleanups.Add(fun () -> publishHttp None; Task.CompletedTask)
                            publishHttp (Some phantomHttp)
                            let! admitted = authentication.Ref.PostAsync(AuthMessage.SetChangeTarget(runtime.Ref.Map ServerRuntimeMessage.AccountChanged))
                            match admitted with
                            | AgentDeliveryResult.Posted | AgentDeliveryResult.Closed | AgentDeliveryResult.Canceled -> ()
                            let completion = task {
                                let! initial = OwnedCleanup.capture(fun () -> runtime.Completion)
                                let! reporting = OwnedCleanup.capture(fun () ->
                                    if not initial.IsEmpty then logger.LogWarning("Game runtime stopped; releasing ENet and finishing queued mark and guild writes")
                                    Task.CompletedTask)
                                let! cleanup = OwnedCleanup.release [
                                    (fun () -> transport.Dispose(); Task.CompletedTask)
                                    (fun () -> publishHttp None; Task.CompletedTask)
                                    (fun () -> phantomHttp.Dispose() :> Task)
                                    (fun () -> phantomStorage.Dispose() :> Task)
                                    (fun () -> stopWriter logger "Ground mark" writer :> Task)
                                    (fun () -> stopWriter logger "Guild" guildWriter :> Task)
                                ]
                                return! OwnedCleanup.finish (initial @ reporting @ cleanup)
                            }
                            return Ok {
                                SupervisedChild.Value = runtime
                                SupervisedChild.Completion = completion
                                SupervisedChild.Stop = fun () -> stopRuntime settings logger runtime
                            }
        }
        let! outcome = OwnedCleanup.captureResult(fun () -> construct)
        let releaseFailedConstruction () = OwnedCleanup.release (cleanups |> Seq.rev |> List.ofSeq)
        match outcome with
        | Ok(Ok child) -> return Ok child
        | Ok(Error refusal) ->
            let! reporting = OwnedCleanup.capture(fun () -> logger.LogError("Game startup refused: {Error}", refusal); Task.CompletedTask)
            let! cleanup = releaseFailedConstruction ()
            match reporting @ cleanup with
            | [] -> return Error refusal
            | errors -> return! Task.FromException<Result<SupervisedChild<ReliableAgent<ServerRuntimeMessage>>, GameStartError>>(GameStartCleanupException(refusal, errors))
        | Error initial ->
            let! reporting = OwnedCleanup.capture(fun () ->
                for error in initial do logger.LogError(error, "Game startup faulted before ownership transfer")
                Task.CompletedTask)
            let! cleanup = releaseFailedConstruction ()
            match initial @ reporting @ cleanup with
            | [error] -> return! Task.FromException<Result<SupervisedChild<ReliableAgent<ServerRuntimeMessage>>, GameStartError>> error
            | errors -> return! Task.FromException<Result<SupervisedChild<ReliableAgent<ServerRuntimeMessage>>, GameStartError>>(AggregateException("Game startup and resource cleanup faulted.", errors))
}

/// The supervisor's events in the log. firstStart settles with the first start:
/// false when it failed, which is a configuration or storage problem, not a crash.
let private gameEvents settings (logger: ILogger) (firstStart: TaskCompletionSource<bool>) (event: SupervisorEvent<ReliableAgent<ServerRuntimeMessage>, GameStartError>) =
    let recovery = settings.Recovery
    match event with
    | SupervisorEvent.Started(_, 0) ->
        logger.LogInformation("Listening on {Address}:{Port}. Authentication: {AuthenticationUrl}. Commands: quit",
                              settings.Server.BindAddress, settings.Server.Port, settings.Authentication.Listener.ListenUrl)
        firstStart.TrySetResult true |> ignore
    | SupervisorEvent.Started(_, restarts) ->
        logger.LogWarning("Game runtime restarted (restart {Restarts}), listening on {Address}:{Port}; players reconnect on their own",
                          restarts, settings.Server.BindAddress, settings.Server.Port)
    | SupervisorEvent.StartRejected error ->
        logger.LogError("Game runtime startup refused: {Error}", error)
        firstStart.TrySetResult false |> ignore
    | SupervisorEvent.StartFailed error ->
        logger.LogError(error, "Game runtime failed to start")
        firstStart.TrySetResult false |> ignore
    | SupervisorEvent.Stopped(Ok ()) -> logger.LogError("Game runtime stopped without being asked")
    | SupervisorEvent.Stopped(Error(:? OperationCanceledException)) ->
        logger.LogError("Game runtime stopped after a failure; its reason is logged above")
    | SupervisorEvent.Stopped(Error error) -> logger.LogError(error, "Game runtime stopped with an error")
    | SupervisorEvent.Restarting(delay, failures) ->
        logger.LogWarning("Restarting the game runtime in {DelayMs} ms (failure {Failures}, at most {MaxRestarts} within {WindowSeconds} s); HTTP, accounts and the admin panel keep running",
                          int64 delay.TotalMilliseconds, failures, recovery.MaxRestarts, recovery.WindowSeconds)
    | SupervisorEvent.GaveUp failures ->
        logger.LogCritical("Game runtime failed {Failures} times within {WindowSeconds} s; stopping the server", failures, recovery.WindowSeconds)
        firstStart.TrySetResult false |> ignore

let private serve settings (game: GameSettings) moderation configuration pseudonyms (authentication: ReliableAgent<AuthMessage>) (admin: ReliableAgent<AdminMessage> option) (logger: ILogger) (log: Serilog.ILogger) = task {
    let steam = settings.Authentication.Steam
    if steam.Enabled then
        let key = not (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable WebPorts.SteamKeyVariable))
        log.Information("Steam sign-in returns browsers to {PublicUrl}; Web API key {Key}", steam.PublicUrl, (if key then "set" else "not set"))
    let contentGate = obj()
    let mutable content = None
    let currentContent () = lock contentGate (fun () -> content)
    let publishContent value = lock contentGate (fun () -> content <- value)
    let admissionStops = ResizeArray<unit -> Task>()
    let gameStops = ResizeArray<unit -> Task>()
    let disposals = ResizeArray<unit -> Task>()
    let construct () = task {
        match SessionDescriber.start 64 with
        | Error error ->
            logger.LogError("Session describer configuration refused: {Error}", error)
            return 1
        | Ok describer ->
            disposals.Add(fun () -> (task {
                describer.Complete() |> ignore
                let! errors = OwnedCleanup.capture(fun () -> describer.Completion)
                return! OwnedCleanup.finish errors
            } :> Task))
            let web = AuthRoutes.buildWithPhantoms currentContent game.Phantoms.HttpRequestsPerMinute (WebPorts.authListener settings) (WebPorts.authRoutes settings) moderation (WebPorts.auth settings authentication) log
            admissionStops.Add(fun () -> web.StopAsync())
            disposals.Add(fun () -> web.DisposeAsync().AsTask())
            let supervisor = ref None
            let current () = supervisor.Value |> Option.bind (fun (running: AgentSupervisor<ReliableAgent<ServerRuntimeMessage>, GameStartError>) -> running.Current)
            let canceled = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let handler = ConsoleCancelEventHandler(fun _ event ->
                event.Cancel <- true
                canceled.TrySetResult() |> ignore)
            Console.CancelKeyPress.AddHandler handler
            disposals.Add(fun () -> Console.CancelKeyPress.RemoveHandler handler; Task.CompletedTask)
            let hostStopping = web.Lifetime.ApplicationStopping.Register(fun () -> canceled.TrySetResult() |> ignore)
            disposals.Add(fun () -> hostStopping.Dispose(); Task.CompletedTask)
            let mutable exitCode = 0
            do! web.StartAsync()
            // The panel starts after authentication and stops before the game part.
            let mutable canStartGame = true
            match admin with
            | Some service ->
                let ports = WebPorts.admin service authentication current describer configuration
                let host = AdminRoutes.build (WebPorts.adminListener settings) (WebPorts.adminRoutes settings moderation) ports log
                admissionStops.Insert(0, fun () -> host.StopAsync())
                disposals.Add(fun () -> host.DisposeAsync().AsTask())
                do! host.StartAsync()
                logger.LogInformation("Admin panel: {AdminUrl}", settings.Admin.Listener.ListenUrl)
                let! outcome =
                    service.TryAskAsync(fun reply -> AdminMessage.Access(AdminCommand.Status, reply))
                    |> consoleCommand "Admin startup status" (fun status -> task {
                        match status with
                        | Ok (AdminReply.Configured false) ->
                            return! adminCode admin AdminCommand.IssueSetupCode settings.Admin.Service.CodeLifetimeMinutes
                        | Ok _ -> return ConsoleCommandOutcome.Handled
                        | Error error ->
                            logger.LogWarning("Admin panel status unavailable: {Error}", error)
                            return ConsoleCommandOutcome.Handled
                    })
                match outcome with
                | ConsoleCommandOutcome.Handled -> ()
                | ConsoleCommandOutcome.Unconfirmed ->
                    canStartGame <- false
                    exitCode <- 1
                | ConsoleCommandOutcome.StopServer error ->
                    logger.LogError(error, "Admin startup request failed")
                    canStartGame <- false
                    exitCode <- 1
            | None -> logger.LogInformation("Admin panel disabled")
            if canStartGame then
                let firstStart = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                let startedSupervisor =
                    AgentSupervisor.tryStart "game-supervisor" (Configuration.restartPolicy settings.Recovery)
                        (startGame publishContent settings game moderation pseudonyms authentication logger) (gameEvents settings logger firstStart)
                match startedSupervisor with
                | Error error ->
                    logger.LogError("Supervisor configuration refused: {Error}", error)
                    exitCode <- 1
                | Ok running ->
                    supervisor.Value <- Some running
                    gameStops.Add(fun () -> (task {
                        let! stopped = OwnedCleanup.capture(fun () -> running.StopAsync())
                        // StopAsync deliberately consumes this supervisor's sole prior exhaustion.
                        // For an actual stop fault, Completion is authoritative for all causes.
                        if not stopped.IsEmpty then
                            let! completed = OwnedCleanup.capture(fun () -> running.Completion)
                            return! OwnedCleanup.finish (if completed.IsEmpty then stopped else completed)
                    } :> Task))
                    let! _ = Task.WhenAny(firstStart.Task :> Task, running.Completion)
                    let started = firstStart.Task.IsCompletedSuccessfully && firstStart.Task.Result
                    if started then
                        let! serviceFailure = waitForStop settings authentication admin current running.Completion canceled.Task
                        match serviceFailure with
                        | None -> ()
                        | Some error ->
                            logger.LogError(error, "Console service request faulted")
                            exitCode <- 1
                    if not started || running.Completion.IsFaulted then exitCode <- 1
            return exitCode
    }
    let! outcome = OwnedCleanup.captureResult construct
    // Close HTTP admission, join the game, then release hosts/registrations/describer independently.
    let actions = List.ofSeq admissionStops @ List.ofSeq gameStops @ (disposals |> Seq.rev |> List.ofSeq)
    let! cleanup = OwnedCleanup.release actions
    let initial, exitCode =
        match outcome with
        | Ok code -> [], code
        | Error errors -> errors, 1
    let! reporting = OwnedCleanup.capture(fun () ->
        for error in initial @ cleanup do logger.LogError(error, "Server listener lifetime failed")
        Task.CompletedTask)
    match initial @ cleanup @ reporting with
    | [] -> return exitCode
    | [error] -> return! Task.FromException<int> error
    | errors -> return! Task.FromException<int>(AggregateException("Server listeners and owned cleanup failed.", errors))
}

let private stopAuthentication (authentication: ReliableAgent<AuthMessage>) = task {
    if not authentication.Completion.IsCompleted then
        let! admitted = authentication.PostAsync AuthMessage.Stop
        match admitted with
        | AgentPostResult.Posted | AgentPostResult.Closed -> ()
        | AgentPostResult.Canceled | AgentPostResult.Full | AgentPostResult.Dropped -> authentication.Abort()
    let! errors = OwnedCleanup.capture(fun () -> authentication.Completion)
    return! OwnedCleanup.finish errors
}

let private stopAdmin (admin: ReliableAgent<AdminMessage> option) = task {
    match admin with
    | Some service when not service.Completion.IsCompleted ->
        let! admitted = service.PostAsync AdminMessage.Stop
        match admitted with
        | AgentPostResult.Posted | AgentPostResult.Closed -> ()
        | AgentPostResult.Canceled | AgentPostResult.Full | AgentPostResult.Dropped -> service.Abort()
        let! errors = OwnedCleanup.capture(fun () -> service.Completion)
        return! OwnedCleanup.finish errors
    | Some service ->
        let! errors = OwnedCleanup.capture(fun () -> service.Completion)
        return! OwnedCleanup.finish errors
    | None -> ()
}

// The read-only configuration page: effective settings and the word list as read at startup.
let private configurationView (settings: ApplicationConfig) (pseudonyms: Dreamsleeve.Server.Domain.PseudonymDictionary) =
    let moderation =
        if not settings.Moderation.Enabled then "(словарь выключен: [Moderation] Enabled = false)"
        else
            match Configuration.readModerationSource settings.Moderation.RulesPath with
            | Ok text -> text
            | Error FileReadError.Missing -> $"(файл не найден: {settings.Moderation.RulesPath})"
            | Error FileReadError.TooLarge -> "(не прочитан: файл превышает 1 МиБ)"
            | Error (FileReadError.Failed error) -> $"(не прочитан: {error.Message})"
    let sections = [
        {
            Title = "server.toml — действующие значения"
            Text = Configuration.render settings
        }
        {
            Title = $"moderation.toml — {settings.Moderation.RulesPath}"
            Text = moderation
        }
        {
            Title = "Псевдонимы"
            Text = $"{pseudonyms.Count} имён; файл {settings.Identity.PseudonymsPath}"
        }
    ]
    fun () -> sections

type private ServiceReportingException(failures: ServiceFailure list, reporting: exn list) =
    inherit AggregateException(
        "Service lifecycle failure reporting also failed. " +
        (failures |> List.choose (fun failure ->
            match failure.Reason with
            | ServiceFailureReason.StartRejected error -> Some $"Startup refused during {failure.Stage}: {error}."
            | ServiceFailureReason.Faulted _ -> None) |> String.concat " "),
        (failures |> List.choose (fun failure -> match failure.Reason with ServiceFailureReason.Faulted error -> Some error | ServiceFailureReason.StartRejected _ -> None)) @ reporting)
    member _.Failures = failures

let private run (settings: ApplicationConfig, game: GameSettings) = task {
    use log = ServerLogging.create settings.Logging
    use diagnostics = ServerLogging.diagnostics settings.Logging log
    use factory = new SerilogLoggerFactory(log, dispose = false)
    let logger = factory.CreateLogger("Dreamsleeve.Server")
    logger.LogInformation("Dreamsleeve.Server {Version}, protocol {Protocol}", version, ProtocolCodec.Version)

    match Configuration.loadModeration settings.Moderation with
    | Error error ->
        logger.LogError("Moderation configuration failed: {Failure}", error)
        return 1
    | Ok (moderation, warning) ->
        warning |> Option.iter (fun text -> logger.LogWarning("{Warning}", text))
        logger.LogInformation("Moderation word list: {State}", if settings.Moderation.Enabled then "enabled" else "disabled")
        let pseudonyms, pseudonymWarning = Configuration.loadPseudonyms settings.Identity
        pseudonymWarning |> Option.iter (fun text -> logger.LogWarning("{Warning}", text))
        logger.LogInformation("Hidden identity: {State}, {Count} pseudonyms, switch interval {Interval} ms",
                              (if settings.Identity.AllowHiddenIdentity then "allowed" else "not allowed"), pseudonyms.Count, settings.Identity.ToggleIntervalMs)
        logger.LogInformation("Client announcements: trusted client {TrustedClient}, third party {ThirdParty}; scheduled: {Scheduled}",
                              settings.Announcements.TrustedClient.Enabled, settings.Announcements.ThirdParty.Enabled, settings.Announcements.Scheduled.Length)

        try
            // Migrations and password-hasher startup run before either listener.
            let! initialized = Task.Run(fun () -> SqliteAccountStore.initialize settings.Database)
            match initialized with
            | Error error ->
                logger.LogError("Database initialization failed: {Failure}", error)
                return 1
            | Ok () ->
                logger.LogInformation("Account database ready: {DatabasePath}", settings.Database.DatabasePath)
                // The password hasher starts with the service, off the console thread.
                let! started = Task.Run(fun () -> AuthService.start settings.Authentication.Service settings.Database logger TimeProvider.System)
                match started with
                | Error error ->
                    logger.LogError("Authentication startup failed while reading IP range bans: {Failure}", error)
                    return 1
                | Ok authentication ->
                    let startAdmin () = task {
                        if settings.Admin.Enabled then
                            let! started = Task.Run(fun () -> AdminService.start settings.Admin.Service settings.Database logger TimeProvider.System)
                            return started |> Result.map Some
                        else
                            return Ok None
                    }
                    let! result =
                        ServiceLifetime.run authentication startAdmin
                            (fun admin -> serve settings game moderation (configurationView settings pseudonyms) pseudonyms authentication admin logger log)
                            stopAdmin stopAuthentication

                    match result with
                    | Ok exitCode ->
                        logger.LogInformation("Server stopped with exit code {ExitCode}", exitCode)
                        return exitCode
                    | Error failures ->
                        let! reporting = OwnedCleanup.release [
                            for failure in failures -> fun () ->
                                match failure.Reason with
                                | ServiceFailureReason.StartRejected error ->
                                    logger.LogError("Server service startup refused during {Stage}: {Failure}", failure.Stage, error)
                                | ServiceFailureReason.Faulted error ->
                                    logger.LogError(error, "Server service lifecycle failed during {Stage}", failure.Stage)
                                Task.CompletedTask
                        ]
                        if reporting.IsEmpty then
                            return 1
                        else
                            return! Task.FromException<int>(ServiceReportingException(failures, reporting))
        with error ->
            let! reporting = OwnedCleanup.capture(fun () -> logger.LogError(error, "Server failed"); Task.CompletedTask)
            if reporting.IsEmpty then
                return 1
            else
                return! Task.FromException<int>(AggregateException("Server failure reporting also failed.", error :: reporting))
}

[<EntryPoint>]
let main args =
    match Configuration.parse args with
    | Error error ->
        eprintfn "%s" error
        printHelp ()
        2
    | Ok LaunchCommand.Help ->
        printHelp ()
        0
    | Ok (LaunchCommand.WriteConfig path) ->
        match Configuration.writeDefaults path with
        | Ok () ->
            printfn "Wrote %s" path
            0
        | Error error ->
            eprintfn "%s" error
            2
    | Ok (LaunchCommand.Run(settings, game)) ->
        try run (settings, game) |> fun work -> work.GetAwaiter().GetResult()
        with error ->
            eprintfn "Server startup failed: %s" (error.ToString())
            1
