module Dreamsleeve.Server.Tests.LifetimeTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Dreamsleeve.Agent
open Expecto
open AgentTests
open BackgroundTests

type private LifecycleMessage =
    | Attach of bool * TaskCompletionSource<unit>
    | Ended of Result<unit, exn>

let private lifecycle (child: Agent<'T>) (ended: TaskCompletionSource<Result<unit, exn>>)
                      (context: ReliableAgentContext<LifecycleMessage>) message = task {
    match message with
    | Attach(own, ready) ->
        if own then
            context.Own(child, Ended)
        else
            context.Watch(child, Ended)
        ready.SetResult()
    | Ended result ->
        ended.SetResult result
}

let private attach own (agent: Agent<LifecycleMessage>) = task {
    let ready = gate<unit>()
    agent.TryPost(Attach(own, ready)) |> ignore
    do! awaitResult ready.Task
}

let private taskLifecycle (completion: Task) (ended: TaskCompletionSource<Result<unit, exn>>)
                          (context: ReliableAgentContext<LifecycleMessage>) message = task {
    match message with
    | Attach(_, ready) ->
        context.Watch(completion, Ended)
        ready.SetResult()
    | Ended result ->
        ended.SetResult result
}

let private finish (agent: Agent<'T>) = task {
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
}

type private TargetMessage =
    | Capture of ReplyChannel<int>
    | Crash of exn

let private targetHandler (captured: Channel<ReplyChannel<int>>) (_: AgentContext<TargetMessage>) message = task {
    match message with
    | Capture reply ->
        check (captured.Writer.TryWrite reply) "Capture channel closed."
    | Crash error ->
        return raise error
}

type private RouteMessage =
    | Initialize of TaskCompletionSource<unit>
    | Forward of ReplyChannel<int>
    | Refuse of ReplyChannel<int>
    | Throw of exn * ReplyChannel<int>
    | Close
    | HoldRoute of TaskCompletionSource<unit> * TaskCompletionSource<unit>

let private startRouterWithOptions agentOptions capacity target =
    let mutable scope: AgentReplyScope<int> option = None
    let handle (context: ReliableAgentContext<RouteMessage>) message = task {
        match message with
        | Initialize ready ->
            scope <- Some (TestReplyScope.create context target capacity -1 -2)
            ready.SetResult()
        | Forward reply ->
            scope.Value.Forward(reply, fun forwarded -> target.TryPost(Capture forwarded) = AgentPostResult.Posted)
        | Refuse reply ->
            scope.Value.Forward(reply, fun _ -> false)
        | Throw(error, reply) ->
            scope.Value.Forward(reply, fun _ -> raise error)
        | Close ->
            scope.Value.Close()
        | HoldRoute(entered, release) ->
            entered.SetResult()
            do! release.Task.WaitAsync context.CancellationToken
    }
    TestAgent.StartReliable(agentOptions, handle)

let private startRouter capacity target =
    startRouterWithOptions (options "router" (AgentMailbox.boundedWait 2)) capacity target

let private initialize (router: Agent<RouteMessage>) = task {
    let ready = gate<unit>()
    router.TryPost(Initialize ready) |> ignore
    do! awaitResult ready.Task
}

let private capture (replies: Channel<ReplyChannel<int>>) = replies.Reader.ReadAsync().AsTask().WaitAsync guard

let tests = testList "Lifetimes" [
    case "owned child is aborted and joined through its asynchronous cleanup" (fun () -> task {
        let entered, cleaning, release = gate<unit>(), gate<unit>(), gate<unit>()
        let childHandler (context: ReliableAgentContext<unit>) () = task {
            entered.SetResult()
            try
                do! Task.Delay(Timeout.Infinite, context.CancellationToken)
            with :? OperationCanceledException -> ()
            cleaning.SetResult()
            do! release.Task
        }
        use child = TestAgent.StartReliable(AgentOptions.create "owned", childHandler)
        child.TryPost() |> ignore
        do! awaitResult entered.Task
        let ended = gate<Result<unit, exn>>()
        use owner = TestAgent.StartReliable(AgentOptions.create "owner", lifecycle child ended)
        do! attach true owner
        owner.Abort()
        do! awaitResult cleaning.Task
        check (not owner.Completion.IsCompleted) "Owner abandoned child cleanup."
        release.SetResult()
        let! _ = terminal owner.Completion
        check child.Completion.IsCompleted "Owner finished before its child."
        check child.Completion.IsCanceled "Child was not aborted."
        check (not ended.Task.IsCompleted) "Aborted owner received a termination message."
    })

    case "owned child fault is observed even when ownership begins after its stop" (fun () -> task {
        let failure = InvalidOperationException("child failed")
        use child = TestAgent.Start(AgentOptions.create "child", ordinaryHandler (ConcurrentQueue<int>()))
        child.TryPost(AgentTests.Message.FailMessage failure) |> ignore
        let! _ = terminal child.Completion
        let ended = gate<Result<unit, exn>>()
        use owner = TestAgent.StartReliable(AgentOptions.create "owner", lifecycle child ended)
        do! attach true owner
        let! result = awaitResult ended.Task
        match result with
        | Error error -> check (obj.ReferenceEquals(failure, error)) "Original child fault was lost."
        | Ok () -> failwith "Child failure became success."
        do! finish owner
    })

    case "Watch detaches on Complete and Abort without stopping the shared target" (fun () -> task {
        for abort in [false; true] do
            use child = TestAgent.Start(AgentOptions.create "shared", ordinaryHandler (ConcurrentQueue<int>()))
            let ended = gate<Result<unit, exn>>()
            use owner = TestAgent.StartReliable(AgentOptions.create "observer", lifecycle child ended)
            do! attach false owner
            if abort then
                owner.Abort()
            else
                owner.Complete() |> ignore
            let! _ = terminal owner.Completion
            check child.IsAcceptingMessages "Observation took ownership of the dependency."
            let! reply = child.TryAskAsync(fun reply -> AgentTests.Message.Request(42, reply)) |> awaitResult
            expectReply 42 reply
            do! finish child
            check (not ended.Task.IsCompleted) "Detached watcher delivered a stop message."
    })

    case "Watch observes an already faulted Task without requiring its owning agent" (fun () -> task {
        let failure = InvalidOperationException("shared dependency failed")
        let ended = gate<Result<unit, exn>>()
        use owner = TestAgent.StartReliable(AgentOptions.create "observer", taskLifecycle (Task.FromException failure) ended)
        do! attach false owner
        let! result = awaitResult ended.Task
        match result with
        | Error error -> check (obj.ReferenceEquals(failure, error)) "Task watcher replaced the dependency failure."
        | Ok () -> failwith "Task failure became success."
        do! finish owner
    })

    case "Watch Task detaches on Complete and Abort without awaiting dependency completion" (fun () -> task {
        for abort in [false; true] do
            let pending = gate<unit>()
            let ended = gate<Result<unit, exn>>()
            use owner = TestAgent.StartReliable(AgentOptions.create "observer", taskLifecycle pending.Task ended)
            do! attach false owner
            if abort then
                owner.Abort()
            else
                owner.Complete() |> ignore
            let! _ = terminal owner.Completion
            check (not pending.Task.IsCompleted) "Watching changed the dependency lifetime."
            pending.SetResult()
            check (not ended.Task.IsCompleted) "Detached task watcher delivered a stop message."
    })

    case "forwarded replies are bounded, reclaim settled slots and preserve the first result" (fun () -> task {
        let captured = Channel.CreateUnbounded<ReplyChannel<int>>()
        use target = TestAgent.Start(AgentOptions.create "target", targetHandler captured)
        use router = startRouter 1 target
        do! initialize router
        let first = router.TryAskAsync Forward
        let! reply = capture captured
        let! busy = router.TryAskAsync Forward |> awaitResult
        expectReply -2 busy
        reply.Reply 42
        let! completed = awaitResult first
        expectReply 42 completed
        let second = router.TryAskAsync Forward
        let! pending = capture captured
        router.TryPost Close |> ignore
        let! closed = awaitResult second
        expectReply -1 closed
        check (not (pending.TryReply 99)) "A late reply overwrote closure."
        let! afterClose = router.TryAskAsync Forward |> awaitResult
        expectReply -1 afterClose
        equal 0 captured.Reader.Count
        do! finish router
        do! finish target
    })

    case "target failure closes replies even while the router cannot handle messages" (fun () -> task {
        let captured = Channel.CreateUnbounded<ReplyChannel<int>>()
        use target = TestAgent.Start(AgentOptions.create "target", targetHandler captured)
        use router = startRouter 1 target
        do! initialize router
        let waiting = router.TryAskAsync Forward
        let! _ = capture captured
        let entered, release = gate<unit>(), gate<unit>()
        router.TryPost(HoldRoute(entered, release)) |> ignore
        do! awaitResult entered.Task
        target.TryPost(Crash(InvalidOperationException("target failed"))) |> ignore
        let! result = awaitResult waiting
        expectReply -1 result
        check (not release.Task.IsCompleted) "Router was released before reply cleanup."
        release.SetResult()
        do! finish router
        let! _ = terminal target.Completion
        ()
    })

    case "stopping the router settles replies owned by a still-live caller" (fun () -> task {
        for abort in [false; true] do
            let captured = Channel.CreateUnbounded<ReplyChannel<int>>()
            use target = TestAgent.Start(AgentOptions.create "target", targetHandler captured)
            use router = startRouter 1 target
            do! initialize router
            let forward (_: ReliableAgentContext<ReplyChannel<int>>) reply = task {
                let! result = router.PostAsync(Forward reply)
                equal AgentPostResult.Posted result
            }
            use caller = TestAgent.StartReliable(AgentOptions.create "caller", forward)
            let waiting = caller.TryAskAsync id
            let! _ = capture captured
            if abort then
                router.Abort()
            else
                router.Complete() |> ignore
            let! result = awaitResult waiting
            expectReply -1 result
            let! _ = terminal router.Completion
            check target.IsAcceptingMessages "Reply scope stopped its target."
            do! finish caller
            do! finish target
    })

    case "caller cancellation frees capacity without waiting for the old target reply" (fun () -> task {
        let captured = Channel.CreateUnbounded<ReplyChannel<int>>()
        use target = TestAgent.Start(AgentOptions.create "target", targetHandler captured)
        use router = startRouter 1 target
        do! initialize router
        use cancel = new CancellationTokenSource()
        let waiting = router.TryAskAsync(Forward, cancellationToken = cancel.Token)
        let! old = capture captured
        cancel.Cancel()
        let! canceled = awaitResult waiting
        equal AgentAskResult.Canceled canceled
        let next = router.TryAskAsync Forward
        let! fresh = capture captured
        check (not (old.TryReply 1)) "Canceled reply was overwritten."
        fresh.Reply 2
        let! result = awaitResult next
        expectReply 2 result
        do! finish router
        do! finish target
    })

    case "refused forwarding settles the request and does not consume capacity" (fun () -> task {
        let captured = Channel.CreateUnbounded<ReplyChannel<int>>()
        use target = TestAgent.Start(AgentOptions.create "target", targetHandler captured)
        use router = startRouter 1 target
        do! initialize router
        let! refused = router.TryAskAsync Refuse |> awaitResult
        expectReply -1 refused
        let next = router.TryAskAsync Forward
        let! reply = capture captured
        reply.Reply 5
        let! result = awaitResult next
        expectReply 5 result
        do! finish router
        do! finish target
    })

    case "a forwarding exception settles an external caller and preserves the cause" (fun () -> task {
        let captured = Channel.CreateUnbounded<ReplyChannel<int>>()
        use target = TestAgent.Start(AgentOptions.create "target", targetHandler captured)
        let mutable policies = 0
        let routerOptions = {
            options "router" (AgentMailbox.boundedWait 2) with
                OnError = Some(fun _ ->
                    policies <- policies + 1
                    AgentErrorAction.Continue)
        }
        use router = startRouterWithOptions routerOptions 1 target
        do! initialize router
        let failure = InvalidOperationException("cannot schedule")
        let forward (_: ReliableAgentContext<ReplyChannel<int>>) reply = task {
            let! _ = router.PostAsync(Throw(failure, reply))
            ()
        }
        use caller = TestAgent.StartReliable(AgentOptions.create "caller", forward)
        let! result = caller.TryAskAsync id |> awaitResult
        expectFault failure result
        let! _ = terminal router.Completion
        expectStopFault failure router.StopReason
        equal 0 policies
        equal 1 router.Completion.Exception.InnerExceptions.Count
        check (obj.ReferenceEquals(failure, router.Completion.Exception.InnerExceptions[0])) "Route exception was replaced."
        do! finish caller
        do! finish target
    })

    case "Own delivers every child cause to the active owner's recovery policy" (fun () -> task {
        let original, secondary = InvalidOperationException("child handler"), InvalidOperationException("child cleanup")
        let owned, delivered = gate<unit>(), gate<Result<unit, exn>>()
        let childOptions = { AgentOptions.create "recoverable-child" with OnStopped = Some(fun _ -> raise secondary) }
        use child = TestAgent.Start(childOptions, fun _ (_: int) -> Task.FromException<unit>(original))
        let handle (context: ReliableAgentContext<Result<unit, exn> option>) message = task {
            match message with
            | None ->
                context.Own(child, Some)
                owned.SetResult()
            | Some outcome -> delivered.SetResult outcome
        }
        use parent = TestAgent.StartReliable(AgentOptions.create "recovering-parent", handle)
        parent.TryPost None |> ignore
        do! awaitUnit owned.Task
        child.TryPost 0 |> ignore
        let! outcome = awaitResult delivered.Task
        match outcome with
        | Error (:? AggregateException as error) ->
            equal 2 error.InnerExceptions.Count
            check (obj.ReferenceEquals(original, error.InnerExceptions[0])) "Primary child error order changed."
            check (obj.ReferenceEquals(secondary, error.InnerExceptions[1])) "Secondary child error order changed."
        | other -> failtestf "Expected complete child failure result, got %A" other
        check parent.IsAcceptingMessages "An observed child failure bypassed the owner policy."
        do! finish parent
    })

    case "parent Abort joins child cleanup and retains every child cleanup cause" (fun () -> task {
        let cleanupEntered, cleanupRelease, owned = gate<unit>(), gate<unit>(), gate<unit>()
        let original, secondary = InvalidOperationException("child stopped event"), InvalidOperationException("child stopped callback")
        let childOptions = { AgentOptions.create "owned-cleanup-child" with OnStopped = Some(fun _ -> raise secondary) }
        use child = TestAgent.Start(childOptions, fun _ (_: int) -> task { () })
        child.Stopped.Add(fun _ ->
            cleanupEntered.SetResult()
            cleanupRelease.Task.GetAwaiter().GetResult()
            raise original)
        let handle (context: ReliableAgentContext<int>) message = task {
            if message = 0 then
                context.Own(child, fun _ -> 1)
                owned.SetResult()
        }
        use parent = TestAgent.StartReliable(AgentOptions.create "owned-cleanup-parent", handle)
        parent.TryPost 0 |> ignore
        do! awaitUnit owned.Task
        parent.Abort()
        do! awaitUnit cleanupEntered.Task
        check (not parent.Completion.IsCompleted) "Parent abandoned child cleanup."
        cleanupRelease.SetResult()
        let! _ = terminal parent.Completion
        equal 2 parent.Completion.Exception.InnerExceptions.Count
        for cause in [original; secondary] do
            equal 1 (parent.Completion.Exception.InnerExceptions |> Seq.filter(fun error -> obj.ReferenceEquals(cause, error)) |> Seq.length)
        equal (Some AgentStopReason.Aborted) parent.StopReason
        check child.Completion.IsFaulted "Child cleanup fault was hidden."
        equal 0 parent.QueueLength
    })

    case "startup callback fault settles pending requests and stops before dispatch" (fun () -> task {
        let entered, release = gate<unit>(), gate<unit>()
        let original = InvalidOperationException("startup notification")
        let seen = ConcurrentQueue<int>()
        let options = {
            AgentOptions.create "startup-fault" with
                OnStarted = Some(fun _ ->
                    entered.TrySetResult() |> ignore
                    release.Task.GetAwaiter().GetResult()
                    raise original)
        }
        use agent = TestAgent.Start(options, fun _ reply -> task { seen.Enqueue reply })
        try
            do! awaitUnit entered.Task
            let pending: Task<AgentAskResult<int>> = agent.TryAskAsync(fun _ -> 1)
            equal AgentPostResult.Posted (agent.TryPost 2)
            release.TrySetResult() |> ignore
            let! result = awaitResult pending
            expectFault original result
            let! failure = terminal agent.Completion
            check (failure |> Option.exists (fun actual -> Object.ReferenceEquals(original, actual))) "Startup cause remains original."
            expectStopFault original agent.StopReason
            equal AgentPostResult.Closed (agent.TryPost 3)
            equal 0 seen.Count
            equal 0 agent.QueueLength
        finally
            release.TrySetResult() |> ignore
    })

    case "committed transition callback fault cannot enter recovery or process the next command" (fun () -> task {
        let entered, release = gate<unit>(), gate<unit>()
        let original = InvalidOperationException("transition notification")
        let committed = ConcurrentQueue<int * int>()
        let mutable calls, recoveries = 0, 0
        let options = {
            StatefulAgentOptions.create "transition-fault" with
                OnUnhandled = Some(fun _ ->
                    recoveries <- recoveries + 1
                    StatefulErrorAction.KeepStateAndContinue)
                OnTransition = Some(fun states ->
                    committed.Enqueue states
                    raise original)
        }
        use agent = TestStatefulAgent.Start(options, 0, fun _ state command -> task {
            calls <- calls + 1
            entered.TrySetResult() |> ignore
            do! release.Task
            return StatefulTransition.SetState(state + command)
        })
        try
            equal AgentPostResult.Posted (agent.TryPost 1)
            do! awaitUnit entered.Task
            equal AgentPostResult.Posted (agent.TryPost 10)
            release.TrySetResult() |> ignore
            let! _ = terminal agent.Completion
            expectStopFault original agent.StopReason
            equal [| (0, 1) |] (committed.ToArray())
            equal 1 calls
            equal 0 recoveries
            equal AgentPostResult.Closed (agent.TryPost 20)
            equal 0 agent.QueueLength
        finally
            release.TrySetResult() |> ignore
    })

    case "clean completion stop notifications share sealed reason and retain each failure" (fun () -> task {
        let configured = InvalidOperationException("configured stop")
        let observed = InvalidOperationException("stop event")
        let reasons = ConcurrentQueue<AgentStopReason>()
        use agent = TestAgent.Start({
            AgentOptions.create "stop-notifications" with
                OnStopped = Some(fun (_, reason) ->
                    reasons.Enqueue reason
                    raise configured)
        }, fun _ (_: int) -> task { () })
        agent.Stopped.Add(fun (_, reason) ->
            reasons.Enqueue reason
            raise observed)
        agent.Complete() |> ignore
        let! _ = terminal agent.Completion
        equal (Some AgentStopReason.Completed) agent.StopReason
        equal [| AgentStopReason.Completed; AgentStopReason.Completed |] (reasons.ToArray())
        let errors = agent.Completion.Exception.Flatten().InnerExceptions
        check (errors |> Seq.exists (fun error -> Object.ReferenceEquals(observed, error))) "Event failure remains original."
        check (errors |> Seq.exists (fun error -> Object.ReferenceEquals(configured, error))) "Separate callback is attempted and retained."
    })

    case "primary handler fault joins background and retains cancellation and stop notification failures" (fun () -> task {
        let working, release, canceled = gate<unit>(), gate<unit>(), gate<unit>()
        let primary = InvalidOperationException("primary handler")
        let cancel = InvalidOperationException("owned cancellation")
        let stopped = InvalidOperationException("secondary stopped")
        let mutable joined = false
        let handle (context: ReliableAgentContext<int>) _ = task {
            context.PipeToSelf((fun token -> task {
                use registration = token.Register(fun () ->
                    canceled.TrySetResult() |> ignore
                    raise cancel)
                working.TrySetResult() |> ignore
                do! release.Task
                joined <- true
                return ()
            }), fun _ -> 2)
            do! working.Task
            return raise primary
        }
        use agent = TestAgent.StartReliable({ AgentOptions.create "primary-and-cleanup" with OnStopped = Some(fun _ -> raise stopped) }, handle)
        try
            equal AgentPostResult.Posted (agent.TryPost 1)
            do! awaitUnit working.Task
            do! awaitUnit canceled.Task
            check (not agent.Completion.IsCompleted) "Failure cannot skip the owned background join."
            release.TrySetResult() |> ignore
            let! _ = terminal agent.Completion
            check joined "Owned work was joined despite a cancellation callback failure."
            expectStopFault primary agent.StopReason
            let errors = agent.Completion.Exception.Flatten().InnerExceptions
            for expected in [primary; cancel; stopped] do
                equal 1 (errors |> Seq.filter (fun actual -> Object.ReferenceEquals(expected, actual)) |> Seq.length)
        finally
            release.TrySetResult() |> ignore
    })

    case "mutable recovery callback fault retains handler cause and never reuses changed state" (fun () -> task {
        let entered, release = gate<unit>(), gate<unit>()
        let primary = InvalidOperationException("mutable handler")
        let policy = InvalidOperationException("mutable recovery callback")
        let state = ResizeArray<int>()
        let options = { MutableStatefulAgentOptions.create "mutable-recovery-fault" with OnUnhandled = Some(fun _ -> raise policy) }
        use agent = TestMutableAgent.Start(options, state, fun _ state command -> task {
            state.Add command
            entered.TrySetResult() |> ignore
            do! release.Task
            return raise primary
        })
        try
            equal AgentPostResult.Posted (agent.TryPost 1)
            do! awaitUnit entered.Task
            equal AgentPostResult.Posted (agent.TryPost 2)
            release.TrySetResult() |> ignore
            let! _ = terminal agent.Completion
            expectStopFault primary agent.StopReason
            equal [| 1 |] (state.ToArray())
            equal AgentPostResult.Closed (agent.TryPost 3)
            let errors = agent.Completion.Exception.Flatten().InnerExceptions
            for expected in [primary; policy] do
                check (errors |> Seq.exists (fun actual -> Object.ReferenceEquals(expected, actual))) "Original handler/policy cause is retained."
        finally
            release.TrySetResult() |> ignore
    })


    case "error observer failure overrides Continue after invoking the configured policy" (fun () -> task {
        let entered, release = gate<unit>(), gate<unit>()
        let primary = InvalidOperationException("reported handler")
        let observer = InvalidOperationException("error observer")
        let seen = ConcurrentQueue<int>()
        let mutable policies = 0
        let policy (_, error) =
            check (Object.ReferenceEquals(primary, error)) "Policy still receives original handler fault."
            policies <- policies + 1
            AgentErrorAction.Continue
        let options = { AgentOptions.create "error-observer-fault" with OnError = Some policy }
        use agent = TestAgent.Start(options, fun _ number -> task {
            seen.Enqueue number
            entered.TrySetResult() |> ignore
            do! release.Task
            return raise primary
        })
        agent.Errored.Add(fun _ -> raise observer)
        try
            equal AgentPostResult.Posted (agent.TryPost 1)
            do! awaitUnit entered.Task
            equal AgentPostResult.Posted (agent.TryPost 2)
            release.TrySetResult() |> ignore
            let! _ = terminal agent.Completion
            equal 1 policies
            equal [| 1 |] (seen.ToArray())
            equal 0 agent.QueueLength
            equal AgentPostResult.Closed (agent.TryPost 3)
            expectStopFault primary agent.StopReason
            let errors = agent.Completion.Exception.Flatten().InnerExceptions
            for expected in [primary; observer] do
                equal 1 (errors |> Seq.filter (fun actual -> Object.ReferenceEquals(expected, actual)) |> Seq.length)
        finally
            release.TrySetResult() |> ignore
    })


    case "owned handler cancellation after a background fault settles with that original cause" (fun () -> task {
        let entered, fail = gate<unit>(), gate<unit>()
        let original = InvalidOperationException("background mapper")
        let handle (context: ReliableAgentContext<ReplyChannel<int>>) _ = task {
            context.PipeToSelf((fun _ -> task { do! fail.Task }), fun _ -> raise original)
            entered.TrySetResult() |> ignore
            do! Task.Delay(Timeout.Infinite, context.CancellationToken)
        }
        use agent = TestAgent.StartReliable(AgentOptions.create "background-cancel-handler", handle)
        let current = agent.TryAskAsync id
        do! awaitUnit entered.Task
        let queued = agent.TryAskAsync id
        fail.TrySetResult() |> ignore
        let! result = awaitResult current
        expectFault original result
        let! result = awaitResult queued
        expectFault original result
        let! failure = terminal agent.Completion
        check (failure |> Option.exists (fun actual -> Object.ReferenceEquals(original, actual))) "Completion retains original singleton cause."
        equal 1 agent.Completion.Exception.InnerExceptions.Count
        expectStopFault original agent.StopReason
        equal 0 agent.QueueLength

        let unowned = OperationCanceledException("unowned while healthy")
        use healthy = TestAgent.Start(AgentOptions.create "unowned-cancellation", fun _ (_: int) -> task { return raise unowned })
        healthy.TryPost 1 |> ignore
        let! failure = terminal healthy.Completion
        check (failure |> Option.exists (fun actual -> Object.ReferenceEquals(unowned, actual))) "Unowned cancellation still faults."
        expectStopFault unowned healthy.StopReason
    })

]
