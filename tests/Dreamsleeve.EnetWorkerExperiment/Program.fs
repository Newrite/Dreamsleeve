module Dreamsleeve.EnetWorkerExperiment.Tests

open System
open System.Collections.Concurrent
open System.Threading
open Expecto
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Infrastructure

let options = { ServerConfig.defaults with EventBudget = 16 }
let packet bytes = { Lane = DeliveryLane.Control; Bytes = bytes }
let smallQueues = { options with MaxPacketBytes = 8; Worker = { options.Worker with QueueBytes = 8 } }

let waitFor condition =
    let deadline = Environment.TickCount64 + 2000L
    while not (condition()) && Environment.TickCount64 < deadline do Thread.Sleep 1
    Expect.isTrue (condition()) "Condition should become observable within two seconds."

let get = function
    | Ok value -> value
    | Error reason -> failtest reason

let fake id (calls: ConcurrentQueue<int * string>) =
    let mutable connect = true
    let record name = calls.Enqueue(Environment.CurrentManagedThreadId, name)
    record "create"
    {
        SetReadyHandler = ignore
        Poll = fun () ->
            record "poll"
            if connect then
                connect <- false
                Ok [ServerTransportEvent.Connected id]
            else Ok []
        Send = fun (_, _) -> record "send"; Ok ()
        MaxUnfragmentedPayloadBytes = fun _ -> record "mtu"; 1364
        Close = fun _ -> record "close"
        Reset = fun _ -> record "reset"
        Dispose = fun () -> record "dispose"
    }

[<EntryPoint>]
let main args =
    testList "Dedicated transport worker" [
        testCase "One owner creates, sends, closes and destroys the transport" <| fun _ ->
            let calls = ConcurrentQueue()
            let id = Guid.NewGuid()
            let caller = Environment.CurrentManagedThreadId
            let proxy = TransportOwner.create options (fun () -> Ok (fake id calls)) |> get
            try
                waitFor (fun () -> proxy.MaxUnfragmentedPayloadBytes id = 1364)
                proxy.Send(id, packet [|1uy|]) |> get
                proxy.Close id
                waitFor (fun () -> calls |> Seq.exists (fun (_,name) -> name = "close"))
            finally proxy.Dispose()
            let recorded = calls.ToArray()
            let owners = recorded |> Array.map fst |> Array.distinct
            Expect.equal owners.Length 1 "All native operations have one owner."
            Expect.notEqual owners[0] caller "Owner is independent of the caller."
            let effects = recorded |> Array.map snd |> Array.filter ((<>) "poll")
            Expect.equal effects [|"create";"mtu";"send";"close";"dispose"|] "FIFO and owner disposal."

        testCase "Owner continues polling while consumer is idle" <| fun _ ->
            let calls = ConcurrentQueue()
            let proxy = TransportOwner.create options (fun () -> Ok (fake (Guid.NewGuid()) calls)) |> get
            try waitFor (fun () -> calls |> Seq.filter (fun (_,name) -> name = "poll") |> Seq.length >= 5)
            finally proxy.Dispose()

        testCase "Startup failure is returned" <| fun _ ->
            let result = TransportOwner.create options (fun () -> Error "bind failed")
            match result with
            | Error error -> Expect.equal error "bind failed" "Original reason."
            | Ok proxy -> proxy.Dispose(); failtest "Unexpected success."

        testCase "Owner exception is visible to the consumer and disposes host" <| fun _ ->
            let calls = ConcurrentQueue()
            let factory () =
                let transport = fake (Guid.NewGuid()) calls
                Ok { transport with Poll = fun () -> failwith "probe failure" }
            let proxy = TransportOwner.create options factory |> get
            try
                waitFor (fun () -> Result.isError (proxy.Poll()))
                match proxy.Poll() with
                | Error error -> Expect.stringContains error "probe failure" "Owner exception preserved."
                | Ok _ -> failtest "Failure hidden."
            finally proxy.Dispose()
            Expect.isTrue (calls |> Seq.exists (fun (_,name) -> name = "dispose")) "Host disposed."

        testCase "Reliable inbound saturation isolates the peer without blocking owner" <| fun _ ->
            let calls = ConcurrentQueue()
            let id = Guid.NewGuid()
            let factory () =
                let transport = fake id calls
                let mutable first = true
                let poll () =
                    if first then
                        first <- false
                        Ok [ServerTransportEvent.Connected id
                            ServerTransportEvent.Received(id, DeliveryLane.Control, [|1uy|])
                            ServerTransportEvent.Received(id, DeliveryLane.Control, [|2uy|])]
                    else Ok []
                Ok { transport with Poll = poll }
            let config = { options with Worker = { options.Worker with QueueCapacity = 1 } }
            let proxy = TransportOwner.create config factory |> get
            try
                waitFor (fun () -> calls |> Seq.exists (fun (_, name) -> name = "reset"))
                let events = proxy.Poll() |> get
                Expect.isTrue (events |> List.exists (function ServerTransportEvent.Failed(peer, _) -> peer = id | _ -> false)) "Peer failure is visible despite full data queue."
                Expect.isOk (proxy.Poll()) "Owner remains healthy."
            finally proxy.Dispose()

        testCase "Ready admission is retried without waiting for a server tick" <| fun _ ->
            let calls = ConcurrentQueue()
            let proxy = TransportOwner.create options (fun () -> Ok(fake (Guid.NewGuid()) calls)) |> get
            let mutable attempts = 0
            try
                proxy.SetReadyHandler(fun () -> Interlocked.Increment(&attempts) >= 2)
                waitFor (fun () -> Volatile.Read(&attempts) >= 2)
                Expect.isNonEmpty (proxy.Poll() |> get) "Retry announces queued lifecycle data."
            finally proxy.Dispose()

        testCase "Oversized outgoing admission fails before the native send" <| fun _ ->
            let calls = ConcurrentQueue()
            let id = Guid.NewGuid()
            let proxy = TransportOwner.create smallQueues (fun () -> Ok (fake id calls)) |> get
            try
                waitFor (fun () -> proxy.MaxUnfragmentedPayloadBytes id > 0)
                Expect.isTrue (Result.isError (proxy.Send(id, packet (Array.zeroCreate 9)))) "Byte limit applies."
            finally proxy.Dispose()
            Expect.isFalse (calls |> Seq.exists (fun (_,name) -> name = "send")) "Rejected payload never reaches ENet."
    ] |> runTestsWithCLIArgs [] args
