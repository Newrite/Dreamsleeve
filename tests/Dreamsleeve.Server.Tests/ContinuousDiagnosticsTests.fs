module Dreamsleeve.Server.Tests.ContinuousDiagnosticsTests

open System
open System.Collections.Concurrent
open System.Diagnostics.Metrics
open System.IO
open System.Threading.Tasks
open System.Text.Json
open Expecto
open Dreamsleeve.Server.Infrastructure

let private start write close failed =
    ContinuousDiagnostics.TryStart(Func<string, DiagnosticOperationResult>(write),
                                   close |> Option.map (fun callback -> Func<DiagnosticOperationResult>(callback)) |> Option.toObj,
                                   failed |> Option.map (fun callback -> Action<Exception>(callback)) |> Option.toObj).Match(
        (fun collector -> collector), (fun () -> failtest "Invalid test sink."))

let private createFile path limit =
    DiagnosticFile.TryCreate(path, limit).Match((fun file -> file), (fun error -> failtestf "%A" error))

let private written (result: DiagnosticOperationResult) =
    result.Match((fun () -> ()), (fun error -> failtestf "Unexpected I/O error: %A" error.Causes),
                 (fun causes -> failtestf "Unexpected owner fault: %A" causes))

let private faults (collector: ContinuousDiagnostics) =
    let outcome = collector.Completion.WaitAsync(AgentTests.guard).GetAwaiter().GetResult()
    outcome.Match((fun () -> []), (fun causes -> List.ofSeq causes))

// MeterListener observes every process-global Dreamsleeve meter; fault injection
// must not deliver an unsupported tag to a neighboring collector fixture.
let tests = testSequenced <| testList "Continuous diagnostics" [
    testCase "flushes metric samples and structured lifecycle on shutdown" <| fun _ ->
        let lines = ConcurrentQueue<string>()
        use meter = new Meter("Dreamsleeve.Test.Diagnostics")
        let stage = meter.CreateHistogram<double>("test.stage", "ms")
        let lifecycle = meter.CreateCounter<int64>("phantom.lifecycle")
        let collector = start (fun line -> lines.Enqueue line; DiagnosticOperationResult.Success) None None
        stage.Record 2.5
        stage.Record 7.5
        let mutable tags = Diagnostics.TagList()
        tags.Add("event", "context"); tags.Add("context", 123UL)
        lifecycle.Add(1L, &tags)
        collector.Dispose()
        let records = lines.ToArray() |> Array.map JsonDocument.Parse
        try
            let samples = records |> Array.filter (fun x -> x.RootElement.GetProperty("kind").GetString() = "sample")
            Expect.isGreaterThan samples.Length 0 "final sample persisted"
            let found = samples |> Array.collect (fun x -> x.RootElement.GetProperty("metrics").EnumerateArray() |> Seq.toArray)
                        |> Array.find (fun x -> x.GetProperty("name").GetString() = "test.stage")
            Expect.equal (found.GetProperty("count").GetInt64()) 2L "all measurements"
            Expect.equal (found.GetProperty("sum").GetDouble()) 10.0 "sum"
            let transition = records |> Array.find (fun x -> x.RootElement.GetProperty("kind").GetString() = "phantom" && x.RootElement.GetProperty("fields").GetProperty("event").GetString() = "context")
            Expect.equal (transition.RootElement.GetProperty("fields").GetProperty("context").GetUInt64()) 123UL "numeric context"
        finally for record in records do record.Dispose()
    testCase "HTTP events retain transfer correlation and phases without aggregation" <| fun _ ->
        let lines = ConcurrentQueue<string>()
        use meter = new Meter("Dreamsleeve.Test.Http")
        let events = meter.CreateCounter<int64>("phantom.http")
        let collector = start (fun line -> lines.Enqueue line; DiagnosticOperationResult.Success) None None
        for phase in ["claimed"; "first_body"; "body_complete"] do
            let mutable tags = Diagnostics.TagList()
            tags.Add("event", "http"); tags.Add("phase", phase)
            tags.Add("transfer", 791UL); tags.Add("body_ms", 123.5)
            events.Add(1L, &tags)
        collector.Dispose()
        let phases = ResizeArray<string>()
        for line in lines do
            use record = JsonDocument.Parse line
            let root = record.RootElement
            if root.GetProperty("kind").GetString() = "phantom" then
                let fields = root.GetProperty("fields")
                if fields.GetProperty("event").GetString() = "http" then
                    Expect.equal (fields.GetProperty("transfer").GetUInt64()) 791UL "Exact transfer ID"
                    Expect.equal (fields.GetProperty("body_ms").GetDouble()) 123.5 "Timing preserved"
                    phases.Add(fields.GetProperty("phase").GetString())
        Expect.sequenceEqual phases ["claimed"; "first_body"; "body_complete"] "Every phase persisted"
    testCase "expected optional sink I/O failure cannot fault shutdown or producer" <| fun _ ->
        use collector = start (fun _ -> DiagnosticOperationResult.IoFailure(IOException("disk unavailable"))) None None
        use meter = new Meter("Dreamsleeve.Test.Failure")
        meter.CreateHistogram<double>("test.value").Record 1.0
        collector.Dispose()
        Expect.isEmpty (faults collector) "Expected disk failure remains optional."
    testCase "real file sink reports disk failures and preserves every segmented JSON line" <| fun _ ->
        let root = IO.Path.Combine(IO.Path.GetTempPath(), "dreamsleeve-trace-" + Guid.NewGuid().ToString("N"))
        IO.Directory.CreateDirectory root |> ignore
        try
            let path = IO.Path.Combine(root,"trace.jsonl")
            let file = createFile path 1024L
            let line = "{\"payload\":\"" + String.replicate 700 "a" + "\"}"
            for _ in 1..5 do written (file.TryWrite line)
            written (file.TryClose())
            Expect.equal (IO.Directory.GetFiles(root,"*.jsonl",IO.SearchOption.AllDirectories).Length) 5 "all session parts retained"
            for filename in IO.Directory.GetFiles(root,"*.jsonl",IO.SearchOption.AllDirectories) do
                for text in IO.File.ReadAllLines filename do
                    use document = JsonDocument.Parse text
                    Expect.equal (document.RootElement.GetProperty("payload").GetString().Length) 700 "whole records"
            let blocked = IO.Path.Combine(root,"blocked")
            IO.File.WriteAllText(blocked,"not a directory")
            let failures = ConcurrentQueue<Exception>()
            let badFile = createFile (IO.Path.Combine(blocked,"trace.jsonl")) 1024L
            use collector = start badFile.TryWrite (Some badFile.TryClose) (Some failures.Enqueue)
            collector.Dispose()
            Expect.isGreaterThan failures.Count 0 "production sink failures reach reporter"
            Expect.isEmpty (faults collector) "Expected blocked path is optional."
        finally IO.Directory.Delete(root,true)
    testCase "blocked sink does not indefinitely block shutdown" <| fun _ ->
        use release = new Threading.ManualResetEventSlim(false)
        use started = new Threading.ManualResetEventSlim(false)
        use finished = new Threading.ManualResetEventSlim(false)
        let collector = start (fun _ -> started.Set(); release.Wait(); DiagnosticOperationResult.Success)
                              (Some (fun () -> finished.Set(); DiagnosticOperationResult.Success)) None
        try
            Expect.isTrue (started.Wait(5000)) "writer started"
            let time = Diagnostics.Stopwatch.StartNew()
            collector.Dispose()
            Expect.isLessThan time.Elapsed.TotalSeconds 4.0 "bounded shutdown"
            Expect.isFalse collector.Completion.IsCompleted "Blocked owner has not completed or released its sink."
            Expect.isFalse finished.IsSet "Close waits for the owned write."
        finally
            release.Set()
            Expect.isTrue (finished.Wait(5000)) "writer owns and closes sink after release"
            Expect.isEmpty (faults collector) "Actual completion is observed after release."
            collector.Dispose()
    testCase "checked diagnostic construction rejects raw invalid options" <| fun _ ->
        let invalidLimit = DiagnosticFile.TryCreate("diagnostics/server.jsonl", 512L)
        invalidLimit.Match((fun _ -> failtest "Invalid limit started a sink."), (fun error ->
            match error with
            | :? DiagnosticFileStartError.InvalidLimit as error -> Expect.equal error.Value 512L "Original limit."
            | _ -> failtestf "%A" error))
        DiagnosticFile.TryCreate(null, 1024L).Match((fun _ -> failtest "Missing path started a sink."), (fun error ->
            Expect.isTrue (error :? DiagnosticFileStartError.InvalidPath) "Missing path is typed."))
        DiagnosticFile.TryCreate(Path.GetPathRoot(Environment.CurrentDirectory), 1024L).Match(
            (fun _ -> failtest "Root directory has no file parent."),
            (fun error -> Expect.isTrue (error :? DiagnosticFileStartError.InvalidPath) "Root path is an ordinary typed rejection."))
        ContinuousDiagnostics.TryStart(null).Match((fun _ -> failtest "Null callback started collector."), (fun () -> ()))

    testCase "startup callback fault closes once and remains observable after Dispose" <| fun _ ->
        let original = InvalidOperationException("startup diagnostic callback")
        let mutable writes, closes = 0, 0
        let reported = ConcurrentQueue<Exception>()
        let collector = start (fun _ -> Threading.Interlocked.Increment(&writes) |> ignore; raise original)
                              (Some (fun () -> Threading.Interlocked.Increment(&closes) |> ignore; DiagnosticOperationResult.Success))
                              (Some reported.Enqueue)
        let causes = faults collector
        collector.Dispose()
        collector.Dispose()
        Expect.equal writes 1 "Failed start is never flushed/retried."
        Expect.equal closes 1 "Owned cleanup still runs once."
        Expect.isTrue (obj.ReferenceEquals(original, List.exactlyOne causes)) "Original startup fault."
        Expect.isTrue (obj.ReferenceEquals(original, reported.ToArray() |> Array.exactlyOne)) "Original fault reported."

    testCase "unexpected write reporter and close faults retain primary and secondary causes" <| fun _ ->
        let original = InvalidOperationException("write callback")
        let reporting = InvalidOperationException("report callback")
        let cleanup = InvalidOperationException("close callback")
        let mutable writes, reports, closes = 0, 0, 0
        let collector = start (fun _ ->
                                  if Threading.Interlocked.Increment(&writes) = 1 then DiagnosticOperationResult.Success
                                  else raise original)
                              (Some (fun () -> Threading.Interlocked.Increment(&closes) |> ignore; raise cleanup))
                              (Some (fun _ -> Threading.Interlocked.Increment(&reports) |> ignore; raise reporting))
        collector.Dispose()
        let causes = faults collector
        Expect.equal (writes, reports, closes) (2, 1, 1) "No failed callback retry; cleanup remains independent."
        Expect.equal causes.Length 3 "All three original causes."
        List.iter2 (fun expected actual -> Expect.isTrue (obj.ReferenceEquals(expected, actual)) "Original fault order retained.")
                   [ original :> exn; reporting; cleanup ] causes

    testCase "callback cancellation remains a fault even while owner shutdown is canceled" <| fun _ ->
        let original = OperationCanceledException("unexpected sink callback cancellation")
        let mutable writes, closes = 0, 0
        let collector = start (fun _ ->
                                  if Threading.Interlocked.Increment(&writes) = 1 then DiagnosticOperationResult.Success
                                  else raise original)
                              (Some (fun () -> Threading.Interlocked.Increment(&closes) |> ignore; DiagnosticOperationResult.Success)) None
        collector.Dispose()
        Expect.isTrue (obj.ReferenceEquals(original, List.exactlyOne (faults collector))) "Only timer cancellation is normal shutdown."
        Expect.equal closes 1 "Unexpected cancellation still closes owned sink."

    testCase "expected I/O followed by reporter and cleanup failure stops owner without losing I/O" <| fun _ ->
        let disk = IOException("expected write I/O")
        let reporting = InvalidOperationException("reporter fault")
        let cleanup = IOException("expected close I/O")
        let mutable writes, closes = 0, 0
        let collector = start (fun _ -> Threading.Interlocked.Increment(&writes) |> ignore; DiagnosticOperationResult.IoFailure disk)
                              (Some (fun () -> Threading.Interlocked.Increment(&closes) |> ignore; DiagnosticOperationResult.IoFailure cleanup))
                              (Some (fun _ -> raise reporting))
        let causes = faults collector
        collector.Dispose()
        Expect.equal (writes, closes) (1, 1) "Reporter fault stops writing but still closes sink."
        List.iter2 (fun expected actual -> Expect.isTrue (obj.ReferenceEquals(expected, actual)) "Original I/O/reporter/cleanup causes.")
                   [ disk :> exn; reporting; cleanup ] causes

    testCase "unexpected tag serialization stops collector and closes its sink" <| fun _ ->
        let mutable closes = 0
        let lines = ConcurrentQueue<string>()
        use meter = new Meter("Dreamsleeve.Test.Serialization")
        let events = meter.CreateCounter<int64>("phantom.lifecycle")
        let collector = start (fun line -> lines.Enqueue line; DiagnosticOperationResult.Success)
                              (Some (fun () -> Threading.Interlocked.Increment(&closes) |> ignore; DiagnosticOperationResult.Success)) None
        let mutable tags = Diagnostics.TagList()
        tags.Add("unsupported", typeof<string>)
        events.Add(1L, &tags)
        collector.Dispose()
        let causes = faults collector
        Expect.equal closes 1 "Serialization failure cannot skip close."
        Expect.isTrue (List.exactlyOne causes :? NotSupportedException) "Unsupported tag remains unexpected serialization fault."
        Expect.equal lines.Count 1 "Start only; failed final flush is not retried."

    testCase "concurrent shutdown retains one owner and safe token lifetime" <| fun _ ->
        let mutable closes = 0
        let collector = start (fun _ -> DiagnosticOperationResult.Success)
                              (Some (fun () -> Threading.Interlocked.Increment(&closes) |> ignore; DiagnosticOperationResult.Success)) None
        Task.WhenAll(Array.init 32 (fun _ -> Task.Run(Action collector.Dispose))).WaitAsync(AgentTests.guard).GetAwaiter().GetResult()
        Expect.isEmpty (faults collector) "Concurrent Dispose does not cancel an already-disposed source."
        Expect.equal closes 1 "Exactly one owned close."

    testCase "expected real file failure reconstructs the sink on the next write" <| fun _ ->
        let root = Path.Combine(Path.GetTempPath(), "dreamsleeve-recover-trace-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let blocked = Path.Combine(root, "blocked")
        File.WriteAllText(blocked, "ordinary file")
        let sink = createFile (Path.Combine(blocked, "trace.jsonl")) 1024L
        try
            sink.TryWrite("{} ").Match((fun () -> failtest "Blocked parent accepted write."),
                (fun error -> Expect.isTrue (error.Causes[0] :? IOException) "Expected disk error."),
                (fun causes -> failtestf "Unexpected fault: %A" causes))
            File.Delete blocked
            Directory.CreateDirectory blocked |> ignore
            written (sink.TryWrite("{\"recovered\":true}"))
            written (sink.TryClose())
            let saved = Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories) |> Array.exactlyOne
            use document = JsonDocument.Parse(File.ReadAllText saved)
            Expect.isTrue (document.RootElement.GetProperty("recovered").GetBoolean()) "Next independent write succeeded."
        finally
            written (sink.TryClose())
            Directory.Delete(root, true)
]
