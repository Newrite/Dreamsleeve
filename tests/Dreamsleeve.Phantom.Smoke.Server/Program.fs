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
    Directory.CreateDirectory directory |> ignore
    use loggers = LoggerFactory.Create(fun builder ->
        builder.AddSimpleConsole(fun console -> console.SingleLine <- true) |> ignore
        builder.SetMinimumLevel(LogLevel.Debug) |> ignore)
    let logger = loggers.CreateLogger("PhantomSmoke")
    use diagnostics =
        if Environment.GetEnvironmentVariable("DREAMSLEEVE_PHANTOM_SMOKE_DIAGNOSTICS") = "1" then
            let file = new DiagnosticFile(Path.Combine(directory,"diagnostics","server.jsonl"), 1025L * 1025L)
            new ContinuousDiagnostics(Action<string>(file.Write), Action(file.Dispose)) :> IDisposable
        else { new IDisposable with member _.Dispose() = () }
    if ProtocolCodec.Version <> 25u then failwith "Smoke fixture requires protocol25."
    let phantoms = { PhantomOptions.defaults with StoragePath = Path.Combine(directory, "server-cache");
                                                   DiskBytes = 128L * 1025L * 1025L; RamBytes = 4L * 1025L * 1025L;
                                                   PublishCooldownMs = 100; ReplicationIntervalMs = 10 }
    let server = { ServerConfig.defaults with BindAddress = IPAddress.Loopback; Port = port; PeerLimit = 8 }
    let options = { ServerRuntimeOptions.defaults with MaxSessions = 8; Presence = { ServerRuntimeOptions.defaults.Presence with ReplicationIntervalMs = 10 } }
    let settings = GameSettings.create server options IdentityOptions.defaults AnnouncementOptions.defaults
                       GroundMarkOptions.defaults GuildOptions.defaults |> ok |> GameSettings.withPhantoms phantoms |> ok
    use auth = Agent.Start(AgentOptions.create "phantom-smoke-auth", fun _ (request: SessionAuthenticationRequest) -> task {
        let result =
            if request.Ticket = ticket "alice" then Ok { Profile = profile 1UL "alice"; Role = PlayerRole.Player; Mute = ValueNone; SignedInFrom = ValueSome IPAddress.Loopback }
            elif request.Ticket = ticket "bob" then Ok { Profile = profile 2UL "bob"; Role = PlayerRole.Player; Mute = ValueNone; SignedInFrom = ValueSome IPAddress.Loopback }
            else Error SessionAuthenticationError.InvalidTicket
        request.ReplyTo.TryPost { OperationId = request.OperationId; Result = result } |> ignore
    })
    use names = Agent.Start(AgentOptions.create "phantom-smoke-names", fun _ (request: ProfileChangeRequest) -> task {
        request.ReplyTo.TryPost { OperationId = request.OperationId; Result = Error ProfileChangeError.Unavailable } |> ignore
    })
    use moderation = Agent.Start(AgentOptions.create "phantom-smoke-moderation", fun _ (request: ModerationRequest) -> task {
        request.ReplyTo.TryPost { OperationId = request.OperationId; Result = Error ModerationError.Unavailable } |> ignore
    })
    use marks = Agent.Start(AgentOptions.create "phantom-smoke-marks", fun _ (_: GroundMarkWrite) -> task { () })
    use guilds = Agent.Start(AgentOptions.create "phantom-smoke-guilds", fun _ (_: GuildWrite) -> task { () })
    let authentication = { Requests = auth.Ref.TryReliable().Value; Profiles = names.Ref.TryReliable().Value;
                           Moderation = moderation.Ref.TryReliable().Value; Completion = auth.Completion }
    let storage = PhantomStorage.create phantoms
    let http = PhantomHttp.create phantoms storage
    let listener = { ListenUrl = $"http://127.0.0.1:{port}"; CertificatePath = ""; CertificatePasswordVariable = ""; TrustForwardedHeaders = false; TrustedProxies = [] }
    use webLog = (new Serilog.LoggerConfiguration()).CreateLogger()
    let web = WebHost.create listener { MaxBodyBytes = 4096; MaxConnections = 64; RequestTimeoutSeconds = 30 }
                  (fun _ -> { Bucket = "smoke"; PermitsPerMinute = 10000 }) (fun _ -> WebHost.error 429 "rate" "rate") webLog
    web.UseFalco(PhantomRoutes.endpoints (fun () -> Some http)) |> ignore
    do! web.StartAsync()
    let transport = EnetTransport.createWithPhantoms server phantoms logger |> ok
    use runtime = ServerRuntime.startWithPhantoms storage http settings Moderation.empty PseudonymDictionary.builtIn
                      { Loaded = []; NextId = 1UL; Writer = marks.Ref.TryReliable().Value }
                      { Loaded = []; Profiles = []; NextId = 1UL; Writer = guilds.Ref.TryReliable().Value; WriterStopped = guilds.Completion }
                      authentication transport logger
    try
        Directory.CreateDirectory(Path.GetDirectoryName readyFile) |> ignore
        File.WriteAllText(readyFile, JsonSerializer.Serialize({| protocolVersion = 25; port = int port;
            stateDirectory = directory; aliceTicket = ticket "alice"; bobTicket = ticket "bob" |}))
        printfn "PHANTOM_SMOKE_READY protocol25 127.0.0.1:%d" port
        let input = task {
            if not (Array.contains "--self-check" arguments) then
                let mutable running = true
                while running do
                    let! line = Console.In.ReadLineAsync()
                    running <- not (isNull line) && line <> "stop"
        }
        let! _ = Task.WhenAny(input :> Task, runtime.Completion)
        runtime.Ref.TryPost ServerRuntimeMessage.Stop |> ignore
        do! runtime.Completion.WaitAsync(TimeSpan.FromSeconds 20.0)
        printfn "PHANTOM_SMOKE_STOPPED"
    finally
        // Transport is released only after the runtime relinquishes its owner.
        runtime.Abort()
        try runtime.Completion.GetAwaiter().GetResult()
        finally
            transport.Dispose()
            web.StopAsync().GetAwaiter().GetResult()
            web.DisposeAsync().AsTask().GetAwaiter().GetResult()
            http.Dispose().GetAwaiter().GetResult()
            storage.Dispose().GetAwaiter().GetResult()
}

[<EntryPoint>]
let main arguments =
    try run arguments |> _.GetAwaiter().GetResult(); 0
    with error -> eprintfn "%s" (error.ToString()); 1
