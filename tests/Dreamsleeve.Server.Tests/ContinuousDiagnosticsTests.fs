module Dreamsleeve.Server.Tests.ContinuousDiagnosticsTests

open System
open System.Collections.Concurrent
open System.Diagnostics.Metrics
open System.Text.Json
open Expecto
open Dreamsleeve.Server.Infrastructure

let tests = testList "Continuous diagnostics" [
    testCase "flushes metric samples and structured lifecycle on shutdown" <| fun _ ->
        let lines = ConcurrentQueue<string>()
        use meter = new Meter("Dreamsleeve.Test.Diagnostics")
        let stage = meter.CreateHistogram<double>("test.stage", "ms")
        let lifecycle = meter.CreateCounter<int64>("phantom.lifecycle")
        let collector = new ContinuousDiagnostics(Action<string>(lines.Enqueue))
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
        let collector = new ContinuousDiagnostics(Action<string>(lines.Enqueue))
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
    testCase "sink failure cannot fault shutdown or producer" <| fun _ ->
        use collector = new ContinuousDiagnostics(Action<string>(fun _ -> failwith "disk unavailable"))
        use meter = new Meter("Dreamsleeve.Test.Failure")
        meter.CreateHistogram<double>("test.value").Record 1.0
    testCase "real file sink reports disk failures and preserves every segmented JSON line" <| fun _ ->
        let root = IO.Path.Combine(IO.Path.GetTempPath(), "dreamsleeve-trace-" + Guid.NewGuid().ToString("N"))
        IO.Directory.CreateDirectory root |> ignore
        try
            let path = IO.Path.Combine(root,"trace.jsonl")
            use file = new DiagnosticFile(path,1024L)
            let line = "{\"payload\":\"" + String.replicate 700 "a" + "\"}"
            for _ in 1..5 do file.Write line
            file.Dispose()
            Expect.equal (IO.Directory.GetFiles(root,"*.jsonl",IO.SearchOption.AllDirectories).Length) 5 "all session parts retained"
            for filename in IO.Directory.GetFiles(root,"*.jsonl",IO.SearchOption.AllDirectories) do
                for text in IO.File.ReadAllLines filename do
                    use document = JsonDocument.Parse text
                    Expect.equal (document.RootElement.GetProperty("payload").GetString().Length) 700 "whole records"
            let blocked = IO.Path.Combine(root,"blocked")
            IO.File.WriteAllText(blocked,"not a directory")
            let failures = ConcurrentQueue<Exception>()
            let badFile = new DiagnosticFile(IO.Path.Combine(blocked,"trace.jsonl"),1024L)
            use collector = new ContinuousDiagnostics(Action<string>(badFile.Write),Action(badFile.Dispose),Action<Exception>(failures.Enqueue))
            collector.Dispose()
            Expect.isGreaterThan failures.Count 0 "production sink failures reach reporter"
        finally IO.Directory.Delete(root,true)
    testCase "blocked sink does not indefinitely block shutdown" <| fun _ ->
        use release = new Threading.ManualResetEventSlim(false)
        use started = new Threading.ManualResetEventSlim(false)
        use finished = new Threading.ManualResetEventSlim(false)
        let collector = new ContinuousDiagnostics(Action<string>(fun _ -> started.Set(); release.Wait()),Action(finished.Set))
        try
            Expect.isTrue (started.Wait(5000)) "writer started"
            let time = Diagnostics.Stopwatch.StartNew()
            collector.Dispose()
            Expect.isLessThan time.Elapsed.TotalSeconds 4.0 "bounded shutdown"
        finally
            release.Set()
            Expect.isTrue (finished.Wait(5000)) "writer owns and closes sink after release"
]
