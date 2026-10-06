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
        let safeDelete path =
            let absolute = Path.GetFullPath path
            if Path.GetDirectoryName absolute <> directory then invalidOp "Cache path escaped storage directory."
            File.Delete absolute
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
                        try hash <- AssetHash.create (Convert.FromHexString name) |> Result.toOption
                        with :? FormatException -> ()
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
            if not entry.Verified then
                use stream = File.OpenRead entry.Path
                // SHA256.HashData(Stream) streams; it does not allocate the asset.
                let digest = SHA256.HashData stream
                if stream.Length <> entry.Size || digest <> AssetHash.bytes hash then invalidOp "cache hash mismatch"
                entry.Verified <- true
            entry.Touched <- time()
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
                        reply.TrySetResult(Ok (operation())) |> ignore
                with error -> reply.TrySetResult(Error error.Message) |> ignore
            if not (channel.Writer.TryWrite work) then reply.TrySetResult(Error "storage queue full") |> ignore
            reply.Task
        let startUpload (id, manifest: PhantomManifest) = enqueue (fun () ->
            clean()
            if transfers.ContainsKey id || transfers.Count >= options.MaxTransfers then invalidOp "transfer limit"
            match entries.TryGetValue manifest.Hash with
            | true, entry when entry.Size = int64 manifest.CompressedBytes ->
                try verify manifest.Hash entry; true
                with _ -> remove manifest.Hash entry; invalidOp "cache integrity"
            | _ ->
                if not (makeRoom (int64 manifest.CompressedBytes)) then invalidOp "disk quota"
                let path = Path.Combine(directory, $"{id.Value}-{Guid.NewGuid():N}.tmp")
                let file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, options.ChunkBytes, FileOptions.SequentialScan)
                let upload = { Manifest = manifest; Path = path; File = file; Hash = IncrementalHash.CreateHash HashAlgorithmName.SHA256; Offset = 0 }
                transfers[id] <- Upload upload
                reserved <- reserved + int64 manifest.CompressedBytes
                false)
        let writeChunk (id, offset, bytes: byte array) = enqueue (fun () ->
            match transfers.TryGetValue id with
            | true, Upload upload ->
                try
                    if offset <> upload.Offset || bytes.Length = 0 || bytes.Length > options.ChunkBytes
                       || bytes.Length > upload.Manifest.CompressedBytes - offset then invalidOp "chunk offset/size"
                    upload.File.Write(bytes, 0, bytes.Length)
                    upload.Hash.AppendData bytes
                    upload.Offset <- upload.Offset + bytes.Length
                    if upload.Offset <> upload.Manifest.CompressedBytes then false
                    else
                        let digest = upload.Hash.GetHashAndReset()
                        if digest <> AssetHash.bytes upload.Manifest.Hash then invalidOp "hash mismatch"
                        upload.File.Flush true
                        upload.File.Dispose()
                        let path = Path.Combine(directory, upload.Manifest.Hash.Hex + ".zst")
                        match entries.TryGetValue upload.Manifest.Hash with
                        | true, entry ->
                            if entry.Size <> int64 upload.Manifest.CompressedBytes then invalidOp "hash size conflict"
                            verify upload.Manifest.Hash entry
                            safeDelete upload.Path
                        | false, _ ->
                            File.Move(upload.Path, path)
                            entries[upload.Manifest.Hash] <- { Path = path; Size = int64 upload.Manifest.CompressedBytes; Touched = time(); Pins = 0; Verified = true; Ram = None }
                            used <- used + int64 upload.Manifest.CompressedBytes
                        upload.Hash.Dispose()
                        reserved <- reserved - int64 upload.Manifest.CompressedBytes
                        transfers.Remove id |> ignore
                        true
                with error -> cancel id; raise error
            | _ -> invalidOp "unknown upload")
        let startDownload (id, manifest: PhantomManifest) = enqueue (fun () ->
            clean()
            if transfers.ContainsKey id || transfers.Count >= options.MaxTransfers then invalidOp "transfer limit"
            match entries.TryGetValue manifest.Hash with
            | true, entry when entry.Size = int64 manifest.CompressedBytes ->
                try verify manifest.Hash entry
                with _ -> remove manifest.Hash entry; invalidOp "cache integrity"
                entry.Pins <- entry.Pins + 1
                transfers[id] <- Download entry
            | _ -> invalidOp "asset unavailable")
        let readChunk (id, offset, count) = enqueue (fun () ->
            match transfers.TryGetValue id with
            | true, Download entry ->
                if offset < 0 || count < 1 || count > options.ChunkBytes || int64 offset + int64 count > entry.Size then invalidOp "read bounds"
                entry.Touched <- time()
                if entry.Ram.IsNone && entry.Size <= options.RamBytes then
                    let oldest = entries.Values |> Seq.filter (fun item -> item.Ram.IsSome) |> Seq.sortBy _.Touched |> Seq.toArray
                    let mutable index = 0
                    while ram + entry.Size > options.RamBytes && index < oldest.Length do
                        let item = oldest[index]
                        ram <- ram - int64 item.Ram.Value.Length
                        item.Ram <- None
                        index <- index + 1
                    // The verified descriptor bounds allocation even if a local
                    // cache file was subsequently truncated or replaced/grown.
                    use file = File.OpenRead entry.Path
                    if file.Length <> entry.Size then invalidOp "cache size changed"
                    let bytes = Array.zeroCreate (int entry.Size)
                    file.ReadExactly(bytes.AsSpan())
                    entry.Ram <- Some bytes
                    ram <- ram + int64 bytes.Length
                let bytes = Array.zeroCreate count
                match entry.Ram with
                | Some content -> Buffer.BlockCopy(content, offset, bytes, 0, count)
                | None ->
                    use file = File.OpenRead entry.Path
                    file.Position <- int64 offset
                    file.ReadExactly(bytes.AsSpan())
                bytes
            | _ -> invalidOp "unknown download")
        let cancelTask id = task { let! _ = enqueue (fun () -> cancel id)
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
