namespace Dreamsleeve.Server.Infrastructure

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Threading.Channels
open System.Threading.Tasks
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Core

/// Content-addressed opaque compressed files. One worker owns registry/cache
/// metadata; each admitted transfer serializes its own asynchronous file I/O.
[<RequireQualifiedAccess>]
module PhantomStorage =
    let private meter = new System.Diagnostics.Metrics.Meter("Dreamsleeve.PhantomDelta")
    let private applyDuration = meter.CreateHistogram<double>("phantom.delta.apply", "ms")
    type private Patch = { Descriptor: PhantomDelta; Path: string }
    type private Entry = {
        Path: string; Size: int64; mutable Touched: int64; mutable Pins: int
        mutable Verified: bool; mutable Ram: byte array option; Patch: Patch option
    }
    type private Upload = { Manifest: PhantomManifest; Delta: PhantomDelta option; Basis: Entry option; Reserved: int64; Path: string; File: FileStream; Hash: IncrementalHash; mutable Offset: int; Gate: Threading.SemaphoreSlim; mutable Closed: bool }
    type private Download = { Entry: Entry; Size: int64; File: FileStream; Gate: Threading.SemaphoreSlim; mutable Closed: bool }
    type private Transfer = Upload of Upload | Download of Download

    let create (options: PhantomOptions) : PhantomStoragePort =
        let directory = Path.GetFullPath options.StoragePath
        let entries = Dictionary<AssetHash, Entry>()
        let transfers = Dictionary<PhantomTransferId, Transfer>()
        // At most one detached reconstruction per store; queued transfers stay on disk.
        let reconstruction = new Threading.SemaphoreSlim(1, 1)
        let cost (entry: Entry) = entry.Size + (entry.Patch |> Option.map (fun p -> int64 p.Descriptor.CompressedBytes) |> Option.defaultValue 0L)
        let mutable used = 0L
        let mutable reserved = 0L
        let mutable ram = 0L
        let mutable initialized = false
        let mutable disposed = false
        let channel = Channel.CreateBounded<unit -> unit>(BoundedChannelOptions(options.MaxTransfers * 4 + 16,
                                                        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait))
        let time () = Environment.TickCount64
        let safeDelete (path: string) =
            // Paths are created here or enumerated directly in this directory.
            // Resolve only their final filename, so deletion cannot escape it.
            File.Delete(Path.Combine(directory, Path.GetFileName path))
        let remove key (entry: Entry) =
            safeDelete entry.Path
            entry.Patch |> Option.iter (fun patch -> safeDelete patch.Path)
            used <- used - cost entry
            entry.Ram |> Option.iter (fun bytes -> ram <- ram - int64 bytes.Length)
            entries.Remove key |> ignore
        let clean () =
            let expired = entries |> Seq.filter (fun pair -> pair.Value.Pins = 0 && time() - pair.Value.Touched >= int64 options.CacheTtlSeconds * 1000L) |> Seq.toArray
            for pair in expired do remove pair.Key pair.Value
        let makeRoom size =
            clean()
            // Download leases pin existing entries; only uploads add a file.
            let partials = transfers.Values |> Seq.sumBy (function Upload _ -> 1 | Download _ -> 0)
            let oldest = entries |> Seq.filter (fun pair -> pair.Value.Pins = 0) |> Seq.sortBy (fun pair -> pair.Value.Touched) |> Seq.toArray
            let mutable cursor = 0
            while (used + reserved + size > options.DiskBytes || entries.Count + partials >= options.CacheEntries) && cursor < oldest.Length do
                let pair = oldest[cursor]
                remove pair.Key pair.Value
                cursor <- cursor + 1
            used + reserved + size <= options.DiskBytes && entries.Count + partials < options.CacheEntries
        let initialize () =
            if not initialized then
                Directory.CreateDirectory directory |> ignore
                for pattern in ["*.tmp"; "*.delta"] do
                    for path in Directory.EnumerateFiles(directory, pattern) do safeDelete path
                for path in Directory.EnumerateFiles(directory, "*.zst") do
                    let name = Path.GetFileNameWithoutExtension path
                    let mutable hash = None
                    if name.Length = 64 then
                        let bytes = Array.zeroCreate<byte> 32
                        let mutable consumed, written = 0, 0
                        if Convert.FromHexString(name, bytes.AsSpan(), &consumed, &written) = System.Buffers.OperationStatus.Done then
                            hash <- AssetHash.create bytes |> Result.toOption
                    let file = FileInfo path
                    match hash with
                    | Some key when file.Length > 0L && file.Length <= int64 options.Limits.CompressedBytes && makeRoom file.Length ->
                        let age = max 0L (int64 (DateTime.UtcNow - file.LastWriteTimeUtc).TotalMilliseconds)
                        entries[key] <- { Path = path; Size = file.Length; Touched = time() - age; Pins = 0; Verified = false; Ram = None; Patch = None }
                        used <- used + file.Length
                    | _ -> safeDelete path
                initialized <- true
                clean()
        let verify hash (entry: Entry) =
            // Both integrity failures and IO failures invalidate the descriptor.
            // Convert filesystem exceptions here so the caller can evict it.
            try
                let valid =
                    if entry.Verified then true
                    else
                        use stream = File.OpenRead entry.Path
                        // Hash the stream without allocating the whole asset.
                        stream.Length = entry.Size && SHA256.HashData stream = AssetHash.bytes hash
                if not valid then Error "cache hash mismatch"
                else
                    entry.Verified <- true
                    entry.Touched <- time()
                    Ok ()
            with error -> Error error.Message
        let cancel id =
            match transfers.TryGetValue id with
            | true, Upload upload ->
                upload.Closed <- true
                upload.File.Dispose()
                upload.Hash.Dispose()
                transfers.Remove id |> ignore
                reserved <- reserved - upload.Reserved
                upload.Basis |> Option.iter (fun entry -> entry.Pins <- entry.Pins - 1)
                safeDelete (upload.Path + ".delta")
                safeDelete upload.Path
            | true, Download download ->
                download.Closed <- true
                download.File.Dispose()
                let entry = download.Entry
                entry.Pins <- entry.Pins - 1
                entry.Touched <- time()
                transfers.Remove id |> ignore
            | false, _ -> ()
        let workerLoop () : Task = task {
            let mutable running = true
            while running do
                let! available = channel.Reader.WaitToReadAsync().AsTask()
                if not available then running <- false
                else
                    let mutable work = Unchecked.defaultof<unit -> unit>
                    while channel.Reader.TryRead(&work) do work()
        }
        let worker = Task.Run(Func<Task>(workerLoop))
        let enqueue operation =
            let reply = TaskCompletionSource<Result<'a, string>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let work () =
                try
                    if disposed then reply.TrySetResult(Error "storage closed") |> ignore
                    else
                        initialize()
                        reply.TrySetResult(operation()) |> ignore
                with error -> reply.TrySetResult(Error error.Message) |> ignore
            if not (channel.Writer.TryWrite work) then reply.TrySetResult(Error "storage queue full") |> ignore
            reply.Task
        let startUpload (id, manifest: PhantomManifest, change: PhantomDelta option) = enqueue (fun () ->
            clean()
            if transfers.ContainsKey id || transfers.Count >= options.MaxTransfers then Error "transfer limit"
            else
                match entries.TryGetValue manifest.Hash with
                | true, entry when entry.Size = int64 manifest.CompressedBytes ->
                    match verify manifest.Hash entry with
                    | Ok () -> Ok true
                    | Error error -> remove manifest.Hash entry; Error error
                | _ ->
                    let basis =
                        match change with
                        | None -> Ok None
                        | Some delta ->
                            match entries.TryGetValue delta.BaseHash with
                            | true, entry -> verify delta.BaseHash entry |> Result.map (fun () -> Some entry)
                            | _ -> Error "delta base unavailable"
                    match basis with
                    | Error error -> Error error
                    | Ok previous ->
                        previous |> Option.iter (fun entry -> entry.Pins <- entry.Pins + 1)
                        let mutable retained = false
                        try
                            let reservation = int64 manifest.CompressedBytes + (change |> Option.map (fun d -> int64 d.CompressedBytes) |> Option.defaultValue 0L)
                            if not (makeRoom reservation) then
                                Error "disk quota"
                            else
                                let path = Path.Combine(directory, $"{id.Value}-{Guid.NewGuid():N}.tmp")
                                let file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, options.ChunkBytes, FileOptions.SequentialScan ||| FileOptions.Asynchronous)
                                let upload = { Manifest = manifest; Delta = change; Basis = previous; Reserved = reservation; Path = path; File = file; Hash = IncrementalHash.CreateHash HashAlgorithmName.SHA256; Offset = 0; Gate = new Threading.SemaphoreSlim(1, 1); Closed = false }
                                transfers[id] <- Upload upload
                                reserved <- reserved + reservation
                                retained <- true
                                Ok false
                        finally
                            if not retained then previous |> Option.iter (fun entry -> entry.Pins <- entry.Pins - 1))
        let completeUpload (upload: Upload) =
            if upload.Hash.GetHashAndReset() <> AssetHash.bytes upload.Manifest.Hash then Error "hash mismatch"
            else
                upload.File.Flush true
                upload.File.Dispose()
                let path = Path.Combine(directory, upload.Manifest.Hash.Hex + ".zst")
                match entries.TryGetValue upload.Manifest.Hash with
                | true, entry when entry.Size <> int64 upload.Manifest.CompressedBytes -> Error "hash size conflict"
                | true, entry ->
                    verify upload.Manifest.Hash entry
                    |> Result.map (fun () -> safeDelete upload.Path; safeDelete (upload.Path + ".delta"))
                | false, _ ->
                    File.Move(upload.Path, path)
                    let patch = upload.Delta |> Option.map (fun descriptor ->
                        let deltaPath = Path.Combine(directory, upload.Manifest.Hash.Hex + ".delta")
                        File.Move(upload.Path + ".delta", deltaPath, true)
                        { Descriptor = descriptor; Path = deltaPath })
                    let entry = { Path = path; Size = int64 upload.Manifest.CompressedBytes; Touched = time(); Pins = 0; Verified = true; Ram = None; Patch = patch }
                    entries[upload.Manifest.Hash] <- entry
                    used <- used + cost entry
                    Ok ()
        let restore (upload: Upload) = task {
            match upload.Delta, upload.Basis with
            | None, _ -> return Ok ()
            | Some delta, Some basis ->
                if upload.Hash.GetHashAndReset() <> AssetHash.bytes delta.Hash then return Error "delta hash mismatch"
                else
                    do! reconstruction.WaitAsync()
                    let started = System.Diagnostics.Stopwatch.GetTimestamp()
                    try
                        let! result = Task.Run(fun () ->
                            let previous = File.ReadAllBytes basis.Path
                            let patch = Array.zeroCreate<byte> delta.CompressedBytes
                            upload.File.Position <- 0L
                            upload.File.ReadExactly(patch.AsSpan())
                            let mutable full = Array.empty<byte>
                            if not (PhantomDeltaCodec.TryApply(previous, patch, upload.Manifest.RawBytes, options.Limits.RawBytes, options.Limits.CompressedBytes, &full)) then Error "delta reconstruction"
                            elif full.Length <> upload.Manifest.CompressedBytes || SHA256.HashData full <> AssetHash.bytes upload.Manifest.Hash then Error "delta target hash"
                            else
                                File.WriteAllBytes(upload.Path + ".delta", patch)
                                Ok full)
                        match result with
                        | Error reason -> return Error reason
                        | Ok full ->
                            upload.File.Position <- 0L
                            upload.File.SetLength 0L
                            do! upload.File.WriteAsync(full.AsMemory())
                            upload.Hash.AppendData full
                            return Ok ()
                    finally
                        applyDuration.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds)
                        reconstruction.Release() |> ignore
            | _ -> return Error "delta base unavailable"
        }
        let writeChunk (id, offset, bytes: byte array) = task {
            let! admitted = enqueue (fun () ->
                match transfers.TryGetValue id with
                | true, Upload upload -> Ok upload
                | _ -> Error "unknown upload")
            match admitted with
            | Error error -> return Error error
            | Ok upload ->
                do! upload.Gate.WaitAsync()
                try
                    let! outcome = task {
                        try
                            let bodyBytes = upload.Delta |> Option.map _.CompressedBytes |> Option.defaultValue upload.Manifest.CompressedBytes
                            if upload.Closed then return Error "upload closed"
                            elif offset <> upload.Offset || bytes.Length = 0 || bytes.Length > options.ChunkBytes
                                 || bytes.Length > bodyBytes - offset then return Error "chunk offset/size"
                            else
                                do! upload.File.WriteAsync(bytes.AsMemory())
                                upload.Hash.AppendData bytes
                                upload.Offset <- upload.Offset + bytes.Length
                                if upload.Offset <> bodyBytes then return Ok false
                                else
                                    do! upload.File.FlushAsync()
                                    let! restored = restore upload
                                    match restored with
                                    | Error reason -> return Error reason
                                    | Ok () -> return! enqueue (fun () -> completeUpload upload |> Result.map (fun () -> true))
                        with error -> return Error error.Message
                    }
                    match outcome with
                    | Ok false -> return outcome
                    | _ ->
                        let! _ = enqueue (fun () ->
                            match outcome with
                            | Ok true ->
                                upload.Closed <- true
                                upload.Hash.Dispose()
                                upload.Basis |> Option.iter (fun entry -> entry.Pins <- entry.Pins - 1)
                                reserved <- reserved - upload.Reserved
                                transfers.Remove id |> ignore
                            | _ -> cancel id
                            Ok ())
                        return outcome
                finally upload.Gate.Release() |> ignore
        }
        let startDownload (id, manifest: PhantomManifest, basis: AssetHash option) = enqueue (fun () ->
            clean()
            if transfers.ContainsKey id || transfers.Count >= options.MaxTransfers then Error "transfer limit"
            else
                match entries.TryGetValue manifest.Hash with
                | true, entry when entry.Size = int64 manifest.CompressedBytes ->
                    match verify manifest.Hash entry with
                    | Error error -> remove manifest.Hash entry; Error error
                    | Ok () ->
                        let patch = entry.Patch |> Option.filter (fun patch -> basis = Some patch.Descriptor.BaseHash)
                        let path = patch |> Option.map _.Path |> Option.defaultValue entry.Path
                        let size = patch |> Option.map (fun patch -> int64 patch.Descriptor.CompressedBytes) |> Option.defaultValue entry.Size
                        let file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, options.ChunkBytes, FileOptions.SequentialScan ||| FileOptions.Asynchronous)
                        entry.Pins <- entry.Pins + 1
                        transfers[id] <- Download { Entry = entry; Size = size; File = file; Gate = new Threading.SemaphoreSlim(1, 1); Closed = false }
                        Ok (patch |> Option.map _.Descriptor)
                | _ -> Error "asset unavailable")
        let readFile (entry: Entry) offset count =
            use file = File.OpenRead entry.Path
            if file.Length <> entry.Size then Error "cache size changed"
            else
                file.Position <- int64 offset
                let bytes = Array.zeroCreate<byte> count
                let mutable read = 0
                let mutable ended = false
                while read < count && not ended do
                    let next = file.Read(bytes, read, count - read)
                    if next = 0 then ended <- true else read <- read + next
                if read = count then Ok bytes else Error "cache truncated"
        let cache (entry: Entry) =
            if entry.Ram.IsSome || entry.Size > options.RamBytes then Ok ()
            else
                let oldest = entries.Values |> Seq.filter (fun item -> item.Ram.IsSome) |> Seq.sortBy _.Touched |> Seq.toArray
                let mutable index = 0
                while ram + entry.Size > options.RamBytes && index < oldest.Length do
                    let item = oldest[index]
                    ram <- ram - int64 item.Ram.Value.Length
                    item.Ram <- None
                    index <- index + 1
                // Validate length before allocating descriptor-sized content.
                readFile entry 0 (int entry.Size)
                |> Result.map (fun bytes ->
                    entry.Ram <- Some bytes
                    ram <- ram + int64 bytes.Length)
        let readChunk (id, offset, destination: Memory<byte>) = task {
            let count = destination.Length
            let! admitted = enqueue (fun () ->
                match transfers.TryGetValue id with
                | true, Download download ->
                    let entry = download.Entry
                    if offset < 0 || count < 1 || count > options.ChunkBytes || int64 offset + int64 count > download.Size then Error "read bounds"
                    else
                        entry.Touched <- time()
                        (if download.Size = entry.Size then cache entry else Ok ()) |> Result.map (fun () -> download, if download.Size = entry.Size then entry.Ram else None)
                | _ -> Error "unknown download")
            match admitted with
            | Error error -> return Error error
            | Ok (download, content) ->
                do! download.Gate.WaitAsync()
                try
                    if download.Closed then return Error "download closed"
                    else
                        match content with
                        | Some memory ->
                            memory.AsMemory(offset, count).CopyTo destination
                            return Ok count
                        | None ->
                            try
                                if download.File.Length <> download.Size then return Error "cache size changed"
                                else
                                    download.File.Position <- int64 offset
                                    let mutable read = 0
                                    let mutable ended = false
                                    while read < count && not ended do
                                        let! next = download.File.ReadAsync(destination.Slice(read, count - read))
                                        if next = 0 then ended <- true else read <- read + next
                                    return if read = count then Ok read else Error "cache truncated"
                            with error -> return Error error.Message
                finally download.Gate.Release() |> ignore
        }
        let cancelTask id = task {
            let! pending = enqueue (fun () ->
                match transfers.TryGetValue id with
                | true, Upload upload -> Ok (Some upload.Gate)
                | true, Download download -> Ok (Some download.Gate)
                | _ -> Ok None)
            match pending with
            | Ok (Some gate) ->
                do! gate.WaitAsync()
                try
                    let! _ = enqueue (fun () -> cancel id; Ok ())
                    return ()
                finally gate.Release() |> ignore
            | _ -> ()
        }
        let dispose () = task {
            let! active = enqueue (fun () -> Ok (transfers.Keys |> Seq.toArray))
            match active with
            | Ok ids -> for id in ids do do! cancelTask id
            | Error _ -> ()
            // Async admission for final cleanup; preserves every accepted work item.
            let reply = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            let finish () =
                try
                    for id in transfers.Keys |> Seq.toArray do cancel id
                    entries.Clear()
                    disposed <- true
                    reply.TrySetResult() |> ignore
                with error -> reply.TrySetException error |> ignore
            do! channel.Writer.WriteAsync(finish).AsTask()
            do! reply.Task
            channel.Writer.TryComplete() |> ignore
            do! worker
        }
        { StartUpload = startUpload; WriteChunk = writeChunk; StartDownload = startDownload
          ReadChunk = readChunk; Cancel = cancelTask; Dispose = dispose }
