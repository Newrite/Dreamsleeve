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
let private options = { PhantomOptions.defaults with ReplicationIntervalMs = 1; PublishCooldownMs = 0; PoseIntervalMs = 1; ChunkBytes = 4 }
let private asset gen (bytes: byte array) =
    PhantomManifest.create options.Limits (AssetHash.create (SHA256.HashData bytes) |> ok)
        (AppearanceGeneration.create gen |> ok) 2u (uint32 bytes.Length) (uint32 bytes.Length) 2u |> ok
let private player number context =
    let profile = PlayerData.create (PlayerId.create number |> ok) (Username.create 32 $"p{number}" |> ok)
                      (DisplayName.create 64 $"P{number}" |> ok) NameColor.unknown
    let form = FormKey.create (PluginName.create 260 "Skyrim.esm" |> ok) (LocalFormId.create 1u |> ok)
    let location = PlayerLocation.create (Location.create form (LocationName.create 256 "Test" |> ok)) Position.zero CameraDirection.zero
    Player.create profile
    |> Player.applyUpdate (PlayerUpdate.BeginCharacter(CharacterName.create 128 "Test" |> ok))
    |> Player.applyUpdate (PlayerUpdate.SetLocation(context, ValueSome location))
    |> Player.snapshot
let private result value = Task.FromResult(Ok value)
let private memoryStorage : PhantomStoragePort = {
    StartUpload = fun _ -> result true
    WriteChunk = fun _ -> result false
    StartDownload = fun _ -> result None
    ReadChunk = fun (_, offset, destination) ->
        for index in 0 .. destination.Length - 1 do destination.Span[index] <- byte (offset + index)
        result destination.Length
    Cancel = fun _ -> Task.FromResult ()
    Dispose = fun () -> Task.FromResult ()
    OwnerFailure = TaskCompletionSource<exn>().Task
}
let private leases = Runtime.CompilerServices.ConditionalWeakTable<PhantomAgent.State, Collections.Generic.Dictionary<PhantomTransferId, PhantomHttpLease>>()
let private fakeHttp () =
    let live = Collections.Generic.Dictionary<PhantomTransferId, PhantomHttpLease>()
    let port = {
        Admit = fun (_, id, manifest, _, _) ->
            let lease = PhantomHttpLease(String.replicate 64 "a", manifest.CompressedBytes)
            live[id] <- lease
            lease
        Cancel = fun id ->
            match live.TryGetValue id with true, lease -> lease.Finish(Error PhantomHttpError.Canceled) | _ -> ()
            Task.FromResult ()
        Serve = fun _ -> Task.FromResult(Error PhantomHttpError.Capability)
        Dispose = fun () -> Task.FromResult ()
        OwnerFailure = TaskCompletionSource<exn>().Task
    }
    port, live
let private advance state id count at =
    let live = leases.GetValue(state, fun _ -> failwith "missing HTTP fixture")
    live[id].Advance count
    PhantomAgent.tick state at
let private readChunk (storage: PhantomStoragePort) (id, offset, count) = task {
    let bytes = Array.zeroCreate<byte> count
    let! read = storage.ReadChunk(id, offset, bytes.AsMemory())
    return read |> Result.map (fun count -> bytes[..count-1])
}
let private setup config storage count =
    let output = ResizeArray<Guid * TransportPacket>()
    let http, live = fakeHttp()
    let state = PhantomAgent.create config storage http (fun (id, packet) -> output.Add(id, packet); Ok ())
    leases.Add(state, live)
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
    PhantomAgent.handle state 2L id (PhantomRequest.Publish(manifest, value.MovementContext, requestId(), None))
    PhantomAgent.tick state 3L
let private view state (observer, _: PlayerSnapshot) (source, value: PlayerSnapshot) revision distance =
    PhantomAgent.observe state (PhantomObservation.View(observer, source, value.Identity.PlayerId, revision, distance, None))
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

type private ControlledRead(bytes: byte array, entered: TaskCompletionSource<unit>, resume: TaskCompletionSource<unit>, failure: exn option) =
    inherit MemoryStream(bytes)
    let reading () =
        entered.TrySetResult() |> ignore
        resume.Task.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
        match failure with Some error -> raise error | None -> ()
    override _.Read(buffer: byte array, offset: int, count: int) =
        reading()
        base.Read(buffer, offset, count)
    override _.Read(buffer: Span<byte>) =
        reading()
        base.Read buffer

type private FaultingDestination(error: exn) =
    inherit System.Buffers.MemoryManager<byte>()
    override this.Memory = this.CreateMemory 4
    override _.GetSpan() = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw(); Span<byte>.Empty
    override _.Pin(_: int) = raise error
    override _.Unpin() = ()
    override _.Dispose(_: bool) = ()

let tests = testList "Phantoms" [

    testCase "camera sector has near protection hysteresis and unknown fallback" <| fun _ ->
        Expect.isTrue (PhantomPolicy.inView false 100.0 (Some -1.0)) "Near player remains visible."
        Expect.isTrue (PhantomPolicy.inView false 1000000.0 None) "Unknown camera fails open."
        Expect.isFalse (PhantomPolicy.inView false 1000000.0 (Some -1.0)) "Behind camera is culled."
        Expect.isFalse (PhantomPolicy.inView false 1000000.0 (Some -0.4)) "Does not enter beyond 105 degrees."
        Expect.isTrue (PhantomPolicy.inView true 1000000.0 (Some -0.4)) "Retained until 120 degrees."
        let origin = (player 1UL 10UL).Location.Value
        let camera = CameraDirection.create 0.0f 1.0f 0.0f |> ok
        let origin = PlayerLocation.create origin.Location Position.zero camera
        let target y = PlayerLocation.create origin.Location (Position.create 0.0f y 0.0f |> ok) CameraDirection.zero
        Expect.equal (PhantomPolicy.facing origin (target 1000.0f)) (Some 1.0) "Forward."
        Expect.equal (PhantomPolicy.facing origin (target -1000.0f)) (Some -1.0) "Backward."

    testCase "camera turn changes subscriptions and resumes demand without republishing model" <| fun _ ->
        let state, members, output = setup options memoryStorage 2
        let sourceId, sourcePlayer = members[0]
        let observerId, _ = members[1]
        ready state members[0] (asset 1UL [|1uy|])
        output.Clear()
        let look cosine =
            PhantomAgent.observe state (PhantomObservation.View(observerId, sourceId, sourcePlayer.Identity.PlayerId, 1UL, 1000000.0, Some cosine))
        look -1.0
        PhantomAgent.tick state 4L
        Expect.equal (PhantomAgent.snapshot state).Subscriptions 0 "Behind camera."
        look 1.0
        PhantomAgent.tick state 5L
        Expect.equal (PhantomAgent.snapshot state).Subscriptions 1 "Same AOI authority, changed camera."
        let demand required = models output |> Array.exists (fun p -> not (isNull p.PoseDemand) && p.PoseDemand.Required = required && p.PoseDemand.ContextRevision = 10UL)
        Expect.isTrue (demand true) "Source resumes."
        output.Clear()
        look -0.4
        PhantomAgent.tick state 6L
        Expect.equal (PhantomAgent.snapshot state).Subscriptions 1 "Hysteresis retains view."
        look -1.0
        PhantomAgent.tick state 7L
        Expect.equal (PhantomAgent.snapshot state).Subscriptions 0 "Last viewer gone."
        Expect.isTrue (demand false) "Source pauses."
        Expect.equal (PhantomAgent.snapshot state).Sources 1 "Asset retained."
        output.Clear()
        look 1.0
        PhantomAgent.tick state 8L
        Expect.isTrue (demand true) "Source resumes from retained asset."
        PhantomAgent.stop state

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
        let state = PhantomAgent.create disabled storage (PhantomHttp.create disabled storage) (fun (_, packet) -> output.Add packet; Ok ())
        Expect.equal (PhantomAgent.observationMode state) PhantomObservationMode.Membership "Bootstrap membership survives disabled replication."
        let id = Guid.NewGuid()
        PhantomAgent.observe state (PhantomObservation.Member(id, player 1UL 10UL))
        PhantomAgent.activate state id
        PhantomAgent.tick state 1L
        Expect.equal output.Count 1 "Only the activation policy is sent."
        let policy = Dreamsleeve.Protocol.Phantom.ServerAssetPacket.Parser.ParseFrom(output[0].Bytes).Policy
        Expect.isFalse policy.Enabled "Authenticated client learns the effective disabled policy."
        PhantomAgent.handle state 2L id (PhantomRequest.Publish(asset 1UL [|1uy|], 10UL, requestId(), None))
        PhantomAgent.tick state 3L
        Expect.equal started 0 "Disabled publication never starts detached IO."
        Expect.equal (PhantomAgent.snapshot state).Subscriptions 0 "No subscriptions."
        PhantomAgent.observe state (PhantomObservation.Departed id)
        Expect.equal (PhantomAgent.snapshot state).Members 0 "Departure cleanup is observed."

    testCase "pose payload owns its constructor copy and exposes only detached writable copies" <| fun _ ->
        let source = PlayerId.create 1UL |> ok
        let incoming = [|1uy;2uy;3uy|]
        let generation = AppearanceGeneration.create 2UL |> ok
        let sequence = PhantomSequence.create 3UL |> ok
        let value = PhantomPose.create options.Limits generation 10UL sequence 0UL (ReadOnlyMemory<byte>(incoming)) |> ok
        incoming[0] <- 99uy
        let detached = value.Payload.ToArray()
        detached[1] <- 88uy
        let encoded = PhantomCodec.encodePoseSample value
        encoded[0] <- 0uy
        let actual = Dreamsleeve.Protocol.Phantom.ServerPosePacket.Parser.ParseFrom((PhantomCodec.encodePose source 1UL value).Bytes)
        Expect.equal actual.Sample.Payload (ByteString.CopyFrom [|1uy;2uy;3uy|]) "Neither input, detached copy nor encoded bytes can mutate the retained value."

    testCase "pose payload span encoding matches generated current and previous samples at wire boundaries" <| fun _ ->
        let wireSample generation context sequence sampledAtUs (payload: byte array) =
            Dreamsleeve.Protocol.Phantom.PoseSample(Generation = generation, ContextRevision = context, Sequence = sequence,
                SampledAtUs = sampledAtUs, Payload = ByteString.CopyFrom payload)
        let varints = [2UL] @ [ for shift in 7 .. 7 .. 63 do let boundary = 1UL <<< shift in yield boundary - 1UL; yield boundary ] @ [UInt64.MaxValue]
        for length in [1;127;128;16383;16384;options.Limits.PoseBytes] do
            let payload = Array.init length (fun index -> byte index)
            let priorPayload = Array.init length (fun index -> byte (index + 1))
            for sampledAt in [0UL;PhantomPose.maximumSampledAtUs] do
                for generation in varints do
                    let context, sequence, revision, player =
                        if generation = 2UL then 1UL, 1UL, 128UL, 1UL
                        else generation, generation, generation, generation
                    let source = PlayerId.create player |> ok
                    let value = PhantomPose.create options.Limits (AppearanceGeneration.create generation |> ok) context
                                    (PhantomSequence.create sequence |> ok) sampledAt (ReadOnlyMemory<byte>(payload)) |> ok
                    let previous = PhantomPose.create options.Limits (AppearanceGeneration.create (generation - 1UL) |> ok) context
                                       (PhantomSequence.create sequence |> ok) sampledAt (ReadOnlyMemory<byte>(priorPayload)) |> ok
                    let wire = wireSample generation context sequence sampledAt payload
                    Expect.equal (PhantomCodec.encodePoseSample value) (wire.ToByteArray()) "Sample bytes follow generated protobuf field/default rules."
                    for paired in [false;true] do
                        let pose = if paired then PhantomPose.withPrevious previous value |> ok else value
                        let expected = Dreamsleeve.Protocol.Phantom.ServerPosePacket(ProtocolVersion = ProtocolCodec.Version,
                                           PlayerId = player, ViewRevision = revision, Sample = wire)
                        if paired then expected.PreviousSample <- wireSample (generation - 1UL) context sequence sampledAt priorPayload
                        let encoded = PhantomCodec.encodePose source revision pose
                        Expect.equal encoded.Bytes (expected.ToByteArray()) "Current and Previous preserve the generated wire contract."
                        Expect.equal encoded.Bytes.Length (PhantomCodec.posePacketSize source revision pose) "Traffic admission uses the exact encoded size."
                    let expectedPrior = ByteString.CopyFrom priorPayload
                    priorPayload[0] <- 77uy
                    let detachedPrior = previous.Payload.ToArray()
                    detachedPrior[0] <- 66uy
                    let paired = PhantomPose.withPrevious previous value |> ok
                    let actual = Dreamsleeve.Protocol.Phantom.ServerPosePacket.Parser.ParseFrom((PhantomCodec.encodePose source revision paired).Bytes)
                    Expect.equal actual.PreviousSample.Payload expectedPrior "Previous retains its constructor-owned payload."
                    priorPayload[0] <- 1uy

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


    testCase "repeated unchanged authority views do not allocate replacements" <| fun _ ->
        let state, members, _ = setup options memoryStorage 2
        let observation = PhantomObservation.View(fst members[1], fst members[0], (snd members[0]).Identity.PlayerId, 1UL, 100.0, None)
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
                        Publish = Dreamsleeve.Protocol.Phantom.Publish())
        Expect.isError (PhantomCodec.decodeAsset options (packet.ToByteArray())) "missing descriptor"
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
        let state = PhantomAgent.create options memoryStorage (PhantomHttp.create options memoryStorage) (fun (_, packet) -> output.Add packet; Ok ())
        let id = Guid.NewGuid()
        PhantomAgent.observe state (PhantomObservation.Member(id, player 1UL 10UL))
        PhantomAgent.activate state id
        PhantomAgent.tick state 1L
        let policy = Dreamsleeve.Protocol.Phantom.ServerAssetPacket.Parser.ParseFrom(output[0].Bytes).Policy
        Expect.equal policy.CompressedAssetBytes (uint32 options.Limits.CompressedBytes) "asset cap"
        Expect.equal policy.CompressedPoseBytes 131072u "pose cap"

    testCase "authenticated source assignment and generation/context admission" <| fun _ ->
        let state, members, output = setup options memoryStorage 1
        PhantomAgent.handle state 2L (Guid.NewGuid()) (PhantomRequest.Publish(asset 1UL [|1uy|], 10UL, requestId(), None))
        Expect.equal (PhantomAgent.snapshot state).Transfers 0 "unauthenticated"
        ready state members[0] (asset 2UL [|1uy|])
        let sent = models output |> Array.pick (fun packet -> if isNull packet.Complete then None else Some packet.Complete)
        Expect.equal sent.PlayerId (PlayerId.value (snd members[0]).Identity.PlayerId) "server source"
        PhantomAgent.handle state 4L (fst members[0]) (PhantomRequest.Publish(asset 1UL [|2uy|], 10UL, requestId(), None))
        PhantomAgent.handle state 4L (fst members[0]) (PhantomRequest.Publish(asset 3UL [|2uy|], 9UL, requestId(), None))
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
        let storage = { memoryStorage with StartDownload = fun _ -> downloads <- downloads + 1; result None }
        let state, members, output = setup options storage 2
        let oldId, oldSource = members[0]
        let observerId, _ = members[1]
        let manifest = asset 1UL [|1uy|]
        ready state members[0] manifest
        view state members[1] members[0] 1UL 1.0
        PhantomAgent.tick state 4L
        Expect.equal (PhantomAgent.snapshot state).Subscriptions 1 "Initial Presence authority."
        PhantomAgent.handle state 5L observerId (PhantomRequest.Download(oldSource.Identity.PlayerId, manifest.Generation, requestId(), None))
        Expect.equal (PhantomAgent.snapshot state).Transfers 1 "Old session download is live."
        output.Clear()
        PhantomAgent.detach state oldId
        Expect.equal (PhantomAgent.snapshot state).Subscriptions 0 "Departure immediately revokes the old view."
        Expect.equal (PhantomAgent.snapshot state).Transfers 0 "Departure cancels old transfers."
        // Runtime can detach before already queued Presence observations drain.
        view state members[1] members[0] 2UL 1.0
        PhantomAgent.observe state (PhantomObservation.Departed oldId)
        let previous = oldSource.Location.Value
        let far = PlayerLocation.create previous.Location (Position.create 20000.0f 0.0f 0.0f |> ok) previous.CameraDirection
        let newId = Guid.NewGuid()
        let newSource = { oldSource with Location = ValueSome far }
        PhantomAgent.observe state (PhantomObservation.Member(newId, newSource))
        PhantomAgent.activate state newId
        PhantomAgent.handle state 6L newId (PhantomRequest.Publish(manifest, newSource.MovementContext, requestId(), None))
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
        PhantomAgent.handle state 9L observerId (PhantomRequest.Download(newSource.Identity.PlayerId, manifest.Generation, requestId(), None))
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
        PhantomAgent.handle state 12L observerId (PhantomRequest.Download(newSource.Identity.PlayerId, manifest.Generation, requestId(), None))
        Expect.equal (PhantomAgent.snapshot state).Transfers 1 "Fresh authorized download still works."
        Expect.equal downloads 2 "Only authorized sessions create read IO."

    testCase "withdraw and movement retain authorized delta basis without retaining visible publication" <| fun _ ->
        let state, members, output = setup options memoryStorage 1
        let first = asset 1UL [|1uy;2uy|]
        ready state members[0] first
        PhantomAgent.handle state 4L (fst members[0]) PhantomRequest.Withdraw
        PhantomAgent.observe state (PhantomObservation.Member(fst members[0], { snd members[0] with MovementContext = 11UL }))
        Expect.equal (PhantomAgent.snapshot state).Sources 0 "No visible publication crosses withdraw"
        let next = asset 2UL [|2uy;3uy|]
        let delta = PhantomDelta.create next first.Hash first.Hash 1u |> ok
        output.Clear()
        PhantomAgent.handle state 5L (fst members[0]) (PhantomRequest.Publish(next,11UL,requestId(),Some delta))
        Expect.equal (PhantomAgent.snapshot state).Transfers 1 "Exact committed base survives movement"
        PhantomAgent.stop state

    testCase "cold download survives replacement until displayed acknowledgement" <| fun _ ->
        let state, members, output = setup options memoryStorage 2
        let first = asset 1UL [|1uy;2uy;3uy;4uy|]
        ready state members[0] first
        view state members[1] members[0] 1UL 1.0
        PhantomAgent.tick state 4L
        let offer = models output |> Array.pick (fun p -> if isNull p.Offer then None else Some p.Offer)
        output.Clear()
        PhantomAgent.handle state 5L (fst members[1]) (PhantomRequest.Download((snd members[0]).Identity.PlayerId, first.Generation, requestId(), None))
        PhantomAgent.tick state 6L
        let downloading = transfer output
        let next = asset 2UL [|2uy;3uy;4uy;5uy|]
        PhantomAgent.handle state 7L (fst members[0]) (PhantomRequest.Publish(next,10UL,requestId(),None))
        PhantomAgent.tick state 8L
        Expect.equal (PhantomAgent.snapshot state).Transfers 1 "Cold transfer is not canceled or replaced"
        Expect.isTrue (models output |> Array.exists (fun p -> not (isNull p.Complete) && p.Complete.Reason = "initial display pending")) "Retryable admission"
        let live = leases.GetValue(state, fun _ -> failwith "fixture missing")
        live[downloading].Advance 4
        live[downloading].Finish(Ok ())
        PhantomAgent.tick state 9L
        PhantomAgent.handle state 10L (fst members[1]) (PhantomRequest.Displayed((snd members[0]).Identity.PlayerId,offer.ViewRevision,first.Generation))
        PhantomAgent.handle state 11L (fst members[0]) (PhantomRequest.Publish(next,10UL,requestId(),None))
        Expect.equal (PhantomAgent.snapshot state).Transfers 1 "Replacement admitted after display"
        PhantomAgent.stop state

    testCase "missing first display acknowledgement cannot indefinitely block publication" <| fun _ ->
        let state, members, output = setup options memoryStorage 2
        let first = asset 1UL [|1uy;2uy|]
        ready state members[0] first
        view state members[1] members[0] 1UL 1.0
        PhantomAgent.tick state 4L
        PhantomAgent.handle state 5L (fst members[1]) (PhantomRequest.Download((snd members[0]).Identity.PlayerId,first.Generation,requestId(),None))
        PhantomAgent.tick state 6L
        let at = 7L + int64 options.TransferTimeoutMs
        PhantomAgent.handle state at (fst members[0]) (PhantomRequest.Publish(asset 2UL [|2uy;3uy|],10UL,requestId(),None))
        Expect.equal (PhantomAgent.snapshot state).Transfers 2 "Replacement admitted after inactivity timeout"
        PhantomAgent.stop state

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
        let pending = TaskCompletionSource<Result<bool,PhantomStorageError>>()
        let state, members, _ = setup options { memoryStorage with StartUpload = fun _ -> pending.Task } 1
        PhantomAgent.handle state 2L (fst members[0]) (PhantomRequest.Publish(asset 1UL [|1uy|], 10UL, requestId(), None))
        PhantomAgent.observe state (PhantomObservation.Member(fst members[0], { snd members[0] with CharacterGeneration = 2UL }))
        pending.SetResult(Ok true)
        PhantomAgent.tick state 3L
        Expect.equal (PhantomAgent.snapshot state).Sources 0 "stale completion"
        PhantomAgent.detach state (fst members[0])
        Expect.equal (PhantomAgent.snapshot state).Members 0 "disconnect"



    testCase "cross-context same-generation publication cannot correlate old starting IO with the new request" <| fun _ ->
        let old = TaskCompletionSource<Result<bool,PhantomStorageError>>()
        let mutable started = 0
        let storage = { memoryStorage with StartUpload = fun _ -> started <- started + 1; if started = 1 then old.Task else result true }
        let state, members, output = setup options storage 1
        let manifest = asset 1UL [|1uy|]
        let first, next = PhantomRequestId.create 1001UL |> ok, PhantomRequestId.create 1002UL |> ok
        PhantomAgent.handle state 2L (fst members[0]) (PhantomRequest.Publish(manifest, 10UL, first, None))
        PhantomAgent.observe state (PhantomObservation.Member(fst members[0], { snd members[0] with MovementContext = 11UL }))
        PhantomAgent.handle state 3L (fst members[0]) (PhantomRequest.Publish(manifest, 11UL, next, None))
        PhantomAgent.tick state 4L
        let complete = models output |> Array.choose (fun packet -> if isNull packet.Complete then None else Some packet.Complete)
        let cancelled = complete |> Array.find (fun value -> value.RequestId = 1001UL)
        Expect.equal (cancelled.TransferId,cancelled.Accepted,cancelled.Generation,cancelled.RetryAfterMs) (0UL,false,1UL,0u) "Only old Awaiting request is rejected."
        let accepted = complete |> Array.find (fun value -> value.RequestId = 1002UL)
        Expect.isTrue accepted.Accepted "New same-generation context publication is independent."
        let assigned = models output |> Array.choose (fun packet -> if isNull packet.Transfer then None else Some packet.Transfer)
        Expect.isEmpty assigned "Cache hit requires no HTTP body or capability."
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
        | PhantomRequest.Download(_,_,request, None) -> Expect.equal request.Value 101UL "Typed request retained."
        | _ -> failtest "Expected download."


    testCase "same immutable generation retries IO and cooldown failures; conflicting and lower generations are terminal" <| fun _ ->
        let mutable attempts = 0
        let storage = { memoryStorage with StartUpload = fun _ -> attempts <- attempts + 1; if attempts = 1 then Task.FromResult(Error (PhantomStorageError.Io (IOException "storage busy"))) else result true }
        let state, members, output = setup { options with PublishCooldownMs = 100 } storage 1
        let manifest = asset 2UL [|1uy|]
        let publish at value context = PhantomAgent.handle state at (fst members[0]) (PhantomRequest.Publish(value, context, requestId(), None)); PhantomAgent.tick state (at + 1L)
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
        Expect.equal accepted.TransferId 0UL "Cache hit completes the correlated request without HTTP."
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
        let pending = TaskCompletionSource<Result<bool,PhantomStorageError>>()
        let mutable attempts = 0
        let storage = { memoryStorage with StartUpload = fun _ -> attempts <- attempts + 1; if attempts = 1 then pending.Task else result true }
        let state, members, output = setup { options with MaxTransfers = 1; PublishCooldownMs = 10 } storage 2
        let manifest = asset 1UL [|1uy|]
        PhantomAgent.handle state 2L (fst members[0]) (PhantomRequest.Publish(manifest, 10UL, requestId(), None))
        PhantomAgent.handle state 2L (fst members[1]) (PhantomRequest.Publish(manifest, 10UL, requestId(), None))
        PhantomAgent.tick state 3L
        let busy = models output |> Array.pick (fun packet -> if isNull packet.Complete then None else Some packet.Complete)
        Expect.equal (busy.TransferId,busy.PlayerId,busy.Generation,busy.Upload,busy.RetryAfterMs) (0UL,2UL,1UL,true,10u) "Request-level upload capacity correlation."
        pending.SetResult(Error (PhantomStorageError.Io (IOException "storage busy")))
        PhantomAgent.tick state 4L
        PhantomAgent.handle state 12L (fst members[1]) (PhantomRequest.Publish(manifest, 10UL, requestId(), None))
        PhantomAgent.tick state 13L
        Expect.equal (PhantomAgent.snapshot state).Sources 1 "Unchanged generation retries after capacity becomes available."
        output.Clear()
        PhantomAgent.handle state 14L (fst members[0]) (PhantomRequest.Download((snd members[1]).Identity.PlayerId, manifest.Generation, requestId(), None))
        PhantomAgent.tick state 15L
        let download = models output |> Array.pick (fun packet -> if isNull packet.Complete then None else Some packet.Complete)
        Expect.equal (download.TransferId,download.PlayerId,download.Generation,download.Upload,download.RetryAfterMs) (0UL,2UL,1UL,false,1000u) "Download denial can leave client's requested phase."

    testCase "starting IO timeout without a Transfer response remains request-correlated" <| fun _ ->
        let storage = { memoryStorage with StartUpload = fun _ -> TaskCompletionSource<Result<bool,PhantomStorageError>>().Task }
        let state, members, output = setup { options with TransferTimeoutMs = 2; PublishCooldownMs = 10 } storage 1
        PhantomAgent.handle state 2L (fst members[0]) (PhantomRequest.Publish(asset 1UL [|1uy|], 10UL, requestId(), None))
        PhantomAgent.tick state 5L
        let complete = models output |> Array.pick (fun packet -> if isNull packet.Complete then None else Some packet.Complete)
        Expect.equal (complete.TransferId,complete.PlayerId,complete.Generation,complete.Upload,complete.RetryAfterMs) (0UL,1UL,1UL,true,10u) "Starting timeout is retryable without unknown transfer correlation."

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
        let pending = TaskCompletionSource<Result<bool,PhantomStorageError>>()
        let storage = { memoryStorage with StartUpload = fun (_, manifest, _) -> if manifest.Generation.Value = 2UL then pending.Task else result true }
        let state, members, _ = setup { options with MaxSources = 2 } storage 2
        ready state members[0] (asset 1UL [|1uy|])
        PhantomAgent.handle state 4L (fst members[0]) (PhantomRequest.Publish(asset 2UL [|2uy|], 10UL, requestId(), None))
        PhantomAgent.handle state 5L (fst members[1]) (PhantomRequest.Publish(asset 1UL [|3uy|], 10UL, requestId(), None))
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
            PhantomAgent.handle state (at + 1L) (fst members[1]) (PhantomRequest.Download((snd members[0]).Identity.PlayerId, manifest.Generation, requestId(), None))
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
    testCase "jitter around the replication grid does not halve a ten Hz source" <| fun _ ->
        let config = { options with ReplicationIntervalMs = 100; PoseIntervalMs = 100 }
        let state, members, output = setup config memoryStorage 2
        ready state members[0] (asset 1UL [|1uy|])
        view state members[1] members[0] 1UL 1.0
        PhantomAgent.tick state 100L
        output.Clear()
        let arrivals = [199L;301L;399L;501L;599L;701L;799L;901L]
        let mutable sequence = 0UL
        for at in 101L .. 950L do
            if List.contains at arrivals then
                sequence <- sequence + 1UL
                PhantomAgent.receive state at (fst members[0]) DeliveryLane.Poses (pose 1UL sequence 10UL)
            PhantomAgent.tick state at
        let delivered = output |> Seq.choose (fun (_, packet) ->
            if packet.Lane = DeliveryLane.Poses then Some(Dreamsleeve.Protocol.Phantom.ServerPosePacket.Parser.ParseFrom(packet.Bytes).Sample.Sequence) else None) |> Seq.toList
        Expect.equal delivered [1UL..8UL] "Independent 10 Hz clocks with 2 ms jitter must not discard every second full snapshot."

    testCase "small dispatch turns preserve source rate and shared fanout credit" <| fun _ ->
        let config = { options with ReplicationIntervalMs = 100; PoseIntervalMs = 1; MaxPoseFanoutPerTick = 1 }
        let state, members, output = setup config memoryStorage 3
        ready state members[0] (asset 1UL [|1uy|])
        for observer in [members[1];members[2]] do view state observer members[0] 1UL 1.0
        PhantomAgent.tick state 100L
        output.Clear()
        for at in 101L .. 1100L do
            PhantomAgent.receive state at (fst members[0]) DeliveryLane.Poses (pose 1UL (uint64 at) 10UL)
            PhantomAgent.tick state at
            let count = output |> Seq.filter (fun (_, packet) -> packet.Lane = DeliveryLane.Poses) |> Seq.length
            Expect.isLessThanOrEqual count (1 + int ((at - 100L) / 100L)) "Four times as many turns do not multiply the shared operation rate."
        let recipients = output |> Seq.choose(fun (id, packet) -> if packet.Lane = DeliveryLane.Poses then Some id else None) |> Set.ofSeq
        Expect.equal recipients.Count 2 "Both observers progress under sustained overload."

    testCase "source replication rate stays bounded when ingress and dispatch are faster" <| fun _ ->
        let config = { options with ReplicationIntervalMs = 100; PoseIntervalMs = 1 }
        let state, members, output = setup config memoryStorage 2
        ready state members[0] (asset 1UL [|1uy|])
        view state members[1] members[0] 1UL 1.0
        PhantomAgent.tick state 100L
        output.Clear()
        for at in 101L .. 1100L do
            PhantomAgent.receive state at (fst members[0]) DeliveryLane.Poses (pose 1UL (uint64 at) 10UL)
            PhantomAgent.tick state at
        let count = output |> Seq.filter(fun (_, packet) -> packet.Lane = DeliveryLane.Poses) |> Seq.length
        Expect.isLessThanOrEqual count 12 "10 Hz plus at most two initial jitter tokens."
        Expect.isGreaterThanOrEqual count 10 "Fast dispatch still serves the configured source rate."

    testCase "partial fanout admits a snapshot once across transient peer pressure" <| fun _ ->
        let config = { options with ReplicationIntervalMs = 100; PoseIntervalMs = 100 }
        let delivered = ResizeArray<Guid>()
        let mutable sentThisTurn = false
        let state = PhantomAgent.create config memoryStorage (PhantomHttp.create config memoryStorage) (fun (id, packet) ->
            if packet.Lane <> DeliveryLane.Poses then Ok ()
            elif sentThisTurn then Error(TransportSendError.BudgetExceeded "peer budget")
            else sentThisTurn <- true; delivered.Add id; Ok ())
        let members = Array.init 5 (fun index -> Guid.NewGuid(), player (uint64 index + 1UL) 10UL)
        for id, value in members do
            PhantomAgent.observe state (PhantomObservation.Member(id, value))
            PhantomAgent.activate state id
        ready state members[0] (asset 1UL [|1uy|])
        for observer in members[1..] do view state observer members[0] 1UL 1.0
        PhantomAgent.tick state 100L
        PhantomAgent.receive state 101L (fst members[0]) DeliveryLane.Poses (pose 1UL 1UL 10UL)
        for at in [125L;150L;175L;200L] do
            sentThisTurn <- false
            PhantomAgent.tick state at
        Expect.equal (Set.ofSeq delivered).Count 4 "All portions of one admitted snapshot finish without paying the source rate token again."

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
        let pending = TaskCompletionSource<Result<bool,PhantomStorageError>>()
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
        let visible = models output |> Array.pick (fun p -> if isNull p.Offer then None else Some p.Offer)
        PhantomAgent.handle state 5L observerId (PhantomRequest.Displayed((snd members[0]).Identity.PlayerId,visible.ViewRevision,AppearanceGeneration.create 1UL |> ok))
        output.Clear()
        PhantomAgent.handle state 6L observerId (PhantomRequest.Download((snd members[0]).Identity.PlayerId, AppearanceGeneration.create 1UL |> ok, requestId(), None))
        PhantomAgent.tick state 6L
        let oldDownload = transfer output
        output.Clear()
        nextUpload <- pending.Task
        PhantomAgent.handle state 7L sourceId (PhantomRequest.Publish(asset 2UL [|2uy|], 10UL, requestId(), None))
        PhantomAgent.tick state 7L
        let snapshot = PhantomAgent.snapshot state
        Expect.equal snapshot.Sources 1 "Previous publication remains available."
        Expect.equal snapshot.Subscriptions 1 "No appearance-induced AOI departure."
        Expect.equal snapshot.Transfers 2 "Previous model download continues while upload is pending."
        Expect.equal snapshot.LatestPoses 1 "Current pose remains usable."
        PhantomAgent.receive state 8L sourceId DeliveryLane.Poses (pose 1UL 9UL 10UL)
        PhantomAgent.tick state 8L
        Expect.isFalse (output |> Seq.exists (fun (_, packet) -> packet.Lane = DeliveryLane.Poses)) "Pending upload does not reset the old sequence floor."
        pending.SetResult(Error (PhantomStorageError.Io (IOException "temporary IO failure")))
        PhantomAgent.tick state 9L
        Expect.equal (PhantomAgent.snapshot state).Sources 1 "Failed replacement retains publication."
        Expect.isFalse (models output |> Array.exists (fun packet -> not (isNull packet.Remove))) "No removal while pending or after failure."
        Expect.isFalse (models output |> Array.exists (fun packet -> not (isNull packet.Complete) && packet.Complete.TransferId = oldDownload.Value)) "Old download is not cancelled by failure."
        output.Clear()
        nextUpload <- result true
        PhantomAgent.handle state 10L sourceId (PhantomRequest.Publish(asset 2UL [|2uy|], 10UL, requestId(), None))
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

    testCase "model bridge follows advancing download progress and grants load grace" <| fun _ ->
        let state, members, output = setup options memoryStorage 2
        ready state members[0] (asset 1UL [|1uy..8uy|])
        view state members[1] members[0] 1UL 1.0
        PhantomAgent.tick state 4L
        PhantomAgent.handle state 5L (fst members[0]) (PhantomRequest.Publish(asset 2UL [|1uy..8uy|], 10UL, requestId(), None))
        PhantomAgent.tick state 6L
        output.Clear()
        PhantomAgent.handle state 7L (fst members[1]) (PhantomRequest.Download((snd members[0]).Identity.PlayerId, AppearanceGeneration.create 2UL |> ok, requestId(), None))
        for at in 7L .. 10L do PhantomAgent.tick state at
        let id = transfer output
        output.Clear()
        advance state id 4 29000L
        PhantomAgent.tick state 31000L
        Expect.isFalse (models output |> Array.exists (fun p -> not (isNull p.Settled))) "Active download outlives the original commit timeout."
        advance state id 8 58000L
        PhantomAgent.tick state 58000L
        Expect.isFalse (models output |> Array.exists (fun p -> not (isNull p.Settled))) "Final ACK begins native load grace."
        advance state id 8 87999L
        PhantomAgent.tick state 87999L
        Expect.isFalse (models output |> Array.exists (fun p -> not (isNull p.Settled))) "Load grace remains valid."
        PhantomAgent.tick state 88000L
        Expect.isTrue (models output |> Array.exists (fun p -> not (isNull p.Settled))) "Duplicate ACK cannot extend grace forever."

    testCase "restarted downloads cannot retain bridge by acknowledging the same prefix" <| fun _ ->
        let state, members, output = setup options memoryStorage 2
        ready state members[0] (asset 1UL [|1uy..8uy|])
        view state members[1] members[0] 1UL 1.0
        PhantomAgent.tick state 4L
        PhantomAgent.handle state 5L (fst members[0]) (PhantomRequest.Publish(asset 2UL [|1uy..8uy|], 10UL, requestId(), None))
        PhantomAgent.tick state 6L
        let download at =
            output.Clear()
            PhantomAgent.handle state at (fst members[1]) (PhantomRequest.Download((snd members[0]).Identity.PlayerId, AppearanceGeneration.create 2UL |> ok, requestId(), None))
            for tick in at .. at + 3L do PhantomAgent.tick state tick
            transfer output
        let first = download 7L
        advance state first 4 1000L
        let second = download 20000L
        Expect.notEqual second first "New request starts a new transfer."
        advance state second 4 29000L
        PhantomAgent.tick state 30999L
        Expect.isFalse (models output |> Array.exists (fun p -> not (isNull p.Settled))) "Original unique prefix still has grace."
        PhantomAgent.tick state 31000L
        Expect.isTrue (models output |> Array.exists (fun p -> not (isNull p.Settled))) "Retry of the same prefix cannot prolong bridge."

    testCase "late offer gets its own display timeout" <| fun _ ->
        let state, members, output = setup options memoryStorage 3
        ready state members[0] (asset 1UL [|1uy|])
        view state members[1] members[0] 1UL 1.0
        PhantomAgent.tick state 4L
        PhantomAgent.handle state 10L (fst members[0]) (PhantomRequest.Publish(asset 2UL [|2uy|], 10UL, requestId(), None))
        PhantomAgent.tick state 11L
        let offer = models output |> Array.pick (fun p -> if not (isNull p.Offer) && p.Offer.Asset.Generation = 2UL then Some p.Offer else None)
        view state members[2] members[0] 1UL 1.0
        PhantomAgent.tick state 29000L
        output.Clear()
        PhantomAgent.handle state 30020L (fst members[1]) (PhantomRequest.Displayed((snd members[0]).Identity.PlayerId, offer.ViewRevision, AppearanceGeneration.create 2UL |> ok))
        PhantomAgent.tick state 30020L
        Expect.isFalse (models output |> Array.exists (fun p -> not (isNull p.Settled))) "Late receiver has not exhausted its own grace."
        PhantomAgent.tick state 59001L
        Expect.isTrue (models output |> Array.exists (fun p -> not (isNull p.Settled))) "Inactive late receiver eventually expires."



    testCase "replacement bundles keep the old generation moving until display acknowledgement" <| fun _ ->
        let pending = TaskCompletionSource<Result<bool,PhantomStorageError>>()
        let mutable next = result true
        let state, members, output = setup options { memoryStorage with StartUpload = fun _ -> next } 2
        ready state members[0] (asset 1UL [|1uy|])
        view state members[1] members[0] 1UL 1.0
        PhantomAgent.tick state 4L
        next <- pending.Task
        PhantomAgent.handle state 5L (fst members[0]) (PhantomRequest.Publish(asset 2UL [|2uy|], 10UL, requestId(), None))
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
        PhantomAgent.handle state 8L (fst members[0]) (PhantomRequest.Publish(asset 3UL [|3uy|], 10UL, requestId(), None))
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
        let pending = TaskCompletionSource<Result<bool,PhantomStorageError>>()
        let mutable next = result true
        let state, members, output = setup options { memoryStorage with StartUpload = fun _ -> next } 2
        ready state members[0] (asset 1UL [|1uy|])
        view state members[1] members[0] 1UL 1.0
        PhantomAgent.tick state 4L
        next <- pending.Task
        let id = fst members[0]
        PhantomAgent.handle state 5L id (PhantomRequest.Publish(asset 2UL [|2uy|], 10UL, requestId(), None))
        let packet = Dreamsleeve.Protocol.Phantom.ClientPosePacket.Parser.ParseFrom(pose 2UL 1UL 10UL)
        packet.PreviousSample <- Dreamsleeve.Protocol.Phantom.ClientPosePacket.Parser.ParseFrom(pose 1UL 20UL 10UL).Sample
        packet.PreviousSample.SampledAtUs <- packet.Sample.SampledAtUs
        PhantomAgent.receive state 6L id DeliveryLane.Poses (packet.ToByteArray())
        PhantomAgent.tick state 6L
        pending.SetResult(Error PhantomStorageError.HashMismatch)
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
        PhantomAgent.handle state 6L (fst members[0]) (PhantomRequest.Publish(manifest, 10UL, requestId(), None))
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
        let state, members, _ = setup config { memoryStorage with StartUpload = fun _ -> TaskCompletionSource<Result<bool,PhantomStorageError>>().Task } 3
        Expect.equal (PhantomAgent.snapshot state).Members 3 "receivers remain admitted"
        for id, _ in members do PhantomAgent.handle state 2L id (PhantomRequest.Publish(asset 1UL [|1uy|], 10UL, requestId(), None))
        Expect.equal (PhantomAgent.snapshot state).Transfers 1 "concurrency"
        PhantomAgent.tick state 5L
        Expect.equal (PhantomAgent.snapshot state).Transfers 0 "expiry"

    storageCase "cold HTTP body round trip and warm hash reuse" (fun root config _ -> task {
        let config = { config with StoragePath = Path.Combine(root, "streaming"); Limits = PhantomOptions.defaults.Limits
                                   DiskBytes = 32L * 1024L * 1024L; RamBytes = 0L; ChunkBytes = 16384 }
        let storage = PhantomStorage.create config
        let http = PhantomHttp.create config storage
        try
            let bytes = Array.init (1024 * 1024 + 7) (fun index -> byte (index % 251))
            let manifest = asset 1UL bytes
            let uploadId, downloadId = PhantomTransferId 1UL, PhantomTransferId 2UL
            let! initial = storage.StartUpload(uploadId, manifest, None)
            Expect.equal initial (Ok false) "Cold file."
            let upload = http.Admit(Guid.NewGuid(), uploadId, manifest, true, None)
            use input = new MemoryStream(bytes, false)
            let! written = http.Serve { Token = upload.Token; Upload = true; Length = Some(int64 bytes.Length); Body = input
                                        BeginResponse = ignore; Cancellation = Threading.CancellationToken.None }
            Expect.equal written (Ok ()) "Complete verified upload."
            Expect.equal upload.Progress bytes.Length "Monotonic full progress."
            Expect.equal (File.ReadAllBytes(Path.Combine(config.StoragePath, manifest.Hash.Hex + ".zst"))) bytes "Exact file."
            let! _ = storage.StartDownload(downloadId, manifest, None)
            let download = http.Admit(Guid.NewGuid(), downloadId, manifest, false, None)
            use output = new MemoryStream()
            let! read = http.Serve { Token = download.Token; Upload = false; Length = None; Body = output
                                     BeginResponse = (fun size -> Expect.equal size bytes.Length "HTTP content length")
                                     Cancellation = Threading.CancellationToken.None }
            Expect.equal read (Ok ()) "Download completed."
            Expect.equal (output.ToArray()) bytes "Exact round trip."
            let! warm = storage.StartUpload(PhantomTransferId 3UL, asset 2UL bytes, None)
            Expect.equal warm (Ok true) "Same hash needs no second upload."
        finally
            http.Dispose().GetAwaiter().GetResult()
            storage.Dispose().GetAwaiter().GetResult()
    })
    storageCase "streaming SHA256 atomic completion and restart deduplication" (fun root config storage -> task {
        let bytes = [|1uy;2uy;3uy;4uy;5uy;6uy;7uy;8uy|]
        let manifest = asset 1UL bytes
        let! started = storage.StartUpload(PhantomTransferId 1UL, manifest, None)
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
            let! cached = restarted.StartUpload(PhantomTransferId 2UL, manifest, None)
            Expect.equal (ok cached) true "restart cache verified"
        finally restarted.Dispose().GetAwaiter().GetResult()
    })

    storageCase "cache file lost after initialization can be uploaded again" (fun root config _ -> task {
        Directory.CreateDirectory root |> ignore
        let content = [|1uy;2uy;3uy;4uy|]
        let manifest = asset 1UL content
        let path = Path.Combine(root, manifest.Hash.Hex + ".zst")
        File.WriteAllBytes(path, content)
        let store = PhantomStorage.create config
        try
            let! _ = store.StartDownload(PhantomTransferId 1UL, asset 2UL [|5uy|], None)
            File.Delete path
            let! lost = store.StartDownload(PhantomTransferId 2UL, manifest, None)
            Expect.isError lost "Cache disappeared before its first verification."
            let! retry = store.StartUpload(PhantomTransferId 3UL, manifest, None)
            Expect.equal retry (Ok false) "The stale entry was evicted; publication can recover."
            let! completed = store.WriteChunk(PhantomTransferId 3UL, 0, content)
            Expect.equal completed (Ok true) "Recovered cache content."
        finally store.Dispose().GetAwaiter().GetResult()
    })

    testSequenced (storageCase "expected storage failures return errors without first-chance exceptions" (fun _ _ storage -> task {
        let raised = System.Collections.Concurrent.ConcurrentQueue<string>()
        let handler = EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs>(fun _ args ->
            let trace = args.Exception.StackTrace
            if not (isNull trace) && trace.Contains("PhantomStorage") then raised.Enqueue args.Exception.Message)
        AppDomain.CurrentDomain.FirstChanceException.AddHandler handler
        try
            let manifest = asset 1UL [|1uy;2uy;3uy;4uy|]
            let! missing = storage.StartDownload(PhantomTransferId 1UL, manifest, None)
            Expect.isError missing "Missing cache entry."
            let! started = storage.StartUpload(PhantomTransferId 2UL, manifest, None)
            Expect.equal started (Ok false) "Cold upload."
            let! duplicate = storage.StartUpload(PhantomTransferId 2UL, manifest, None)
            Expect.isError duplicate "Duplicate transfer."
            let! offset = storage.WriteChunk(PhantomTransferId 2UL, 1, [|1uy|])
            Expect.isError offset "Bad offset releases reservation."
            let! restarted = storage.StartUpload(PhantomTransferId 3UL, manifest, None)
            Expect.equal restarted (Ok false) "Admission refunded."
            let! hash = storage.WriteChunk(PhantomTransferId 3UL, 0, [|4uy;3uy;2uy;1uy|])
            Expect.isError hash "Wrong hash."
            let! unknown = readChunk storage (PhantomTransferId 99UL, 0, 4)
            Expect.isError unknown "Unknown download."
            Expect.isEmpty (raised.ToArray()) "Expected failures must not throw and catch internally."
        finally AppDomain.CurrentDomain.FirstChanceException.RemoveHandler handler
    }))

    storageCase "hash and offset failures delete temporary files and refund admission" (fun root _ storage -> task {
        let manifest = asset 1UL [|1uy;2uy;3uy;4uy|]
        let! _ = storage.StartUpload(PhantomTransferId 1UL, manifest, None)
        let! corrupt = storage.WriteChunk(PhantomTransferId 1UL, 0, [|4uy;3uy;2uy;1uy|])
        Expect.isError corrupt "hash mismatch"
        Expect.equal (Directory.GetFiles(root, "*.tmp").Length) 0 "cleanup"
        let! retry = storage.StartUpload(PhantomTransferId 2UL, manifest, None)
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
            let! _ = store.StartUpload(PhantomTransferId 1UL, first, None)
            let! stored = store.WriteChunk(PhantomTransferId 1UL, 0, [|1uy;2uy;3uy;4uy|])
            Expect.isOk stored "stored"
            let! opened = store.StartDownload(PhantomTransferId 2UL, first, None)
            Expect.isOk opened "pinned"
            let! refused = store.StartUpload(PhantomTransferId 3UL, second, None)
            Expect.isError refused "quota protects active read"
            let! bytes = readChunk store (PhantomTransferId 2UL, 0, 4)
            Expect.equal (ok bytes) [|1uy;2uy;3uy;4uy|] "RAM read"
            do! store.Cancel(PhantomTransferId 2UL)
            let! admitted = store.StartUpload(PhantomTransferId 4UL, second, None)
            Expect.equal (ok admitted) false "unpin enables LRU"
        finally store.Dispose().GetAwaiter().GetResult()
    })

    storageCase "multiple read pins count as one cached file for entry admission" (fun root config _ -> task {
        let store = PhantomStorage.create { config with StoragePath = Path.Combine(root, "pin-quota"); DiskBytes = 8L; CacheEntries = 2 }
        try
            let first, second = asset 1UL [|1uy;2uy;3uy;4uy|], asset 2UL [|4uy;3uy;2uy;1uy|]
            let! _ = store.StartUpload(PhantomTransferId 1UL, first, None)
            let! _ = store.WriteChunk(PhantomTransferId 1UL, 0, [|1uy;2uy;3uy;4uy|])
            let! _ = store.StartDownload(PhantomTransferId 2UL, first, None)
            let! _ = store.StartDownload(PhantomTransferId 3UL, first, None)
            let! admitted = store.StartUpload(PhantomTransferId 4UL, second, None)
            Expect.equal (ok admitted) false "Read leases do not consume extra file-entry quota."
            let! full = store.WriteChunk(PhantomTransferId 4UL, 0, [|4uy;3uy;2uy;1uy|])
            Expect.equal (ok full) true "Two files fit exact disk/entry quota while old file remains pinned twice."
        finally store.Dispose().GetAwaiter().GetResult()
    })
    storageCase "RAM fill rejects changed file length before allocating descriptor-sized cache" (fun root _ storage -> task {
        let bytes = [|1uy;2uy;3uy;4uy|]
        let manifest = asset 1UL bytes
        let! _ = storage.StartUpload(PhantomTransferId 1UL, manifest, None)
        let! _ = storage.WriteChunk(PhantomTransferId 1UL, 0, bytes)
        let! _ = storage.StartDownload(PhantomTransferId 2UL, manifest, None)
        let path = Path.Combine(root, manifest.Hash.Hex + ".zst")
        do! storage.Cancel(PhantomTransferId 2UL)
        File.WriteAllBytes(path, Array.zeroCreate 32)
        let! _ = storage.StartDownload(PhantomTransferId 2UL, manifest, None)
        let! grown = readChunk storage (PhantomTransferId 2UL, 0, 4)
        Expect.isError grown "Warm verified entry cannot allocate an unexpectedly grown file."
        do! storage.Cancel(PhantomTransferId 2UL)
        File.WriteAllBytes(path, bytes)
        let! _ = storage.StartDownload(PhantomTransferId 2UL, manifest, None)
        let! recovered = readChunk storage (PhantomTransferId 2UL, 0, 4)
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
            let! corrupt = store.StartDownload(PhantomTransferId 1UL, manifest, None)
            Expect.isError corrupt "integrity on restart"
            Expect.equal (Directory.GetFiles(root, "*.tmp").Length) 0 "partial cleanup"
            Expect.equal (Directory.GetFiles(root, "*.zst").Length) 0 "corruption removed"
        finally store.Dispose().GetAwaiter().GetResult()
    })
    testCase "typed storage failures preserve Complete wire reasons and retry values" <| fun _ ->
        let cases = [
            PhantomStorageError.HashMismatch, "hash mismatch", 0u
            PhantomStorageError.ChunkOffsetOrSize, "chunk offset/size", 0u
            PhantomStorageError.ReadBounds, "read bounds", 0u
            PhantomStorageError.CacheHashMismatch, "cache hash mismatch", 27u
            PhantomStorageError.DeltaHashMismatch, "delta hash mismatch", 27u
            PhantomStorageError.DeltaBaseUnavailable, "delta base unavailable", 27u
            PhantomStorageError.QueueFull, "storage queue full", 27u
            PhantomStorageError.Io (IOException(String('x', 257))), "storage failure", 27u
        ]
        for error, reason, retry in cases do
            let store = { memoryStorage with StartUpload = fun _ -> Task.FromResult(Error error) }
            let state, members, output = setup { options with PublishCooldownMs = 27 } store 1
            let request = requestId()
            PhantomAgent.handle state 0L (fst members[0]) (PhantomRequest.Publish(asset 1UL [|1uy|], 10UL, request, None))
            PhantomAgent.tick state 1L
            let completed = models output |> Array.find (fun packet -> not (isNull packet.Complete)) |> _.Complete
            Expect.equal (completed.TransferId, completed.RequestId, completed.Reason, completed.RetryAfterMs) (0UL, request.Value, reason, retry) "Protocol unchanged across typed outcome."
            Expect.isNone (PhantomAgent.failure state) "Expected rejection is not owner fault."

    case "cleanup retains transfer capacity until acknowledged and faults are not discarded" (fun () -> task {
        let cleanup = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let started = TaskCompletionSource<Result<bool,PhantomStorageError>>(TaskCreationOptions.RunContinuationsAsynchronously)
        let store = { memoryStorage with StartUpload = (fun _ -> started.Task); Cancel = (fun _ -> cleanup.Task) }
        let state, members, output = setup { options with MaxTransfers = 1 } store 1
        let publish request = PhantomAgent.handle state 0L (fst members[0]) (PhantomRequest.Publish(asset 1UL [|1uy|], 10UL, request, None))
        publish (requestId())
        started.SetResult(Error PhantomStorageError.DiskQuota)
        PhantomAgent.tick state 1L
        let second = requestId()
        publish second
        PhantomAgent.tick state 2L
        let denied = models output |> Array.find (fun packet -> not (isNull packet.Complete) && packet.Complete.RequestId = second.Value) |> _.Complete
        Expect.equal denied.Reason "transfer limit" "Removal isn't completed cleanup."
        let original = InvalidOperationException "cleanup failed"
        cleanup.SetException original
        // Wait on observable release work without relying on timing luck.
        let until = DateTime.UtcNow + TimeSpan.FromSeconds 2.
        while (PhantomAgent.failure state).IsNone && DateTime.UtcNow < until do do! Task.Delay 1
        Expect.isTrue (PhantomAgent.failure state |> Option.exists (fun error -> Object.ReferenceEquals(error, original))) "Lifecycle failure retained."
        PhantomAgent.tick state 3L
        Expect.isSome (PhantomAgent.failure state) "Tick never discards failed cleanup."
    })

    testCase "faulted starting work cannot publish success or become a normal retry" <| fun _ ->
        let original = InvalidOperationException "worker internal fault"
        let state, members, output = setup options { memoryStorage with StartUpload = fun _ -> Task.FromException<Result<bool,PhantomStorageError>>(original) } 1
        PhantomAgent.handle state 0L (fst members[0]) (PhantomRequest.Publish(asset 1UL [|1uy|], 10UL, requestId(), None))
        PhantomAgent.tick state 1L
        Expect.equal (PhantomAgent.snapshot state).Sources 0 "No false Ready."
        Expect.isTrue (models output |> Array.forall (fun packet -> isNull packet.Complete)) "No fabricated retryable Complete."
        Expect.isTrue (PhantomAgent.failure state |> Option.exists (fun error -> Object.ReferenceEquals(error, original))) "Fault reaches runtime owner."

    storageCase "duplicate final chunks and cancellation refund each lease exactly once" (fun root config _ -> task {
        let store = PhantomStorage.create { config with StoragePath = Path.Combine(root, "once"); DiskBytes = 4L; CacheEntries = 2; MaxTransfers = 1 }
        try
            let bytes = [|1uy;2uy;3uy;4uy|]
            let value = asset 1UL bytes
            let id = PhantomTransferId 1UL
            let! _ = store.StartUpload(id, value, None)
            let! outcomes = Task.WhenAll [|store.WriteChunk(id, 0, bytes); store.WriteChunk(id, 0, bytes)|]
            Expect.equal (outcomes |> Array.filter ((=) (Ok true)) |> Array.length) 1 "Only one canonical completion."
            Expect.equal (outcomes |> Array.filter Result.isError |> Array.length) 1 "Late chunk cannot complete twice."
            let! _ = Task.WhenAll [|store.Cancel id; store.Cancel id|]
            let! _ = store.StartDownload(id, value, None)
            let! _ = Task.WhenAll [|store.Cancel id; store.Cancel id|]
            let! admitted = store.StartUpload(id, asset 2UL [|4uy;3uy;2uy;1uy|], None)
            Expect.equal admitted (Ok false) "Exact quota admission proves no leaked pin/reservation."
        finally store.Dispose().GetAwaiter().GetResult()
    })

    storageCase "canonical rename IO failure is not success and refunds temporary ownership" (fun root _ store -> task {
        let bytes = [|1uy;2uy;3uy;4uy|]
        let value = asset 1UL bytes
        let id = PhantomTransferId 1UL
        let! _ = store.StartUpload(id, value, None)
        let path = Path.Combine(root, value.Hash.Hex + ".zst")
        Directory.CreateDirectory path |> ignore
        let! failed = store.WriteChunk(id, 0, bytes)
        match failed with Error (PhantomStorageError.Io _) -> () | other -> failtestf "Expected rename IO error, got %A" other
        Expect.equal (Directory.GetFiles(root, "*.tmp").Length) 0 "Partial ownership removed."
        Expect.isFalse store.OwnerFailure.IsCompleted "Fully rolled-back expected IO keeps store usable."
        Directory.Delete path
        let! admitted = store.StartUpload(id, value, None)
        Expect.equal admitted (Ok false) "No false canonical cache hit."
        let! completed = store.WriteChunk(id, 0, bytes)
        Expect.equal completed (Ok true) "Retry of whole manifest can commit."
    })

    storageCase "unexpected detached read fault closes admission and disposal releases active resources" (fun root config _ -> task {
        let store = PhantomStorage.create { config with StoragePath = Path.Combine(root, "faulted") }
        let bytes = [|1uy;2uy;3uy;4uy|]
        let value = asset 1UL bytes
        let! _ = store.StartUpload(PhantomTransferId 1UL, value, None)
        let! _ = store.WriteChunk(PhantomTransferId 1UL, 0, bytes)
        let! _ = store.StartDownload(PhantomTransferId 2UL, value, None)
        let! _ = store.StartUpload(PhantomTransferId 3UL, asset 2UL [|5uy;6uy;7uy;8uy|], None)
        let original = InvalidOperationException "borrowed destination fault"
        use destination = new FaultingDestination(original)
        let mutable seen = None
        try let! _ = store.ReadChunk(PhantomTransferId 2UL, 0, destination.Memory) in ()
        with error -> seen <- Some error
        Expect.isTrue (seen |> Option.exists (fun error -> Object.ReferenceEquals(error, original))) "Fault is not fabricated read absence."
        let! failed = store.OwnerFailure.WaitAsync(TimeSpan.FromSeconds 2.)
        Expect.isTrue (Object.ReferenceEquals(failed, original)) "Owner failure signal."
        let mutable refused = false
        try let! _ = store.StartUpload(PhantomTransferId 4UL, value, None) in ()
        with error -> refused <- Object.ReferenceEquals(error, original)
        Expect.isTrue refused "Faulted owner never executes new business work."
        let mutable disposed = false
        try do! (store.Dispose()).WaitAsync(TimeSpan.FromSeconds 2.)
        with error -> disposed <- Object.ReferenceEquals(error, original)
        Expect.isTrue disposed "Disposal preserves fault after releasing resources."
        Expect.equal (Directory.GetFiles(Path.Combine(root, "faulted"), "*.tmp").Length) 0 "Active upload temp released."
        use exclusive = new FileStream(Path.Combine(root, "faulted", value.Hash.Hex + ".zst"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)
        Expect.equal exclusive.Length 4L "Download handle released."
    })

    storageCase "cleanup failure after canonical commit is an owner fault, never a retryable rejection" (fun root config _ -> task {
        let directory = Path.Combine(root, "committed-fault")
        let store = PhantomStorage.create { config with StoragePath = directory }
        let bytes = [|1uy;2uy;3uy;4uy|]
        let value = asset 1UL bytes
        let! _ = store.StartUpload(PhantomTransferId 1UL, value, None)
        let temporary = Directory.GetFiles(directory, "*.tmp") |> Array.exactlyOne
        let obstruction = temporary + ".delta"
        Directory.CreateDirectory obstruction |> ignore
        let mutable commandFault = None
        try let! _ = store.WriteChunk(PhantomTransferId 1UL, 0, bytes) in ()
        with error -> commandFault <- Some error
        Expect.isSome commandFault "No expected error or success is fabricated after failed cleanup."
        let! original = store.OwnerFailure.WaitAsync(TimeSpan.FromSeconds 2.)
        Expect.isTrue (commandFault |> Option.exists (fun error -> Object.ReferenceEquals(error, original))) "One original lifecycle fault."
        Expect.sequenceEqual (File.ReadAllBytes(Path.Combine(directory, value.Hash.Hex + ".zst"))) bytes "Canonical commit remains durable."
        Expect.equal (Directory.GetFiles(directory, "*.tmp").Length) 0 "Other owned temp cleanup still attempted."
        let mutable refused = false
        try let! _ = store.StartDownload(PhantomTransferId 2UL, value, None) in ()
        with error -> refused <- Object.ReferenceEquals(error, original)
        Expect.isTrue refused "Partially failed cleanup cannot resume business work."
        let mutable released = false
        try do! (store.Dispose()).WaitAsync(TimeSpan.FromSeconds 2.)
        with error -> released <- Object.ReferenceEquals(error, original)
        Expect.isTrue released "Disposer finishes and reports retained fault."
        use exclusive = new FileStream(Path.Combine(directory, value.Hash.Hex + ".zst"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)
        Expect.equal exclusive.Length 4L "Upload handle was closed."
    })

    storageCase "saturated registry still admits cancellation and refunds the download pin" (fun root config _ -> task {
        let directory = Path.Combine(root, "saturated")
        Directory.CreateDirectory directory |> ignore
        let bytes = [|1uy;2uy;3uy;4uy|]
        let value = asset 1UL bytes
        File.WriteAllBytes(Path.Combine(directory, value.Hash.Hex + ".zst"), bytes)
        let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let resume = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let store = PhantomStorage.createWithFileReader { config with StoragePath = directory; DiskBytes = 4L; CacheEntries = 2; MaxTransfers = 1 }
                        (fun _ -> new ControlledRead(bytes, entered, resume, None) :> Stream)
        try
            let pending = store.StartDownload(PhantomTransferId 1UL, value, None)
            do! entered.Task.WaitAsync(TimeSpan.FromSeconds 2.)
            let other = asset 2UL [|4uy;3uy;2uy;1uy|]
            // MaxTransfers*4+16 =20 queue entries; the worker is gated in SHA.
            let queued = Array.init 20 (fun index -> store.StartUpload(PhantomTransferId(uint64 index + 2UL), other, None))
            let! rejected = store.StartUpload(PhantomTransferId 22UL, other, None)
            Expect.equal rejected (Error PhantomStorageError.QueueFull) "Exact saturated ordinary admission."
            let canceled = store.Cancel(PhantomTransferId 1UL)
            Expect.isFalse canceled.IsCompleted "Control waits for capacity instead of claiming cleanup."
            resume.SetResult()
            let! opened = pending
            Expect.equal opened (Ok None) "Originally accepted download pinned."
            let! outcomes = Task.WhenAll queued
            Expect.isTrue (outcomes |> Array.forall ((=) (Error PhantomStorageError.TransferLimit))) "Queued commands all receive their real outcome."
            do! canceled.WaitAsync(TimeSpan.FromSeconds 2.)
            let! admitted = store.StartUpload(PhantomTransferId 23UL, other, None)
            Expect.equal admitted (Ok false) "Completed cancel released pin; exact disk quota reusable."
        finally
            resume.TrySetResult() |> ignore
            store.Dispose().GetAwaiter().GetResult()
    })

    storageCase "unexpected registry dependency fault settles every accepted reply and closes owner" (fun root config _ -> task {
        let directory = Path.Combine(root, "worker-fault")
        Directory.CreateDirectory directory |> ignore
        let bytes = [|1uy;2uy;3uy;4uy|]
        let value = asset 1UL bytes
        File.WriteAllBytes(Path.Combine(directory, value.Hash.Hex + ".zst"), bytes)
        let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let resume = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let original = InvalidOperationException "verification dependency fault"
        let store = PhantomStorage.createWithFileReader { config with StoragePath = directory; MaxTransfers = 1 }
                        (fun _ -> new ControlledRead(bytes, entered, resume, Some original) :> Stream)
        let pending = store.StartDownload(PhantomTransferId 1UL, value, None)
        do! entered.Task.WaitAsync(TimeSpan.FromSeconds 2.)
        let queued = Array.init 10 (fun index -> store.StartUpload(PhantomTransferId(uint64 index + 2UL), value, None))
        resume.SetResult()
        let mutable first = None
        try let! _ = pending.WaitAsync(TimeSpan.FromSeconds 2.) in ()
        with error -> first <- Some error
        Expect.isTrue (first |> Option.exists (fun error -> Object.ReferenceEquals(error, original))) "Unexpected fault isn't ordinary IO rejection."
        let mutable settled = None
        try let! _ = (Task.WhenAll queued).WaitAsync(TimeSpan.FromSeconds 2.) in ()
        with error -> settled <- Some error
        Expect.isTrue (settled |> Option.exists (fun error -> Object.ReferenceEquals(error, original))) "Queued admitted replies preserve the fault."
        Expect.isTrue (queued |> Array.forall _.IsFaulted) "No accepted reply remains abandoned."
        let! failed = store.OwnerFailure
        Expect.isTrue (Object.ReferenceEquals(failed, original)) "Failure exposed to runtime."
        let mutable released = false
        try do! (store.Dispose()).WaitAsync(TimeSpan.FromSeconds 2.)
        with error -> released <- Object.ReferenceEquals(error, original)
        Expect.isTrue released "Disposal terminates after fault."
        Expect.sequenceEqual (File.ReadAllBytes(Path.Combine(directory, value.Hash.Hex + ".zst"))) bytes "Previously committed file preserved."
    })

    storageCase "initialization IO rejection permits cleanup and retry without poisoning owner" (fun root config _ -> task {
        Directory.CreateDirectory root |> ignore
        let directory = Path.Combine(root, "initialization")
        File.WriteAllBytes(directory, [|0uy|])
        let store = PhantomStorage.create { config with StoragePath = directory }
        try
            let bytes = [|1uy;2uy;3uy;4uy|]
            let value = asset 1UL bytes
            let id = PhantomTransferId 1UL
            let! rejected = store.StartUpload(id, value, None)
            match rejected with Error (PhantomStorageError.Io _) -> () | other -> failtestf "Expected directory IO rejection, got %A" other
            do! store.Cancel id
            Expect.isFalse store.OwnerFailure.IsCompleted "No resources admitted; cleanup cannot promote initialization rejection into fault."
            File.Delete directory
            let! started = store.StartUpload(id, value, None)
            Expect.equal started (Ok false) "Changed filesystem condition permits whole admission retry."
            let! complete = store.WriteChunk(id, 0, bytes)
            Expect.equal complete (Ok true) "Retry commits once."
        finally store.Dispose().GetAwaiter().GetResult()
    })

    testCase "synchronous cancellation port fault still releases both owners and reaches supervisor" <| fun _ ->
        for failHttp in [true; false] do
            let original = InvalidOperationException "synchronous cleanup dependency fault"
            let mutable httpCalls, storageCalls = 0, 0
            let http, _ = fakeHttp()
            let cancelHttp _ =
                httpCalls <- httpCalls + 1
                if failHttp then raise original else Task.FromResult()
            let cancelStorage _ =
                storageCalls <- storageCalls + 1
                if not failHttp then raise original else Task.FromResult()
            let http = { http with Cancel = cancelHttp }
            let storage = { memoryStorage with StartUpload = (fun _ -> Task.FromResult(Error PhantomStorageError.DiskQuota)); Cancel = cancelStorage }
            let state = PhantomAgent.create options storage http (fun _ -> Ok ())
            let id = Guid.NewGuid()
            PhantomAgent.observe state (PhantomObservation.Member(id, player 1UL 10UL))
            PhantomAgent.activate state id
            PhantomAgent.handle state 0L id (PhantomRequest.Publish(asset 1UL [|1uy|], 10UL, requestId(), None))
            PhantomAgent.tick state 1L
            Expect.equal (httpCalls, storageCalls) (1, 1) "A synchronous port fault cannot skip the other release."
            Expect.isTrue (PhantomAgent.failure state |> Option.exists (fun error -> Object.ReferenceEquals(error, original))) "Original fault reaches supervision."
            PhantomAgent.tick state 2L
            Expect.equal (httpCalls, storageCalls) (1, 1) "Faulted cleanup cannot be discarded or executed twice."
            Expect.isSome (PhantomAgent.failure state) "Lifecycle failure retained."

]
