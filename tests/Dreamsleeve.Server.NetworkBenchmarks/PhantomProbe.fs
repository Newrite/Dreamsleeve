module Dreamsleeve.Server.NetworkBenchmarks.PhantomProbe

open System
open System.IO
open System.Collections.Generic
open System.Security.Cryptography
open System.Text.Json
open System.Net.Http
open System.Net.Http.Headers
open System.Threading
open System.Threading.Tasks
open Google.Protobuf
open Dreamsleeve.Protocol.Phantom
open Dreamsleeve.Server.Infrastructure.Interop
open Dreamsleeve.Server.NetworkBenchmarks.Measurements

// Optional opaque envelope workload, independent of game decoding/rendering.
type Config = { Publishers: int; Rate: float; ModelPath: string; RawModelBytes: int; Channels: int; PosePaths: string array; Maximum: int; ClientCacheWarm: bool }
let configuration () =
    match Environment.GetEnvironmentVariable "DREAMSLEEVE_BENCH_PHANTOM" with
    | null | "" -> None
    | path -> Some(JsonSerializer.Deserialize<Config>(File.ReadAllText path))

type private HttpJob = { Id: uint64; Stop: CancellationTokenSource; Task: Task<Result<int,string>>; mutable Accepted: bool }
type private UploadState = HttpJob
type private DownloadState = HttpJob
type private PeerState = {
    mutable PoseRequired: bool; mutable Ready: bool; mutable ReadyAt: float; mutable Due: float; mutable Sequence: uint64
    mutable PublishRequest: uint64; mutable PublishDue: float; mutable Upload: UploadState option
    mutable DownloadRequest: uint64; mutable DownloadDue: float; mutable DownloadPending: bool
    mutable Download: DownloadState option; mutable Cached: bool
    Offers: Dictionary<uint64,uint64>; Seen: Dictionary<uint64,uint64>
    mutable Sent: int64; mutable Missed: int64
}
type Probe(config: Config, authUrl: Uri, allIds: uint64 array, offset: int, stride: int, count: int, now: unit -> float,
           send: int -> byte -> PacketDelivery -> byte array -> bool, fail: string -> unit) =
    let ids = Array.init count (fun index -> allIds[offset + index * stride])
    let publisher index = offset + index * stride < config.Publishers
    let localPublishers = [|0 .. count-1|] |> Array.filter publisher
    let model = File.ReadAllBytes config.ModelPath
    let hash = SHA256.HashData model
    let poses = config.PosePaths |> Array.map(fun path -> ByteString.CopyFrom(File.ReadAllBytes path))
    let poseFor sequence = poses[int ((sequence - 1UL) % uint64 poses.Length)]
    let descriptor = AssetDescriptor(Hash = ByteString.CopyFrom hash, Generation = 1UL, FormatVersion = 2u,
                                    CompressedBytes = uint32 model.Length, RawBytes = uint32 config.RawModelBytes, Channels = uint32 config.Channels)
    let peers = Array.init ids.Length (fun _ -> {
        PoseRequired=false; Ready=false; ReadyAt= -1.; Due=0.; Sequence=0UL; PublishRequest=1000UL; PublishDue=0.; Upload=None
        DownloadRequest=1000000UL; DownloadDue=0.; DownloadPending=false; Download=None; Cached=config.ClientCacheWarm
        Offers=Dictionary(); Seen=Dictionary(); Sent=0L; Missed=0L })
    let policies = ResizeArray<Policy>()
    let pairs = HashSet<struct(int*uint64)>()
    let lastAt = Dictionary<struct(int*uint64),float>()
    let ages, gaps, readyTimes, downloadTimes = Distribution(), Distribution(), Distribution(), Distribution()
    let refusals = Dictionary<string,int>()
    let mutable active = false
    let mutable measuring = false
    let mutable warmupUploadBytes, warmupDownloadBytes = 0L, 0L
    let mutable started, duration = 0., 0.
    let mutable sentBytes, receivedBytes, uploadBytes, downloadBytes = 0L,0L,0L,0L
    let mutable received, stale, offers, admissionDrops = 0L,0L,0L,0L
    let mutable verifiedDownloads = 0
    let mutable unfinishedAtStop = 0
    let mutable subscriptionsAtStop = 0
    let mutable subscriptionSeconds, subscriptionAt = 0., 0.
    let integrateSubscriptions () =
        if measuring then
            let at = now()
            subscriptionSeconds <- subscriptionSeconds + float(peers |> Array.sumBy(fun p -> p.Offers.Count)) * max 0. (at-subscriptionAt)/1000.
            subscriptionAt <- at
    let displayed = Array.init ids.Length (fun _ -> Dictionary<uint64,uint64>())
    let modelOutbox = Array.init ids.Length (fun _ -> Queue<byte array>())
    let flushModels () =
        for index in 0 .. peers.Length-1 do
            let queue = modelOutbox[index]
            let mutable available = true
            while available && queue.Count > 0 do
                if send index 3uy PacketDelivery.ReliableBulk (queue.Peek()) then queue.Dequeue() |> ignore
                else available <- false
            for KeyValue(source,revision) in displayed[index] |> Seq.toArray do
                if available then
                    let packet = ClientAssetPacket(ProtocolVersion=Dreamsleeve.Server.Core.ProtocolCodec.Version,
                                     Displayed=Displayed(PlayerId=source,ViewRevision=revision,Generation=1UL))
                    if send index 3uy PacketDelivery.ReliableBulk (packet.ToByteArray()) then displayed[index].Remove source |> ignore
                    else available <- false
    let asset index (packet: ClientAssetPacket) =
        packet.ProtocolVersion <- Dreamsleeve.Server.Core.ProtocolCodec.Version
        // A reliable request waits behind the bounded transfer window when ENet
        // is full; unreliable poses are dropped separately, never queued here.
        if modelOutbox[index].Count >= 16 then fail "Reliable model outbox limit"; false
        else modelOutbox[index].Enqueue(packet.ToByteArray()); true
    let http = new HttpClient(Timeout = Timeout.InfiniteTimeSpan)
    let httpOrigin = authUrl.GetLeftPart(UriPartial.Authority)
    let beginHttp (transfer: Transfer) =
        let stop = new CancellationTokenSource()
        let operation = task {
            try
                use request = new HttpRequestMessage((if transfer.Upload then HttpMethod.Put else HttpMethod.Get), httpOrigin + "/phantoms/content")
                request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", transfer.HttpToken)
                if transfer.Upload then request.Content <- new ByteArrayContent(model)
                use! response = http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token)
                if int response.StatusCode <> (if transfer.Upload then 204 else 200) then return Error $"HTTP {int response.StatusCode}"
                elif transfer.Upload then return Ok model.Length
                elif response.Content.Headers.ContentLength <> Nullable(int64 model.Length) then return Error "HTTP content length"
                else
                    use! body = response.Content.ReadAsStreamAsync(stop.Token)
                    use digest = IncrementalHash.CreateHash HashAlgorithmName.SHA256
                    let buffer = Array.zeroCreate<byte> 65536
                    let mutable offset = 0
                    let mutable finished = false
                    while not finished && offset <= model.Length do
                        let! count = body.ReadAsync(buffer.AsMemory(), stop.Token)
                        if count = 0 then finished <- true
                        else digest.AppendData(buffer, 0, count); offset <- offset + count
                    if offset <> model.Length || not (digest.GetHashAndReset().AsSpan().SequenceEqual(hash.AsSpan())) then return Error "HTTP body/hash"
                    else return Ok offset
            with
            | :? OperationCanceledException -> return Error "HTTP canceled"
            | :? HttpRequestException -> return Error "HTTP request"
            | :? IOException -> return Error "HTTP I/O"
        }
        { Id = transfer.TransferId; Stop = stop; Task = operation; Accepted = false }
    let releaseJob (job: HttpJob) =
        job.Stop.Cancel()
        if job.Task.IsCompleted then job.Stop.Dispose()
        else job.Task.ContinueWith(fun (_: Task<Result<int,string>>) -> job.Stop.Dispose()) |> ignore
    let finishDownload (peer: PeerState) =
        peer.Download |> Option.iter releaseJob
        peer.Download <- None; peer.DownloadPending <- false
    let refusal reason =
        refusals[reason] <- (match refusals.TryGetValue reason with true,n -> n | _ -> 0) + 1
    do
        if config.Publishers < 1 || config.Publishers > allIds.Length || config.Rate <= 0. || config.Rate > 20.
           || model.Length < 1 || model.Length > 64*1024*1024 || config.RawModelBytes < 1 || config.RawModelBytes > 128*1024*1024
           || config.Channels < 1 || config.Channels > 4096 || config.Maximum < 1 || config.Maximum > 64
           || poses.Length = 0 || poses |> Array.exists(fun pose -> pose.Length < 1 || pose.Length > 128*1024) then
            invalidArg "config" "Require bounded native asset and complete compressed pose payload fixtures."
    member _.ClientCacheWarm = config.ClientCacheWarm
    member _.Prepared = (localPublishers |> Array.forall(fun index -> peers[index].Ready && peers[index].Upload.IsNone)) && (modelOutbox |> Array.forall(fun queue -> queue.Count = 0))
    member _.Prepare() =
        started <- now(); subscriptionAt <- started; active <- true
        for index in 0 .. peers.Length-1 do
            peers[index].PublishDue <- started
            asset index (ClientAssetPacket(Preferences=Preferences(Publish=publisher index,Receive=true,Maximum=uint32 config.Maximum,Distance=4096.f))) |> ignore
    member this.Start() =
        if not active then this.Prepare()
        started <- now(); subscriptionAt <- started; measuring <- true
        warmupUploadBytes <- uploadBytes; warmupDownloadBytes <- downloadBytes
        uploadBytes <- 0L; downloadBytes <- 0L
        for peer in peers do
            if peer.Ready then peer.ReadyAt <- started; peer.Due <- started
    member _.Tick() =
        flushModels()
        if active then
            let at = now()
            for index in 0 .. peers.Length-1 do
                let peer = peers[index]
                if publisher index && not peer.Ready && peer.Upload.IsNone && at >= peer.PublishDue then
                    peer.PublishRequest <- peer.PublishRequest+1UL; peer.PublishDue <- Double.PositiveInfinity
                    asset index (ClientAssetPacket(Publish=Publish(Asset=descriptor,ContextRevision=1UL,RequestId=peer.PublishRequest))) |> ignore
                match peer.Upload with
                | Some upload when upload.Task.IsCompleted ->
                    match upload.Task.Result with
                    | Ok length -> uploadBytes <- uploadBytes + int64 length
                    | Error reason ->
                        refusal reason
                        asset index (ClientAssetPacket(Cancel=Cancel(TransferId=upload.Id))) |> ignore
                        peer.PublishDue <- at + 1000.
                    releaseJob upload
                    peer.Upload <- None
                | _ -> ()
                match peer.Download with
                | Some download when download.Task.IsCompleted && (download.Accepted || Result.isError download.Task.Result) ->
                    match download.Task.Result with
                    | Ok length ->
                        downloadBytes <- downloadBytes + int64 length
                        peer.Cached <- true; verifiedDownloads <- verifiedDownloads + 1
                        downloadTimes.Add(at-started)
                        for KeyValue(source, revision) in peer.Offers do displayed[index][source] <- revision
                    | Error reason ->
                        refusal reason
                        asset index (ClientAssetPacket(Cancel=Cancel(TransferId=download.Id))) |> ignore
                    finishDownload peer
                    peer.DownloadDue <- at + 1000.
                | _ -> ()
                if not peer.Cached && not peer.DownloadPending && peer.Offers.Count>0 && at>=peer.DownloadDue then
                    let source = peer.Offers.Keys |> Seq.head
                    peer.DownloadRequest <- peer.DownloadRequest+1UL; peer.DownloadPending <- true
                    asset index (ClientAssetPacket(Download=Download(PlayerId=source,Generation=1UL,RequestId=peer.DownloadRequest))) |> ignore
                if measuring && peer.Ready && peer.PoseRequired && at>=peer.Due then
                    let interval = 1000./config.Rate
                    let missed = max 0L (int64 (floor ((at-peer.Due)/interval)))
                    peer.Missed <- peer.Missed+missed; peer.Due <- peer.Due+float(missed+1L)*interval
                    peer.Sequence <- peer.Sequence+1UL
                    let packet = ClientPosePacket(ProtocolVersion=Dreamsleeve.Server.Core.ProtocolCodec.Version,
                        Sample=PoseSample(Generation=1UL,ContextRevision=1UL,Sequence=peer.Sequence,SampledAtUs=uint64(at*1000.)+1UL,Payload=poseFor peer.Sequence))
                    let bytes = packet.ToByteArray()
                    if send index 4uy PacketDelivery.SequencedFragmented bytes then
                        peer.Sent <- peer.Sent+1L; sentBytes <- sentBytes+int64 bytes.Length
                    else admissionDrops <- admissionDrops+1L
        flushModels()
    member _.Asset(index:int,packet:ServerAssetPacket) =
        let peer = peers[index]
        if packet.ProtocolVersion<>Dreamsleeve.Server.Core.ProtocolCodec.Version then fail "Asset protocol mismatch"
        match packet.PayloadCase with
        | ServerAssetPacket.PayloadOneofCase.Policy -> policies.Add(packet.Policy.Clone())
        | ServerAssetPacket.PayloadOneofCase.Transfer ->
            let transfer = packet.Transfer
            if transfer.Asset<>descriptor || transfer.TransferId=0UL then fail "Transfer descriptor mismatch"
            if transfer.Upload then
                if transfer.RequestId<>peer.PublishRequest || not (publisher index) then fail "Upload request correlation"
                peer.Upload <- Some(beginHttp transfer)
            else
                if transfer.RequestId<>peer.DownloadRequest || peer.Download.IsSome then fail "Download request correlation"
                peer.Download <- Some(beginHttp transfer)
        | ServerAssetPacket.PayloadOneofCase.Complete ->
            let complete = packet.Complete
            if complete.Upload then
                if complete.RequestId<>peer.PublishRequest then fail "Upload completion request mismatch"
                if complete.Accepted then
                    peer.Ready <- true; peer.ReadyAt <- now(); peer.Due <- now(); readyTimes.Add(now()-started)
                else
                    peer.Upload |> Option.iter releaseJob
                    peer.Upload <- None
                    refusal complete.Reason
                    peer.PublishDue <- if complete.RetryAfterMs>0u then now()+float complete.RetryAfterMs else Double.PositiveInfinity
            else
                if complete.RequestId<>peer.DownloadRequest then fail "Download completion request mismatch"
                if complete.Accepted then
                    match peer.Download with
                    | Some download when download.Id = complete.TransferId -> download.Accepted <- true
                    | _ -> fail "Unknown completed HTTP download"
                else
                    refusal complete.Reason
                    finishDownload peer
                    peer.DownloadDue <- now()+max 1000. (float complete.RetryAfterMs)
        | ServerAssetPacket.PayloadOneofCase.Offer ->
            let offer = packet.Offer
            if offer.Asset<>descriptor || offer.PlayerId=ids[index] || offer.ViewRevision=0UL then fail "Invalid Offer"
            integrateSubscriptions()
            match peer.Offers.TryGetValue offer.PlayerId with
            | true, revision when revision = offer.ViewRevision -> ()
            | _ -> peer.Seen.Remove offer.PlayerId |> ignore
            peer.Offers[offer.PlayerId] <- offer.ViewRevision; offers <- offers+1L
            if peer.Cached then displayed[index][offer.PlayerId] <- offer.ViewRevision
        | ServerAssetPacket.PayloadOneofCase.PoseDemand ->
            if packet.PoseDemand.ContextRevision<>1UL then fail "Unexpected demand context"
            peer.PoseRequired <- packet.PoseDemand.Required
            peer.Due <- now()
        | ServerAssetPacket.PayloadOneofCase.Settled ->
            if packet.Settled.Generation<>1UL || packet.Settled.ContextRevision<>1UL then fail "Unexpected settled generation"
        | ServerAssetPacket.PayloadOneofCase.Remove ->
            integrateSubscriptions()
            peer.Offers.Remove(packet.Remove.PlayerId) |> ignore
            displayed[index].Remove(packet.Remove.PlayerId) |> ignore
            peer.Seen.Remove(packet.Remove.PlayerId) |> ignore
        | _ -> fail "Unexpected model envelope"
    member _.Pose(index:int,packet:ServerPosePacket,bytes:int) =
        let peer = peers[index]
        if packet.ProtocolVersion<>Dreamsleeve.Server.Core.ProtocolCodec.Version || isNull packet.Sample
           || packet.Sample.Generation<>1UL || packet.Sample.ContextRevision<>1UL || packet.Sample.Sequence=0UL || packet.Sample.Payload<>poseFor packet.Sample.Sequence then fail "Invalid complete phantom pose"
        elif measuring then
            match peer.Offers.TryGetValue packet.PlayerId with
            | true,revision when revision=packet.ViewRevision ->
                let previous = match peer.Seen.TryGetValue packet.PlayerId with true,value -> value | _ -> 0UL
                if packet.Sample.Sequence<=previous then fail "Duplicate/out-of-order phantom pose"
                else
                    peer.Seen[packet.PlayerId] <- packet.Sample.Sequence
                    received <- received+1L; receivedBytes <- receivedBytes+int64 bytes
                    let pair = struct(index,packet.PlayerId)
                    pairs.Add pair |> ignore
                    let at = now()
                    ages.Add(at-float packet.Sample.SampledAtUs/1000.)
                    match lastAt.TryGetValue pair with true,before -> gaps.Add(at-before) | _ -> ()
                    lastAt[pair] <- at
            | _ -> stale <- stale+1L
    member _.Stop() =
        integrateSubscriptions()
        subscriptionsAtStop <- peers |> Array.sumBy(fun p -> p.Offers.Count)
        duration <- now()-started; active <- false; measuring <- false
        unfinishedAtStop <- peers |> Array.filter(fun p -> p.DownloadPending) |> Array.length
        for index in 0 .. peers.Length-1 do
            let peer = peers[index]
            peer.Upload |> Option.iter(fun item -> asset index (ClientAssetPacket(Cancel=Cancel(TransferId=item.Id))) |> ignore)
            peer.Download |> Option.iter(fun item -> asset index (ClientAssetPacket(Cancel=Cancel(TransferId=item.Id))) |> ignore)
        flushModels()
    member _.Report() =
        let ready = localPublishers |> Array.map(fun index -> peers[index]) |> Array.filter _.Ready
        box {| publishers=localPublishers.Length; totalPublishers=config.Publishers; clients=ids.Length; totalClients=allIds.Length; modelBytes=model.Length; modelSha256=Convert.ToHexStringLower hash
               rawManifestBytes=descriptor.RawBytes; channels=descriptor.Channels; assetFormatVersion=descriptor.FormatVersion
               posePayloadBytes=poses |> Array.map _.Length; poseSha256=config.PosePaths |> Array.map(fun path -> Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes path)))
               clientCacheWarm=config.ClientCacheWarm; maximumVisible=config.Maximum; requestedHz=config.Rate; measuredMs=duration
               readyPublishers=ready.Length; allPublishersReady=ready.Length=localPublishers.Length
               verifiedModelDownloads=verifiedDownloads; allClientsCached=peers |> Array.forall _.Cached
               warmupUploadPayloadBytes=warmupUploadBytes; warmupDownloadPayloadBytes=warmupDownloadBytes
               uploadPayloadBytes=uploadBytes; downloadPayloadBytes=downloadBytes
               sentPoses=peers |> Array.sumBy _.Sent; sentEnvelopeBytes=sentBytes
               receivedPoses=received; receivedEnvelopeBytes=receivedBytes; staleViewPoses=stale
               distinctReceivedPairs=pairs.Count; currentSubscriptions=subscriptionsAtStop; activeSubscriptionSeconds=subscriptionSeconds
               receivedHzPerActiveSubscriptionSecond=float received/max 0.001 subscriptionSeconds
               offers=offers; droppedAtPoseAdmission=admissionDrops; missedIntervals=peers |> Array.sumBy _.Missed
               sourceHzWhileReady=ready |> Array.map(fun p -> float p.Sent*1000./max 1. (started+duration-p.ReadyAt))
               sourceHzOverWholeLoad=float(peers |> Array.sumBy _.Sent)*1000./max 1. duration/float(max 1 localPublishers.Length)
               receivedHzPerObservedPair=float received*1000./max 1. duration/float(max 1 pairs.Count)
               readyMs=readyTimes.Summary(); verifiedDownloadMs=downloadTimes.Summary(); deliveryAgeMs=ages.Summary(); receiveGapMs=gaps.Summary()
               unfinishedDownloads=unfinishedAtStop; pendingModelCommands=modelOutbox |> Array.sumBy _.Count
               refusals=refusals; policyCount=policies.Count
               policies=policies |> Seq.map(fun p -> {| enabled=p.Enabled; sampleRate=p.SampleRate
                                                        concurrentTransfers=p.ConcurrentTransfers; modelBytesPerSecond=p.ModelBytesPerSecond
                                                        poseBytesPerSecond=p.PoseBytesPerSecond; maximumVisible=p.MaximumVisible |}) |> Seq.distinct |> Seq.toArray |}
    interface IDisposable with
        member _.Dispose() =
            for peer in peers do
                peer.Upload |> Option.iter releaseJob
                finishDownload peer
            http.Dispose()
