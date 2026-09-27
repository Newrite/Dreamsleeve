module Dreamsleeve.Server.Benchmarks.Program

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure

let private ok = function Ok value -> value | Error error -> failwithf "%A" error
let private guard (work: Task<'a>) = work.WaitAsync(TimeSpan.FromSeconds 30.)
let private guardUnit (work: Task) = work.WaitAsync(TimeSpan.FromSeconds 30.)
let private globalId = ChatChannelId.create 1UL |> ok
let private text = ChatMessageText.create 2000 "same benchmark payload" |> ok

type private Probe() =
    let pending = ConcurrentDictionary<struct (Guid * uint64), TaskCompletionSource<int64>>()
    let mutable publications = 0L
    let mutable departures = 0L

    member _.Publications = Interlocked.Read &publications
    member _.Departures = Interlocked.Read &departures
    member _.Expect(connectionId, requestId) =
        let reply = TaskCompletionSource<int64>(TaskCreationOptions.RunContinuationsAsynchronously)
        if not (pending.TryAdd(struct (connectionId, requestId), reply)) then failwith "Duplicate benchmark request."
        reply.Task

    member _.Receive(connectionId, bytes: byte array) =
        let packet = Dreamsleeve.Protocol.Chat.ServerPacket.Parser.ParseFrom(bytes)
        if not (isNull packet.ChatPublished) then Interlocked.Increment &publications |> ignore
        if not (isNull packet.PlayerLeft) then Interlocked.Increment &departures |> ignore

        if packet.HasRequestId then
            match pending.TryRemove(struct (connectionId, packet.RequestId)) with
            | true, reply when not (isNull packet.RequestRejected) ->
                reply.TrySetException(InvalidOperationException(sprintf "Benchmark request rejected: %A" packet.RequestRejected.Code)) |> ignore
            | true, reply -> reply.TrySetResult(Stopwatch.GetTimestamp()) |> ignore
            | false, _ -> failwith "Unmatched benchmark response."

type private Backend = {
    Send: int -> uint64 -> Task
    Disconnect: int -> Task
    Stop: unit -> Task
    UpdateRead: (int -> Task) option
    ObserveQueues: unit -> int
    ObservedQueues: string
}

let private post (agent: Agent<'a>) command = task {
    let! result = agent.PostAsync command
    if result <> AgentPostResult.Posted then failwithf "Cannot post benchmark command: %A" result
}

let private start count (probe: Probe) = task {
    let ids = Array.init count (fun _ -> Guid.NewGuid())
    let settings = { ServerConfig.defaults with PeerLimit = max 32 count; MaxRecentMessages = 64 }
#if BASELINE
    let profiles = MemoryProfileStore.start { MailboxCapacity = 128; MaxPendingReplies = 128 } |> ok
    let codec = ChatCodec.create settings |> ok
    let handleOutput _ output = task {
        match output with
        | SessionOutput.Send(connectionId, response) -> probe.Receive(connectionId, ChatCodec.encodeServer codec response |> ok)
        | SessionOutput.Close _ -> ()
        | SessionOutput.PlayerFailed(_, error) -> failwithf "Player failed: %A" error
        | SessionOutput.Failed error -> failwithf "Registry failed: %A" error
    }
    let receiver = Agent.Start({ AgentOptions.create "benchmark-output" with Mailbox = AgentMailbox.boundedWait 65536 }, handleOutput)
    let registryConfig = {
        MailboxCapacity = 1024; MaxSessions = count; PlayerMailboxCapacity = 1024
        MaxPendingPerPlayer = 16; MaxPendingChannelRequests = 2 * count + 8
        MaxPendingChatRequests = 256; MaxPendingOutput = 65536
    }
    let registry = SessionRegistry.start registryConfig globalId (profiles.Ref.TryReliable().Value) (receiver.Ref.TryReliable().Value) |> ok
    let chat = ChatAgent.start { MailboxCapacity = 256; HistoryCapacity = 64; MaxPendingReplies = 256 }
                   globalId (registry.Ref.TryReliable().Value.Map SessionRegistryMessage.ChannelReplied) |> ok
    do! post registry (SessionRegistryMessage.BindChannel(chat.Ref.TryReliable().Value))
    for index in 0 .. count - 1 do
        let welcome = probe.Expect(ids[index], 1UL)
        do! post registry (SessionRegistryMessage.Open {
            ConnectionId = ids[index]; RequestId = 1UL
            Username = Username.create 32 (sprintf "bench%d" index) |> ok
            DisplayName = DisplayName.create 64 (sprintf "Bench %d" index) |> ok
        })
        let! _ = guard welcome
        ()

    let send index requestId : Task = post registry (SessionRegistryMessage.SendChat {
        ConnectionId = ids[index]; RequestId = requestId; ChannelId = globalId; Text = text })
    let disconnect index : Task = post registry (SessionRegistryMessage.Disconnect ids[index])
    let stop () : Task = task {
        do! post registry SessionRegistryMessage.Stop
        do! guardUnit registry.Completion
        chat.Complete() |> ignore
        do! guardUnit chat.Completion
        receiver.Complete() |> ignore
        do! guardUnit receiver.Completion
        profiles.Complete() |> ignore
        do! guardUnit profiles.Completion
    }
#else
    let tickets = Array.init count (fun index -> (sprintf "bench%d" index).PadRight(43, '_'))
    let identities =
        tickets |> Array.mapi (fun index ticket ->
            ticket, PlayerData.create (PlayerId.create (uint64 index + 1UL) |> ok)
                        (Username.create 32 (sprintf "bench%d" index) |> ok)
                        (DisplayName.create 64 (sprintf "Bench %d" index) |> ok))
        |> Map.ofArray
    let authenticate (request: SessionAuthenticationRequest) : SessionAuthenticationReply = {
        OperationId = request.OperationId
        Result = match Map.tryFind request.Ticket identities with
                 | Some profile -> Ok profile
                 | None -> Error SessionAuthenticationError.InvalidTicket
    }
    let authentication = Agent.Start(AgentOptions.create "benchmark-authentication",
                             AgentReplyDispatcher.createHandler 128 (fun (request: SessionAuthenticationRequest) -> request.ReplyTo) authenticate)
    let authenticator = { Requests = authentication.Ref.TryReliable().Value; Completion = authentication.Completion }
    let incoming = ConcurrentQueue<ServerTransportEvent>()
    let poll () =
        let events = ResizeArray<ServerTransportEvent>()
        let mutable event = Unchecked.defaultof<ServerTransportEvent>
        while events.Count < 64 && incoming.TryDequeue(&event) do events.Add event
        Ok (List.ofSeq events)
    let transport = {
        Poll = poll
        Send = fun (id, bytes) -> probe.Receive(id, bytes); Ok ()
        Close = fun id -> incoming.Enqueue(ServerTransportEvent.Disconnected id)
        Reset = ignore
        Dispose = ignore
    }
    let options = {
        ServerRuntimeOptions.defaults with
            MaxSessions = count; MailboxCapacity = 65536; ControlReserve = 3 * count + 4
            OpenTimeoutMs = 30000; ShutdownTimeoutMs = 5000
            Player = { ServerRuntimeOptions.defaults.Player with MailboxCapacity = 1024; MaxPendingOutput = 1024; MaxPendingUpdates = 1024 }
            Chat = { ServerRuntimeOptions.defaults.Chat with MailboxCapacity = 256; HistoryCapacity = 64 }
    }
    let runtime = ServerRuntime.start options settings authenticator transport Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance |> ok
    for index in 0 .. count - 1 do
        let welcome = probe.Expect(ids[index], 1UL)
        incoming.Enqueue(ServerTransportEvent.Connected ids[index])
        let packet = Dreamsleeve.Protocol.Chat.ClientPacket(
                         ProtocolVersion = ProtocolCodec.Version, RequestId = 1UL,
                         OpenSession = Dreamsleeve.Protocol.Chat.OpenSession(SessionTicket = tickets[index]))
        incoming.Enqueue(ServerTransportEvent.Received(ids[index], Google.Protobuf.MessageExtensions.ToByteArray packet))
        let! _ = guard welcome
        ()

    let players = Array.zeroCreate<AgentRef<PlayerSessionMessage>> count
    for index in 0 .. count - 1 do
        let! result = runtime.TryAskAsync(fun reply -> ServerRuntimeMessage.FindPlayer(ids[index], reply)) |> guard
        match result with
        | AgentAskResult.Replied(Some player) -> players[index] <- player
        | other -> failwithf "No benchmark player: %A" other

    let send index requestId : Task = task {
        let! posted = players[index].PostAsync(PlayerSessionMessage.SendChat(requestId, globalId, text))
        if posted <> AgentPostResult.Posted then failwithf "Player admission: %A" posted
    }
    let disconnect index : Task =
        incoming.Enqueue(ServerTransportEvent.Disconnected ids[index])
        Task.CompletedTask
    let stop () : Task = task {
        do! post runtime ServerRuntimeMessage.Stop
        do! guardUnit runtime.Completion
        authentication.Complete() |> ignore
        do! guardUnit authentication.Completion
    }
#endif
#if BASELINE
    return ids, {
        Send = send; Disconnect = disconnect; Stop = stop; UpdateRead = None
        ObserveQueues = (fun () -> registry.QueueLength + receiver.QueueLength)
        ObservedQueues = "sum: registry + output receiver (private player/channel child queues excluded)"
    }
#else
    let name = CharacterName.create 64 "Benchmark character" |> ok
    let readPlayer index (player: AgentRef<PlayerSessionMessage>) =
        let mutable requestId = 1000000UL
        let handle _ (reply: ReplyChannel<Result<PlayerSnapshot, PlayerStateError>>) = task {
            requestId <- requestId + 1UL
            let accepted = probe.Expect(ids[index], requestId)
            let! updated = player.PostAsync(PlayerSessionMessage.Update(requestId, PlayerUpdate.BeginCharacter name))
            if updated <> AgentPostResult.Posted then failwithf "Update admission: %A" updated
            let! _ = guard accepted
            let! read = player.PostAsync(PlayerSessionMessage.Read reply)
            if read <> AgentPostResult.Posted then failwithf "Read admission: %A" read
        }
        handle
    let readers = players |> Array.mapi (fun index player ->
        Agent.Start({ AgentOptions.create (sprintf "benchmark-reader-%d" index) with Mailbox = AgentMailbox.boundedWait 1 }, readPlayer index player))
    let updateRead index : Task = task {
        let! result = readers[index].AskAsync id |> guard
        let snapshot = result |> ok
        if snapshot.CharacterName <> ValueSome name then failwith "Player update was not visible in its subsequent read."
    }
    let stopWithReaders () : Task = task {
        for reader in readers do reader.Complete() |> ignore
        for reader in readers do do! guardUnit reader.Completion
        do! stop ()
    }
    return ids, {
        Send = send; Disconnect = disconnect; Stop = stopWithReaders; UpdateRead = Some updateRead
        ObserveQueues = (fun () -> runtime.QueueLength + Array.sumBy (fun (reader: Agent<_>) -> reader.QueueLength) readers)
        ObservedQueues = "sum: runtime + benchmark reply drivers (private player/chat/presence queues excluded)"
    }
#endif
}

#if !BASELINE
let private startRooms roomCount count (probe: Probe) = task {
    let codec = ProtocolCodec.create ServerConfig.defaults |> ok
    let ids = Array.init count (fun _ -> Guid.NewGuid())
    let ready = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let mutable joined = 0
    let handleRoom index _ event = task {
        match event with
        | ChatRoomEvent.Joined _ ->
            if Interlocked.Increment(&joined) = count * roomCount then ready.TrySetResult() |> ignore
        | ChatRoomEvent.Accepted(requestId, message) ->
            probe.Receive(ids[index], ProtocolCodec.encodeServer codec (ServerResponse.ChatAccepted(requestId, message)) |> ok)
        | ChatRoomEvent.Published message ->
            probe.Receive(ids[index], ProtocolCodec.encodeServer codec (ServerResponse.ChatPublished message) |> ok)
        | ChatRoomEvent.JoinFailed reason -> ready.TrySetException(InvalidOperationException reason) |> ignore
        | ChatRoomEvent.Rejected(_, reason) -> failwithf "Room benchmark rejected: %A" reason
    }
    let receivers = Array.init count (fun index ->
        Agent.Start({ AgentOptions.create (sprintf "benchmark-room-client-%d" index) with Mailbox = AgentMailbox.boundedWait 1024 }, handleRoom index))
    let handleControl _ message = task { return failwithf "Unexpected room control: %A" message }
    let host = Agent.Start(AgentOptions.create "benchmark-room-control", handleControl)
    let rooms = Array.init roomCount (fun index ->
        ChatRoomAgent.start { MailboxCapacity = 1024; ControlReserve = 64; HistoryCapacity = 64; MaxControlDeliveries = 128 }
            (ChatChannelId.create (uint64 index + 1UL) |> ok) (host.Ref.TryReliable().Value) |> ok)
    for room in rooms do
        for index in 0 .. count - 1 do
            let profile = PlayerData.create (PlayerId.create (uint64 index + 1UL) |> ok)
                              (Username.create 32 (sprintf "bench%d" index) |> ok)
                              (DisplayName.create 64 (sprintf "Bench %d" index) |> ok)
            do! post room (ChatRoomCommand.Join {
                ConnectionId = ids[index]; Profile = profile; Events = receivers[index].Ref.TryReliable().Value })
    do! guard ready.Task
    let send index requestId : Task =
        let room = rooms[(index + int requestId) % roomCount]
        post room (ChatRoomCommand.Publish {
            ConnectionId = ids[index]; RequestId = requestId; Text = text
            ReplyTo = receivers[index].Ref.TryReliable().Value })
    let stop () : Task = task {
        for room in rooms do room.Complete() |> ignore
        for room in rooms do do! guardUnit room.Completion
        for receiver in receivers do receiver.Complete() |> ignore
        for receiver in receivers do do! guardUnit receiver.Completion
        host.Complete() |> ignore
        do! guardUnit host.Completion
    }
    return ids, {
        Send = send; Disconnect = (fun _ -> Task.CompletedTask); Stop = stop; UpdateRead = None
        ObserveQueues = (fun () ->
            Array.sumBy (fun (room: Agent<_>) -> room.QueueLength) rooms +
            Array.sumBy (fun (receiver: Agent<_>) -> receiver.QueueLength) receivers)
        ObservedQueues = "sum: room owners + output receiver agents"
    }
}
#endif

let private waitFor condition = task {
    let limit = Stopwatch.StartNew()
    while not (condition ()) && limit.Elapsed.TotalSeconds < 30. do do! Task.Delay 1
    if not (condition ()) then failwith "Benchmark fanout/cleanup did not complete."
}

let private workload (probe: Probe) (ids: Guid array) (backend: Backend) count firstRequest = task {
    let perPlayer = count / ids.Length
    let actual = perPlayer * ids.Length
    let startCount = probe.Publications
    let samples = Array.zeroCreate<int64> actual
    let admissions = Array.zeroCreate<int64> actual
    let lane index = task {
        for iteration in 0 .. perPlayer - 1 do
            let requestId = firstRequest + uint64 iteration
            let reply = probe.Expect(ids[index], requestId)
            let started = Stopwatch.GetTimestamp()
            do! backend.Send index requestId
            admissions[index * perPlayer + iteration] <- Stopwatch.GetTimestamp() - started
            let! delivered = guard reply
            samples[index * perPlayer + iteration] <- delivered - started
    }
    let! _ = Array.init ids.Length lane |> Task.WhenAll
    do! waitFor (fun () -> probe.Publications = startCount + int64 actual * int64 ids.Length)
    return samples, admissions
}

let private playerWorkload (ids: Guid array) (backend: Backend) count = task {
    let perPlayer = count / ids.Length
    let samples = Array.zeroCreate<int64> (perPlayer * ids.Length)
    let updateRead = backend.UpdateRead.Value
    let lane index = task {
        for iteration in 0 .. perPlayer - 1 do
            let started = Stopwatch.GetTimestamp()
            do! updateRead index
            samples[index * perPlayer + iteration] <- Stopwatch.GetTimestamp() - started
    }
    let! _ = Array.init ids.Length lane |> Task.WhenAll
    return samples, Array.empty<int64>
}

type Measurement = {
    Backend: string
    Players: int
    Requests: int
    PublishedPackets: int64
    Seconds: float
    RequestsPerSecond: float
    P50Ms: float
    P95Ms: float
    P99Ms: float
    AdmissionP50Ms: Nullable<float>
    AdmissionP95Ms: Nullable<float>
    AdmissionP99Ms: Nullable<float>
    AllocatedBytes: int64
    Gen0: int
    Gen1: int
    Gen2: int
    PrivateBytesBefore: int64
    PrivateBytesAfter: int64
    PeakThreadPoolQueue: int64
    PeakObservedQueueLength: int
    ObservedQueues: string
    DisconnectFanoutMs: float
    ShutdownMs: float
}

let private measure name count requests isPlayerOnly hasPresence startBackend = task {
    let probe = Probe()
    let! ids, backend = startBackend count probe
    try
        let runWork count first = if isPlayerOnly then playerWorkload ids backend count else workload probe ids backend count first
        let! _ = runWork 1024 2UL
        GC.Collect()
        GC.WaitForPendingFinalizers()
        GC.Collect()
        use currentProcess = Process.GetCurrentProcess()
        currentProcess.Refresh()
        let privateBefore = currentProcess.PrivateMemorySize64
        let allocatedBefore = GC.GetTotalAllocatedBytes(true)
        let generations = [| for generation in 0 .. 2 -> GC.CollectionCount generation |]
        let mutable peakQueue = ThreadPool.PendingWorkItemCount
        let mutable peakActorQueues = backend.ObserveQueues()
        use sampling = new CancellationTokenSource()
        let sampleThreadPool () = task {
            while not sampling.IsCancellationRequested do
                peakQueue <- max peakQueue ThreadPool.PendingWorkItemCount
                peakActorQueues <- max peakActorQueues (backend.ObserveQueues())
                try do! Task.Delay(10, sampling.Token) with :? OperationCanceledException -> ()
        }
        let sampler = sampleThreadPool ()
        let watch = Stopwatch.StartNew()
        let! latencies, admissions = runWork requests 10000UL
        watch.Stop()
        sampling.Cancel()
        do! sampler
        let allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore
        let collections = [| for generation in 0 .. 2 -> GC.CollectionCount generation - generations[generation] |]
        currentProcess.Refresh()
        let privateAfter = currentProcess.PrivateMemorySize64
        Array.sortInPlace latencies
        Array.sortInPlace admissions
        let percentile (samples: int64 array) fraction =
            let index = min (samples.Length - 1) (int (float samples.Length * fraction))
            float samples[index] * 1000. / float Stopwatch.Frequency
        let admissionPercentile fraction =
            if admissions.Length = 0 then Nullable()
            else Nullable(percentile admissions fraction)

        let! disconnectMs = task {
            if count = 1 || not hasPresence then return -1.
            else
                let before = probe.Departures
                let clock = Stopwatch.StartNew()
                do! backend.Disconnect 0
                do! waitFor (fun () -> probe.Departures >= before + int64 count - 1L)
                return clock.Elapsed.TotalMilliseconds
        }
        let shutdown = Stopwatch.StartNew()
        do! backend.Stop()
        shutdown.Stop()
        return {
            Backend = name; Players = count; Requests = latencies.Length; PublishedPackets = (if isPlayerOnly then 0L else int64 latencies.Length * int64 count)
            Seconds = watch.Elapsed.TotalSeconds; RequestsPerSecond = float latencies.Length / watch.Elapsed.TotalSeconds
            P50Ms = percentile latencies 0.50; P95Ms = percentile latencies 0.95; P99Ms = percentile latencies 0.99
            AdmissionP50Ms = admissionPercentile 0.50; AdmissionP95Ms = admissionPercentile 0.95; AdmissionP99Ms = admissionPercentile 0.99
            AllocatedBytes = allocated; Gen0 = collections[0]; Gen1 = collections[1]; Gen2 = collections[2]
            PrivateBytesBefore = privateBefore; PrivateBytesAfter = privateAfter; PeakThreadPoolQueue = peakQueue
            PeakObservedQueueLength = peakActorQueues; ObservedQueues = backend.ObservedQueues
            DisconnectFanoutMs = disconnectMs; ShutdownMs = shutdown.Elapsed.TotalMilliseconds
        }
    with error ->
        try do! backend.Stop() with _ -> ()
        return raise error
}

[<EntryPoint>]
let main args =
    let output = if args.Length > 0 then args[0] else "benchmark.json"
    let requests = if args.Length > 1 then Int32.Parse args[1] else 8192
    let measurements = ResizeArray<Measurement>()
    let record (work: Task<Measurement>) =
        let result = work.GetAwaiter().GetResult()
        measurements.Add result
        printfn "%s N=%d: %.0f operations/s, p95 %.3f ms, %.1f MB allocations" result.Backend result.Players result.RequestsPerSecond result.P95Ms (float result.AllocatedBytes / 1048576.)
#if BASELINE
    for count in [1; 16; 32] do record (measure "f4eef57-registry" count requests false true start)
#else
    for count in [1; 16; 32] do record (measure "session-channel" count requests false true start)
    for rooms in [1; 4] do record (measure (sprintf "independent-rooms-%d" rooms) 4 requests false false (startRooms rooms))
    for count in [1; 16; 32] do record (measure "player-update-read" count requests true true start)
#endif
    File.WriteAllText(output, JsonSerializer.Serialize(measurements, JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine)
    0
