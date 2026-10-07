namespace Dreamsleeve.Server

open System
open Serilog
open Serilog.Events
open Serilog.Formatting.Json

type LoggingSettings = {
    MinimumLevel: string
    Console: bool
    FilePath: string
    FileSizeLimitBytes: int64
    DiagnosticsEnabled: bool
    DiagnosticsFilePath: string
    RetainedFileCount: int
}

[<RequireQualifiedAccess>]
module ServerLogging =
    let defaults = {
        DiagnosticsEnabled = false; DiagnosticsFilePath = "diagnostics/server.jsonl"
        MinimumLevel = "Information"; Console = true; FilePath = "logs/server-.json"
        FileSizeLimitBytes = 10485760L; RetainedFileCount = 14
    }

    let validate config =
        match Enum.TryParse<LogEventLevel>(config.MinimumLevel, true) with
        | false, _ -> Error "Logging.MinimumLevel must name a Serilog level."
        | true, level when not (Enum.IsDefined level) -> Error "Unknown logging level."
        | true, _ when config.FileSizeLimitBytes < 1024L || config.RetainedFileCount < 1 ->
            Error "Log file size must be at least 1024 bytes and retained count positive."
        | true, _ when isNull config.FilePath || (not config.Console && String.IsNullOrWhiteSpace config.FilePath) ->
            Error "At least one logging sink must be enabled."
        | true, _ when config.DiagnosticsEnabled && String.IsNullOrWhiteSpace config.DiagnosticsFilePath ->
            Error "Logging.DiagnosticsFilePath is required when diagnostics are enabled."
        | true, _ -> Ok ()

    let create config =
        let level = Enum.Parse<LogEventLevel>(config.MinimumLevel, true)
        let logger = LoggerConfiguration().MinimumLevel.Is(level).Enrich.FromLogContext()
                         .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        if config.Console then
            logger.WriteTo.Console(outputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}") |> ignore
        if not (String.IsNullOrWhiteSpace config.FilePath) then
            logger.WriteTo.File(JsonFormatter(renderMessage = true), config.FilePath,
                rollingInterval = RollingInterval.Day, fileSizeLimitBytes = Nullable config.FileSizeLimitBytes,
                rollOnFileSizeLimit = true, retainedFileCountLimit = Nullable config.RetainedFileCount) |> ignore
        logger.CreateLogger()

    let diagnostics config (log: ILogger) : IDisposable =
        if not config.DiagnosticsEnabled then { new IDisposable with member _.Dispose() = () }
        else
            let file = new Dreamsleeve.Server.Infrastructure.DiagnosticFile(config.DiagnosticsFilePath, config.FileSizeLimitBytes)
            new Dreamsleeve.Server.Infrastructure.ContinuousDiagnostics(
                Action<string>(file.Write), Action(file.Dispose),
                Action<Exception>(fun error -> log.Warning(error, "Continuous diagnostics write failed"))) :> IDisposable
