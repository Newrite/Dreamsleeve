module Dreamsleeve.Server.NetworkBenchmarks.PhantomProbe

open System
open System.IO
open System.Collections.Generic
open System.Security.Cryptography
open System.Text.Json
open Google.Protobuf
open Dreamsleeve.Protocol.Phantom
open Dreamsleeve.Server.Infrastructure.Interop
open Dreamsleeve.Server.NetworkBenchmarks.Measurements

// Optional opaque envelope workload, independent of game decoding/rendering.
type Config = { Publishers: int; Rate: float; PoseBytes: int; ModelPath: string }
let configuration () =
    match Environment.GetEnvironmentVariable "DREAMSLEEVE_BENCH_PHANTOM" with
    | null | "" -> None
    | path -> Some(JsonSerializer.Deserialize<Config>(File.ReadAllText path))

type private UploadState = { Id: uint64; mutable Acknowledged: int; mutable Sent: int }
type private DownloadState = { Id: uint64; Hash: IncrementalHash; mutable Offset: int }
type private PeerState = {
    mutable Ready: bool; mutable ReadyAt: float; mutable Due: float; mutable Sequence: uint64
    mutable PublishRequest: uint64; mutable PublishDue: float; mutable Upload: UploadState option
    mutable DownloadRequest: uint64; mutable DownloadDue: float; mutable DownloadPending: bool
    mutable Download: DownloadState option; mutable Cached: bool
    Offers: Dictionary<uint64,uint64>; Seen: Dictionary<uint64,uint64>
    mutable Sent: int64; mutable Missed: int64
}
type Probe(config: Config, ids: uint64 array, now: unit -> float,
           send: int -> byte -> PacketDelivery -> byte array -> bool, fail: string -> unit) =
    let model = File.ReadAllBytes config.ModelPath
    let hash = SHA256.HashData model
    let pose = ByteString.CopyFrom(Array.init config.PoseBytes (fun i -> byte (i % 251)))
    let descriptor = AssetDescriptor(Hash = ByteString.CopyFrom hash, Generation = 1UL, FormatVersion = 1u,
                                    CompressedBytes = uint32 model.Length, RawBytes = uint32 (2 * model.Length), Channels = 256u, Geometry = 32u)
    let peers = Array.init ids.Length (fun _ -> {
        Ready=false; ReadyAt= -1.; Due=0.; Sequence=0UL; PublishRequest=1000UL; PublishDue=0.; Upload=None
        DownloadRequest=1000000UL; DownloadDue=0.; DownloadPending=false; Download=None; Cached=false
        Offers=Dictionary(); Seen=Dictionary(); Sent=0L; Missed=0L })
    let policies = ResizeArray<Policy>()
    let pairs = HashSet<struct(int*uint64)>()
    let lastAt = Dictionary<struct(int*uint64),float>()
    let ages, gaps, readyTimes = Distribution(), Distribution(), Distribution()
    let refusals = Dictionary<string,int>()
    let mutable active = false
    let mutable started, duration = 0., 0.
    let mutable sentBytes, receivedBytes, uploadBytes, downloadBytes = 0L,0L,0L,0L
    let mutable received, stale, offers = 0L,0L,0L
    let mutable verifiedDownloads = 0
    let mutable unfinishedAtStop = 0
    let mutable subscriptionsAtStop = 0
    let mutable subscriptionSeconds, subscriptionAt = 0., 0.
    let integrateSubscriptions () =
        if active then
            let at = now()
            subscriptionSeconds <- subscriptionSeconds + float(peers |> Array.sumBy(fun p -> p.Offers.Count)) * max 0. (at-subscriptionAt)/1000.
            subscriptionAt <- at
    let asset index (packet: ClientAssetPacket) =
        packet.ProtocolVersion <- Dreamsleeve.Server.Core.ProtocolCodec.Version
        send index 3uy PacketDelivery.ReliableBulk (packet.ToByteArray())
    let finishDownload (peer: PeerState) =
        peer.Download |> Option.iter (fun item -> item.Hash.Dispose())
        peer.Download <- None; peer.DownloadPending <- false
    let refusal reason =
        refusals[reason] <- (match refusals.TryGetValue reason with true,n -> n | _ -> 0) + 1
    do
        if config.Publishers < 1 || config.Publishers > ids.Length || config.Rate <= 0. || config.Rate > 20.
           || config.PoseBytes <> 6144 || model.Length <> 13*1024*1024 then
            invalidArg "config" "Require publishers1..N, rate(0,20], model13MiB/pose6KiB."
    member _.Start() =
        started <- now(); subscriptionAt <- started; active <- true
        for index in 0 .. peers.Length-1 do
            peers[index].PublishDue <- started
            asset index (ClientAssetPacket(Preferences=Preferences(Publish=(index<config.Publishers),Receive=true,Maximum=4u,Distance=4096.f))) |> ignore
    member _.Tick() =
        if active then
            let at = now()
            for index in 0 .. peers.Length-1 do
                let peer = peers[index]
                if index < config.Publishers && not peer.Ready && peer.Upload.IsNone && at >= peer.PublishDue then
                    peer.PublishRequest <- peer.PublishRequest+1UL; peer.PublishDue <- Double.PositiveInfinity
                    asset index (ClientAssetPacket(Publish=Publish(Asset=descriptor,ContextRevision=1UL,RequestId=peer.PublishRequest))) |> ignore
                match peer.Upload with
                | Some upload ->
                    while upload.Sent < model.Length && upload.Sent-upload.Acknowledged < 4*16384 do
                        let length = min 16384 (model.Length-upload.Sent)
                        let packet = ClientAssetPacket(Chunk=Chunk(TransferId=upload.Id,Offset=uint32 upload.Sent,Data=ByteString.CopyFrom(model,upload.Sent,length)))
                        if asset index packet then
                            upload.Sent <- upload.Sent+length; uploadBytes <- uploadBytes+int64 length
                        else upload.Sent <- model.Length
                | None -> ()
                if not peer.Cached && not peer.DownloadPending && peer.Offers.Count>0 && at>=peer.DownloadDue then
                    let source = peer.Offers.Keys |> Seq.head
                    peer.DownloadRequest <- peer.DownloadRequest+1UL; peer.DownloadPending <- true
                    asset index (ClientAssetPacket(Download=Download(PlayerId=source,Generation=1UL,RequestId=peer.DownloadRequest))) |> ignore
                if peer.Ready && at>=peer.Due then
                    let interval = 1000./config.Rate
                    let missed = max 0L (int64 (floor ((at-peer.Due)/interval)))
                    peer.Missed <- peer.Missed+missed; peer.Due <- peer.Due+float(missed+1L)*interval
                    peer.Sequence <- peer.Sequence+1UL
                    let packet = ClientPosePacket(ProtocolVersion=Dreamsleeve.Server.Core.ProtocolCodec.Version,
                        Sample=PoseSample(Generation=1UL,ContextRevision=1UL,Sequence=peer.Sequence,SampledAtUs=uint64(at*1000.)+1UL,Payload=pose))
                    let bytes = packet.ToByteArray()
                    if send index 4uy PacketDelivery.SequencedFragmented bytes then
                        peer.Sent <- peer.Sent+1L; sentBytes <- sentBytes+int64 bytes.Length
    member _.Asset(index:int,packet:ServerAssetPacket) =
        let peer = peers[index]
        if packet.ProtocolVersion<>Dreamsleeve.Server.Core.ProtocolCodec.Version then fail "Asset protocol mismatch"
        match packet.PayloadCase with
        | ServerAssetPacket.PayloadOneofCase.Policy -> policies.Add(packet.Policy.Clone())
        | ServerAssetPacket.PayloadOneofCase.Transfer ->
            let transfer = packet.Transfer
            if transfer.Asset<>descriptor || transfer.TransferId=0UL then fail "Transfer descriptor mismatch"
            if transfer.Upload then
                if transfer.RequestId<>peer.PublishRequest || index>=config.Publishers then fail "Upload request correlation"
                peer.Upload <- Some { Id=transfer.TransferId; Sent=0; Acknowledged=0 }
            else
                if transfer.RequestId<>peer.DownloadRequest || peer.Download.IsSome then fail "Download request correlation"
                peer.Download <- Some { Id=transfer.TransferId; Offset=0; Hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256) }
        | ServerAssetPacket.PayloadOneofCase.Progress ->
            match peer.Upload with
            | Some upload when upload.Id=packet.Progress.TransferId ->
                let offset = int packet.Progress.NextOffset
                if offset<upload.Acknowledged || offset>upload.Sent then fail "Upload progress outside window"
                upload.Acknowledged <- offset
            | _ -> fail "Unmatched upload progress"
        | ServerAssetPacket.PayloadOneofCase.Chunk ->
            match peer.Download with
            | Some download when download.Id=packet.Chunk.TransferId ->
                let chunk = packet.Chunk
                let bytes = chunk.Data.ToByteArray()
                if int chunk.Offset<>download.Offset || bytes.Length<1 || bytes.Length>16384 || bytes.Length>model.Length-download.Offset then fail "Download chunk offset/size"
                elif not(bytes.AsSpan().SequenceEqual(model.AsSpan(download.Offset,bytes.Length))) then fail "Download content mismatch"
                else
                    download.Hash.AppendData bytes; download.Offset <- download.Offset+bytes.Length
                    downloadBytes <- downloadBytes+int64 bytes.Length
                    asset index (ClientAssetPacket(Progress=Progress(TransferId=download.Id,NextOffset=uint32 download.Offset))) |> ignore
            | _ -> fail "Unmatched download chunk"
        | ServerAssetPacket.PayloadOneofCase.Complete ->
            let complete = packet.Complete
            if complete.Generation<>1UL then fail "Completion generation mismatch"
            if complete.Upload then
                if complete.RequestId<>peer.PublishRequest then fail "Upload completion request mismatch"
                peer.Upload <- None
                if complete.Accepted then
                    peer.Ready <- true; peer.ReadyAt <- now(); peer.Due <- peer.ReadyAt
                    readyTimes.Add(peer.ReadyAt-started)
                else
                    refusal complete.Reason
                    peer.PublishDue <- if complete.RetryAfterMs>0u then now()+float complete.RetryAfterMs else Double.PositiveInfinity
            else
                if complete.RequestId<>peer.DownloadRequest then fail "Download completion request mismatch"
                if complete.Accepted then
                    match peer.Download with
                    | Some download when download.Id=complete.TransferId && download.Offset=model.Length ->
                        if not(download.Hash.GetHashAndReset().AsSpan().SequenceEqual(hash.AsSpan())) then fail "Download SHA256 mismatch"
                        else peer.Cached <- true; verifiedDownloads <- verifiedDownloads+1
                    | _ -> fail "Download accepted before complete verified bytes"
                else refusal complete.Reason
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
        | ServerAssetPacket.PayloadOneofCase.Remove ->
            integrateSubscriptions()
            peer.Offers.Remove(packet.Remove.PlayerId) |> ignore
            peer.Seen.Remove(packet.Remove.PlayerId) |> ignore
        | _ -> fail "Unexpected model envelope"
    member _.Pose(index:int,packet:ServerPosePacket,bytes:int) =
        let peer = peers[index]
        if packet.ProtocolVersion<>Dreamsleeve.Server.Core.ProtocolCodec.Version || isNull packet.Sample
           || packet.Sample.Generation<>1UL || packet.Sample.ContextRevision<>1UL || packet.Sample.Sequence=0UL || packet.Sample.Payload<>pose then fail "Invalid complete phantom pose"
        elif active then
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
        duration <- now()-started; active <- false
        unfinishedAtStop <- peers |> Array.filter(fun p -> p.DownloadPending) |> Array.length
        for index in 0 .. peers.Length-1 do
            let peer = peers[index]
            peer.Upload |> Option.iter(fun item -> asset index (ClientAssetPacket(Cancel=Cancel(TransferId=item.Id))) |> ignore)
            peer.Download |> Option.iter(fun item -> asset index (ClientAssetPacket(Cancel=Cancel(TransferId=item.Id))) |> ignore)
    member _.Report() =
        let ready = peers |> Array.take config.Publishers |> Array.filter _.Ready
        box {| publishers=config.Publishers; clients=ids.Length; modelBytes=model.Length; modelSha256=Convert.ToHexStringLower hash
               rawManifestBytes=descriptor.RawBytes; channels=descriptor.Channels; geometry=descriptor.Geometry
               poseBytes=config.PoseBytes; requestedHz=config.Rate; measuredMs=duration
               readyPublishers=ready.Length; allPublishersReady=ready.Length=config.Publishers
               verifiedModelDownloads=verifiedDownloads; allClientsDownloaded=verifiedDownloads=ids.Length
               uploadPayloadBytes=uploadBytes; downloadPayloadBytes=downloadBytes
               sentPoses=peers |> Array.sumBy _.Sent; sentEnvelopeBytes=sentBytes
               receivedPoses=received; receivedEnvelopeBytes=receivedBytes; staleViewPoses=stale
               distinctReceivedPairs=pairs.Count; currentSubscriptions=subscriptionsAtStop; activeSubscriptionSeconds=subscriptionSeconds
               receivedHzPerActiveSubscriptionSecond=float received/max 0.001 subscriptionSeconds
               offers=offers; missedIntervals=peers |> Array.sumBy _.Missed
               sourceHzWhileReady=ready |> Array.map(fun p -> float p.Sent*1000./max 1. (started+duration-p.ReadyAt))
               sourceHzOverWholeLoad=float(peers |> Array.sumBy _.Sent)*1000./max 1. duration/float config.Publishers
               receivedHzPerObservedPair=float received*1000./max 1. duration/float(max 1 pairs.Count)
               readyMs=readyTimes.Summary(); deliveryAgeMs=ages.Summary(); receiveGapMs=gaps.Summary()
               unfinishedDownloads=unfinishedAtStop
               refusals=refusals; policyCount=policies.Count
               policies=policies |> Seq.map(fun p -> {| enabled=p.Enabled; sampleRate=p.SampleRate; windowChunks=p.WindowChunks
                                                        concurrentTransfers=p.ConcurrentTransfers; modelBytesPerSecond=p.ModelBytesPerSecond
                                                        poseBytesPerSecond=p.PoseBytesPerSecond; maximumVisible=p.MaximumVisible |}) |> Seq.distinct |> Seq.toArray |}
    interface IDisposable with
        member _.Dispose() = for peer in peers do finishDownload peer
