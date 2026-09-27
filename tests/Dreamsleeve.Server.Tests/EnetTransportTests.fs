module Dreamsleeve.Server.Tests.EnetTransportTests

open System
open System.Diagnostics
open System.Net
open System.Net.Sockets
open System.Threading
open Enet
open Expecto
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

let private withPeers run =
    Expect.equal (enet.ENET_API.enet_initialize()) 0 "initialize"
    try
        use server = EnetHost.Create(address 0us, 1un, 2un, 0u, 0u, EnetHostOption.Ipv4)
        use client = EnetHost.Create(Unchecked.defaultof<enet.ENetAddress>, 1un, 2un, 0u, 0u, EnetHostOption.Ipv4)
        let mutable clientPeer = Unchecked.defaultof<EnetPeer>
        Expect.isTrue (client.TryConnect(server.Address, 2un, 0u, &clientPeer)) "begin connect"
        let mutable serverPeer = None
        let mutable clientReady = false
        let pump () =
            service server (fun event ->
                if event.Type = EnetEventType.Connect then serverPeer <- Some event.Peer
                discard event)
            service client (fun event ->
                if event.Type = EnetEventType.Connect then clientReady <- true
                discard event)
        until (fun () -> serverPeer.IsSome && clientReady) pump
        run server serverPeer.Value pump
    finally
        enet.ENET_API.enet_deinitialize()

let private withAdapter settings run =
    let config = { settings with Port = freePort (); ServiceTimeoutMs = 0u }
    let transport = EnetTransport.create config |> ok
    try
        use client = EnetHost.Create(Unchecked.defaultof<enet.ENetAddress>, 1un, 2un, 0u, 0u, EnetHostOption.Ipv4)
        let mutable peer = Unchecked.defaultof<EnetPeer>
        Expect.isTrue (client.TryConnect(address config.Port, 2un, 0u, &peer)) "begin connect"
        let events = ResizeArray<ServerTransportEvent>()
        let packets = ResizeArray<byte array>()
        let pump () =
            events.AddRange(transport.Poll() |> ok)
            service client (fun event ->
                if event.Type = EnetEventType.Receive then
                    use packet = event.Packet
                    packets.Add(packet.AsSpan().ToArray()))
        until (fun () -> events |> Seq.exists (function ServerTransportEvent.Connected _ -> true | _ -> false)) pump
        let connection = events |> Seq.pick (function ServerTransportEvent.Connected id -> Some id | _ -> None)
        run config transport client peer connection events packets pump
    finally
        transport.Dispose()

let tests = testSequenced <| testList "ENet transport" [
    testCase "socket buffer readback borrows the handle and leaves the host usable" <| fun _ ->
        withPeers (fun host peer pump ->
            Expect.isTrue (TransportDiagnostics.ConfigureBuffers(host, 1024 * 1024, 1024 * 1024)) "socket options applied"
            let struct (receive, send) = TransportDiagnostics.ReadBuffers host
            Expect.isGreaterThanOrEqual receive (1024 * 1024) "receive buffer granted"
            Expect.isGreaterThanOrEqual send (1024 * 1024) "send buffer granted"
            let budget = PacketBudget(2, 1024L)
            let peerBudget = PacketBudget(2, 1024L)
            Expect.equal (OutgoingPackets.TrySend(peer, ReadOnlySpan<byte>([|1uy|]), budget, peerBudget))
                PacketSendResult.Sent "borrowed socket was not closed"
            until (fun () -> budget.Packets = 0) pump)

    testCase "invalid socket buffers are rejected before host allocation" <| fun _ ->
        Expect.isError (EnetTransport.create { ServerConfig.defaults with ReceiveBufferBytes = 0 }) "invalid receive buffer"
        Expect.isError (EnetTransport.create { ServerConfig.defaults with SendBufferBytes = -1 }) "invalid send buffer"

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
            let received = events |> Seq.pick (function ServerTransportEvent.Received(id, payload) -> Some(id, payload) | _ -> None)
            Expect.equal received (connection, bytes) "detached exact payload"

            transport.Send(connection, bytes) |> ok
            Expect.isError (transport.Send(connection, bytes)) "unacknowledged send still consumes capacity"
            until (fun () -> packets.Count = 1) pump
            let mutable admitted = false
            until (fun () -> admitted) (fun () ->
                pump ()
                admitted <- Result.isOk (transport.Send(connection, bytes)))
            Expect.isTrue admitted "ACK restores budget"
            transport.Close connection
            Expect.isError (transport.Send(connection, bytes)) "closed connection never reaches reused peer")

    testCase "graceful close delivers the queued reliable reply before disconnect" <| fun _ ->
        withAdapter ServerConfig.defaults (fun _ transport _ _ connection events packets pump ->
            let reply = [|11uy; 22uy; 33uy|]
            transport.Send(connection, reply) |> ok
            transport.Close connection
            Expect.isError (transport.Send(connection, reply)) "closing route rejects new sends"
            until (fun () -> events.Contains(ServerTransportEvent.Disconnected connection)) pump
            Expect.equal (List.ofSeq packets) [reply] "already queued reply survived close")

    testCase "reused ENet peer slot receives a fresh connection identity" <| fun _ ->
        withAdapter ServerConfig.defaults (fun settings transport client _ connection events packets pump ->
            transport.Close connection
            until (fun () -> events.Contains(ServerTransportEvent.Disconnected connection)) pump
            let mutable replacement = Unchecked.defaultof<EnetPeer>
            Expect.isTrue (client.TryConnect(address settings.Port, 2un, 0u, &replacement)) "reconnect"
            until (fun () -> events |> Seq.filter (function ServerTransportEvent.Connected _ -> true | _ -> false) |> Seq.length = 2) pump
            let next = events |> Seq.pick (function ServerTransportEvent.Connected id when id <> connection -> Some id | _ -> None)
            transport.Close connection
            transport.Reset connection
            Expect.isError (transport.Send(connection, [|1uy|])) "old route stays invalid"
            transport.Send(next, [|2uy|]) |> ok
            until (fun () -> packets.Count = 1) pump
            Expect.equal packets[0] [|2uy|] "old close/reset cannot affect replacement")

    testCase "unreliable application packets are rejected at the transport boundary" <| fun _ ->
        withAdapter ServerConfig.defaults (fun _ transport _ peer connection events _ pump ->
            let mutable packet = EnetPacket.Create(ReadOnlySpan<byte>([|1uy|]), enum<EnetPacketFlag> 0)
            try Expect.isTrue (peer.TrySend(0uy, &packet)) "send unreliable packet"
            finally if packet.IsCreated then packet.Dispose()
            until (fun () -> events.Contains(ServerTransportEvent.Disconnected connection)) pump
            Expect.isFalse (events |> Seq.exists (function ServerTransportEvent.Received _ -> true | _ -> false)) "unreliable packet not dispatched"
            Expect.isError (transport.Send(connection, [|1uy|])) "route reset")

    testCase "wrong ENet channel disconnects the connection instead of entering the application" <| fun _ ->
        withAdapter ServerConfig.defaults (fun _ transport _ peer connection events _ pump ->
            let mutable packet = EnetPacket.Create(ReadOnlySpan<byte>([|1uy|]), EnetPacketFlag.Reliable)
            try Expect.isTrue (peer.TrySend(1uy, &packet)) "send wrong channel"
            finally if packet.IsCreated then packet.Dispose()
            until (fun () -> events.Contains(ServerTransportEvent.Disconnected connection)) pump
            Expect.isFalse (events |> Seq.exists (function ServerTransportEvent.Received _ -> true | _ -> false)) "packet not dispatched"
            Expect.isError (transport.Send(connection, [|1uy|])) "route removed")

    testCase "invalid outgoing budgets fail before host allocation" <| fun _ ->
        for settings in [
            { ServerConfig.defaults with MaxOutgoingPacketsPerPeer = 0 }
            { ServerConfig.defaults with MaxOutgoingPackets = 1 }
            { ServerConfig.defaults with MaxOutgoingBytesPerPeer = 1 }
            { ServerConfig.defaults with MaxOutgoingBytes = 1 }
        ] do
            Expect.isError (EnetTransport.create settings) "invalid transport config"
]
