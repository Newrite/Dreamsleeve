module Dreamsleeve.Server.Tests.PhantomTests

open System
open System.IO
open System.Security.Cryptography
open System.Threading.Tasks
open Google.Protobuf
open Expecto
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Infrastructure

let private ok = function Ok value -> value | Error error -> failwithf "%A" error
let mutable private requestCounter = 0L
let private requestId () = PhantomRequestId.create (uint64 (System.Threading.Interlocked.Increment &requestCounter)) |> ok
let private options = { PhantomOptions.defaults with ReplicationIntervalMs = 1; PublishCooldownMs = 0; PoseIntervalMs = 1; ChunkBytes = 4; WindowChunks = 2 }
let private asset gen (bytes: byte array) =
    PhantomManifest.create options.Limits (AssetHash.create (SHA256.HashData bytes) |> ok)
        (AppearanceGeneration.create gen |> ok) 2u (uint32 bytes.Length) (uint32 bytes.Length) 2u |> ok
let private player number context =
    let profile = PlayerData.create (PlayerId.create number |> ok) (Username.create 32 $"p{number}" |> ok)
                      (DisplayName.create 64 $"P{number}" |> ok) NameColor.unknown
    let form = FormKey.create (PluginName.create 260 "Skyrim.esm" |> ok) (LocalFormId.create 1u |> ok)
    let location = PlayerLocation.create (Location.create form (LocationName.create 256 "Test" |> ok)) Position.zero Rotation.zero
    Player.create profile
    |> Player.applyUpdate (PlayerUpdate.BeginCharacter(CharacterName.create 128 "Test" |> ok))
    |> Player.applyUpdate (PlayerUpdate.SetLocation(context, ValueSome location))
    |> Player.snapshot
let private result value = Task.FromResult(Ok value)
let private memoryStorage : PhantomStoragePort = {
    StartUpload = fun _ -> result true
    WriteChunk = fun _ -> result false
    StartDownload = fun _ -> result ()
    ReadChunk = fun (_, offset, count) -> result (Array.init count (fun index -> byte (offset + index)))
    Cancel = fun _ -> Task.FromResult ()
    Dispose = fun () -> Task.FromResult ()
}
let private setup config storage count =
    let output = ResizeArray<Guid * TransportPacket>()
    let state = PhantomAgent.create config storage (fun (id, packet) -> output.Add(id, packet); Ok ())
    let members = Array.init count (fun index -> Guid.NewGuid(), player (uint64 index + 1UL) 10UL)
    for id, value in members do
        PhantomAgent.observe state (PhantomObservation.Member(id, value))
        PhantomAgent.activate state id
    PhantomAgent.tick state 1L
    output.Clear()
    state, members, output
let private models (output: ResizeArray<Guid * TransportPacket>) =
    output |> Seq.choose (fun (_, packet) -> if packet.Lane = DeliveryLane.Models then Some(Dreamsleeve.Protocol.Phantom.ServerAssetPacket.Parser.ParseFrom packet.Bytes) else None) |> Seq.toArray
let private transfer output =
    models output |> Array.pick (fun packet -> if isNull packet.Transfer then None else Some(PhantomTransferId packet.Transfer.TransferId))
let private ready state (id, value: PlayerSnapshot) manifest =
    PhantomAgent.handle state 2L id (PhantomRequest.Publish(manifest, value.MovementContext, requestId()))
    PhantomAgent.tick state 3L
let private view state (observer, _: PlayerSnapshot) (source, value: PlayerSnapshot) revision distance =
    PhantomAgent.observe state (PhantomObservation.View(observer, source, value.Identity.PlayerId, revision, distance))
let private pose gen sequence context =
    Dreamsleeve.Protocol.Phantom.ClientPosePacket(ProtocolVersion = ProtocolCodec.Version,
        Sample = Dreamsleeve.Protocol.Phantom.PoseSample(Generation = gen, ContextRevision = context, Sequence = sequence,
                    SampledAtUs = sequence * 50000UL, Payload = ByteString.CopyFrom [|1uy;2uy|])).ToByteArray()
let private preferences receive distance = PhantomRequest.Preferences { Publish = true; Receive = receive; Maximum = 1; Distance = distance }
let private case name run = testCaseAsync name (async { do! run() |> Async.AwaitTask })
let private storageCase name run = case name (fun () -> task {
    let root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dreamsleeve-phantom-" + Guid.NewGuid().ToString("N")))
    let config = { options with StoragePath = root; DiskBytes = 64L; RamBytes = 8L; CacheEntries = 8
                                Limits = { options.Limits with CompressedBytes = 64; RawBytes = 64 } }
    let store = PhantomStorage.create config
    try do! run root config store
    finally
        store.Dispose().GetAwaiter().GetResult()
        let parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
        if Path.GetDirectoryName root <> parent then failwith "Test cleanup escaped temporary directory."
        if Directory.Exists root then Directory.Delete(root, true)
})

let tests = testList "Phantoms" [
    testCase "native asset policy bounds bytes and pose channels without geometry schema" <| fun _ ->
        let defaults = PhantomOptions.defaults
        Expect.isEmpty (PhantomOptions.validate defaults) "Default is within format bounds."
        let valid = asset 1UL [|1uy|]
        let manifest = PhantomManifest.create defaults.Limits valid.Hash valid.Generation 2u 1u 1u 4096u |> ok
        Expect.equal manifest.FormatVersion 2u "Native NIF container."
        Expect.equal manifest.Channels 4096 "Pose binding count retained."
        let encoded = PhantomCodec.encode (PhantomResponse.Policy(PhantomOptions.policy defaults))
        let policy = Dreamsleeve.Protocol.Phantom.ServerAssetPacket.Parser.ParseFrom(encoded.Bytes).Policy
        Expect.equal policy.RawAssetBytes (128u * 1024u * 1024u) "Raw byte cap unchanged."
        Expect.equal policy.CompressedAssetBytes (64u * 1024u * 1024u) "Compressed byte cap unchanged."
        Expect.equal policy.Channels 4096u "Node/channel cap unchanged."
        Expect.equal policy.PoseBytes (256u * 1024u) "Native raw pose cap."
        Expect.equal policy.CompressedPoseBytes (128u * 1024u) "Native compressed pose cap."

    testCase "reliable transfer progress is independent of the pose tick" <| fun _ ->
        let config = { options with ReplicationIntervalMs = 1000 }
        let storage = { memoryStorage with StartUpload = fun _ -> result false }
        let state, members, output = setup config storage 1
        let owner, snapshot = members[0]
        PhantomAgent.handle state 2L owner (PhantomRequest.Publish(asset 1UL (Array.zeroCreate 16), snapshot.MovementContext, requestId()))
        PhantomAgent.tick state 3L
        let id = transfer output
        output.Clear()
        for offset in [0;4] do PhantomAgent.handle state 4L owner (PhantomRequest.Chunk(id, offset, Array.zeroCreate 4))
        PhantomAgent.tick state 5L
        let acknowledgements = models output |> Array.choose (fun packet -> if isNull packet.Progress then None else Some packet.Progress.NextOffset)
        Expect.equal acknowledgements [|8u|] "The durable window is acknowledged before the next 1 Hz pose tick."
        Expect.isFalse (output |> Seq.exists (fun (_, packet) -> packet.Lane = DeliveryLane.Poses)) "No pose timer was due."

    testCase "late runtime ticks preserve the pose cadence without catch-up bursts" <| fun _ ->
        let config = { options with ReplicationIntervalMs = 100 }
        let state, members, output = setup config memoryStorage 2
        view state members[1] members[0] 1UL 10.0
        ready state members[0] (asset 1UL [|1uy|])
        for at in 16L .. 16L .. 1008L do
            PhantomAgent.receive state at (fst members[0]) DeliveryLane.Poses (pose 1UL (uint64 at) 10UL)
            PhantomAgent.tick state at
        let delivered () = output |> Seq.filter (fun (_, packet) -> packet.Lane = DeliveryLane.Poses) |> Seq.length
        Expect.equal (delivered()) 10 "16 ms scheduling steps must not reduce 10 Hz to 9 Hz."
        output.Clear()
        PhantomAgent.receive state 2000L (fst members[0]) DeliveryLane.Poses (pose 1UL 2000UL 10UL)
        PhantomAgent.tick state 2000L
        PhantomAgent.tick state 2000L
        PhantomAgent.tick state 2001L
        Expect.equal (delivered()) 1 "A pause emits the latest snapshot once."

    testCase "disabled replication retains membership policy without views or IO" <| fun _ ->
        let disabled = { options with Enabled = false }
        let output = ResizeArray<TransportPacket>()
        let mutable started = 0
        let storage = { memoryStorage with StartUpload = fun _ -> started <- started + 1; result true }
        let state = PhantomAgent.create disabled storage (fun (_, packet) -> output.Add packet; Ok ())
        Expect.equal (PhantomAgent.observationMode state) PhantomObservationMode.Membership "Bootstrap membership survives disabled replication."
        let id = Guid.NewGuid()
        PhantomAgent.observe state (PhantomObservation.Member(id, player 1UL 10UL))
        PhantomAgent.activate state id
        PhantomAgent.tick state 1L
        Expect.equal output.Count 1 "Only the activation policy is sent."
        let policy = Dreamsleeve.Protocol.Phantom.ServerAssetPacket.Parser.ParseFrom(output[0].Bytes).Policy
        Expect.isFalse policy.Enabled "Authenticated client learns the effective disabled policy."
        PhantomAgent.handle state 2L id (PhantomRequest.Publish(asset 1UL [|1uy|], 10UL, requestId()))
        PhantomAgent.tick state 3L
        Expect.equal started 0 "Disabled publication never starts detached IO."
        Expect.equal (PhantomAgent.snapshot state).Subscriptions 0 "No subscriptions."
        PhantomAgent.observe state (PhantomObservation.Departed id)
        Expect.equal (PhantomAgent.snapshot state).Members 0 "Departure cleanup is observed."

    testCase "pose encode allocates one final buffer and preserves protobuf bytes" <| fun _ ->
        let payload = Array.init (64 * 1024) (fun index -> byte index)
        let source = PlayerId.create 1UL |> ok
        let value = PhantomPose.create options.Limits (AppearanceGeneration.create 2UL |> ok) 10UL
                        (PhantomSequence.create 3UL |> ok) PhantomPose.maximumSampledAtUs (ReadOnlyMemory<byte>(payload)) |> ok
        PhantomCodec.encodePose source 129UL value |> ignore
        let before = GC.GetAllocatedBytesForCurrentThread()
        let packet = PhantomCodec.encodePose source 129UL value
        let allocated = GC.GetAllocatedBytesForCurrentThread() - before
        Expect.isLessThanOrEqual allocated (int64 packet.Bytes.Length + 1024L) "No intermediate full sample copy."
        Expect.equal packet.Bytes.Length (PhantomCodec.posePacketSize source 129UL value) "Admission accounts for the exact final wire size."
        Expect.equal packet.Bytes (PhantomCodec.encodePoseEnvelope source 129UL (PhantomCodec.encodePoseSample value)).Bytes "Direct encoding matches the generated sample codec."
        let decoded = Dreamsleeve.Protocol.Phantom.ServerPosePacket.Parser.ParseFrom packet.Bytes
        Expect.equal decoded.Sample.Payload (ByteString.CopyFrom payload) "Opaque compressed bytes are unchanged."
        Expect.equal decoded.Sample.SampledAtUs PhantomPose.maximumSampledAtUs "Timestamp bound survives encoding."

    testCase "model chunk encode avoids scratch copy and owns final bytes" <| fun _ ->
        let payload = Array.init 16384 (fun index -> byte index)
        let response = PhantomResponse.Chunk(PhantomTransferId 1UL, 16384, payload)
        PhantomCodec.encode response |> ignore
        let before = GC.GetAllocatedBytesForCurrentThread()
        let packet = PhantomCodec.encode response
        let allocated = GC.GetAllocatedBytesForCurrentThread() - before
        Expect.isLessThanOrEqual allocated (int64 packet.Bytes.Length + 1024L) "No intermediate ByteString payload copy."
        payload[0] <- 255uy
        let decoded = Dreamsleeve.Protocol.Phantom.ServerAssetPacket.Parser.ParseFrom packet.Bytes
        Expect.equal decoded.Chunk.Offset 16384u "Offset unchanged."
        Expect.equal decoded.Chunk.Data[0] 0uy "Synchronous encoding owns the final packet."

    testCase "repeated unchanged authority views do not allocate replacements" <| fun _ ->
        let state, members, _ = setup options memoryStorage 2
        let observation = PhantomObservation.View(fst members[1], fst members[0], (snd members[0]).Identity.PlayerId, 1UL, 100.0)
        PhantomAgent.observe state observation
        let before = GC.GetAllocatedBytesForCurrentThread()
        for _ in 1 .. 16384 do PhantomAgent.observe state observation
        let allocated = GC.GetAllocatedBytesForCurrentThread() - before
        Expect.isLessThanOrEqual allocated 4096L "Stable dense views reuse the retained authority record."

    testCase "manifest and codec reject malformed identity/version/limits" <| fun _ ->
        Expect.isError (AssetHash.create [|1uy|]) "exactly SHA256"
        Expect.isError (AppearanceGeneration.create 0UL) "nonzero generation"
        let valid = asset 1UL [|1uy|]
        for version, compressed, raw, channels in [ 1u,1u,1u,1u; 2u,0u,1u,1u; 2u,1u,129u*1024u*1024u,1u; 2u,1u,1u,4097u; 2u,1u,1u,0u ] do
            Expect.isError (PhantomManifest.create options.Limits valid.Hash valid.Generation version compressed raw channels) "declared limit"
        let packet = Dreamsleeve.Protocol.Phantom.ClientAssetPacket(ProtocolVersion = ProtocolCodec.Version,
                        Chunk = Dreamsleeve.Protocol.Phantom.Chunk(TransferId = 1UL, Data = ByteString.CopyFrom(Array.zeroCreate 5)))
        Expect.isError (PhantomCodec.decodeAsset options (packet.ToByteArray())) "chunk bound"
        packet.ProtocolVersion <- ProtocolCodec.Version - 1u
        Expect.isError (PhantomCodec.decodeAsset options (packet.ToByteArray())) "version"
        Expect.isError (PhantomCodec.decodePose options [|255uy|]) "malformed"

    testCase "pose timestamp is validated centrally before payload copy" <| fun _ ->
        let generation = AppearanceGeneration.create 1UL |> ok
        let sequence = PhantomSequence.create 1UL |> ok
        let payload = ReadOnlyMemory<byte>([|1uy|])
        for timestamp in [0UL; PhantomPose.maximumSampledAtUs] do
            let value = PhantomPose.create options.Limits generation 10UL sequence timestamp payload |> ok
            Expect.equal value.SampledAtUs timestamp "Timestamp boundary admitted."
            Expect.isOk (PhantomCodec.decodePose options (Dreamsleeve.Protocol.Phantom.ClientPosePacket(ProtocolVersion = ProtocolCodec.Version,
                Sample = Dreamsleeve.Protocol.Phantom.PoseSample(Generation = 1UL, ContextRevision = 10UL, Sequence = 1UL, SampledAtUs = timestamp, Payload = ByteString.CopyFrom [|1uy|])).ToByteArray())) "Wire uses central validation."
        for timestamp in [PhantomPose.maximumSampledAtUs + 1UL; UInt64.MaxValue] do
            Expect.isError (PhantomPose.create options.Limits generation 10UL sequence timestamp payload) "Domain prevents clock overflow."
            let packet = Dreamsleeve.Protocol.Phantom.ClientPosePacket(ProtocolVersion = ProtocolCodec.Version,
                Sample = Dreamsleeve.Protocol.Phantom.PoseSample(Generation = 1UL, ContextRevision = 10UL, Sequence = 1UL, SampledAtUs = timestamp, Payload = ByteString.CopyFrom [|1uy|]))
            Expect.isError (PhantomCodec.decodePose options (packet.ToByteArray())) "Oversized wire timestamp rejected."

    testCase "policy is first bootstrap message and reports effective limits" <| fun _ ->
        let output = ResizeArray<TransportPacket>()
        let state = PhantomAgent.create options memoryStorage (fun (_, packet) -> output.Add packet; Ok ())
        let id = Guid.NewGuid()
        PhantomAgent.observe state (PhantomObservation.Member(id, player 1UL 10UL))
        PhantomAgent.activate state id
        PhantomAgent.tick state 1L
        let policy = Dreamsleeve.Protocol.Phantom.ServerAssetPacket.Parser.ParseFrom(output[0].Bytes).Policy
        Expect.equal policy.WindowChunks 2u "window"
        Expect.equal policy.CompressedAssetBytes (uint32 options.Limits.CompressedBytes) "asset cap"
        Expect.equal policy.CompressedPoseBytes 131072u "pose cap"

    testCase "authenticated source assignment and generation/context admission" <| fun _ ->
        let state, members, output = setup options memoryStorage 1
        PhantomAgent.handle state 2L (Guid.NewGuid()) (PhantomRequest.Publish(asset 1UL [|1uy|], 10UL, requestId()))
        Expect.equal (PhantomAgent.snapshot state).Transfers 0 "unauthenticated"
        ready state members[0] (asset 2UL [|1uy|])
        let sent = models output |> Array.pick (fun packet -> if isNull packet.Transfer then None else Some packet.Transfer)
        Expect.equal sent.PlayerId (PlayerId.value (snd members[0]).Identity.PlayerId) "server source"
        PhantomAgent.handle state 4L (fst members[0]) (PhantomRequest.Publish(asset 1UL [|2uy|], 10UL, requestId()))
        PhantomAgent.handle state 4L (fst members[0]) (PhantomRequest.Publish(asset 3UL [|2uy|], 9UL, requestId()))
        Expect.equal (PhantomAgent.snapshot state).Sources 1 "old publish rejected"
        Expect.equal (PhantomAgent.snapshot state).Transfers 0 "context rejected before IO"

    testCase "Presence authority distance count and receive preferences change traffic" <| fun _ ->
        let state, members, output = setup { options with Maximum = 1 } memoryStorage 3
        ready state members[0] (asset 1UL [|1uy|])
        ready state members[1] (asset 1UL [|2uy|])
        Expect.equal (PhantomAgent.snapshot state).Subscriptions 0 "hash knowledge is not AOI"
        view state members[2] members[0] 1UL 100.0
        view state members[2] members[1] 2UL 400.0
        PhantomAgent.tick state 4L
        Expect.equal (PhantomAgent.snapshot state).Subscriptions 1 "count"
        let offered = models output |> Array.pick (fun packet -> if isNull packet.Offer then None else Some packet.Offer)
        Expect.equal offered.PlayerId 1UL "nearest"
        PhantomAgent.handle state 5L (fst members[2]) (preferences false 4096.0f)
        Expect.equal (PhantomAgent.snapshot state).Subscriptions 0 "receive off"
        PhantomAgent.handle state 6L (fst members[2]) (preferences true 5.0f)
        Expect.equal (PhantomAgent.snapshot state).Subscriptions 0 "distance"

    testCase "unsubscribe and reentry allocate fresh revision and reject stale resurrection" <| fun _ ->
        let state, members, output = setup options memoryStorage 2
        ready state members[0] (asset 1UL [|1uy|])
        view state members[1] members[0] 1UL 1.0
        PhantomAgent.tick state 4L
        PhantomAgent.handle state 5L (fst members[1]) (preferences false 10.0f)
        PhantomAgent.tick state 5L
        PhantomAgent.handle state 6L (fst members[1]) (preferences true 10.0f)
        PhantomAgent.tick state 6L
        let revisions = models output |> Array.choose (fun packet -> if not (isNull packet.Offer) then Some packet.Offer.ViewRevision elif not (isNull packet.Remove) then Some packet.Remove.ViewRevision else None)
        Expect.equal revisions [|1UL;2UL;3UL|] "monotonic subscription epochs"
        PhantomAgent.observe state (PhantomObservation.Hidden(fst members[1], (snd members[0]).Identity.PlayerId, 2UL))
        PhantomAgent.tick state 7L
        view state members[1] members[0] 1UL 1.0
        PhantomAgent.tick state 8L
        Expect.equal (PhantomAgent.snapshot state).Subscriptions 0 "old Presence baseline rejected"

    testCase "departure late View and reconnect outside AOI require current session epoch and fresh authority" <| fun _ ->
        let mutable downloads = 0
        let storage = { memoryStorage with StartDownload = fun _ -> downloads <- downloads + 1; result () }
        let state, members, output = setup options storage 2
        let oldId, oldSource = members[0]
        let observerId, _ = members[1]
        let manifest = asset 1UL [|1uy|]
        ready state members[0] manifest
        view state members[1] members[0] 1UL 1.0
        PhantomAgent.tick state 4L
        Expect.equal (PhantomAgent.snapshot state).Subscriptions 1 "Initial Presence authority."
        PhantomAgent.handle state 5L observerId (PhantomRequest.Download(oldSource.Identity.PlayerId, manifest.Generation, requestId()))
        Expect.equal (PhantomAgent.snapshot state).Transfers 1 "Old session download is live."
        output.Clear()
        PhantomAgent.detach state oldId
        Expect.equal (PhantomAgent.snapshot state).Subscriptions 0 "Departure immediately revokes the old view."
        Expect.equal (PhantomAgent.snapshot state).Transfers 0 "Departure cancels old transfers."
        // Runtime can detach before already queued Presence observations drain.
        view state members[1] members[0] 2UL 1.0
        PhantomAgent.observe state (PhantomObservation.Departed oldId)
        let previous = oldSource.Location.Value
        let far = PlayerLocation.create previous.Location (Position.create 20000.0f 0.0f 0.0f |> ok) previous.Rotation
        let newId = Guid.NewGuid()
        let newSource = { oldSource with Location = ValueSome far }
        PhantomAgent.observe state (PhantomObservation.Member(newId, newSource))
        PhantomAgent.activate state newId
        PhantomAgent.handle state 6L newId (PhantomRequest.Publish(manifest, newSource.MovementContext, requestId()))
        PhantomAgent.tick state 7L
        Expect.equal (PhantomAgent.snapshot state).Sources 1 "Same-player cached publication is allowed."
        Expect.equal (PhantomAgent.snapshot state).Subscriptions 0 "No View is inherited outside AOI."
        // Reject the old epoch even above the watermark, and reject the cleared
        // revision even when supplied with the current epoch.
        view state members[1] members[0] 3UL 1.0
        view state members[1] (newId, newSource) 1UL 1.0
        PhantomAgent.receive state 8L newId DeliveryLane.Poses (pose 1UL 1UL newSource.MovementContext)
        PhantomAgent.tick state 9L
        Expect.equal (PhantomAgent.snapshot state).Subscriptions 0 "Both session epoch and authority floor are required."
        Expect.isFalse (models output |> Array.exists (fun packet -> not (isNull packet.Offer))) "No stale model offer."
        Expect.isFalse (output |> Seq.exists (fun (_, packet) -> packet.Lane = DeliveryLane.Poses)) "No stale pose fanout."
        PhantomAgent.handle state 9L observerId (PhantomRequest.Download(newSource.Identity.PlayerId, manifest.Generation, requestId()))
        Expect.equal (PhantomAgent.snapshot state).Transfers 0 "Unseen reconnect cannot be downloaded."
        Expect.equal downloads 1 "Denied reconnect starts no IO."
        // Presence later authorizes the new session at a fresh revision.
        PhantomAgent.observe state (PhantomObservation.Member(newId, oldSource))
        view state members[1] (newId, oldSource) 2UL 1.0
        PhantomAgent.tick state 10L
        Expect.equal (PhantomAgent.snapshot state).Subscriptions 1 "Fresh authority restores the legitimate view."
        view state members[1] members[0] 4UL 1.0
        PhantomAgent.observe state (PhantomObservation.Departed oldId)
        PhantomAgent.tick state 11L
        Expect.equal (PhantomAgent.snapshot state).Subscriptions 1 "Late old-session facts cannot replace or remove the new view."
        Expect.equal (models output |> Array.filter (fun packet -> not (isNull packet.Offer)) |> Array.length) 1 "Exactly one new offer."
        PhantomAgent.handle state 12L observerId (PhantomRequest.Download(newSource.Identity.PlayerId, manifest.Generation, requestId()))
        Expect.equal (PhantomAgent.snapshot state).Transfers 1 "Fresh authorized download still works."
        Expect.equal downloads 2 "Only authorized sessions create read IO."

    testCase "context retains appearance but clears poses and source/observer subscriptions" <| fun _ ->
        let state, members, output = setup options memoryStorage 2
        ready state members[0] (asset 1UL [|1uy|])
        view state members[1] members[0] 1UL 1.0
        PhantomAgent.tick state 4L
        PhantomAgent.receive state 5L (fst members[0]) DeliveryLane.Poses (pose 1UL 1UL 10UL)
        PhantomAgent.observe state (PhantomObservation.Member(fst members[0], { snd members[0] with MovementContext = 11UL }))
        Expect.equal (PhantomAgent.snapshot state).Sources 1 "appearance independent of context"
        Expect.equal (PhantomAgent.snapshot state).Subscriptions 0 "AOI invalidated"
        view state members[1] members[0] 1UL 1.0
        PhantomAgent.tick state 5L
        Expect.equal (PhantomAgent.snapshot state).Subscriptions 0 "Prior-context authority cannot resurrect a view."
        Expect.isTrue (models output |> Array.exists (fun packet -> not (isNull packet.Remove))) "Context change explicitly removes the scene, unlike an appearance replacement."
        PhantomAgent.receive state 6L (fst members[0]) DeliveryLane.Poses (pose 1UL 2UL 10UL)
        Expect.equal (PhantomAgent.snapshot state).LatestPoses 0 "old context"

    testCase "late worker completion after character change and disconnect cannot publish" <| fun _ ->
        let pending = TaskCompletionSource<Result<bool,string>>()
        let state, members, _ = setup options { memoryStorage with StartUpload = fun _ -> pending.Task } 1
        PhantomAgent.handle state 2L (fst members[0]) (PhantomRequest.Publish(asset 1UL [|1uy|], 10UL, requestId()))
        PhantomAgent.observe state (PhantomObservation.Member(fst members[0], { snd members[0] with CharacterGeneration = 2UL }))
        pending.SetResult(Ok true)
        PhantomAgent.tick state 3L
        Expect.equal (PhantomAgent.snapshot state).Sources 0 "stale completion"
        PhantomAgent.detach state (fst members[0])
        Expect.equal (PhantomAgent.snapshot state).Members 0 "disconnect"

    testCase "download ACK opens bounded window and cannot be spoofed or acknowledge unsent bytes" <| fun _ ->
        let state, members, output = setup options memoryStorage 3
        let manifest = asset 1UL (Array.zeroCreate 20)
        ready state members[0] manifest
        view state members[1] members[0] 1UL 1.0
        PhantomAgent.tick state 4L
        output.Clear()
        PhantomAgent.handle state 5L (fst members[1]) (PhantomRequest.Download((snd members[0]).Identity.PlayerId, manifest.Generation, requestId()))
        for at in 5L .. 9L do PhantomAgent.tick state at
        let chunks () = models output |> Array.filter (fun packet -> not (isNull packet.Chunk)) |> Array.length
        Expect.equal (chunks()) 2 "one window"
        let id = transfer output
        PhantomAgent.handle state 10L (fst members[2]) (PhantomRequest.Progress(id, 8))
        PhantomAgent.handle state 10L (fst members[1]) (PhantomRequest.Progress(id, 16))
        PhantomAgent.tick state 10L
        Expect.equal (chunks()) 2 "spoof and unsent rejected"
        PhantomAgent.handle state 11L (fst members[1]) (PhantomRequest.Progress(id, 8))
        PhantomAgent.tick state 11L
        PhantomAgent.tick state 12L
        Expect.equal (chunks()) 4 "next window"
        PhantomAgent.handle state 13L (fst members[1]) (PhantomRequest.Progress(id, 16))
        PhantomAgent.tick state 13L
        PhantomAgent.tick state 14L
        Expect.equal (PhantomAgent.snapshot state).Transfers 1 "final ACK required"
        PhantomAgent.handle state 15L (fst members[1]) (PhantomRequest.Progress(id, 20))
        PhantomAgent.tick state 15L
        Expect.equal (PhantomAgent.snapshot state).Transfers 0 "completion"

    testCase "upload progress follows disk completion and overflow never queues more IO" <| fun _ ->
        let writes = ResizeArray<TaskCompletionSource<Result<bool,string>>>()
        let write _ =
            let pending = TaskCompletionSource<Result<bool,string>>()
            writes.Add pending
            pending.Task
        let storage = { memoryStorage with StartUpload = (fun _ -> result false); WriteChunk = write }
        let state, members, output = setup options storage 1
        ready state members[0] (asset 1UL (Array.zeroCreate 16))
        let id = transfer output
        for offset in [0;4] do PhantomAgent.handle state 4L (fst members[0]) (PhantomRequest.Chunk(id, offset, Array.zeroCreate 4))
        PhantomAgent.tick state 4L
        Expect.equal (models output |> Array.filter (fun packet -> not (isNull packet.Progress)) |> Array.length) 0 "not written yet"
        writes[0].SetResult(Ok false)
        PhantomAgent.tick state 5L
        Expect.equal (models output |> Array.choose (fun packet -> if isNull packet.Progress then None else Some packet.Progress.NextOffset)) [|4u|] "durable write offset"
        PhantomAgent.handle state 6L (fst members[0]) (PhantomRequest.Chunk(id, 8, Array.zeroCreate 4))
        PhantomAgent.handle state 6L (fst members[0]) (PhantomRequest.Chunk(id, 12, Array.zeroCreate 4))
        Expect.equal (PhantomAgent.snapshot state).Transfers 0 "window exceeded"
        Expect.equal writes.Count 2 "overflow and cancellation do not queue extra IO"

    testCase "cross-context same-generation publication cannot correlate old starting IO with the new request" <| fun _ ->
        let old = TaskCompletionSource<Result<bool,string>>()
        let mutable started = 0
        let storage = { memoryStorage with StartUpload = fun _ -> started <- started + 1; if started = 1 then old.Task else result true }
        let state, members, output = setup options storage 1
        let manifest = asset 1UL [|1uy|]
        let first, next = PhantomRequestId.create 1001UL |> ok, PhantomRequestId.create 1002UL |> ok
        PhantomAgent.handle state 2L (fst members[0]) (PhantomRequest.Publish(manifest, 10UL, first))
        PhantomAgent.observe state (PhantomObservation.Member(fst members[0], { snd members[0] with MovementContext = 11UL }))
        PhantomAgent.handle state 3L (fst members[0]) (PhantomRequest.Publish(manifest, 11UL, next))
        PhantomAgent.tick state 4L
        let complete = models output |> Array.choose (fun packet -> if isNull packet.Complete then None else Some packet.Complete)
        let cancelled = complete |> Array.find (fun value -> value.RequestId = 1001UL)
        Expect.equal (cancelled.TransferId,cancelled.Accepted,cancelled.Generation,cancelled.RetryAfterMs) (0UL,false,1UL,0u) "Only old Awaiting request is rejected."
        let accepted = complete |> Array.find (fun value -> value.RequestId = 1002UL)
        Expect.isTrue accepted.Accepted "New same-generation context publication is independent."
        let assigned = models output |> Array.choose (fun packet -> if isNull packet.Transfer then None else Some packet.Transfer)
        Expect.equal (assigned |> Array.map _.RequestId) [|1002UL|] "No stale Transfer assignment for old request."
        old.SetResult(Ok true)
        output.Clear()
        PhantomAgent.tick state 5L
        Expect.equal (models output |> Array.filter (fun packet -> not (isNull packet.Transfer) || not (isNull packet.Complete)) |> Array.length) 0 "Late old IO cannot publish or settle the new request."
        Expect.equal (PhantomAgent.snapshot state).Sources 1 "Current source remains ready."

    testCase "publish and download wire require positive request correlation" <| fun _ ->
        Expect.isError (PhantomRequestId.create 0UL) "Invalid request IDs cannot enter domain."
        let download = Dreamsleeve.Protocol.Phantom.ClientAssetPacket(ProtocolVersion = ProtocolCodec.Version,
            Download = Dreamsleeve.Protocol.Phantom.Download(PlayerId = 1UL, Generation = 1UL))
        Expect.isError (PhantomCodec.decodeAsset options (download.ToByteArray())) "Missing download request ID."
        download.Download.RequestId <- 101UL
        match PhantomCodec.decodeAsset options (download.ToByteArray()) |> ok with
        | PhantomRequest.Download(_,_,request) -> Expect.equal request.Value 101UL "Typed request retained."
        | _ -> failtest "Expected download."

    testCase "upload windows and ACKs remain admitted independently of control flood budget" <| fun _ ->
        let state, members, output = setup { options with CommandsPerSecond = 1 } { memoryStorage with StartUpload = fun _ -> result false } 1
        let manifest = asset 1UL (Array.zeroCreate 8)
        ready state members[0] manifest
        let id = transfer output
        for offset in [0;4] do
            let packet = Dreamsleeve.Protocol.Phantom.ClientAssetPacket(ProtocolVersion = ProtocolCodec.Version, Chunk = Dreamsleeve.Protocol.Phantom.Chunk(TransferId = id.Value, Offset = uint32 offset, Data = ByteString.CopyFrom(Array.zeroCreate 4)))
            PhantomAgent.receive state 4L (fst members[0]) DeliveryLane.Models (packet.ToByteArray())
        Expect.equal (PhantomAgent.snapshot state).PendingIo 2 "Every admitted reliable chunk retained even at low control rate."
    testCase "same immutable generation retries IO and cooldown failures; conflicting and lower generations are terminal" <| fun _ ->
        let mutable attempts = 0
        let storage = { memoryStorage with StartUpload = fun _ -> attempts <- attempts + 1; if attempts = 1 then Task.FromResult(Error "storage busy") else result true }
        let state, members, output = setup { options with PublishCooldownMs = 100 } storage 1
        let manifest = asset 2UL [|1uy|]
        let publish at value context = PhantomAgent.handle state at (fst members[0]) (PhantomRequest.Publish(value, context, requestId())); PhantomAgent.tick state (at + 1L)
        publish 2L manifest 10UL
        let first = models output |> Array.pick (fun packet -> if isNull packet.Complete then None else Some packet.Complete)
        Expect.equal (first.TransferId, first.PlayerId, first.Generation, first.Upload, first.RetryAfterMs) (0UL,1UL,2UL,true,100u) "Pre-Transfer IO failure is correlated and temporary."
        output.Clear()
        publish 4L manifest 10UL
        let cooldown = models output |> Array.pick (fun packet -> if isNull packet.Complete then None else Some packet.Complete)
        Expect.equal cooldown.RetryAfterMs 98u "Remaining cooldown reported."
        Expect.equal attempts 1 "Cooldown did not schedule IO."
        output.Clear()
        publish 102L manifest 10UL
        Expect.equal (PhantomAgent.snapshot state).Sources 1 "Same generation and immutable descriptor retry succeeds."
        let accepted = models output |> Array.pick (fun packet -> if isNull packet.Complete then None else Some packet.Complete)
        Expect.isTrue accepted.Accepted "Cache hit publication accepted."
        Expect.isGreaterThan accepted.TransferId 0UL "Transfer was announced before accepted Complete."
        PhantomAgent.observe state (PhantomObservation.Member(fst members[0], { snd members[0] with MovementContext = 11UL }))
        publish 202L manifest 11UL
        Expect.equal (PhantomAgent.snapshot state).Sources 1 "Context can republish cached immutable generation."
        output.Clear()
        publish 204L (asset 2UL [|2uy|]) 11UL
        publish 206L (asset 1UL [|1uy|]) 11UL
        let refused = models output |> Array.choose (fun packet -> if isNull packet.Complete then None else Some packet.Complete)
        Expect.equal refused.Length 2 "Conflicting and lower generations rejected."
        Expect.isTrue (refused |> Array.forall (fun value -> not value.Accepted && value.RetryAfterMs = 0u && value.Upload)) "Terminal generation denials."
        Expect.equal attempts 3 "No IO for conflicting descriptors."

    testCase "capacity denial preserves generation for retry and correlates upload/download requests" <| fun _ ->
        let pending = TaskCompletionSource<Result<bool,string>>()
        let mutable attempts = 0
        let storage = { memoryStorage with StartUpload = fun _ -> attempts <- attempts + 1; if attempts = 1 then pending.Task else result true }
        let state, members, output = setup { options with MaxTransfers = 1; PublishCooldownMs = 10 } storage 2
        let manifest = asset 1UL [|1uy|]
        PhantomAgent.handle state 2L (fst members[0]) (PhantomRequest.Publish(manifest, 10UL, requestId()))
        PhantomAgent.handle state 2L (fst members[1]) (PhantomRequest.Publish(manifest, 10UL, requestId()))
        PhantomAgent.tick state 3L
        let busy = models output |> Array.pick (fun packet -> if isNull packet.Complete then None else Some packet.Complete)
        Expect.equal (busy.TransferId,busy.PlayerId,busy.Generation,busy.Upload,busy.RetryAfterMs) (0UL,2UL,1UL,true,10u) "Request-level upload capacity correlation."
        pending.SetResult(Error "storage busy")
        PhantomAgent.tick state 4L
        PhantomAgent.handle state 12L (fst members[1]) (PhantomRequest.Publish(manifest, 10UL, requestId()))
        PhantomAgent.tick state 13L
        Expect.equal (PhantomAgent.snapshot state).Sources 1 "Unchanged generation retries after capacity becomes available."
        output.Clear()
        PhantomAgent.handle state 14L (fst members[0]) (PhantomRequest.Download((snd members[1]).Identity.PlayerId, manifest.Generation, requestId()))
        PhantomAgent.tick state 15L
        let download = models output |> Array.pick (fun packet -> if isNull packet.Complete then None else Some packet.Complete)
        Expect.equal (download.TransferId,download.PlayerId,download.Generation,download.Upload,download.RetryAfterMs) (0UL,2UL,1UL,false,1000u) "Download denial can leave client's requested phase."

    testCase "starting IO timeout without a Transfer response remains request-correlated" <| fun _ ->
        let storage = { memoryStorage with StartUpload = fun _ -> TaskCompletionSource<Result<bool,string>>().Task }
        let state, members, output = setup { options with TransferTimeoutMs = 2; PublishCooldownMs = 10 } storage 1
        PhantomAgent.handle state 2L (fst members[0]) (PhantomRequest.Publish(asset 1UL [|1uy|], 10UL, requestId()))
        PhantomAgent.tick state 5L
        let complete = models output |> Array.pick (fun packet -> if isNull packet.Complete then None else Some packet.Complete)
        Expect.equal (complete.TransferId,complete.PlayerId,complete.Generation,complete.Upload,complete.RetryAfterMs) (0UL,1UL,1UL,true,10u) "Starting timeout is retryable without unknown transfer correlation."
    testCase "bandwidth admission cannot skip an earlier full chunk for a short final chunk" <| fun _ ->
        let writes = ResizeArray<int>()
        let storage = { memoryStorage with StartUpload = (fun _ -> result false); WriteChunk = (fun (_, offset, _) -> writes.Add offset; result false) }
        let state, members, output = setup { options with PlayerModelBytesPerSecond = 5; ModelBytesPerSecond = 5; WindowChunks = 3 } storage 1
        ready state members[0] (asset 1UL (Array.zeroCreate 9))
        let id = transfer output
        for offset, count in [0,4;4,4;8,1] do
            PhantomAgent.handle state 4L (fst members[0]) (PhantomRequest.Chunk(id, offset, Array.zeroCreate count))
        PhantomAgent.tick state 4L
        Expect.equal (Seq.toArray writes) [|0|] "Final short chunk must not jump over offset four."
        PhantomAgent.tick state 1004L
        Expect.equal (Seq.toArray writes) [|0;4;8|] "Resumed writes preserve streaming hash order."

    testCase "source quota limits publishers while receivers still get policy" <| fun _ ->
        let state, members, output = setup { options with MaxSources = 1 } memoryStorage 3
        ready state members[0] (asset 1UL [|1uy|])
        ready state members[1] (asset 1UL [|2uy|])
        Expect.equal (PhantomAgent.snapshot state).Members 3 "Receiver admission independent of source quota."
        Expect.equal (PhantomAgent.snapshot state).Sources 1 "Published sources bounded."
        view state members[2] members[0] 1UL 1.0
        PhantomAgent.tick state 4L
        Expect.equal (PhantomAgent.snapshot state).Subscriptions 1 "Receiver can subscribe at source capacity."
        Expect.isTrue (models output |> Array.exists (fun packet -> not (isNull packet.Complete) && not packet.Complete.Accepted)) "Excess source refused."

    testCase "replacement upload does not consume another publisher quota slot" <| fun _ ->
        let pending = TaskCompletionSource<Result<bool,string>>()
        let storage = { memoryStorage with StartUpload = fun (_, manifest) -> if manifest.Generation.Value = 2UL then pending.Task else result true }
        let state, members, _ = setup { options with MaxSources = 2 } storage 2
        ready state members[0] (asset 1UL [|1uy|])
        PhantomAgent.handle state 4L (fst members[0]) (PhantomRequest.Publish(asset 2UL [|2uy|], 10UL, requestId()))
        PhantomAgent.handle state 5L (fst members[1]) (PhantomRequest.Publish(asset 1UL [|3uy|], 10UL, requestId()))
        PhantomAgent.tick state 6L
        Expect.equal (PhantomAgent.snapshot state).Sources 2 "Two distinct publishers fit while one replaces its asset."
        pending.SetResult(Ok true)
        PhantomAgent.tick state 7L
        Expect.equal (PhantomAgent.snapshot state).Sources 2 "Replacement does not change source count."

    testCase "max and distance reductions revoke active downloads immediately" <| fun _ ->
        let state, members, output = setup options memoryStorage 2
        let manifest = asset 1UL (Array.zeroCreate 20)
        ready state members[0] manifest
        view state members[1] members[0] 1UL 100.0
        PhantomAgent.tick state 4L
        let receive maximum distance = PhantomRequest.Preferences { Publish = true; Receive = true; Maximum = maximum; Distance = distance }
        for index, reduced in [0, receive 0 4096.0f; 1, receive 1 5.0f] do
            let at = 5L + int64 index * 4L
            PhantomAgent.handle state at (fst members[1]) (receive 1 4096.0f)
            PhantomAgent.tick state at
            PhantomAgent.handle state (at + 1L) (fst members[1]) (PhantomRequest.Download((snd members[0]).Identity.PlayerId, manifest.Generation, requestId()))
            Expect.equal (PhantomAgent.snapshot state).Transfers 1 "Download started."
            PhantomAgent.handle state (at + 1L) (fst members[1]) reduced
            Expect.equal (PhantomAgent.snapshot state).Subscriptions 0 "Reduction takes effect in the command turn."
            Expect.equal (PhantomAgent.snapshot state).Transfers 0 "Download immediately cancelled."
            PhantomAgent.tick state (at + 2L)
        Expect.equal (models output |> Array.filter (fun packet -> not (isNull packet.Remove)) |> Array.length) 2 "Both reductions issue Remove."

    testCase "shared encoded sample preserves fields across different observer revisions" <| fun _ ->
        let value = PhantomCodec.decodePose options (pose 1UL 1UL 10UL) |> ok
        let sample = PhantomCodec.encodePoseSample value
        for revision in [1UL;128UL;UInt64.MaxValue] do
            let packet = PhantomCodec.encodePoseEnvelope (PlayerId.create 123UL |> ok) revision sample
            let decoded = Dreamsleeve.Protocol.Phantom.ServerPosePacket.Parser.ParseFrom packet.Bytes
            Expect.equal decoded.ViewRevision revision "Only the observer envelope changes."
            Expect.equal packet.Bytes.Length (PhantomCodec.posePacketSize (PlayerId.create 123UL |> ok) revision value) "Budget size equals actual wire bytes."
            Expect.equal decoded.Sample.Payload (ByteString.CopyFrom value.Payload) "Compressed payload reused unchanged."
            Expect.equal decoded.Sample.ContextRevision 10UL "Complete source envelope retained."
    testCase "source flood retains latest only and reuses encoded fanout bytes" <| fun _ ->
        let state, members, output = setup options memoryStorage 3
        ready state members[0] (asset 1UL [|1uy|])
        for observer in [members[1];members[2]] do view state observer members[0] 1UL 1.0
        PhantomAgent.tick state 4L
        output.Clear()
        let bytes = pose 1UL 1UL 10UL
        for _ in 1 .. 10000 do PhantomAgent.receive state 5L (fst members[0]) DeliveryLane.Poses bytes
        Expect.equal (PhantomAgent.snapshot state).LatestPoses 1 "source bounded"
        PhantomAgent.receive state 6L (fst members[0]) DeliveryLane.Poses (pose 1UL 2UL 10UL)
        PhantomAgent.tick state 6L
        let packets = output |> Seq.filter (fun (_, packet) -> packet.Lane = DeliveryLane.Poses) |> Seq.map snd |> Seq.toArray
        Expect.equal packets.Length 2 "latest per subscriber"
        Expect.isTrue (Object.ReferenceEquals(packets[0].Bytes, packets[1].Bytes)) "immutable encoding shared"
        Expect.equal (Dreamsleeve.Protocol.Phantom.ServerPosePacket.Parser.ParseFrom(packets[0].Bytes).Sample.Sequence) 2UL "latest"

    testCase "bounded fanout rotates between sources and subscribers without retaining a pose backlog" <| fun _ ->
        let state, members, output = setup { options with MaxPoseFanoutPerTick = 1 } memoryStorage 4
        for index in [0;1] do
            ready state members[index] (asset 1UL [|byte (index + 1)|])
            for observer in [2;3] do view state members[observer] members[index] (uint64 index + 1UL) (double (index + 1))
        for observer in [2;3] do PhantomAgent.handle state 4L (fst members[observer]) (PhantomRequest.Preferences { Publish = true; Receive = true; Maximum = 4; Distance = 4096.0f })
        PhantomAgent.tick state 4L
        output.Clear()
        for index in [0;1] do PhantomAgent.receive state 5L (fst members[index]) DeliveryLane.Poses (pose 1UL 1UL 10UL)
        for at in 5L .. 12L do
            let previous = output.Count
            PhantomAgent.tick state at
            Expect.isLessThanOrEqual (output.Count - previous) 1 "Per-turn work/fanout bound."
        let pairs = output |> Seq.filter (fun (_, packet) -> packet.Lane = DeliveryLane.Poses) |> Seq.map (fun (id, packet) -> id, Dreamsleeve.Protocol.Phantom.ServerPosePacket.Parser.ParseFrom(packet.Bytes).PlayerId) |> Set.ofSeq
        Expect.equal pairs.Count 4 "Both subscribers of both sources get their latest snapshot fairly."
        Expect.equal (PhantomAgent.snapshot state).LatestPoses 2 "One retained snapshot per source."
    testCase "shared pose byte budget rotates sources instead of starving the tail" <| fun _ ->
        let config = { options with PoseBytesPerSecond = 1024; TotalPoseBytesPerSecond = 1024 }
        let state, members, output = setup config memoryStorage 6
        for index in 0 .. 2 do
            ready state members[index] (asset 1UL [|byte index|])
            view state members[index+3] members[index] 1UL 1.0
        PhantomAgent.tick state 4L
        output.Clear()
        for sequence in 1UL .. 3UL do
            let at = int64 sequence * 1000L
            for index in 0 .. 2 do
                let packet = Dreamsleeve.Protocol.Phantom.ClientPosePacket.Parser.ParseFrom(pose 1UL sequence 10UL)
                packet.Sample.Payload <- ByteString.CopyFrom(Array.zeroCreate 600)
                PhantomAgent.receive state at (fst members[index]) DeliveryLane.Poses (packet.ToByteArray())
            let previous = output.Count
            PhantomAgent.tick state at
            let bytes = output |> Seq.skip previous |> Seq.filter(fun (_, packet) -> packet.Lane = DeliveryLane.Poses) |> Seq.sumBy(fun (_, packet) -> packet.Bytes.Length)
            Expect.isLessThanOrEqual bytes 1024 "Each tick respects the shared byte credit."
        let delivered = output |> Seq.choose(fun (_, packet) ->
            if packet.Lane = DeliveryLane.Poses then Some(Dreamsleeve.Protocol.Phantom.ServerPosePacket.Parser.ParseFrom(packet.Bytes).PlayerId) else None) |> Set.ofSeq
        Expect.equal delivered.Count 3 "Every publisher gets a turn within three refill ticks."

    testCase "pending and failed replacement retain publication and commit without AOI removal" <| fun _ ->
        let pending = TaskCompletionSource<Result<bool,string>>()
        let mutable nextUpload = result true
        let storage = { memoryStorage with StartUpload = fun _ -> nextUpload }
        let state, members, output = setup options storage 2
        ready state members[0] (asset 1UL [|1uy|])
        view state members[1] members[0] 1UL 1.0
        PhantomAgent.tick state 4L
        let sourceId = fst members[0]
        let observerId = fst members[1]
        PhantomAgent.receive state 5L sourceId DeliveryLane.Poses (pose 1UL 10UL 10UL)
        PhantomAgent.tick state 5L
        output.Clear()
        PhantomAgent.handle state 6L observerId (PhantomRequest.Download((snd members[0]).Identity.PlayerId, AppearanceGeneration.create 1UL |> ok, requestId()))
        PhantomAgent.tick state 6L
        let oldDownload = transfer output
        output.Clear()
        nextUpload <- pending.Task
        PhantomAgent.handle state 7L sourceId (PhantomRequest.Publish(asset 2UL [|2uy|], 10UL, requestId()))
        PhantomAgent.tick state 7L
        let snapshot = PhantomAgent.snapshot state
        Expect.equal snapshot.Sources 1 "Previous publication remains available."
        Expect.equal snapshot.Subscriptions 1 "No appearance-induced AOI departure."
        Expect.equal snapshot.Transfers 2 "Previous model download continues while upload is pending."
        Expect.equal snapshot.LatestPoses 1 "Current pose remains usable."
        PhantomAgent.receive state 8L sourceId DeliveryLane.Poses (pose 1UL 9UL 10UL)
        PhantomAgent.tick state 8L
        Expect.isFalse (output |> Seq.exists (fun (_, packet) -> packet.Lane = DeliveryLane.Poses)) "Pending upload does not reset the old sequence floor."
        pending.SetResult(Error "temporary IO failure")
        PhantomAgent.tick state 9L
        Expect.equal (PhantomAgent.snapshot state).Sources 1 "Failed replacement retains publication."
        Expect.isFalse (models output |> Array.exists (fun packet -> not (isNull packet.Remove))) "No removal while pending or after failure."
        Expect.isFalse (models output |> Array.exists (fun packet -> not (isNull packet.Complete) && packet.Complete.TransferId = oldDownload.Value)) "Old download is not cancelled by failure."
        output.Clear()
        nextUpload <- result true
        PhantomAgent.handle state 10L sourceId (PhantomRequest.Publish(asset 2UL [|2uy|], 10UL, requestId()))
        PhantomAgent.tick state 11L
        Expect.isFalse (models output |> Array.exists (fun packet -> not (isNull packet.Remove))) "Replacement sends Offer without Remove."
        let offer = models output |> Array.pick (fun packet -> if isNull packet.Offer then None else Some packet.Offer)
        Expect.equal offer.Asset.Generation 2UL "Only committed generation is offered."
        Expect.isGreaterThan offer.ViewRevision 1UL "Replacement has fresh revision."
        Expect.equal (PhantomAgent.snapshot state).LatestPoses 0 "Old pose cleared only on commit."
        PhantomAgent.receive state 12L sourceId DeliveryLane.Poses (pose 1UL 11UL 10UL)
        Expect.equal (PhantomAgent.snapshot state).LatestPoses 0 "Previous generation rejected after commit."
        PhantomAgent.receive state 13L sourceId DeliveryLane.Poses (pose 2UL 1UL 10UL)
        PhantomAgent.tick state 13L
        let received = output |> Seq.choose (fun (id, packet) -> if id = observerId && packet.Lane = DeliveryLane.Poses then Some(Dreamsleeve.Protocol.Phantom.ServerPosePacket.Parser.ParseFrom packet.Bytes) else None) |> Seq.toArray
        Expect.equal received.Length 1 "New generation starts at sequence one."
        Expect.equal received[0].Sample.Generation 2UL "No mixed-generation fanout."

    testCase "replacement bundles keep the old generation moving until display acknowledgement" <| fun _ ->
        let pending = TaskCompletionSource<Result<bool,string>>()
        let mutable next = result true
        let state, members, output = setup options { memoryStorage with StartUpload = fun _ -> next } 2
        ready state members[0] (asset 1UL [|1uy|])
        view state members[1] members[0] 1UL 1.0
        PhantomAgent.tick state 4L
        next <- pending.Task
        PhantomAgent.handle state 5L (fst members[0]) (PhantomRequest.Publish(asset 2UL [|2uy|], 10UL, requestId()))
        let packet = Dreamsleeve.Protocol.Phantom.ClientPosePacket.Parser.ParseFrom(pose 2UL 1UL 10UL)
        packet.PreviousSample <- Dreamsleeve.Protocol.Phantom.ClientPosePacket.Parser.ParseFrom(pose 1UL 20UL 10UL).Sample
        packet.PreviousSample.SampledAtUs <- packet.Sample.SampledAtUs
        output.Clear()
        PhantomAgent.receive state 6L (fst members[0]) DeliveryLane.Poses (packet.ToByteArray())
        PhantomAgent.tick state 6L
        let sent = output |> Seq.find (fun (_, p) -> p.Lane = DeliveryLane.Poses) |> snd
        let received = Dreamsleeve.Protocol.Phantom.ServerPosePacket.Parser.ParseFrom sent.Bytes
        Expect.equal received.PreviousSample.Generation 1UL "Receiver can decode a live pose against its still-visible scene."
        let decoded = PhantomCodec.decodePose options (packet.ToByteArray()) |> ok
        Expect.equal (PhantomCodec.posePacketSize (snd members[0]).Identity.PlayerId received.ViewRevision decoded) sent.Bytes.Length "Bundle budget is exact."
        pending.SetResult(Ok true)
        PhantomAgent.tick state 7L
        let offer = models output |> Array.pick (fun p -> if isNull p.Offer then None else Some p.Offer)
        Expect.equal offer.Asset.Generation 2UL "Only complete asset gets offered."
        output.Clear()
        PhantomAgent.handle state 8L (fst members[0]) (PhantomRequest.Publish(asset 3UL [|3uy|], 10UL, requestId()))
        PhantomAgent.tick state 8L
        Expect.isTrue (models output |> Array.exists (fun p -> not (isNull p.Complete) && p.Complete.RetryAfterMs > 0u)) "Third generation waits for display."
        PhantomAgent.handle state 9L (fst members[1]) (PhantomRequest.Displayed((snd members[0]).Identity.PlayerId, offer.ViewRevision - 1UL, AppearanceGeneration.create 2UL |> ok))
        PhantomAgent.tick state 9L
        Expect.isFalse (models output |> Array.exists (fun p -> not (isNull p.Settled))) "Stale view cannot retire the bridge."
        PhantomAgent.handle state 10L (fst members[1]) (PhantomRequest.Displayed((snd members[0]).Identity.PlayerId, offer.ViewRevision, AppearanceGeneration.create 2UL |> ok))
        PhantomAgent.tick state 10L
        Expect.isTrue (models output |> Array.exists (fun p -> not (isNull p.Settled) && p.Settled.Generation = 2UL)) "Actual display retires previous pose."
        output.Clear()
        packet.Sample.Sequence <- 2UL
        PhantomAgent.receive state 11L (fst members[0]) DeliveryLane.Poses (packet.ToByteArray())
        PhantomAgent.tick state 11L
        let latest = output |> Seq.find (fun (_, p) -> p.Lane = DeliveryLane.Poses) |> snd
        Expect.isNull (Dreamsleeve.Protocol.Phantom.ServerPosePacket.Parser.ParseFrom(latest.Bytes).PreviousSample) "Late bridge stripped after settle."

    testCase "rejected replacement falls back to live ready poses without reopening either sequence floor" <| fun _ ->
        let pending = TaskCompletionSource<Result<bool,string>>()
        let mutable next = result true
        let state, members, output = setup options { memoryStorage with StartUpload = fun _ -> next } 2
        ready state members[0] (asset 1UL [|1uy|])
        view state members[1] members[0] 1UL 1.0
        PhantomAgent.tick state 4L
        next <- pending.Task
        let id = fst members[0]
        PhantomAgent.handle state 5L id (PhantomRequest.Publish(asset 2UL [|2uy|], 10UL, requestId()))
        let packet = Dreamsleeve.Protocol.Phantom.ClientPosePacket.Parser.ParseFrom(pose 2UL 1UL 10UL)
        packet.PreviousSample <- Dreamsleeve.Protocol.Phantom.ClientPosePacket.Parser.ParseFrom(pose 1UL 20UL 10UL).Sample
        packet.PreviousSample.SampledAtUs <- packet.Sample.SampledAtUs
        PhantomAgent.receive state 6L id DeliveryLane.Poses (packet.ToByteArray())
        PhantomAgent.tick state 6L
        pending.SetResult(Error "hash mismatch")
        PhantomAgent.tick state 7L
        output.Clear()
        PhantomAgent.receive state 8L id DeliveryLane.Poses (pose 1UL 21UL 10UL)
        PhantomAgent.tick state 8L
        let sent = output |> Seq.find (fun (_, p) -> p.Lane = DeliveryLane.Poses) |> snd
        let restored = Dreamsleeve.Protocol.Phantom.ServerPosePacket.Parser.ParseFrom sent.Bytes
        Expect.equal restored.Sample.Generation 1UL "The still-ready native scene keeps moving."
        Expect.equal restored.Sample.Sequence 21UL "Fallback advances its own sequence."
        output.Clear()
        PhantomAgent.receive state 9L id DeliveryLane.Poses (pose 1UL 20UL 10UL)
        PhantomAgent.tick state 9L
        PhantomAgent.receive state 10L id DeliveryLane.Poses (packet.ToByteArray())
        PhantomAgent.tick state 10L
        Expect.isFalse (output |> Seq.exists (fun (_, p) -> p.Lane = DeliveryLane.Poses)) "Neither old floor nor pending floor reopens."

    testCase "pose timeout and withdrawal preserve admission and sequence watermarks" <| fun _ ->
        let state, members, _ = setup options memoryStorage 1
        ready state members[0] (asset 1UL [|1uy|])
        let id = fst members[0]
        PhantomAgent.receive state 5L id DeliveryLane.Poses (pose 1UL 20UL 10UL)
        let expired = 6L + int64 options.PoseTimeoutMs
        PhantomAgent.tick state expired
        PhantomAgent.receive state (expired + 1L) id DeliveryLane.Poses (pose 1UL 19UL 10UL)
        Expect.equal (PhantomAgent.snapshot state).LatestPoses 0 "Timeout does not reopen the sequence floor."
        PhantomAgent.receive state (expired + 2L) id DeliveryLane.Poses (pose 1UL 21UL 10UL)
        Expect.equal (PhantomAgent.snapshot state).LatestPoses 1 "Fresh complete snapshot resumes."
        PhantomAgent.handle state (expired + 3L) id PhantomRequest.Withdraw
        PhantomAgent.receive state (expired + 4L) id DeliveryLane.Poses (pose 1UL 22UL 10UL)
        Expect.equal (PhantomAgent.snapshot state).LatestPoses 0 "Historical manifest cannot authorize post-withdraw poses."

    testCase "bundled pose rejects mismatched source time context and non-previous generation" <| fun _ ->
        let packet = Dreamsleeve.Protocol.Phantom.ClientPosePacket.Parser.ParseFrom(pose 2UL 1UL 10UL)
        packet.PreviousSample <- packet.Sample.Clone()
        Expect.isError (PhantomCodec.decodePose options (packet.ToByteArray())) "Same generation is not previous."
        packet.PreviousSample.Generation <- 1UL
        packet.PreviousSample.ContextRevision <- 11UL
        Expect.isError (PhantomCodec.decodePose options (packet.ToByteArray())) "Context mismatch."
        packet.PreviousSample.ContextRevision <- 10UL
        packet.PreviousSample.SampledAtUs <- 50001UL
        Expect.isError (PhantomCodec.decodePose options (packet.ToByteArray())) "Two poses must be simultaneous."
        packet.PreviousSample.SampledAtUs <- 50000UL
        Expect.isOk (PhantomCodec.decodePose options (packet.ToByteArray())) "Independent sequences are allowed."

    testCase "same-generation warm republish keeps pose sequence floor" <| fun _ ->
        let state, members, _ = setup options memoryStorage 1
        let manifest = asset 1UL [|1uy|]
        ready state members[0] manifest
        PhantomAgent.receive state 5L (fst members[0]) DeliveryLane.Poses (pose 1UL 10UL 10UL)
        PhantomAgent.handle state 6L (fst members[0]) (PhantomRequest.Publish(manifest, 10UL, requestId()))
        PhantomAgent.tick state 7L
        PhantomAgent.receive state 8L (fst members[0]) DeliveryLane.Poses (pose 1UL 9UL 10UL)
        Expect.equal (PhantomAgent.snapshot state).LatestPoses 1 "Identical model republish retains the current pose; old sequence cannot replace it."
        PhantomAgent.receive state 9L (fst members[0]) DeliveryLane.Poses (pose 1UL 11UL 10UL)
        Expect.equal (PhantomAgent.snapshot state).LatestPoses 1 "Newer pose accepted."
    testCase "exhausted fanout byte budget does not serialize a full pose for denied subscribers" <| fun _ ->
        let config = { options with PoseBytesPerSecond = 1024 * 1024; TotalPoseBytesPerSecond = 1024 * 1024; MaxSubscribers = 16 }
        let state, members, output = setup config memoryStorage 17
        ready state members[0] (asset 1UL [|1uy|])
        for observer in members[1..] do view state observer members[0] 1UL 1.0
        PhantomAgent.tick state 4L
        let incoming sequence = Dreamsleeve.Protocol.Phantom.ClientPosePacket(ProtocolVersion = ProtocolCodec.Version,
            Sample = Dreamsleeve.Protocol.Phantom.PoseSample(Generation = 1UL, ContextRevision = 10UL, Sequence = sequence, Payload = ByteString.CopyFrom(Array.zeroCreate 73728))).ToByteArray()
        PhantomAgent.receive state 5L (fst members[0]) DeliveryLane.Poses (incoming 1UL)
        PhantomAgent.tick state 5L
        output.Clear()
        PhantomAgent.receive state 6L (fst members[0]) DeliveryLane.Poses (incoming 2UL)
        let before = GC.GetAllocatedBytesForCurrentThread()
        PhantomAgent.tick state 6L
        let allocated = GC.GetAllocatedBytesForCurrentThread() - before
        Expect.equal output.Count 0 "No recipient fits the remaining global byte budget."
        Expect.isLessThan allocated 65536L "No complete compressed pose allocation when admission fails."

    testCase "control source flood preserves bounded replies and admitted source" <| fun _ ->
        let state, members, output = setup options memoryStorage 1
        ready state members[0] (asset 1UL [|1uy|])
        output.Clear()
        let bytes = Dreamsleeve.Protocol.Phantom.ClientAssetPacket(ProtocolVersion = ProtocolCodec.Version,
            Download = Dreamsleeve.Protocol.Phantom.Download(PlayerId = 2UL, Generation = 1UL, RequestId = 1001UL)).ToByteArray()
        for _ in 1 .. 10000 do PhantomAgent.receive state 4L (fst members[0]) DeliveryLane.Models bytes
        PhantomAgent.tick state 5L
        Expect.equal (PhantomAgent.snapshot state).Sources 1 "Control denial burst cannot poison active phantom ownership."
        Expect.isLessThanOrEqual (models output |> Array.filter (fun packet -> not (isNull packet.Complete)) |> Array.length) 8 "Bounded denial burst."
    testCase "source admission transfer concurrency and timeout are bounded" <| fun _ ->
        let config = { options with MaxSources = 2; MaxTransfers = 1; TransferTimeoutMs = 2 }
        let state, members, _ = setup config { memoryStorage with StartUpload = fun _ -> TaskCompletionSource<Result<bool,string>>().Task } 3
        Expect.equal (PhantomAgent.snapshot state).Members 3 "receivers remain admitted"
        for id, _ in members do PhantomAgent.handle state 2L id (PhantomRequest.Publish(asset 1UL [|1uy|], 10UL, requestId()))
        Expect.equal (PhantomAgent.snapshot state).Transfers 1 "concurrency"
        PhantomAgent.tick state 5L
        Expect.equal (PhantomAgent.snapshot state).Transfers 0 "expiry"

    storageCase "cold full model upload and download round trip; warm generation needs no chunks" (fun root config _ -> task {
        let config = { config with StoragePath = Path.Combine(root, "streaming"); Limits = PhantomOptions.defaults.Limits
                                   DiskBytes = 32L * 1024L * 1024L; RamBytes = 16L * 1024L * 1024L
                                   ChunkBytes = 16384; WindowChunks = 4 }
        let storage = PhantomStorage.create config
        try
            let state, members, output = setup config storage 2
            let bytes = Array.init (13 * 1024 * 1024 + 7) (fun index -> byte (index % 251))
            let manifest = asset 1UL bytes
            let mutable clock = 2L
            let pump predicate = task {
                let deadline = Environment.TickCount64 + 15000L
                while not (predicate()) && Environment.TickCount64 < deadline do
                    clock <- clock + 100L
                    PhantomAgent.tick state clock
                    do! Task.Delay 1
                Expect.isTrue (predicate()) "Bounded streaming made progress before test deadline."
            }
            PhantomAgent.handle state clock (fst members[0]) (PhantomRequest.Publish(manifest, 10UL, requestId()))
            do! pump (fun () -> models output |> Array.exists (fun packet -> not (isNull packet.Transfer)))
            let uploadId = transfer output
            let mutable sent = 0
            let mutable acknowledged = 0
            while sent < bytes.Length || (PhantomAgent.snapshot state).Sources = 0 do
                while sent < bytes.Length && sent - acknowledged < config.WindowChunks * config.ChunkBytes do
                    let count = min config.ChunkBytes (bytes.Length - sent)
                    PhantomAgent.handle state clock (fst members[0]) (PhantomRequest.Chunk(uploadId, sent, bytes[sent..sent+count-1]))
                    sent <- sent + count
                let previous = acknowledged
                do! pump (fun () ->
                    acknowledged <- models output |> Array.choose (fun packet -> if isNull packet.Progress then None else Some(int packet.Progress.NextOffset)) |> Array.fold max 0
                    acknowledged > previous || (PhantomAgent.snapshot state).Sources = 1)
            Expect.equal acknowledged bytes.Length "Every compressed byte was acknowledged."
            Expect.equal (File.ReadAllBytes(Path.Combine(config.StoragePath, manifest.Hash.Hex + ".zst"))) bytes "Cold verified file."
            view state members[1] members[0] 1UL 1.0
            clock <- clock + 100L
            PhantomAgent.tick state clock
            output.Clear()
            PhantomAgent.handle state clock (fst members[1]) (PhantomRequest.Download((snd members[0]).Identity.PlayerId, manifest.Generation, requestId()))
            do! pump (fun () -> models output |> Array.exists (fun packet -> not (isNull packet.Transfer)))
            let downloadId = transfer output
            let received = Array.zeroCreate bytes.Length
            let mutable next = 0
            while (PhantomAgent.snapshot state).Transfers > 0 do
                do! pump (fun () -> models output |> Array.exists (fun packet -> not (isNull packet.Chunk)) || (PhantomAgent.snapshot state).Transfers = 0)
                for packet in models output do
                    if not (isNull packet.Chunk) then
                        let chunk = packet.Chunk
                        Expect.equal (int chunk.Offset) next "Chunks stay ordered."
                        let content = chunk.Data.ToByteArray()
                        Buffer.BlockCopy(content, 0, received, next, content.Length)
                        next <- next + content.Length
                        PhantomAgent.handle state clock (fst members[1]) (PhantomRequest.Progress(downloadId, next))
                output.Clear()
                clock <- clock + 100L
                PhantomAgent.tick state clock
            Expect.equal received bytes "Cold download round trip."
            output.Clear()
            let warm = asset 2UL bytes
            PhantomAgent.handle state clock (fst members[0]) (PhantomRequest.Publish(warm, 10UL, requestId()))
            do! pump (fun () -> models output |> Array.exists (fun packet -> not (isNull packet.Complete) && packet.Complete.Accepted))
            Expect.equal (PhantomAgent.snapshot state).Transfers 0 "Warm publication settles without incoming chunks."
            Expect.equal (Directory.GetFiles(config.StoragePath, "*.tmp").Length) 0 "No partial upload left."
            PhantomAgent.stop state
        finally storage.Dispose().GetAwaiter().GetResult()
    })
    storageCase "streaming SHA256 atomic completion and restart deduplication" (fun root config storage -> task {
        let bytes = [|1uy;2uy;3uy;4uy;5uy;6uy;7uy;8uy|]
        let manifest = asset 1UL bytes
        let! started = storage.StartUpload(PhantomTransferId 1UL, manifest)
        Expect.equal (ok started) false "upload needed"
        let! partial = storage.WriteChunk(PhantomTransferId 1UL, 0, bytes[0..3])
        Expect.equal (ok partial) false "not ready"
        Expect.equal (Directory.GetFiles(root, "*.zst").Length) 0 "no partial final"
        let! complete = storage.WriteChunk(PhantomTransferId 1UL, 4, bytes[4..7])
        Expect.equal (ok complete) true "verified"
        Expect.equal (File.ReadAllBytes(Path.Combine(root, manifest.Hash.Hex + ".zst"))) bytes "opaque content"
        Expect.equal (Directory.GetFiles(root, "*.tmp").Length) 0 "atomic rename"
        let restarted = PhantomStorage.create config
        try
            let! cached = restarted.StartUpload(PhantomTransferId 2UL, manifest)
            Expect.equal (ok cached) true "restart cache verified"
        finally restarted.Dispose().GetAwaiter().GetResult()
    })

    storageCase "hash and offset failures delete temporary files and refund admission" (fun root _ storage -> task {
        let manifest = asset 1UL [|1uy;2uy;3uy;4uy|]
        let! _ = storage.StartUpload(PhantomTransferId 1UL, manifest)
        let! corrupt = storage.WriteChunk(PhantomTransferId 1UL, 0, [|4uy;3uy;2uy;1uy|])
        Expect.isError corrupt "hash mismatch"
        Expect.equal (Directory.GetFiles(root, "*.tmp").Length) 0 "cleanup"
        let! retry = storage.StartUpload(PhantomTransferId 2UL, manifest)
        Expect.equal (ok retry) false "quota refunded"
        let! offset = storage.WriteChunk(PhantomTransferId 2UL, 1, [|1uy|])
        Expect.isError offset "order"
        Expect.equal (Directory.GetFiles(root, "*.tmp").Length) 0 "failed offset cleanup"
    })

    storageCase "active read pins exclude eviction until cancellation" (fun root config _ -> task {
        let store = PhantomStorage.create { config with StoragePath = Path.Combine(root, "bounded"); DiskBytes = 4L; CacheEntries = 2 }
        try
            let first = asset 1UL [|1uy;2uy;3uy;4uy|]
            let second = asset 2UL [|4uy;3uy;2uy;1uy|]
            let! _ = store.StartUpload(PhantomTransferId 1UL, first)
            let! stored = store.WriteChunk(PhantomTransferId 1UL, 0, [|1uy;2uy;3uy;4uy|])
            Expect.isOk stored "stored"
            let! opened = store.StartDownload(PhantomTransferId 2UL, first)
            Expect.isOk opened "pinned"
            let! refused = store.StartUpload(PhantomTransferId 3UL, second)
            Expect.isError refused "quota protects active read"
            let! bytes = store.ReadChunk(PhantomTransferId 2UL, 0, 4)
            Expect.equal (ok bytes) [|1uy;2uy;3uy;4uy|] "RAM read"
            do! store.Cancel(PhantomTransferId 2UL)
            let! admitted = store.StartUpload(PhantomTransferId 4UL, second)
            Expect.equal (ok admitted) false "unpin enables LRU"
        finally store.Dispose().GetAwaiter().GetResult()
    })

    storageCase "multiple read pins count as one cached file for entry admission" (fun root config _ -> task {
        let store = PhantomStorage.create { config with StoragePath = Path.Combine(root, "pin-quota"); DiskBytes = 8L; CacheEntries = 2 }
        try
            let first, second = asset 1UL [|1uy;2uy;3uy;4uy|], asset 2UL [|4uy;3uy;2uy;1uy|]
            let! _ = store.StartUpload(PhantomTransferId 1UL, first)
            let! _ = store.WriteChunk(PhantomTransferId 1UL, 0, [|1uy;2uy;3uy;4uy|])
            let! _ = store.StartDownload(PhantomTransferId 2UL, first)
            let! _ = store.StartDownload(PhantomTransferId 3UL, first)
            let! admitted = store.StartUpload(PhantomTransferId 4UL, second)
            Expect.equal (ok admitted) false "Read leases do not consume extra file-entry quota."
            let! full = store.WriteChunk(PhantomTransferId 4UL, 0, [|4uy;3uy;2uy;1uy|])
            Expect.equal (ok full) true "Two files fit exact disk/entry quota while old file remains pinned twice."
        finally store.Dispose().GetAwaiter().GetResult()
    })
    storageCase "RAM fill rejects changed file length before allocating descriptor-sized cache" (fun root _ storage -> task {
        let bytes = [|1uy;2uy;3uy;4uy|]
        let manifest = asset 1UL bytes
        let! _ = storage.StartUpload(PhantomTransferId 1UL, manifest)
        let! _ = storage.WriteChunk(PhantomTransferId 1UL, 0, bytes)
        let! _ = storage.StartDownload(PhantomTransferId 2UL, manifest)
        let path = Path.Combine(root, manifest.Hash.Hex + ".zst")
        File.WriteAllBytes(path, Array.zeroCreate 32)
        let! grown = storage.ReadChunk(PhantomTransferId 2UL, 0, 4)
        Expect.isError grown "Warm verified entry cannot allocate an unexpectedly grown file."
        File.WriteAllBytes(path, bytes)
        let! recovered = storage.ReadChunk(PhantomTransferId 2UL, 0, 4)
        Expect.equal (ok recovered) bytes "Failed fill did not publish invalid RAM cache."
        do! storage.Cancel(PhantomTransferId 2UL)
    })
    storageCase "startup removes abandoned partials and corrupt cached files" (fun root config _ -> task {
        Directory.CreateDirectory root |> ignore
        File.WriteAllBytes(Path.Combine(root, "orphan.tmp"), [|0uy|])
        let manifest = asset 1UL [|1uy;2uy;3uy;4uy|]
        File.WriteAllBytes(Path.Combine(root, manifest.Hash.Hex + ".zst"), [|4uy;3uy;2uy;1uy|])
        let store = PhantomStorage.create config
        try
            let! corrupt = store.StartDownload(PhantomTransferId 1UL, manifest)
            Expect.isError corrupt "integrity on restart"
            Expect.equal (Directory.GetFiles(root, "*.tmp").Length) 0 "partial cleanup"
            Expect.equal (Directory.GetFiles(root, "*.zst").Length) 0 "corruption removed"
        finally store.Dispose().GetAwaiter().GetResult()
    })
]