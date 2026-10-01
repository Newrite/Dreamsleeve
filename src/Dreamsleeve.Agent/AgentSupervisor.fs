namespace Dreamsleeve.Agent

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

/// When a supervised child is started again after it stopped without being asked.
type RestartPolicy = {
    /// Delay before the restart that follows the first failure in Window; each
    /// further failure in Window doubles it, up to MaxDelay.
    InitialDelay: TimeSpan
    MaxDelay: TimeSpan
    /// Failures allowed within Window; one more and the supervisor gives up. 0 never restarts.
    MaxRestarts: int
    Window: TimeSpan
}

[<RequireQualifiedAccess>]
module RestartPolicy =
    /// Doubling stops here; the delay is MaxDelay long before that anyway.
    [<Literal>]
    let private MaxDoublings = 30

    let validate policy =
        if policy.InitialDelay < TimeSpan.Zero then invalidArg (nameof policy) "InitialDelay must not be negative."
        if policy.MaxDelay < policy.InitialDelay then invalidArg (nameof policy) "MaxDelay must not be less than InitialDelay."
        if policy.MaxRestarts < 0 then invalidArg (nameof policy) "MaxRestarts must not be negative."
        if policy.Window <= TimeSpan.Zero then invalidArg (nameof policy) "Window must be positive."

    /// The delay after the n-th failure within the window, n >= 1.
    let delay policy failures =
        let doublings = min MaxDoublings (max 0 (failures - 1))
        let ticks = float policy.InitialDelay.Ticks * Math.Pow(2.0, float doublings)
        if ticks >= float policy.MaxDelay.Ticks then policy.MaxDelay else TimeSpan.FromTicks(int64 ticks)

/// A started child: the value consumers use, its full stop (including the
/// release of everything it owns) and its graceful stop. Qualified, so that
/// its common field names never steer record inference elsewhere.
[<RequireQualifiedAccess>]
type SupervisedChild<'Child> = {
    Value: 'Child
    Completion: Task
    Stop: unit -> Task
}

[<RequireQualifiedAccess>]
type SupervisorEvent<'Child> =
    /// restarts is 0 for the first start.
    | Started of child: 'Child * restarts: int
    | StartFailed of error: exn
    /// The child stopped without being asked; Ok means it completed on its own.
    | Stopped of outcome: Result<unit, exn>
    | Restarting of delay: TimeSpan * failures: int
    /// More than MaxRestarts failures within Window: supervision ends and Completion faults.
    | GaveUp of failures: int

/// Raised by AgentSupervisor.Completion when restarts ran out; the inner exception is the last failure.
type SupervisorGaveUpException(name: string, failures: int, lastError: exn) =
    inherit Exception($"Supervisor {name} gave up after {failures} failures.", lastError)
    member _.Failures = failures

[<RequireQualifiedAccess>]
type internal SupervisorMessage<'Child> =
    | Launch
    | Launched of generation: int * Result<SupervisedChild<'Child>, exn>
    | ChildStopped of generation: int * Result<unit, exn>
    | RestartDue of generation: int
    | Stop of ReplyChannel<unit>

/// One supervisor per child, itself an agent: the child's start, its stop and the
/// restart delays reach it as messages. Every stop it did not ask for is a failure,
/// even a graceful one: the child is meant to run until StopAsync.
[<Sealed>]
type AgentSupervisor<'Child> internal (agent: Agent<SupervisorMessage<'Child>>, current: unit -> 'Child option, completion: Task) =
    /// The running child; none while it starts, restarts or after supervision ended.
    member _.Current = current ()

    /// Stops the child gracefully and ends supervision; nothing is restarted afterwards.
    member _.StopAsync() : Task = task {
        let! result = agent.TryAskAsync SupervisorMessage.Stop
        match result with
        | AgentAskResult.Replied () | AgentAskResult.Closed | AgentAskResult.Canceled -> ()
        | AgentAskResult.Full | AgentAskResult.Dropped | AgentAskResult.TimedOut -> invalidOp "The supervisor did not accept Stop."
        | AgentAskResult.Faulted error -> raise error
        try do! completion with :? SupervisorGaveUpException -> ()
    }

    /// Completes after StopAsync; faults with SupervisorGaveUpException when restarts ran out.
    member _.Completion = completion

[<RequireQualifiedAccess>]
module AgentSupervisor =
    type private State<'Child> = {
        mutable Generation: int
        mutable Launching: bool
        mutable Child: SupervisedChild<'Child> voption
        mutable Restarts: int
        /// Stop callers; present once supervision is ending.
        mutable Stopping: ReplyChannel<unit> list voption
        mutable Delay: CancellationTokenSource voption
        /// Monotonic timestamps of the failures within the window.
        Failures: Queue<int64>
    }

    /// Supervises the children start makes, one at a time, restarting a failed one
    /// by policy. observe receives every event on the supervisor's handler and must
    /// be quick (logging); its exceptions are ignored.
    let startWithTimeProvider (time: TimeProvider) name policy (start: CancellationToken -> Task<SupervisedChild<'Child>>)
                              (observe: SupervisorEvent<'Child> -> unit) =
        RestartPolicy.validate policy
        let finished = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        let current = ref (None: 'Child option)
        let state = {
            Generation = 0; Launching = false; Child = ValueNone; Restarts = 0
            Stopping = ValueNone; Delay = ValueNone; Failures = Queue()
        }
        let report event = try observe event with _ -> ()

        let serve (child: SupervisedChild<'Child> voption) =
            state.Child <- child
            Volatile.Write(&current.contents, child |> ValueOption.map _.Value |> ValueOption.toOption)

        let cancelDelay () =
            state.Delay |> ValueOption.iter (fun cancel -> cancel.Cancel(); cancel.Dispose())
            state.Delay <- ValueNone

        let finish (context: AgentContext<SupervisorMessage<'Child>>) =
            state.Stopping |> ValueOption.iter (List.iter (fun reply -> reply.Reply()))
            finished.TrySetResult() |> ignore
            context.Complete() |> ignore

        let stopChild (child: SupervisedChild<'Child>) = task {
            try do! child.Stop() with _ -> ()
            try do! child.Completion with _ -> ()
        }

        let launch (context: AgentContext<SupervisorMessage<'Child>>) =
            state.Generation <- state.Generation + 1
            state.Launching <- true
            let generation = state.Generation
            context.PipeToSelf(start, fun result -> SupervisorMessage.Launched(generation, result))

        let failed (context: AgentContext<SupervisorMessage<'Child>>) (error: exn) =
            let now = time.GetTimestamp()
            let window = int64 (policy.Window.TotalSeconds * float time.TimestampFrequency)
            while state.Failures.Count > 0 && now - state.Failures.Peek() >= window do
                state.Failures.Dequeue() |> ignore
            state.Failures.Enqueue now
            let failures = state.Failures.Count
            if failures > policy.MaxRestarts then
                report (SupervisorEvent.GaveUp failures)
                finished.TrySetException(SupervisorGaveUpException(name, failures, error)) |> ignore
                context.Complete() |> ignore
            else
                let delay = RestartPolicy.delay policy failures
                report (SupervisorEvent.Restarting(delay, failures))
                let generation = state.Generation
                let cancel = new CancellationTokenSource()
                state.Delay <- ValueSome cancel
                let wait (token: CancellationToken) = task {
                    use linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancel.Token)
                    do! Task.Delay(delay, time, linked.Token)
                }
                context.PipeToSelf(wait, fun _ -> SupervisorMessage.RestartDue generation)

        let handle (context: AgentContext<SupervisorMessage<'Child>>) message = task {
            match message with
            | SupervisorMessage.Launch -> launch context
            | SupervisorMessage.Launched(generation, _) when generation <> state.Generation -> ()
            | SupervisorMessage.Launched(generation, result) ->
                state.Launching <- false
                match result, state.Stopping with
                | Ok child, ValueSome _ ->
                    // Stop came while it was starting: it never serves.
                    do! stopChild child
                    finish context
                | Error _, ValueSome _ -> finish context
                | Error error, ValueNone ->
                    report (SupervisorEvent.StartFailed error)
                    failed context error
                | Ok child, ValueNone ->
                    serve (ValueSome child)
                    report (SupervisorEvent.Started(child.Value, state.Restarts))
                    context.PipeToSelf((fun _ -> task { do! child.Completion }), fun outcome ->
                        SupervisorMessage.ChildStopped(generation, outcome))
            | SupervisorMessage.ChildStopped(generation, _) when generation <> state.Generation || state.Stopping.IsSome -> ()
            | SupervisorMessage.ChildStopped(_, outcome) ->
                serve ValueNone
                report (SupervisorEvent.Stopped outcome)
                let error =
                    match outcome with
                    | Ok () -> InvalidOperationException("The child completed without being asked to stop.") :> exn
                    | Error error -> error
                failed context error
            | SupervisorMessage.RestartDue generation when generation <> state.Generation || state.Stopping.IsSome -> ()
            | SupervisorMessage.RestartDue _ ->
                cancelDelay ()
                state.Restarts <- state.Restarts + 1
                launch context
            | SupervisorMessage.Stop reply ->
                match state.Stopping with
                | ValueSome waiting -> state.Stopping <- ValueSome (reply :: waiting)
                | ValueNone ->
                    state.Stopping <- ValueSome [ reply ]
                    cancelDelay ()
                    match state.Child with
                    | ValueSome child ->
                        serve ValueNone
                        do! stopChild child
                        finish context
                    // The start in flight is stopped when it arrives.
                    | ValueNone when state.Launching -> ()
                    | ValueNone -> finish context
        }

        let agent = Agent.Start(AgentOptions.create name, handle)
        agent.TryPost SupervisorMessage.Launch |> ignore
        AgentSupervisor(agent, (fun () -> Volatile.Read(&current.contents)), finished.Task)

    let start name policy start observe = startWithTimeProvider TimeProvider.System name policy start observe
