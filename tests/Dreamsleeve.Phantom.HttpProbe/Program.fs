module Dreamsleeve.Phantom.HttpProbe
open System
open System.IO
open System.Diagnostics
open System.Net.Http
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Infrastructure

let stop message = eprintfn "%s" message; exit 1
let ok = function Ok x -> x | Error e -> stop (sprintf "%A" e)
// Instrument the real response stream; no synthetic replacement of HTTP or storage.
type TimedStream(inner: Stream) =
    inherit Stream()
    let mutable elapsed = 0.
    member _.Elapsed = elapsed
    override _.CanRead = false
    override _.CanWrite = true
    override _.CanSeek = false
    override _.Length = 0L
    override _.Position with get() = 0L and set _ = ()
    override _.Flush() = ()
    override _.Read(_,_,_) = 0
    override _.Seek(_,_) = 0L
    override _.SetLength _ = ()
    override _.Write(bytes,offset,count) = inner.Write(bytes,offset,count)
    override _.WriteAsync(bytes: ReadOnlyMemory<byte>, cancellation: CancellationToken) =
        ValueTask(task {
            let start = Stopwatch.GetTimestamp()
            do! inner.WriteAsync(bytes, cancellation)
            elapsed <- elapsed + Stopwatch.GetElapsedTime(start).TotalMilliseconds
        })

let run (args: string array) = task {
    if args.Length < 2 || args.Length > 3 then stop "Usage: HttpProbe model.zst output-directory [native-probe.exe]"
    if not (File.Exists args[0]) then stop "Model file missing"
    if args.Length = 3 && not (File.Exists args[2]) then stop "Native probe missing"
    let data = File.ReadAllBytes args[0]
    let root = Path.GetFullPath args[1]
    Directory.CreateDirectory root |> ignore
    let options = { PhantomOptions.defaults with StoragePath = Path.Combine(root, "cache"); RamBytes = 0L }
    let asset = PhantomManifest.create options.Limits (AssetHash.create (SHA256.HashData data) |> ok)
                    (AppearanceGeneration.create 1UL |> ok) 2u (uint32 data.Length) (uint32 data.Length) 2u |> ok
    let storage = PhantomStorage.create options
    let mutable diskMs = 0.
    let measured =
        { storage with
            ReadChunk = fun request -> task {
                let start = Stopwatch.GetTimestamp()
                let! result = storage.ReadChunk request
                diskMs <- diskMs + Stopwatch.GetElapsedTime(start).TotalMilliseconds
                return result
            } }
    let http = PhantomHttp.create options measured
    let id = PhantomTransferId 1UL
    let! cached = storage.StartUpload(id,asset, None)
    if not (ok cached) then
        let mutable offset = 0
        while offset < data.Length do
            let count = min options.ChunkBytes (data.Length-offset)
            let! result = storage.WriteChunk(id,offset,data[offset..offset+count-1])
            ok result |> ignore
            offset <- offset+count
    do! storage.Cancel id
    let builder = WebApplication.CreateBuilder()
    builder.Logging.ClearProviders() |> ignore
    builder.WebHost.UseSetting("urls", "http://127.0.0.1:0") |> ignore
    let app = builder.Build()
    let mutable responseMs, pumpMs = 0.,0.
    let mutable finished = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    app.Run(RequestDelegate(fun context -> task {
        let start = Stopwatch.GetTimestamp()
        use body = new TimedStream(context.Response.Body)
        let! result = http.Serve {
            Token = context.Request.Headers.Authorization.ToString().Replace("Bearer ", "")
            Upload = false; Length = None; Body = body
            BeginResponse = fun size -> context.Response.ContentLength <- Nullable(int64 size)
            Cancellation = context.RequestAborted }
        responseMs <- body.Elapsed
        pumpMs <- Stopwatch.GetElapsedTime(start).TotalMilliseconds
        if Result.isError result then context.Abort()
        finished.TrySetResult() |> ignore
    }))
    do! app.StartAsync()
    use client = new HttpClient(Timeout = TimeSpan.FromSeconds 90.)
    let owner = Guid.NewGuid()
    let results = ResizeArray<obj>()
    let mutable number = 1UL
    let managedCases =
        if Environment.GetEnvironmentVariable("DREAMSLEEVE_HTTP_PROBE_NATIVE_ONLY") = "1" then []
        else ["fast1",0,0; "fast2",0,0; "slow512KiB",524288,0; "fastAfterSlow",0,0; "pause3s",0,3000; "fastAfterPause",0,0]
    for name, rate, pause in managedCases do
        number <- number+1UL
        let transfer = PhantomTransferId number
        let! admitted = storage.StartDownload(transfer, asset, None)
        ok admitted |> ignore
        let lease = http.Admit(owner,transfer,asset,false, None)
        diskMs <- 0.; responseMs <- 0.; pumpMs <- 0.
        finished <- TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        use request = new HttpRequestMessage(HttpMethod.Get, Seq.head app.Urls)
        request.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer",lease.Token)
        let watch = Stopwatch.StartNew()
        use! response = client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead)
        response.EnsureSuccessStatusCode() |> ignore
        use! stream = response.Content.ReadAsStreamAsync()
        use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
        let buffer = Array.zeroCreate<byte> 32768
        let mutable total = 0
        if pause > 0 then do! Task.Delay pause
        let mutable ended = false
        while not ended do
            let! count = stream.ReadAsync(buffer.AsMemory())
            if count = 0 then ended <- true
            else
                hash.AppendData(buffer,0,count)
                total <- total+count
                if rate > 0 then
                    let delay = float total / float rate * 1000. - watch.Elapsed.TotalMilliseconds
                    if delay > 0. then do! Task.Delay(TimeSpan.FromMilliseconds delay)
        let elapsed = watch.Elapsed.TotalMilliseconds
        do! finished.Task
        let valid = total = data.Length && Convert.ToHexStringLower(hash.GetHashAndReset()) = asset.Hash.Hex
        let row = {| name=name; bytes=total; elapsedMs=elapsed; pumpMs=pumpMs; responseWriteMs=responseMs; storageReadMs=diskMs
                     remainingPumpMs=pumpMs-responseMs-diskMs; valid=valid |}
        if not valid then stop "Body hash/length mismatch"
        results.Add row
        printfn "%s" (JsonSerializer.Serialize row)
        do! http.Cancel transfer
        do! storage.Cancel transfer
    if args.Length > 2 then
        let start = ProcessStartInfo(Path.GetFullPath args[2], UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardInput=true, CreateNoWindow=true)
        for value in [Seq.head app.Urls; Path.GetFullPath args[0]] do start.ArgumentList.Add value
        use child = Process.Start start
        for name, rate in ["winhttpFast1",5242880; "winhttpSlow512KiB",524288; "winhttpFastAfterSlow",5242880] do
            number <- number+1UL
            let transfer = PhantomTransferId number
            let! admitted = storage.StartDownload(transfer,asset, None)
            ok admitted |> ignore
            let lease = http.Admit(owner,transfer,asset,false, None)
            diskMs <- 0.; responseMs <- 0.; pumpMs <- 0.
            finished <- TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            do! child.StandardInput.WriteLineAsync lease.Token
            do! child.StandardInput.WriteLineAsync(string rate)
            do! child.StandardInput.FlushAsync()
            let! output = child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds 90.)
            if isNull output then stop "Native probe ended without a result"
            do! finished.Task
            let client = JsonDocument.Parse output
            let row = {| name=name; elapsedMs=client.RootElement.GetProperty("elapsedMs").GetDouble(); valid=client.RootElement.GetProperty("valid").GetBoolean()
                         pumpMs=pumpMs; responseWriteMs=responseMs; storageReadMs=diskMs; remainingPumpMs=pumpMs-responseMs-diskMs |}
            results.Add row
            printfn "%s" (JsonSerializer.Serialize row)
            do! http.Cancel transfer
            do! storage.Cancel transfer
        child.StandardInput.Close()
        do! child.WaitForExitAsync()
        if child.ExitCode <> 0 then stop (sprintf "Native probe failed: %d" child.ExitCode)
    File.WriteAllText(Path.Combine(root,"results.json"), JsonSerializer.Serialize(results,JsonSerializerOptions(WriteIndented=true)))
    do! app.StopAsync()
    do! app.DisposeAsync().AsTask()
    do! http.Dispose()
    do! storage.Dispose()
}
[<EntryPoint>]
let main args = run args |> _.GetAwaiter().GetResult(); 0
