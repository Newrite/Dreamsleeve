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

let private ok = function Ok value -> value | Error error -> failtestf "%A" error
let private manifest (bytes: byte array) =
    PhantomManifest.create PhantomOptions.defaults.Limits (AssetHash.create (SHA256.HashData bytes) |> ok)
        (AppearanceGeneration.create 1UL |> ok) 2u (uint32 bytes.Length) (uint32 bytes.Length) 2u |> ok
let private request lease upload length body = {
    Token = lease; Upload = upload; Length = length; Body = body; BeginResponse = ignore; Cancellation = CancellationToken.None
}
let private fixture name run = testCaseAsync name (async {
    let root = Path.Combine(Path.GetTempPath(), "dreamsleeve-http-" + Guid.NewGuid().ToString("N"))
    let options = { PhantomOptions.defaults with StoragePath = root; RamBytes = 0L }
    let storage = PhantomStorage.create options
    let http = PhantomHttp.create options storage
    try do! run options storage http |> Async.AwaitTask
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

let tests = testList "Phantom HTTP" [
    fixture "capability binds direction and exact size, is single use, and never exposes a hash URL" (fun _ storage http -> task {
        let bytes = Array.init 65539 (fun index -> byte index)
        let asset = manifest bytes
        let id = PhantomTransferId 1UL
        let! cold = storage.StartUpload(id, asset)
        Expect.equal cold (Ok false) "Admission prepared temporary file."
        let lease = http.Admit(Guid.NewGuid(), id, asset, true)
        Expect.equal lease.Token.Length 64 "256 random bits."
        use input = new MemoryStream(bytes)
        let! unknown = http.Serve(request asset.Hash.Hex true (Some(int64 bytes.Length)) input)
        Expect.equal unknown (Error "HTTP capability") "Content hash is not authorization."
        let! wrongMethod = http.Serve(request lease.Token false None input)
        Expect.equal wrongMethod (Error "HTTP capability") "Direction cannot change."
        let! wrongSize = http.Serve(request lease.Token true (Some 4L) input)
        Expect.equal wrongSize (Error "HTTP length") "No body read for mismatched size."
        Expect.equal input.Position 0L "Rejected requests do no I/O."
        let! success = http.Serve(request lease.Token true (Some(int64 bytes.Length)) input)
        Expect.equal success (Ok ()) "Valid request can still use admission."
        let! repeated = http.Serve(request lease.Token true (Some(int64 bytes.Length)) input)
        Expect.equal repeated (Error "HTTP capability") "No second body."
        Expect.equal lease.Completion.Result (Ok ()) "One terminal completion."
        Expect.equal lease.Progress bytes.Length "Full persisted size."
    })
    fixture "hash mismatch and truncated bodies cannot publish partial files" (fun options storage http -> task {
        for number, body in [1UL, [|8uy;8uy;8uy;8uy|]; 2UL, [|1uy|]] do
            let asset = manifest [|1uy;2uy;3uy;4uy|]
            let id = PhantomTransferId number
            let! _ = storage.StartUpload(id, asset)
            let lease = http.Admit(Guid.NewGuid(), id, asset, true)
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
        let! _ = storage.StartUpload(id, asset)
        let lease = http.Admit(Guid.NewGuid(), id, asset, true)
        use input = new BlockedBody()
        let running = http.Serve(request lease.Token true (Some 4L) input)
        do! input.Entered.WaitAsync(TimeSpan.FromSeconds 2.)
        use duplicate = new MemoryStream([|1uy;2uy;3uy;4uy|])
        let! rejected = http.Serve(request lease.Token true (Some 4L) duplicate)
        Expect.equal rejected (Error "HTTP capability") "Only first request owns body."
        do! (http.Cancel id).WaitAsync(TimeSpan.FromSeconds 2.)
        let! stopped = running
        Expect.equal stopped (Error "HTTP canceled") "Abort reaches pending read."
        Expect.equal lease.Completion.Result stopped "Cancellation is observable to actor."
    })
    fixture "slow upload does not prevent another transfer's disk I/O" (fun _ storage http -> task {
        let blockedAsset = manifest [|1uy;2uy;3uy;4uy|]
        let blockedId, fastId = PhantomTransferId 1UL, PhantomTransferId 2UL
        let! _ = storage.StartUpload(blockedId, blockedAsset)
        let slow = http.Admit(Guid.NewGuid(), blockedId, blockedAsset, true)
        use body = new BlockedBody()
        let waiting = http.Serve(request slow.Token true (Some 4L) body)
        do! body.Entered.WaitAsync(TimeSpan.FromSeconds 2.)
        let bytes = Array.create 32768 9uy
        let fastAsset = manifest bytes
        let! _ = storage.StartUpload(fastId, fastAsset)
        let fast = http.Admit(Guid.NewGuid(), fastId, fastAsset, true)
        use ready = new MemoryStream(bytes)
        let! finished = (http.Serve(request fast.Token true (Some(int64 bytes.Length)) ready)).WaitAsync(TimeSpan.FromSeconds 2.)
        Expect.equal finished (Ok ()) "Independent upload completes."
        Expect.isFalse waiting.IsCompleted "First body is still blocked."
        do! http.Cancel blockedId
    })
    fixture "unclaimed capability is revoked immediately" (fun _ storage http -> task {
        let asset = manifest [|1uy|]
        let id = PhantomTransferId 1UL
        let! _ = storage.StartUpload(id, asset)
        let lease = http.Admit(Guid.NewGuid(), id, asset, true)
        do! http.Cancel id
        use input = new MemoryStream([|1uy|])
        let! denied = http.Serve(request lease.Token true (Some 1L) input)
        Expect.equal denied (Error "HTTP capability") "No request after departure."
        Expect.isError lease.Completion.Result "Actor can drain canceled admission."
    })
]
