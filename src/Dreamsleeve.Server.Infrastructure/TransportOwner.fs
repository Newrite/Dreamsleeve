namespace Dreamsleeve.Server.Infrastructure

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Diagnostics.Metrics
open System.Threading
open System.Threading.Tasks
open Dreamsleeve.Server.Core

/// One native owner; the short boundary lock only protects detached handoff data.
[<RequireQualifiedAccess>]
module TransportOwner =
    let private meter = new Meter("Dreamsleeve.Transport.Owner")
    let private incomingCount = meter.CreateHistogram<int>("incoming.queue.count", "packets")
    let private incomingBytes = meter.CreateHistogram<int64>("incoming.queue.bytes", "bytes")
    let private incomingAge = meter.CreateHistogram<double>("incoming.queue.age", "ms")
    let private outgoingCount = meter.CreateHistogram<int>("outgoing.queue.count", "packets")
    let private outgoingBytes = meter.CreateHistogram<int64>("outgoing.queue.bytes", "bytes")
    let private outgoingAge = meter.CreateHistogram<double>("outgoing.queue.age", "ms")
    let private poseAge = meter.CreateHistogram<double>("outgoing.pose.age", "ms")
    let private realtimeDropped = meter.CreateCounter<int64>("realtime.dropped", "packets")
    let private nativeFailures = meter.CreateCounter<int64>("native.send.failures", "packets")
    type private Peer = {
        mutable Packets: int
        mutable Bytes: int64
        mutable Closing: bool
        mutable ResetQueued: bool
        mutable IncomingPackets: int
        mutable IncomingBytes: int64
    }

    type private Command =
        | Send of Guid * TransportPacket
        | Close of Guid
        | Reset of Guid

    type private State = {
        Config: ServerConfig
        Gate: obj
        Incoming: Queue<struct (ServerTransportEvent * int64)>
        Outgoing: Queue<struct (Command * int64)>
        RealtimeOutgoing: Queue<struct (Command * int64)>
        PoseOutgoing: LinkedList<struct (Command * int64)>
        PoseSlots: Dictionary<struct (Guid * uint64), LinkedListNode<struct (Command * int64)>>
        mutable PoseBurst: int
        mutable ModelNotices: int
        ViewNotices: Dictionary<struct (Guid * uint64), int>
        BulkOutgoing: Dictionary<Guid, Queue<struct (Command * int64)>>
        BulkPeers: Queue<Guid>
        mutable BulkCommands: int
        Peers: Dictionary<Guid, Peer>
        Payloads: ConcurrentDictionary<Guid, int>
        Wake: AutoResetEvent
        Ready: TaskCompletionSource<Result<unit, string>>
        mutable IncomingPackets: int
        mutable IncomingBytes: int64
        mutable OutgoingPackets: int
        mutable OutgoingBytes: int64
        mutable Stopped: bool
        mutable Fault: string option
        mutable ReadyHandler: (unit -> bool) option
        mutable Notified: bool
        mutable Dropped: int64
        mutable NativeFailures: int64
    }

    // Realtime may use only the non-reserved share. Reliable packets have the
    // full data quota; lifecycle notifications have a separate count reserve.
    let private countLimit lane capacity peers =
        if LanePolicy.bulk lane then max 1 (capacity / 2)
        elif not (LanePolicy.reliable lane) then capacity - max 1 (min peers (max 1 (capacity / 8)))
        else capacity

    let private byteLimit lane capacity =
        if LanePolicy.bulk lane then max 1L (int64 capacity / 2L)
        elif not (LanePolicy.reliable lane) then int64 capacity - max 1L (int64 capacity / 4L)
        else int64 capacity

    let private reserve state = 2 * state.Config.PeerLimit + 2

    let private fail state reason =
        lock state.Gate (fun () ->
            if state.Fault.IsNone then state.Fault <- Some reason)

    let private notify state =
        let callback =
            lock state.Gate (fun () ->
                if state.Notified || (state.Incoming.Count = 0 && state.Fault.IsNone) then None
                else
                    match state.ReadyHandler with
                    | None -> None
                    | Some callback ->
                        state.Notified <- true
                        Some callback)
        match callback with
        | None -> ()
        | Some callback ->
            let accepted =
                try callback()
                with error ->
                    fail state ("Transport ready callback failed: " + error.Message)
                    false
            if not accepted then lock state.Gate (fun () -> state.Notified <- false)

    let private stopped state = lock state.Gate (fun () -> state.Stopped)
    let private healthy state = lock state.Gate (fun () -> not state.Stopped && state.Fault.IsNone)

    let private forget state id =
        lock state.Gate (fun () ->
            state.Peers.Remove id |> ignore
            state.Payloads.TryRemove id |> ignore)

    let private publish state event =
        lock state.Gate (fun () ->
            match event with
            | ServerTransportEvent.Received(id, lane, bytes) ->
                match state.Peers.TryGetValue id with
                | false, _ -> false
                | true, peer ->
                    if state.IncomingPackets >= countLimit lane state.Config.Worker.QueueCapacity state.Config.PeerLimit
                       || int64 bytes.Length > byteLimit lane state.Config.Worker.QueueBytes - state.IncomingBytes
                       || peer.IncomingPackets >= countLimit lane state.Config.MaxOutgoingPacketsPerPeer 1
                       || int64 bytes.Length > byteLimit lane state.Config.MaxOutgoingBytesPerPeer - peer.IncomingBytes then
                        if not (LanePolicy.reliable lane) then state.Dropped <- state.Dropped + 1L
                        false
                    else
                        state.Incoming.Enqueue(struct (event, Stopwatch.GetTimestamp()))
                        state.IncomingPackets <- state.IncomingPackets + 1
                        state.IncomingBytes <- state.IncomingBytes + int64 bytes.Length
                        peer.IncomingPackets <- peer.IncomingPackets + 1
                        peer.IncomingBytes <- peer.IncomingBytes + int64 bytes.Length
                        true
            | ServerTransportEvent.Connected _ | ServerTransportEvent.Disconnected _ | ServerTransportEvent.Failed _ ->
                if state.Incoming.Count - state.IncomingPackets >= reserve state then
                    state.Fault <- Some "Transport lifecycle handoff reserve exhausted."
                    false
                else
                    state.Incoming.Enqueue(struct (event, Stopwatch.GetTimestamp()))
                    true)

    let private disconnect state (transport: ServerTransport) id reason =
        transport.Reset id
        forget state id
        publish state (ServerTransportEvent.Failed(id, reason)) |> ignore

    let private handle state (transport: ServerTransport) command =
        match command with
        | Send(id, packet) ->
            let exists = lock state.Gate (fun () -> state.Peers.ContainsKey id)
            if exists then
                match transport.Send(id, packet) with
                | Ok () -> true
                | Error reason when packet.Lane = DeliveryLane.Models && reason.Contains("budget", StringComparison.OrdinalIgnoreCase) -> false
                | Error reason ->
                    lock state.Gate (fun () ->
                        state.NativeFailures <- state.NativeFailures + 1L
                        if not (LanePolicy.reliable packet.Lane) then state.Dropped <- state.Dropped + 1L)
                    if LanePolicy.reliable packet.Lane then disconnect state transport id reason
                    true
            else true
        | Close id -> transport.Close id; true
        | Reset id ->
            transport.Reset id
            forget state id
            true

    let private viewNotice = function
        | Send(id, { Schedule = PacketSchedule.ModelNotice source }) when source <> 0UL -> ValueSome(struct (id, source))
        | _ -> ValueNone

    let private releaseCommand state command =
        lock state.Gate (fun () ->
                match command with
                | Send(id, packet) ->
                    match viewNotice command with
                    | ValueSome key ->
                        match state.ViewNotices.TryGetValue key with
                        | true, count when count > 1 -> state.ViewNotices[key] <- count - 1
                        | _ -> state.ViewNotices.Remove key |> ignore
                    | _ -> ()
                    state.OutgoingPackets <- state.OutgoingPackets - 1
                    state.OutgoingBytes <- state.OutgoingBytes - int64 packet.Bytes.Length
                    match state.Peers.TryGetValue id with
                    | true, peer ->
                        peer.Packets <- peer.Packets - 1
                        peer.Bytes <- peer.Bytes - int64 packet.Bytes.Length
                    | false, _ -> ()
                | Close _ | Reset _ -> ())

    let private modelNotice = function
        | struct (Send(_, { Schedule = PacketSchedule.ModelNotice _ }), _) -> true
        | _ -> false

    let private enqueueBulk state id command =
        let queue =
            match state.BulkOutgoing.TryGetValue id with
            | true, queue -> queue
            | _ ->
                let queue = Queue()
                state.BulkOutgoing[id] <- queue
                state.BulkPeers.Enqueue id
                queue
        if queue.Count = 0 && modelNotice command then state.ModelNotices <- state.ModelNotices + 1
        let struct (value, _) = command
        match viewNotice value with
        | ValueSome key ->
            let count = match state.ViewNotices.TryGetValue key with true, value -> value | _ -> 0
            state.ViewNotices[key] <- count + 1
        | _ -> ()
        queue.Enqueue command
        state.BulkCommands <- state.BulkCommands + 1

    let private poseKey = function
        | Send(id, packet) -> packet.PoseStream |> ValueOption.map (fun stream -> struct (id, stream))
        | _ -> ValueNone

    let private removePose state (node: LinkedListNode<struct (Command * int64)>) =
        let struct (command, _) = node.Value
        state.PoseOutgoing.Remove node
        match poseKey command with ValueSome key -> state.PoseSlots.Remove key |> ignore | _ -> ()
        releaseCommand state command

    let private takeCommand state (blocked: HashSet<Guid>) =
        lock state.Gate (fun () ->
            let mutable checking = true
            while checking do
                match state.Outgoing.TryPeek() with
                | true, struct (Close id, _) ->
                    match state.Peers.TryGetValue id with
                    | true, peer when peer.Packets > 0 -> enqueueBulk state id (state.Outgoing.Dequeue())
                    | _ -> checking <- false
                | _ -> checking <- false
            let dequeue (queue: Queue<struct (Command * int64)>) =
                let struct (command, _) as queued = queue.Dequeue()
                releaseCommand state command
                ValueSome(queued, None)
            let pose () =
                let mutable node = state.PoseOutgoing.First
                let mutable waiting = true
                while not (isNull node) && waiting do
                    let struct (command, _) = node.Value
                    waiting <- match poseKey command with ValueSome key -> state.ViewNotices.ContainsKey key | _ -> false
                    if waiting then node <- node.Next
                if isNull node then ValueNone
                else
                    let queued = node.Value
                    removePose state node
                    state.PoseBurst <- min 8 (state.PoseBurst + 1)
                    ValueSome(queued, None)
            let bulk noticesOnly =
                let mutable remaining = state.BulkPeers.Count
                let mutable result = ValueNone
                while remaining > 0 && result.IsNone do
                    remaining <- remaining - 1
                    let id = state.BulkPeers.Dequeue()
                    let command = state.BulkOutgoing[id].Peek()
                    let pendingClose =
                        match command, state.Peers.TryGetValue id with
                        | struct (Close _, _), (true, peer) -> peer.Packets > 0
                        | _ -> false
                    if blocked.Contains id || pendingClose || (noticesOnly && not (modelNotice command)) then state.BulkPeers.Enqueue id
                    else result <- ValueSome(command, Some id)
                result
            if state.Outgoing.Count > 0 then dequeue state.Outgoing
            elif state.RealtimeOutgoing.Count > 0 then dequeue state.RealtimeOutgoing
            else
                // Notices establish the view used by a pose. Prioritize only the
                // head of each model FIFO, never jump a Complete over its chunks.
                let notice = if state.ModelNotices > 0 then bulk true else ValueNone
                if notice.IsSome then notice
                elif state.PoseOutgoing.Count > 0 && (state.PoseBurst < 8 || state.BulkCommands = 0) then
                    match pose() with ValueSome _ as ready -> ready | _ -> bulk false
                else
                    match bulk false with
                    | ValueSome _ as result -> state.PoseBurst <- 0; result
                    | _ when state.PoseOutgoing.Count > 0 -> pose()
                    | _ -> ValueNone)

    let private finishBulk state id completed =
        lock state.Gate (fun () ->
            let queue = state.BulkOutgoing[id]
            if completed then
                if modelNotice (queue.Peek()) then state.ModelNotices <- state.ModelNotices - 1
                let struct (command, _) = queue.Dequeue()
                if queue.Count > 0 && modelNotice (queue.Peek()) then state.ModelNotices <- state.ModelNotices + 1
                state.BulkCommands <- state.BulkCommands - 1
                releaseCommand state command
            if queue.Count > 0 then state.BulkPeers.Enqueue id
            else state.BulkOutgoing.Remove id |> ignore)

    let private drain state transport =
        let options = state.Config.Worker
        let started = Stopwatch.GetTimestamp()
        let mutable commands, bytes = 0, 0L
        let mutable available = true
        let blocked = HashSet<Guid>()
        while available && commands < options.SendCommandsPerPass
              && bytes < int64 options.SendBytesPerPass
              && (commands = 0 || Stopwatch.GetElapsedTime(started).TotalMilliseconds < float options.WorkBudgetMs)
              && healthy state do
            match takeCommand state blocked with
            | ValueNone -> available <- false
            | ValueSome(struct (command, timestamp), bulkPeer) ->
                commands <- commands + 1
                match command with
                | Send(_, packet) -> bytes <- bytes + int64 packet.Bytes.Length
                | Close _ | Reset _ -> ()
                match command with
                | Send(_, packet) when packet.Lane = DeliveryLane.Poses -> poseAge.Record(Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds)
                | _ -> ()
                let stale =
                    match command with
                    | Send(_, packet) when packet.Lane = DeliveryLane.Poses -> Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds > 200.0
                    | _ -> false
                let completed = stale || handle state transport command
                match bulkPeer with
                | Some id ->
                    // Preserve FIFO for this peer, but give every other peer a
                    // turn even while its reliable ENet budget is exhausted.
                    finishBulk state id completed
                    if not completed then blocked.Add id |> ignore
                | None -> ()
        commands

    let private receive state (transport: ServerTransport) =
        match transport.Poll() with
        | Error reason ->
            fail state reason
            0
        | Ok events ->
            for event in events do
                match event with
                | ServerTransportEvent.Connected(id, _) ->
                    let payload = transport.MaxUnfragmentedPayloadBytes id
                    lock state.Gate (fun () ->
                        state.Peers[id] <- { Packets = 0; Bytes = 0L; Closing = false; ResetQueued = false; IncomingPackets = 0; IncomingBytes = 0L }
                        state.Payloads[id] <- payload)
                    publish state event |> ignore
                | ServerTransportEvent.Disconnected id | ServerTransportEvent.Failed(id, _) ->
                    forget state id
                    publish state event |> ignore
                // Empty channel epoch bookkeeping is never a domain pose.
                | ServerTransportEvent.Received(_, DeliveryLane.Poses, bytes) when bytes.Length = 0 -> ()
                | ServerTransportEvent.Received(id, lane, _) ->
                    let active = lock state.Gate (fun () ->
                        match state.Peers.TryGetValue id with
                        | true, peer -> not peer.Closing
                        | false, _ -> false)
                    if active && not (publish state event) && LanePolicy.reliable lane && not (LanePolicy.bulk lane) then
                        disconnect state transport id "Reliable incoming handoff budget exceeded."
            events.Length

    let private recordQueues state =
        let snapshot = lock state.Gate (fun () ->
            let age (queue: Queue<struct ('T * int64)>) =
                match queue.TryPeek() with
                | true, struct (_, timestamp) -> Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds
                | false, _ -> 0.
            let snapshot = struct (state.Incoming.Count, state.IncomingBytes, age state.Incoming,
                                   state.Outgoing.Count + state.RealtimeOutgoing.Count + state.PoseOutgoing.Count + state.BulkCommands, state.OutgoingBytes,
                                   state.BulkOutgoing.Values |> Seq.map age |> Seq.fold max (max (max (age state.Outgoing) (age state.RealtimeOutgoing)) (if state.PoseOutgoing.Count = 0 then 0. else let struct (_, at) = state.PoseOutgoing.First.Value in Stopwatch.GetElapsedTime(at).TotalMilliseconds)),
                                   state.Dropped, state.NativeFailures)
            state.Dropped <- 0L
            state.NativeFailures <- 0L
            snapshot)
        let struct (inputCount, inputBytes, inputAge, outputCount, outputBytes, outputAge, dropped, failed) = snapshot
        incomingCount.Record inputCount
        incomingBytes.Record inputBytes
        incomingAge.Record inputAge
        outgoingCount.Record outputCount
        outgoingBytes.Record outputBytes
        outgoingAge.Record outputAge
        realtimeDropped.Add dropped
        nativeFailures.Add failed

    let private loop state transport =
        let mutable nextMetrics = 0L
        while healthy state do
            let sent = drain state transport
            let received = receive state transport
            notify state
            if Environment.TickCount64 >= nextMetrics then
                nextMetrics <- Environment.TickCount64 + 100L
                recordQueues state
            if sent = 0 && received = 0 then state.Wake.WaitOne(state.Config.Worker.IdleWaitMs) |> ignore

    let private run state factory () =
        let mutable transport = None
        try
            try
                match factory() with
                | Error reason -> state.Ready.TrySetResult(Error reason) |> ignore
                | Ok owned ->
                    transport <- Some owned
                    state.Ready.TrySetResult(Ok ()) |> ignore
                    loop state owned
            with error ->
                let reason = "Transport owner failed: " + error.Message
                fail state reason
                state.Ready.TrySetResult(Error reason) |> ignore
        finally
            match transport with
            | None -> ()
            | Some owned ->
                try recordQueues state
                with error -> fail state ("Transport metrics finalization failed: " + error.Message)
                try owned.Dispose()
                with error -> fail state ("Transport disposal failed: " + error.Message)

        // A full consumer mailbox cannot swallow a terminal failure notification.
        // Native resources are already released while we retry its admission.
        let mutable retry = transport.IsSome
        while retry && not (stopped state) do
            notify state
            retry <- lock state.Gate (fun () -> state.Fault.IsSome && not state.Notified)
            if retry then state.Wake.WaitOne(state.Config.Worker.IdleWaitMs) |> ignore

    let private queueControl state reset id =
        match state.Peers.TryGetValue id with
        | false, _ -> ()
        | true, peer when peer.ResetQueued || (peer.Closing && not reset) -> ()
        | true, peer ->
            if state.Outgoing.Count + state.RealtimeOutgoing.Count + state.PoseOutgoing.Count + state.BulkCommands - state.OutgoingPackets >= reserve state then
                state.Fault <- Some "Transport outgoing lifecycle reserve exhausted."
            else
                peer.Closing <- true
                peer.ResetQueued <- reset
                state.Payloads.TryRemove id |> ignore
                let wake = state.Outgoing.Count = 0
                state.Outgoing.Enqueue(struct ((if reset then Reset id else Close id), Stopwatch.GetTimestamp()))
                if wake then state.Wake.Set() |> ignore

    let private control state reset id =
        lock state.Gate (fun () ->
            if not state.Stopped then queueControl state reset id)

    let private send state (id, packet: TransportPacket) =
        lock state.Gate (fun () ->
            if state.Stopped then Error "Transport owner is stopped."
            elif state.Fault.IsSome then Error state.Fault.Value
            elif isNull packet.Bytes || packet.Bytes.Length = 0 || packet.Bytes.Length > state.Config.MaxPacketBytes then
                Error "Outgoing packet size is outside configured limits."
            elif (match packet.Schedule with PacketSchedule.ModelNotice _ -> packet.Lane <> DeliveryLane.Models | _ -> false) then
                Error "Model notices require the model lane."
            elif packet.PoseStream.IsSome && packet.Lane <> DeliveryLane.Poses then
                Error "Only pose packets may replace a stream slot."
            elif not (Enum.IsDefined packet.Lane) then Error "Invalid delivery lane."
            else
                match state.Peers.TryGetValue id with
                | false, _ -> Error "Connection is closed."
                | true, peer when peer.Closing -> Error "Connection is closing."
                | true, _ when packet.Lane = DeliveryLane.Realtime
                               && packet.Bytes.Length > state.Payloads[id] -> Error "Realtime payload exceeds negotiated MTU."
                | true, peer ->
                    let replaced =
                        match packet.PoseStream with
                        | ValueSome stream -> match state.PoseSlots.TryGetValue(struct (id, stream)) with true, node -> Some node | _ -> None
                        | _ -> None
                    let previousBytes =
                        match replaced with
                        | Some node -> let struct (command, _) = node.Value in match command with Send(_, old) -> int64 old.Bytes.Length | _ -> 0L
                        | _ -> 0L
                    let admission = LanePolicy.admissionLane packet
                    let extra = if replaced.IsSome then 0 else 1
                    let size = int64 packet.Bytes.Length - previousBytes
                    if (extra > 0 && (state.OutgoingPackets + extra > countLimit admission state.Config.Worker.QueueCapacity state.Config.PeerLimit
                                      || peer.Packets + extra > countLimit admission state.Config.MaxOutgoingPacketsPerPeer 1))
                       || (size > 0L && (size > byteLimit admission state.Config.Worker.QueueBytes - state.OutgoingBytes
                                        || size > byteLimit admission state.Config.MaxOutgoingBytesPerPeer - peer.Bytes)) then
                        if not (LanePolicy.reliable packet.Lane) then state.Dropped <- state.Dropped + 1L
                        Error "Outgoing transport handoff budget exceeded."
                    else
                        // A new view invalidates any unsent pose from its predecessor.
                        // Do not release that old pose after the new Offer/Remove.
                        match packet.Schedule with
                        | PacketSchedule.ModelNotice source when source <> 0UL ->
                            match state.PoseSlots.TryGetValue(struct (id, source)) with
                            | true, node -> removePose state node
                            | _ -> ()
                        | _ -> ()
                        let wake = state.Outgoing.Count + state.RealtimeOutgoing.Count + state.PoseOutgoing.Count + state.BulkCommands = 0
                        let command = struct (Send(id, packet), Stopwatch.GetTimestamp())
                        match replaced with
                        | Some node ->
                            // Retain the source's fair turn; only payload age is refreshed.
                            node.Value <- command
                        | None ->
                            if packet.Lane = DeliveryLane.Poses then
                                let node = state.PoseOutgoing.AddLast command
                                match packet.PoseStream with ValueSome stream -> state.PoseSlots[struct (id, stream)] <- node | _ -> ()
                            elif LanePolicy.bulk packet.Lane then enqueueBulk state id command
                            elif packet.Lane = DeliveryLane.Realtime then state.RealtimeOutgoing.Enqueue command
                            else state.Outgoing.Enqueue command
                        state.OutgoingPackets <- state.OutgoingPackets + extra
                        state.OutgoingBytes <- state.OutgoingBytes + size
                        peer.Packets <- peer.Packets + extra
                        peer.Bytes <- peer.Bytes + size
                        if wake then state.Wake.Set() |> ignore
                        Ok ())

    let private poll state () =
        lock state.Gate (fun () ->
            state.Notified <- false
            match state.Fault with
            | Some reason -> Error reason
            | None when state.Stopped -> Error "Transport owner is stopped."
            | None ->
                let events = ResizeArray()
                while events.Count < state.Config.EventBudget && state.Incoming.Count > 0 do
                    let struct (event, _) = state.Incoming.Dequeue()
                    match event with
                    | ServerTransportEvent.Received(id, _, bytes) ->
                        match state.Peers.TryGetValue id with
                        | true, peer ->
                            peer.IncomingPackets <- peer.IncomingPackets - 1
                            peer.IncomingBytes <- peer.IncomingBytes - int64 bytes.Length
                        | false, _ -> ()
                        state.IncomingPackets <- state.IncomingPackets - 1
                        state.IncomingBytes <- state.IncomingBytes - int64 bytes.Length
                    | ServerTransportEvent.Connected _ | ServerTransportEvent.Disconnected _ | ServerTransportEvent.Failed _ -> ()
                    events.Add event
                state.Wake.Set() |> ignore
                Ok(List.ofSeq events))

    /// Factory and every call on its result execute on the dedicated owner thread.
    /// Success from Send means bounded handoff admission, not remote delivery.
    let create (config: ServerConfig) factory =
        let state = {
            Config = config; Gate = obj(); Incoming = Queue(); Outgoing = Queue(); BulkOutgoing = Dictionary(); Peers = Dictionary()
            RealtimeOutgoing = Queue(); PoseOutgoing = LinkedList(); PoseSlots = Dictionary(); PoseBurst = 0; ModelNotices = 0; ViewNotices = Dictionary()
            BulkPeers = Queue(); BulkCommands = 0
            Payloads = ConcurrentDictionary(); Wake = new AutoResetEvent(false)
            Ready = TaskCompletionSource<Result<unit, string>>(TaskCreationOptions.RunContinuationsAsynchronously)
            IncomingPackets = 0; IncomingBytes = 0L; OutgoingPackets = 0; OutgoingBytes = 0L
            Stopped = false; Fault = None; ReadyHandler = None; Notified = false; Dropped = 0L; NativeFailures = 0L
        }
        let worker = Thread(ThreadStart(run state factory), IsBackground = true, Name = "Dreamsleeve ENet owner")
        worker.Start()
        match state.Ready.Task.GetAwaiter().GetResult() with
        | Error reason ->
            lock state.Gate (fun () -> state.Stopped <- true)
            state.Wake.Set() |> ignore
            worker.Join()
            state.Wake.Dispose()
            Error reason
        | Ok () ->
            Ok {
                Poll = poll state
                SetReadyHandler = fun callback ->
                    lock state.Gate (fun () ->
                        if not state.Stopped then
                            state.ReadyHandler <- Some callback
                            state.Notified <- false
                            state.Wake.Set() |> ignore)
                Send = send state
                MaxUnfragmentedPayloadBytes = fun id ->
                    match state.Payloads.TryGetValue id with true, size -> size | false, _ -> 0
                Close = control state false
                Reset = control state true
                Dispose = fun () ->
                    let dispose = lock state.Gate (fun () ->
                        if state.Stopped then false
                        else
                            state.Stopped <- true
                            state.ReadyHandler <- None
                            state.Wake.Set() |> ignore
                            true)
                    if dispose then
                        worker.Join()
                        lock state.Gate (fun () ->
                            state.Incoming.Clear(); state.Outgoing.Clear(); state.RealtimeOutgoing.Clear(); state.BulkOutgoing.Clear(); state.BulkPeers.Clear(); state.PoseOutgoing.Clear(); state.PoseSlots.Clear()
                            state.ViewNotices.Clear()
                            state.Peers.Clear(); state.Payloads.Clear())
                        state.Wake.Dispose()
            }
