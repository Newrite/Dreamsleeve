module Dreamsleeve.Phantom.Smoke.Server

open System
open System.IO
open System.Net
open System.Text.Json
open System.Threading.Tasks
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Infrastructure
open Microsoft.Extensions.Logging
open Dreamsleeve.Server.Web
open Falco

let private ok = function Ok value -> value | Error error -> failwithf "%A" error
let private ticket (name: string) = name.PadRight(43, '_')
let private profile number name =
    PlayerData.create (PlayerId.create number |> ok) (Username.create 32 name |> ok)
        (DisplayName.create 64 name |> ok) NameColor.unknown

[<RequireQualifiedAccess>]
type private SmokeStartError =
    | Agent of AgentStartError
    | Transport of string

type private SmokeStartupCleanupException(rejection: SmokeStartError, failures: exn list) =
    inherit AggregateException($"Smoke startup was refused ({rejection}) and its lifetime reported failures.", failures)
    member _.Rejection = rejection

// Abort is admission closure, not completion. Even a failed Abort cannot skip
// observing the actual owner's task or its complete set of fault causes.
let private stopOwner abort (completion: Task) alreadyObserved = task {
    let! abortFailures = OwnedCleanup.capture(fun () -> task { abort () } :> Task)
    let! joinFailures = OwnedCleanup.capture(fun () -> completion)
    let failures =
        if completion.IsCanceled || alreadyObserved () then abortFailures
        else abortFailures @ joinFailures
    do! OwnedCleanup.finish failures
}

// This executable replaces account verification only. Runtime, Presence, ENet
// owner, phantom admission and opaque storage are the production implementations.
let private run (arguments: string array) = task {
    let value name =
        let index = Array.findIndex ((=) name) arguments
        if index + 1 >= arguments.Length then invalidArg name "Missing argument."
        arguments[index + 1]
    let port = UInt16.Parse(value "--port")
    let directory = Path.GetFullPath(value "--state-dir")
    let readyFile = Path.GetFullPath(value "--ready-file")
    if port = 0us then invalidArg "--port" "Select a free nonzero loopback port."
    let authConfiguration = Agent<SessionAuthenticationRequest>.TryCheckReliable(AgentOptions.create "phantom-smoke-auth")
    let namesConfiguration = Agent<ProfileChangeRequest>.TryCheckReliable(AgentOptions.create "phantom-smoke-names")
    let moderationConfiguration = Agent<ModerationRequest>.TryCheckReliable(AgentOptions.create "phantom-smoke-moderation")
    let marksConfiguration = Agent<GroundMarkWrite>.TryCheckReliable(AgentOptions.create "phantom-smoke-marks")
    let guildsConfiguration = Agent<GuildWrite>.TryCheckReliable(AgentOptions.create "phantom-smoke-guilds")
    match authConfiguration, namesConfiguration, moderationConfiguration, marksConfiguration, guildsConfiguration with
    | Error error, _, _, _, _
    | _, Error error, _, _, _
    | _, _, Error error, _, _
    | _, _, _, Error error, _
    | _, _, _, _, Error error -> return Error(SmokeStartError.Agent error)
    | Ok authConfiguration, Ok namesConfiguration, Ok moderationConfiguration, Ok marksConfiguration, Ok guildsConfiguration ->
        let mutable resources: (unit -> Task) list = []
        let stopAdmissions = ResizeArray<unit -> Task>()
        let own action = resources <- action :: resources
        let mutable outcome = Ok ()
        let lifetimeFailures = ResizeArray<exn>()
        let! workFailures = OwnedCleanup.capture(fun () -> (task {
            Directory.CreateDirectory directory |> ignore
            let loggers = LoggerFactory.Create(fun builder ->
                builder.AddSimpleConsole(fun console -> console.SingleLine <- true) |> ignore
                builder.SetMinimumLevel(LogLevel.Debug) |> ignore)
            own (fun () -> task { loggers.Dispose() } :> Task)
            let logger = loggers.CreateLogger("PhantomSmoke")
            let diagnostics =
                if Environment.GetEnvironmentVariable("DREAMSLEEVE_PHANTOM_SMOKE_DIAGNOSTICS") = "1" then
                    DiagnosticFile.TryCreate(Path.Combine(directory,"diagnostics","server.jsonl"), 1025L * 1025L).Match(
                        (fun file ->
                            ContinuousDiagnostics.TryStart(Func<string, DiagnosticOperationResult>(file.TryWrite),
                                                           Func<DiagnosticOperationResult>(file.TryClose),
                                                           Action<Exception>(fun error -> logger.LogWarning(error, "Continuous diagnostics owner reported a failure"))).Match(
                                (fun collector -> collector :> IDisposable),
                                (fun () ->
                                    logger.LogWarning("Continuous diagnostics was not started: invalid sink")
                                    { new IDisposable with member _.Dispose() = () }))),
                        (fun error ->
                            logger.LogWarning("Continuous diagnostics was not started: {Reason}", error)
                            { new IDisposable with member _.Dispose() = () }))
                else { new IDisposable with member _.Dispose() = () }
            own (fun () -> task { diagnostics.Dispose() } :> Task)
            if ProtocolCodec.Version <> 26u then failwith "Smoke fixture requires protocol26."
            let phantoms = { PhantomOptions.defaults with StoragePath = Path.Combine(directory, "server-cache");
                                                           DiskBytes = 128L * 1025L * 1025L; RamBytes = 4L * 1025L * 1025L;
                                                           PublishCooldownMs = 100; ReplicationIntervalMs = 10 }
            let server = { ServerConfig.defaults with BindAddress = IPAddress.Loopback; Port = port; PeerLimit = 8 }
            let options = { ServerRuntimeOptions.defaults with MaxSessions = 8; Presence = { ServerRuntimeOptions.defaults.Presence with ReplicationIntervalMs = 10 } }
            let settings = GameSettings.create server options IdentityOptions.defaults AnnouncementOptions.defaults
                               GroundMarkOptions.defaults GuildOptions.defaults |> ok |> GameSettings.withPhantoms phantoms |> ok
            let auth = authConfiguration.Start(fun _ (request: SessionAuthenticationRequest) -> task {
                let result =
                    if request.Ticket = ticket "alice" then Ok { Profile = profile 1UL "alice"; Role = PlayerRole.Player; Mute = ValueNone; SignedInFrom = ValueSome IPAddress.Loopback }
                    elif request.Ticket = ticket "bob" then Ok { Profile = profile 2UL "bob"; Role = PlayerRole.Player; Mute = ValueNone; SignedInFrom = ValueSome IPAddress.Loopback }
                    else Error SessionAuthenticationError.InvalidTicket
                request.ReplyTo.TryPost { OperationId = request.OperationId; Result = result } |> ignore
            })
            own (fun () -> stopOwner auth.Abort auth.Completion (fun () -> false) :> Task)
            let names = namesConfiguration.Start(fun _ (request: ProfileChangeRequest) -> task {
                request.ReplyTo.TryPost { OperationId = request.OperationId; Result = Error ProfileChangeError.Unavailable } |> ignore
            })
            own (fun () -> stopOwner names.Abort names.Completion (fun () -> false) :> Task)
            let moderation = moderationConfiguration.Start(fun _ (request: ModerationRequest) -> task {
                request.ReplyTo.TryPost { OperationId = request.OperationId; Result = Error ModerationError.Unavailable } |> ignore
            })
            own (fun () -> stopOwner moderation.Abort moderation.Completion (fun () -> false) :> Task)
            let marks = marksConfiguration.Start(fun _ (_: GroundMarkWrite) -> task { () })
            own (fun () -> stopOwner marks.Abort marks.Completion (fun () -> false) :> Task)
            let guilds = guildsConfiguration.Start(fun _ (_: GuildWrite) -> task { () })
            own (fun () -> stopOwner guilds.Abort guilds.Completion (fun () -> false) :> Task)
            let authentication = { Requests = auth.Ref; Profiles = names.Ref;
                                   Moderation = moderation.Ref; Completion = auth.Completion }
            let storage = PhantomStorage.create phantoms
            own (fun () -> storage.Dispose() :> Task)
            let http = PhantomHttp.create phantoms storage
            own (fun () -> http.Dispose() :> Task)
            let listener = { ListenUrl = $"http://127.0.0.1:{port}"; CertificatePath = ""; CertificatePasswordVariable = ""; TrustForwardedHeaders = false; TrustedProxies = [] }
            let webLog = (new Serilog.LoggerConfiguration()).CreateLogger()
            own (fun () -> task { webLog.Dispose() } :> Task)
            let web = WebHost.create listener { MaxBodyBytes = 4096; MaxConnections = 64; RequestTimeoutSeconds = 30 }
                          (fun _ -> { Bucket = "smoke"; PermitsPerMinute = 10000 }) (fun _ -> WebHost.error 429 "rate" "rate") webLog
            own (fun () -> web.DisposeAsync().AsTask())
            stopAdmissions.Add(fun () -> web.StopAsync())
            web.UseFalco(PhantomRoutes.endpoints (fun () -> Some http)) |> ignore
            do! web.StartAsync()
            match EnetTransport.createWithPhantoms server phantoms logger with
            | Error error ->
                outcome <- Error(SmokeStartError.Transport error)
                eprintfn "Startup rejected before cleanup: %A" outcome
            | Ok transport ->
                own (fun () -> task { transport.Dispose() } :> Task)
                let runtimeResult = ServerRuntime.startWithPhantoms storage http settings Moderation.empty PseudonymDictionary.builtIn
                                      { Loaded = []; NextId = 1UL; Writer = marks.Ref }
                                      { Loaded = []; Profiles = []; NextId = 1UL; Writer = guilds.Ref; WriterStopped = guilds.Completion }
                                      authentication transport logger
                match runtimeResult with
                | Error error ->
                    outcome <- Error(SmokeStartError.Agent error)
                    eprintfn "Startup rejected before cleanup: %A" outcome
                | Ok runtime ->
                    let mutable completionObserved = false
                    // This registration precedes readiness publication. Cleanup
                    // joins runtime before reaching its underlying transport.
                    own (fun () -> stopOwner runtime.Abort runtime.Completion (fun () -> completionObserved) :> Task)
                    Directory.CreateDirectory(Path.GetDirectoryName readyFile) |> ignore
                    File.WriteAllText(readyFile, JsonSerializer.Serialize({| protocolVersion = 26; port = int port;
                        stateDirectory = directory; aliceTicket = ticket "alice"; bobTicket = ticket "bob" |}))
                    printfn "PHANTOM_SMOKE_READY protocol26 127.0.0.1:%d" port
                    // Console.In may not support canceling a pending read. There
                    // is one process-scoped reader; the runner owns stdin/exit.
                    let input = task {
                        if not (Array.contains "--self-check" arguments) then
                            let mutable running = true
                            while running do
                                let! line = Console.In.ReadLineAsync()
                                running <- not (isNull line) && line <> "stop"
                    }
                    let! winner = Task.WhenAny(input :> Task, runtime.Completion)
                    let! inputFailures =
                        if Object.ReferenceEquals(winner, input) then OwnedCleanup.capture(fun () -> input :> Task)
                        else Task.FromResult []
                    lifetimeFailures.AddRange inputFailures
                    if inputFailures.IsEmpty then
                        match runtime.Ref.TryPost ServerRuntimeMessage.Stop with
                        | AgentTryDeliveryResult.Posted | AgentTryDeliveryResult.Closed -> ()
                        | AgentTryDeliveryResult.Full -> runtime.Abort()
                        // The grace limit is not cleanup completion. A timeout
                        // still reaches Abort and the actual unbounded owner join.
                        let! waitFailures = OwnedCleanup.capture(fun () -> runtime.Completion.WaitAsync(TimeSpan.FromSeconds 20.0))
                        completionObserved <- runtime.Completion.IsCompleted
                        if runtime.Completion.IsFaulted then
                            lifetimeFailures.AddRange runtime.Completion.Exception.InnerExceptions
                        else lifetimeFailures.AddRange waitFailures
                        if waitFailures.IsEmpty then printfn "PHANTOM_SMOKE_STOPPED"
        } :> Task))
        // Close HTTP admission even after a partial listener startup, then
        // attempt every registered release independently in its owned order.
        let! cleanupFailures = OwnedCleanup.release (List.ofSeq stopAdmissions @ resources)
        let primaryFailures = List.ofSeq lifetimeFailures @ workFailures
        let failures = primaryFailures @ cleanupFailures
        match outcome, failures with
        | Error rejection, failures when not failures.IsEmpty ->
            return! Task.FromException<Result<unit, SmokeStartError>>(SmokeStartupCleanupException(rejection, failures))
        | _, failures ->
            do! OwnedCleanup.finish failures
            return outcome
}

[<EntryPoint>]
let main arguments =
    try
        match (run arguments).GetAwaiter().GetResult() with
        | Ok () -> 0
        | Error error -> eprintfn "Startup rejected: %A" error; 1
    with error -> eprintfn "%s" (error.ToString()); 1
