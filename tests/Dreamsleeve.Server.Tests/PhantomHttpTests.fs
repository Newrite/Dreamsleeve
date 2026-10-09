module Dreamsleeve.Server.Tests.PhantomHttpTests

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Expecto
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure

let private ok = function
    | Ok value -> value
    | Error error -> failtestf "%A" error

let private manifest (bytes: byte array) =
    PhantomManifest.create PhantomOptions.defaults.Limits (AssetHash.create (SHA256.HashData bytes) |> ok)
        (AppearanceGeneration.create 1UL |> ok) 2u (uint32 bytes.Length) (uint32 bytes.Length) 2u |> ok

let private request lease upload length body = {
    Token = lease
    Upload = upload
    Length = length

    Body = body
    BeginResponse = ignore
    Cancellation = CancellationToken.None
}

let private fixture name run = testCaseAsync name (async {
    let root = Path.Combine(Path.GetTempPath(), "dreamsleeve-http-" + Guid.NewGuid().ToString("N"))
    let options = {
        PhantomOptions.defaults with
            StoragePath = root
            RamBytes = 0L
    }
    let storage = PhantomStorage.create options
    let http = PhantomHttp.create options storage
    try
        do! run options storage http |> Async.AwaitTask
    finally
        http.Dispose().GetAwaiter().GetResult()
        storage.Dispose().GetAwaiter().GetResult()
        if Path.GetDirectoryName(Path.GetFullPath root) = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) && Directory.Exists root then
            Directory.Delete(root, true)
})

type private BlockedBody() =
    inherit MemoryStream()
    let entered = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    member _.Entered = entered.Task
    override _.ReadAsync(_: Memory<byte>, cancellation: CancellationToken) =
        entered.TrySetResult() |> ignore
        ValueTask<int>(TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync cancellation)

type private FailedBody(error: exn) =
    inherit MemoryStream()
    override _.ReadAsync(_: Memory<byte>, _: CancellationToken) = ValueTask<int>(Task.FromException<int>(error))

let private deltaFixture = fixture "native prefix delta reconstructs canonical archive and serves full or delta by exact base" (fun options storage http -> task {
    let directory = Environment.GetEnvironmentVariable "DREAMSLEEVE_DELTA_FIXTURE"
    let basis, target, patch =
        if String.IsNullOrEmpty directory then
            let raw = Array.zeroCreate<byte> (256 * 1024)
            Random(42).NextBytes raw

            use encoder = new ZstdSharp.Compressor(3)
            let basis = encoder.Wrap(raw.AsSpan()).ToArray()
            let changed = Array.copy raw
            changed[17000] <- changed[17000] ^^^ 85uy
            let target = encoder.Wrap(changed.AsSpan()).ToArray()

            encoder.LoadDictionary raw
            basis, target, encoder.Wrap(changed.AsSpan()).ToArray()
        else
            File.ReadAllBytes(Path.Combine(directory, "base.zst")),
            File.ReadAllBytes(Path.Combine(directory, "target.zst")),
            File.ReadAllBytes(Path.Combine(directory, "patch.zst"))

    let rawSize = int (ZstdSharp.Decompressor.GetDecompressedSize(target.AsSpan()))
    let baseAsset = manifest basis
    let asset =
        PhantomManifest.create
            options.Limits
            (AssetHash.create(SHA256.HashData target) |> ok)
            (AppearanceGeneration.create 2UL |> ok)
            2u
            (uint32 target.Length)
            (uint32 rawSize)
            2u
        |> ok
    let delta =
        PhantomDelta.create
            asset
            baseAsset.Hash
            (AssetHash.create(SHA256.HashData patch) |> ok)
            (uint32 patch.Length)
        |> ok

    let owner = Guid.NewGuid()
    let upload id value change bytes = task {
        let! admitted = storage.StartUpload(id, value, change)
        Expect.equal admitted (Ok false) "New body."

        let lease = http.Admit(owner, id, value, true, change)
        use input = new MemoryStream(bytes: byte array)
        let! result = http.Serve(request lease.Token true (Some(int64 bytes.Length)) input)
        do! http.Cancel id
        return result
    }

    let! full = upload (PhantomTransferId 1UL) baseAsset None basis
    Expect.equal full (Ok ()) "Base committed."

    // Force the real patch rename dependency to fail before canonical commit.
    let blockedPatch = Path.Combine(options.StoragePath, asset.Hash.Hex + ".delta")
    Directory.CreateDirectory blockedPatch |> ignore
    let! failedPublication = upload (PhantomTransferId 2UL) asset (Some delta) patch
    match failedPublication with
    | Error (PhantomHttpError.Storage (PhantomStorageError.Io _)) -> ()
    | other -> failtestf "Expected patch filesystem failure, got %A" other

    Expect.isFalse (File.Exists(Path.Combine(options.StoragePath, asset.Hash.Hex + ".zst"))) "No rejected canonical target."
    Expect.equal (Directory.GetFiles(options.StoragePath, "*.tmp").Length) 0 "Failed publication releases temporary file."
    Expect.isTrue (File.Exists(Path.Combine(options.StoragePath, baseAsset.Hash.Hex + ".zst"))) "Usable base retained."

    Directory.Delete blockedPatch
    let! restored = upload (PhantomTransferId 2UL) asset (Some delta) patch
    Expect.equal restored (Ok ()) "Delta committed only after canonical target hash."
    Expect.sequenceEqual (File.ReadAllBytes(Path.Combine(options.StoragePath, asset.Hash.Hex + ".zst"))) target "Exact compressed target."

    for number, wanted, expected in [
        3UL, None, target
        4UL, Some baseAsset.Hash, patch
        5UL, Some asset.Hash, target
    ] do
        let id = PhantomTransferId number
        let! selected = storage.StartDownload(id, asset, wanted)
        let change = ok selected
        Expect.equal change (if number = 4UL then Some delta else None) "Only exact base selects patch."

        let lease = http.Admit(owner, id, asset, false, change)
        use output = new MemoryStream()
        let! result = http.Serve(request lease.Token false None output)
        Expect.equal result (Ok ()) "Body served."
        Expect.sequenceEqual (output.ToArray()) expected "Correct transport body."

        do! http.Cancel id
        do! storage.Cancel id

    let different = Array.copy target
    different[different.Length - 1] <- different[different.Length - 1] ^^^ 1uy
    let invalidTarget =
        PhantomManifest.create
            options.Limits
            (AssetHash.create(SHA256.HashData different) |> ok)
            (AppearanceGeneration.create 3UL |> ok)
            2u
            (uint32 target.Length)
            (uint32 rawSize)
            2u
        |> ok
    let badDelta =
        PhantomDelta.create invalidTarget baseAsset.Hash delta.Hash (uint32 patch.Length)
        |> ok
    let! rejected = upload (PhantomTransferId 6UL) invalidTarget (Some badDelta) patch
    Expect.isError rejected "Valid patch cannot publish a different target hash."
    Expect.isFalse (File.Exists(Path.Combine(options.StoragePath, invalidTarget.Hash.Hex + ".zst"))) "Rejected target not committed."

    let bad = Array.copy patch
    bad[bad.Length - 1] <- bad[bad.Length - 1] ^^^ 1uy
    let! corrupt = upload (PhantomTransferId 7UL) invalidTarget (Some badDelta) bad
    Expect.isError corrupt "Patch transport hash checked."

    let absent = AssetHash.create(Array.create 32 123uy) |> ok
    let missingDelta =
        PhantomDelta.create invalidTarget absent delta.Hash (uint32 patch.Length)
        |> ok
    let! missing = storage.StartUpload(PhantomTransferId 8UL, invalidTarget, Some missingDelta)
    Expect.isError missing "Missing base requires full fallback before receiving body."

    let mutable ignored = Array.empty<byte>
    Expect.isFalse
        (PhantomDeltaCodec.TryApply(
            basis, patch, rawSize - 1,
            options.Limits.RawBytes, options.Limits.CompressedBytes, &ignored))
        "Exact raw bound checked."
})

let tests = testList "Phantom HTTP" [
    fixture "HTTP diagnostics preserve real upload phases and omit capability" (fun _ storage http -> task {
        let lines = Collections.Concurrent.ConcurrentQueue<string>()
        let collector = ContinuousDiagnostics.TryStart(Func<string, DiagnosticOperationResult>(fun line ->
            lines.Enqueue line
            DiagnosticOperationResult.Success)).Match(
                (fun collector -> collector), (fun () -> failtest "Invalid diagnostic test sink."))
        use cleanup = collector
        let owner = Guid.NewGuid()
        let bytes = Array.init 65539 (fun index -> byte index)
        let asset = manifest bytes
        let id = PhantomTransferId 918273UL
        let! _ = storage.StartUpload(id, asset, None)
        let lease = http.Admit(owner, id, asset, true, None)
        use input = new MemoryStream(bytes)
        let! result = http.Serve(request lease.Token true (Some(int64 bytes.Length)) input)
        Expect.equal result (Ok ()) "Instrumented upload succeeds"
        collector.Dispose()
        let phases = ResizeArray<string>()
        for line in lines do
            Expect.isFalse (line.Contains lease.Token) "Capability must never be logged"
            use record = Text.Json.JsonDocument.Parse line
            let root = record.RootElement
            if root.GetProperty("kind").GetString() = "phantom" then
                let fields = root.GetProperty("fields")
                let mutable identity = Unchecked.defaultof<Text.Json.JsonElement>
                if fields.GetProperty("event").GetString() = "http" && fields.TryGetProperty("owner", &identity) && identity.GetString() = string owner then
                    Expect.equal (fields.GetProperty("transfer").GetUInt64()) id.Value "Transfer correlation"
                    let phase = fields.GetProperty("phase").GetString()
                    phases.Add phase
                    if phase = "body_complete" then
                        Expect.equal (fields.GetProperty("progress").GetInt32()) bytes.Length "Whole body measured"
                        Expect.isGreaterThan (fields.GetProperty("storage_ms").GetDouble()) 0. "Storage timing present"
        Expect.isTrue (phases.Contains "claimed") "Request start"
        Expect.isTrue (phases.Contains "first_body") "First body chunk"
        Expect.isTrue (phases.Contains "body_complete") "Completion"
    })
    deltaFixture
    fixture "capability binds direction and exact size, is single use, and never exposes a hash URL" (fun _ storage http -> task {
        let bytes = Array.init 65539 (fun index -> byte index)
        let asset = manifest bytes
        let id = PhantomTransferId 1UL
        let! cold = storage.StartUpload(id, asset, None)
        Expect.equal cold (Ok false) "Admission prepared temporary file."
        let lease = http.Admit(Guid.NewGuid(), id, asset, true, None)
        Expect.equal lease.Token.Length 64 "256 random bits."
        use input = new MemoryStream(bytes)
        let! unknown = http.Serve(request asset.Hash.Hex true (Some(int64 bytes.Length)) input)
        Expect.equal unknown (Error PhantomHttpError.Capability) "Content hash is not authorization."
        let! wrongMethod = http.Serve(request lease.Token false None input)
        Expect.equal wrongMethod (Error PhantomHttpError.Capability) "Direction cannot change."
        let! wrongSize = http.Serve(request lease.Token true (Some 4L) input)
        Expect.equal wrongSize (Error PhantomHttpError.Length) "No body read for mismatched size."
        Expect.equal input.Position 0L "Rejected requests do no I/O."
        let! success = http.Serve(request lease.Token true (Some(int64 bytes.Length)) input)
        Expect.equal success (Ok ()) "Valid request can still use admission."
        let! repeated = http.Serve(request lease.Token true (Some(int64 bytes.Length)) input)
        Expect.equal repeated (Error PhantomHttpError.Capability) "No second body."
        Expect.equal lease.Completion.Result (Ok ()) "One terminal completion."
        Expect.equal lease.Progress bytes.Length "Full persisted size."
    })
    fixture "hash mismatch and truncated bodies cannot publish partial files" (fun options storage http -> task {
        for number, body in [1UL, [|8uy;8uy;8uy;8uy|]; 2UL, [|1uy|]] do
            let asset = manifest [|1uy;2uy;3uy;4uy|]
            let id = PhantomTransferId number
            let! _ = storage.StartUpload(id, asset, None)
            let lease = http.Admit(Guid.NewGuid(), id, asset, true, None)
            use input = new MemoryStream(body)
            let! outcome = http.Serve(request lease.Token true (Some 4L) input)
            Expect.isError outcome "Neither truncated nor corrupt data is ready."
            do! http.Cancel id
            do! storage.Cancel id
            Expect.equal (Directory.GetFiles(options.StoragePath, "*.tmp").Length) 0 "Partial removed."
            Expect.equal (Directory.GetFiles(options.StoragePath, "*.zst").Length) 0 "No asset published."
    })
    fixture "revocation interrupts active I/O and duplicate requests cannot steal it" (fun _ storage http -> task {
        let id = PhantomTransferId 1UL
        let asset = manifest [|1uy;2uy;3uy;4uy|]
        let! _ = storage.StartUpload(id, asset, None)
        let lease = http.Admit(Guid.NewGuid(), id, asset, true, None)
        use input = new BlockedBody()
        let running = http.Serve(request lease.Token true (Some 4L) input)
        do! input.Entered.WaitAsync(TimeSpan.FromSeconds 2.)
        use duplicate = new MemoryStream([|1uy;2uy;3uy;4uy|])
        let! rejected = http.Serve(request lease.Token true (Some 4L) duplicate)
        Expect.equal rejected (Error PhantomHttpError.Capability) "Only first request owns body."
        do! (http.Cancel id).WaitAsync(TimeSpan.FromSeconds 2.)
        let! stopped = running
        Expect.equal stopped (Error PhantomHttpError.Canceled) "Abort reaches pending read."
        Expect.equal lease.Completion.Result stopped "Cancellation is observable to actor."
    })
    fixture "slow upload does not prevent another transfer's disk I/O" (fun _ storage http -> task {
        let blockedAsset = manifest [|1uy;2uy;3uy;4uy|]
        let blockedId, fastId = PhantomTransferId 1UL, PhantomTransferId 2UL
        let! _ = storage.StartUpload(blockedId, blockedAsset, None)
        let slow = http.Admit(Guid.NewGuid(), blockedId, blockedAsset, true, None)
        use body = new BlockedBody()
        let waiting = http.Serve(request slow.Token true (Some 4L) body)
        do! body.Entered.WaitAsync(TimeSpan.FromSeconds 2.)
        let bytes = Array.create 32768 9uy
        let fastAsset = manifest bytes
        let! _ = storage.StartUpload(fastId, fastAsset, None)
        let fast = http.Admit(Guid.NewGuid(), fastId, fastAsset, true, None)
        use ready = new MemoryStream(bytes)
        let! finished = (http.Serve(request fast.Token true (Some(int64 bytes.Length)) ready)).WaitAsync(TimeSpan.FromSeconds 2.)
        Expect.equal finished (Ok ()) "Independent upload completes."
        Expect.isFalse waiting.IsCompleted "First body is still blocked."
        do! http.Cancel blockedId
    })
    fixture "unclaimed capability is revoked immediately" (fun _ storage http -> task {
        let asset = manifest [|1uy|]
        let id = PhantomTransferId 1UL
        let! _ = storage.StartUpload(id, asset, None)
        let lease = http.Admit(Guid.NewGuid(), id, asset, true, None)
        do! http.Cancel id
        use input = new MemoryStream([|1uy|])
        let! denied = http.Serve(request lease.Token true (Some 1L) input)
        Expect.equal denied (Error PhantomHttpError.Capability) "No request after departure."
        Expect.isError lease.Completion.Result "Actor can drain canceled admission."
    })
    fixture "ordinary body IO failure remains typed and releases the claimed capability" (fun _ storage http -> task {
        let id = PhantomTransferId 1UL
        let asset = manifest [|1uy;2uy;3uy;4uy|]
        let! _ = storage.StartUpload(id, asset, None)
        let lease = http.Admit(Guid.NewGuid(), id, asset, true, None)
        use body = new FailedBody(IOException "dependency read failure")
        let! result = http.Serve(request lease.Token true (Some 4L) body)
        Expect.equal result (Error PhantomHttpError.Io) "Expected stream failure has stable wire category."
        Expect.equal (PhantomHttpError.message PhantomHttpError.Io) "HTTP I/O" "Wire text unchanged."
        Expect.isFalse http.OwnerFailure.IsCompleted "Ordinary IO does not poison registry."
        do! (http.Cancel id).WaitAsync(TimeSpan.FromSeconds 2.)
        do! storage.Cancel id
        let! next = storage.StartUpload(PhantomTransferId 2UL, asset, None)
        Expect.equal next (Ok false) "Failed body has not committed and reservation is reusable."
    })
    fixture "unexpected body fault settles lifetime and closes registry without a retryable rejection" (fun _ storage http -> task {
        let id = PhantomTransferId 1UL
        let asset = manifest [|1uy;2uy;3uy;4uy|]
        let! _ = storage.StartUpload(id, asset, None)
        let lease = http.Admit(Guid.NewGuid(), id, asset, true, None)
        let original = InvalidOperationException "unexpected dependency fault"
        use body = new FailedBody(original)
        let mutable seen = None
        try
            let! _ = http.Serve(request lease.Token true (Some 4L) body)
            ()
        with error ->
            seen <- Some error
        Expect.isTrue (seen |> Option.exists (fun error -> Object.ReferenceEquals(error, original))) "Original fault observed."
        let! failed = http.OwnerFailure.WaitAsync(TimeSpan.FromSeconds 2.)
        Expect.isTrue (Object.ReferenceEquals(failed, original)) "Lifecycle owner receives original fault."
        Expect.isTrue lease.Completion.IsFaulted "Lease has no fabricated expected rejection."
        do! (http.Cancel id).WaitAsync(TimeSpan.FromSeconds 2.)
        Expect.isTrue lease.Completion.IsFaulted "Cancel cannot replace first completion."
        use retry = new MemoryStream([|1uy;2uy;3uy;4uy|])
        let! denied = http.Serve(request lease.Token true (Some 4L) retry)
        Expect.equal denied (Error PhantomHttpError.Capability) "Revoked capability preserves HTTP contract."
    })
    fixture "HTTP cancellation after canonical commit retains content and cannot prove rollback" (fun options storage _ -> task {
        use stop = new CancellationTokenSource()
        let wrapped = {
            storage with
                WriteChunk = fun args -> task {
                    let! written = storage.WriteChunk args
                    match written with
                    | Ok true -> stop.Cancel()
                    | Ok false
                    | Error _ -> ()

                    return written
                }
        }
        let http = PhantomHttp.create options wrapped
        try
            let bytes = [|1uy;2uy;3uy;4uy|]
            let asset = manifest bytes
            let id = PhantomTransferId 1UL
            let! _ = storage.StartUpload(id, asset, None)
            let lease = http.Admit(Guid.NewGuid(), id, asset, true, None)
            use body = new MemoryStream(bytes)
            let! result = http.Serve { request lease.Token true (Some 4L) body with Cancellation = stop.Token }
            Expect.equal result (Error PhantomHttpError.Canceled) "Existing canceled outcome retained."
            Expect.sequenceEqual (File.ReadAllBytes(Path.Combine(options.StoragePath, asset.Hash.Hex + ".zst"))) bytes "Durable content remains committed."
            do! http.Cancel id
            do! storage.Cancel id
            let! warm = storage.StartUpload(PhantomTransferId 2UL, asset, None)
            Expect.equal warm (Ok true) "Whole manifest retry observes canonical cache; no second body."
        finally
            http.Dispose().GetAwaiter().GetResult()
    })

    fixture "storage cleanup IO fault after commit faults HTTP lease instead of HTTP IO rejection" (fun options _ _ -> task {
        let directory = Path.Combine(options.StoragePath, "postcommit")
        let store = PhantomStorage.create { options with StoragePath = directory }
        let http = PhantomHttp.create options store
        let bytes = [|1uy;2uy;3uy;4uy|]
        let asset = manifest bytes
        let id = PhantomTransferId 1UL
        let! _ = store.StartUpload(id, asset, None)
        let temporary = Directory.GetFiles(directory, "*.tmp") |> Array.exactlyOne
        Directory.CreateDirectory(temporary + ".delta") |> ignore
        let lease = http.Admit(Guid.NewGuid(), id, asset, true, None)
        use body = new MemoryStream(bytes)
        let mutable observed = None
        try
            let! _ = http.Serve(request lease.Token true (Some 4L) body)
            ()
        with error ->
            observed <- Some error
        Expect.isSome observed "Cleanup fault is not an ordinary body IOException result."
        let! original = store.OwnerFailure.WaitAsync(TimeSpan.FromSeconds 2.)
        let! forwarded = http.OwnerFailure.WaitAsync(TimeSpan.FromSeconds 2.)
        Expect.isTrue (Object.ReferenceEquals(original, forwarded)) "Original storage fault crosses lifetime boundary."
        Expect.isTrue (observed |> Option.exists (fun error -> Object.ReferenceEquals(error, original))) "Caller receives lifecycle fault."
        Expect.isTrue lease.Completion.IsFaulted "No retryable lease result after committed effect."
        do! (http.Cancel id).WaitAsync(TimeSpan.FromSeconds 2.)
        do! http.Dispose()
        let mutable released = false
        try
            do! (store.Dispose()).WaitAsync(TimeSpan.FromSeconds 2.)
        with error ->
            released <- Object.ReferenceEquals(error, original)
        Expect.isTrue released "Storage teardown reports original fault after cleanup."
        Expect.sequenceEqual (File.ReadAllBytes(Path.Combine(directory, asset.Hash.Hex + ".zst"))) bytes "Canonical content still committed."
    })

]
