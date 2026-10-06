module Dreamsleeve.Server.Tests.TransportOwnerTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Infrastructure
open Expecto

let private config =
    { ServerConfig.defaults with PeerLimit = 4; EventBudget = 8
                                 Worker = { ServerConfig.defaults.Worker with QueueCapacity = 4 } }

let private ok = function Ok value -> value | Error error -> failwithf "%A" error
let private packet lane size = { Lane = lane; Bytes = Array.zeroCreate size }
let private case name run = testCaseAsync name (async { do! run() |> Async.AwaitTask })

let private eventually predicate = task {
    let until = Environment.TickCount64 + 5000L
    while not (predicate()) && Environment.TickCount64 < until do do! Task.Delay 1
    Expect.isTrue (predicate()) "Timed out waiting for owner progress."
}

type private Fake = {
    Id: Guid
    Events: ConcurrentQueue<ServerTransportEvent>
    Calls: ConcurrentQueue<string * int>
    mutable Send: unit -> Result<unit, string>
    mutable SendPacket: (TransportPacket -> Result<unit, string>) option
    mutable PollFailure: string option
    mutable ThrowPoll: bool
}

let private record fake name = fake.Calls.Enqueue(name, Environment.CurrentManagedThreadId)
let private factory fake () =
    record fake "create"
    Ok {
        Poll = fun () ->
            record fake "poll"
            if Volatile.Read(&fake.ThrowPoll) then failwith "poll exploded"
            match fake.PollFailure with
            | Some reason -> Error reason
            | None ->
                let events = ResizeArray()
                let mutable value = Unchecked.defaultof<ServerTransportEvent>
                while fake.Events.TryDequeue(&value) do events.Add value
                Ok(List.ofSeq events)
        SetReadyHandler = ignore
        Send = fun (_, packet) -> record fake "send"; match fake.SendPacket with Some send -> send packet | None -> fake.Send()
        MaxUnfragmentedPayloadBytes = fun _ -> record fake "mtu"; 1200
        Close = fun _ -> record fake "close"
        Reset = fun _ -> record fake "reset"
        Dispose = fun () -> record fake "dispose"
    }

let private setup settings = task {
    let fake = { Id = Guid.NewGuid(); Events = ConcurrentQueue(); Calls = ConcurrentQueue()
                 Send = (fun () -> Ok ()); SendPacket = None; PollFailure = None; ThrowPoll = false }
    fake.Events.Enqueue(ServerTransportEvent.Connected(fake.Id, Net.IPAddress.Loopback))
    let owner = TransportOwner.create settings (factory fake) |> ok
    do! eventually (fun () -> owner.MaxUnfragmentedPayloadBytes fake.Id = 1200)
    let mutable connected = false
    do! eventually (fun () ->
        owner.Poll() |> ok |> List.iter (function ServerTransportEvent.Connected(id, _) when id = fake.Id -> connected <- true | _ -> ())
        connected)
    return fake, owner
}

let private observed fake name = fake.Calls |> Seq.exists (fun (call, _) -> call = name)

let tests = testList "TransportOwner" [
    case "chat passes model budget pressure while model chunks retain accepted order" (fun () -> task {
        let! fake, owner = setup config
        let delivered = ConcurrentQueue<int>()
        use entered = new ManualResetEventSlim(false)
        use release = new ManualResetEventSlim(false)
        use retrySeen = new ManualResetEventSlim(false)
        use resumeModel = new ManualResetEventSlim(false)
        let value lane number = { Lane = lane; Bytes = [|byte number|] }
        try
            fake.SendPacket <- Some(fun packet ->
                let number = int packet.Bytes[0]
                if number = 0 then entered.Set(); release.Wait(TimeSpan.FromSeconds 5.) |> ignore
                if number = 1 && not resumeModel.IsSet then retrySeen.Set(); Error "Native packet budget exceeded."
                else delivered.Enqueue number; Ok ())
            owner.Send(fake.Id, value DeliveryLane.Control 0) |> ok
            do! eventually (fun () -> entered.IsSet)
            owner.Send(fake.Id, value DeliveryLane.Models 1) |> ok
            owner.Send(fake.Id, value DeliveryLane.Models 2) |> ok
            owner.Send(fake.Id, value DeliveryLane.Chat 3) |> ok
            release.Set()
            do! eventually (fun () -> retrySeen.IsSet && (delivered |> Seq.contains 3))
            Expect.equal (delivered.ToArray()) [|0;3|] "Chat has priority and second model chunk cannot jump a retry."
            resumeModel.Set()
            do! eventually (fun () -> delivered.Count = 4)
            Expect.equal (delivered.ToArray()) [|0;3;1;2|] "Accepted reliable model order preserved after budget recovery."
            Expect.isFalse (observed fake "reset") "Bulk pressure does not reset the healthy peer."
        finally release.Set(); resumeModel.Set(); owner.Dispose()
    })

    case "one exhausted model peer cannot block other peers' models or poses" (fun () -> task {
        let! fake, owner = setup { config with Worker = { config.Worker with QueueCapacity = 8 } }
        let other = Guid.NewGuid()
        let delivered = ConcurrentQueue<int>()
        use entered = new ManualResetEventSlim(false)
        use release = new ManualResetEventSlim(false)
        use resume = new ManualResetEventSlim(false)
        try
            fake.Events.Enqueue(ServerTransportEvent.Connected(other, Net.IPAddress.Loopback))
            do! eventually (fun () -> owner.MaxUnfragmentedPayloadBytes other = 1200)
            fake.SendPacket <- Some(fun packet ->
                let number = int packet.Bytes[0]
                if number = 0 then entered.Set(); release.Wait(TimeSpan.FromSeconds 5.) |> ignore
                if number = 1 && not resume.IsSet then Error "Native packet budget exceeded."
                else delivered.Enqueue number; Ok ())
            let send id lane number = owner.Send(id, { Lane = lane; Bytes = [|byte number|] }) |> ok
            send fake.Id DeliveryLane.Control 0
            do! eventually (fun () -> entered.IsSet)
            send fake.Id DeliveryLane.Models 1
            send fake.Id DeliveryLane.Models 2
            send other DeliveryLane.Models 4
            send other DeliveryLane.Poses 5
            send other DeliveryLane.Chat 3
            release.Set()
            do! eventually (fun () -> delivered.Count = 4)
            Expect.equal (delivered.ToArray()) [|0;3;4;5|] "Other peer's chat, model and pose pass; blocked peer stays FIFO."
            resume.Set()
            do! eventually (fun () -> delivered.Count = 6)
            Expect.equal (delivered.ToArray()) [|0;3;4;5;1;2|] "Only blocked peer retries its first chunk."
        finally release.Set(); resume.Set(); owner.Dispose()
    })
    case "control and chat pass an accepted realtime backlog without reordering realtime itself" (fun () -> task {
        let! fake, owner = setup { config with Worker = { config.Worker with QueueCapacity = 8 } }
        let delivered = ConcurrentQueue<int>()
        use entered = new ManualResetEventSlim(false)
        use release = new ManualResetEventSlim(false)
        try
            fake.SendPacket <- Some(fun packet ->
                let number = int packet.Bytes[0]
                if number = 0 then entered.Set(); release.Wait(TimeSpan.FromSeconds 5.) |> ignore
                delivered.Enqueue number
                Ok ())
            let send lane number = owner.Send(fake.Id, { Lane = lane; Bytes = [|byte number|] }) |> ok
            send DeliveryLane.Realtime 0
            do! eventually (fun () -> entered.IsSet)
            send DeliveryLane.Realtime 1
            send DeliveryLane.Realtime 2
            send DeliveryLane.Chat 3
            send DeliveryLane.Control 4
            release.Set()
            do! eventually (fun () -> delivered.Count = 5)
            Expect.equal (delivered.ToArray()) [|0;3;4;1;2|] "High priority lanes pass, and each lane's accepted order remains."
        finally release.Set(); owner.Dispose()
    })
    case "empty Poses epoch bookkeeping never enters the runtime handoff" (fun () -> task {
        let! fake, owner = setup config
        try
            fake.Events.Enqueue(ServerTransportEvent.Received(fake.Id, DeliveryLane.Poses, [||]))
            fake.Events.Enqueue(ServerTransportEvent.Received(fake.Id, DeliveryLane.Poses, [|1uy|]))
            let received = ResizeArray<byte array>()
            do! eventually (fun () ->
                for event in owner.Poll() |> ok do
                    match event with ServerTransportEvent.Received(_, DeliveryLane.Poses, bytes) -> received.Add bytes | _ -> ()
                received.Count > 0)
            Expect.equal (received.ToArray()) [|[|1uy|]|] "Epoch marker is transport-only."
        finally owner.Dispose()
    })
    case "owner continues servicing transport while application consumer is idle" (fun () -> task {
        let! fake, owner = setup config
        try
            let polls () = fake.Calls |> Seq.filter (fun (name, _) -> name = "poll") |> Seq.length
            let before = polls()
            do! eventually (fun () -> polls() >= before + 5)
        finally owner.Dispose()
    })

    case "factory protocol calls FIFO send close and disposal have one dedicated owner" (fun () -> task {
        let caller = Environment.CurrentManagedThreadId
        let! fake, owner = setup config
        try
            owner.Send(fake.Id, packet DeliveryLane.Control 8) |> ok
            owner.Close fake.Id
            do! eventually (fun () -> observed fake "close")
        finally owner.Dispose()
        owner.Dispose()
        let effects = fake.Calls |> Seq.filter (fun (call, _) -> call <> "poll" && call <> "mtu") |> Seq.toArray
        Expect.equal (effects |> Array.map fst) [|"create"; "send"; "close"; "dispose"|] "Close drains accepted commands in FIFO order."
        let threads = fake.Calls |> Seq.map snd |> Set.ofSeq
        Expect.equal threads.Count 1 "All native access has one owner."
        Expect.isFalse (threads.Contains caller) "Caller does not own native ENet."
    })

    case "ready admission retries then coalesces until Poll and bounded remainder renotifies" (fun () -> task {
        let! fake, owner = setup { config with EventBudget = 1 }
        try
            let mutable calls = 0
            owner.SetReadyHandler(fun () -> Interlocked.Increment(&calls) > 1)
            fake.Events.Enqueue(ServerTransportEvent.Received(fake.Id, DeliveryLane.Realtime, [|1uy|]))
            fake.Events.Enqueue(ServerTransportEvent.Received(fake.Id, DeliveryLane.Realtime, [|2uy|]))
            do! eventually (fun () -> Volatile.Read(&calls) = 2)
            Expect.equal (owner.Poll() |> ok |> List.length) 1 "Consumer drain is bounded."
            do! eventually (fun () -> Volatile.Read(&calls) = 3)
            Expect.equal (owner.Poll() |> ok |> List.length) 1 "Remaining data triggers another notification."
        finally owner.Dispose()
    })

    case "realtime input saturation drops samples but reliable overflow reports affected peer" (fun () -> task {
        let! fake, owner = setup { config with Worker = { config.Worker with QueueCapacity = 2 } }
        try
            let mutable notified = 0
            owner.SetReadyHandler(fun () -> Interlocked.Increment(&notified) |> ignore; true)
            fake.Events.Enqueue(ServerTransportEvent.Received(fake.Id, DeliveryLane.Realtime, [|1uy|]))
            fake.Events.Enqueue(ServerTransportEvent.Received(fake.Id, DeliveryLane.Realtime, [|2uy|]))
            do! eventually (fun () -> Volatile.Read(&notified) > 0)
            Expect.isFalse (observed fake "reset") "Realtime overflow does not reset a peer."
            fake.Events.Enqueue(ServerTransportEvent.Received(fake.Id, DeliveryLane.Control, [|3uy|]))
            fake.Events.Enqueue(ServerTransportEvent.Received(fake.Id, DeliveryLane.Control, [|4uy|]))
            do! eventually (fun () -> observed fake "reset")
            let mutable failed = false
            do! eventually (fun () ->
                for event in owner.Poll() |> ok do
                    match event with
                    | ServerTransportEvent.Failed(id, reason) -> failed <- id = fake.Id && reason.Contains "incoming"
                    | _ -> ()
                failed)
        finally owner.Dispose()
    })

    case "native reliable send failure reports reason and frees the peer without killing owner" (fun () -> task {
        let! fake, owner = setup config
        try
            fake.Send <- fun () -> Error "native rejected"
            owner.Send(fake.Id, packet DeliveryLane.Control 8) |> ok
            let mutable failed = false
            do! eventually (fun () ->
                for event in owner.Poll() |> ok do
                    match event with
                    | ServerTransportEvent.Failed(id, reason) -> failed <- id = fake.Id && reason = "native rejected"
                    | _ -> ()
                failed)
            Expect.equal (owner.MaxUnfragmentedPayloadBytes fake.Id) 0 "Failed peer cache is removed."
            Expect.isFalse (observed fake "dispose") "One peer failure does not stop the host."
        finally owner.Dispose()
    })

    case "native realtime send failure is a drop and subsequent reliable commands work" (fun () -> task {
        let! fake, owner = setup config
        try
            fake.Send <- fun () -> Error "budget"
            owner.Send(fake.Id, packet DeliveryLane.Realtime 8) |> ok
            do! eventually (fun () -> observed fake "send")
            fake.Send <- fun () -> Ok ()
            owner.Send(fake.Id, packet DeliveryLane.Control 8) |> ok
            owner.Close fake.Id
            do! eventually (fun () -> observed fake "close")
            Expect.isFalse (observed fake "reset") "Dropped realtime did not disconnect."
        finally owner.Dispose()
    })

    case "outgoing per-peer budget and reserved Reset remain bounded under full handoff" (fun () -> task {
        let settings = { config with MaxOutgoingPacketsPerPeer = 2 }
        let! fake, owner = setup settings
        use entered = new ManualResetEventSlim(false)
        use release = new ManualResetEventSlim(false)
        try
            fake.Send <- fun () -> entered.Set(); release.Wait(TimeSpan.FromSeconds 5.) |> ignore; Ok ()
            owner.Send(fake.Id, packet DeliveryLane.Control 8) |> ok
            do! eventually (fun () -> entered.IsSet)
            owner.Send(fake.Id, packet DeliveryLane.Realtime 8) |> ok
            Expect.isError (owner.Send(fake.Id, packet DeliveryLane.Realtime 8)) "Realtime cannot consume extra peer quota."
            Expect.equal (owner.MaxUnfragmentedPayloadBytes fake.Id) 1200 "Realtime saturation keeps the peer."
            owner.Send(fake.Id, packet DeliveryLane.Control 8) |> ok
            Expect.isError (owner.Send(fake.Id, packet DeliveryLane.Control 8)) "Reliable overload is explicit after its reserve fills."
            owner.Reset fake.Id
            release.Set()
            do! eventually (fun () -> observed fake "reset")
            Expect.equal (owner.MaxUnfragmentedPayloadBytes fake.Id) 0 "Reserved reset remains admitted."
        finally
            release.Set()
            owner.Dispose()
    })

    case "owner fault is observable even when incoming data queue is full" (fun () -> task {
        let! fake, owner = setup { config with Worker = { config.Worker with QueueCapacity = 2 } }
        try
            fake.Events.Enqueue(ServerTransportEvent.Received(fake.Id, DeliveryLane.Realtime, [|1uy|]))
            Volatile.Write(&fake.ThrowPoll, true)
            let mutable failure = None
            do! eventually (fun () ->
                match owner.Poll() with Error error -> failure <- Some error; true | Ok _ -> false)
            Expect.isTrue (failure.Value.Contains "poll exploded") "Original failure is observable outside the queue."
            do! eventually (fun () -> observed fake "dispose")
        finally owner.Dispose()
    })

    case "factory error and exception return without leaving an owner alive" (fun () -> task {
        Expect.equal (TransportOwner.create config (fun () -> Error "startup") |> Result.map (fun _ -> ())) (Error "startup") "Startup errors propagate."
        let thrown = TransportOwner.create config (fun () -> failwith "startup throw")
        match thrown with
        | Error reason -> Expect.isTrue (reason.Contains "startup throw") "Startup exception is observed."
        | Ok owner -> owner.Dispose(); failtest "Expected startup error."
    })
    case "handoff rejects oversized realtime before admission and enforces byte quota" (fun () -> task {
        let! fake, owner = setup config
        try
            Expect.isError (owner.Send(fake.Id, packet DeliveryLane.Control (config.MaxPacketBytes + 1))) "Reliable packet limit is checked before native admission."
            Expect.isError (owner.Send(fake.Id, packet DeliveryLane.Realtime 1201)) "MTU rejection is synchronous."
            Expect.isFalse (observed fake "send") "Oversized realtime never reaches native ENet."
        finally owner.Dispose()
        let settings = { config with MaxPacketBytes = 8; MaxOutgoingBytesPerPeer = 8
                                     Worker = { config.Worker with QueueBytes = 8 } }
        let! fake, owner = setup settings
        use entered = new ManualResetEventSlim(false)
        use release = new ManualResetEventSlim(false)
        try
            fake.Send <- fun () -> entered.Set(); release.Wait(TimeSpan.FromSeconds 5.) |> ignore; Ok ()
            owner.Send(fake.Id, packet DeliveryLane.Realtime 6) |> ok
            do! eventually (fun () -> entered.IsSet)
            owner.Send(fake.Id, packet DeliveryLane.Realtime 6) |> ok
            Expect.isError (owner.Send(fake.Id, packet DeliveryLane.Realtime 1)) "Byte quota applies before count capacity."
        finally
            release.Set()
            owner.Dispose()
    })

    case "Dispose cancels a full handoff without sending queued payloads or leaking the owner" (fun () -> task {
        let! fake, owner = setup config
        use entered = new ManualResetEventSlim(false)
        use release = new ManualResetEventSlim(false)
        try
            fake.Send <- fun () -> entered.Set(); release.Wait(TimeSpan.FromSeconds 5.) |> ignore; Ok ()
            owner.Send(fake.Id, packet DeliveryLane.Realtime 8) |> ok
            do! eventually (fun () -> entered.IsSet)
            for _ in 1 .. config.Worker.QueueCapacity - 1 do owner.Send(fake.Id, packet DeliveryLane.Realtime 8) |> ok
            let disposal = Task.Run(Action owner.Dispose)
            do! eventually (fun () ->
                match owner.Send(fake.Id, packet DeliveryLane.Realtime 8) with
                | Error reason -> reason.Contains "stopped"
                | Ok () -> false)
            release.Set()
            do! disposal.WaitAsync(TimeSpan.FromSeconds 5.)
            Expect.equal (fake.Calls |> Seq.filter (fun (name, _) -> name = "send") |> Seq.length) 1 "Queued data is abandoned at stop."
            Expect.equal (fake.Calls |> Seq.filter (fun (name, _) -> name = "dispose") |> Seq.length) 1 "Native resources released exactly once."
        finally
            release.Set()
            owner.Dispose()
    })

    case "realtime saturation retains reliable headroom for another peer in both directions" (fun () -> task {
        let! fake, owner = setup config
        let other = Guid.NewGuid()
        use entered = new ManualResetEventSlim(false)
        use release = new ManualResetEventSlim(false)
        try
            fake.Events.Enqueue(ServerTransportEvent.Connected(other, Net.IPAddress.Loopback))
            do! eventually (fun () -> owner.MaxUnfragmentedPayloadBytes other = 1200)
            owner.Poll() |> ok |> ignore
            fake.Send <- fun () -> entered.Set(); release.Wait(TimeSpan.FromSeconds 5.) |> ignore; Ok ()
            owner.Send(fake.Id, packet DeliveryLane.Realtime 8) |> ok
            do! eventually (fun () -> entered.IsSet)
            for _ in 1 .. config.Worker.QueueCapacity - 1 do
                owner.Send(fake.Id, packet DeliveryLane.Realtime 8) |> ok
                fake.Events.Enqueue(ServerTransportEvent.Received(fake.Id, DeliveryLane.Realtime, [|1uy|]))
            Expect.isError (owner.Send(fake.Id, packet DeliveryLane.Realtime 8)) "Realtime stops before exhausting the shared queue."
            owner.Send(other, packet DeliveryLane.Control 8) |> ok
            fake.Events.Enqueue(ServerTransportEvent.Received(other, DeliveryLane.Control, [|2uy|]))
            release.Set()
            let mutable received = false
            do! eventually (fun () ->
                for event in owner.Poll() |> ok do
                    match event with
                    | ServerTransportEvent.Received(id, DeliveryLane.Control, _) when id = other -> received <- true
                    | ServerTransportEvent.Failed(_, reason) -> failtestf "Healthy reliable peer failed: %s" reason
                    | _ -> ()
                received)
            Expect.isFalse (observed fake "reset") "Realtime load cannot consume reserved reliable headroom."
        finally
            release.Set()
            owner.Dispose()
    })

]
