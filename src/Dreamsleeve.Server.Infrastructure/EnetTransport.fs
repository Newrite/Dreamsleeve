// Handle every defined transport enum; unknown numeric values are a transport error.
#nowarn "104"

namespace Dreamsleeve.Server.Infrastructure

open System
open System.Collections.Generic
open System.Net.Sockets
open Enet
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Infrastructure.Interop

/// Raw ENet state stays on its owner. Production create supplies a dedicated thread.
[<RequireQualifiedAccess>]
module EnetTransport =
    type private Connection = {
        Id: Guid
        Peer: EnetPeer
        ConnectId: uint32
        Outgoing: PacketBudget
        mutable Closing: bool
    }

    type private State = {
        Diagnostics: TransportDiagnostics
        Config: ServerConfig
        Host: EnetHost
        Connections: Dictionary<Guid, Connection>
        Slots: Dictionary<uint16, Connection>
        Outgoing: PacketBudget
        mutable Disposed: bool
    }

    let private remove state connection =
        state.Connections.Remove connection.Id |> ignore
        state.Slots.Remove connection.Peer.IncomingPeerId |> ignore

    let private close state connectionId =
        match state.Connections.TryGetValue connectionId with
        | true, connection when not state.Disposed && not connection.Closing ->
            connection.Closing <- true
            // Keep servicing until ENet acknowledges queued reliable packets and
            // emits Disconnect. The runtime owns the deadline and may call Reset.
            connection.Peer.DisconnectLater(0u)
        | true, _ | false, _ -> ()

    let private reset state connectionId =
        match state.Connections.TryGetValue connectionId with
        | true, connection when not state.Disposed ->
            remove state connection
            connection.Peer.DisconnectNow(0u)
        | true, _ | false, _ -> ()

    let private connected state (events: ResizeArray<ServerTransportEvent>) (peer: EnetPeer) =
        if peer.ChannelCount < 3un then
            peer.DisconnectNow(0u)
        else
            match state.Slots.TryGetValue peer.IncomingPeerId with
            | true, previous ->
                remove state previous
                events.Add(ServerTransportEvent.Disconnected previous.Id)
            | false, _ -> ()

            let connection = {
                Id = Guid.NewGuid()
                Peer = peer
                ConnectId = peer.ConnectId
                Outgoing = PacketBudget(state.Config.MaxOutgoingPacketsPerPeer, int64 state.Config.MaxOutgoingBytesPerPeer)
                Closing = false
            }
            state.Connections.Add(connection.Id, connection)
            state.Slots.Add(peer.IncomingPeerId, connection)
            events.Add(ServerTransportEvent.Connected connection.Id)

    let private disconnected state (events: ResizeArray<ServerTransportEvent>) (peer: EnetPeer) =
        // ENet may reset connectID before delivering Disconnect; the slot remains valid.
        match state.Slots.TryGetValue peer.IncomingPeerId with
        | true, connection ->
            remove state connection
            events.Add(ServerTransportEvent.Disconnected connection.Id)
        | false, _ -> ()

    let private received state (events: ResizeArray<ServerTransportEvent>) (event: EnetEvent) =
        use packet = event.Packet

        match state.Slots.TryGetValue event.Peer.IncomingPeerId with
        | true, connection when connection.ConnectId = event.Peer.ConnectId && not connection.Closing ->
            let reliable = (packet.Flags &&& EnetPacketFlag.Reliable) = EnetPacketFlag.Reliable
            let unsequenced = (packet.Flags &&& EnetPacketFlag.Unsequenced) = EnetPacketFlag.Unsequenced
            if event.ChannelId > byte DeliveryLane.Realtime || unsequenced
               || (event.ChannelId <> byte DeliveryLane.Realtime && not reliable)
               || packet.DataLength > unativeint state.Config.MaxPacketBytes
               || (event.ChannelId = byte DeliveryLane.Realtime
                   && packet.DataLength > unativeint (OutgoingPackets.GetUnfragmentedPayloadBytes connection.Peer)) then
                reset state connection.Id
                events.Add(ServerTransportEvent.Disconnected connection.Id)
            else
                events.Add(ServerTransportEvent.Received(connection.Id, enum<DeliveryLane>(int event.ChannelId), packet.AsSpan().ToArray()))
        | true, _ | false, _ -> ()

    let private poll state () =
        if state.Disposed then
            Error "ENet transport is disposed."
        else
            let started = state.Diagnostics.BeginPoll()
            let events = ResizeArray<ServerTransportEvent>()
            let mutable remaining = state.Config.EventBudget
            let mutable socketError = SocketError.Success
            let pumpResult = EnetPump.Service(state.Host, &socketError)
            let mutable error =
                if pumpResult < 0 then
                    Some $"ENet protocol pump failed (result {pumpResult}; last socket error: {socketError}, code {int socketError}; active peers: {state.Connections.Count})."
                else None

            while remaining > 0 && error.IsNone do
                let mutable event = Unchecked.defaultof<EnetEvent>
                let result = state.Host.CheckEvents(&event)

                if result < 0 then
                    error <- Some "ENet service failed."
                elif result = 0 then
                    remaining <- 0
                else
                    remaining <- remaining - 1
                    match event.Type with
                    | EnetEventType.Connect -> connected state events event.Peer
                    | EnetEventType.Disconnect -> disconnected state events event.Peer
                    | EnetEventType.Receive -> received state events event
                    | EnetEventType.None -> ()
                    | unknown when not (Enum.IsDefined unknown) ->
                        error <- Some "ENet returned an unknown event type."

            state.Diagnostics.EndPoll(started, events.Count, state.Host, state.Outgoing)

            match error with
            | Some failure -> Error failure
            | None -> Ok (List.ofSeq events)

    let private maxUnfragmentedPayloadBytes state connectionId =
        if state.Disposed then 0
        else
            match state.Connections.TryGetValue connectionId with
            | true, connection when not connection.Closing ->
                OutgoingPackets.GetUnfragmentedPayloadBytes connection.Peer
            | true, _ | false, _ -> 0

    let private send state (connectionId, packet: TransportPacket) =
        let bytes = packet.Bytes
        if state.Disposed then
            Error "ENet transport is disposed."
        elif not (Enum.IsDefined packet.Lane) then Error "Invalid delivery lane."
        elif isNull bytes || bytes.Length = 0 || bytes.Length > state.Config.MaxPacketBytes then
            Error "Outgoing packet size is outside the configured limits."
        else
            match state.Connections.TryGetValue connectionId with
            | false, _ -> Error "Connection is closed."
            | true, connection when connection.Closing -> Error "Connection is closing."
            | true, connection when packet.Lane = DeliveryLane.Realtime && bytes.Length > OutgoingPackets.GetUnfragmentedPayloadBytes connection.Peer ->
                Error "Realtime payload exceeds negotiated MTU."
            | true, connection ->
                let started = TransportDiagnostics.BeginSend()
                let result = OutgoingPackets.TrySend(connection.Peer, ReadOnlySpan<byte>(bytes), state.Outgoing, connection.Outgoing, byte packet.Lane, packet.Lane <> DeliveryLane.Realtime)
                TransportDiagnostics.EndSend(started, bytes.Length)
                match result with
                | PacketSendResult.Sent ->
                    TransportDiagnostics.RecordAcceptedPacket(connection.Peer, bytes.Length)
                    Ok ()
                | PacketSendResult.BudgetExceeded -> Error "Outgoing ENet packet budget exceeded."
                | PacketSendResult.PeerRejected -> Error "ENet peer rejected the outgoing packet."
                | unknown when not (Enum.IsDefined unknown) -> Error "Unknown ENet packet admission result."

    let private dispose state () =
        if not state.Disposed then
            state.Disposed <- true
            try
                // Graceful shutdown has no remaining peers. On fault/Abort notify
                // any still tracked peers before freeing their queued packets.
                for connection in state.Connections.Values do
                    try connection.Peer.DisconnectNow(0u) with _ -> ()
            finally
                state.Connections.Clear()
                state.Slots.Clear()
                try
                    state.Host.Dispose()
                finally
                    enet.ENET_API.enet_deinitialize()

    let private allocate config =
        let mutable address = Unchecked.defaultof<enet.ENetAddress>
        let resolved = enet.ENetAddress.FromIpAddress(config.BindAddress, config.Port, &address)

        if resolved <> SocketError.Success then
            Error (sprintf "ENet bind address is invalid: %A." resolved)
        elif enet.ENET_API.enet_initialize() <> 0 then
            Error "ENet initialization failed."
        else
            try
                let host = EnetHost.Create(address, unativeint config.PeerLimit, unativeint config.ChannelLimit,
                                           0u, 0u, EnetHostOption.Ipv4)
                if not (TransportDiagnostics.ConfigureBuffers(host, config.ReceiveBufferBytes, config.SendBufferBytes)) then
                    host.Dispose()
                    enet.ENET_API.enet_deinitialize()
                    Error "Could not configure ENet UDP socket buffers."
                else
                    // Checksums and compression stay disabled, matching the native client.
                    host.SetMaximumPacketSize(unativeint config.MaxPacketBytes)
                    host.SetMaximumWaitingData(unativeint config.MaxWaitingData)

                    let state = {
                        Diagnostics = TransportDiagnostics()
                        Config = config
                        Host = host
                        Connections = Dictionary()
                        Slots = Dictionary()
                        Outgoing = PacketBudget(config.MaxOutgoingPackets, int64 config.MaxOutgoingBytes, max 1 (min config.PeerLimit (max 1 (config.MaxOutgoingPackets / 8))))
                        Disposed = false
                    }
                    Ok {
                        Poll = poll state
                        SetReadyHandler = ignore
                        Send = send state
                        MaxUnfragmentedPayloadBytes = maxUnfragmentedPayloadBytes state
                        Close = close state
                        Reset = reset state
                        Dispose = dispose state
                    }
            with
            | :? SocketException as error ->
                enet.ENET_API.enet_deinitialize()
                Error (sprintf "ENet host creation failed: %s" error.Message)
            | :? ArgumentException as error ->
                enet.ENET_API.enet_deinitialize()
                Error (sprintf "ENet host configuration failed: %s" error.Message)

    /// The settings come checked by GameSettings.create.
    let createInline config = allocate config

    let create config = TransportOwner.create config (fun () -> allocate config)
