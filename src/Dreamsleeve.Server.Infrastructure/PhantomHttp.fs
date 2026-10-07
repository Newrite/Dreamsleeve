namespace Dreamsleeve.Server.Infrastructure

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Core

/// A byte scheduler, not a congestion window. TCP owns loss recovery and flight.
/// Reservations are one I/O buffer at a time; no frame/poll-frequency rate cap.
type private HttpByteBudget(rate: int) =
    let gate = obj()
    let mutable due = 0L
    member _.Wait(count: int, cancellation: CancellationToken) = task {
        let at = Stopwatch.GetTimestamp()
        let scheduled = lock gate (fun () ->
            let next = max at due
            due <- next + int64 (ceil (float count * float Stopwatch.Frequency / float rate))
            next)
        let delay = Stopwatch.GetElapsedTime(at, scheduled)
        if delay > TimeSpan.Zero then do! Task.Delay(delay, cancellation)
    }

[<RequireQualifiedAccess>]
module PhantomHttp =
    type private Transfer = {
        Id: PhantomTransferId; Owner: Guid; Upload: bool; Lease: PhantomHttpLease
        Stop: CancellationTokenSource; mutable Claimed: bool
        Finished: TaskCompletionSource; Budget: HttpByteBudget
    }
    type private PeerBudget = { Value: HttpByteBudget; mutable Users: int }

    /// Storage is already admitted/pinned when a capability is issued. The
    /// registry owns only HTTP operation lifetime, never model readiness/AOI.
    let create (options: PhantomOptions) (storage: PhantomStoragePort) : PhantomHttpPort =
        let gate = obj()
        let transfers = Dictionary<PhantomTransferId, Transfer>()
        let tokens = Dictionary<string, Transfer>(StringComparer.Ordinal)
        let peers = Dictionary<Guid, PeerBudget>()
        let globalBudget = HttpByteBudget(options.ModelBytesPerSecond)
        let mutable closed = false
        let admit (owner, id, manifest: PhantomManifest, upload) =
            lock gate (fun () ->
                let token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes 32)
                let lease = PhantomHttpLease(token, manifest.CompressedBytes)
                if closed then lease.Finish(Error "HTTP closed")
                else
                    let budget =
                        match peers.TryGetValue owner with
                        | true, existing -> existing.Users <- existing.Users + 1; existing.Value
                        | _ ->
                            let value = HttpByteBudget(options.PlayerModelBytesPerSecond)
                            peers[owner] <- { Value = value; Users = 1 }
                            value
                    let transfer = { Id = id; Owner = owner; Upload = upload; Lease = lease
                                     Stop = new CancellationTokenSource(); Claimed = false
                                     Finished = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Budget = budget }
                    transfers.Add(id, transfer); tokens.Add(token, transfer)
                lease)
        let pump (transfer: Transfer) (request: PhantomHttpRequest) = task {
            use linked = CancellationTokenSource.CreateLinkedTokenSource(request.Cancellation, transfer.Stop.Token)
            let cancellation = linked.Token
            let mutable offset = 0
            let mutable outcome = Ok ()
            // One bounded body buffer, never a model-sized HTTP/protobuf string.
            let buffer = Array.zeroCreate<byte> options.ChunkBytes
            request.BeginResponse transfer.Lease.Size
            while offset < transfer.Lease.Size && Result.isOk outcome && not cancellation.IsCancellationRequested do
                let count = min options.ChunkBytes (transfer.Lease.Size - offset)
                do! transfer.Budget.Wait(count, cancellation)
                do! globalBudget.Wait(count, cancellation)
                if transfer.Upload then
                    let mutable read = 0
                    let mutable ended = false
                    while read < count && not ended do
                        let! received = request.Body.ReadAsync(buffer.AsMemory(read, count - read), cancellation)
                        if received = 0 then ended <- true else read <- read + received
                    if read <> count then outcome <- Error "HTTP truncated"
                    else
                        // The current storage port retains each chunk until its
                        // completion; reuse only after that task has completed.
                        let bytes = if count = buffer.Length then buffer else buffer[..count-1]
                        let! written = storage.WriteChunk(transfer.Id, offset, bytes)
                        match written with
                        | Error reason -> outcome <- Error reason
                        | Ok complete when complete <> (offset + count = transfer.Lease.Size) -> outcome <- Error "HTTP storage completion"
                        | Ok _ -> offset <- offset + count
                else
                    let! content = storage.ReadChunk(transfer.Id, offset, buffer.AsMemory(0, count))
                    match content with
                    | Error reason -> outcome <- Error reason
                    | Ok read when read <> count -> outcome <- Error "HTTP storage length"
                    | Ok _ ->
                        do! request.Body.WriteAsync(buffer.AsMemory(0, count), cancellation)
                        offset <- offset + count
                transfer.Lease.Advance offset
            return if cancellation.IsCancellationRequested then Error "HTTP canceled" else outcome
        }
        let serve (request: PhantomHttpRequest) = task {
            let admitted = lock gate (fun () ->
                match tokens.TryGetValue request.Token with
                | true, transfer when not transfer.Claimed && transfer.Upload = request.Upload ->
                    if transfer.Upload && request.Length <> Some(int64 transfer.Lease.Size) then Error "HTTP length"
                    else
                        transfer.Claimed <- true
                        tokens.Remove request.Token |> ignore
                        Ok transfer
                | _ -> Error "HTTP capability")
            match admitted with
            | Error reason -> return Error reason
            | Ok transfer ->
                let! outcome = task {
                    try return! pump transfer request
                    with
                    | :? OperationCanceledException -> return Error "HTTP canceled"
                    | :? IOException -> return Error "HTTP I/O"
                    | error -> return Error ("HTTP boundary: " + error.GetType().Name)
                }
                transfer.Lease.Finish outcome
                transfer.Finished.TrySetResult() |> ignore
                return outcome
        }
        let cancel id = task {
            let found = lock gate (fun () ->
                match transfers.TryGetValue id with
                | true, transfer ->
                    transfers.Remove id |> ignore
                    tokens.Remove transfer.Lease.Token |> ignore
                    let peer = peers[transfer.Owner]
                    peer.Users <- peer.Users - 1
                    if peer.Users = 0 then peers.Remove transfer.Owner |> ignore
                    if not transfer.Claimed then transfer.Finished.TrySetResult() |> ignore
                    Some transfer
                | _ -> None)
            match found with
            | None -> ()
            | Some transfer ->
                transfer.Stop.Cancel()
                do! transfer.Finished.Task
                transfer.Lease.Finish(Error "HTTP canceled")
                transfer.Stop.Dispose()
        }
        let dispose () = task {
            let ids = lock gate (fun () -> closed <- true; transfers.Keys |> Seq.toArray)
            for id in ids do do! cancel id
        }
        { Admit = admit; Cancel = cancel; Serve = serve; Dispose = dispose }
