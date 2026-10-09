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
    member _.Stops id = match stops.TryGetValue id with | true, count -> count | false, _ -> 0
    member _.Fail(id, error: exn) = finished[id].TrySetException error |> ignore
    member _.End id = finished[id].TrySetResult() |> ignore

    member _.Next() =
        let id = Interlocked.Increment &started
        let completion = gate<unit>()
        finished[id] <- completion
        { SupervisedChild.Value = id; SupervisedChild.Completion = completion.Task
          SupervisedChild.Stop = fun () ->
              stops.AddOrUpdate(id, 1, fun _ count -> count + 1) |> ignore
              completion.TrySetResult() |> ignore
              Task.CompletedTask }

let private immediate = { InitialDelay = TimeSpan.Zero; MaxDelay = TimeSpan.Zero; MaxRestarts = 3; Window = TimeSpan.FromMinutes 1.0 }

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

let tests = testList "Supervisor" [
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
            with :? SupervisorGaveUpException<string> as error -> return Some error
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
            if Interlocked.Increment &attempts = 1 then Task.FromException<Result<SupervisedChild<int>, string>>(InvalidOperationException "port in use")
            else Task.FromResult(Ok(children.Next()))
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
            with :? SupervisorGaveUpException<string> as error -> return Some error
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
            with :? SupervisorGaveUpException<string> as error -> return Some error
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
            else return Ok(children.Next())
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
        let policy = { immediate with MaxRestarts = 1; Window = TimeSpan.FromSeconds 10.0 }
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
        let supervisor, events = supervise { immediate with InitialDelay = TimeSpan.FromHours 1.0; MaxDelay = TimeSpan.FromHours 1.0 } (fromChildren children)
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
        let policy = { InitialDelay = TimeSpan.FromSeconds 1.0; MaxDelay = TimeSpan.FromSeconds 5.0; MaxRestarts = 10; Window = TimeSpan.FromMinutes 1.0 }
        let delays = [ 1 .. 5 ] |> List.map (RestartPolicy.delay policy >> _.TotalSeconds)
        Expect.equal delays [ 1.0; 2.0; 4.0; 5.0; 5.0 ] "doubling, then the cap"
        Expect.equal (RestartPolicy.delay policy 1000) policy.MaxDelay "no overflow far past the cap"
        for invalid in [ { policy with InitialDelay = TimeSpan.FromSeconds -1.0 }; { policy with MaxDelay = TimeSpan.Zero }
                         { policy with MaxRestarts = -1 }; { policy with Window = TimeSpan.Zero } ] do
            Expect.isError (RestartPolicy.tryValidate invalid) $"refused: %A{invalid}"
]
