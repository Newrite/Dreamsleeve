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
        if lane = DeliveryLane.Realtime then capacity - max 1 (min peers (max 1 (capacity / 8)))
        else capacity

    let private byteLimit lane capacity =
        if lane = DeliveryLane.Realtime then int64 capacity - max 1L (int64 capacity / 4L)
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
                        if lane = DeliveryLane.Realtime then state.Dropped <- state.Dropped + 1L
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
                | Ok () -> ()
                | Error reason ->
                    lock state.Gate (fun () ->
                        state.NativeFailures <- state.NativeFailures + 1L
                        if packet.Lane = DeliveryLane.Realtime then state.Dropped <- state.Dropped + 1L)
                    if packet.Lane <> DeliveryLane.Realtime then disconnect state transport id reason
        | Close id -> transport.Close id
        | Reset id ->
            transport.Reset id
            forget state id

    let private takeCommand state =
        lock state.Gate (fun () ->
            match state.Outgoing.TryDequeue() with
            | false, _ -> ValueNone
            | true, struct (command, _) ->
                match command with
                | Send(id, packet) ->
                    state.OutgoingPackets <- state.OutgoingPackets - 1
                    state.OutgoingBytes <- state.OutgoingBytes - int64 packet.Bytes.Length
                    match state.Peers.TryGetValue id with
                    | true, peer ->
                        peer.Packets <- peer.Packets - 1
                        peer.Bytes <- peer.Bytes - int64 packet.Bytes.Length
                    | false, _ -> ()
                | Close _ | Reset _ -> ()
                ValueSome command)

    let private drain state transport =
        let options = state.Config.Worker
        let started = Stopwatch.GetTimestamp()
        let mutable commands, bytes = 0, 0L
        let mutable available = true
        while available && commands < options.SendCommandsPerPass
              && bytes < int64 options.SendBytesPerPass
              && (commands = 0 || Stopwatch.GetElapsedTime(started).TotalMilliseconds < float options.WorkBudgetMs)
              && healthy state do
            match takeCommand state with
            | ValueNone -> available <- false
            | ValueSome command ->
                commands <- commands + 1
                match command with
                | Send(_, packet) -> bytes <- bytes + int64 packet.Bytes.Length
                | Close _ | Reset _ -> ()
                handle state transport command
        commands

    let private receive state (transport: ServerTransport) =
        match transport.Poll() with
        | Error reason ->
            fail state reason
            0
        | Ok events ->
            for event in events do
                match event with
                | ServerTransportEvent.Connected id ->
                    let payload = transport.MaxUnfragmentedPayloadBytes id
                    lock state.Gate (fun () ->
                        state.Peers[id] <- { Packets = 0; Bytes = 0L; Closing = false; ResetQueued = false; IncomingPackets = 0; IncomingBytes = 0L }
                        state.Payloads[id] <- payload)
                    publish state event |> ignore
                | ServerTransportEvent.Disconnected id | ServerTransportEvent.Failed(id, _) ->
                    forget state id
                    publish state event |> ignore
                | ServerTransportEvent.Received(id, lane, _) ->
                    let active = lock state.Gate (fun () ->
                        match state.Peers.TryGetValue id with
                        | true, peer -> not peer.Closing
                        | false, _ -> false)
                    if active && not (publish state event) && lane <> DeliveryLane.Realtime then
                        disconnect state transport id "Reliable incoming handoff budget exceeded."
            events.Length

    let private recordQueues state =
        let snapshot = lock state.Gate (fun () ->
            let age (queue: Queue<struct ('T * int64)>) =
                match queue.TryPeek() with
                | true, struct (_, timestamp) -> Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds
                | false, _ -> 0.
            let snapshot = struct (state.Incoming.Count, state.IncomingBytes, age state.Incoming,
                                   state.Outgoing.Count, state.OutgoingBytes, age state.Outgoing,
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
            if state.Outgoing.Count - state.OutgoingPackets >= reserve state then
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
            elif not (Enum.IsDefined packet.Lane) then Error "Invalid delivery lane."
            else
                match state.Peers.TryGetValue id with
                | false, _ -> Error "Connection is closed."
                | true, peer when peer.Closing -> Error "Connection is closing."
                | true, _ when packet.Lane = DeliveryLane.Realtime
                               && packet.Bytes.Length > state.Payloads[id] -> Error "Realtime payload exceeds negotiated MTU."
                | true, peer ->
                    let size = int64 packet.Bytes.Length
                    if state.OutgoingPackets >= countLimit packet.Lane state.Config.Worker.QueueCapacity state.Config.PeerLimit
                       || size > byteLimit packet.Lane state.Config.Worker.QueueBytes - state.OutgoingBytes
                       || peer.Packets >= countLimit packet.Lane state.Config.MaxOutgoingPacketsPerPeer 1
                       || size > byteLimit packet.Lane state.Config.MaxOutgoingBytesPerPeer - peer.Bytes then
                        if packet.Lane = DeliveryLane.Realtime then state.Dropped <- state.Dropped + 1L
                        Error "Outgoing transport handoff budget exceeded."
                    else
                        let wake = state.Outgoing.Count = 0
                        state.Outgoing.Enqueue(struct (Send(id, packet), Stopwatch.GetTimestamp()))
                        state.OutgoingPackets <- state.OutgoingPackets + 1
                        state.OutgoingBytes <- state.OutgoingBytes + size
                        peer.Packets <- peer.Packets + 1
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
        match ServerConfig.validate config with
        | Error errors -> Error(String.concat " " errors)
        | Ok config ->
            let state = {
                Config = config; Gate = obj(); Incoming = Queue(); Outgoing = Queue(); Peers = Dictionary()
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
                                state.Incoming.Clear(); state.Outgoing.Clear(); state.Peers.Clear(); state.Payloads.Clear())
                            state.Wake.Dispose()
                }
