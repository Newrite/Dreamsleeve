module Dreamsleeve.Server.Tests.OutboxTests

open System.Collections.Concurrent
open System.Threading.Channels
open System.Threading.Tasks
open Dreamsleeve.Agent
open Expecto
open AgentTests
open BackgroundTests

type private OwnerMessage =
    | Enqueue of int list * TaskCompletionSource<bool list * int>
    | Inspect of TaskCompletionSource<int>
    | Hold of TaskCompletionSource<unit> * TaskCompletionSource<unit>
    | Delivered of Result<AgentDeliveryResult, exn>

let private handle (outbox: AgentOutbox<int>) (deliveries: Channel<Result<AgentDeliveryResult, exn>>)
                   (context: AgentContext<OwnerMessage>) message = task {
    match message with
    | Enqueue(values, reply) ->
        let accepted = values |> List.map outbox.TryEnqueue
        outbox.Pump(context, Delivered)
        outbox.Pump(context, Delivered)
        reply.SetResult(accepted, outbox.Count)

    | Inspect reply -> reply.SetResult outbox.Count

    | Hold(entered, release) ->
        entered.SetResult()
        do! release.Task.WaitAsync context.CancellationToken

    | Delivered result ->
        outbox.Acknowledge()
        check (deliveries.Writer.TryWrite result) "Delivery observer is closed."
        outbox.Pump(context, Delivered)
}

let private start capacity (destination: Agent<AgentTests.Message>) deliveries =
    let address = destination.Ref.TryReliable().Value.Map AgentTests.Message.Record
    let outbox = AgentOutbox<int>(capacity, address)
    Agent.Start(options "outbox-owner" (AgentMailbox.boundedWait 1), handle outbox deliveries)

let private enqueue (owner: Agent<OwnerMessage>) values = task {
    let reply = gate<bool list * int>()
    let! admitted = owner.PostAsync(Enqueue(values, reply))
    equal AgentPostResult.Posted admitted
    return! awaitResult reply.Task
}

let private count (owner: Agent<OwnerMessage>) = task {
    let reply = gate<int>()
    let! admitted = owner.PostAsync(Inspect reply)
    equal AgentPostResult.Posted admitted
    return! awaitResult reply.Task
}

let private delivered (deliveries: Channel<Result<AgentDeliveryResult, exn>>) =
    deliveries.Reader.ReadAsync().AsTask().WaitAsync guard

let private complete (agent: Agent<'T>) = task {
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
}

let tests = testList "Outbox" [
    case "bounded ordered sends keep the owner responsive while the target is full" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use destination = Agent.Start(options "destination" (AgentMailbox.boundedWait 1), ordinaryHandler seen)
        let! release = holdAgent destination
        equal AgentPostResult.Posted (destination.TryPost(AgentTests.Message.Record 99))
        let deliveries = Channel.CreateUnbounded<Result<AgentDeliveryResult, exn>>()
        use owner = start 2 destination deliveries

        let! admission = enqueue owner [1; 2; 3]
        equal ([true; true; false], 2) admission
        let! waiting = count owner
        equal 2 waiting

        release.SetResult()
        let! first = delivered deliveries
        let! second = delivered deliveries
        equal (Ok AgentDeliveryResult.Posted) first
        equal (Ok AgentDeliveryResult.Posted) second
        let! remaining = count owner
        equal 0 remaining

        do! complete owner
        do! complete destination
        equal [|99; 1; 2|] (seen.ToArray())
        equal 0 deliveries.Reader.Count
    })

    case "an admitted message occupies capacity until the owner handles its completion" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use destination = Agent.Start(options "destination" (AgentMailbox.boundedWait 1), ordinaryHandler seen)
        let! releaseDestination = holdAgent destination
        equal AgentPostResult.Posted (destination.TryPost(AgentTests.Message.Record 99))
        let deliveries = Channel.CreateUnbounded<Result<AgentDeliveryResult, exn>>()
        use owner = start 1 destination deliveries
        let! first = enqueue owner [1]
        equal ([true], 1) first

        let entered, releaseOwner = gate<unit>(), gate<unit>()
        equal AgentPostResult.Posted (owner.TryPost(Hold(entered, releaseOwner)))
        do! awaitResult entered.Task
        let next = gate<bool list * int>()
        equal AgentPostResult.Posted (owner.TryPost(Enqueue([2], next)))

        releaseDestination.SetResult()
        do! eventually (fun () -> seen.Count = 2)
        releaseOwner.SetResult()
        let! refused = awaitResult next.Task
        equal ([false], 1) refused
        let! result = delivered deliveries
        equal (Ok AgentDeliveryResult.Posted) result

        do! complete owner
        do! complete destination
        equal [|99; 1|] (seen.ToArray())
    })

    case "closed destination is reported and releases the admitted slot" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use destination = Agent.Start(AgentOptions.create "closed-destination", ordinaryHandler seen)
        do! complete destination
        let deliveries = Channel.CreateUnbounded<Result<AgentDeliveryResult, exn>>()
        use owner = start 1 destination deliveries

        let! accepted = enqueue owner [1]
        equal ([true], 1) accepted
        let! result = delivered deliveries
        equal (Ok AgentDeliveryResult.Closed) result
        let! remaining = count owner
        equal 0 remaining
        do! complete owner
        equal 0 seen.Count
    })

    case "aborting the owner cancels a waiting send and joins it" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use destination = Agent.Start(options "destination" (AgentMailbox.boundedWait 1), ordinaryHandler seen)
        let! release = holdAgent destination
        equal AgentPostResult.Posted (destination.TryPost(AgentTests.Message.Record 99))
        let deliveries = Channel.CreateUnbounded<Result<AgentDeliveryResult, exn>>()
        use owner = start 2 destination deliveries
        let! accepted = enqueue owner [1; 2]
        equal ([true; true], 2) accepted

        owner.Abort()
        let! _ = terminal owner.Completion
        check owner.Completion.IsCanceled "Owner did not abort."
        release.SetResult()
        do! complete destination
        equal [|99|] (seen.ToArray())
        equal 0 deliveries.Reader.Count
    })
]
