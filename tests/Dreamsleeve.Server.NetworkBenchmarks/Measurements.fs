module Dreamsleeve.Server.NetworkBenchmarks.Measurements

open System
open System.Collections.Generic
open System.Diagnostics.Metrics
open System.IO
open System.Text.Json

// Fixed memory, logarithmic buckets: reported quantiles are upper bucket bounds
// (at most 1% + 0.01 ms above a sample). Max is an exact observation.
type Distribution() =
    let bins = Array.zeroCreate<int64> 2048
    let scale = log 1.01
    let mutable count = 0L
    let mutable maximum = 0.
    member _.Add(value: float) =
        if Double.IsFinite value && value >= 0. then
            let index = min (bins.Length - 1) (int (log (1. + value) / scale))
            bins[index] <- bins[index] + 1L
            count <- count + 1L
            maximum <- max maximum value

    member _.Summary() =
        let percentile fraction =
            if count = 0L then 0.
            else
                let target = int64 (ceil (fraction * float count))
                let mutable seen = 0L
                let mutable index = 0
                while seen < target && index < bins.Length do
                    seen <- seen + bins[index]
                    index <- index + 1
                min maximum (exp (float index * scale) - 1.)
        {| count = count; p50 = percentile 0.5; p95 = percentile 0.95; p99 = percentile 0.99; max = maximum |}

// Test host only; runs the normal server entry point with a MeterListener.
let runServer config output =
    printfn "BENCHMARK_HOST server_gc=%b" System.Runtime.GCSettings.IsServerGC
    let gate = obj()
    let measurements = Dictionary<string, Distribution>()
    use listener = new MeterListener()
    listener.InstrumentPublished <- fun instrument owner ->
        if instrument.Meter.Name = "Dreamsleeve.Server" then owner.EnableMeasurementEvents instrument
    listener.SetMeasurementEventCallback<double>(fun instrument value _ _ ->
        lock gate (fun () ->
            let distribution =
                match measurements.TryGetValue instrument.Name with
                | true, existing -> existing
                | false, _ ->
                    let created = Distribution()
                    measurements.Add(instrument.Name, created)
                    created
            distribution.Add value))
    listener.Start()
    try Dreamsleeve.Server.Program.main [|"--config"; config|]
    finally
        lock gate (fun () ->
            let result = measurements |> Seq.map (fun pair -> pair.Key, pair.Value.Summary()) |> dict
            File.WriteAllText(output, JsonSerializer.Serialize(result, JsonSerializerOptions(WriteIndented = true))))
