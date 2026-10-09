module Dreamsleeve.Server.NetworkBenchmarks.Measurements

open System
open System.Collections.Generic
open System.Diagnostics
open System.Diagnostics.Metrics
open System.Threading
open System.IO
open System.Net.NetworkInformation
open System.Text.Json

// Fixed memory, logarithmic buckets: reported quantiles are upper bucket bounds
// (at most 1% + 0.01 ms above a sample). Max is an exact observation.
type Distribution() =
    let bins = Array.zeroCreate<int64> 2048
    let scale = log 1.01
    let mutable count = 0L
    let mutable maximum = 0.
    let mutable total = 0.

    member _.Add(value: float) =
        if Double.IsFinite value && value >= 0. then
            let index = min (bins.Length - 1) (int (log (1. + value) / scale))
            bins[index] <- bins[index] + 1L
            count <- count + 1L
            maximum <- max maximum value
            total <- total + value

    member _.Summary() =
        let percentile fraction =
            if count = 0L then
                0.
            else
                let target = int64 (ceil (fraction * float count))
                let mutable seen = 0L
                let mutable index = 0

                while seen < target && index < bins.Length do
                    seen <- seen + bins[index]
                    index <- index + 1

                min maximum (exp (float index * scale) - 1.)

        {| count = count
           sum = total
           p50 = percentile 0.5
           p95 = percentile 0.95
           p99 = percentile 0.99
           max = maximum |}

// Benchmark-only phase sampling. The runner marks phases through a shared file;
// transitions have up to 100 ms uncertainty. Histograms never retain raw events.
type Recorder(output: string) =
    let gate = obj()
    let clock = Stopwatch.StartNew()
    let measurements = Dictionary<string, Dictionary<string, Distribution>>()
    let samples = ResizeArray<obj>()
    let slowEvents = ResizeArray<obj>()
    let mutable phase = "startup"
    let phasePath = Environment.GetEnvironmentVariable "DREAMSLEEVE_BENCH_PHASE"
    let listener = new MeterListener()

    let sample _ =
        lock gate (fun () ->
            if not (String.IsNullOrEmpty phasePath) then
                try
                    // Shared for writing and replacing: the runner rewrites the file at any moment.
                    use stream = new FileStream(phasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
                    use reader = new StreamReader(stream)
                    let text = reader.ReadToEnd().Trim()
                    if text <> "" then phase <- text
                with :? IOException -> ()

            let memory = GC.GetGCMemoryInfo()
            let udp = IPGlobalProperties.GetIPGlobalProperties().GetUdpIPv4Statistics()
            samples.Add(box {|
                elapsedMs = clock.Elapsed.TotalMilliseconds
                phase = phase
                allocatedBytes = GC.GetTotalAllocatedBytes(false)
                udpReceiveErrors = udp.IncomingDatagramsWithErrors
                udpReceived = udp.DatagramsReceived
                udpSent = udp.DatagramsSent
                gcPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds
                gen0 = GC.CollectionCount(0)
                gen1 = GC.CollectionCount(1)
                gen2 = GC.CollectionCount(2)
                heapBytes = memory.HeapSizeBytes
                fragmentedBytes = memory.FragmentedBytes
                threadPoolThreads = ThreadPool.ThreadCount
                threadPoolPending = ThreadPool.PendingWorkItemCount
            |}))

    let record (instrument: Instrument) (value: double) =
        lock gate (fun () ->
            let values =
                match measurements.TryGetValue phase with
                | true, existing -> existing
                | false, _ ->
                    let created = Dictionary()
                    measurements.Add(phase, created)
                    created

            let distribution =
                match values.TryGetValue instrument.Name with
                | true, existing -> existing
                | false, _ ->
                    let created = Distribution()
                    values.Add(instrument.Name, created)
                    created
            distribution.Add value

            if slowEvents.Count < 2048 && instrument.Name.EndsWith(".duration") && value >= 20. then
                slowEvents.Add(box {| elapsedMs = clock.Elapsed.TotalMilliseconds
                                      phase = phase
                                      name = instrument.Name
                                      durationMs = value |}))

    do
        listener.InstrumentPublished <- fun instrument owner ->
            if instrument.Meter.Name = "Dreamsleeve.Server" || instrument.Meter.Name = "Dreamsleeve.Transport"
               || instrument.Meter.Name = "Dreamsleeve.Transport.Owner" || instrument.Meter.Name = "Dreamsleeve.Phantoms" then
                owner.EnableMeasurementEvents instrument

        listener.SetMeasurementEventCallback<double>(fun instrument value _ _ -> record instrument value)
        listener.SetMeasurementEventCallback<int>(fun instrument value _ _ -> record instrument (double value))
        listener.SetMeasurementEventCallback<int64>(fun instrument value _ _ -> record instrument (double value))
        listener.Start()

    let timer = new Timer(TimerCallback sample, null, 0, 100)

    interface IDisposable with
        member _.Dispose() =
            timer.DisposeAsync().AsTask().GetAwaiter().GetResult()
            listener.Dispose()

            lock gate (fun () ->
                let phases = measurements |> Seq.map (fun phase ->
                    phase.Key, (phase.Value |> Seq.map (fun pair -> pair.Key, pair.Value.Summary()) |> dict)) |> dict
                let result =
                    {| schemaVersion = 2
                       byPhase = phases
                       runtimeSamples = samples
                       slowEvents = slowEvents |}
                File.WriteAllText(output, JsonSerializer.Serialize(result, JsonSerializerOptions(WriteIndented = true))))

let runServer config output =
    printfn "BENCHMARK_HOST server_gc=%b" System.Runtime.GCSettings.IsServerGC
    use recorder = new Recorder(output)
    Dreamsleeve.Server.Program.main [|"--config"; config|]
