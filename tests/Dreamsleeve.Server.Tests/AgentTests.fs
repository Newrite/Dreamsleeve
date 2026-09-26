module Dreamsleeve.Server.Tests.AgentTests

open Expecto

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Dreamsleeve.Agent

// Gates, rather than sleeps, establish ordering. The guard detects deadlocks;
// short timeouts appear only in tests whose subject is timeout behavior.
let guard = TimeSpan.FromSeconds 5.0
let gate<'T> () = TaskCompletionSource<'T>(TaskCreationOptions.RunContinuationsAsynchronously)
let check condition message = if not condition then failwith message
let equal expected actual =
    if expected <> actual then failwithf "Expected %A, received %A" expected actual

let awaitResult (work: Task<'T>) = work.WaitAsync guard
let awaitUnit (work: Task) = work.WaitAsync guard

let terminal (work: Task) = task {
    let mutable error = None
    try do! work.WaitAsync guard
    with ex -> error <- Some ex
    check work.IsCompleted "Agent did not reach terminal state within the guard."
    return error
}

let eventually predicate = task {
    let elapsed = Stopwatch.StartNew()
    while not (predicate ()) do
        check (elapsed.Elapsed < guard) "Condition did not become true within the guard."
        do! Task.Yield()
}

let expectReply expected = function
    | AgentAskResult.Replied value -> equal expected value
    | result -> failwithf "Expected Replied %A, received %A" expected result

let expectFault (expected: exn) = function
    | AgentAskResult.Faulted actual ->
        check (Object.ReferenceEquals(expected, actual)) "The original request failure was not preserved."
    | result -> failwithf "Expected Faulted, received %A" result

let expectStopFault (expected: exn) = function
    | Some (AgentStopReason.Faulted actual) ->
        check (Object.ReferenceEquals(expected, actual)) "The original stop failure was not preserved."
    | reason -> failwithf "Expected Faulted stop reason, received %A" reason

let options name mailbox = { AgentOptions.create name with Mailbox = mailbox }

type Message =
    | Hold of entered: TaskCompletionSource<unit> * release: TaskCompletionSource<unit>
    | Record of int
    | Request of int * ReplyChannel<int>
    | FailRequest of exn * ReplyChannel<int>
    | FailMessage of exn
    | CaptureReply of TaskCompletionSource<ReplyChannel<int>> * ReplyChannel<int>

let ordinaryHandler (seen: ConcurrentQueue<int>) (context: AgentContext<Message>) message = task {
    match message with
    | Hold (entered, release) ->
        entered.TrySetResult() |> ignore
        do! release.Task.WaitAsync context.CancellationToken
    | Record number -> seen.Enqueue number
    | Request (number, reply) ->
        seen.Enqueue number
        reply.Reply number
    | FailRequest (error, _) | FailMessage error -> return raise error
    | CaptureReply (captured, reply) -> captured.TrySetResult reply |> ignore
}

let holdAgent (agent: Agent<Message>) = task {
    let entered, release = gate<unit> (), gate<unit> ()
    equal AgentPostResult.Posted (agent.TryPost(Hold(entered, release)))
    do! awaitResult entered.Task
    return release
}

let serialAndReply () = task {
    let seen = ConcurrentQueue<int>()
    use agent = Agent.Start(AgentOptions.create "fifo", ordinaryHandler seen)
    let! release = holdAgent agent
    equal AgentPostResult.Posted (agent.TryPost(Record 1))
    equal AgentPostResult.Posted (agent.TryPost(Record 2))
    let request = agent.TryAskAsync(fun reply -> Request(3, reply))
    do! eventually (fun () -> agent.QueueLength = 3)
    release.SetResult()
    let! result = awaitResult request
    expectReply 3 result
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
    equal [| 1; 2; 3 |] (seen.ToArray())
    equal 0 agent.QueueLength
}

let boundedBackpressure () = task {
    let seen = ConcurrentQueue<int>()
    use agent = Agent.Start(options "backpressure" (AgentMailbox.boundedWait 1), ordinaryHandler seen)
    let! release = holdAgent agent
    equal AgentPostResult.Posted (agent.TryPost(Record 1))
    equal AgentPostResult.Full (agent.TryPost(Record 2))
    use cancellation = new CancellationTokenSource()
    let blocked = agent.PostAsync(Record 3, cancellationToken = cancellation.Token)
    check (not blocked.IsCompleted) "A write to a full Wait mailbox should wait."
    cancellation.Cancel()
    let! result = awaitResult blocked
    equal AgentPostResult.Canceled result
    release.SetResult()
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
    equal [| 1 |] (seen.ToArray())
}

let askTimeoutIncludesAdmission () = task {
    let seen = ConcurrentQueue<int>()
    use agent = Agent.Start(options "admission-timeout" (AgentMailbox.boundedWait 1), ordinaryHandler seen)
    let! release = holdAgent agent
    equal AgentPostResult.Posted (agent.TryPost(Record 1))
    let! result =
        agent.TryAskAsync((fun reply -> Request(2, reply)), timeout = TimeSpan.FromMilliseconds 80.0)
        |> awaitResult
    equal AgentAskResult.TimedOut result
    equal 1 agent.QueueLength
    release.SetResult()
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
    equal [| 1 |] (seen.ToArray())
}

let replyTimeoutSettlesChannel () = task {
    let captured = gate<ReplyChannel<int>> ()
    use agent = Agent.Start(AgentOptions.create "reply-timeout", ordinaryHandler (ConcurrentQueue<int>()))
    let request = agent.TryAskAsync((fun reply -> CaptureReply(captured, reply)), timeout = TimeSpan.FromMilliseconds 100.0)
    let! reply = awaitResult captured.Task
    let! result = awaitResult request
    equal AgentAskResult.TimedOut result
    check reply.IsCompleted "A timed-out request left its reply channel open."
    check (not (reply.TryReply 123)) "A late reply should lose the one-shot race."
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
}

let preCanceledAskHasNoSideEffects () = task {
    let seen = ConcurrentQueue<int>()
    use agent = Agent.Start(AgentOptions.create "pre-cancel", ordinaryHandler seen)
    use cancellation = new CancellationTokenSource()
    cancellation.Cancel()
    let mutable built = false
    let! result = agent.TryAskAsync((fun reply -> built <- true; Request(1, reply)), cancellationToken = cancellation.Token) |> awaitResult
    equal AgentAskResult.Canceled result
    check (not built) "A pre-canceled Ask invoked the message builder."
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
    equal 0 seen.Count
}

let invalidAskTimeoutHasNoSideEffects () = task {
    let seen = ConcurrentQueue<int>()
    use agent = Agent.Start(AgentOptions.create "invalid-timeout", ordinaryHandler seen)
    for timeout in [ TimeSpan.FromMilliseconds -2.0; TimeSpan.MaxValue ] do
        let mutable built = false
        let! result = agent.TryAskAsync((fun reply -> built <- true; Request(1, reply)), timeout = timeout) |> awaitResult
        match result with
        | AgentAskResult.Faulted (:? ArgumentOutOfRangeException) -> ()
        | other -> failwithf "Expected timeout validation failure, received %A" other
        check (not built) "An invalid timeout invoked the message builder."
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
    equal 0 seen.Count
}

let builderFailureIsIsolated () = task {
    let expected = InvalidOperationException "builder" :> exn
    use agent = Agent.Start(AgentOptions.create "builder", ordinaryHandler (ConcurrentQueue<int>()))
    let! result = agent.TryAskAsync<int>(fun _ -> raise expected) |> awaitResult
    expectFault expected result
    let! next = agent.TryAskAsync(fun reply -> Request(5, reply)) |> awaitResult
    expectReply 5 next
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
}

let dropWriteSettlesRequest () = task {
    let seen = ConcurrentQueue<int>()
    use agent = Agent.Start(options "drop-write" (AgentMailbox.bounded 1 BoundedChannelFullMode.DropWrite), ordinaryHandler seen)
    let! release = holdAgent agent
    equal AgentPostResult.Posted (agent.TryPost(Record 1))
    equal AgentPostResult.Dropped (agent.TryPost(Record 2))
    let! request = agent.TryAskAsync(fun reply -> Request(3, reply)) |> awaitResult
    equal AgentAskResult.Dropped request
    equal 1 agent.QueueLength
    equal 2L agent.DroppedCount
    release.SetResult()
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
    equal [| 1 |] (seen.ToArray())
    equal 0 agent.QueueLength
}

let evictionSettlesRequest fullMode () = task {
    let seen = ConcurrentQueue<int>()
    use agent = Agent.Start(options (string fullMode) (AgentMailbox.bounded 2 fullMode), ordinaryHandler seen)
    let! release = holdAgent agent
    let evictedRequest =
        if fullMode = BoundedChannelFullMode.DropOldest then
            let request = agent.TryAskAsync(fun reply -> Request(10, reply))
            equal AgentPostResult.Posted (agent.TryPost(Record 20))
            request
        else
            equal AgentPostResult.Posted (agent.TryPost(Record 20))
            agent.TryAskAsync(fun reply -> Request(10, reply))
    do! eventually (fun () -> agent.QueueLength = 2)
    equal AgentPostResult.Posted (agent.TryPost(Record 30))
    let! result = awaitResult evictedRequest
    equal AgentAskResult.Dropped result
    equal 2 agent.QueueLength
    equal 1L agent.DroppedCount
    release.SetResult()
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
    equal [| 20; 30 |] (seen.ToArray())
    equal 0 agent.QueueLength
}

let dropAccounting () = task {
    let seen = ConcurrentQueue<int>()
    use agent = Agent.Start(options "drop-accounting" (AgentMailbox.bounded 3 BoundedChannelFullMode.DropOldest), ordinaryHandler seen)
    let! release = holdAgent agent
    for number in 1 .. 1000 do equal AgentPostResult.Posted (agent.TryPost(Record number))
    equal 3 agent.QueueLength
    equal 997L agent.DroppedCount
    release.SetResult()
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
    equal [| 998; 999; 1000 |] (seen.ToArray())
    equal 0 agent.QueueLength
}

let requestFailureCanContinue () = task {
    let expected = InvalidOperationException "request handler" :> exn
    let mutable errors = 0
    let config = { AgentOptions.create "continue" with OnError = Some(fun _ -> errors <- errors + 1; AgentErrorAction.Continue) }
    use agent = Agent.Start(config, ordinaryHandler (ConcurrentQueue<int>()))
    let! result = agent.TryAskAsync(fun reply -> FailRequest(expected, reply)) |> awaitResult
    expectFault expected result
    let! next = agent.TryAskAsync(fun reply -> Request(7, reply)) |> awaitResult
    expectReply 7 next
    equal 1 errors
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
}

let abortDiscardsQueuedWork () = task {
    let seen = ConcurrentQueue<int>()
    use agent = Agent.Start(AgentOptions.create "abort-queue", ordinaryHandler seen)
    let! _ = holdAgent agent
    equal AgentPostResult.Posted (agent.TryPost(Record 1))
    let request = agent.TryAskAsync(fun reply -> Request(2, reply))
    do! eventually (fun () -> agent.QueueLength = 2)
    agent.Abort()
    let! result = awaitResult request
    equal AgentAskResult.Canceled result
    let! _ = terminal agent.Completion
    check agent.Completion.IsCanceled "Abort should cancel Completion."
    equal (Some AgentStopReason.Aborted) agent.StopReason
    equal 0 agent.QueueLength
    equal 0 seen.Count
}

let cooperativeAbortHasConsistentNotifications () = task {
    let started = gate<unit> ()
    let stopped = ConcurrentQueue<AgentStopReason>()
    let mutable errors = 0
    let config =
        { AgentOptions.create "cooperative-abort" with
            OnError = Some(fun _ -> errors <- errors + 1; AgentErrorAction.Stop)
            OnStopped = Some(fun (_, reason) -> stopped.Enqueue reason) }
    use agent = Agent.Start(config, fun context () -> task {
        started.TrySetResult() |> ignore
        do! Task.Delay(Timeout.Infinite, context.CancellationToken)
    })
    equal AgentPostResult.Posted (agent.TryPost())
    do! awaitResult started.Task
    agent.Abort()
    let! _ = terminal agent.Completion
    check agent.Completion.IsCanceled "Abort should cancel Completion."
    equal (Some AgentStopReason.Aborted) agent.StopReason
    equal [| AgentStopReason.Aborted |] (stopped.ToArray())
    equal 0 errors
    agent.Abort()
    equal [| AgentStopReason.Aborted |] (stopped.ToArray())
}

let gracefulCompleteDrainsAndIsStable () = task {
    let seen = ConcurrentQueue<int>()
    let stopped = ConcurrentQueue<AgentStopReason>()
    let config = { AgentOptions.create "complete" with OnStopped = Some(fun (_, reason) -> stopped.Enqueue reason) }
    use agent = Agent.Start(config, ordinaryHandler seen)
    let! release = holdAgent agent
    equal AgentPostResult.Posted (agent.TryPost(Record 1))
    let request = agent.TryAskAsync(fun reply -> Request(2, reply))
    do! eventually (fun () -> agent.QueueLength = 2)
    check (agent.Complete()) "First Complete should close the mailbox."
    check (not (agent.Complete())) "Repeated Complete should report no change."
    equal AgentPostResult.Closed (agent.TryPost(Record 3))
    release.SetResult()
    let! result = awaitResult request
    expectReply 2 result
    do! awaitUnit agent.Completion
    equal [| 1; 2 |] (seen.ToArray())
    equal (Some AgentStopReason.Completed) agent.StopReason
    equal [| AgentStopReason.Completed |] (stopped.ToArray())
    agent.Abort()
    check agent.Completion.IsCompletedSuccessfully "Abort after completion changed the terminal outcome."
    equal (Some AgentStopReason.Completed) agent.StopReason
    equal [| AgentStopReason.Completed |] (stopped.ToArray())
}

let throwingCancellationCallbackCannotStrandWriter () = task {
    let entered, release = gate<unit> (), gate<unit> ()
    let seen = ConcurrentQueue<int>()
    use agent = Agent.Start(options "throwing-cancel" (AgentMailbox.boundedWait 1), fun context number -> task {
        if number = 0 then
            use registration = context.CancellationToken.Register(fun () -> raise (InvalidOperationException "cancel callback"))
            entered.TrySetResult() |> ignore
            // Deliberately noncooperative: Abort must release writers before this finishes.
            do! release.Task
        else seen.Enqueue number
    })
    try
        equal AgentPostResult.Posted (agent.TryPost 0)
        do! awaitResult entered.Task
        equal AgentPostResult.Posted (agent.TryPost 1)
        let blocked = agent.PostAsync 2
        check (not blocked.IsCompleted) "Expected a blocked bounded writer."
        agent.Abort()
        let! result = awaitResult blocked
        equal AgentPostResult.Closed result
        check (not agent.Completion.IsCompleted) "Abort cannot forcibly terminate a running handler."
    finally
        release.TrySetResult() |> ignore
    let! _ = terminal agent.Completion
    equal 0 seen.Count
    equal 0 agent.QueueLength
    equal (Some AgentStopReason.Aborted) agent.StopReason
}

let faultDiscardsAndSettlesQueuedRequests () = task {
    let seen = ConcurrentQueue<int>()
    let expected = InvalidOperationException "stop on error" :> exn
    use agent = Agent.Start(AgentOptions.create "fault-discard", ordinaryHandler seen)
    let! release = holdAgent agent
    equal AgentPostResult.Posted (agent.TryPost(FailMessage expected))
    let request = agent.TryAskAsync(fun reply -> Request(2, reply))
    do! eventually (fun () -> agent.QueueLength = 2)
    release.SetResult()
    let! result = awaitResult request
    expectFault expected result
    let! _ = terminal agent.Completion
    check agent.Completion.IsFaulted "Handler Stop should fault Completion."
    expectStopFault expected agent.StopReason
    equal 0 seen.Count
    equal 0 agent.QueueLength
    equal AgentPostResult.Closed (agent.TryPost(Record 3))
}

let unansweredRequestSettlesOnComplete () = task {
    let captured = gate<ReplyChannel<int>> ()
    use agent = Agent.Start(AgentOptions.create "unanswered", ordinaryHandler (ConcurrentQueue<int>()))
    let request = agent.TryAskAsync(fun reply -> CaptureReply(captured, reply))
    let! reply = awaitResult captured.Task
    agent.Complete() |> ignore
    let! result = awaitResult request
    equal AgentAskResult.Closed result
    do! awaitUnit agent.Completion
    check reply.IsCompleted "A terminal agent left an unanswered reply open."
}

type StateCommand =
    | GateState of TaskCompletionSource<unit> * TaskCompletionSource<unit>
    | Add of int
    | Explode of exn
    | Finish
    | AddReply of int * ReplyChannel<int>
    | ExplodeReply of exn * ReplyChannel<int>

let stateHandler (seen: ConcurrentQueue<int>) (context: StatefulAgentContext) state command = task {
    match command with
    | GateState (entered, release) ->
        entered.TrySetResult() |> ignore
        do! release.Task.WaitAsync context.CancellationToken
        return StatefulTransition.Stay
    | Add number ->
        seen.Enqueue number
        return StatefulTransition.SetState(state + number)
    | Explode error | ExplodeReply (error, _) -> return raise error
    | Finish -> return StatefulTransition.Stop
    | AddReply (number, reply) ->
        let next = state + number
        reply.Reply next
        return StatefulTransition.SetState next
}

let mutableHandler (seen: ConcurrentQueue<int>) (context: StatefulAgentContext) (state: ResizeArray<int>) command = task {
    match command with
    | GateState (entered, release) ->
        entered.TrySetResult() |> ignore
        do! release.Task.WaitAsync context.CancellationToken
        return MutableStatefulTransition.Stay
    | Add number ->
        state.Add number
        seen.Enqueue number
        return MutableStatefulTransition.Stay
    | Explode error | ExplodeReply (error, _) -> return raise error
    | Finish -> return MutableStatefulTransition.Stop
    | AddReply (number, reply) ->
        state.Add number
        reply.Reply(state |> Seq.sum)
        return MutableStatefulTransition.Stay
}

let statefulErrorStopDiscards () = task {
    let seen = ConcurrentQueue<int>()
    let expected = InvalidOperationException "state error" :> exn
    let config = { StatefulAgentOptions.create "state-stop" with OnUnhandled = Some(fun _ -> StatefulErrorAction.Stop) }
    use agent = StatefulAgent.Start(config, 0, stateHandler seen)
    let entered, release = gate<unit> (), gate<unit> ()
    equal AgentPostResult.Posted (agent.TryPost(GateState(entered, release)))
    do! awaitResult entered.Task
    equal AgentPostResult.Posted (agent.TryPost(Explode expected))
    equal AgentPostResult.Posted (agent.TryPost(Add 7))
    release.SetResult()
    let! _ = terminal agent.Completion
    check agent.Completion.IsFaulted "Stateful error Stop became graceful completion."
    expectStopFault expected agent.StopReason
    equal 0 seen.Count
    equal 0 agent.QueueLength
}

let mutableErrorStopDiscards () = task {
    let seen = ConcurrentQueue<int>()
    let expected = InvalidOperationException "mutable error" :> exn
    let config = { MutableStatefulAgentOptions.create "mutable-stop" with OnUnhandled = Some(fun _ -> MutableStatefulErrorAction.Stop) }
    use agent = MutableStatefulAgent.Start(config, ResizeArray<int>(), mutableHandler seen)
    let entered, release = gate<unit> (), gate<unit> ()
    equal AgentPostResult.Posted (agent.TryPost(GateState(entered, release)))
    do! awaitResult entered.Task
    equal AgentPostResult.Posted (agent.TryPost(Explode expected))
    equal AgentPostResult.Posted (agent.TryPost(Add 7))
    release.SetResult()
    let! _ = terminal agent.Completion
    check agent.Completion.IsFaulted "Mutable error Stop became graceful completion."
    expectStopFault expected agent.StopReason
    equal 0 seen.Count
    equal 0 agent.QueueLength
}

let successfulStopTransitionsDrain () = task {
    let stateSeen, mutableSeen = ConcurrentQueue<int>(), ConcurrentQueue<int>()
    use stateAgent = StatefulAgent.Start(StatefulAgentOptions.create "state-transition", 0, stateHandler stateSeen)
    use mutableAgent = MutableStatefulAgent.Start(MutableStatefulAgentOptions.create "mutable-transition", ResizeArray<int>(), mutableHandler mutableSeen)
    let entered1, release1 = gate<unit> (), gate<unit> ()
    let entered2, release2 = gate<unit> (), gate<unit> ()
    equal AgentPostResult.Posted (stateAgent.TryPost(GateState(entered1, release1)))
    equal AgentPostResult.Posted (mutableAgent.TryPost(GateState(entered2, release2)))
    do! awaitResult entered1.Task
    do! awaitResult entered2.Task
    equal AgentPostResult.Posted (stateAgent.TryPost Finish)
    equal AgentPostResult.Posted (stateAgent.TryPost(Add 1))
    equal AgentPostResult.Posted (mutableAgent.TryPost Finish)
    equal AgentPostResult.Posted (mutableAgent.TryPost(Add 2))
    release1.SetResult()
    release2.SetResult()
    do! awaitUnit stateAgent.Completion
    do! awaitUnit mutableAgent.Completion
    equal [| 1 |] (stateSeen.ToArray())
    equal [| 2 |] (mutableSeen.ToArray())
    equal (Some AgentStopReason.Completed) stateAgent.StopReason
    equal (Some AgentStopReason.Completed) mutableAgent.StopReason
}

let queryFailuresAreIsolated () = task {
    let expected = InvalidOperationException "projection" :> exn
    use stateAgent = StatefulAgent.Start(StatefulAgentOptions.create "state-query", 10, stateHandler (ConcurrentQueue<int>()))
    use mutableAgent = MutableStatefulAgent.Start(MutableStatefulAgentOptions.create "mutable-query", ResizeArray<int>([ 3; 4 ]), mutableHandler (ConcurrentQueue<int>()))
    let! stateFailure = stateAgent.TryReadAsync<int>(fun _ -> raise expected) |> awaitResult
    let! mutableFailure = mutableAgent.TryReadAsync<int>(fun _ -> raise expected) |> awaitResult
    expectFault expected stateFailure
    expectFault expected mutableFailure
    let! stateValue = stateAgent.ReadAsync id |> awaitResult
    let! mutableValue = mutableAgent.ReadAsync(fun state -> state |> Seq.sum) |> awaitResult
    equal 10 stateValue
    equal 7 mutableValue
    stateAgent.Complete() |> ignore
    mutableAgent.Complete() |> ignore
    do! awaitUnit stateAgent.Completion
    do! awaitUnit mutableAgent.Completion
}

let statefulPolicyPrecedenceAndRecovery () = task {
    let transitions = ConcurrentQueue<int * int>()
    let mutable baseCalls = 0
    let config =
        { StatefulAgentOptions.create "policy-state" with
            AgentOptions = { AgentOptions.create "policy-state" with OnError = Some(fun _ -> baseCalls <- baseCalls + 1; AgentErrorAction.Stop) }
            OnUnhandled = Some(fun _ -> StatefulErrorAction.ReplaceStateAndContinue 100)
            OnTransition = Some transitions.Enqueue }
    use agent = StatefulAgent.Start(config, 1, stateHandler (ConcurrentQueue<int>()))
    equal AgentPostResult.Posted (agent.TryPost(Explode(InvalidOperationException "recover")))
    equal AgentPostResult.Posted (agent.TryPost(Add 3))
    let! value = agent.ReadAsync id |> awaitResult
    equal 103 value
    equal 0 baseCalls
    equal [| (1, 100); (100, 103) |] (transitions.ToArray())
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
}

let basePolicyFallbackForBothWrappers () = task {
    let mutable calls = 0
    let baseOptions = { AgentOptions.create "fallback" with OnError = Some(fun _ -> Interlocked.Increment(&calls) |> ignore; AgentErrorAction.Continue) }
    let stateOptions = { StatefulAgentOptions.create "state-fallback" with AgentOptions = baseOptions }
    let mutableOptions = { MutableStatefulAgentOptions.create "mutable-fallback" with AgentOptions = baseOptions }
    use stateAgent = StatefulAgent.Start(stateOptions, 1, stateHandler (ConcurrentQueue<int>()))
    use mutableAgent = MutableStatefulAgent.Start(mutableOptions, ResizeArray<int>([ 1 ]), mutableHandler (ConcurrentQueue<int>()))
    let error = InvalidOperationException "fallback failure" :> exn
    equal AgentPostResult.Posted (stateAgent.TryPost(Explode error))
    equal AgentPostResult.Posted (mutableAgent.TryPost(Explode error))
    let! stateValue = stateAgent.ReadAsync id |> awaitResult
    let! mutableValue = mutableAgent.ReadAsync(fun state -> state |> Seq.sum) |> awaitResult
    equal 1 stateValue
    equal 1 mutableValue
    equal 2 calls
    stateAgent.Complete() |> ignore
    mutableAgent.Complete() |> ignore
    do! awaitUnit stateAgent.Completion
    do! awaitUnit mutableAgent.Completion
}

let statefulAtomicCommandReplies () = task {
    let error = InvalidOperationException "state request" :> exn
    let config = { StatefulAgentOptions.create "state-ask" with OnUnhandled = Some(fun _ -> StatefulErrorAction.KeepStateAndContinue) }
    use agent = StatefulAgent.Start(config, 10, stateHandler (ConcurrentQueue<int>()))
    let! value = agent.AskAsync(fun reply -> AddReply(5, reply)) |> awaitResult
    equal 15 value
    let! failed = agent.TryAskAsync(fun reply -> ExplodeReply(error, reply)) |> awaitResult
    expectFault error failed
    let! current = agent.ReadAsync id |> awaitResult
    equal 15 current
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
}

let mutableAtomicCommandReplies () = task {
    let error = InvalidOperationException "mutable request" :> exn
    let config = { MutableStatefulAgentOptions.create "mutable-ask" with OnUnhandled = Some(fun _ -> MutableStatefulErrorAction.Continue) }
    use agent = MutableStatefulAgent.Start(config, ResizeArray<int>([ 10 ]), mutableHandler (ConcurrentQueue<int>()))
    let! value = agent.AskAsync(fun reply -> AddReply(5, reply)) |> awaitResult
    equal 15 value
    let! failed = agent.TryAskAsync(fun reply -> ExplodeReply(error, reply)) |> awaitResult
    expectFault error failed
    let! current = agent.ReadAsync(fun state -> state |> Seq.sum) |> awaitResult
    equal 15 current
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
}

let synchronousIdleAbortCancelsLifetime () = task {
    // There is no public "reader is awaiting" hook. A completed handler handshake
    // and a yield give the loop a chance to park. Bounded repetitions exercise
    // both channel implementations without relying on an arbitrary sleep.
    let mailboxes =
        [ AgentMailbox.unboundedAllowSync
          AgentMailbox.Bounded(1, BoundedChannelFullMode.Wait, true) ]
    for mailbox in mailboxes do
        for iteration in 1 .. 50 do
            let captured = gate<CancellationToken * CancellationTokenRegistration> ()
            let mutable callbacks = 0
            let stopped = ConcurrentQueue<AgentStopReason>()
            let config =
                { options $"sync-idle-{iteration}" mailbox with
                    OnStopped = Some(fun (_, reason) -> stopped.Enqueue reason) }
            use agent = Agent.Start(config, fun context () -> task {
                let token = context.CancellationToken
                let registration = token.Register(fun () -> Interlocked.Increment(&callbacks) |> ignore)
                captured.TrySetResult(token, registration) |> ignore
            })
            equal AgentPostResult.Posted (agent.TryPost())
            let! token, registration = awaitResult captured.Task
            use registration = registration
            do! Task.Yield()
            agent.Abort()
            let! _ = terminal agent.Completion
            check agent.Completion.IsCanceled "Synchronous Abort should cancel Completion."
            check token.IsCancellationRequested "Idle Abort finalized before canceling its lifetime token."
            equal 1 (Volatile.Read(&callbacks))
            equal (Some AgentStopReason.Aborted) agent.StopReason
            equal [| AgentStopReason.Aborted |] (stopped.ToArray())
}

let concurrentTerminalRequestsRemainConsistent () = task {
    let entered, release, startRace = gate<unit> (), gate<unit> (), gate<unit> ()
    let registered = gate<CancellationTokenRegistration> ()
    let mutable callbacks = 0
    let stopped = ConcurrentQueue<AgentStopReason>()
    let config =
        { options "concurrent-stop" AgentMailbox.unboundedAllowSync with
            OnStopped = Some(fun (_, reason) -> stopped.Enqueue reason) }
    use agent = Agent.Start(config, fun context () -> task {
        let registration = context.CancellationToken.Register(fun () -> Interlocked.Increment(&callbacks) |> ignore)
        registered.TrySetResult registration |> ignore
        entered.TrySetResult() |> ignore
        // Keep completion open until all concurrent lifecycle calls have returned.
        do! release.Task
    })
    equal AgentPostResult.Posted (agent.TryPost())
    let! registration = awaitResult registered.Task
    // Keep the registration alive until Completion; disposing it in the handler
    // would intentionally be allowed to unregister a not-yet-started callback.
    use registration = registration
    try
        do! awaitResult entered.Task
        let callers =
            [| for index in 0 .. 15 -> task {
                do! startRace.Task
                if index % 2 = 0 then agent.Complete() |> ignore
                else agent.Abort()
            } |]
        startRace.SetResult()
        let! _ = Task.WhenAll callers |> awaitResult
        check (not agent.IsAcceptingMessages) "Concurrent terminal calls left admission open."
    finally
        release.TrySetResult() |> ignore
    let! _ = terminal agent.Completion
    check agent.Completion.IsCanceled "An Abort before the held handler finishes must win over draining."
    equal (Some AgentStopReason.Aborted) agent.StopReason
    equal [| AgentStopReason.Aborted |] (stopped.ToArray())
    equal 1 (Volatile.Read(&callbacks))
    agent.Abort()
    agent.Complete() |> ignore
    equal [| AgentStopReason.Aborted |] (stopped.ToArray())
}

let private scenarios : (string * (unit -> Task<unit>)) list =
    [ "Serial FIFO handling and reply", serialAndReply
      "Bounded backpressure and canceled writer", boundedBackpressure
      "Ask timeout includes mailbox admission", askTimeoutIncludesAdmission
      "Reply timeout settles one-shot channel", replyTimeoutSettlesChannel
      "Pre-canceled Ask does not invoke builder", preCanceledAskHasNoSideEffects
      "Invalid timeout has no side effects", invalidAskTimeoutHasNoSideEffects
      "Message builder failure is isolated", builderFailureIsIsolated
      "DropWrite reports loss and settles Ask", dropWriteSettlesRequest
      "DropOldest settles evicted Ask", evictionSettlesRequest BoundedChannelFullMode.DropOldest
      "DropNewest settles evicted Ask", evictionSettlesRequest BoundedChannelFullMode.DropNewest
      "Bounded drop accounting remains bounded", dropAccounting
      "Handler failure faults current Ask and Continue survives", requestFailureCanContinue
      "Abort discards queued work and settles requests", abortDiscardsQueuedWork
      "Cooperative abort has consistent terminal reports", cooperativeAbortHasConsistentNotifications
      "Complete drains and terminal state is stable", gracefulCompleteDrainsAndIsStable
      "Throwing cancellation callback cannot strand writer", throwingCancellationCallbackCannotStrandWriter
      "Fault discards queue and settles pending requests", faultDiscardsAndSettlesQueuedRequests
      "Unanswered request settles on Complete", unansweredRequestSettlesOnComplete
      "Stateful error Stop faults and discards", statefulErrorStopDiscards
      "Mutable error Stop faults and discards", mutableErrorStopDiscards
      "Successful Stop transitions preserve graceful drain", successfulStopTransitionsDrain
      "State query failures do not kill agents", queryFailuresAreIsolated
      "State policy precedence and replacement recovery", statefulPolicyPrecedenceAndRecovery
      "Base error policy fallback in both wrappers", basePolicyFallbackForBothWrappers
      "Stateful atomic command/reply and failure Continue", statefulAtomicCommandReplies
      "Mutable atomic command/reply and failure Continue", mutableAtomicCommandReplies
      "Idle synchronous-continuation Abort cancels lifetime", synchronousIdleAbortCancelsLifetime
      "Concurrent Complete and Abort preserve one terminal outcome", concurrentTerminalRequestsRemainConsistent ]

// Keep the original suite sequential and preserve its per-scenario hang guard.
let tests =
    scenarios
    |> List.map (fun (name, run) ->
        testCaseAsync name (async {
            do! (run ()).WaitAsync(TimeSpan.FromSeconds 15.0) |> Async.AwaitTask
        }))
    |> testList "Dreamsleeve.Agent"
    |> testSequenced
