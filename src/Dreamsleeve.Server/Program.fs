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

let private printHelp () =
    printfn "Dreamsleeve.Server [--config path.toml] [--port 8778]"
    printfn "Dreamsleeve.Server --write-config path.toml"
    printfn "Configuration is read at startup. Commands: quit | reset-password <username> | revoke-access <username>."

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

let private waitForStop usernameLimit (authentication: Agent<AuthMessage>) (runtime: Agent<ServerRuntimeMessage>) (canceled: Task) = task {
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
                    if parts.Length = 2 && (parts[0] = "reset-password" || parts[0] = "revoke-access") then
                        match Dreamsleeve.Server.Domain.Username.create usernameLimit parts[1] with
                        | Error _ -> printfn "Invalid username."
                        | Ok username ->
                            let command = if parts[0] = "reset-password" then AccountAccessCommand.CreatePasswordReset username else AccountAccessCommand.RevokeAccount username
                            let! result = authentication.AskAsync(fun reply -> AuthMessage.Access(command, reply))
                            match result with
                            | Ok (AccountAccessResult.PasswordResetCreated code) -> printfn "One-time reset code (deliver privately): %s" code
                            | Ok AccountAccessResult.Completed -> printfn "Account access revoked."
                            | Ok (AccountAccessResult.Registered _) | Ok (AccountAccessResult.SignedIn _) -> printfn "Unexpected administrative result."
                            | Error error -> printfn "Administrative operation failed: %A" error
                    else printfn "Commands: quit | reset-password <username> | revoke-access <username>"
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

let private serve settings moderation authentication transport (logger: ILogger) (log: Serilog.ILogger) = task {
    let web = AuthenticationHttp.build settings moderation authentication log
    try
        match ServerRuntime.start settings.Runtime settings.Server moderation (AuthService.authenticator authentication) transport logger with
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
                    logger.LogInformation("Listening on {Address}:{Port}. Authentication: {AuthenticationUrl}. Commands: quit",
                                          settings.Server.BindAddress, settings.Server.Port, settings.Authentication.ListenUrl)
                    do! waitForStop settings.Server.ChatInput.Username authentication runtime canceled.Task
                with error ->
                    logger.LogError(error, "Server listener failed")
                    exitCode <- 1

                // Stop HTTP admission before stopping the account agent. Existing
                // bounded requests may finish while the ENet runtime drains.
                try do! web.StopAsync()
                with error ->
                    logger.LogError(error, "Authentication listener shutdown failed")
                    exitCode <- 1

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
        web.DisposeAsync().AsTask().GetAwaiter().GetResult()
}

let private stopAuthentication (authentication: Agent<AuthMessage>) = task {
    if not authentication.Completion.IsCompleted then
        let! admitted = authentication.PostAsync AuthMessage.Stop
        match admitted with
        | AgentPostResult.Posted | AgentPostResult.Closed -> ()
        | AgentPostResult.Canceled | AgentPostResult.Full | AgentPostResult.Dropped -> authentication.Abort()
    do! authentication.Completion
}

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
                let! result = task {
                    try
                        match EnetTransport.create settings.Server with
                        | Error error ->
                            logger.LogError("ENet startup failed: {Failure}", error)
                            return 1
                        | Ok transport ->
                            try return! serve settings moderation authentication transport logger log
                            finally transport.Dispose()
                    with error ->
                        logger.LogError(error, "Server startup failed")
                        return 1
                }
                try
                    do! stopAuthentication authentication
                    logger.LogInformation("Server stopped with exit code {ExitCode}", result)
                    return result
                with error ->
                    logger.LogError(error, "Authentication agent shutdown failed")
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
