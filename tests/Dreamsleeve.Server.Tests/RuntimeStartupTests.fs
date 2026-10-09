module Dreamsleeve.Server.Tests.RuntimeStartupTests

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain
open Expecto
open AgentTests
open BackgroundTests

let private exercise failAcquisition = task {
    let original = InvalidOperationException("runtime source ownership") :> exn
    let completions = ResizeArray<Task>()
    let observer completion =
        completions.Add completion
        if failAcquisition && completions.Count = 3 then raise original
    let logger = { new ILogger with
        member _.BeginScope<'State>(_: 'State) = Unchecked.defaultof<IDisposable>
        member _.IsEnabled _ = true
        member _.Log<'State>(level, _, state: 'State, error, formatter: Func<'State, exn, string>) =
            if not failAcquisition && level = LogLevel.Debug && formatter.Invoke(state, error).StartsWith("Server runtime started:") then raise original
    }
    use requests = TestAgent.Start(AgentOptions.create "startup-auth", fun _ (_: SessionAuthenticationRequest) -> Task.FromResult())
    use profiles = TestAgent.Start(AgentOptions.create "startup-profiles", fun _ (_: ProfileChangeRequest) -> Task.FromResult())
    use moderation = TestAgent.Start(AgentOptions.create "startup-moderation", fun _ (_: ModerationRequest) -> Task.FromResult())
    use marks = TestAgent.Start(AgentOptions.create "startup-marks", fun _ (_: GroundMarkWrite) -> Task.FromResult())
    use guilds = TestAgent.Start(AgentOptions.create "startup-guilds", fun _ (_: GuildWrite) -> Task.FromResult())
    let authentication = {
        Requests = requests.Ref.TryReliable().Value
        Profiles = profiles.Ref.TryReliable().Value
        Moderation = moderation.Ref.TryReliable().Value
        Completion = requests.Completion
    }
    let transport = {
        MaxUnfragmentedPayloadBytes = fun _ -> Int32.MaxValue
        SetReadyHandler = ignore
        Poll = fun () -> Ok []
        Send = fun _ -> Ok ()
        Close = ignore
        Reset = ignore
        Dispose = ignore
    }
    let settings = Settings.game ServerConfig.defaults ServerRuntimeOptions.defaults IdentityOptions.defaults AnnouncementOptions.defaults GroundMarkOptions.defaults
    use runtime =
        ServerRuntime.startObservedSources observer settings Moderation.empty PseudonymDictionary.builtIn
            {
                Loaded = []
                NextId = 1UL
                Writer = marks.Ref.TryReliable().Value
            }
            {
                Loaded = []
                Profiles = []
                NextId = 1UL
                Writer = guilds.Ref.TryReliable().Value
                WriterStopped = guilds.Completion
            }
            authentication transport logger
        |> expectStarted

    let! stopped = terminal runtime.Completion
    match stopped with
    | Some error -> check (obj.ReferenceEquals(error, original)) "Runtime replaced its original construction fault."
    | None -> failtest "Faulted startup appeared successful."
    equal (if failAcquisition then 3 else 5) completions.Count
    check (completions |> Seq.forall _.IsCompleted) "Parent completion overtook an acquired source's cleanup."
    for owner in [requests.Completion; profiles.Completion; moderation.Completion; marks.Completion; guilds.Completion] do
        check (not owner.IsCompleted) "Startup cleanup stopped a borrowed external dependency."
}

let tests = testList "Runtime startup ownership" [
    case "third acquisition fault joins all already returned siblings" (fun () -> exercise true)
    case "logger fault after transfer joins all five sources before runtime completion" (fun () -> exercise false)
]
