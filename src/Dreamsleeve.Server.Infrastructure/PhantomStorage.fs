namespace Dreamsleeve.Server.Infrastructure

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Threading.Channels
open System.Threading.Tasks
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Core

/// Content-addressed opaque compressed files. All filesystem state belongs to
/// one detached worker; callers retain only bounded chunks and completion tasks.
[<RequireQualifiedAccess>]
module PhantomStorage =
    type private Entry = {
        Path: string; Size: int64; mutable Touched: int64; mutable Pins: int
        mutable Verified: bool; mutable Ram: byte array option
    }
    type private Upload = { Manifest: PhantomManifest; Path: string; File: FileStream; Hash: IncrementalHash; mutable Offset: int }
    type private Transfer = Upload of Upload | Download of Entry

    let create (options: PhantomOptions) : PhantomStoragePort =
        let directory = Path.GetFullPath options.StoragePath
        let entries = Dictionary<AssetHash, Entry>()
        let transfers = Dictionary<PhantomTransferId, Transfer>()
        let mutable used = 0L
        let mutable reserved = 0L
        let mutable ram = 0L
        let mutable initialized = false
        let mutable disposed = false
        let channel = Channel.CreateBounded<unit -> unit>(BoundedChannelOptions(options.MaxTransfers * (options.WindowChunks + 4) + 16,
                                                        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait))
        let time () = Environment.TickCount64
        let safeDelete (path: string) =
            // Paths are created here or enumerated directly in this directory.
            // Resolve only their final filename, so deletion cannot escape it.
            File.Delete(Path.Combine(directory, Path.GetFileName path))
        let remove key (entry: Entry) =
            safeDelete entry.Path
            used <- used - entry.Size
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
                for path in Directory.EnumerateFiles(directory, "*.tmp") do safeDelete path
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
                        entries[key] <- { Path = path; Size = file.Length; Touched = time() - age; Pins = 0; Verified = false; Ram = None }
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
                upload.File.Dispose()
                upload.Hash.Dispose()
                transfers.Remove id |> ignore
                reserved <- reserved - int64 upload.Manifest.CompressedBytes
                safeDelete upload.Path
            | true, Download entry ->
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
        let startUpload (id, manifest: PhantomManifest) = enqueue (fun () ->
            clean()
            if transfers.ContainsKey id || transfers.Count >= options.MaxTransfers then Error "transfer limit"
            else
                match entries.TryGetValue manifest.Hash with
                | true, entry when entry.Size = int64 manifest.CompressedBytes ->
                    match verify manifest.Hash entry with
                    | Ok () -> Ok true
                    | Error error -> remove manifest.Hash entry; Error error
                | _ when not (makeRoom (int64 manifest.CompressedBytes)) -> Error "disk quota"
                | _ ->
                    let path = Path.Combine(directory, $"{id.Value}-{Guid.NewGuid():N}.tmp")
                    let file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, options.ChunkBytes, FileOptions.SequentialScan)
                    let upload = { Manifest = manifest; Path = path; File = file; Hash = IncrementalHash.CreateHash HashAlgorithmName.SHA256; Offset = 0 }
                    transfers[id] <- Upload upload
                    reserved <- reserved + int64 manifest.CompressedBytes
                    Ok false)
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
                    |> Result.map (fun () -> safeDelete upload.Path)
                | false, _ ->
                    File.Move(upload.Path, path)
                    entries[upload.Manifest.Hash] <- { Path = path; Size = int64 upload.Manifest.CompressedBytes; Touched = time(); Pins = 0; Verified = true; Ram = None }
                    used <- used + int64 upload.Manifest.CompressedBytes
                    Ok ()
        let writeChunk (id, offset, bytes: byte array) = enqueue (fun () ->
            match transfers.TryGetValue id with
            | true, Upload upload ->
                // Filesystem APIs can throw; convert at the boundary and run the
                // same cleanup for IO failures and ordinary rejected chunks.
                let outcome =
                    try
                        if offset <> upload.Offset || bytes.Length = 0 || bytes.Length > options.ChunkBytes
                           || bytes.Length > upload.Manifest.CompressedBytes - offset then Error "chunk offset/size"
                        else
                            upload.File.Write(bytes, 0, bytes.Length)
                            upload.Hash.AppendData bytes
                            upload.Offset <- upload.Offset + bytes.Length
                            if upload.Offset <> upload.Manifest.CompressedBytes then Ok false
                            else completeUpload upload |> Result.map (fun () -> true)
                    with error -> Error error.Message
                match outcome with
                | Error _ -> cancel id; outcome
                | Ok true ->
                    upload.Hash.Dispose()
                    reserved <- reserved - int64 upload.Manifest.CompressedBytes
                    transfers.Remove id |> ignore
                    outcome
                | Ok false -> outcome
            | _ -> Error "unknown upload")
        let startDownload (id, manifest: PhantomManifest) = enqueue (fun () ->
            clean()
            if transfers.ContainsKey id || transfers.Count >= options.MaxTransfers then Error "transfer limit"
            else
                match entries.TryGetValue manifest.Hash with
                | true, entry when entry.Size = int64 manifest.CompressedBytes ->
                    match verify manifest.Hash entry with
                    | Error error -> remove manifest.Hash entry; Error error
                    | Ok () ->
                        entry.Pins <- entry.Pins + 1
                        transfers[id] <- Download entry
                        Ok ()
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
        let readChunk (id, offset, count) = enqueue (fun () ->
            match transfers.TryGetValue id with
            | true, Download entry ->
                if offset < 0 || count < 1 || count > options.ChunkBytes || int64 offset + int64 count > entry.Size then Error "read bounds"
                else
                    entry.Touched <- time()
                    cache entry |> Result.bind (fun () ->
                        match entry.Ram with
                        | Some content ->
                            let bytes = Array.zeroCreate count
                            Buffer.BlockCopy(content, offset, bytes, 0, count)
                            Ok bytes
                        | None -> readFile entry offset count)
            | _ -> Error "unknown download")
        let cancelTask id = task { let! _ = enqueue (fun () -> cancel id; Ok ())
                                  return () }
        let dispose () = task {
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
