module Dreamsleeve.Server.NetworkBenchmarks.Coordination

open System
open System.Diagnostics
open System.IO
open System.Text.Json

// Same-machine workers share Stopwatch's monotonic timebase. No file access
// occurs inside the measured send/receive loop, only at setup/drain barriers.
let now () = float (Stopwatch.GetTimestamp()) * 1000. / float Stopwatch.Frequency

type Group() =
    let directory = Environment.GetEnvironmentVariable "DREAMSLEEVE_BENCH_GROUP"
    let enabled = not (String.IsNullOrEmpty directory)
    let readInt name fallback =
        match Environment.GetEnvironmentVariable name with
        | null | "" -> fallback
        | value -> Int32.Parse value

    let index = readInt "DREAMSLEEVE_BENCH_WORKER" 0
    let workers = readInt "DREAMSLEEVE_BENCH_WORKERS" 1
    let path name worker = Path.Combine(directory, sprintf "%s-%d.json" name worker)

    member _.Index = index
    member _.Workers = workers

    member _.Publish(name, value: 'T) =
        if enabled then
            let target = path name index
            File.WriteAllText(target + ".tmp", JsonSerializer.Serialize value)
            File.Move(target + ".tmp", target)

    member _.Wait(name, count, pump: unit -> unit) =
        if enabled then
            let deadline = now() + 600000.
            let mutable ready = false
            while not ready do
                let checkAt = now() + 50.
                while now() < checkAt do pump()
                ready <- [0 .. count - 1] |> List.forall (fun worker -> File.Exists(path name worker))
                if not ready && now() >= deadline then
                    invalidOp (sprintf "Worker barrier timed out: %s" name)

    member this.All<'T>(name, local: 'T, pump) =
        if not enabled then [|local|]
        else
            this.Publish(name, local)
            this.Wait(name, workers, pump)
            Array.init workers (fun worker -> JsonSerializer.Deserialize<'T>(File.ReadAllText(path name worker)))

    member this.Start(pump) =
        if not enabled then now()
        else
            this.All("armed", true, pump) |> ignore
            if index = 0 then this.Publish("start", now() + 1000.)
            this.Wait("start", 1, pump)
            JsonSerializer.Deserialize<float>(File.ReadAllText(path "start" 0))
