module Dreamsleeve.Server.Tests.OutboxTests

open System
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
    | Failed of AgentSendFailure
    | StopAfterOutput

let private handle (outbox: AgentOutbox<int>) (failures: Channel<AgentSendFailure>)
                   (context: ReliableAgentContext<OwnerMessage>) message = task {
    match message with
    | Enqueue(values, reply) ->
        let accepted = values |> List.map (fun value -> outbox.TrySend(context, value, Failed))
        reply.SetResult(accepted, outbox.Count)
    | Inspect reply ->
        reply.SetResult outbox.Count
    | Hold(entered, release) ->
        entered.SetResult()
        do! release.Task.WaitAsync context.CancellationToken
    | Failed failure ->
        check (failures.Writer.TryWrite failure) "Failure observer is closed."
    | StopAfterOutput ->
        outbox.AbortAfterDrain context
}

let private start capacity (destination: Agent<AgentTests.Message>) failures =
    let address = destination.Ref.TryReliable().Value.Map AgentTests.Message.Record
    let outbox = TestOutbox<int>.Create(capacity, address)
    TestAgent.StartReliable(options "outbox-owner" (AgentMailbox.boundedWait 1), handle outbox failures)

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

let private complete (agent: Agent<'T>) = task {
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
}

let tests = testList "Outbox" [
    case "synchronous execution retains Continue policy and releases the reply reservation" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use destination = TestAgent.Start(AgentOptions.create "business-reply", ordinaryHandler seen)
        let original = InvalidOperationException("business execution")
        let policyCalled = gate<exn>()
        let policy (_, error) =
            policyCalled.SetResult error
            AgentErrorAction.Continue
        let options = {
            AgentOptions.create "business-owner" with
                OnError = Some policy
        }
        let execute value =
            if value = 1 then
                raise original
            else
                AgentTests.Message.Record value
        let handle = TestReplyDispatcher.createHandler 1 (fun _ -> destination.Ref.TryReliable().Value) execute
        use owner = TestAgent.StartReliable(options, handle)
        owner.TryPost 1 |> ignore
        let! observed = awaitResult policyCalled.Task
        check (obj.ReferenceEquals(original, observed)) "Business exception was replaced."
        equal AgentPostResult.Posted (owner.TryPost 2)
        do! eventually (fun () -> seen.Count = 1)
        do! complete owner
        do! complete destination
        equal [|2|] (seen.ToArray())
    })

    case "delivery and failure mapper faults retain both causes and release capacity" (fun () -> task {
        let original, mapper = InvalidOperationException("delivery map"), InvalidOperationException("failure map")
        use destination = TestAgent.Start(AgentOptions.create "mapping-target", fun _ (_: int) -> task { () })
        let address = destination.Ref.TryReliable().Value.Map(fun (_: int) -> raise original)
        let outbox = TestOutbox<int>.Create(1, address)
        let mutable policies = 0
        let options = {
            AgentOptions.create "mapping-owner" with
                OnError = Some(fun _ ->
                    policies <- policies + 1
                    AgentErrorAction.Continue)
        }
        let handle context value = task {
            outbox.TrySend(context, value, fun _ -> raise mapper) |> ignore
        }
        use owner = TestAgent.StartReliable(options, handle)
        owner.TryPost 1 |> ignore
        let! _ = terminal owner.Completion
        check owner.Completion.IsFaulted "Mapping faults were hidden."
        equal 2 owner.Completion.Exception.InnerExceptions.Count
        for cause in [original; mapper] do
            equal 1 (owner.Completion.Exception.InnerExceptions |> Seq.filter(fun error -> obj.ReferenceEquals(cause, error)) |> Seq.length)
        equal 0 policies
        equal 0 outbox.Count
        do! complete destination
    })

    case "direct admission and queued fallback preserve FIFO and map each accepted message once" (fun () -> task {
        let seen, mapped = ConcurrentQueue<int>(), ConcurrentQueue<int>()
        use destination = TestAgent.Start(options "destination" (AgentMailbox.boundedWait 1), ordinaryHandler seen)
        let! release = holdAgent destination
        let address = destination.Ref.TryReliable().Value.Map(fun value ->
            mapped.Enqueue value
            AgentTests.Message.Record value)
        let outbox = TestOutbox<int>.Create(2, address)
        let failures = Channel.CreateUnbounded<AgentSendFailure>()
        use owner = TestAgent.StartReliable(options "owner" (AgentMailbox.boundedWait 1), handle outbox failures)

        let! accepted = enqueue owner [1; 2; 3; 4]
        equal ([true; true; true; false], 2) accepted
        equal [|1; 2|] (mapped.ToArray())

        release.SetResult()
        do! eventually (fun () -> outbox.IsEmpty && seen.Count = 3)
        let! direct = enqueue owner [4]
        equal ([true], 0) direct
        do! complete owner
        do! complete destination
        equal [|1; 2; 3; 4|] (seen.ToArray())
        equal [|1; 2; 3; 4|] (mapped.ToArray())
        equal 0 failures.Reader.Count
    })

    case "direct delivery cannot bypass the non-dropping owner requirement" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use destination = TestAgent.Start(AgentOptions.create "destination", ordinaryHandler seen)
        let outbox = TestOutbox<AgentTests.Message>.Create(1, destination.Ref.TryReliable().Value)
        let send context value = task {
            outbox.TrySend(context, AgentTests.Message.Record value) |> ignore
        }
        match Agent.TryStartReliable(options "dropping-owner" (AgentMailbox.bounded 1 BoundedChannelFullMode.DropWrite), send) with
        | Error AgentStartError.DroppingMailbox -> ()
        | result -> failtestf "Expected reliable construction rejection, got %A" result
        do! complete destination
        equal 0 seen.Count
    })

    case "bounded FIFO sends keep the owner responsive and Complete drains them" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use destination = TestAgent.Start(options "destination" (AgentMailbox.boundedWait 1), ordinaryHandler seen)
        let! release = holdAgent destination
        equal AgentPostResult.Posted (destination.TryPost(AgentTests.Message.Record 99))
        let failures = Channel.CreateUnbounded<AgentSendFailure>()
        use owner = start 2 destination failures
        let! admission = enqueue owner [1; 2; 3]
        equal ([true; true; false], 2) admission
        let! waiting = count owner
        equal 2 waiting
        owner.Complete() |> ignore
        check (not owner.Completion.IsCompleted) "Completion abandoned delivery."
        release.SetResult()
        do! awaitUnit owner.Completion
        do! complete destination
        equal [|99; 1; 2|] (seen.ToArray())
        equal 0 failures.Reader.Count
    })

    case "successful sends release capacity while the owner mailbox is blocked" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use destination = TestAgent.Start(options "destination" (AgentMailbox.boundedWait 1), ordinaryHandler seen)
        let! releaseDestination = holdAgent destination
        destination.TryPost(AgentTests.Message.Record 99) |> ignore
        let failures = Channel.CreateUnbounded<AgentSendFailure>()
        let outbox = TestOutbox<int>.Create(1, destination.Ref.TryReliable().Value.Map AgentTests.Message.Record)
        use owner = TestAgent.StartReliable(options "owner" (AgentMailbox.boundedWait 1), handle outbox failures)
        let! first = enqueue owner [1]
        equal ([true], 1) first
        let entered, releaseOwner = gate<unit>(), gate<unit>()
        owner.TryPost(Hold(entered, releaseOwner)) |> ignore
        do! awaitResult entered.Task
        let next = gate<bool list * int>()
        equal AgentPostResult.Posted (owner.TryPost(Enqueue([2], next)))

        releaseDestination.SetResult()
        do! eventually (fun () -> outbox.IsEmpty)
        releaseOwner.SetResult()
        let! accepted, _ = awaitResult next.Task
        equal [true] accepted
        do! complete owner
        do! complete destination
        equal [|99; 1; 2|] (seen.ToArray())
        equal 0 failures.Reader.Count
    })

    case "closed destination enters the owner's mailbox as a failure" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use destination = TestAgent.Start(AgentOptions.create "closed-destination", ordinaryHandler seen)
        do! complete destination
        let failures = Channel.CreateUnbounded<AgentSendFailure>()
        use owner = start 1 destination failures
        let! accepted, _ = enqueue owner [1]
        equal [true] accepted
        let! failure = failures.Reader.ReadAsync().AsTask().WaitAsync guard
        match failure with
        | AgentSendFailure.Closed -> ()
        | other -> failwithf "Expected closed destination: %A" other
        do! complete owner
        equal 0 seen.Count
    })

    case "aborting cancels queued and waiting sends and joins delivery workers" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use destination = TestAgent.Start(options "destination" (AgentMailbox.boundedWait 1), ordinaryHandler seen)
        let! release = holdAgent destination
        destination.TryPost(AgentTests.Message.Record 99) |> ignore
        let failures = Channel.CreateUnbounded<AgentSendFailure>()
        use owner = start 2 destination failures
        let! accepted = enqueue owner [1; 2]
        equal ([true; true], 2) accepted
        owner.Abort()
        let! _ = terminal owner.Completion
        check owner.Completion.IsCanceled "Owner did not abort."
        release.SetResult()
        do! complete destination
        equal [|99|] (seen.ToArray())
        equal 0 failures.Reader.Count
    })

    case "terminal failure flushes accepted output before aborting" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use destination = TestAgent.Start(options "destination" (AgentMailbox.boundedWait 1), ordinaryHandler seen)
        let! release = holdAgent destination
        destination.TryPost(AgentTests.Message.Record 99) |> ignore
        let failures = Channel.CreateUnbounded<AgentSendFailure>()
        use owner = start 2 destination failures
        let! _ = enqueue owner [1; 2]
        let! _ = owner.PostAsync StopAfterOutput
        let! _ = count owner
        check (not owner.Completion.IsCompleted) "Abort discarded the terminal output."
        release.SetResult()
        let! _ = terminal owner.Completion
        check owner.Completion.IsCanceled "Terminal failure completed successfully."
        do! complete destination
        equal [|99; 1; 2|] (seen.ToArray())
    })

    case "delivery failure after Complete cannot be silently lost" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use destination = TestAgent.Start(options "destination" (AgentMailbox.boundedWait 1), ordinaryHandler seen)
        let! release = holdAgent destination
        destination.TryPost(AgentTests.Message.Record 99) |> ignore
        let failures = Channel.CreateUnbounded<AgentSendFailure>()
        use owner = start 1 destination failures
        let! _ = enqueue owner [1]
        owner.Complete() |> ignore
        destination.Complete() |> ignore
        let! _ = terminal owner.Completion
        check owner.Completion.IsCanceled "Failure was lost because its owner stopped receiving messages."
        release.SetResult()
        do! awaitUnit destination.Completion
        equal [|99|] (seen.ToArray())
    })

    case "ordered handler reserves capacity before mutation and drains a full mailbox" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use destination = TestAgent.Start(options "destination" (AgentMailbox.boundedWait 1), ordinaryHandler seen)
        let! release = holdAgent destination
        destination.TryPost(AgentTests.Message.Record 99) |> ignore
        let executed = ConcurrentQueue<int>()
        let execute value =
            executed.Enqueue value
            AgentTests.Message.Record value
        let handle = TestOrderedHandler.createHandler 2 (destination.Ref.TryReliable().Value) execute
        use owner = TestAgent.StartReliable(options "ordered-owner" (AgentMailbox.boundedWait 1), handle)
        let! _ = owner.PostAsync 1
        let! _ = owner.PostAsync 2
        do! eventually (fun () -> executed.Count = 2)
        let! _ = owner.PostAsync 3
        do! eventually (fun () -> owner.QueueLength = 0)
        equal AgentPostResult.Posted (owner.TryPost 4)
        equal AgentPostResult.Full (owner.TryPost 5)
        equal [|1; 2|] (executed.ToArray())

        owner.Complete() |> ignore
        check (not owner.Completion.IsCompleted) "Completion abandoned pending output."
        release.SetResult()
        do! awaitUnit owner.Completion
        do! complete destination
        equal [|1; 2; 3; 4|] (executed.ToArray())
        equal [|99; 1; 2; 3; 4|] (seen.ToArray())
    })

    case "independent replies reserve capacity before mutation and abort cancels the wait" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use destination = TestAgent.Start(options "destination" (AgentMailbox.boundedWait 1), ordinaryHandler seen)
        let! release = holdAgent destination
        destination.TryPost(AgentTests.Message.Record 99) |> ignore
        let executed = ConcurrentQueue<int>()
        let execute value =
            executed.Enqueue value
            AgentTests.Message.Record value
        let handle = TestReplyDispatcher.createHandler 1 (fun _ -> destination.Ref.TryReliable().Value) execute
        use owner = TestAgent.StartReliable(options "independent-owner" (AgentMailbox.boundedWait 1), handle)
        let! _ = owner.PostAsync 1
        do! eventually (fun () -> executed.Count = 1)
        let! _ = owner.PostAsync 2
        do! eventually (fun () -> owner.QueueLength = 0)
        equal AgentPostResult.Posted (owner.TryPost 3)
        equal AgentPostResult.Full (owner.TryPost 4)

        owner.Abort()
        let! _ = terminal owner.Completion
        check owner.Completion.IsCanceled "Abort did not retain its outcome."
        equal [|1|] (executed.ToArray())
        release.SetResult()
        do! complete destination
        equal [|99|] (seen.ToArray())
    })

    case "a reply mapping failure faults the owner and joins other blocked deliveries" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use destination = TestAgent.Start(options "destination" (AgentMailbox.boundedWait 1), ordinaryHandler seen)
        let! release = holdAgent destination
        destination.TryPost(AgentTests.Message.Record 99) |> ignore
        let failure = InvalidOperationException("broken reply mapping")
        let address = destination.Ref.TryReliable().Value.Map(fun value ->
            if value = 2 then raise failure
            AgentTests.Message.Record value)
        let handle = TestReplyDispatcher.createHandler 2 (fun _ -> address) id
        use owner = TestAgent.StartReliable(options "failed-delivery" (AgentMailbox.boundedWait 1), handle)
        let! _ = owner.PostAsync 1
        let! _ = owner.PostAsync 2
        let! _ = terminal owner.Completion
        match owner.StopReason with
        | Some (AgentStopReason.Faulted error) ->
            check (obj.ReferenceEquals(failure, error)) "Delivery exception was replaced."
        | other -> failwithf "Delivery failure was hidden: %A" other
        release.SetResult()
        do! complete destination
        equal [|99|] (seen.ToArray())
    })
]
