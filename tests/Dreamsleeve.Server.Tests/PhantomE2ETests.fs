module Dreamsleeve.Server.Tests.PhantomE2ETests

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Security.Cryptography
open System.Threading
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Infrastructure
open Enet
open Google.Protobuf
open Microsoft.Extensions.Logging.Abstractions
open Expecto

let private ok = function Ok value -> value | Error error -> failwithf "%A" error
let private ticket name = (name: string).PadRight(43, '_')
let private profile number name = PlayerData.create (PlayerId.create number |> ok) (Username.create 32 name |> ok) (DisplayName.create 64 name |> ok) NameColor.unknown
let private address port =
    let mutable value = Unchecked.defaultof<enet.ENetAddress>
    Expect.equal (enet.ENetAddress.FromIpAddress(IPAddress.Loopback, port, &value)) SocketError.Success "Loopback address."
    value
let private freePort () =
    use socket = new UdpClient(IPEndPoint(IPAddress.Loopback, 0))
    uint16 (socket.Client.LocalEndPoint :?> IPEndPoint).Port

type private Client = {
    Host: EnetHost; Peer: EnetPeer; mutable Connected: bool
    Models: ResizeArray<Dreamsleeve.Protocol.Phantom.ServerAssetPacket>
    Poses: ResizeArray<Dreamsleeve.Protocol.Phantom.ServerPosePacket>
    Control: ResizeArray<Dreamsleeve.Protocol.Chat.ServerPacket>
}
let private client port =
    let host = EnetHost.Create(Unchecked.defaultof<enet.ENetAddress>, 1un, 5un, 0u, 0u, EnetHostOption.Ipv4)
    let mutable peer = Unchecked.defaultof<EnetPeer>
    Expect.isTrue (host.TryConnect(address port, 5un, 0u, &peer)) "Client connects."
    { Host = host; Peer = peer; Connected = false; Models = ResizeArray(); Poses = ResizeArray(); Control = ResizeArray() }
let private service client =
    let mutable remaining = 128
    while remaining > 0 do
        let mutable event = Unchecked.defaultof<EnetEvent>
        let result = client.Host.Service(0u, &event)
        Expect.isGreaterThanOrEqual result 0 "Client ENet service."
        if result = 0 then remaining <- 0
        else
            remaining <- remaining - 1
            match event.Type with
            | EnetEventType.Connect -> client.Connected <- true
            | EnetEventType.Receive ->
                use packet = event.Packet
                let reliable = (packet.Flags &&& EnetPacketFlag.Reliable) = EnetPacketFlag.Reliable
                if event.ChannelId = 4uy && packet.DataLength = 0un then Expect.isTrue reliable "Only epoch bookkeeping is empty."
                else
                    let bytes = packet.AsSpan().ToArray()
                    match event.ChannelId with
                    | 3uy ->
                        Expect.isTrue reliable "Models reliable."
                        let value = Dreamsleeve.Protocol.Phantom.ServerAssetPacket.Parser.ParseFrom bytes
                        Expect.equal value.ProtocolVersion 21u "Protocol21 model envelope."
                        if not (isNull value.Transfer) then Expect.isGreaterThan value.Transfer.RequestId 0UL "Transfer request correlation."
                        if not (isNull value.Complete) then Expect.isGreaterThan value.Complete.RequestId 0UL "Complete request correlation."
                        client.Models.Add value
                    | 4uy ->
                        Expect.isFalse reliable "Pose payload must stay unreliable."
                        Expect.equal (packet.Flags &&& EnetPacketFlag.Unsequenced) (enum<EnetPacketFlag> 0) "Pose sequenced."
                        let value = Dreamsleeve.Protocol.Phantom.ServerPosePacket.Parser.ParseFrom bytes
                        Expect.equal value.ProtocolVersion 21u "Protocol21 pose envelope."
                        client.Poses.Add value
                    | 0uy | 1uy -> client.Control.Add(Dreamsleeve.Protocol.Chat.ServerPacket.Parser.ParseFrom bytes)
                    | 2uy -> ()
                    | _ -> failtest "Unexpected lane."
            | EnetEventType.Disconnect -> client.Connected <- false
            | _ -> ()
let private send client lane flags (value: IMessage) =
    let mutable packet = EnetPacket.Create(ReadOnlySpan<byte>(value.ToByteArray()), flags)
    try Expect.isTrue (client.Peer.TrySend(lane, &packet)) "Client packet admitted by ENet."
    finally if packet.IsCreated then packet.Dispose()
let private asset client value = send client 3uy EnetPacketFlag.Reliable value
let private command client id apply =
    let packet = Dreamsleeve.Protocol.Chat.ClientPacket(ProtocolVersion = 21u, RequestId = id)
    apply packet
    send client 0uy EnetPacketFlag.Reliable packet

/// This is the real server's UDP path with production runtime, Presence, transport
/// owner and file storage. Account verification is a controlled dependency; the
/// parent-owned native smoke separately exercises production client Streaming.
let tests = testSequenced <| testList "Phantom protocol21 E2E" [
    testCase "authenticated UDP cold/warm transfer, pose fanout, chat and receive revocation" <| fun _ ->
        Expect.equal ProtocolCodec.Version 21u "Fixture targets protocol21."
        let root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dreamsleeve-phantom-e2e-" + Guid.NewGuid().ToString("N")))
        let options = { PhantomOptions.defaults with StoragePath = root; DiskBytes = 8L * 1024L * 1024L; RamBytes = 1024L * 1024L;
                                                      PublishCooldownMs = 100; ReplicationIntervalMs = 10; PoseIntervalMs = 10;
                                                      PlayerModelBytesPerSecond = 4 * 1024 * 1024; ModelBytesPerSecond = 8 * 1024 * 1024;
                                                      Limits = { PhantomOptions.defaults.Limits with CompressedBytes = 2 * 1024 * 1024; RawBytes = 4 * 1024 * 1024 } }
        let server = { ServerConfig.defaults with Port = freePort() }
        let runtimeOptions = { ServerRuntimeOptions.defaults with Presence = { ServerRuntimeOptions.defaults.Presence with ReplicationIntervalMs = 10 } }
        let settings = Settings.game server runtimeOptions IdentityOptions.defaults AnnouncementOptions.defaults GroundMarkOptions.defaults |> GameSettings.withPhantoms options |> ok
        use auth = Agent.Start(AgentOptions.create "phantom-e2e-auth", fun _ (request: SessionAuthenticationRequest) -> task {
            let result =
                if request.Ticket = ticket "alice" then Ok { Profile = profile 1UL "alice"; Role = PlayerRole.Player; Mute = ValueNone; SignedInFrom = ValueSome IPAddress.Loopback }
                elif request.Ticket = ticket "bob" then Ok { Profile = profile 2UL "bob"; Role = PlayerRole.Player; Mute = ValueNone; SignedInFrom = ValueSome IPAddress.Loopback }
                else Error SessionAuthenticationError.InvalidTicket
            request.ReplyTo.TryPost { OperationId = request.OperationId; Result = result } |> ignore
        })
        use names = Agent.Start(AgentOptions.create "phantom-e2e-names", fun _ (request: ProfileChangeRequest) -> task {
            request.ReplyTo.TryPost { OperationId = request.OperationId; Result = Error ProfileChangeError.Unavailable } |> ignore
        })
        use moderation = Agent.Start(AgentOptions.create "phantom-e2e-moderation", fun _ (request: ModerationRequest) -> task {
            request.ReplyTo.TryPost { OperationId = request.OperationId; Result = Error ModerationError.Unavailable } |> ignore
        })
        use marks = Agent.Start(AgentOptions.create "phantom-e2e-marks", fun _ (_: GroundMarkWrite) -> task { () })
        use guilds = Agent.Start(AgentOptions.create "phantom-e2e-guilds", fun _ (_: GuildWrite) -> task { () })
        let authentication = { Requests = auth.Ref.TryReliable().Value; Profiles = names.Ref.TryReliable().Value; Moderation = moderation.Ref.TryReliable().Value; Completion = auth.Completion }
        let storage = PhantomStorage.create options
        let transport = EnetTransport.createWithPhantoms server options NullLogger.Instance |> ok
        use runtime = ServerRuntime.startWithPhantoms storage settings Moderation.empty PseudonymDictionary.builtIn
                            { Loaded = []; NextId = 1UL; Writer = marks.Ref.TryReliable().Value }
                            { Loaded = []; Profiles = []; NextId = 1UL; Writer = guilds.Ref.TryReliable().Value; WriterStopped = guilds.Completion }
                            authentication transport NullLogger.Instance
        let alice, bob = client server.Port, client server.Port
        let pump () = service alice; service bob
        let wait reason predicate =
            let until = Environment.TickCount64 + 15000L
            while not (predicate()) && Environment.TickCount64 < until do pump(); Thread.Sleep 1
            Expect.isTrue (predicate()) reason
        let preferences receive = asset bob (Dreamsleeve.Protocol.Phantom.ClientAssetPacket(ProtocolVersion = 21u, Preferences = Dreamsleeve.Protocol.Phantom.Preferences(Publish = true, Receive = receive, Maximum = 1u, Distance = 4096.0f)))
        try
            wait "Both ENet peers connected." (fun () -> alice.Connected && bob.Connected)
            for _ in 1 .. 30 do pump(); Thread.Sleep 1
            Expect.equal (alice.Models.Count + bob.Models.Count) 0 "No Policy or model data before authentication."
            for client, name in [alice,"alice";bob,"bob"] do
                command client 1UL (fun packet -> packet.OpenSession <- Dreamsleeve.Protocol.Chat.OpenSession(SessionTicket = ticket name))
            wait "Authenticated welcome and Policy." (fun () ->
                [alice;bob] |> List.forall (fun client -> client.Control |> Seq.exists (fun packet -> not (isNull packet.SessionOpened)) && client.Models.Count > 0))
            Expect.equal alice.Models[0].Policy.WindowChunks 4u "Policy bootstrap precedes model offers."
            for client in [alice;bob] do
                command client 2UL (fun packet -> packet.UpdatePlayer <- Dreamsleeve.Protocol.Chat.UpdatePlayer(BeginCharacter = Dreamsleeve.Protocol.Chat.BeginCharacter(Name = "E2E")))
                command client 10UL (fun packet ->
                    packet.UpdatePlayer <- Dreamsleeve.Protocol.Chat.UpdatePlayer(SetLocation = Dreamsleeve.Protocol.Chat.SetPlayerLocation(ContextRevision = 10UL,
                        Location = Dreamsleeve.Protocol.Chat.PlayerLocation(Location = Dreamsleeve.Protocol.Chat.Location(LocationId = Dreamsleeve.Protocol.Chat.FormKey(PluginName = "Skyrim.esm", LocalFormId = 60u), LocationName = "Whiterun"), Position = Dreamsleeve.Protocol.Chat.Position(), Rotation = Dreamsleeve.Protocol.Chat.Rotation()))))
            wait "Source movement context admitted." (fun () -> alice.Control |> Seq.exists (fun packet -> packet.RequestId = 10UL && not (isNull packet.PlayerUpdateAccepted)))
            let bytes = Array.zeroCreate (256 * 1024 + 7)
            Random(21).NextBytes bytes
            let descriptor = Dreamsleeve.Protocol.Phantom.AssetDescriptor(Hash = ByteString.CopyFrom(SHA256.HashData bytes), Generation = 1UL, FormatVersion = 1u,
                                CompressedBytes = uint32 bytes.Length, RawBytes = uint32 bytes.Length, Channels = 2u, Geometry = 1u)
            let publish request descriptor = asset alice (Dreamsleeve.Protocol.Phantom.ClientAssetPacket(ProtocolVersion = 21u, Publish = Dreamsleeve.Protocol.Phantom.Publish(Asset = descriptor, ContextRevision = 10UL, RequestId = request)))
            publish 101UL descriptor
            wait "Cold upload transfer." (fun () -> alice.Models |> Seq.exists (fun packet -> not (isNull packet.Transfer)))
            let transfer = alice.Models |> Seq.pick (fun packet -> if isNull packet.Transfer then None else Some packet.Transfer)
            Expect.equal transfer.RequestId 101UL "Publish assignment echoes request."
            Expect.equal transfer.PlayerId 1UL "Authenticated source assigned by server."
            let chat = Dreamsleeve.Protocol.Chat.ClientPacket(ProtocolVersion = 21u, RequestId = 11UL, SendChat = Dreamsleeve.Protocol.Chat.SendChat(ChannelId = 1UL, Text = "phantom-e2e-chat"))
            send bob 1uy EnetPacketFlag.Reliable chat
            let mutable sent, acknowledged = 0, 0
            while sent < bytes.Length do
                while sent < bytes.Length && sent - acknowledged < options.WindowChunks * options.ChunkBytes do
                    let count = min options.ChunkBytes (bytes.Length - sent)
                    asset alice (Dreamsleeve.Protocol.Phantom.ClientAssetPacket(ProtocolVersion = 21u, Chunk = Dreamsleeve.Protocol.Phantom.Chunk(TransferId = transfer.TransferId, Offset = uint32 sent, Data = ByteString.CopyFrom(bytes, sent, count))))
                    sent <- sent + count
                let previous = acknowledged
                wait "Upload window acknowledged." (fun () ->
                    acknowledged <- alice.Models |> Seq.choose (fun packet -> if isNull packet.Progress then None else Some(int packet.Progress.NextOffset)) |> Seq.fold max 0
                    acknowledged > previous)
            wait "Cold upload ready and AOI Offer." (fun () ->
                alice.Models |> Seq.exists (fun packet -> not (isNull packet.Complete) && packet.Complete.Accepted)
                && bob.Models |> Seq.exists (fun packet -> not (isNull packet.Offer)))
            let offer = bob.Models |> Seq.pick (fun packet -> if isNull packet.Offer then None else Some packet.Offer)
            Expect.equal offer.PlayerId 1UL "Presence-authorized source."
            Expect.isTrue (bob.Control |> Seq.exists (fun packet -> not (isNull packet.ChatPublished) && packet.ChatPublished.Message.Text = "phantom-e2e-chat")) "Chat remains live during model streaming."
            asset bob (Dreamsleeve.Protocol.Phantom.ClientAssetPacket(ProtocolVersion = 21u, Download = Dreamsleeve.Protocol.Phantom.Download(PlayerId = 1UL, Generation = 1UL, RequestId = 201UL)))
            wait "Download window." (fun () -> bob.Models |> Seq.exists (fun packet -> not (isNull packet.Chunk)))
            let firstWindow = bob.Models |> Seq.filter (fun packet -> not (isNull packet.Chunk)) |> Seq.length
            Expect.isLessThanOrEqual firstWindow 4 "Download waits for application Progress."
            preferences false
            wait "Receive reduction removes view." (fun () -> bob.Models |> Seq.exists (fun packet -> not (isNull packet.Remove)))
            let removed = bob.Models |> Seq.pick (fun packet -> if isNull packet.Remove then None else Some packet.Remove)
            Expect.isGreaterThan removed.ViewRevision offer.ViewRevision "Monotonic revoke."
            preferences true
            wait "Fresh reentry Offer." (fun () -> bob.Models |> Seq.exists (fun packet -> not (isNull packet.Offer) && packet.Offer.ViewRevision > removed.ViewRevision))
            let fresh = bob.Models |> Seq.choose (fun packet -> if isNull packet.Offer then None else Some packet.Offer) |> Seq.last
            bob.Models.Clear()
            asset bob (Dreamsleeve.Protocol.Phantom.ClientAssetPacket(ProtocolVersion = 21u, Download = Dreamsleeve.Protocol.Phantom.Download(PlayerId = 1UL, Generation = 1UL, RequestId = 202UL)))
            wait "New download Transfer." (fun () -> bob.Models |> Seq.exists (fun packet -> not (isNull packet.Transfer)))
            let download = bob.Models |> Seq.pick (fun packet -> if isNull packet.Transfer then None else Some packet.Transfer)
            Expect.equal download.RequestId 202UL "New download assignment echoes request."
            let received = ResizeArray<byte>()
            let mutable inspected = 0
            while received.Count < bytes.Length do
                wait "Download chunk progress." (fun () -> bob.Models.Count > inspected)
                while inspected < bob.Models.Count do
                    let packet = bob.Models[inspected]
                    inspected <- inspected + 1
                    if not (isNull packet.Chunk) then
                        Expect.equal packet.Chunk.Offset (uint32 received.Count) "Chunk ordering."
                        received.AddRange(packet.Chunk.Data.ToByteArray())
                        asset bob (Dreamsleeve.Protocol.Phantom.ClientAssetPacket(ProtocolVersion = 21u, Progress = Dreamsleeve.Protocol.Phantom.Progress(TransferId = download.TransferId, NextOffset = uint32 received.Count)))
            wait "Final download ACK completes." (fun () -> bob.Models |> Seq.exists (fun packet -> not (isNull packet.Complete) && packet.Complete.TransferId = download.TransferId && packet.Complete.Accepted))
            Expect.equal (received.ToArray()) bytes "Real UDP compressed-byte round trip."
            let pose = Dreamsleeve.Protocol.Phantom.ClientPosePacket(ProtocolVersion = 21u, Sample = Dreamsleeve.Protocol.Phantom.PoseSample(Generation = 1UL, ContextRevision = 10UL, Sequence = 1UL, SampledAtUs = 50000UL, Payload = ByteString.CopyFrom(Array.init 20000 (fun index -> byte (index % 251)))))
            send alice 4uy EnetPacketFlag.UnreliableFragment pose
            wait "Fragmented pose fanout." (fun () -> bob.Poses.Count > 0)
            Expect.equal (bob.Poses[0].PlayerId, bob.Poses[0].ViewRevision, bob.Poses[0].Sample.Sequence) (1UL,fresh.ViewRevision,1UL) "Pose follows authenticated source and fresh AOI epoch."
            alice.Models.Clear()
            let warm = descriptor.Clone()
            warm.Generation <- 2UL
            publish 102UL warm
            wait "Warm cached publication ready without any chunks." (fun () -> alice.Models |> Seq.exists (fun packet -> not (isNull packet.Complete) && packet.Complete.Accepted && packet.Complete.Generation = 2UL))
            Expect.equal (File.ReadAllBytes(Path.Combine(root, Convert.ToHexStringLower(SHA256.HashData bytes) + ".zst"))) bytes "One verified content-addressed file."
            Expect.equal (Directory.GetFiles(root, "*.tmp").Length) 0 "No partial upload after warm reuse."
            printfn "protocol21 E2E PASS: auth/bootstrap + cold 262151-byte UDP upload/download + ACK windows + chat + fragmented pose + receive revoke/reentry + warm cache"
            runtime.PostAsync(ServerRuntimeMessage.Stop).GetAwaiter().GetResult() |> ignore
            wait "Runtime cleanup completes." (fun () -> runtime.Completion.IsCompleted)
            runtime.Completion.GetAwaiter().GetResult()
        finally
            runtime.Abort()
            alice.Host.Dispose(); bob.Host.Dispose()
            transport.Dispose()
            storage.Dispose().GetAwaiter().GetResult()
            let parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            if Path.GetDirectoryName root <> parent then failwith "E2E cleanup escaped temporary directory."
            if Directory.Exists root then Directory.Delete(root, true)
]