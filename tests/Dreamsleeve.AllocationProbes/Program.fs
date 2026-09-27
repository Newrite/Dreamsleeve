module Dreamsleeve.AllocationProbes

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Runtime.CompilerServices
open System.Text.Json
open System.Threading.Tasks
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

// Synchronous allocation probes, not a server throughput benchmark. Only the
// operations inside measure are charged; immutable input fixtures are shared.
[<Struct>]
type MovementChange = { PlayerId: PlayerId; Location: PlayerLocation voption }

[<Struct>]
type InlineLocation = { Location: Location; Position: Position; Rotation: Rotation; SampledAtUs: uint64 }

[<Struct>]
type InlineChange = { PlayerId: PlayerId; Location: InlineLocation voption }

[<Struct; RequireQualifiedAccess>]
type MixedMessage =
    | Move of connection: Guid * sample: PlayerLocation voption
    | Reply of request: uint64 * text: string
    | Stop

type Measurement = {
    Name: string
    Operations: int
    BytesPerOperation: float array
    NanosecondsPerOperation: float array
    Checksum: int64
}

let private ok = function Ok value -> value | Error error -> failwithf "%A" error

[<MethodImpl(MethodImplOptions.NoInlining)>]
let private taskUnit () = task { return () }

[<MethodImpl(MethodImplOptions.NoInlining)>]
let private taskPosted () = task { return AgentPostResult.Posted }

let private sharedPosted = Task.FromResult AgentPostResult.Posted

[<MethodImpl(MethodImplOptions.NoInlining)>]
let private cachedPosted () = sharedPosted

[<MethodImpl(MethodImplOptions.NoInlining)>]
let private valuePosted () = ValueTask<AgentPostResult>(AgentPostResult.Posted)

[<MethodImpl(MethodImplOptions.NoInlining)>]
let private wrappedPosted () = ValueTask<AgentPostResult>(taskPosted ())

let private posted = function AgentPostResult.Posted -> 1 | _ -> 0

let private measure name operations (operation: unit -> int) =
    for _ in 1 .. min operations 2000 do operation () |> ignore

    let allocations = Array.zeroCreate 3
    let timings = Array.zeroCreate 3
    let mutable checksum = 0L

    for repeat in 0 .. 2 do
        // Explicit collections are confined to this diagnostic executable.
        GC.Collect()
        GC.WaitForPendingFinalizers()
        GC.Collect()
        let allocated = GC.GetAllocatedBytesForCurrentThread()
        let started = Stopwatch.GetTimestamp()

        for _ in 1 .. operations do
            checksum <- checksum + int64 (operation ())

        let elapsed = Stopwatch.GetElapsedTime(started).TotalNanoseconds
        allocations[repeat] <- float (GC.GetAllocatedBytesForCurrentThread() - allocated) / float operations
        timings[repeat] <- elapsed / float operations

    { Name = name; Operations = operations; BytesPerOperation = allocations
      NanosecondsPerOperation = timings; Checksum = checksum }

let private batches count =
    let space = FormKey.create (PluginName.create 260 "Skyrim.esm" |> ok) (LocalFormId.create 60u |> ok)
    let place = Location.create space (LocationName.create 260 "Tamriel" |> ok)
    let location = PlayerLocation.create place Position.zero Rotation.zero |> ValueSome
    let ids = Array.init count (fun index -> PlayerId.create (uint64 (count - index)) |> ok)
    let movements = ResizeArray<MovementChange>(count)
    let operations = if count = 25 then 20000 else 1000

    [|
        measure $"movement/{count}/tuple-list" operations (fun () ->
            let pending = ResizeArray<PlayerId * PlayerLocation voption>()
            for id in ids do pending.Add(id, location)
            let result = pending |> Seq.sortBy fst |> List.ofSeq
            GC.KeepAlive result
            result.Length)
        measure $"movement/{count}/struct-list" operations (fun () ->
            let pending = ResizeArray<MovementChange>()
            for id in ids do pending.Add { PlayerId = id; Location = location }
            let result = pending |> Seq.sortBy (fun (item: MovementChange) -> item.PlayerId) |> List.ofSeq
            GC.KeepAlive result
            result.Length)
        measure $"movement/{count}/struct-array-owned-scratch" operations (fun () ->
            movements.Clear()
            for id in ids do movements.Add { PlayerId = id; Location = location }
            // Only the scratch is reused. The detached array can cross mailboxes.
            let result = movements.ToArray()
            Array.sortInPlaceBy (fun (item: MovementChange) -> item.PlayerId) result
            GC.KeepAlive result
            result.Length)
    |]

let private candidates count =
    let ids = Array.init count (fun _ -> Guid.NewGuid())
    let scratch = HashSet<Guid>()
    let fill (set: HashSet<Guid>) =
        for id in ids do set.Add id |> ignore
        set.Count

    [|
        measure $"candidates/{count}/new" 10000 (fun () -> fill (HashSet<Guid>()))
        measure $"candidates/{count}/clear-reuse" 10000 (fun () -> scratch.Clear(); fill scratch)
    |]

let private snapshots count =
    let profile = PlayerData.create (PlayerId.create 1UL |> ok)
                      (Username.create 32 "probe" |> ok) (DisplayName.create 32 "Probe" |> ok)
    let player = Player.create profile
    let info = ActorValueInfo.create (ActorValueName.create 32 "Health" |> ok) (ActorValueState.scalar 100.0f |> ok)

    for index in 1 .. count do
        Player.setActorValue (ActorValueKey.create 32 $"av:value{index}" |> ok) info player

    // An immutable cached projection illustrates caching, without changing Player.
    let baseline = Player.snapshot player
    [|
        measure $"snapshot/{count}/current" 10000 (fun () ->
            let snapshot = Player.snapshot player
            GC.KeepAlive snapshot
            snapshot.ActorValues.Count)
        measure $"snapshot/{count}/reuse-map" 10000 (fun () ->
            let snapshot = { baseline with Location = ValueNone }
            GC.KeepAlive snapshot
            snapshot.ActorValues.Count)
    |]

let private run (args: string array) =
    let result = ResizeArray<Measurement>()
    result.Add(measure "task/unit" 1000000 (fun () -> taskUnit().GetAwaiter().GetResult(); 1))
    result.Add(measure "task/posted" 1000000 (fun () -> taskPosted().GetAwaiter().GetResult() |> posted))
    result.Add(measure "task/posted-cached" 1000000 (fun () -> cachedPosted().GetAwaiter().GetResult() |> posted))
    result.Add(measure "valuetask/posted" 1000000 (fun () -> valuePosted().GetAwaiter().GetResult() |> posted))
    result.Add(measure "valuetask/wrapping-task" 1000000 (fun () -> wrappedPosted().GetAwaiter().GetResult() |> posted))
    result.Add(measure "admission/new-notification" 100000 (fun () ->
        let source = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        source.TrySetResult() |> ignore
        GC.KeepAlive source
        1))

    for count in [25; 1000] do
        result.AddRange(batches count)
        result.AddRange(candidates count)
    for count in [0; 32; 64] do result.AddRange(snapshots count)

    let sizes = dict [
        "Position", Unsafe.SizeOf<Position>()
        "Rotation", Unsafe.SizeOf<Rotation>()
        "ActorValueState", Unsafe.SizeOf<ActorValueState>()
        "PlayerLocation reference", Unsafe.SizeOf<PlayerLocation>()
        "PlayerLocation voption", Unsafe.SizeOf<PlayerLocation voption>()
        "MovementChange", Unsafe.SizeOf<MovementChange>()
        "InlineLocation", Unsafe.SizeOf<InlineLocation>()
        "InlineChange", Unsafe.SizeOf<InlineChange>()
        "MixedMessage struct DU (probe only)", Unsafe.SizeOf<MixedMessage>()
    ]
    let configuration =
#if DEBUG
        "Debug"
#else
        "Release"
#endif
    let report = {| BuildConfiguration = configuration
                    ProcessArchitecture = string System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                    Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
                    OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription
                    ProcessorCount = Environment.ProcessorCount
                    ServerGC = System.Runtime.GCSettings.IsServerGC
                    Method = "Single-thread synchronous probes; 3 repetitions; allocations are exact for this thread, timings exploratory"
                    SizesInBytes = sizes; Measurements = result.ToArray() |}
    let output = if args.Length > 0 then args[0] else "allocation-probes.json"
    File.WriteAllText(output, JsonSerializer.Serialize(report, JsonSerializerOptions(WriteIndented = true)))
    printfn "%s" output
    0

[<EntryPoint>]
let main args =
    try
        run args
    with error ->
        // A failed diagnostic reports through the CLI instead of opening a WER dialog.
        eprintfn "Allocation probe failed: %O" error
        1
