module Dreamsleeve.Server.Tests.EnetTransportTests

open System
open System.Diagnostics
open System.Diagnostics.Metrics
open System.IO
open System.Net
open System.Net.Sockets
#nowarn "9"
open Microsoft.FSharp.NativeInterop
open System.Threading
open Enet
open Expecto
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Infrastructure
open Dreamsleeve.Server.Infrastructure.Interop

let private ok = function
    | Ok value -> value
    | Error error -> failwithf "%A" error

let private address port =
    let mutable value = Unchecked.defaultof<enet.ENetAddress>
    Expect.equal (enet.ENetAddress.FromIpAddress(IPAddress.Loopback, port, &value)) SocketError.Success "address"
    value

let private freePort () =
    use socket = new UdpClient(IPEndPoint(IPAddress.Loopback, 0))
    uint16 (socket.Client.LocalEndPoint :?> IPEndPoint).Port

let private service (host: EnetHost) receive =
    let mutable keepGoing = true
    let mutable remaining = 64
    while keepGoing && remaining > 0 do
        let mutable event = Unchecked.defaultof<EnetEvent>
        let result = host.Service(0u, &event)
        Expect.isGreaterThanOrEqual result 0 "ENet service"
        if result = 0 then
            keepGoing <- false
        else
            remaining <- remaining - 1
            receive event

let private discard (event: EnetEvent) =
    if event.Type = EnetEventType.Receive then
        event.Packet.Dispose()

let private until condition pump =
    let deadline = Stopwatch.StartNew()
    while not (condition ()) && deadline.ElapsedMilliseconds < 5000L do
        pump ()
        Thread.Sleep 1
    Expect.isTrue (condition ()) "loopback operation completed before the guard deadline"

let private withPeersAtMtuObserved mtu received run =
    Expect.equal (enet.ENET_API.enet_initialize()) 0 "initialize"
    try
        use server = EnetHost.Create(address 0us, 1un, 5un, 0u, 0u, EnetHostOption.Ipv4)
        use client = EnetHost.Create(Unchecked.defaultof<enet.ENetAddress>, 1un, 5un, 0u, 0u, EnetHostOption.Ipv4)
        Expect.isTrue (client.SetMtu mtu) "Set proposed MTU before connect."
        let mutable clientPeer = Unchecked.defaultof<EnetPeer>
        Expect.isTrue (client.TryConnect(server.Address, 5un, 0u, &clientPeer)) "begin connect"
        let mutable serverPeer = None
        let mutable clientReady = false
        let pump () =
            service server (fun event ->
                if event.Type = EnetEventType.Connect then serverPeer <- Some event.Peer
                discard event)
            service client (fun event ->
                if event.Type = EnetEventType.Connect then clientReady <- true
                received event
                discard event)
        until (fun () -> serverPeer.IsSome && clientReady) pump
        run server serverPeer.Value pump
    finally
        enet.ENET_API.enet_deinitialize()

let private withPeersAtMtu mtu run = withPeersAtMtuObserved mtu ignore run
let private withPeers run = withPeersAtMtu 1392u run

let private withAdapter settings run =
    let config = { settings with Port = freePort (); ServiceTimeoutMs = 0u }
    let transport = EnetTransport.createInline config NullLogger.Instance |> ok
    try
        use client = EnetHost.Create(Unchecked.defaultof<enet.ENetAddress>, 1un, 5un, 0u, 0u, EnetHostOption.Ipv4)
        let mutable peer = Unchecked.defaultof<EnetPeer>
        Expect.isTrue (client.TryConnect(address config.Port, 5un, 0u, &peer)) "begin connect"
        let events = ResizeArray<ServerTransportEvent>()
        let packets = ResizeArray<byte array>()
        let pump () =
            events.AddRange(transport.Poll() |> ok)
            service client (fun event ->
                if event.Type = EnetEventType.Receive then
                    use packet = event.Packet
                    packets.Add(packet.AsSpan().ToArray()))
        until (fun () -> events |> Seq.exists (function ServerTransportEvent.Connected _ -> true | _ -> false)) pump
        let connection = events |> Seq.pick (function ServerTransportEvent.Connected(id, _) -> Some id | _ -> None)
        run config transport client peer connection events packets pump
    finally
        transport.Dispose()

let private logger observe =
    { new ILogger with
        member _.BeginScope<'T>(_: 'T) = Unchecked.defaultof<IDisposable>
        member _.IsEnabled _ = true
        member _.Log<'T>(level, _, state: 'T, error, formatter: Func<'T, exn, string>) = observe level error }

let private expectClosed (socket: Socket) =
    let mutable closed = false
    try socket.ReceiveBufferSize |> ignore
    with :? SocketException -> closed <- true
    Expect.isTrue closed "The allocated native socket was closed before startup returned."

let tests = testSequenced <| testList "ENet transport" [
    testCase "internal lanes map to schema channels and reject every unknown channel" <| fun _ ->
        let lanes = [
            DeliveryLane.Control, Dreamsleeve.Protocol.Network.DeliveryLane.Control
            DeliveryLane.Chat, Dreamsleeve.Protocol.Network.DeliveryLane.Chat
            DeliveryLane.Realtime, Dreamsleeve.Protocol.Network.DeliveryLane.Realtime
            DeliveryLane.Models, Dreamsleeve.Protocol.Network.DeliveryLane.Models
            DeliveryLane.Poses, Dreamsleeve.Protocol.Network.DeliveryLane.Poses
        ]
        Expect.equal lanes.Length (Enum.GetValues<Dreamsleeve.Protocol.Network.DeliveryLane>().Length) "Every schema lane has an explicit internal owner."
        for lane, wire in lanes do
            Expect.equal (DeliveryLane.toChannel lane) (byte wire) "Schema owns the number."
            Expect.equal (DeliveryLane.fromChannel (byte wire)) (Ok lane) "Boundary constructs the trusted lane."
        for value in 0 .. 255 do
            let channel = byte value
            if not (Enum.IsDefined(enum<Dreamsleeve.Protocol.Network.DeliveryLane> value)) then
                Expect.equal (DeliveryLane.fromChannel channel) (Error(DeliveryLaneError.UnknownChannel channel)) "Unknown wire lane is a typed rejection."

    testCase "models reliable and fragmented sequenced poses use their agreed lanes" <| fun _ ->
        withAdapter ServerConfig.defaults (fun _ transport client peer connection events _ pump ->
            let payload = Array.init 20000 (fun index -> byte (index % 251))
            for lane, flags in [DeliveryLane.Models, EnetPacketFlag.Reliable; DeliveryLane.Poses, EnetPacketFlag.UnreliableFragment] do
                let bytes = if lane = DeliveryLane.Models then payload[0..PhantomAssetLimits.assetPacketBytes-1] else payload
                let mutable packet = EnetPacket.Create(ReadOnlySpan<byte>(bytes), flags)
                try Expect.isTrue (peer.TrySend(DeliveryLane.toChannel lane, &packet)) "native send"
                finally if packet.IsCreated then packet.Dispose()
                until (fun () -> events |> Seq.exists (function ServerTransportEvent.Received(_, actual, data) -> actual = lane && data = bytes | _ -> false)) pump
            let mutable received = None
            transport.Send(connection, { Schedule = PacketSchedule.Ordered; Lane = DeliveryLane.Poses; Bytes = payload }) |> ok
            until (fun () -> received.IsSome) (fun () ->
                transport.Poll() |> ok |> ignore
                service client (fun event ->
                    if event.Type = EnetEventType.Receive then
                        use packet = event.Packet
                        received <- Some(event.ChannelId, packet.Flags, packet.AsSpan().ToArray())))
            let channel, flags, data = received.Value
            Expect.equal channel 4uy "poses channel"
            Expect.equal data payload "complete independently usable sample"
            Expect.equal (flags &&& EnetPacketFlag.Reliable) (enum<EnetPacketFlag> 0) "fragmentation never upgrades to reliable"
            Expect.equal (flags &&& EnetPacketFlag.Unsequenced) (enum<EnetPacketFlag> 0) "sequenced")

    testCase "rollover sends a budgeted empty reliable epoch marker and keeps every pose unreliable" <| fun _ ->
        let received = ResizeArray<byte * EnetPacketFlag * byte array>()
        let capture (event: EnetEvent) =
            if event.Type = EnetEventType.Receive then received.Add(event.ChannelId, event.Packet.Flags, if event.Packet.DataLength = 0un then [||] else event.Packet.AsSpan().ToArray())
        withPeersAtMtuObserved 1392u capture (fun _ peer pump ->
            let budget, peerBudget = PacketBudget(16, 1024L * 1024L), PacketBudget(16, 1024L * 1024L)
            let inner: enet.ENetPeer = NativePtr.read (peer.GetInner())
            let channel = NativePtr.add inner.channels 4
            for size in [8;20000] do
                let mutable state = NativePtr.read channel
                state.outgoingUnreliableSequenceNumber <- UInt16.MaxValue
                NativePtr.write channel state
                received.Clear()
                let bytes = Array.init size (fun index -> byte (index % 251))
                Expect.equal (OutgoingPackets.TrySend(peer, ReadOnlySpan<byte>(bytes), budget, peerBudget, 4uy, PacketDelivery.SequencedFragmented)) PacketSendResult.Sent "Rollover send."
                Expect.equal (NativePtr.read channel).outgoingUnreliableSequenceNumber 1us "Marker advances reliable epoch; pose restarts at one."
                until (fun () -> received.Count = 2 && budget.Packets = 0) pump
                let markerChannel, markerFlags, marker = received[0]
                Expect.equal markerChannel 4uy "Epoch belongs to Poses."
                Expect.equal marker.Length 0 "Marker contains no domain payload."
                Expect.equal (markerFlags &&& EnetPacketFlag.Reliable) EnetPacketFlag.Reliable "Only marker reliable."
                let poseChannel, flags, payload = received[1]
                Expect.equal poseChannel 4uy "Pose lane."
                Expect.equal payload bytes "Complete payload."
                Expect.equal (flags &&& EnetPacketFlag.Reliable) (enum<EnetPacketFlag> 0) "No reliable pose at rollover."
                Expect.equal (flags &&& EnetPacketFlag.Unsequenced) (enum<EnetPacketFlag> 0) "Sequenced."
                Expect.equal budget.Bytes 0L "Both packet leases released."
            let mutable state = NativePtr.read channel
            state.outgoingUnreliableSequenceNumber <- UInt16.MaxValue
            NativePtr.write channel state
            let tight = PacketBudget(2, 1024L * 1024L)
            Expect.equal (OutgoingPackets.TrySend(peer, ReadOnlySpan<byte>([|1uy|]), tight, peerBudget, 4uy, PacketDelivery.SequencedFragmented)) PacketSendResult.BudgetExceeded "Marker is counted; pose waits for capacity."
            until (fun () -> tight.Packets = 0) pump
            Expect.equal (OutgoingPackets.TrySend(peer, ReadOnlySpan<byte>([|1uy|]), tight, peerBudget, 4uy, PacketDelivery.SequencedFragmented)) PacketSendResult.Sent "Next pose can use rotated epoch."
            until (fun () -> tight.Packets = 0) pump)

    testCase "empty reliable Poses epoch marker is swallowed before delivery policy and domain parsing" <| fun _ ->
        withAdapter ServerConfig.defaults (fun _ _ _ peer connection events _ pump ->
            let mutable marker = EnetPacket.Create(ReadOnlySpan<byte>.Empty, EnetPacketFlag.Reliable)
            try Expect.isTrue (peer.TrySend(4uy, &marker)) "Epoch marker queued."
            finally if marker.IsCreated then marker.Dispose()
            let mutable pose = EnetPacket.Create(ReadOnlySpan<byte>([|1uy;2uy|]), EnetPacketFlag.UnreliableFragment)
            try Expect.isTrue (peer.TrySend(4uy, &pose)) "Next pose queued."
            finally if pose.IsCreated then pose.Dispose()
            until (fun () -> events |> Seq.exists (function ServerTransportEvent.Received(_, DeliveryLane.Poses, _) -> true | _ -> false)) pump
            Expect.isFalse (events.Contains(ServerTransportEvent.Disconnected connection)) "Epoch marker does not violate reliable policy."
            let received = events |> Seq.choose (function ServerTransportEvent.Received(_, DeliveryLane.Poses, bytes) -> Some bytes | _ -> None) |> Seq.toArray
            Expect.equal received [|[|1uy;2uy|]|] "Marker never crosses transport boundary.")
    testCase "reliable poses are rejected instead of creating a reliable snapshot backlog" <| fun _ ->
        withAdapter ServerConfig.defaults (fun _ _ _ peer connection events _ pump ->
            let mutable packet = EnetPacket.Create(ReadOnlySpan<byte>([|1uy|]), EnetPacketFlag.Reliable)
            try Expect.isTrue (peer.TrySend(4uy, &packet)) "send invalid delivery"
            finally if packet.IsCreated then packet.Dispose()
            until (fun () -> events.Contains(ServerTransportEvent.Disconnected connection)) pump
            Expect.isFalse (events |> Seq.exists (function ServerTransportEvent.Received _ -> true | _ -> false)) "not dispatched")

    testCase "pump captures socket send failure and clears diagnostic on success" <| fun _ ->
        Expect.equal (enet.ENET_API.enet_initialize()) 0 "initialize"
        try
            use host = EnetHost.Create(address 0us, 1un, 5un, 0u, 0u, EnetHostOption.Ipv4)
            let mutable endpoint = Unchecked.defaultof<enet.ENetAddress>
            Expect.equal (enet.ENetAddress.FromIpAddress(IPAddress.Broadcast, 8778us, &endpoint)) SocketError.Success "address"
            let mutable peer = Unchecked.defaultof<EnetPeer>
            Expect.isTrue (host.TryConnect(endpoint, 5un, 0u, &peer)) "queue connect"
            Expect.equal (enet.ENET_API.enet_socket_set_option(host.Socket, enet.ENetSocketOption.ENET_SOCKOPT_BROADCAST, 0)) 0 "disable broadcast"
            // With broadcast disabled the OS rejects this send locally.
            let mutable socketError = SocketError.Success
            Expect.equal (EnetPump.Service(host, &socketError)) -1 "send error propagated"
            Expect.equal socketError SocketError.AccessDenied "actual socket reason preserved"
            Expect.isTrue (PumpHealth.transient socketError) "a refused destination does not break the host"
            peer.Reset()
            Expect.equal (EnetPump.Service(host, &socketError)) 0 "healthy host still services"
            Expect.equal socketError SocketError.Success "do not attach stale OS errors to success"
        finally
            enet.ENET_API.enet_deinitialize()

    testCase "a failed send to one destination passes and is summarized; an unknown failure breaks" <| fun _ ->
        let health = PumpHealth.create ()
        let observe now result error = PumpHealth.observe health now result error
        Expect.equal (observe 0L -1 SocketError.HostUnreachable) (PumpHealth.Verdict.Report(1, SocketError.HostUnreachable)) "the first failure is reported at once"
        Expect.equal (observe 1L -1 SocketError.NetworkUnreachable) PumpHealth.Verdict.Passing "then summarized"
        Expect.equal (observe 2L 0 SocketError.Success) PumpHealth.Verdict.Healthy "a healthy pump in between"
        Expect.equal (observe (PumpHealth.ReportIntervalMs + 1L) 0 SocketError.Success)
            (PumpHealth.Verdict.Report(1, SocketError.NetworkUnreachable)) "the unreported tail once the interval passed"
        Expect.equal (observe (PumpHealth.ReportIntervalMs + 2L) 0 SocketError.Success) PumpHealth.Verdict.Healthy "nothing left to report"
        Expect.equal (observe (PumpHealth.ReportIntervalMs + 3L) -1 SocketError.NotSocket) (PumpHealth.Verdict.Broken false) "an unusable socket breaks"
        Expect.equal (PumpHealth.observe (PumpHealth.create ()) 0L -1 SocketError.Success) (PumpHealth.Verdict.Broken false)
            "a failure without a socket cause is not a passing network problem"

    testCase "failing without a single healthy pump for MaxFailingMs breaks the transport" <| fun _ ->
        let health = PumpHealth.create ()
        for now in [ 0L; PumpHealth.MaxFailingMs / 2L; PumpHealth.MaxFailingMs - 1L ] do
            Expect.notEqual (PumpHealth.observe health now -1 SocketError.HostUnreachable) (PumpHealth.Verdict.Broken true) "still within the limit"
        Expect.equal (PumpHealth.observe health PumpHealth.MaxFailingMs -1 SocketError.HostUnreachable)
            (PumpHealth.Verdict.Broken true) "nothing but failures for the whole limit"
        let recovering = PumpHealth.create ()
        PumpHealth.observe recovering 0L -1 SocketError.HostUnreachable |> ignore
        PumpHealth.observe recovering (PumpHealth.MaxFailingMs - 1L) 0 SocketError.Success |> ignore
        Expect.notEqual (PumpHealth.observe recovering PumpHealth.MaxFailingMs -1 SocketError.HostUnreachable) (PumpHealth.Verdict.Broken true)
            "one healthy pump starts the count again"

    testCase "payload budget follows negotiated MTU and ENet fragmentation overhead" <| fun _ ->
        for mtu in [576u; 1392u] do
            withPeersAtMtu mtu (fun _ peer pump ->
                Expect.equal peer.Mtu mtu "Peer negotiated the proposed MTU."
                let payload = OutgoingPackets.GetUnfragmentedPayloadBytes peer
                Expect.equal payload (int mtu - 28) "Header plus fragment command, no checksum."
                let budget = PacketBudget(2, 4096L)
                let peerBudget = PacketBudget(2, 4096L)
                for size in [payload; payload + 1] do
                    Expect.equal (OutgoingPackets.TrySend(peer, ReadOnlySpan<byte>(Array.zeroCreate size), budget, peerBudget))
                        PacketSendResult.Sent "Boundary and fragmented packet are both accepted."
                until (fun () -> budget.Packets = 0) pump)

    testCase "sends preserve the native ENet flight window" <| fun _ ->
        withPeers (fun _ peer pump ->
            let budget = PacketBudget(16, 1024L * 1024L)
            let peerBudget = PacketBudget(16, 1024L * 1024L)
            for limited in [true; false] do
                let mutable inner = NativePtr.read (peer.GetInner())
                inner.incomingBandwidth <- if limited then 65536u else 0u
                inner.windowSize <- 4096u
                NativePtr.write (peer.GetInner()) inner
                Expect.equal (OutgoingPackets.TrySend(peer, ReadOnlySpan<byte>(Array.zeroCreate 16384), budget, peerBudget,
                                                     3uy, PacketDelivery.ReliableBulk)) PacketSendResult.Sent "Bulk admitted."
                Expect.equal peer.WindowSize 4096u "The transport preserves the native window."
                until (fun () -> budget.Packets = 0) pump)

    testCase "adapter exposes a payload budget only for live connections" <| fun _ ->
        withAdapter ServerConfig.defaults (fun _ transport _ peer connection _ _ _ ->
            Expect.equal (transport.MaxUnfragmentedPayloadBytes connection) (OutgoingPackets.GetUnfragmentedPayloadBytes peer) "Negotiated budget."
            Expect.equal (transport.MaxUnfragmentedPayloadBytes(Guid.NewGuid())) 0 "Unknown peer."
            transport.Reset connection
            Expect.equal (transport.MaxUnfragmentedPayloadBytes connection) 0 "Reset peer."
            transport.Dispose()
            Expect.equal (transport.MaxUnfragmentedPayloadBytes connection) 0 "Disposed host is not accessed.")

    testCase "buffer read rejection releases the host and preserves its diagnostic cause" <| fun _ ->
        let failure = SocketException(int SocketError.AccessDenied)
        let errors = ResizeArray<exn>()
        let mutable borrowed: Socket = null
        let read (host: EnetHost) =
            borrowed <- new Socket(new SafeSocketHandle(host.Socket.Handle, false))
            Error failure
        try
            let result = EnetTransport.allocateWith read PhantomOptions.defaults { ServerConfig.defaults with Port = 0us }
                            (logger (fun _ error -> errors.Add error))
            Expect.isError result "Expected read failure rejects startup."
            Expect.equal errors.Count 1 "One original diagnostic."
            Expect.isTrue (obj.ReferenceEquals(errors[0], failure)) "Original dependency failure retained."
            Expect.isNotNull borrowed "Host reached the read boundary."
            expectClosed borrowed
        finally
            if not (isNull borrowed) then borrowed.Dispose()

    testCase "unexpected startup logger fault escapes after releasing the allocated host" <| fun _ ->
        let failure = ArgumentException("logger fault is not host configuration rejection")
        let mutable borrowed: Socket = null
        let mutable observed: exn = null
        let read (host: EnetHost) =
            borrowed <- new Socket(new SafeSocketHandle(host.Socket.Handle, false))
            EnetTransport.readBuffers host
        try
            try
                EnetTransport.allocateWith read PhantomOptions.defaults { ServerConfig.defaults with Port = 0us }
                    (logger (fun _ _ -> raise failure)) |> ignore
            with error -> observed <- error
            Expect.isTrue (obj.ReferenceEquals(observed, failure)) "Original unexpected failure, not Error."
            Expect.isNotNull borrowed "Host reached the read boundary."
            expectClosed borrowed
        finally
            if not (isNull borrowed) then borrowed.Dispose()

    testCase "failed optional trace disables only its probe and leaves transport usable" <| fun _ ->
        let directory = Path.Combine(Path.GetTempPath(), "dreamsleeve-enet-trace-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory directory |> ignore
        let previous = Environment.GetEnvironmentVariable "DREAMSLEEVE_ENET_TRACE_DIRECTORY"
        try
            let blocked = Path.Combine(directory, "blocked")
            File.WriteAllText(blocked, "A file cannot be the trace directory.")
            Environment.SetEnvironmentVariable("DREAMSLEEVE_ENET_TRACE_DIRECTORY", blocked)
            withPeers (fun host peer pump ->
                let errors = ResizeArray<exn>()
                use listener = new MeterListener()
                let mutable samples = 0
                listener.InstrumentPublished <- fun instrument listener ->
                    if instrument.Meter.Name = "Dreamsleeve.Transport" then listener.EnableMeasurementEvents instrument
                listener.SetMeasurementEventCallback<double>(fun instrument _ _ _ ->
                    if instrument.Name = "transport.pending.bytes" then samples <- samples + 1)
                listener.Start()
                let diagnostics = TransportDiagnostics(fun error -> errors.Add error)
                let budget = PacketBudget(2, 1024L)
                let peerBudget = PacketBudget(2, 1024L)
                until (fun () -> samples >= 2) (fun () ->
                    diagnostics.EndPoll(diagnostics.BeginPoll(), 0, host, budget)
                    pump())
                Expect.equal errors.Count 1 "Failed trace creation reports once despite subsequent samples."
                Expect.isTrue (errors[0] :? IOException || errors[0] :? UnauthorizedAccessException) "Expected original filesystem error."
                Expect.equal (OutgoingPackets.TrySend(peer, ReadOnlySpan<byte>([|1uy|]), budget, peerBudget)) PacketSendResult.Sent "Traffic continues."
                until (fun () -> budget.Packets = 0) pump)
        finally
            Environment.SetEnvironmentVariable("DREAMSLEEVE_ENET_TRACE_DIRECTORY", previous)
            Directory.Delete(directory, true)

    testCase "trace append failure reports once after creation and does not stop packet ownership" <| fun _ ->
        let directory = Path.Combine(Path.GetTempPath(), "dreamsleeve-enet-trace-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory directory |> ignore
        let previous = Environment.GetEnvironmentVariable "DREAMSLEEVE_ENET_TRACE_DIRECTORY"
        try
            Environment.SetEnvironmentVariable("DREAMSLEEVE_ENET_TRACE_DIRECTORY", directory)
            withPeers (fun host peer pump ->
                let errors = ResizeArray<exn>()
                use listener = new MeterListener()
                let mutable samples = 0
                listener.InstrumentPublished <- fun instrument listener ->
                    if instrument.Meter.Name = "Dreamsleeve.Transport" then listener.EnableMeasurementEvents instrument
                listener.SetMeasurementEventCallback<double>(fun instrument _ _ _ ->
                    if instrument.Name = "transport.pending.bytes" then samples <- samples + 1)
                listener.Start()
                let diagnostics = TransportDiagnostics(fun error -> errors.Add error)
                let budget = PacketBudget(2, 1024L)
                let sample () = diagnostics.EndPoll(diagnostics.BeginPoll(), 0, host, budget)
                sample()
                let files = Directory.GetFiles(directory, "*.jsonl")
                Expect.equal files.Length 1 "Host trace created."
                Expect.equal errors.Count 0 "Creation succeeds."
                Expect.stringContains (File.ReadAllText files[0]) "\"kind\":\"host\"" "Real serialized header."
                File.Delete files[0]
                Directory.CreateDirectory files[0] |> ignore
                let mutable native = NativePtr.read (peer.GetInner())
                let previousTimeout = native.earliestTimeout
                native.earliestTimeout <- host.ServiceTime - 251u
                NativePtr.write (peer.GetInner()) native
                try
                    until (fun () -> errors.Count = 1) sample
                    let afterFailure = samples
                    until (fun () -> samples >= afterFailure + 2) sample
                    Expect.equal errors.Count 1 "No retry of the failed optional probe."
                    Expect.isTrue (errors[0] :? IOException || errors[0] :? UnauthorizedAccessException) "Original append failure."
                finally
                    let mutable current = NativePtr.read (peer.GetInner())
                    current.earliestTimeout <- previousTimeout
                    NativePtr.write (peer.GetInner()) current
                let peerBudget = PacketBudget(2, 1024L)
                Expect.equal (OutgoingPackets.TrySend(peer, ReadOnlySpan<byte>([|1uy|]), budget, peerBudget)) PacketSendResult.Sent "Packet remains independently owned."
                until (fun () -> budget.Packets = 0) pump)
        finally
            Environment.SetEnvironmentVariable("DREAMSLEEVE_ENET_TRACE_DIRECTORY", previous)
            Directory.Delete(directory, true)

    testCase "socket buffer readback borrows the handle and leaves the host usable" <| fun _ ->
        withPeers (fun host peer pump ->
            Expect.isTrue (TransportDiagnostics.ConfigureBuffers(host, 1024 * 1024, 1024 * 1024)) "socket options applied"
            let struct (receive, send) = EnetTransport.readBuffers host |> ok
            Expect.isGreaterThanOrEqual receive (1024 * 1024) "receive buffer granted"
            Expect.isGreaterThanOrEqual send (1024 * 1024) "send buffer granted"
            let budget = PacketBudget(2, 1024L)
            let peerBudget = PacketBudget(2, 1024L)
            Expect.equal (OutgoingPackets.TrySend(peer, ReadOnlySpan<byte>([|1uy|]), budget, peerBudget))
                PacketSendResult.Sent "borrowed socket was not closed"
            until (fun () -> budget.Packets = 0) pump)

    testCase "invalid socket buffers fail the settings check before any host exists" <| fun _ ->
        Expect.isError (ServerConfig.validate { ServerConfig.defaults with ReceiveBufferBytes = 0 }) "invalid receive buffer"
        Expect.isError (ServerConfig.validate { ServerConfig.defaults with SendBufferBytes = -1 }) "invalid send buffer"

    testCase "unknown delivery policy is rejected before native access or budget reservation" <| fun _ ->
        let hostBudget = PacketBudget(2, 64L)
        let peerBudget = PacketBudget(2, 64L)
        for value in [ -1; 99 ] do
            let result = OutgoingPackets.TrySend(Unchecked.defaultof<EnetPeer>, ReadOnlySpan<byte>([|1uy|]),
                                                hostBudget, peerBudget, 0uy, enum<PacketDelivery> value)
            Expect.equal result PacketSendResult.InvalidDelivery "Invalid CLR enum is a typed refusal."
            Expect.equal (hostBudget.Packets, hostBudget.Bytes, peerBudget.Packets, peerBudget.Bytes)
                         (0, 0L, 0, 0L) "No native peer access or lease mutation is needed."

    testCase "outgoing leases enforce packet and byte budgets, then release after ACK and reset" <| fun _ ->
        withPeers (fun _ peer pump ->
            let hostBudget = PacketBudget(8, 64L)
            let peerBudget = PacketBudget(2, 8L)
            let bytes = [|1uy; 2uy; 3uy; 4uy|]
            let send () = OutgoingPackets.TrySend(peer, ReadOnlySpan<byte>(bytes), hostBudget, peerBudget)
            Expect.equal (send ()) PacketSendResult.Sent "first admitted"
            Expect.equal (send ()) PacketSendResult.Sent "second admitted"
            Expect.equal (send ()) PacketSendResult.BudgetExceeded "third cannot bypass packet budget"
            Expect.equal (hostBudget.Packets, hostBudget.Bytes) (2, 8L) "failed admission reserved nothing"
            until (fun () -> hostBudget.Packets = 0) pump
            Expect.equal (peerBudget.Packets, peerBudget.Bytes) (0, 0L) "ACK frees exact peer lease"

            let large = Array.zeroCreate<byte> 9
            Expect.equal (OutgoingPackets.TrySend(peer, ReadOnlySpan<byte>(large), hostBudget, peerBudget))
                PacketSendResult.BudgetExceeded "byte budget enforced independently"
            Expect.equal hostBudget.Bytes 0L "global reservation rolled back"
            Expect.equal (send ()) PacketSendResult.Sent "capacity reusable"
            peer.Reset()
            Expect.equal (hostBudget.Packets, peerBudget.Bytes) (0, 0L) "reset destroys outstanding packet"
            Expect.equal (send ()) PacketSendResult.PeerRejected "disconnected peer rejected send"
            Expect.equal (hostBudget.Packets, peerBudget.Packets) (0, 0) "rejected packet freed once")

    testCase "realtime leaves native packet headroom at both host and peer admission" <| fun _ ->
        for hostLimit, peerLimit, delivery in [4, 16, PacketDelivery.Sequenced; 16, 4, PacketDelivery.Sequenced;
                                                4, 16, PacketDelivery.SequencedFragmented; 16, 4, PacketDelivery.SequencedFragmented] do
            withPeers (fun _ peer _ ->
                let hostBudget = PacketBudget(hostLimit, 64L)
                let peerBudget = PacketBudget(peerLimit, 64L)
                let bytes = [|1uy; 2uy; 3uy; 4uy|]
                let sample () = OutgoingPackets.TrySend(peer, ReadOnlySpan<byte>(bytes), hostBudget, peerBudget, 2uy, delivery)
                for _ in 1 .. 3 do Expect.equal (sample()) PacketSendResult.Sent "Realtime fits below reserved slot."
                Expect.equal (sample()) PacketSendResult.BudgetExceeded "Realtime cannot consume last reliable slot."
                Expect.equal (hostBudget.Packets, peerBudget.Packets) (3, 3) "Refused sample reserves neither budget."
                Expect.equal (OutgoingPackets.TrySend(peer, ReadOnlySpan<byte>(bytes), hostBudget, peerBudget))
                    PacketSendResult.Sent "Legacy four-argument send remains reliable and uses full capacity."
                Expect.equal (OutgoingPackets.TrySend(peer, ReadOnlySpan<byte>(bytes), hostBudget, peerBudget))
                    PacketSendResult.BudgetExceeded "Reliable also respects the original total cap."
                peer.Reset()
                Expect.equal (hostBudget.Packets, hostBudget.Bytes, peerBudget.Packets, peerBudget.Bytes)
                    (0, 0L, 0, 0L) "Reset releases mixed reliable and realtime leases exactly once.")

    testCase "realtime leaves native byte headroom independently of packet count" <| fun _ ->
        for hostBytes, peerBytes in [8L, 64L; 64L, 8L] do
            withPeers (fun _ peer _ ->
                let hostBudget = PacketBudget(16, hostBytes)
                let peerBudget = PacketBudget(16, peerBytes)
                let sample = [|1uy; 2uy; 3uy|]
                for _ in 1 .. 2 do
                    Expect.equal (OutgoingPackets.TrySend(peer, ReadOnlySpan<byte>(sample), hostBudget, peerBudget, 2uy, PacketDelivery.Sequenced))
                        PacketSendResult.Sent "Realtime may occupy three quarters of bytes."
                Expect.equal (OutgoingPackets.TrySend(peer, ReadOnlySpan<byte>([|1uy|]), hostBudget, peerBudget, 2uy, PacketDelivery.Sequenced))
                    PacketSendResult.BudgetExceeded "Realtime leaves reliable byte reserve."
                Expect.equal (OutgoingPackets.TrySend(peer, ReadOnlySpan<byte>([|4uy; 5uy|]), hostBudget, peerBudget))
                    PacketSendResult.Sent "Reliable fills remaining bytes without raising budget."
                Expect.equal (hostBudget.Bytes, peerBudget.Bytes) (8L, 8L) "Both budgets include actual retained payloads."
                peer.Reset()
                Expect.equal (hostBudget.Bytes, peerBudget.Bytes) (0L, 0L) "All bytes released.")

    testCase "configured native host headroom reserves several reliable packet slots" <| fun _ ->
        withPeers (fun _ peer _ ->
            let hostBudget = PacketBudget(4, 64L, 2)
            let peerBudget = PacketBudget(16, 64L)
            let bytes = [|1uy|]
            for _ in 1 .. 2 do
                Expect.equal (OutgoingPackets.TrySend(peer, ReadOnlySpan<byte>(bytes), hostBudget, peerBudget, 2uy, PacketDelivery.Sequenced))
                    PacketSendResult.Sent "Realtime below configured headroom."
            Expect.equal (OutgoingPackets.TrySend(peer, ReadOnlySpan<byte>(bytes), hostBudget, peerBudget, 2uy, PacketDelivery.Sequenced))
                PacketSendResult.BudgetExceeded "Host leaves both reliable slots available."
            for _ in 1 .. 2 do
                Expect.equal (OutgoingPackets.TrySend(peer, ReadOnlySpan<byte>(bytes), hostBudget, peerBudget))
                    PacketSendResult.Sent "Reliable uses reserved slot."
            peer.Reset()
            Expect.equal (hostBudget.Packets, peerBudget.Packets) (0, 0) "All leases released.")

    testCase "disposing a host releases outstanding outgoing packet leases" <| fun _ ->
        let hostBudget = PacketBudget(1, 16L)
        let peerBudget = PacketBudget(2, 16L)
        withPeers (fun _ peer _ ->
            let bytes = [|42uy|]
            Expect.equal (OutgoingPackets.TrySend(peer, ReadOnlySpan<byte>(bytes), hostBudget, peerBudget)) PacketSendResult.Sent "sent"
            Expect.equal (OutgoingPackets.TrySend(peer, ReadOnlySpan<byte>(bytes), hostBudget, peerBudget))
                PacketSendResult.BudgetExceeded "global packet cap applies across peer budget"
            Expect.equal hostBudget.Packets 1 "packet still owned by ENet before disposal")
        Expect.equal (hostBudget.Packets, peerBudget.Bytes) (0, 0L) "host destruction settled both budgets"

    testCase "adapter copies incoming packets and bounds sends until ENet frees them" <| fun _ ->
        let settings = { ServerConfig.defaults with MaxOutgoingPacketsPerPeer = 1; MaxOutgoingPackets = 2 }
        withAdapter settings (fun _ transport _ peer connection events packets pump ->
            let bytes = [|10uy; 20uy; 30uy|]
            let mutable packet = EnetPacket.Create(ReadOnlySpan<byte>(bytes), EnetPacketFlag.Reliable)
            try Expect.isTrue (peer.TrySend(0uy, &packet)) "client send"
            finally if packet.IsCreated then packet.Dispose()
            until (fun () -> events |> Seq.exists (function ServerTransportEvent.Received _ -> true | _ -> false)) pump
            let received = events |> Seq.pick (function ServerTransportEvent.Received(id, lane, payload) -> Some(id, lane, payload) | _ -> None)
            Expect.equal received (connection, DeliveryLane.Control, bytes) "detached exact payload"

            transport.Send(connection, { Schedule = PacketSchedule.Ordered; Lane = DeliveryLane.Control; Bytes = bytes }) |> ok
            Expect.isError (transport.Send(connection, { Schedule = PacketSchedule.Ordered; Lane = DeliveryLane.Control; Bytes = bytes })) "unacknowledged send still consumes capacity"
            until (fun () -> packets.Count = 1) pump
            let mutable admitted = false
            until (fun () -> admitted) (fun () ->
                pump ()
                admitted <- Result.isOk (transport.Send(connection, { Schedule = PacketSchedule.Ordered; Lane = DeliveryLane.Control; Bytes = bytes })))
            Expect.isTrue admitted "ACK restores budget"
            transport.Close connection
            Expect.isError (transport.Send(connection, { Schedule = PacketSchedule.Ordered; Lane = DeliveryLane.Control; Bytes = bytes })) "closed connection never reaches reused peer")

    testCase "graceful close delivers the queued reliable reply before disconnect" <| fun _ ->
        withAdapter ServerConfig.defaults (fun _ transport _ _ connection events packets pump ->
            let reply = [|11uy; 22uy; 33uy|]
            transport.Send(connection, { Schedule = PacketSchedule.Ordered; Lane = DeliveryLane.Control; Bytes = reply }) |> ok
            transport.Close connection
            Expect.isError (transport.Send(connection, { Schedule = PacketSchedule.Ordered; Lane = DeliveryLane.Control; Bytes = reply })) "closing route rejects new sends"
            until (fun () -> events.Contains(ServerTransportEvent.Disconnected connection)) pump
            Expect.equal (List.ofSeq packets) [reply] "already queued reply survived close")

    testCase "reused ENet peer slot receives a fresh connection identity" <| fun _ ->
        withAdapter ServerConfig.defaults (fun settings transport client _ connection events packets pump ->
            transport.Close connection
            until (fun () -> events.Contains(ServerTransportEvent.Disconnected connection)) pump
            let mutable replacement = Unchecked.defaultof<EnetPeer>
            Expect.isTrue (client.TryConnect(address settings.Port, 5un, 0u, &replacement)) "reconnect"
            until (fun () -> events |> Seq.filter (function ServerTransportEvent.Connected _ -> true | _ -> false) |> Seq.length = 2) pump
            let next = events |> Seq.pick (function ServerTransportEvent.Connected(id, _) when id <> connection -> Some id | _ -> None)
            transport.Close connection
            transport.Reset connection
            Expect.isError (transport.Send(connection, { Schedule = PacketSchedule.Ordered; Lane = DeliveryLane.Control; Bytes = [|1uy|] })) "old route stays invalid"
            transport.Send(next, { Schedule = PacketSchedule.Ordered; Lane = DeliveryLane.Control; Bytes = [|2uy|] }) |> ok
            until (fun () -> packets.Count = 1) pump
            Expect.equal packets[0] [|2uy|] "old close/reset cannot affect replacement")

    testCase "unreliable application packets are rejected at the transport boundary" <| fun _ ->
        withAdapter ServerConfig.defaults (fun _ transport _ peer connection events _ pump ->
            let mutable packet = EnetPacket.Create(ReadOnlySpan<byte>([|1uy|]), enum<EnetPacketFlag> 0)
            try Expect.isTrue (peer.TrySend(0uy, &packet)) "send unreliable packet"
            finally if packet.IsCreated then packet.Dispose()
            until (fun () -> events.Contains(ServerTransportEvent.Disconnected connection)) pump
            Expect.isFalse (events |> Seq.exists (function ServerTransportEvent.Received _ -> true | _ -> false)) "unreliable packet not dispatched"
            Expect.isError (transport.Send(connection, { Schedule = PacketSchedule.Ordered; Lane = DeliveryLane.Control; Bytes = [|1uy|] })) "route reset")

    testCase "chat and realtime use independent channels without reliable-only rejection" <| fun _ ->
        withAdapter ServerConfig.defaults (fun _ transport _ peer connection events _ pump ->
            for lane in [DeliveryLane.Chat; DeliveryLane.Realtime] do
                let flags = if lane = DeliveryLane.Realtime then enum<EnetPacketFlag> 0 else EnetPacketFlag.Reliable
                let mutable packet = EnetPacket.Create(ReadOnlySpan<byte>([|DeliveryLane.toChannel lane|]), flags)
                try Expect.isTrue (peer.TrySend(DeliveryLane.toChannel lane, &packet)) "send on agreed channel"
                finally if packet.IsCreated then packet.Dispose()
                until (fun () -> events |> Seq.exists (function ServerTransportEvent.Received(_, actual, _) -> actual = lane | _ -> false)) pump
            Expect.isFalse (events.Contains(ServerTransportEvent.Disconnected connection)) "supported channels retain connection"
            let oversized = Array.zeroCreate<byte> (transport.MaxUnfragmentedPayloadBytes connection + 1)
            Expect.isError (transport.Send(connection, { Schedule = PacketSchedule.Ordered; Lane = DeliveryLane.Realtime; Bytes = oversized })) "realtime never fragments"
            transport.Send(connection, { Schedule = PacketSchedule.Ordered; Lane = DeliveryLane.Control; Bytes = oversized }) |> ok)

    testCase "a peer negotiating fewer than five channels is not admitted" <| fun _ ->
        let settings = { ServerConfig.defaults with Port = freePort (); ServiceTimeoutMs = 0u }
        let transport = EnetTransport.createInline settings NullLogger.Instance |> ok
        try
            use client = EnetHost.Create(Unchecked.defaultof<enet.ENetAddress>, 1un, 2un, 0u, 0u, EnetHostOption.Ipv4)
            let mutable peer = Unchecked.defaultof<EnetPeer>
            Expect.isTrue (client.TryConnect(address settings.Port, 2un, 0u, &peer)) "connect old peer"
            let events = ResizeArray<ServerTransportEvent>()
            let mutable stopped = false
            let pump () =
                events.AddRange(transport.Poll() |> ok)
                service client (fun event ->
                    if event.Type = EnetEventType.Disconnect then stopped <- true
                    discard event)
            until (fun () -> stopped) pump
            Expect.equal events.Count 0 "incompatible peer is never visible to the application"
        finally transport.Dispose()

    testCase "invalid outgoing budgets fail the settings check before any host exists" <| fun _ ->
        for settings in [
            { ServerConfig.defaults with MaxOutgoingPacketsPerPeer = 0 }
            { ServerConfig.defaults with MaxOutgoingPackets = 1 }
            { ServerConfig.defaults with MaxOutgoingBytesPerPeer = 1 }
            { ServerConfig.defaults with MaxOutgoingBytes = 1 }
        ] do
            Expect.isError (ServerConfig.validate settings) "invalid transport config"
]
