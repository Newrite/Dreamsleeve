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
                      (context: AgentContext<LifecycleMessage>) message = task {
    match message with
    | Attach(own, ready) ->
        if own then context.Own(child, Ended)
        else context.Watch(child, Ended)
        ready.SetResult()
    | Ended result -> ended.SetResult result
}

let private attach own (agent: Agent<LifecycleMessage>) = task {
    let ready = gate<unit>()
    agent.TryPost(Attach(own, ready)) |> ignore
    do! awaitResult ready.Task
}

let private taskLifecycle (completion: Task) (ended: TaskCompletionSource<Result<unit, exn>>)
                          (context: AgentContext<LifecycleMessage>) message = task {
    match message with
    | Attach(_, ready) ->
        context.Watch(completion, Ended)
        ready.SetResult()
    | Ended result -> ended.SetResult result
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
    | Capture reply -> check (captured.Writer.TryWrite reply) "Capture channel closed."
    | Crash error -> return raise error
}

type private RouteMessage =
    | Initialize of TaskCompletionSource<unit>
    | Forward of ReplyChannel<int>
    | Refuse of ReplyChannel<int>
    | Throw of exn * ReplyChannel<int>
    | Close
    | HoldRoute of TaskCompletionSource<unit> * TaskCompletionSource<unit>

let private startRouter capacity target =
    let mutable scope: AgentReplyScope<int> option = None
    let handle (context: AgentContext<RouteMessage>) message = task {
        match message with
        | Initialize ready ->
            scope <- Some (AgentReplyScope.create context target capacity -1 -2)
            ready.SetResult()
        | Forward reply -> scope.Value.Forward(reply, fun forwarded -> target.TryPost(Capture forwarded) = AgentPostResult.Posted)
        | Refuse reply -> scope.Value.Forward(reply, fun _ -> false)
        | Throw(error, reply) -> scope.Value.Forward(reply, fun _ -> raise error)
        | Close -> scope.Value.Close()
        | HoldRoute(entered, release) ->
            entered.SetResult()
            do! release.Task.WaitAsync context.CancellationToken
    }
    Agent.Start(options "router" (AgentMailbox.boundedWait 2), handle)

let private initialize (router: Agent<RouteMessage>) = task {
    let ready = gate<unit>()
    router.TryPost(Initialize ready) |> ignore
    do! awaitResult ready.Task
}

let private capture (replies: Channel<ReplyChannel<int>>) = replies.Reader.ReadAsync().AsTask().WaitAsync guard

let tests = testList "Lifetimes" [
    case "owned child is aborted and joined through its asynchronous cleanup" (fun () -> task {
        let entered, cleaning, release = gate<unit>(), gate<unit>(), gate<unit>()
        let childHandler (context: AgentContext<unit>) () = task {
            entered.SetResult()
            try do! Task.Delay(Timeout.Infinite, context.CancellationToken)
            with :? OperationCanceledException -> ()
            cleaning.SetResult()
            do! release.Task
        }
        use child = Agent.Start(AgentOptions.create "owned", childHandler)
        child.TryPost() |> ignore
        do! awaitResult entered.Task
        let ended = gate<Result<unit, exn>>()
        use owner = Agent.Start(AgentOptions.create "owner", lifecycle child ended)
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
        use child = Agent.Start(AgentOptions.create "child", ordinaryHandler (ConcurrentQueue<int>()))
        child.TryPost(AgentTests.Message.FailMessage failure) |> ignore
        let! _ = terminal child.Completion
        let ended = gate<Result<unit, exn>>()
        use owner = Agent.Start(AgentOptions.create "owner", lifecycle child ended)
        do! attach true owner
        let! result = awaitResult ended.Task
        match result with
        | Error error -> check (obj.ReferenceEquals(failure, error)) "Original child fault was lost."
        | Ok () -> failwith "Child failure became success."
        do! finish owner
    })

    case "Watch detaches on Complete and Abort without stopping the shared target" (fun () -> task {
        for abort in [false; true] do
            use child = Agent.Start(AgentOptions.create "shared", ordinaryHandler (ConcurrentQueue<int>()))
            let ended = gate<Result<unit, exn>>()
            use owner = Agent.Start(AgentOptions.create "observer", lifecycle child ended)
            do! attach false owner
            if abort then owner.Abort() else owner.Complete() |> ignore
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
        use owner = Agent.Start(AgentOptions.create "observer", taskLifecycle (Task.FromException failure) ended)
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
            use owner = Agent.Start(AgentOptions.create "observer", taskLifecycle pending.Task ended)
            do! attach false owner
            if abort then owner.Abort() else owner.Complete() |> ignore
            let! _ = terminal owner.Completion
            check (not pending.Task.IsCompleted) "Watching changed the dependency lifetime."
            pending.SetResult()
            check (not ended.Task.IsCompleted) "Detached task watcher delivered a stop message."
    })

    case "forwarded replies are bounded, reclaim settled slots and preserve the first result" (fun () -> task {
        let captured = Channel.CreateUnbounded<ReplyChannel<int>>()
        use target = Agent.Start(AgentOptions.create "target", targetHandler captured)
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
        use target = Agent.Start(AgentOptions.create "target", targetHandler captured)
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
            use target = Agent.Start(AgentOptions.create "target", targetHandler captured)
            use router = startRouter 1 target
            do! initialize router
            let forward (_: AgentContext<ReplyChannel<int>>) reply = task {
                let! result = router.PostAsync(Forward reply)
                equal AgentPostResult.Posted result
            }
            use caller = Agent.Start(AgentOptions.create "caller", forward)
            let waiting = caller.TryAskAsync id
            let! _ = capture captured
            if abort then router.Abort() else router.Complete() |> ignore
            let! result = awaitResult waiting
            expectReply -1 result
            let! _ = terminal router.Completion
            check target.IsAcceptingMessages "Reply scope stopped its target."
            do! finish caller
            do! finish target
    })

    case "caller cancellation frees capacity without waiting for the old target reply" (fun () -> task {
        let captured = Channel.CreateUnbounded<ReplyChannel<int>>()
        use target = Agent.Start(AgentOptions.create "target", targetHandler captured)
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
        use target = Agent.Start(AgentOptions.create "target", targetHandler captured)
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
        use target = Agent.Start(AgentOptions.create "target", targetHandler captured)
        use router = startRouter 1 target
        do! initialize router
        let failure = InvalidOperationException("cannot schedule")
        let forward (_: AgentContext<ReplyChannel<int>>) reply = task {
            let! _ = router.PostAsync(Throw(failure, reply))
            ()
        }
        use caller = Agent.Start(AgentOptions.create "caller", forward)
        let! result = caller.TryAskAsync id |> awaitResult
        expectFault failure result
        let! _ = terminal router.Completion
        expectStopFault failure router.StopReason
        do! finish caller
        do! finish target
    })
]
