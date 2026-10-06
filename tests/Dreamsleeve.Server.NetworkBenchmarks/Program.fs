// Benchmark process only. One owner services independent peers across explicitly configured UDP sockets.
#nowarn "104"
module Dreamsleeve.Server.NetworkBenchmarks.Program

open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Net
open System.Net.Sockets
open System.Net.Http
open System.Net.Http.Json
open System.Text.Json
open Enet
open Google.Protobuf
open Dreamsleeve.Protocol.Chat
open Dreamsleeve.Server.Infrastructure.Interop

type private Options = { AuthUrl: Uri; Port: uint16; Clients: int; Hosts: int; Seconds: float; Rate: float; ReplicationMs: int; ActorValuesHz: float; Scenario: string; Output: string }

type private Client = {
    Index: int
    HostIndex: int
    AccountId: uint64
    mutable SessionTicket: string
    mutable Peer: EnetPeer
    Budget: PacketBudget
    mutable StartedMs: float
    mutable ConnectedMs: float
    mutable ReadyMs: float
    mutable Ready: bool
    mutable Closed: bool
    mutable PlayerId: uint64
    mutable ChannelId: uint64
    mutable LastMessageId: uint64
    mutable Received: int
    Pending: Dictionary<uint64, int>
    Online: HashSet<uint64>
}

type private Submission = {
    Sender: int
    RequestId: uint64
    SentMs: float
    mutable MessageId: uint64
    mutable Received: int
    mutable AuthorAckMs: float
    mutable CompletedMs: float
}

type private State = {
    Group: Coordination.Group
    AllPlayerIds: uint64 array
    EventBudgetPerHost: int
    GlobalOffset: int
    mutable Movement: Movement.Probe option
    mutable Phantom: PhantomProbe.Probe option
    Options: Options
    Diagnostics: TransportDiagnostics array
    Hosts: EnetHost array
    Authentication: HttpClient
    RegistrationMs: float
    mutable LoginMs: float
    Clock: Stopwatch
    Prefix: string
    Clients: Client array
    Slots: Dictionary<struct (int * uint16), Client>
    Budget: PacketBudget
    Messages: ResizeArray<Submission>
    Errors: ResizeArray<string>
    mutable ErrorCount: int
    mutable ReadyCount: int
    mutable Disconnections: int
    mutable Rejections: int
    mutable Received: int64
    mutable SentChatPayloadBytes: int64
    mutable ReceivedChatPayloadBytes: int64
    mutable Completed: int
    mutable NextSender: int
    mutable Disconnecting: bool
    mutable PresenceConverged: bool
    mutable RampMs: float
    mutable LoadMs: float
    mutable DrainMs: float
    mutable BackpressuredMs: float
    mutable MaxInflight: int
    SentApplicationBytes: int64 array
    ReceivedApplicationBytes: int64 array
    mutable LoadTraffic: obj option
}

let private now state = state.Clock.Elapsed.TotalMilliseconds
let private stage name = printfn "STAGE %s" name; Console.Out.Flush()

let private protocolVersion = Dreamsleeve.Server.Core.ProtocolCodec.Version
let private password = "NetworkBench-Password-2026!"

let private authPost (http: HttpClient) (path: string) body =
    use response = http.PostAsJsonAsync(path, body).GetAwaiter().GetResult()
    if not response.IsSuccessStatusCode then
        invalidOp (sprintf "Benchmark authentication %s failed: HTTP %d" path (int response.StatusCode))
    // Never include the request or response body in diagnostics: both can contain credentials.
    let bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))

let private register (http: HttpClient) prefix index =
    use response = authPost http "auth/register" {|
        username = sprintf "%s%d" prefix index
        displayName = sprintf "Network bench %d" index
        password = password
    |}
    response.RootElement.GetProperty("playerId").GetUInt64()

let private login state client =
    let elapsed = Stopwatch.StartNew()
    use response = authPost state.Authentication "auth/login" {|
        username = sprintf "%s%d" state.Prefix client.Index
        password = password
    |}
    let ticket = response.RootElement.GetProperty("sessionTicket").GetString()
    if isNull ticket || ticket.Length <> 43 || response.RootElement.GetProperty("playerId").GetUInt64() <> client.AccountId then
        invalidOp "Authentication returned an invalid benchmark identity or ticket."
    ticket, elapsed.Elapsed.TotalMilliseconds

let private fail state message =
    state.ErrorCount <- state.ErrorCount + 1
    if state.Errors.Count < 64 then state.Errors.Add message

let private sendBytes state client channel reliable (bytes: byte array) =
    match OutgoingPackets.TrySend(client.Peer, ReadOnlySpan<byte>(bytes), state.Budget, client.Budget, channel, (if reliable then PacketDelivery.Reliable else PacketDelivery.Sequenced)) with
    | PacketSendResult.Sent -> state.SentApplicationBytes[int channel] <- state.SentApplicationBytes[int channel] + int64 bytes.Length; true
    | failure ->
        fail state (sprintf "Client %d packet admission failed: %A" client.Index failure)
        false

let private send state client (message: ClientPacket) =
    let channel = if message.PayloadCase = ClientPacket.PayloadOneofCase.SendChat then 1uy else 0uy
    sendBytes state client channel true (message.ToByteArray())

let private sendMovement state client (message: ClientMovementPacket) =
    sendBytes state client 2uy false (message.ToByteArray())

let private connected state client =
    client.ConnectedMs <- now state
    let request = ClientPacket(ProtocolVersion = protocolVersion, RequestId = 1UL,
                               OpenSession = OpenSession(SessionTicket = client.SessionTicket))
    send state client request |> ignore
    client.SessionTicket <- ""

let private opened state client (packet: ServerPacket) =
    let welcome = packet.SessionOpened
    // The load goes to the global channel; the system channel is not measured.
    let globalChannel = welcome.Channels |> Seq.tryFind (fun channel -> channel.Kind = ChatChannelKind.Global)
    if client.Ready || not packet.HasRequestId || packet.RequestId <> 1UL
       || welcome.SelfPlayerId <> client.AccountId || globalChannel.IsNone || globalChannel.Value.ChannelId = 0UL
       || not (welcome.Players |> Seq.exists (fun player -> not (isNull player.Profile) && player.Profile.PlayerId = welcome.SelfPlayerId)) then
        fail state (sprintf "Client %d invalid session welcome" client.Index)
    else
        client.Ready <- true
        client.ReadyMs <- now state
        client.PlayerId <- welcome.SelfPlayerId
        client.ChannelId <- globalChannel.Value.ChannelId
        state.ReadyCount <- state.ReadyCount + 1
        for player in welcome.Players do
            if not (client.Online.Add player.Profile.PlayerId) then fail state "Duplicate player in initial snapshot"
        // History is outside the measured load. Its tail establishes ordering.
        let history = globalChannel.Value.RecentMessages
        if history.Count > 0 then
            client.LastMessageId <- history[history.Count - 1].MessageId

let private published state client (packet: ServerPacket) =
    let message = packet.ChatPublished.Message
    let prefix = state.Prefix + ":"
    if not client.Ready || isNull message || not (message.Text.StartsWith(prefix, StringComparison.Ordinal)) then
        fail state (sprintf "Client %d received an unknown publication" client.Index)
    else
        match Int32.TryParse(message.Text.AsSpan(prefix.Length)) with
        | true, sequence when sequence >= 0 && sequence < state.Messages.Count ->
            let submitted = state.Messages[sequence]
            let author = state.Clients[submitted.Sender]
            if message.MessageId <= client.LastMessageId || message.ChannelId <> client.ChannelId
               || isNull message.Author || message.Author.PlayerId <> author.PlayerId then
                fail state (sprintf "Client %d duplicate/out-of-order/invalid publication %d" client.Index sequence)
            elif submitted.MessageId <> 0UL && submitted.MessageId <> message.MessageId then
                fail state (sprintf "Publication %d has different IDs across recipients" sequence)
            else
                submitted.MessageId <- message.MessageId
                client.LastMessageId <- message.MessageId
                client.Received <- client.Received + 1
                submitted.Received <- submitted.Received + 1
                state.Received <- state.Received + 1L
                if client.Index = submitted.Sender then
                    if not packet.HasRequestId || packet.RequestId <> submitted.RequestId
                       || not (client.Pending.Remove submitted.RequestId) then
                        fail state (sprintf "Publication %d has unmatched author request ID" sequence)
                    else
                        submitted.AuthorAckMs <- now state - submitted.SentMs
                elif packet.HasRequestId then
                    fail state (sprintf "Publication %d leaks request ID to another recipient" sequence)

                if submitted.Received = state.Clients.Length then
                    submitted.CompletedMs <- now state - submitted.SentMs
                    state.Completed <- state.Completed + 1
                elif submitted.Received > state.Clients.Length then
                    fail state (sprintf "Publication %d has excess recipients" sequence)
        | _ -> fail state (sprintf "Client %d received an unknown benchmark sequence" client.Index)

let private received state client (event: EnetEvent) =
    use packet = event.Packet
    state.ReceivedApplicationBytes[int event.ChannelId] <- state.ReceivedApplicationBytes[int event.ChannelId] + int64 packet.DataLength
    if event.ChannelId = 2uy && packet.Flags = Unchecked.defaultof<EnetPacketFlag> then
        let response = ServerMovementPacket.Parser.ParseFrom(packet.AsSpan().ToArray())
        if response.ProtocolVersion <> protocolVersion then fail state "Unexpected movement protocol version"
        else
            match state.Movement with
            | Some probe -> probe.ReceiveMovement(client.Index, response, int packet.DataLength)
            | None -> fail state "Unexpected realtime packet during chat benchmark"
    elif event.ChannelId = 3uy && (packet.Flags &&& EnetPacketFlag.Reliable) <> enum<EnetPacketFlag> 0 then
        let response = Dreamsleeve.Protocol.Phantom.ServerAssetPacket.Parser.ParseFrom(packet.AsSpan().ToArray())
        match state.Phantom with
        | Some probe -> probe.Asset(client.Index, response)
        | None when response.ProtocolVersion = protocolVersion && not (isNull response.Policy) -> ()
        | None -> fail state "Unexpected model data without phantom workload"
    elif event.ChannelId = 4uy && packet.DataLength = 0un && (packet.Flags &&& EnetPacketFlag.Reliable) <> enum<EnetPacketFlag> 0 then ()
    elif event.ChannelId = 4uy && (packet.Flags &&& (EnetPacketFlag.Reliable ||| EnetPacketFlag.Unsequenced)) = enum<EnetPacketFlag> 0 then
        match state.Phantom with
        | Some probe -> probe.Pose(client.Index, Dreamsleeve.Protocol.Phantom.ServerPosePacket.Parser.ParseFrom(packet.AsSpan().ToArray()), int packet.DataLength)
        | None -> fail state "Unexpected phantom pose without workload"
    elif event.ChannelId > 1uy || (packet.Flags &&& EnetPacketFlag.Reliable) <> EnetPacketFlag.Reliable then
        fail state (sprintf "Client %d received wrong transport channel/flags" client.Index)
    else
        let response = ServerPacket.Parser.ParseFrom(packet.AsSpan().ToArray())
        let validChannel =
            match response.PayloadCase with
            | ServerPacket.PayloadOneofCase.ChatPublished -> event.ChannelId = 1uy
            | ServerPacket.PayloadOneofCase.RequestRejected -> true
            | _ -> event.ChannelId = 0uy
        if response.ProtocolVersion <> protocolVersion then fail state "Unexpected protocol version"
        elif not validChannel then fail state "Response arrived on the wrong delivery channel"
        else
            match response.PayloadCase with
            | ServerPacket.PayloadOneofCase.SessionOpened -> opened state client response
            | ServerPacket.PayloadOneofCase.ChatPublished ->
                state.ReceivedChatPayloadBytes <- state.ReceivedChatPayloadBytes + int64 packet.DataLength
                published state client response
            | ServerPacket.PayloadOneofCase.RequestRejected ->
                state.Rejections <- state.Rejections + 1
                fail state (sprintf "Client %d rejected request %d: %A (%s)" client.Index response.RequestId
                                    response.RequestRejected.Code response.RequestRejected.Message)
            | ServerPacket.PayloadOneofCase.PresenceChanged ->
                let presence = response.PresenceChanged
                for player in presence.Joined do
                    if not client.Ready || isNull player.Profile || not (client.Online.Add player.Profile.PlayerId) then
                        fail state (sprintf "Client %d received an invalid/duplicate presence join" client.Index)
                for playerId in presence.Left do
                    client.Online.Remove playerId |> ignore
                    if not state.Disconnecting then fail state "A player left before benchmark cleanup"
                let moving = presence.Updated.Count > 0 || presence.Metadata.Count > 0 || presence.Visibility.Count > 0
                match state.Movement with
                | Some probe -> probe.ReceivePresence(client.Index, presence, int packet.DataLength)
                | None when moving -> fail state "Unexpected player update during chat benchmark"
                | None -> ()
            | ServerPacket.PayloadOneofCase.PlayerUpdateAccepted ->
                match state.Movement with
                | Some probe -> probe.Receive(client.Index, response, int packet.DataLength)
                | None -> fail state "Unexpected player update during chat benchmark"
            // Nobody places marks: the session still reports the empty own list when it
            // opens and an empty visible set whenever the observer's space changes.
            | ServerPacket.PayloadOneofCase.OwnGroundMarks ->
                if response.OwnGroundMarks.Marks.Count <> 0 then fail state (sprintf "Client %d received own ground marks" client.Index)
            | ServerPacket.PayloadOneofCase.GroundMarksChanged ->
                if response.GroundMarksChanged.Added.Count <> 0 || response.GroundMarksChanged.RemovedIds.Count <> 0 then
                    fail state (sprintf "Client %d received ground marks" client.Index)
            // Fresh benchmark accounts have no guilds or invitations. These
            // uncorrelated lifecycle notifications do not settle load requests.
            | ServerPacket.PayloadOneofCase.GuildsSnapshot ->
                if not client.Ready || response.HasRequestId || response.GuildsSnapshot.Guilds.Count <> 0
                   || response.GuildsSnapshot.Invites.Count <> 0 then
                    fail state (sprintf "Client %d received unexpected guild state" client.Index)
            | ServerPacket.PayloadOneofCase.RoleChanged ->
                if not client.Ready || response.HasRequestId
                   || (response.RoleChanged.Role <> PlayerRole.Player && response.RoleChanged.Role <> PlayerRole.Moderator) then
                    fail state (sprintf "Client %d received invalid role notification" client.Index)
            | ServerPacket.PayloadOneofCase.MuteChanged ->
                if not client.Ready || response.HasRequestId || not (isNull response.MuteChanged.Mute) then
                    fail state (sprintf "Client %d received unexpected mute state" client.Index)
            | unknown -> fail state (sprintf "Unknown server packet payload: %A" unknown)

let private handle state hostIndex (event: EnetEvent) =
    match state.Slots.TryGetValue(struct (hostIndex, event.Peer.IncomingPeerId)) with
    | false, _ ->
        if event.Type = EnetEventType.Receive then event.Packet.Dispose()
        fail state "Event for unknown ENet peer slot"
    | true, client ->
        match event.Type with
        | EnetEventType.Connect -> connected state client
        | EnetEventType.Receive -> received state client event
        | EnetEventType.Disconnect ->
            client.Closed <- true
            if not state.Disconnecting then
                state.Disconnections <- state.Disconnections + 1
                fail state (sprintf "Client %d unexpectedly disconnected" client.Index)
        | EnetEventType.None -> ()
        | unknown -> fail state (sprintf "Unknown ENet event: %A" unknown)

let private pumpHost state hostIndex =
    let host = state.Hosts[hostIndex]
    let diagnostics = state.Diagnostics[hostIndex]
    let started = diagnostics.BeginPoll()
    let mutable remaining = state.EventBudgetPerHost
    let mutable processed = 0
    // Multiple sockets are visited without blocking on an idle one.
    let mutable waitMs = if state.Hosts.Length = 1 then 1u else 0u

    while remaining > 0 do
        let mutable event = Unchecked.defaultof<EnetEvent>
        let result = host.Service(waitMs, &event)
        waitMs <- 0u
        if result < 0 then
            fail state "ENet Service failed"
            remaining <- 0
        elif result = 0 then remaining <- 0
        else
            remaining <- remaining - 1
            processed <- processed + 1
            handle state hostIndex event

    host.Flush()
    diagnostics.EndPoll(started, processed, host, state.Budget)
    processed

let private pump state =
    let mutable processed = 0
    for index in 0 .. state.Hosts.Length - 1 do
        processed <- processed + pumpHost state index

    if processed = 0 && state.Hosts.Length > 1 then
        System.Threading.Thread.Sleep 1

let private address port =
    let mutable value = Unchecked.defaultof<enet.ENetAddress>
    let result = enet.ENetAddress.FromIpAddress(IPAddress.Loopback, port, &value)
    if result <> SocketError.Success then invalidOp (sprintf "Address failed: %A" result)
    value

let private barrierPump state () =
    if state.ErrorCount > 0 then invalidOp "Worker failed while waiting at a barrier"
    pump state

let private ramp state =
    stage "connecting"
    state.Group.Wait("joined", state.Group.Index, barrierPump state)
    let remote = address state.Options.Port
    let mutable started = 0
    let deadline = now state + max 120000. (float state.Clients.Length * 2000.)
    // Finish each join batch before starting another: bootstrap traffic belongs
    // to preparation, not to the movement load we are trying to measure.
    let admittedConverged () =
        state.ReadyCount = started
        && (state.Clients |> Array.take started |> Array.forall (fun client -> client.Online.Count = state.GlobalOffset + started))

    let converged () = started = state.Clients.Length && admittedConverged ()

    while not (converged ()) && state.ErrorCount = 0 && now state < deadline do
        if started < state.Clients.Length && admittedConverged () then
            let count = min 4 (state.Clients.Length - started)
            let batch = state.Clients[started .. started + count - 1]
            let tickets = batch |> Array.Parallel.map (login state)

            for index in 0 .. batch.Length - 1 do
                let client = batch[index]
                let ticket, elapsed = tickets[index]
                client.SessionTicket <- ticket
                state.LoginMs <- state.LoginMs + elapsed

                let mutable peer = Unchecked.defaultof<EnetPeer>
                if state.Hosts[client.HostIndex].TryConnect(remote, 5un, 0u, &peer) then
                    client.Peer <- peer
                    client.StartedMs <- now state
                    state.Slots.Add(struct (client.HostIndex, peer.IncomingPeerId), client)
                    started <- started + 1
                else
                    fail state "ENet could not allocate a client peer"
        pump state
        for client in state.Clients do
            if client.Peer.IsCreated && not client.Ready && now state - client.StartedMs > 30000. then
                fail state (sprintf "Client %d session opening timed out" client.Index)
    state.RampMs <- now state
    state.Group.Publish("joined", true)
    state.Group.Wait("joined", state.Group.Workers, barrierPump state)
    let expectedPlayers = HashSet<uint64>(state.AllPlayerIds)
    let allOnline () = state.Clients |> Array.forall (fun client -> client.Online.SetEquals expectedPlayers)
    while not (allOnline()) && state.ErrorCount = 0 && now state < deadline do pump state
    state.PresenceConverged <-
        state.ReadyCount = state.Clients.Length && expectedPlayers.Count = state.AllPlayerIds.Length
        && (state.Clients |> Array.forall (fun client -> client.Online.SetEquals expectedPlayers))
    if not state.PresenceConverged then fail state "Not all sessions and online lists converged"
    else
        printfn "READY clients=%d ramp_ms=%.3f" state.ReadyCount state.RampMs
        Console.Out.Flush()

let private serviceFor state durationMs =
    let deadline = now state + durationMs
    while now state < deadline && state.ErrorCount = 0 do pump state

let private sendChat state =
    let mutable chosen = None
    let mutable checkedPeers = 0
    while chosen.IsNone && checkedPeers < state.Clients.Length do
        let candidate = state.Clients[state.NextSender]
        state.NextSender <- (state.NextSender + 1) % state.Clients.Length
        checkedPeers <- checkedPeers + 1
        if candidate.Pending.Count = 0 then chosen <- Some candidate

    match chosen with
    | None -> false
    | Some client ->
        let sequence = state.Messages.Count
        let requestId = uint64 sequence + (if state.Phantom.IsSome then 100000000UL else 2UL)
        let packet = ClientPacket(ProtocolVersion = protocolVersion, RequestId = requestId,
                                  SendChat = SendChat(ChannelId = client.ChannelId, Text = sprintf "%s:%d" state.Prefix sequence))
        let sentMs = now state
        if send state client packet then
            state.SentChatPayloadBytes <- state.SentChatPayloadBytes + int64 (packet.CalculateSize())
            state.Messages.Add {
                Sender = client.Index; RequestId = requestId; SentMs = sentMs; MessageId = 0UL
                Received = 0; AuthorAckMs = -1.; CompletedMs = -1.
            }
            client.Pending.Add(requestId, sequence)
            state.MaxInflight <- max state.MaxInflight (state.Messages.Count - state.Completed)
            true
        else false

let private load state =
    stage "idle"
    serviceFor state 3000.
    if state.ErrorCount = 0 then
        stage "load"
        let started = now state
        let deadline = started + state.Options.Seconds * 1000.
        let mutable due = started
        let interval = if state.Options.Rate > 0. then 1000. / state.Options.Rate else Double.PositiveInfinity
        while now state < deadline && state.ErrorCount = 0 do
            let mutable blocked = false
            while state.Options.Rate > 0. && now state >= due && now state < deadline && not blocked && state.ErrorCount = 0 do
                if state.Messages.Count - state.Completed < 128 && sendChat state then due <- due + interval
                else blocked <- true
            let before = now state
            pump state
            if blocked then state.BackpressuredMs <- state.BackpressuredMs + now state - before
        state.LoadMs <- now state - started
        printfn "LOAD_DONE sent=%d expected=%d" state.Messages.Count (int64 state.Messages.Count * int64 state.Clients.Length)
        Console.Out.Flush()

let private drain state =
    stage "drain"
    let started = now state
    let deadline = started + 30000.
    while state.Completed < state.Messages.Count && state.ErrorCount = 0 && now state < deadline do pump state
    state.DrainMs <- now state - started
    if state.Completed <> state.Messages.Count then fail state "Delivery completion timed out or failed"
    for client in state.Clients do
        if client.Received <> state.Messages.Count || client.Pending.Count <> 0 then
            fail state (sprintf "Client %d: received %d of %d; pending author replies %d"
                                client.Index client.Received state.Messages.Count client.Pending.Count)
    printfn "DELIVERY_DONE received=%d" state.Received
    Console.Out.Flush()

let private movementLoad state =
    let probe = Movement.Probe(state.Options.Scenario, state.Options.Rate, state.Options.ReplicationMs, state.Options.ActorValuesHz, state.AllPlayerIds, state.Group.Index, state.Group.Workers, state.Clients.Length,
                               Coordination.now, (fun index packet -> send state state.Clients[index] packet),
                               (fun index packet -> sendMovement state state.Clients[index] packet), fail state)
    state.Movement <- Some probe
    stage "movement-setup"
    state.Group.All("probes", true, barrierPump state) |> ignore
    state.Group.Wait("characters", state.Group.Index, barrierPump state)
    probe.BeginCharacters(fun () -> serviceFor state 150.)
    state.Group.Publish("characters", true)
    state.Group.Wait("characters", state.Group.Workers, barrierPump state)
    let setupDeadline = now state + 30000.
    while not probe.Prepared && state.ErrorCount = 0 && now state < setupDeadline do pump state
    if not probe.Prepared then fail state "Character snapshots did not converge before load"
    serviceFor state 1000.

    if Environment.GetEnvironmentVariable "DREAMSLEEVE_BENCH_WARM_POSITIONS" = "1" && state.ErrorCount = 0 then
        stage "movement-positions"
        state.Group.Wait("positions", state.Group.Index, barrierPump state)
        probe.PreparePositions(fun () -> serviceFor state 150.)
        state.Group.Publish("positions", true)
        state.Group.Wait("positions", state.Group.Workers, barrierPump state)
        let positionDeadline = now state + 30000.
        let mutable ready = false
        let mutable nextCheck = now state
        while not ready && state.ErrorCount = 0 && now state < positionDeadline do
            pump state
            if now state >= nextCheck then
                ready <- probe.PositionsPrepared
                nextCheck <- now state + 100.
        if not ready then fail state "Initial position baselines did not converge before load"
        else state.Group.All("positions-ready", true, barrierPump state) |> ignore

    match state.Phantom with
    | Some phantom when phantom.ClientCacheWarm && state.ErrorCount = 0 ->
        stage "phantom-setup"
        phantom.Prepare()
        let deadline = now state + 30000.
        while not phantom.Prepared && state.ErrorCount = 0 && now state < deadline do
            phantom.Tick()
            pump state
        if not phantom.Prepared then fail state "Phantom publications did not settle before steady load"
        else
            state.Group.All("phantoms-ready", true, fun () -> phantom.Tick(); barrierPump state ()) |> ignore
            serviceFor state 500.
    | _ -> ()
    stage "armed"
    let started = state.Group.Start(barrierPump state)
    while Coordination.now() < started do pump state
    stage "load"
    let hostBefore = state.Hosts |> Array.map(fun host -> host.TotalSentData, host.TotalReceivedData, host.TotalSentPackets, host.TotalReceivedPackets)
    let sentBefore = Array.copy state.SentApplicationBytes
    let receivedBefore = Array.copy state.ReceivedApplicationBytes
    probe.Start(started)
    state.Phantom |> Option.iter _.Start()
    let mutable chatDue = now state
    while Coordination.now() - started < state.Options.Seconds * 1000. && state.ErrorCount = 0 do
        let iterationStarted = Coordination.now()
        probe.SendDue()
        state.Phantom |> Option.iter(fun phantom ->
            phantom.Tick()
            if state.Group.Workers = 1 && now state >= chatDue then
                if sendChat state then chatDue <- chatDue + 100.)
        pump state
        probe.RecordIteration(Coordination.now() - iterationStarted)
    let difference (before: uint32) (after: uint32) = (uint64 after + 0x100000000UL - uint64 before) % 0x100000000UL
    let totals = Array.map2 (fun (sent, received, sentPackets, receivedPackets) (host: EnetHost) ->
        difference sent host.TotalSentData, difference received host.TotalReceivedData,
        difference sentPackets host.TotalSentPackets, difference receivedPackets host.TotalReceivedPackets) hostBefore state.Hosts
    state.LoadTraffic <- Some(box {|
        enetSentBytes = totals |> Array.sumBy(fun (x,_,_,_) -> x)
        enetReceivedBytes = totals |> Array.sumBy(fun (_,x,_,_) -> x)
        udpSentPackets = totals |> Array.sumBy(fun (_,_,x,_) -> x)
        udpReceivedPackets = totals |> Array.sumBy(fun (_,_,_,x) -> x)
        sentApplicationBytesByLane = Array.map2 (-) state.SentApplicationBytes sentBefore
        receivedApplicationBytesByLane = Array.map2 (-) state.ReceivedApplicationBytes receivedBefore |})
    probe.Stop()
    state.Phantom |> Option.iter _.Stop()
    state.LoadMs <- Coordination.now() - started

    stage "drain"
    let draining = now state
    let deadline = draining + 30000.
    while probe.Pending > 0 && state.ErrorCount = 0 && now state < deadline do pump state
    if state.ErrorCount = 0 && probe.Pending = 0 then
        probe.FinalSamples(fun () -> pump state)
        let locations = state.Group.All("final", probe.FinalLocations, barrierPump state) |> Array.transpose |> Array.concat
        probe.SetFinalLocations locations
        let mutable converged = false
        let mutable nextCheck = now state
        while not converged && state.ErrorCount = 0 && now state < deadline do
            probe.RepeatFinalSamples()
            pump state
            if now state >= nextCheck then
                converged <- probe.Converged()
                nextCheck <- now state + 100.
        if not converged then fail state "Final movement/AOI state did not converge"
    elif state.ErrorCount = 0 then fail state "Control acknowledgements did not drain"
    state.DrainMs <- now state - draining
    if state.Phantom.IsSome && state.ErrorCount = 0 then drain state
    if state.ErrorCount = 0 then
        state.Disconnecting <- true
        state.Group.All("verified", true, barrierPump state) |> ignore

let private disconnect state =
    stage "disconnect"
    state.Disconnecting <- true
    for client in state.Clients do
        if client.Peer.IsCreated && not client.Closed then client.Peer.DisconnectLater 0u
    for host in state.Hosts do host.Flush()
    let deadline = now state + 10000.
    let stillConnected () = state.Clients |> Array.exists (fun client -> client.Peer.IsCreated && not client.Closed)
    while stillConnected () && now state < deadline do pump state
    if stillConnected () then fail state "Graceful client disconnect timed out"
    for client in state.Clients do
        if client.Peer.IsCreated && not client.Closed then client.Peer.DisconnectNow 0u

let private summary (values: float array) =
    Array.sortInPlace values
    let percentile fraction =
        if values.Length = 0 then 0.
        else values[max 0 (int (ceil (fraction * float values.Length)) - 1)]
    {| count = values.Length; p50 = percentile 0.50; p95 = percentile 0.95; p99 = percentile 0.99
       max = if values.Length = 0 then 0. else values[values.Length - 1] |}

let private report state =
    let output = Path.GetFullPath state.Options.Output
    Directory.CreateDirectory(Path.GetDirectoryName output) |> ignore
    let result = {|
        success = state.ErrorCount = 0 && state.PresenceConverged && state.ReadyCount = state.Clients.Length
                  && state.Completed = state.Messages.Count && state.Received = int64 state.Messages.Count * int64 state.Clients.Length
        transportDuringLoad = state.LoadTraffic |> Option.toObj
        phantom = state.Phantom |> Option.map _.Report() |> Option.toObj
        movement = state.Movement |> Option.map (fun probe -> probe.Report(state.LoadMs)) |> Option.toObj
        clients = state.Clients.Length; ready = state.ReadyCount; presenceConverged = state.PresenceConverged; seconds = state.Options.Seconds; requestedRate = state.Options.Rate
        sent = state.Messages.Count; received = state.Received; expected = int64 state.Messages.Count * int64 state.Clients.Length
        sentChatPayloadBytes = state.SentChatPayloadBytes
        receivedChatPayloadBytes = state.ReceivedChatPayloadBytes
        sentChatPayloadBytesPerSecond = if state.LoadMs = 0. then 0. else float state.SentChatPayloadBytes * 1000. / state.LoadMs
        receivedChatPayloadBytesPerSecond =
            if state.LoadMs + state.DrainMs = 0. then 0.
            else float state.ReceivedChatPayloadBytes * 1000. / (state.LoadMs + state.DrainMs)
        actualSendRate = if state.LoadMs = 0. then 0. else float state.Messages.Count * 1000. / state.LoadMs
        registrationMs = state.RegistrationMs; loginMs = state.LoginMs
        rampIncludesLogin = true; authenticationConcurrency = 4
        rampMs = state.RampMs; loadMs = state.LoadMs; drainMs = state.DrainMs; totalMs = now state
        backpressuredMs = state.BackpressuredMs; maxInflight = state.MaxInflight; inflightLimit = 128; connectWindow = 4
        workerIndex = state.Group.Index; workerCount = state.Group.Workers; totalClients = state.AllPlayerIds.Length
        hostCount = state.Hosts.Length; socketCount = state.Hosts.Length; serviceLoopCount = 1
        unexpectedDisconnects = state.Disconnections; rejections = state.Rejections; errorCount = state.ErrorCount
        errors = state.Errors.ToArray()
        transportConnectMs = state.Clients |> Array.filter _.Ready |> Array.map (fun client -> client.ConnectedMs - client.StartedMs) |> summary
        applicationOpenMs = state.Clients |> Array.filter _.Ready |> Array.map (fun client -> client.ReadyMs - client.ConnectedMs) |> summary
        authorAckMs = state.Messages |> Seq.filter (fun item -> item.AuthorAckMs >= 0.) |> Seq.map _.AuthorAckMs |> Seq.toArray |> summary
        allRecipientsMs = state.Messages |> Seq.filter (fun item -> item.CompletedMs >= 0.) |> Seq.map _.CompletedMs |> Seq.toArray |> summary
        recipients = state.Clients |> Array.map (fun client -> {| index = client.Index; received = client.Received; lastMessageId = client.LastMessageId |})
    |}
    File.WriteAllText(output, JsonSerializer.Serialize(result, JsonSerializerOptions(WriteIndented = true)))
    stage "done"
    if result.success then 0 else 1

let private run (options: Options) =
    use recorder = new Measurements.Recorder(options.Output + ".metrics.json")
    let prefix = "nb" + Guid.NewGuid().ToString("N").Substring(0, 12)
    use authentication = new HttpClient(BaseAddress = options.AuthUrl, Timeout = TimeSpan.FromSeconds 30.)
    let group = Coordination.Group()
    stage "auth-register"
    group.Wait("accounts", group.Index, fun () -> System.Threading.Thread.Sleep 10)
    let registration = Stopwatch.StartNew()
    let accounts = Array.zeroCreate options.Clients
    System.Threading.Tasks.Parallel.For(0, options.Clients,
        System.Threading.Tasks.ParallelOptions(MaxDegreeOfParallelism = 4),
        fun index -> accounts[index] <- register authentication prefix index) |> ignore
    registration.Stop()
    let accountGroups = group.All("accounts", accounts, fun () -> System.Threading.Thread.Sleep 10)
    let allPlayerIds = accountGroups |> Array.transpose |> Array.concat
    let globalOffset = accountGroups |> Array.take group.Index |> Array.sumBy Array.length

    if enet.ENET_API.enet_initialize() <> 0 then invalidOp "ENet initialization failed"
    let hosts = ResizeArray<EnetHost>()
    try
        let capacity = (options.Clients + options.Hosts - 1) / options.Hosts
        let buffer = Environment.GetEnvironmentVariable "DREAMSLEEVE_BENCH_CLIENT_BUFFER"
        for _ in 1 .. options.Hosts do
            let host = EnetHost.Create(address 0us, unativeint capacity, 5un, 0u, 0u, EnetHostOption.Ipv4)
            hosts.Add host
            if not (String.IsNullOrEmpty buffer) then
                let bytes = Int32.Parse buffer
                if not (TransportDiagnostics.ConfigureBuffers(host, bytes, bytes)) then
                    invalidOp "Cannot configure benchmark socket buffers"
            host.SetMaximumPacketSize(1024un * 1024un)
            host.SetMaximumWaitingData(32un * 1024un * 1024un)

        // Bound native packet ownership independently of control request correlation.
        let outgoingPackets = if options.Scenario = "chat" then 4096 else max (4096 / group.Workers) (16 * options.Clients)
        let totalHosts =
            match Environment.GetEnvironmentVariable "DREAMSLEEVE_BENCH_TOTAL_HOSTS" with
            | null | "" -> options.Hosts
            | value -> Int32.Parse value

        let state = {
            EventBudgetPerHost = max 1 (4096 / totalHosts)
            Group = group; AllPlayerIds = allPlayerIds; GlobalOffset = globalOffset
            Diagnostics = Array.init options.Hosts (fun _ -> TransportDiagnostics()); Movement = None; Phantom = None; Options = options; Hosts = hosts.ToArray(); Authentication = authentication
            RegistrationMs = registration.Elapsed.TotalMilliseconds; LoginMs = 0.
            Clock = Stopwatch.StartNew(); Prefix = prefix
            SentApplicationBytes = Array.zeroCreate 5; ReceivedApplicationBytes = Array.zeroCreate 5; LoadTraffic = None
            Clients = Array.init options.Clients (fun index -> {
                Index = index; HostIndex = index % options.Hosts; AccountId = accounts[index]; SessionTicket = ""; Peer = Unchecked.defaultof<EnetPeer>; Budget = PacketBudget(16, 1024L * 1024L)
                StartedMs = 0.; ConnectedMs = 0.; ReadyMs = 0.; Ready = false; Closed = false
                PlayerId = 0UL; ChannelId = 0UL; LastMessageId = 0UL; Received = 0; Pending = Dictionary(); Online = HashSet()
            })
            Slots = Dictionary(); Budget = PacketBudget(outgoingPackets, 16L * 1024L * 1024L / int64 group.Workers); Messages = ResizeArray(); Errors = ResizeArray()
            ErrorCount = 0; ReadyCount = 0; Disconnections = 0; Rejections = 0; Received = 0L; SentChatPayloadBytes = 0L; ReceivedChatPayloadBytes = 0L; Completed = 0; NextSender = 0
            Disconnecting = false; PresenceConverged = false; RampMs = 0.; LoadMs = 0.; DrainMs = 0.; BackpressuredMs = 0.; MaxInflight = 0
        }
        match PhantomProbe.configuration() with
        | Some config ->
            state.Phantom <- Some(new PhantomProbe.Probe(config, state.AllPlayerIds, state.Group.Index, state.Group.Workers, state.Clients.Length, Coordination.now,
                (fun index lane delivery bytes ->
                    match OutgoingPackets.TrySend(state.Clients[index].Peer, ReadOnlySpan<byte>(bytes), state.Budget, state.Clients[index].Budget, lane, delivery) with
                    | PacketSendResult.Sent -> state.SentApplicationBytes[int lane] <- state.SentApplicationBytes[int lane] + int64 bytes.Length; true
                    | PacketSendResult.BudgetExceeded -> false
                    | result -> fail state (sprintf "Phantom packet admission lane%d: %A" lane result); false), fail state))
        | None -> ()
        try
            ramp state
            if state.ErrorCount = 0 then
                if options.Scenario = "chat" then
                    load state
                    if state.ErrorCount = 0 then drain state
                else movementLoad state
        with error -> fail state (error.ToString())
        try disconnect state with error -> fail state ("Cleanup: " + error.Message)
        let result = report state
        state.Phantom |> Option.iter(fun probe -> (probe :> IDisposable).Dispose())
        result
    finally
        for host in hosts do host.Dispose()
        enet.ENET_API.enet_deinitialize()

let private parse (args: string array) =
    let mutable options = { AuthUrl = Uri("http://127.0.0.1:8779/"); Port = 8778us; Clients = 10; Hosts = 1; Seconds = 10.; Rate = 10.; ReplicationMs = 50; ActorValuesHz = 0.; Scenario = "chat"; Output = "build/network-benchmark.json" }
    if args.Length % 2 <> 0 then invalidArg "args" "Expected --auth-url URL --port P --clients N --seconds D --rate R --output path.json"
    for index in 0 .. 2 .. args.Length - 1 do
        let value = args[index + 1]
        match args[index] with
        | "--auth-url" -> options <- { options with AuthUrl = Uri(value.TrimEnd('/') + "/", UriKind.Absolute) }
        | "--port" -> options <- { options with Port = UInt16.Parse value }
        | "--clients" -> options <- { options with Clients = Int32.Parse value }
        | "--hosts" -> options <- { options with Hosts = Int32.Parse value }
        | "--seconds" -> options <- { options with Seconds = Double.Parse(value, CultureInfo.InvariantCulture) }
        | "--rate" -> options <- { options with Rate = Double.Parse(value, CultureInfo.InvariantCulture) }
        | "--replication-ms" -> options <- { options with ReplicationMs = Int32.Parse(value, CultureInfo.InvariantCulture) }
        | "--actor-values-hz" -> options <- { options with ActorValuesHz = Double.Parse(value, CultureInfo.InvariantCulture) }
        | "--scenario" -> options <- { options with Scenario = value }
        | "--output" -> options <- { options with Output = value }
        | unknown -> invalidArg "args" ("Unknown option: " + unknown)
    if (options.AuthUrl.Scheme <> Uri.UriSchemeHttps && (options.AuthUrl.Scheme <> Uri.UriSchemeHttp || not options.AuthUrl.IsLoopback)) then
        invalidArg "args" "Authentication URL must use HTTPS, or HTTP on loopback for local benchmarks."
    if options.Hosts < 1 || options.Hosts > options.Clients then invalidArg "args" "Hosts must be between 1 and clients."
    if options.ReplicationMs < 1 || options.Port = 0us || options.Clients < 1 || options.Clients > 4095
       || not (Double.IsFinite options.Seconds) || options.Seconds <= 0. || options.Seconds > 300.
       || not (Double.IsFinite options.Rate) || options.Rate < 0. || options.Rate > 1000.
       || options.Rate * options.Seconds > 100000. then
        invalidArg "args" "Require port>0, clients 1..4095, seconds (0,300], rate [0,1000], at most 100000 messages."
    if not (List.contains options.Scenario ["chat"; "dense"; "spaces"; "sparse"; "boundaries"])
       || (options.Scenario <> "chat" && options.Rate <= 0.) then
        invalidArg "args" "Movement requires dense/spaces/sparse/boundaries and rate > 0 (per-client Hz)."
    // The plugin sends actor values at most every 250 ms; the server coalesces per replication tick.
    if not (Double.IsFinite options.ActorValuesHz) || options.ActorValuesHz < 0. || options.ActorValuesHz > 20.
       || (options.Scenario = "chat" && options.ActorValuesHz > 0.) then
        invalidArg "args" "Actor values require a movement scenario and 0..20 Hz per client."
    options

[<EntryPoint>]
let main args =
    try
        match args with
        | [|"--verify-movement-oracle"|] -> Movement.verifyOracle(); 0
        | [|"--server-config"; config; "--metrics-output"; output|] -> Measurements.runServer config output
        | _ -> parse args |> run
    with error -> eprintfn "%s" error.Message; 2
