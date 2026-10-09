namespace Dreamsleeve.Server.Infrastructure

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Core

/// Metadata is worker-owned; borrowed leases serialize detached file I/O.
[<RequireQualifiedAccess>]
module PhantomStorage =
    let private meter = new System.Diagnostics.Metrics.Meter("Dreamsleeve.PhantomDelta")
    let private applyDuration = meter.CreateHistogram<double>("phantom.delta.apply", "ms")
    type private Patch = { Descriptor: PhantomDelta; Path: string }
    type private Entry = {
        Path: string; Size: int64; mutable Touched: int64; mutable Pins: int
        mutable Verified: bool; mutable Ram: byte array option; Patch: Patch option
    }
    type private Lease = { Gate: SemaphoreSlim; mutable Closed: bool; mutable Users: int }
    type private Upload = { Manifest: PhantomManifest; Delta: PhantomDelta option; Basis: Entry option; Reserved: int64; Path: string; File: FileStream; Hash: IncrementalHash; mutable Offset: int; Lease: Lease }
    type private Download = { Entry: Entry; Size: int64; File: FileStream; Lease: Lease }
    type private Transfer = Upload of Upload | Download of Download
    type private Work = { Run: unit -> unit; Fail: exn -> unit }

    // Filesystem adapters translate only ordinary dependency failures.
    let inline private io ([<InlineIfLambda>] operation: unit -> 'a) =
        try Ok (operation())
        with
        | :? IOException as error -> Error (PhantomStorageError.Io error)
        | :? UnauthorizedAccessException as error -> Error (PhantomStorageError.Io error)
        | :? System.Security.SecurityException as error -> Error (PhantomStorageError.Io error)

    let private ioAsync (operation: unit -> Task<'a>) = task {
        try
            let! value = operation()
            return Ok value
        with
        | :? IOException as error -> return Error (PhantomStorageError.Io error)
        | :? UnauthorizedAccessException as error -> return Error (PhantomStorageError.Io error)
        | :? System.Security.SecurityException as error -> return Error (PhantomStorageError.Io error)
    }

    let private lease () = { Gate = new SemaphoreSlim(1, 1); Closed = false; Users = 0 }
    let private borrow value = Interlocked.Increment(&value.Users) |> ignore; value
    let private release value =
        value.Gate.Release() |> ignore
        if Interlocked.Decrement(&value.Users) = 0 && value.Closed then value.Gate.Dispose()
    let private gate = function Upload upload -> upload.Lease | Download download -> download.Lease

    /// Reader ownership transfers to the operation and is disposed after verification
    /// or RAM fill. Production uses File.OpenRead; tests can control this dependency.
    let createWithFileReader (options: PhantomOptions) (openRead: string -> Stream) : PhantomStoragePort =
        let directory = Path.GetFullPath options.StoragePath
        let entries = Dictionary<AssetHash, Entry>()
        let transfers = Dictionary<PhantomTransferId, Transfer>()
        let reconstruction = new SemaphoreSlim(1, 1)
        let failure = TaskCompletionSource<exn>(TaskCreationOptions.RunContinuationsAsynchronously)
        let admission = obj()
        let mutable closing = false
        let cost (entry: Entry) = entry.Size + (entry.Patch |> Option.map (fun p -> int64 p.Descriptor.CompressedBytes) |> Option.defaultValue 0L)
        let mutable used, reserved, ram = 0L, 0L, 0L
        let mutable initialized = false
        let channel = Channel.CreateBounded<Work>(BoundedChannelOptions(options.MaxTransfers * 4 + 16,
                                                        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait))
        let time () = Environment.TickCount64
        let failOwner error =
            lock admission (fun () ->
                closing <- true
                failure.TrySetResult error |> ignore
                channel.Writer.TryComplete() |> ignore)
        let safeDelete (path: string) = io (fun () -> File.Delete(Path.Combine(directory, Path.GetFileName path)))
        let cleanup paths =
            for path in paths do
                match safeDelete path with
                | Ok () -> ()
                | Error (PhantomStorageError.Io error) -> failOwner error
                | Error _ -> ()
        let remove key (entry: Entry) =
            cleanup (seq { yield entry.Path; match entry.Patch with Some patch -> yield patch.Path | None -> () })
            if not failure.Task.IsCompleted then
                used <- used - cost entry
                entry.Ram |> Option.iter (fun bytes -> ram <- ram - int64 bytes.Length)
                entries.Remove key |> ignore
        let clean () =
            let expired = entries |> Seq.filter (fun pair -> pair.Value.Pins = 0 && time() - pair.Value.Touched >= int64 options.CacheTtlSeconds * 1000L) |> Seq.toArray
            for pair in expired do
                if not failure.Task.IsCompleted then remove pair.Key pair.Value
        let makeRoom size =
            clean()
            let partials = transfers.Values |> Seq.sumBy (function Upload _ -> 1 | Download _ -> 0)
            let oldest = entries |> Seq.filter (fun pair -> pair.Value.Pins = 0) |> Seq.sortBy (fun pair -> pair.Value.Touched) |> Seq.toArray
            let mutable cursor = 0
            while not failure.Task.IsCompleted && (used + reserved + size > options.DiskBytes || entries.Count + partials >= options.CacheEntries) && cursor < oldest.Length do
                let pair = oldest[cursor]
                remove pair.Key pair.Value
                cursor <- cursor + 1
            not failure.Task.IsCompleted && used + reserved + size <= options.DiskBytes && entries.Count + partials < options.CacheEntries
        let initialize () =
            if initialized then Ok ()
            else
                let mutable outcome = io (fun () -> Directory.CreateDirectory directory |> ignore)
                for pattern in ["*.tmp"; "*.delta"; "*.zst"] do
                    if Result.isOk outcome then
                        match io (fun () -> Directory.GetFiles(directory, pattern)) with
                        | Error error -> outcome <- Error error
                        | Ok paths ->
                            for path in paths do
                                if Result.isOk outcome then
                                    if pattern <> "*.zst" then outcome <- safeDelete path
                                    else
                                        let name = Path.GetFileNameWithoutExtension path
                                        let mutable hash = None
                                        if name.Length = 64 then
                                            let bytes = Array.zeroCreate<byte> 32
                                            let mutable consumed, written = 0, 0
                                            if Convert.FromHexString(name, bytes.AsSpan(), &consumed, &written) = System.Buffers.OperationStatus.Done then
                                                hash <- AssetHash.create bytes |> Result.toOption
                                        match io (fun () -> let file = FileInfo path in file.Length, file.LastWriteTimeUtc) with
                                        | Error error -> outcome <- Error error
                                        | Ok (size, touched) ->
                                            match hash with
                                            | Some key when size > 0L && size <= int64 options.Limits.CompressedBytes && makeRoom size ->
                                                let age = max 0L (int64 (DateTime.UtcNow - touched).TotalMilliseconds)
                                                entries[key] <- { Path = path; Size = size; Touched = time() - age; Pins = 0; Verified = false; Ram = None; Patch = None }
                                                used <- used + size
                                            | _ -> outcome <- safeDelete path
                match outcome with
                | Ok () -> initialized <- true; clean(); Ok ()
                | Error error ->
                    // No leases exist yet; retry rescans empty metadata. Completed
                    // filesystem deletions remain committed cache maintenance.
                    entries.Clear(); used <- 0L; ram <- 0L
                    Error error
        let verify hash (entry: Entry) =
            let verified =
                if entry.Verified then Ok true
                else io (fun () -> use stream = openRead entry.Path in stream.Length = entry.Size && SHA256.HashData stream = AssetHash.bytes hash)
            match verified with
            | Error error -> Error error
            | Ok false -> Error PhantomStorageError.CacheHashMismatch
            | Ok true -> entry.Verified <- true; entry.Touched <- time(); Ok ()
        let closeTransfer id expected =
            match transfers.TryGetValue id with
            | true, current when Object.ReferenceEquals(current, expected) ->
                let value = gate current
                value.Closed <- true
                transfers.Remove id |> ignore
                match current with
                | Upload upload ->
                    reserved <- reserved - upload.Reserved
                    upload.Basis |> Option.iter (fun entry -> entry.Pins <- entry.Pins - 1)
                    match io (fun () -> upload.File.Dispose()) with
                    | Error (PhantomStorageError.Io error) -> failOwner error
                    | Ok () | Error _ -> ()
                    upload.Hash.Dispose()
                    cleanup [upload.Path + ".delta"; upload.Path]
                | Download download ->
                    download.Entry.Pins <- download.Entry.Pins - 1
                    download.Entry.Touched <- time()
                    match io (fun () -> download.File.Dispose()) with
                    | Error (PhantomStorageError.Io error) -> failOwner error
                    | Ok () | Error _ -> ()
            | _ -> ()
        let workerLoop () : Task = task {
            let mutable running = true
            while running do
                let! available = channel.Reader.WaitToReadAsync().AsTask()
                if not available then running <- false
                else
                    let mutable work = Unchecked.defaultof<Work>
                    while channel.Reader.TryRead(&work) do
                        if failure.Task.IsCompleted then work.Fail failure.Task.Result
                        else
                            // Lifetime boundary: stop the owner and settle accepted
                            // replies; never keep using potentially partial mutation.
                            try work.Run()
                            with error -> failOwner error; work.Fail error
                        if failure.Task.IsCompleted then work.Fail failure.Task.Result
        }
        let worker = Task.Run(Func<Task>(workerLoop))
        let submit control operation =
            let reply = TaskCompletionSource<Result<'a, PhantomStorageError>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let work = {
                Run = fun () ->
                    // Cleanup of an unadmitted/closed lease must not retry failed
                    // initialization or create filesystem resources during shutdown.
                    let result = if control then operation() else initialize() |> Result.bind operation
                    if failure.Task.IsCompleted then reply.TrySetException failure.Task.Result |> ignore
                    else reply.TrySetResult result |> ignore
                Fail = fun error -> reply.TrySetException error |> ignore
            }
            let wait = lock admission (fun () ->
                if failure.Task.IsCompleted then work.Fail failure.Task.Result; false
                elif closing && not control then reply.TrySetResult(Error PhantomStorageError.Closed) |> ignore; false
                elif control then true
                elif channel.Writer.TryWrite work then false
                else reply.TrySetResult(Error PhantomStorageError.QueueFull) |> ignore; false)
            if wait then task {
                try do! channel.Writer.WriteAsync(work).AsTask()
                with :? ChannelClosedException ->
                    if failure.Task.IsCompleted then work.Fail failure.Task.Result
                    else reply.TrySetResult(Error PhantomStorageError.Closed) |> ignore
                return! reply.Task
            }
            else reply.Task
        let enqueue operation = submit false operation
        let control operation = submit true operation
        let startUpload (id, manifest: PhantomManifest, change: PhantomDelta option) = enqueue (fun () ->
            clean()
            if transfers.ContainsKey id || transfers.Count >= options.MaxTransfers then Error PhantomStorageError.TransferLimit
            else
                match entries.TryGetValue manifest.Hash with
                | true, entry when entry.Size = int64 manifest.CompressedBytes ->
                    match verify manifest.Hash entry with
                    | Ok () -> Ok true
                    | Error error ->
                        if entry.Pins = 0 then remove manifest.Hash entry
                        Error error
                | _ ->
                    let basis =
                        match change with
                        | None -> Ok None
                        | Some delta ->
                            match entries.TryGetValue delta.BaseHash with
                            | true, entry -> verify delta.BaseHash entry |> Result.map (fun () -> Some entry)
                            | _ -> Error PhantomStorageError.DeltaBaseUnavailable
                    match basis with
                    | Error error -> Error error
                    | Ok previous ->
                        previous |> Option.iter (fun entry -> entry.Pins <- entry.Pins + 1)
                        let mutable retained = false
                        try
                            let reservation = int64 manifest.CompressedBytes + (change |> Option.map (fun d -> int64 d.CompressedBytes) |> Option.defaultValue 0L)
                            if not (makeRoom reservation) then Error PhantomStorageError.DiskQuota
                            else
                                let path = Path.Combine(directory, $"{id.Value}-{Guid.NewGuid():N}.tmp")
                                match io (fun () -> new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, options.ChunkBytes, FileOptions.SequentialScan ||| FileOptions.Asynchronous)) with
                                | Error error -> Error error
                                | Ok file ->
                                    let mutable fileOwned = true
                                    try
                                        let hash = IncrementalHash.CreateHash HashAlgorithmName.SHA256
                                        let mutable hashOwned = true
                                        try
                                            let value = lease()
                                            let mutable gateOwned = true
                                            try
                                                let upload = { Manifest = manifest; Delta = change; Basis = previous; Reserved = reservation; Path = path; File = file; Hash = hash; Offset = 0; Lease = value }
                                                transfers.Add(id, Upload upload)
                                                reserved <- reserved + reservation
                                                retained <- true; fileOwned <- false; hashOwned <- false; gateOwned <- false
                                                Ok false
                                            finally if gateOwned then value.Gate.Dispose()
                                        finally if hashOwned then hash.Dispose()
                                    finally
                                        if fileOwned then file.Dispose(); cleanup [path]
                        finally if not retained then previous |> Option.iter (fun entry -> entry.Pins <- entry.Pins - 1))
        let completeUpload (upload: Upload) =
            if upload.Hash.GetHashAndReset() <> AssetHash.bytes upload.Manifest.Hash then Error PhantomStorageError.HashMismatch
            else
                match io (fun () -> upload.File.Flush true; upload.File.Dispose()) with
                | Error error -> Error error
                | Ok () ->
                    let path = Path.Combine(directory, upload.Manifest.Hash.Hex + ".zst")
                    match entries.TryGetValue upload.Manifest.Hash with
                    | true, entry when entry.Size <> int64 upload.Manifest.CompressedBytes -> Error PhantomStorageError.HashSizeConflict
                    | true, entry -> verify upload.Manifest.Hash entry
                    | false, _ ->
                        let patch = upload.Delta |> Option.map (fun descriptor -> { Descriptor = descriptor; Path = Path.Combine(directory, upload.Manifest.Hash.Hex + ".delta") })
                        let entry = { Path = path; Size = int64 upload.Manifest.CompressedBytes; Touched = time(); Pins = 0; Verified = true; Ram = None; Patch = patch }
                        // Patch first, canonical last: no rejected orphan canonical
                        // file when preparation fails. Metadata is allocated first.
                        let prepared =
                            match patch with
                            | None -> Ok ()
                            | Some patch -> io (fun () -> File.Move(upload.Path + ".delta", patch.Path, true))
                        match prepared with
                        | Error error -> Error error
                        | Ok () ->
                            match io (fun () -> File.Move(upload.Path, path)) with
                            | Error error ->
                                patch |> Option.iter (fun patch -> cleanup [patch.Path])
                                Error error
                            | Ok () ->
                                entries.Add(upload.Manifest.Hash, entry)
                                used <- used + cost entry
                                Ok ()
        let restore (upload: Upload) = task {
            match upload.Delta, upload.Basis with
            | None, _ -> return Ok ()
            | Some delta, Some basis ->
                if upload.Hash.GetHashAndReset() <> AssetHash.bytes delta.Hash then return Error PhantomStorageError.DeltaHashMismatch
                else
                    do! reconstruction.WaitAsync()
                    let started = System.Diagnostics.Stopwatch.GetTimestamp()
                    try
                        let! result = Task.Run(fun () ->
                            match io (fun () -> File.ReadAllBytes basis.Path) with
                            | Error error -> Error error
                            | Ok previous ->
                                let patch = Array.zeroCreate<byte> delta.CompressedBytes
                                match io (fun () -> upload.File.Position <- 0L; upload.File.ReadExactly(patch.AsSpan())) with
                                | Error error -> Error error
                                | Ok () ->
                                    let mutable full = Array.empty<byte>
                                    if not (PhantomDeltaCodec.TryApply(previous, patch, upload.Manifest.RawBytes, options.Limits.RawBytes, options.Limits.CompressedBytes, &full)) then Error PhantomStorageError.DeltaReconstruction
                                    elif full.Length <> upload.Manifest.CompressedBytes || SHA256.HashData full <> AssetHash.bytes upload.Manifest.Hash then Error PhantomStorageError.DeltaTargetHash
                                    else io (fun () -> File.WriteAllBytes(upload.Path + ".delta", patch)) |> Result.map (fun () -> full))
                        match result with
                        | Error reason -> return Error reason
                        | Ok full ->
                            match io (fun () -> upload.File.Position <- 0L; upload.File.SetLength 0L) with
                            | Error error -> return Error error
                            | Ok () ->
                                let! written = ioAsync (fun () -> task { do! upload.File.WriteAsync(full.AsMemory()) })
                                return written |> Result.map (fun () -> upload.Hash.AppendData full)
                    finally
                        applyDuration.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds)
                        reconstruction.Release() |> ignore
            | _ -> return Error PhantomStorageError.DeltaBaseUnavailable
        }
        let writeChunk (id, offset, bytes: byte array) = task {
            let! admitted = enqueue (fun () ->
                match transfers.TryGetValue id with
                | true, Upload upload -> borrow upload.Lease |> ignore; Ok upload
                | _ -> Error PhantomStorageError.UnknownUpload)
            match admitted with
            | Error error -> return Error error
            | Ok upload ->
                do! upload.Lease.Gate.WaitAsync()
                try
                    try
                        let bodyBytes = upload.Delta |> Option.map _.CompressedBytes |> Option.defaultValue upload.Manifest.CompressedBytes
                        let! outcome = task {
                            if upload.Lease.Closed then return Error PhantomStorageError.UploadClosed
                            elif offset <> upload.Offset || bytes.Length = 0 || bytes.Length > options.ChunkBytes || bytes.Length > bodyBytes - offset then return Error PhantomStorageError.ChunkOffsetOrSize
                            else
                                let! written = ioAsync (fun () -> task { do! upload.File.WriteAsync(bytes.AsMemory()) })
                                match written with
                                | Error error -> return Error error
                                | Ok () ->
                                    upload.Hash.AppendData bytes
                                    upload.Offset <- upload.Offset + bytes.Length
                                    if upload.Offset <> bodyBytes then return Ok false
                                    else
                                        let! flushed = ioAsync (fun () -> task { do! upload.File.FlushAsync() })
                                        match flushed with
                                        | Error error -> return Error error
                                        | Ok () ->
                                            let! restored = restore upload
                                            match restored with
                                            | Error error -> return Error error
                                            | Ok () -> return! control (fun () -> completeUpload upload |> Result.map (fun () -> true))
                        }
                        match outcome with
                        | Ok false -> return outcome
                        | _ ->
                            let! _ = control (fun () ->
                                match transfers.TryGetValue id with
                                | true, Upload current when Object.ReferenceEquals(current, upload) -> closeTransfer id transfers[id]
                                | _ -> ()
                                Ok ())
                            return outcome
                    with error ->
                        // Detached operation supervision: invalid state is isolated,
                        // and shutdown waits this gate before disposing resources.
                        failOwner error
                        return! Task.FromException<Result<bool, PhantomStorageError>>(error)
                finally release upload.Lease
        }
        let startDownload (id, manifest: PhantomManifest, basis: AssetHash option) = enqueue (fun () ->
            clean()
            if transfers.ContainsKey id || transfers.Count >= options.MaxTransfers then Error PhantomStorageError.TransferLimit
            else
                match entries.TryGetValue manifest.Hash with
                | true, entry when entry.Size = int64 manifest.CompressedBytes ->
                    match verify manifest.Hash entry with
                    | Error error ->
                        if entry.Pins = 0 then remove manifest.Hash entry
                        Error error
                    | Ok () ->
                        let patch = entry.Patch |> Option.filter (fun patch -> basis = Some patch.Descriptor.BaseHash)
                        let path = patch |> Option.map _.Path |> Option.defaultValue entry.Path
                        let size = patch |> Option.map (fun patch -> int64 patch.Descriptor.CompressedBytes) |> Option.defaultValue entry.Size
                        match io (fun () -> new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, options.ChunkBytes, FileOptions.SequentialScan ||| FileOptions.Asynchronous)) with
                        | Error error -> Error error
                        | Ok file ->
                            let mutable retained = false
                            try
                                let value = lease()
                                let mutable gateOwned = true
                                try
                                    transfers.Add(id, Download { Entry = entry; Size = size; File = file; Lease = value })
                                    entry.Pins <- entry.Pins + 1
                                    retained <- true; gateOwned <- false
                                    Ok (patch |> Option.map _.Descriptor)
                                finally if gateOwned then value.Gate.Dispose()
                            finally if not retained then file.Dispose()
                | _ -> Error PhantomStorageError.AssetUnavailable)
        let readFile (entry: Entry) offset count =
            io (fun () ->
                use file = openRead entry.Path
                if file.Length <> entry.Size then Error PhantomStorageError.CacheSizeChanged
                else
                    file.Position <- int64 offset
                    let bytes = Array.zeroCreate<byte> count
                    let mutable read = 0
                    let mutable ended = false
                    while read < count && not ended do
                        let next = file.Read(bytes, read, count - read)
                        if next = 0 then ended <- true else read <- read + next
                    if read = count then Ok bytes else Error PhantomStorageError.CacheTruncated)
            |> Result.bind id
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
                readFile entry 0 (int entry.Size)
                |> Result.map (fun bytes -> entry.Ram <- Some bytes; ram <- ram + int64 bytes.Length)
        let readChunk (id, offset, destination: Memory<byte>) = task {
            let count = destination.Length
            let! admitted = enqueue (fun () ->
                match transfers.TryGetValue id with
                | true, Download download ->
                    let entry = download.Entry
                    if offset < 0 || count < 1 || count > options.ChunkBytes || int64 offset + int64 count > download.Size then Error PhantomStorageError.ReadBounds
                    else
                        entry.Touched <- time()
                        (if download.Size = entry.Size then cache entry else Ok ())
                        |> Result.map (fun () -> borrow download.Lease |> ignore; download, if download.Size = entry.Size then entry.Ram else None)
                | _ -> Error PhantomStorageError.UnknownDownload)
            match admitted with
            | Error error -> return Error error
            | Ok (download, content) ->
                do! download.Lease.Gate.WaitAsync()
                try
                    try
                        if download.Lease.Closed then return Error PhantomStorageError.DownloadClosed
                        else
                            match content with
                            | Some memory -> memory.AsMemory(offset, count).CopyTo destination; return Ok count
                            | None ->
                                let! read = ioAsync (fun () -> task {
                                    if download.File.Length <> download.Size then return Error PhantomStorageError.CacheSizeChanged
                                    else
                                        download.File.Position <- int64 offset
                                        let mutable read = 0
                                        let mutable ended = false
                                        while read < count && not ended do
                                            let! next = download.File.ReadAsync(destination.Slice(read, count - read))
                                            if next = 0 then ended <- true else read <- read + next
                                        return if read = count then Ok read else Error PhantomStorageError.CacheTruncated
                                })
                                return Result.bind (fun value -> value) read
                    with error ->
                        failOwner error
                        return! Task.FromException<Result<int, PhantomStorageError>>(error)
                finally release download.Lease
        }
        let cancelTask id = task {
            let! pending = control (fun () ->
                match transfers.TryGetValue id with
                | true, transfer -> borrow (gate transfer) |> ignore; Ok (Some transfer)
                | _ -> Ok None)
            match pending with
            | Ok (Some transfer) ->
                let value = gate transfer
                do! value.Gate.WaitAsync()
                try
                    let! _ = control (fun () -> closeTransfer id transfer; Ok ())
                    return ()
                finally release value
            | Ok None -> ()
            | Error (PhantomStorageError.Io error) ->
                failOwner error
                return! Task.FromException<unit>(error)
            | Error _ -> ()
        }
        let disposal = lazy (task {
            lock admission (fun () -> closing <- true)
            let mutable disposalFault = None
            // Lifetime boundary: preserve fault while releasing all resources.
            // Registry ownership transfers only after worker termination.
            try
                let! active = control (fun () -> Ok (transfers.Keys |> Seq.toArray))
                match active with
                | Ok ids -> for id in ids do do! cancelTask id
                | Error (PhantomStorageError.Io error) -> disposalFault <- Some error
                | Error _ -> ()
            with error -> disposalFault <- Some error
            channel.Writer.TryComplete() |> ignore
            do! worker
            let remaining = transfers |> Seq.map (fun pair -> pair.Key, pair.Value) |> Seq.toArray
            for id, transfer in remaining do
                let value = borrow (gate transfer)
                do! value.Gate.WaitAsync()
                try
                    try closeTransfer id transfer
                    with error -> if disposalFault.IsNone then disposalFault <- Some error
                finally release value
            entries.Clear()
            used <- 0L; reserved <- 0L; ram <- 0L
            reconstruction.Dispose()
            match disposalFault with
            | Some error -> return! Task.FromException<unit>(error)
            | None when failure.Task.IsCompleted -> return! Task.FromException<unit>(failure.Task.Result)
            | None -> return ()
        })
        { StartUpload = startUpload; WriteChunk = writeChunk; StartDownload = startDownload
          ReadChunk = readChunk; Cancel = cancelTask; Dispose = (fun () -> disposal.Value); OwnerFailure = failure.Task }

    let create options = createWithFileReader options (fun path -> File.OpenRead path :> Stream)
