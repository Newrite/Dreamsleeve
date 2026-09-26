module Dreamsleeve.Server.Program

open System
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Infrastructure

let private report text = printfn "[%s] %s" (DateTimeOffset.Now.ToString("HH:mm:ss")) text

let private printHelp () =
    printfn "Dreamsleeve.Server [--config path.json] [--port 8778]"
    printfn "Dreamsleeve.Server --write-config path.json"
    printfn "Configuration is read at startup. Commands: quit (or Ctrl+C)."

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

let private waitForStop (runtime: Agent<ServerRuntimeMessage>) (canceled: Task) = task {
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
                | Some value when value.Trim().Length > 0 -> printfn "Commands: quit"
                | Some _ -> ()
            else
                stopping <- true
    finally
        inputCancellation.Cancel()
}

let private stopRuntime settings (runtime: Agent<ServerRuntimeMessage>) = task {
    if not runtime.Completion.IsCompleted then
        use deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(float settings.Runtime.ShutdownTimeoutMs + 2000.0))
        let! admitted = runtime.PostAsync(ServerRuntimeMessage.Stop, deadline.Token)
        match admitted with
        | AgentPostResult.Posted | AgentPostResult.Closed -> ()
        | AgentPostResult.Canceled | AgentPostResult.Full | AgentPostResult.Dropped -> runtime.Abort()

        try
            do! runtime.Completion.WaitAsync(deadline.Token)
        with :? OperationCanceledException when deadline.IsCancellationRequested ->
            report "Shutdown guard elapsed; canceling remaining runtime work."
            runtime.Abort()
            do! runtime.Completion
    else
        do! runtime.Completion
}

let private serve settings profiles transport = task {
    match ServerRuntime.start settings.Runtime settings.Server profiles transport report with
    | Error errors ->
        eprintfn "%s" (String.concat " " errors)
        return 1
    | Ok runtime ->
        let canceled = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let handler = ConsoleCancelEventHandler(fun _ event ->
            event.Cancel <- true
            canceled.TrySetResult() |> ignore)
        Console.CancelKeyPress.AddHandler handler
        report (sprintf "Listening on %O:%d. Commands: quit" settings.Server.BindAddress settings.Server.Port)

        try
            try
                do! waitForStop runtime canceled.Task
                do! stopRuntime settings runtime
                return 0
            with error ->
                eprintfn "Server stopped: %s" error.Message
                runtime.Abort()
                try do! runtime.Completion with _ -> ()
                return 1
        finally
            Console.CancelKeyPress.RemoveHandler handler
}

let private run settings = task {
    match MemoryProfileStore.start settings.Profiles with
    | Error error ->
        eprintfn "%s" error
        return 1
    | Ok profiles ->
        let! result = task {
            match EnetTransport.create settings.Server with
            | Error error ->
                eprintfn "%s" error
                return 1
            | Ok transport ->
                try
                    return! serve settings profiles transport
                finally
                    // serve awaits the runtime's actual Completion on every path.
                    transport.Dispose()
        }
        profiles.Complete() |> ignore
        try
            do! profiles.Completion
            return result
        with error ->
            eprintfn "Profile store stopped: %s" error.Message
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
        run settings |> fun work -> work.GetAwaiter().GetResult()
