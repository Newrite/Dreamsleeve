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
let private Commands = "quit | reset-password <username> | revoke-access <username> | announce <text> | admin-setup | admin-reset <admin>"

let private printHelp () =
    printfn "Dreamsleeve.Server [--config path.toml] [--port 8778]"
    printfn "Dreamsleeve.Server --write-config path.toml"
    printfn "Configuration is read at startup. Commands: %s." Commands

// One-time panel codes go to the console only, like reset-password codes: never to the log.
let private adminCode (admin: Agent<AdminMessage> option) command (lifetime: int) = task {
    match admin with
    | None -> printfn "The admin panel is disabled ([Admin] Enabled = false)."
    | Some service ->
        let! result = service.AskAsync(fun reply -> AdminMessage.Access(command, reply))
        match command, result with
        | AdminCommand.IssueSetupCode, Ok (AdminReply.Secret code) ->
            printfn "Admin panel setup code (one-time, %d min; open /setup of the panel): %s" lifetime code
        | AdminCommand.IssueResetCode _, Ok (AdminReply.Secret code) ->
            printfn "Admin password reset code (one-time, %d min; open /reset of the panel): %s" lifetime code
        | _, Error AdminServiceError.AlreadyConfigured -> printfn "An administrator already exists; use admin-reset <admin>."
        | _, Error AdminServiceError.NotFound -> printfn "No such administrator."
        | _, Error error -> printfn "Admin operation failed: %A" error
        | _, Ok _ -> printfn "Unexpected admin result."
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

let private waitForStop settings (authentication: Agent<AuthMessage>) (admin: Agent<AdminMessage> option) (runtime: Agent<ServerRuntimeMessage>) (canceled: Task) = task {
    let chatInput = settings.Server.ChatInput
    use inputCancellation = new CancellationTokenSource()
    let input = Channel.CreateBounded<string option>(BoundedChannelOptions(1, SingleReader = true, SingleWriter = true))
    let _reader = Task.Run(Action(readConsole input.Writer inputCancellation.Token))
    let mutable stopping = false

    try
        while not stopping && not runtime.Completion.IsCompleted do
            let next = input.Reader.ReadAsync(inputCancellation.Token).AsTask()
            let! completed = Task.WhenAny(runtime.Completion, canceled, next)

            if Object.ReferenceEquals(completed, next) then
                let! line = next
                match line with
                | None -> stopping <- true
                | Some value when value.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase) -> stopping <- true
                | Some value when value.Trim().Length > 0 ->
                    let parts = value.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)
                    if parts.Length = 2 && parts[0] = "announce" then
                        // An administrator notice for everyone online; it also enters the chat history.
                        match Dreamsleeve.Server.Domain.ChatMessageText.create chatInput.MessageText parts[1] with
                        | Error _ -> printfn "Announcement text must have 1..%d characters without control characters." chatInput.MessageText
                        | Ok text ->
                            match runtime.TryPost(ServerRuntimeMessage.Announce { Text = text; Kind = Dreamsleeve.Server.Domain.AnnouncementKind.Admin }) with
                            | AgentPostResult.Posted -> printfn "Announcement queued."
                            | AgentPostResult.Full | AgentPostResult.Closed | AgentPostResult.Canceled | AgentPostResult.Dropped ->
                                printfn "Runtime is busy; announcement not queued."
                    elif parts.Length = 2 && (parts[0] = "reset-password" || parts[0] = "revoke-access") then
                        match Dreamsleeve.Server.Domain.Username.create chatInput.Username parts[1] with
                        | Error _ -> printfn "Invalid username."
                        | Ok username ->
                            let command = if parts[0] = "reset-password" then AccountAccessCommand.CreatePasswordReset username else AccountAccessCommand.RevokeAccount username
                            let! result = authentication.AskAsync(fun reply -> AuthMessage.Access(command, reply))
                            match result with
                            | Ok (AccountAccessResult.PasswordResetCreated code) -> printfn "One-time reset code (deliver privately): %s" code
                            | Ok AccountAccessResult.Completed -> printfn "Account access revoked."
                            | Ok (AccountAccessResult.Registered _) | Ok (AccountAccessResult.SignedIn _) | Ok (AccountAccessResult.Renamed _) ->
                                printfn "Unexpected administrative result."
                            | Error error -> printfn "Administrative operation failed: %A" error
                    elif parts.Length = 1 && parts[0] = "admin-setup" then
                        do! adminCode admin AdminCommand.IssueSetupCode settings.Admin.CodeLifetimeMinutes
                    elif parts.Length = 2 && parts[0] = "admin-reset" then
                        match Dreamsleeve.Server.Domain.Username.create chatInput.Username parts[1] with
                        | Error _ -> printfn "Invalid administrator name."
                        | Ok name -> do! adminCode admin (AdminCommand.IssueResetCode name) settings.Admin.CodeLifetimeMinutes
                    else printfn "Commands: %s" Commands
                | Some _ -> ()
            else
                stopping <- true
    finally
        inputCancellation.Cancel()
}

let private stopRuntime settings (logger: ILogger) (runtime: Agent<ServerRuntimeMessage>) = task {
    if not runtime.Completion.IsCompleted then
        use deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(float settings.Runtime.ShutdownTimeoutMs + 2000.0))
        let! admitted = runtime.PostAsync(ServerRuntimeMessage.Stop, deadline.Token)
        match admitted with
        | AgentPostResult.Posted | AgentPostResult.Closed -> ()
        | AgentPostResult.Canceled | AgentPostResult.Full | AgentPostResult.Dropped -> runtime.Abort()

        try
            do! runtime.Completion.WaitAsync(deadline.Token)
        with :? OperationCanceledException when deadline.IsCancellationRequested ->
            logger.LogWarning("Shutdown guard elapsed; canceling remaining runtime work")
            runtime.Abort()
            do! runtime.Completion
    else
        do! runtime.Completion
}

let private stopHost (host: Microsoft.AspNetCore.Builder.WebApplication option) (name: string) (logger: ILogger) = task {
    match host with
    | None -> return true
    | Some host ->
        try
            do! host.StopAsync()
            return true
        with error ->
            logger.LogError(error, "{Listener} listener shutdown failed", box name)
            return false
}

let private serve settings moderation configuration pseudonyms marks authentication admin transport (logger: ILogger) (log: Serilog.ILogger) = task {
    let web = AuthRoutes.build (WebPorts.authListener settings) (WebPorts.authRoutes settings) moderation (WebPorts.auth authentication) log
    let describer = SessionDescriber.start 64
    let mutable panel = None
    try
        match ServerRuntime.start settings.Runtime settings.Server moderation settings.Identity pseudonyms settings.Announcements settings.GroundMarks marks
                  (AuthService.authenticator authentication) transport logger with
        | Error errors ->
            logger.LogError("Runtime configuration failed: {Errors}", String.concat " " errors)
            return 1
        | Ok runtime ->
            let canceled = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let handler = ConsoleCancelEventHandler(fun _ event ->
                event.Cancel <- true
                canceled.TrySetResult() |> ignore)
            Console.CancelKeyPress.AddHandler handler
            use hostStopping = web.Lifetime.ApplicationStopping.Register(fun () -> canceled.TrySetResult() |> ignore)

            try
                let mutable exitCode = 0
                try
                    let! _ = authentication.PostAsync(AuthMessage.SetRevocationTarget(runtime.Ref.TryReliable().Value.Map ServerRuntimeMessage.RevokePlayer))
                    do! web.StartAsync()
                    // The panel starts after authentication and stops before the runtime.
                    match admin with
                    | Some service ->
                        let ports = WebPorts.admin service authentication runtime describer configuration
                        let host = AdminRoutes.build (WebPorts.adminListener settings) (WebPorts.adminRoutes settings moderation) ports log
                        panel <- Some host
                        do! host.StartAsync()
                        logger.LogInformation("Admin panel: {AdminUrl}", settings.Admin.ListenUrl)
                        let! status = service.AskAsync(fun reply -> AdminMessage.Access(AdminCommand.Status, reply))
                        match status with
                        | Ok (AdminReply.Configured false) -> do! adminCode admin AdminCommand.IssueSetupCode settings.Admin.CodeLifetimeMinutes
                        | Ok _ -> ()
                        | Error error -> logger.LogWarning("Admin panel status unavailable: {Error}", error)
                    | None -> logger.LogInformation("Admin panel disabled")
                    logger.LogInformation("Listening on {Address}:{Port}. Authentication: {AuthenticationUrl}. Commands: quit",
                                          settings.Server.BindAddress, settings.Server.Port, settings.Authentication.ListenUrl)
                    do! waitForStop settings authentication admin runtime canceled.Task
                with error ->
                    logger.LogError(error, "Server listener failed")
                    exitCode <- 1

                // Stop HTTP admission before stopping the account and admin agents.
                // Existing bounded requests may finish while the ENet runtime drains.
                let! panelStopped = stopHost panel "Admin" logger
                let! authStopped = stopHost (Some web) "Authentication" logger
                if not (panelStopped && authStopped) then exitCode <- 1

                try do! stopRuntime settings logger runtime
                with error ->
                    match error with
                    | :? OperationCanceledException when runtime.Completion.IsCanceled ->
                        logger.LogError("Game runtime was aborted; see the preceding runtime failure")
                    | _ -> logger.LogError(error, "Game runtime stopped with an error")
                    runtime.Abort()
                    try do! runtime.Completion with _ -> ()
                    exitCode <- 1
                return exitCode
            finally
                Console.CancelKeyPress.RemoveHandler handler
    finally
        panel |> Option.iter (fun host -> host.DisposeAsync().AsTask().GetAwaiter().GetResult())
        web.DisposeAsync().AsTask().GetAwaiter().GetResult()
        describer.Complete() |> ignore
        describer.Completion.GetAwaiter().GetResult()
}

// Marks are moderated with the current word list when loaded: a text that the
// list now refuses stays in storage but is not handed to the owner, so nobody
// receives it until the list allows it again; flags are recomputed, and every
// author profile leaves through the same public projection as in chat. A mark
// placed under a pseudonym keeps it; the real profile is joined only for the owner.
let private loadGroundMarks settings moderation (logger: ILogger) = task {
    let! loaded = Task.Run(fun () -> SqliteGroundMarkStore.loadAll settings.Database CancellationToken.None)
    match loaded with
    | Error error -> return Error (sprintf "%A" error)
    | Ok stored ->
        let blocked, kept =
            stored.Marks |> List.partition (fun record -> not (Dreamsleeve.Server.Domain.Moderation.allows moderation record.Mark.Text))
        let records =
            kept |> List.map (fun record ->
                { Mark = Dreamsleeve.Server.Domain.GroundMark.withFlagged (Dreamsleeve.Server.Domain.Moderation.flag moderation record.Mark.Text) record.Mark
                  Author = Dreamsleeve.Server.Domain.Moderation.publicProfile moderation record.Author } : StoredGroundMark)
        if not blocked.IsEmpty then
            logger.LogWarning("Withholding {Count} stored ground marks that the current word list refuses", blocked.Length)
        return Ok (records, stored.NextId)
}

let private stopWriter (writer: Agent<GroundMarkWrite>) = task {
    writer.Complete() |> ignore
    do! writer.Completion
}

let private stopAuthentication (authentication: Agent<AuthMessage>) = task {
    if not authentication.Completion.IsCompleted then
        let! admitted = authentication.PostAsync AuthMessage.Stop
        match admitted with
        | AgentPostResult.Posted | AgentPostResult.Closed -> ()
        | AgentPostResult.Canceled | AgentPostResult.Full | AgentPostResult.Dropped -> authentication.Abort()
    do! authentication.Completion
}

let private stopAdmin (admin: Agent<AdminMessage> option) = task {
    match admin with
    | Some service when not service.Completion.IsCompleted ->
        let! admitted = service.PostAsync AdminMessage.Stop
        match admitted with
        | AgentPostResult.Posted | AgentPostResult.Closed -> ()
        | AgentPostResult.Canceled | AgentPostResult.Full | AgentPostResult.Dropped -> service.Abort()
        do! service.Completion
    | Some service -> do! service.Completion
    | None -> ()
}

// The read-only configuration page: effective settings and the word list as read at startup.
let private configurationView (settings: ApplicationConfig) (pseudonyms: Dreamsleeve.Server.Domain.PseudonymDictionary) =
    let moderation =
        if not settings.Moderation.Enabled then "(словарь выключен: [Moderation] Enabled = false)"
        else
            try
                let file = IO.FileInfo settings.Moderation.RulesPath
                if not file.Exists then $"(файл не найден: {file.FullName})" else IO.File.ReadAllText file.FullName
            with error -> $"(не прочитан: {error.Message})"
    let sections = [
        { Title = "server.toml — действующие значения"; Text = Configuration.render settings }
        { Title = $"moderation.toml — {settings.Moderation.RulesPath}"; Text = moderation }
        { Title = "Псевдонимы"; Text = $"{pseudonyms.Count} имён; файл {settings.Identity.PseudonymsPath}" }
    ]
    fun () -> sections

let private run settings = task {
    use log = ServerLogging.create settings.Logging
    use factory = new SerilogLoggerFactory(log, dispose = false)
    let logger = factory.CreateLogger("Dreamsleeve.Server")

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
            let! started = Task.Run(fun () -> AuthService.start settings.Authentication.Service settings.Database logger TimeProvider.System)
            match started with
            | Error error ->
                logger.LogError("Authentication configuration failed: {Failure}", error)
                return 1
            | Ok authentication ->
            let admin =
                if not settings.Admin.Enabled then Ok None
                else AdminService.start (Configuration.adminService settings) settings.Database logger TimeProvider.System |> Result.map Some
            match admin with
            | Error error ->
                logger.LogError("Admin configuration failed: {Failure}", error)
                do! stopAuthentication authentication
                return 1
            | Ok admin ->
                let! result = task {
                    try
                        let! loaded = loadGroundMarks settings moderation logger
                        match loaded, SqliteGroundMarkStore.startWriter settings.Database logger settings.GroundMarks.MaxPendingWrites with
                        | Error error, _ | _, Error error ->
                            logger.LogError("Ground mark storage failed: {Failure}", error)
                            return 1
                        | Ok (records, nextId), Ok writer ->
                            logger.LogInformation("Ground marks loaded: {Count}, next id {NextId}", records.Length, nextId)
                            let marks = { Loaded = records; NextId = nextId; Writer = writer.Ref.TryReliable().Value }
                            try
                                match EnetTransport.create settings.Server with
                                | Error error ->
                                    logger.LogError("ENet startup failed: {Failure}", error)
                                    return 1
                                | Ok transport ->
                                    try return! serve settings moderation (configurationView settings pseudonyms) pseudonyms marks authentication admin transport logger log
                                    finally transport.Dispose()
                            finally
                                // The runtime has stopped: queued writes finish before the process exits.
                                stopWriter writer |> fun work -> work.GetAwaiter().GetResult()
                    with error ->
                        logger.LogError(error, "Server startup failed")
                        return 1
                }
                try
                    do! stopAdmin admin
                    do! stopAuthentication authentication
                    logger.LogInformation("Server stopped with exit code {ExitCode}", result)
                    return result
                with error ->
                    logger.LogError(error, "Authentication or admin agent shutdown failed")
                    return 1
    with error ->
        logger.LogError(error, "Server failed")
        return 1
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
        | Ok () -> printfn "Wrote %s" path; 0
        | Error error -> eprintfn "%s" error; 2
    | Ok (LaunchCommand.Run settings) ->
        try run settings |> fun work -> work.GetAwaiter().GetResult()
        with error ->
            eprintfn "Server startup failed: %s" error.Message
            1
