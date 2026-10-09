module Dreamsleeve.Server.Tests.SupervisorTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Dreamsleeve.Agent
open Expecto
open AgentTests

/// Children numbered from 1; each completes when stopped, or when the test fails or ends it.
type private Children() =
    let finished = ConcurrentDictionary<int, TaskCompletionSource<unit>>()
    let stops = ConcurrentDictionary<int, int>()
    let mutable started = 0

    member _.Started = Volatile.Read &started
    member _.Stops id =
        match stops.TryGetValue id with
        | true, count -> count
        | false, _ -> 0
    member _.Fail(id, error: exn) = finished[id].TrySetException error |> ignore
    member _.End id = finished[id].TrySetResult() |> ignore

    member _.Next() =
        let id = Interlocked.Increment &started
        let completion = gate<unit>()
        finished[id] <- completion
        {
            SupervisedChild.Value = id
            SupervisedChild.Completion = completion.Task
            SupervisedChild.Stop = fun () ->
                stops.AddOrUpdate(id, 1, fun _ count -> count + 1) |> ignore
                completion.TrySetResult() |> ignore
                Task.CompletedTask
        }

let private immediate = {
    InitialDelay = TimeSpan.Zero
    MaxDelay = TimeSpan.Zero
    MaxRestarts = 3
    Window = TimeSpan.FromMinutes 1.0
}

let private supervise policy (start: CancellationToken -> Task<Result<SupervisedChild<int>, string>>) =
    let events = ConcurrentQueue<SupervisorEvent<int, string>>()
    TestSupervisor.start "supervisor-test" policy start events.Enqueue, events

let private fromChildren (children: Children) = fun (_: CancellationToken) -> Task.FromResult(Ok(children.Next()))

let private serving (supervisor: AgentSupervisor<int, string>) id = eventually (fun () -> supervisor.Current = Some id)

/// Timestamps move only when the test says; timers are real (the tests use zero delays).
type private Clock() =
    inherit TimeProvider()
    let mutable timestamp = 0L
    member _.Advance(milliseconds: int64) = Interlocked.Add(&timestamp, milliseconds) |> ignore
    override _.TimestampFrequency = 1000L
    override _.GetTimestamp() = Volatile.Read &timestamp
    override _.CreateTimer(callback, state, due, period) = TimeProvider.System.CreateTimer(callback, state, due, period)

// Force the ownership waiter to resume after Watch has delivered the child's
// failure and reconstruction has closed dispatch.
let private deferredOwnershipWaitExhaustion observerFails = task {
    let completion, waitInstalled = gate<unit>(), gate<unit>()
    let original = InvalidOperationException "handled child failure"
    let observerFault = InvalidOperationException "GaveUp observer failure"
    let mutable stops = 0
    let child = {
        SupervisedChild.Value = 1
        SupervisedChild.Completion = completion.Task
        SupervisedChild.Stop = fun () ->
            Interlocked.Increment(&stops) |> ignore
            Task.CompletedTask
    }
    let wait (_: Task) (detached: Task) = task {
        waitInstalled.TrySetResult() |> ignore
        do! detached
        return detached
    }
    let observe = function
        | SupervisorEvent.GaveUp _ when observerFails -> raise observerFault
        | SupervisorEvent.Started _ | SupervisorEvent.StartFailed _ | SupervisorEvent.StartRejected _
        | SupervisorEvent.Stopped _ | SupervisorEvent.Restarting _ | SupervisorEvent.GaveUp _ -> ()
    let supervisor: AgentSupervisor<int, string> =
        AgentSupervisor.tryStartWithOwnershipWait wait TimeProvider.System "deferred-ownership-wait"
            { immediate with MaxRestarts = 0 } (fun _ -> Task.FromResult(Ok child)) observe
        |> expectStarted
    do! serving supervisor 1
    do! awaitUnit waitInstalled.Task
    completion.SetException original
    let! failure = terminal supervisor.Completion
    match failure with
    | Some (:? SupervisorGaveUpException<string> as exhaustion) ->
        check (Object.ReferenceEquals(original, exhaustion.InnerException)) "Exhaustion retains the handled child fault."
    | other -> failtestf "Expected own exhaustion, got %A" other

    let errors = supervisor.Completion.Exception.InnerExceptions
    equal (if observerFails then 2 else 1) errors.Count
    check (errors |> Seq.forall (fun error -> not (Object.ReferenceEquals(original, error)))) "Handled failure is not registered again as cleanup."
    if observerFails then
        check (errors |> Seq.exists (fun error -> Object.ReferenceEquals(observerFault, error))) "Independent observer fault survives."
    equal 0 (Volatile.Read &stops)
    equal None supervisor.Current
    for _ in 1 .. 2 do
        let! stopFailure = terminal (supervisor.StopAsync())
        if observerFails then
            check (Option.isSome stopFailure) "Stop must retain the additional observer fault."
        else
            equal None stopFailure
}

let tests = testList "Supervisor" [
    testTask "handled exhaustion survives a delayed ownership wait without duplicate cleanup faults" {
        do! deferredOwnershipWaitExhaustion false
    }

    testTask "a delayed ownership wait preserves a distinct GaveUp observer fault" {
        do! deferredOwnershipWaitExhaustion true
    }
    testTask "restarts a child that fails or ends on its own and serves the new one" {
        let children = Children()
        let supervisor, events = supervise immediate (fromChildren children)
        do! serving supervisor 1
        let boom = InvalidOperationException "boom"
        children.Fail(1, boom)
        do! serving supervisor 2
        children.End 2
        do! serving supervisor 3
        do! supervisor.StopAsync() |> awaitUnit
        equal 1 (children.Stops 3)
        equal 0 (children.Stops 1)
        equal 0 (children.Stops 2)
        equal None supervisor.Current
        check supervisor.Completion.IsCompletedSuccessfully "graceful stop completes supervision"
        let recorded = List.ofSeq events
        match recorded with
        | [ SupervisorEvent.Started(1, 0); SupervisorEvent.Stopped(Error failure); SupervisorEvent.Restarting(_, 1)
            SupervisorEvent.Started(2, 1); SupervisorEvent.Stopped(Ok ()); SupervisorEvent.Restarting(_, 2)
            SupervisorEvent.Started(3, 2) ] -> check (Object.ReferenceEquals(failure, boom)) "the child's own fault"
        | other -> failtestf "Unexpected events: %A" other
    }

    testTask "gives up after more than MaxRestarts failures within the window" {
        let children = Children()
        let supervisor, events = supervise { immediate with MaxRestarts = 2 } (fromChildren children)
        for id in 1 .. 3 do
            do! serving supervisor id
            children.Fail(id, InvalidOperationException $"failure {id}")
        let! (failure: SupervisorGaveUpException<string> option) = task {
            try
                do! supervisor.Completion |> awaitUnit
                return None
            with :? SupervisorGaveUpException<string> as error ->
                return Some error
        }
        match failure with
        | Some error ->
            equal 3 error.Failures
            equal "failure 3" error.InnerException.Message
        | None -> failtest "Supervision must end in a fault."
        equal 3 children.Started
        equal None supervisor.Current
        check (events |> Seq.exists (function SupervisorEvent.GaveUp 3 -> true | _ -> false)) "GaveUp reported"
        do! supervisor.StopAsync() |> awaitUnit
    }

    testTask "a failed start counts as a failure and is retried" {
        let children = Children()
        let mutable attempts = 0
        let start _ =
            if Interlocked.Increment &attempts = 1 then
                Task.FromException<Result<SupervisedChild<int>, string>>(InvalidOperationException "port in use")
            else
                Task.FromResult(Ok(children.Next()))
        let supervisor, events = supervise immediate start
        do! serving supervisor 1
        match List.ofSeq events with
        | [ SupervisorEvent.StartFailed error; SupervisorEvent.Restarting(_, 1); SupervisorEvent.Started(1, 1) ] ->
            equal "port in use" error.Message
        | other -> failtestf "Unexpected events: %A" other
        do! supervisor.StopAsync() |> awaitUnit
    }

    testTask "expected startup refusals retain their typed reason through retry exhaustion" {
        let events = ConcurrentQueue<SupervisorEvent<int, string>>()
        let start (_: CancellationToken) = Task.FromResult(Error "capacity exhausted")
        let supervisor = TestSupervisor.start "typed-rejection" { immediate with MaxRestarts = 1 } start events.Enqueue
        let! (failure: SupervisorGaveUpException<string> option) = task {
            try
                do! awaitUnit supervisor.Completion
                return None
            with :? SupervisorGaveUpException<string> as error ->
                return Some error
        }
        match failure with
        | Some error ->
            equal 2 error.Failures
            equal (SupervisorFailure.StartRejected "capacity exhausted") error.Failure
            check (isNull error.InnerException) "A typed startup refusal must not fabricate an exception."
        | None -> failtest "Exhaustion must fault the supervisor lifecycle."
        match List.ofSeq events with
        | [SupervisorEvent.StartRejected "capacity exhausted"; SupervisorEvent.Restarting(_, 1)
           SupervisorEvent.StartRejected "capacity exhausted"; SupervisorEvent.GaveUp 2] -> ()
        | other -> failtestf "Refusal was not preserved: %A" other
    }

    testTask "unexpected thrown starts preserve their original fault through the same exhaustion policy" {
        let expected = InvalidOperationException("unexpected allocation fault")
        let events = ConcurrentQueue<SupervisorEvent<int, string>>()
        let start (_: CancellationToken) = Task.FromException<Result<SupervisedChild<int>, string>> expected
        let supervisor = TestSupervisor.start "throwing-start" { immediate with MaxRestarts = 1 } start events.Enqueue
        let! (failure: SupervisorGaveUpException<string> option) = task {
            try
                do! awaitUnit supervisor.Completion
                return None
            with :? SupervisorGaveUpException<string> as error ->
                return Some error
        }
        match failure with
        | Some error ->
            equal 2 error.Failures
            match error.Failure with
            | SupervisorFailure.Faulted original -> check (Object.ReferenceEquals(expected, original)) "Original unexpected fault is retained."
            | other -> failtestf "Thrown start was relabeled: %A" other
            check (Object.ReferenceEquals(expected, error.InnerException)) "Actual fault remains the inner exception."
        | None -> failtest "Exhaustion must fault."
        match List.ofSeq events with
        | [SupervisorEvent.StartFailed first; SupervisorEvent.Restarting(_, 1)
           SupervisorEvent.StartFailed last; SupervisorEvent.GaveUp 2] ->
            check (Object.ReferenceEquals(expected, first) && Object.ReferenceEquals(expected, last)) "Fault events retain original cause."
        | other -> failtestf "Unexpected events: %A" other
    }

    testTask "a rejected partial startup joins its owned cleanup before reconstruction" {
        let cleanupStarted, releaseCleanup = gate<unit>(), gate<unit>()
        let children = Children()
        let mutable attempts = 0
        let start (_: CancellationToken) = task {
            let attempt = Interlocked.Increment &attempts
            if attempt = 1 then
                // Factory owns the acquired resource until its asynchronous cleanup joins.
                cleanupStarted.SetResult()
                do! releaseCleanup.Task
                return Error "loaded data refused"
            else
                return Ok(children.Next())
        }
        let supervisor, events = supervise immediate start
        do! awaitResult cleanupStarted.Task
        equal 1 attempts
        check events.IsEmpty "Reconstruction began before factory cleanup completed."
        releaseCleanup.SetResult()
        do! serving supervisor 1
        equal 2 attempts
        do! awaitUnit (supervisor.StopAsync())
    }

    testTask "failures older than the window are forgotten" {
        let clock = Clock()
        let children = Children()
        let events = ConcurrentQueue<SupervisorEvent<int, string>>()
        let policy = {
            immediate with
                MaxRestarts = 1
                Window = TimeSpan.FromSeconds 10.0
        }
        let supervisor = TestSupervisor.startWithTimeProvider clock "supervisor-window" policy (fromChildren children) events.Enqueue
        do! serving supervisor 1
        children.Fail(1, InvalidOperationException "first")
        do! serving supervisor 2
        clock.Advance 10_000L
        children.Fail(2, InvalidOperationException "second, a window later")
        do! serving supervisor 3
        check (events |> Seq.forall (function SupervisorEvent.GaveUp _ -> false | _ -> true)) "no give-up across windows"
        do! supervisor.StopAsync() |> awaitUnit
    }

    testTask "Stop during the restart delay starts nothing more" {
        let children = Children()
        let supervisor, events =
            supervise {
                immediate with
                    InitialDelay = TimeSpan.FromHours 1.0
                    MaxDelay = TimeSpan.FromHours 1.0
            } (fromChildren children)
        do! serving supervisor 1
        children.Fail(1, InvalidOperationException "boom")
        do! eventually (fun () -> events |> Seq.exists (function SupervisorEvent.Restarting _ -> true | _ -> false))
        do! supervisor.StopAsync() |> awaitUnit
        equal 1 children.Started
        check supervisor.Completion.IsCompletedSuccessfully "stopped, not given up"
    }

    testTask "Stop while a start is in flight stops that child before it serves" {
        let children = Children()
        let release = gate<unit>()
        let start _ = task {
            do! release.Task
            return Ok(children.Next())
        }
        let supervisor, events = supervise immediate start
        let stopping = supervisor.StopAsync()
        release.SetResult()
        do! stopping |> awaitUnit
        equal 1 (children.Stops 1)
        equal None supervisor.Current
        check (events.IsEmpty) "the child never served"
    }

    testCase "restart delays double up to MaxDelay and bad policies are refused" <| fun _ ->
        let policy = {
            InitialDelay = TimeSpan.FromSeconds 1.0
            MaxDelay = TimeSpan.FromSeconds 5.0
            MaxRestarts = 10
            Window = TimeSpan.FromMinutes 1.0
        }
        let delays = [ 1 .. 5 ] |> List.map (RestartPolicy.delay policy >> _.TotalSeconds)
        Expect.equal delays [ 1.0; 2.0; 4.0; 5.0; 5.0 ] "doubling, then the cap"
        Expect.equal (RestartPolicy.delay policy 1000) policy.MaxDelay "no overflow far past the cap"
        for invalid in [ { policy with InitialDelay = TimeSpan.FromSeconds -1.0 }; { policy with MaxDelay = TimeSpan.Zero }
                         { policy with MaxRestarts = -1 }; { policy with Window = TimeSpan.Zero } ] do
            Expect.isError (RestartPolicy.tryValidate invalid) $"refused: %A{invalid}"

    testTask "concurrent Stop callers share one child stop and retain its cleanup failures" {
        let completion, stopping = gate<unit>(), gate<unit>()
        let primary = InvalidOperationException("child stop")
        let secondary = InvalidOperationException("child completion cleanup")
        let mutable stops = 0
        let child = {
            SupervisedChild.Value = 1
            SupervisedChild.Completion = completion.Task
            SupervisedChild.Stop = fun () ->
                Interlocked.Increment(&stops) |> ignore
                stopping.TrySetResult() |> ignore
                Task.FromException primary
        }
        let supervisor, _ = supervise immediate (fun _ -> Task.FromResult(Ok child))
        do! serving supervisor 1
        let first = supervisor.StopAsync()
        do! awaitUnit stopping.Task
        let second = supervisor.StopAsync()
        check (not first.IsCompleted && not second.IsCompleted && not supervisor.Completion.IsCompleted) "Every caller waits actual child cleanup."
        completion.SetException secondary
        for stop in [first; second] do
            let! failure = terminal stop
            check (failure |> Option.exists (fun actual -> Object.ReferenceEquals(primary, actual))) "Explicit Stop exposes original child stop fault."
        let errors = supervisor.Completion.Exception.Flatten().InnerExceptions
        for expected in [primary; secondary] do
            equal 1 (errors |> Seq.filter (fun actual -> Object.ReferenceEquals(expected, actual)) |> Seq.length)
        equal 1 stops
        equal None supervisor.Current
    }

    testTask "Started observer fault owns child cleanup before publishing terminal completion" {
        let completion, stopping = gate<unit>(), gate<unit>()
        let original = InvalidOperationException("Started observer")
        let stopFailure = InvalidOperationException("observer-triggered child stop")
        let cleanupFailure = InvalidOperationException("observer-triggered child cleanup")
        let mutable starts, stops = 0, 0
        let start _ =
            starts <- starts + 1
            Task.FromResult(Ok {
                SupervisedChild.Value = 1
                SupervisedChild.Completion = completion.Task
                SupervisedChild.Stop = fun () ->
                    Interlocked.Increment(&stops) |> ignore
                    stopping.TrySetResult() |> ignore
                    Task.FromException stopFailure
            })
        let observe = function
            | SupervisorEvent.Started _ -> raise original
            | SupervisorEvent.StartFailed _ | SupervisorEvent.StartRejected _ | SupervisorEvent.Stopped _
            | SupervisorEvent.Restarting _ | SupervisorEvent.GaveUp _ -> ()
        let supervisor: AgentSupervisor<int, string> = TestSupervisor.start "observer-ownership" immediate start observe
        do! awaitUnit stopping.Task
        check (not supervisor.Completion.IsCompleted) "Observer fault cannot bypass child cleanup."
        equal None supervisor.Current
        completion.SetException cleanupFailure
        let! failure = terminal supervisor.Completion
        check (failure |> Option.exists (fun actual -> Object.ReferenceEquals(original, actual))) "Observer is original lifecycle fault."
        let errors = supervisor.Completion.Exception.Flatten().InnerExceptions
        for expected in [original; stopFailure; cleanupFailure] do
            equal 1 (errors |> Seq.filter (fun actual -> Object.ReferenceEquals(expected, actual)) |> Seq.length)
        equal 1 starts
        equal 1 stops
        let! failure = terminal (supervisor.StopAsync())
        check (Option.isSome failure) "Callback/cleanup faults are not suppressed by Stop."
    }

    testTask "late successful factory joins one acquired child for all waiting Stop callers" {
        let constructing, created, stopping, releaseCleanup = gate<unit>(), gate<unit>(), gate<unit>(), gate<unit>()
        let completion = gate<unit>()
        let mutable stops = 0
        let start _ = task {
            constructing.TrySetResult() |> ignore
            do! created.Task
            return Ok {
                SupervisedChild.Value = 1
                SupervisedChild.Completion = completion.Task
                SupervisedChild.Stop = fun () -> task {
                    Interlocked.Increment(&stops) |> ignore
                    stopping.TrySetResult() |> ignore
                    do! releaseCleanup.Task
                    completion.TrySetResult() |> ignore
                }
            }
        }
        let supervisor, events = supervise immediate start
        do! awaitUnit constructing.Task
        let first, second = supervisor.StopAsync(), supervisor.StopAsync()
        created.SetResult()
        do! awaitUnit stopping.Task
        check (not first.IsCompleted && not second.IsCompleted && not supervisor.Completion.IsCompleted) "Late acquisition is still joined before any terminal success."
        equal None supervisor.Current
        releaseCleanup.SetResult()
        do! awaitUnit first
        do! awaitUnit second
        equal 1 stops
        check supervisor.Completion.IsCompletedSuccessfully "Actual late cleanup completed."
        check (events |> Seq.forall (function SupervisorEvent.Started _ -> false | _ -> true)) "Late child never serves."
    }

    testTask "Stop consumes sole own exhaustion but preserves an additional observer fault" {
        let original = InvalidOperationException("GaveUp observer")
        let observe = function
            | SupervisorEvent.GaveUp _ -> raise original
            | SupervisorEvent.Started _ | SupervisorEvent.StartFailed _ | SupervisorEvent.StartRejected _
            | SupervisorEvent.Stopped _ | SupervisorEvent.Restarting _ -> ()
        let start (_: CancellationToken) = Task.FromResult(Error "expected refusal")
        let supervisor: AgentSupervisor<int, string> = TestSupervisor.start "giveup-observer" { immediate with MaxRestarts = 0 } start observe
        let! failure = terminal supervisor.Completion
        check (failure |> Option.exists (fun (error: exn) -> error :? SupervisorGaveUpException<string>)) "Typed exhaustion remains the primary cause."
        let errors = supervisor.Completion.Exception.InnerExceptions
        equal 2 errors.Count
        check (errors |> Seq.exists (fun error -> Object.ReferenceEquals(original, error))) "Secondary observer fault is retained."
        let! failure = terminal (supervisor.StopAsync())
        check (Option.isSome failure) "Additional fault must prevent exhaustion-only Stop consumption."
    }

    testTask "a child's generic exhaustion exception is not the supervisor's own exhaustion" {
        let original = SupervisorGaveUpException<string>("nested child", 1, SupervisorFailure.StartRejected "child refusal")
        let ready = gate<unit>()
        let child = {
            SupervisedChild.Value = 1
            SupervisedChild.Completion = ready.Task
            SupervisedChild.Stop = fun () ->
                ready.TrySetResult() |> ignore
                Task.FromException original
        }
        let supervisor, _ = supervise immediate (fun _ -> Task.FromResult(Ok child))
        do! serving supervisor 1
        let! failure = terminal (supervisor.StopAsync())
        check (failure |> Option.exists (fun error -> Object.ReferenceEquals(original, error))) "Unrelated exhaustion cannot manufacture successful Stop."
    }

]
