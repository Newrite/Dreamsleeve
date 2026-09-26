module Dreamsleeve.Server.Tests.BackgroundTests

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Dreamsleeve.Agent
open Expecto
open AgentTests

type Message =
    | Launch
    | Finished of Result<int, exn>
    | Ping of TaskCompletionSource<unit>
    | HoldMailbox of TaskCompletionSource<unit> * TaskCompletionSource<unit>

let case name (run: unit -> Task<unit>) = testCaseAsync name (async {
    do! (run ()).WaitAsync(TimeSpan.FromSeconds 10.) |> Async.AwaitTask
})

type OwnerMessage =
    | WatchChild of Agent<AgentTests.Message>
    | ChildStopped of Agent<AgentTests.Message> * Result<unit, exn>

let observeChild (child: Agent<AgentTests.Message>) (token: CancellationToken) = task {
    do! child.Completion.WaitAsync token
}

let handleOwner seen (replacement: TaskCompletionSource<Agent<AgentTests.Message> * exn>)
                (context: AgentContext<OwnerMessage>) message = task {
    match message with
    | WatchChild child ->
        context.PipeToSelf(observeChild child, fun result -> ChildStopped(child, result))
    | ChildStopped(child, Error error) ->
        check child.Completion.IsCompleted "Restart preceded the old child's cleanup."
        expectStopFault error child.StopReason
        let next = Agent.Start(AgentOptions.create child.Name, ordinaryHandler seen)
        replacement.SetResult(next, error)
    | ChildStopped(_, Ok ()) -> failwith "The test child should have faulted."
}

let tests = testList "Background" [
    case "a late owner observes a child fault as a message and starts a replacement" (fun () -> task {
        let seen = System.Collections.Concurrent.ConcurrentQueue<int>()
        let restarted = gate<Agent<AgentTests.Message> * exn>()
        let expected = InvalidOperationException("child failed")
        use child = Agent.Start(AgentOptions.create "child", ordinaryHandler seen)
        equal AgentPostResult.Posted (child.TryPost(AgentTests.Message.FailMessage expected))
        do! eventually (fun () -> child.Completion.IsCompleted)

        // Subscribe after failure: observing Completion cannot miss an earlier stop.
        use owner = Agent.Start(AgentOptions.create "owner", handleOwner seen restarted)
        equal AgentPostResult.Posted (owner.TryPost(WatchChild child))
        let! next, error = awaitResult restarted.Task
        use next = next
        check (Object.ReferenceEquals(expected, error)) "Owner lost the original failure."
        equal AgentPostResult.Closed (child.Ref.TryPost(AgentTests.Message.Record 1))
        let! response = next.TryAskAsync(fun reply -> AgentTests.Message.Request(42, reply))
        expectReply 42 response

        next.Complete() |> ignore
        do! awaitUnit next.Completion
        owner.Complete() |> ignore
        do! awaitUnit owner.Completion
    })

    case "reliable mapped addresses wait for capacity and report cancellation or closure" (fun () -> task {
        let seen = System.Collections.Concurrent.ConcurrentQueue<int>()
        use agent = Agent.Start(options "reliable" (AgentMailbox.boundedWait 1), ordinaryHandler seen)
        let! release = holdAgent agent
        equal AgentPostResult.Posted (agent.TryPost(AgentTests.Message.Record 1))
        let address = agent.Ref.TryReliable().Value.Map AgentTests.Message.Record

        use cancel = new CancellationTokenSource()
        let canceled = address.PostAsync(2, cancellationToken = cancel.Token)
        check (not canceled.IsCompleted) "A full mailbox must wait for capacity."
        cancel.Cancel()
        let! result = awaitResult canceled
        equal AgentDeliveryResult.Canceled result

        let pending = address.PostAsync 3
        check (not pending.IsCompleted) "Pending admission completed without space."
        release.SetResult()
        let! posted = awaitResult pending
        equal AgentDeliveryResult.Posted posted

        agent.Complete() |> ignore
        do! awaitUnit agent.Completion
        equal [|1; 3|] (seen.ToArray())
        let! closed = address.PostAsync 4
        equal AgentDeliveryResult.Closed closed
    })

    case "completion is a message; agent handles commands while work waits" (fun () -> task {
        let started, release, finished = gate<unit>(), gate<int>(), gate<int>()
        let operation (token: CancellationToken) = task {
            started.SetResult()
            return! release.Task.WaitAsync token
        }

        let handle (context: AgentContext<Message>) message = task {
            match message with
            | Launch -> context.PipeToSelf(operation, Finished)
            | Finished (Ok value) -> finished.SetResult value
            | Finished (Error error) -> finished.SetException error
            | Ping reply -> reply.SetResult()
            | HoldMailbox(entered, unblock) ->
                entered.SetResult()
                do! unblock.Task
        }

        use agent = Agent.Start(options "fsm" (AgentMailbox.boundedWait 1), handle)
        equal AgentPostResult.Posted (agent.TryPost Launch)
        do! awaitResult started.Task

        let ping = gate<unit>()
        let pingAddress = agent.Ref.Map(Ping)
        equal true pingAddress.IsNonDropping
        let! admitted = pingAddress.PostAsync ping
        equal AgentPostResult.Posted admitted
        do! awaitResult ping.Task
        check (not finished.Task.IsCompleted) "Background operation should still be waiting."

        // Occupy the reader and fill its mailbox before completing the operation.
        let entered, unblock = gate<unit>(), gate<unit>()
        let! _ = agent.Ref.PostAsync(HoldMailbox(entered, unblock))
        do! awaitResult entered.Task
        equal AgentPostResult.Posted (agent.TryPost(Ping(gate<unit>())))
        release.SetResult 42
        unblock.SetResult()

        let! value = awaitResult finished.Task
        equal 42 value
        agent.Complete() |> ignore
        do! awaitUnit agent.Completion
        equal AgentPostResult.Closed (pingAddress.TryPost(gate<unit>()))
    })

    case "operation exceptions return to the FSM" (fun () -> task {
        let observed = gate<exn>()
        let expected = InvalidOperationException("store unavailable")
        let operation (_: CancellationToken) : Task<int> = raise expected
        let handle (context: AgentContext<Message>) message = task {
            match message with
            | Launch -> context.PipeToSelf(operation, Finished)
            | Finished (Error error) -> observed.SetResult error
            | _ -> ()
        }

        use agent = Agent.Start(AgentOptions.create "failure", handle)
        agent.TryPost Launch |> ignore
        let! error = awaitResult observed.Task
        check (Object.ReferenceEquals(expected, error)) "Original operation error lost."
        check agent.IsAcceptingMessages "An operation failure should be handled by the FSM."
        agent.Complete() |> ignore
        do! awaitUnit agent.Completion
    })

    case "abort cancels work and waits for its cleanup" (fun () -> task {
        let started, canceled, cleanup = gate<unit>(), gate<unit>(), gate<unit>()
        let operation (token: CancellationToken) = task {
            started.SetResult()
            try
                do! Task.Delay(Timeout.Infinite, token)
            with :? OperationCanceledException ->
                canceled.SetResult()
                do! cleanup.Task
            return 0
        }
        let handle (context: AgentContext<Message>) message = task {
            match message with
            | Launch -> context.PipeToSelf(operation, Finished)
            | _ -> ()
        }

        use agent = Agent.Start(AgentOptions.create "abort", handle)
        agent.TryPost Launch |> ignore
        do! awaitResult started.Task
        agent.Abort()
        do! awaitResult canceled.Task
        check (not agent.Completion.IsCompleted) "Completion must wait for background cleanup."
        cleanup.SetResult()
        let! _ = terminal agent.Completion
        check agent.Completion.IsCanceled "Abort must retain its cancellation outcome."
    })

    case "dropping mailboxes reject background completion delivery" (fun () -> task {
        let mutable launched = false
        let operation (_: CancellationToken) =
            launched <- true
            Task.FromResult 1
        let handle (context: AgentContext<Message>) message = task {
            match message with
            | Launch -> context.PipeToSelf(operation, Finished)
            | _ -> ()
        }

        use agent = Agent.Start(options "drop" (AgentMailbox.bounded 1 BoundedChannelFullMode.DropOldest), handle)
        equal false agent.Ref.IsNonDropping
        agent.TryPost Launch |> ignore
        let! _ = terminal agent.Completion
        check agent.Completion.IsFaulted "Dropping a completion must not be silently allowed."
        check (not launched) "Invalid mailbox must be rejected before starting work."
    })
    case "mapper failure faults the agent and cancels sibling work" (fun () -> task {
        let started, canceled = gate<unit>(), gate<unit>()
        let expected = InvalidOperationException("bad completion mapping")
        let sibling (token: CancellationToken) = task {
            started.SetResult()
            try
                do! Task.Delay(Timeout.Infinite, token)
            with :? OperationCanceledException ->
                canceled.SetResult()
            return 0
        }
        let trigger (_: CancellationToken) = task {
            do! started.Task
            return 1
        }
        let broken (_: Result<int, exn>) : Message = raise expected
        let handle (context: AgentContext<Message>) message = task {
            match message with
            | Launch ->
                context.PipeToSelf(sibling, Finished)
                context.PipeToSelf(trigger, broken)
            | _ -> ()
        }

        use agent = Agent.Start(AgentOptions.create "mapping-failure", handle)
        agent.TryPost Launch |> ignore
        do! awaitResult canceled.Task
        let! _ = terminal agent.Completion
        expectStopFault expected agent.StopReason
    })

    case "Complete joins outstanding work even when its reply cannot enter" (fun () -> task {
        let started, release = gate<unit>(), gate<int>()
        let operation (token: CancellationToken) = task {
            started.SetResult()
            return! release.Task.WaitAsync token
        }
        let handle (context: AgentContext<Message>) message = task {
            match message with
            | Launch -> context.PipeToSelf(operation, Finished)
            | _ -> ()
        }

        use agent = Agent.Start(AgentOptions.create "join", handle)
        agent.TryPost Launch |> ignore
        do! awaitResult started.Task
        agent.Complete() |> ignore
        check (not agent.Completion.IsCompleted) "Work must not outlive Completion."
        release.SetResult 7
        do! awaitUnit agent.Completion
    })

    case "mapper failure during graceful join is not hidden" (fun () -> task {
        let started, release, siblingStarted, canceled = gate<unit>(), gate<int>(), gate<unit>(), gate<unit>()
        let expected = InvalidOperationException("late mapping failure")
        let operation (token: CancellationToken) = task {
            started.SetResult()
            return! release.Task.WaitAsync token
        }
        let sibling (token: CancellationToken) = task {
            siblingStarted.SetResult()
            try
                do! Task.Delay(Timeout.Infinite, token)
            with :? OperationCanceledException -> canceled.SetResult()
            return 0
        }
        let broken (_: Result<int, exn>) : Message = raise expected
        let handle (context: AgentContext<Message>) message = task {
            match message with
            | Launch ->
                context.PipeToSelf(sibling, Finished)
                context.PipeToSelf(operation, broken)
            | _ -> ()
        }

        use agent = Agent.Start(AgentOptions.create "late-fault", handle)
        agent.TryPost Launch |> ignore
        do! awaitResult started.Task
        do! awaitResult siblingStarted.Task
        agent.Complete() |> ignore
        release.SetResult 1
        do! awaitResult canceled.Task
        let! _ = terminal agent.Completion
        expectStopFault expected agent.StopReason
    })

]
