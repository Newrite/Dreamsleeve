// Handle every defined transport enum; unknown numeric values are a transport error.
#nowarn "104"

namespace Dreamsleeve.Server.Infrastructure

open System
open System.Collections.Generic
open System.Net
open System.Net.Sockets
open Microsoft.Extensions.Logging
open Enet
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Infrastructure.Interop

/// How a failed ENet pump is judged. ENet gives up the whole service call when a
/// single send fails, although the host socket is fine: after a VPN switch or a
/// lost route, sendto to one player fails with HostUnreachable. Such failures pass,
/// and that player times out on ENet's own schedule; only an unknown failure, or
/// failing without a single healthy pump for MaxFailingMs, breaks the transport.
[<RequireQualifiedAccess>]
module PumpHealth =
    /// Passing failures are summarized in the log at most this often.
    [<Literal>]
    let ReportIntervalMs = 5000L

    /// Failing on every pump for this long means the socket itself is unusable.
    [<Literal>]
    let MaxFailingMs = 60000L

    /// Errors about one destination or a passing network change; the host keeps its socket.
    let transient error =
        match error with
        | SocketError.HostUnreachable | SocketError.NetworkUnreachable | SocketError.HostDown
        | SocketError.NetworkDown | SocketError.NetworkReset | SocketError.ConnectionReset
        | SocketError.ConnectionRefused | SocketError.ConnectionAborted | SocketError.TimedOut
        | SocketError.AddressNotAvailable | SocketError.AccessDenied | SocketError.NoBufferSpaceAvailable
        | SocketError.MessageSize | SocketError.TryAgain -> true
        | _ -> false

    type State = {
        mutable FailingSince: int64 voption
        /// Failures since the last report.
        mutable Unreported: int
        mutable LastError: SocketError
        mutable LastReport: int64 voption
    }

    [<RequireQualifiedAccess>]
    type Verdict =
        | Healthy
        | Passing
        /// Passing failures to log: how many since the last report, and the latest error.
        | Report of failures: int * last: SocketError
        | Broken of persistent: bool

    let create () = { FailingSince = ValueNone; Unreported = 0; LastError = SocketError.Success; LastReport = ValueNone }

    let private due state now =
        state.Unreported > 0 && state.LastReport |> ValueOption.forall (fun last -> now - last >= ReportIntervalMs)

    let private report state now =
        let failures = state.Unreported
        state.Unreported <- 0
        state.LastReport <- ValueSome now
        Verdict.Report(failures, state.LastError)

    /// One pump at now (milliseconds of a monotonic clock): its result and the socket error.
    let observe state now result error =
        if result >= 0 then
            state.FailingSince <- ValueNone
            if due state now then report state now else Verdict.Healthy
        elif not (transient error) then Verdict.Broken false
        else
            if state.FailingSince.IsNone then state.FailingSince <- ValueSome now
            state.Unreported <- state.Unreported + 1
            state.LastError <- error
            if now - state.FailingSince.Value >= MaxFailingMs then Verdict.Broken true
            elif due state now then report state now
            else Verdict.Passing

/// Raw ENet state stays on its owner. Production create supplies a dedicated thread.
[<RequireQualifiedAccess>]
module EnetTransport =
    type private Connection = {
        Id: Guid
        Peer: EnetPeer
        ConnectId: uint32
        Outgoing: PacketBudget
        mutable Closing: bool
        mutable PoseReceivedAt: int64
        mutable PoseSamples: double
    }

    type private State = {
        Diagnostics: TransportDiagnostics
        Config: ServerConfig
        Phantoms: PhantomOptions
        Logger: ILogger
        Pump: PumpHealth.State
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
        if peer.ChannelCount < unativeint ServerConfig.MinChannelLimit then
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
                PoseReceivedAt = Environment.TickCount64
                PoseSamples = 2.0
            }
            state.Connections.Add(connection.Id, connection)
            state.Slots.Add(peer.IncomingPeerId, connection)
            let mutable address: IPAddress = null
            let known = peer.Address.ToIpAddress(&address) = SocketError.Success && not (isNull address)
            events.Add(ServerTransportEvent.Connected(connection.Id, if known then address else IPAddress.IPv6None))

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
            let lane = enum<DeliveryLane>(int event.ChannelId)
            let time = Environment.TickCount64
            if lane = DeliveryLane.Poses then
                connection.PoseSamples <- min 2.0 (connection.PoseSamples + double (time - connection.PoseReceivedAt) / double state.Phantoms.PoseIntervalMs)
                connection.PoseReceivedAt <- time
            // The only reliable Poses packet is ENet's empty epoch marker at
            // unreliable sequence rollover. It never reaches the domain codec.
            if lane = DeliveryLane.Poses && reliable && not unsequenced && packet.DataLength = 0un then ()
            elif event.ChannelId > byte DeliveryLane.Poses || unsequenced
               || (event.ChannelId <= byte DeliveryLane.Poses && LanePolicy.reliable lane <> reliable)
               || packet.DataLength > unativeint state.Config.MaxPacketBytes
               || (lane = DeliveryLane.Models && packet.DataLength > unativeint PhantomAssetLimits.assetPacketBytes)
               || (lane = DeliveryLane.Poses && packet.DataLength > unativeint (PhantomAssetLimits.posePacketBytes state.Phantoms.Limits))
               || (event.ChannelId = byte DeliveryLane.Realtime
                   && packet.DataLength > unativeint (OutgoingPackets.GetUnfragmentedPayloadBytes connection.Peer)) then
                reset state connection.Id
                events.Add(ServerTransportEvent.Disconnected connection.Id)
            elif lane = DeliveryLane.Poses && connection.PoseSamples < 1.0 then ()
            else
                if lane = DeliveryLane.Poses then connection.PoseSamples <- connection.PoseSamples - 1.0
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
                match PumpHealth.observe state.Pump Environment.TickCount64 pumpResult socketError with
                | PumpHealth.Verdict.Healthy | PumpHealth.Verdict.Passing -> None
                | PumpHealth.Verdict.Report(failures, last) ->
                    state.Logger.LogWarning(
                        "ENet could not send to some peers: {Failures} failed pumps since the last report, last socket error {SocketError} ({Code}); unreachable players time out on their own",
                        failures, last, int last)
                    None
                | PumpHealth.Verdict.Broken persistent ->
                    let failing = if persistent then $"on every pump for {PumpHealth.MaxFailingMs / 1000L} s" else $"(result {pumpResult})"
                    Some $"ENet protocol pump failed {failing}; last socket error: {socketError}, code {int socketError}; active peers: {state.Connections.Count}."

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
                let delivery =
                    match LanePolicy.reliability packet.Lane with
                    | LaneReliability.Reliable when LanePolicy.bulk (LanePolicy.admissionLane packet) -> PacketDelivery.ReliableBulk
                    | LaneReliability.Reliable -> PacketDelivery.Reliable
                    | LaneReliability.Sequenced -> PacketDelivery.Sequenced
                    | LaneReliability.SequencedFragmented -> PacketDelivery.SequencedFragmented
                let result = OutgoingPackets.TrySend(connection.Peer, ReadOnlySpan<byte>(bytes), state.Outgoing, connection.Outgoing, byte packet.Lane, delivery)
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

    let private allocate phantoms config (logger: ILogger) =
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
                    // Linux silently caps the sizes at net.core.rmem_max/wmem_max and
                    // reports double what it grants; Windows grants them as asked.
                    let granted value = if OperatingSystem.IsLinux() then value / 2 else value
                    let struct (receive, sent) = TransportDiagnostics.ReadBuffers host
                    let receive, sent = granted receive, granted sent
                    logger.LogInformation("ENet UDP socket buffers: receive {Receive} bytes, send {Send} bytes", receive, sent)
                    if receive < config.ReceiveBufferBytes || sent < config.SendBufferBytes then
                        logger.LogWarning(
                            "The system granted smaller UDP socket buffers than Server.ReceiveBufferBytes {Receive} / SendBufferBytes {Send}; on Linux raise net.core.rmem_max and net.core.wmem_max",
                            config.ReceiveBufferBytes, config.SendBufferBytes)
                    // Checksums and compression stay disabled, matching the native client.
                    host.SetMaximumPacketSize(unativeint config.MaxPacketBytes)
                    host.SetMaximumWaitingData(unativeint config.MaxWaitingData)

                    let state = {
                        Diagnostics = TransportDiagnostics()
                        Config = config
                        Phantoms = phantoms
                        Logger = logger
                        Pump = PumpHealth.create ()
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
    let createInline config logger = allocate PhantomOptions.defaults config logger

    let create config logger = TransportOwner.create config (fun () -> allocate PhantomOptions.defaults config logger)

    let createWithPhantoms config phantoms logger = TransportOwner.create config (fun () -> allocate phantoms config logger)
