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

    let tryValidate (policy: RestartPolicy) =
        if isNull (box policy) then
            Error (AgentStartError.NullArgument "policy")
        elif policy.InitialDelay < TimeSpan.Zero then
            Error (AgentStartError.InvalidRestartPolicy "InitialDelay")
        elif policy.MaxDelay < policy.InitialDelay || policy.MaxDelay.Ticks / TimeSpan.TicksPerMillisecond > 4294967294L then
            Error (AgentStartError.InvalidRestartPolicy "MaxDelay")
        elif policy.MaxRestarts < 0 then
            Error (AgentStartError.InvalidRestartPolicy "MaxRestarts")
        elif policy.Window <= TimeSpan.Zero then
            Error (AgentStartError.InvalidRestartPolicy "Window")
        else
            Ok policy

    /// The delay after the n-th failure within the window, n >= 1.
    let delay policy failures =
        let doublings = min MaxDoublings (max 0 (failures - 1))
        let ticks = float policy.InitialDelay.Ticks * Math.Pow(2.0, float doublings)

        if ticks >= float policy.MaxDelay.Ticks then
            policy.MaxDelay
        else
            TimeSpan.FromTicks(int64 ticks)

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
type SupervisorFailure<'StartError> =
    | StartRejected of error: 'StartError
    | Faulted of error: exn
    | CompletedUnexpectedly

[<RequireQualifiedAccess>]
type SupervisorEvent<'Child, 'StartError> =
    /// restarts is 0 for the first start.
    | Started of child: 'Child * restarts: int
    | StartFailed of error: exn
    | StartRejected of error: 'StartError
    /// The child stopped without being asked; Ok means it completed on its own.
    | Stopped of outcome: Result<unit, exn>
    | Restarting of delay: TimeSpan * failures: int
    /// More than MaxRestarts failures within Window: supervision ends and Completion faults.
    | GaveUp of failures: int

/// Raised only when reconstruction attempts are exhausted; expected startup
/// rejections remain typed, and an actual last fault is the original inner exception.
type SupervisorGaveUpException<'StartError>(name: string, failures: int, failure: SupervisorFailure<'StartError>) =
    inherit Exception($"Supervisor {name} gave up after {failures} failures.",
                      match failure with
                      | SupervisorFailure.Faulted error -> error
                      | SupervisorFailure.StartRejected _ | SupervisorFailure.CompletedUnexpectedly -> null)
    member _.Failures = failures
    member _.Failure = failure

[<RequireQualifiedAccess>]
type internal SupervisorMessage<'Child, 'StartError> =
    | Launch
    /// Outer Error is an unexpected thrown fault; inner Error is an expected typed refusal.
    | Launched of generation: int * Result<Result<SupervisedChild<'Child>, 'StartError>, exn>
    | ChildStopped of generation: int * Result<unit, exn>
    | RestartDue of generation: int
    | Stop

/// One supervisor per child, itself an agent: the child's start, its stop and the
/// restart delays reach it as messages. Every stop it did not ask for is a failure,
/// even a graceful one: the child is meant to run until StopAsync.
[<Sealed>]
type AgentSupervisor<'Child, 'StartError> internal (agent: ReliableAgent<SupervisorMessage<'Child, 'StartError>>, current: unit -> 'Child option, isExhaustion: exn -> bool) =
    /// The running child; none while it starts, restarts or after supervision ended.
    member _.Current = current ()

    /// Stops the child gracefully and ends supervision; concurrent callers join the same cleanup.
    /// A sole prior exhaustion of this supervisor is consumed; other failures propagate.
    /// Await exposes one error; Completion retains every original lifetime cause.
    member _.StopAsync() : Task = task {
        // The shared terminal tasks join the same stop for concurrent callers.
        // Closed admission can mean an already terminated supervisor; still observe it.
        let! admission = agent.Ref.PostAsync SupervisorMessage.Stop
        match admission with
        | AgentDeliveryResult.Posted | AgentDeliveryResult.Closed | AgentDeliveryResult.Canceled -> ()
        try do! agent.Completion
        with :? SupervisorGaveUpException<'StartError> as error when
            agent.Completion.Exception.InnerExceptions.Count = 1 && isExhaustion error -> ()
    }

    /// Authoritative full lifetime result, including acquired child cleanup and observer faults.
    /// Inspect Exception.InnerExceptions for all causes after a faulted completion.
    member _.Completion = agent.Completion

[<RequireQualifiedAccess>]
module AgentSupervisor =
    /// One actual stop per acquired child, shared by explicit stop and lifecycle
    /// cleanup. Both Stop and full Completion are attempted, retaining every fault.
    type private OwnedChild<'Child>(child: SupervisedChild<'Child>, failureSink: exn -> unit) =
        let gate = obj()
        let mutable stopping: TaskCompletionSource<unit> option = None
        member _.Child = child
        member _.Stop() : Task =
            let pending, selected =
                lock gate (fun () ->
                    match stopping with
                    | Some pending -> pending, false
                    | None ->
                        let pending = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                        stopping <- Some pending
                        pending, true)
            if selected then
                let cleanup = task {
                    let failures = ResizeArray<exn>()
                    let retain error =
                        if not (failures |> Seq.exists (fun previous -> Object.ReferenceEquals(previous, error))) then
                            failures.Add error
                            failureSink error

                    let join operation = task {
                        let mutable work: Task = null
                        try
                            work <- operation ()
                            do! work
                        with error ->
                            if not (isNull work) && work.IsFaulted then
                                for failure in work.Exception.InnerExceptions do
                                    retain failure
                            else
                                retain error
                    }
                    do! join child.Stop
                    do! join (fun () -> child.Completion)

                    if failures.Count = 0 then
                        pending.TrySetResult() |> ignore
                    else
                        pending.TrySetException(failures) |> ignore
                }
                cleanup |> ignore
            pending.Task :> Task

    type private State<'Child> = {
        mutable Generation: int
        mutable Launching: bool
        mutable Child: OwnedChild<'Child> voption
        mutable Restarts: int
        mutable Stopping: bool
        mutable Delay: CancellationTokenSource voption
        Failures: Queue<int64>
    }

    /// Observer callbacks belong to the supervisor lifecycle. Failed callbacks
    /// stop supervision and release/join the acquired child, without restarting it.
    let private startCheckedWithTimeProvider (time: TimeProvider) name policy (start: CancellationToken -> Task<Result<SupervisedChild<'Child>, 'StartError>>)
                              (observe: SupervisorEvent<'Child, 'StartError> -> unit) =
        let current = ref (None: 'Child option)
        let exhausted = ref (None: exn option)
        let state = {
            Generation = 0
            Launching = false
            Child = ValueNone
            Restarts = 0

            Stopping = false
            Delay = ValueNone
            Failures = Queue()
        }

        let serve (child: OwnedChild<'Child> voption) =
            state.Child <- child
            Volatile.Write(&current.contents, child |> ValueOption.map (fun owned -> owned.Child.Value) |> ValueOption.toOption)

        let cancelDelay (context: ReliableAgentContext<SupervisorMessage<'Child, 'StartError>>) =
            let pending = state.Delay
            state.Delay <- ValueNone
            let mutable succeeded = true
            pending |> ValueOption.iter (fun cancel ->
                try cancel.Cancel()
                with error ->
                    succeeded <- false
                    context.Fail error

                try cancel.Dispose()
                with error ->
                    succeeded <- false
                    context.Fail error)
            succeeded

        let report (context: ReliableAgentContext<SupervisorMessage<'Child, 'StartError>>) original event =
            try
                observe event
                true
            with error ->
                original |> Option.iter context.Fail
                context.Fail error
                state.Stopping <- true
                serve ValueNone
                cancelDelay context |> ignore
                false

        let finish (context: ReliableAgentContext<SupervisorMessage<'Child, 'StartError>>) =
            serve ValueNone
            context.Complete() |> ignore

        let own (context: ReliableAgentContext<SupervisorMessage<'Child, 'StartError>>) child =
            let owned = OwnedChild(child, context.Fail)
            // Install ownership before reporting Started. DispatchStopped also
            // runs on an observer fault, so cleanup cannot depend on another message.
            let cleanup _ = task {
                let detached = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                use registration = context.DispatchStopped.Register(fun () -> detached.TrySetResult() |> ignore)
                let! completed = Task.WhenAny(child.Completion, detached.Task)
                if not (Object.ReferenceEquals(completed, child.Completion)) then
                    do! owned.Stop()
                // Already terminated children are joined. Their known outcome
                // goes to ChildStopped and the existing reconstruction policy.
            }
            context.StartDelivery cleanup
            owned

        let launch (context: ReliableAgentContext<SupervisorMessage<'Child, 'StartError>>) =
            state.Generation <- state.Generation + 1
            state.Launching <- true
            let generation = state.Generation
            let construct token = task {
                let! result = task {
                    try
                        let! result = start token
                        return Ok result
                    with error -> return Error error
                }
                let! admitted = context.PostAsync(SupervisorMessage.Launched(generation, result), cancellationToken = token)
                match admitted, result with
                | AgentDeliveryResult.Posted, _ -> ()
                | (AgentDeliveryResult.Closed | AgentDeliveryResult.Canceled), Ok(Ok child) ->
                    // A successful late factory still transferred ownership.
                    // Admission closure cannot drop its resource cleanup.
                    do! (OwnedChild(child, context.Fail)).Stop()
                | (AgentDeliveryResult.Closed | AgentDeliveryResult.Canceled), Error error ->
                    match error with
                    | :? OperationCanceledException when token.IsCancellationRequested -> ()
                    | error -> context.Fail error
                | (AgentDeliveryResult.Closed | AgentDeliveryResult.Canceled), Ok(Error _) -> ()
            }
            context.StartDelivery construct

        let sourceFault = function
            | SupervisorFailure.Faulted error -> Some error
            | SupervisorFailure.StartRejected _ | SupervisorFailure.CompletedUnexpectedly -> None

        let failed (context: ReliableAgentContext<SupervisorMessage<'Child, 'StartError>>) (failure: SupervisorFailure<'StartError>) =
            let now = time.GetTimestamp()
            let window = int64 (policy.Window.TotalSeconds * float time.TimestampFrequency)
            while state.Failures.Count > 0 && now - state.Failures.Peek() >= window do
                state.Failures.Dequeue() |> ignore

            state.Failures.Enqueue now
            let failures = state.Failures.Count

            if failures > policy.MaxRestarts then
                let error = SupervisorGaveUpException<'StartError>(name, failures, failure)
                Volatile.Write(&exhausted.contents, Some(error :> exn))
                context.Fail error
                report context None (SupervisorEvent.GaveUp failures) |> ignore
            else
                let delay = RestartPolicy.delay policy failures
                if report context (sourceFault failure) (SupervisorEvent.Restarting(delay, failures)) then
                    let generation = state.Generation
                    let cancel = new CancellationTokenSource()
                    state.Delay <- ValueSome cancel
                    let wait (token: CancellationToken) = task {
                        use linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancel.Token)
                        do! Task.Delay(delay, time, linked.Token)
                    }
                    context.PipeToSelf(wait, fun _ -> SupervisorMessage.RestartDue generation)

        let handle (context: ReliableAgentContext<SupervisorMessage<'Child, 'StartError>>) message = task {
            match message with
            | SupervisorMessage.Launch -> launch context
            | SupervisorMessage.Launched(generation, result) when generation <> state.Generation ->
                match result with
                | Ok(Ok child) -> do! (OwnedChild(child, context.Fail)).Stop()
                | Error _ | Ok(Error _) -> ()
            | SupervisorMessage.Launched(generation, result) ->
                state.Launching <- false
                match result, state.Stopping with
                | Ok(Ok child), true ->
                    do! (OwnedChild(child, context.Fail)).Stop()
                    finish context
                | Error _, true | Ok(Error _), true -> finish context
                | Error error, false ->
                    if report context (Some error) (SupervisorEvent.StartFailed error) then
                        failed context (SupervisorFailure.Faulted error)
                | Ok(Error error), false ->
                    if report context None (SupervisorEvent.StartRejected error) then
                        failed context (SupervisorFailure.StartRejected error)
                | Ok(Ok child), false ->
                    let owned = own context child
                    serve (ValueSome owned)

                    if report context None (SupervisorEvent.Started(child.Value, state.Restarts)) then
                        context.Watch(child.Completion, fun outcome -> SupervisorMessage.ChildStopped(generation, outcome))
                    else
                        // Completion may have raced the observer fault and won the
                        // ownership wait. Still retain its fault during terminal cleanup.
                        do! owned.Stop()
            | SupervisorMessage.ChildStopped(generation, _) when generation <> state.Generation || state.Stopping -> ()
            | SupervisorMessage.ChildStopped(_, outcome) ->
                serve ValueNone
                let failure =
                    match outcome with
                    | Ok () -> SupervisorFailure.CompletedUnexpectedly
                    | Error error -> SupervisorFailure.Faulted error
                if report context (sourceFault failure) (SupervisorEvent.Stopped outcome) then
                    failed context failure
            | SupervisorMessage.RestartDue generation when generation <> state.Generation || state.Stopping -> ()
            | SupervisorMessage.RestartDue _ ->
                if cancelDelay context then
                    state.Restarts <- state.Restarts + 1
                    launch context
            | SupervisorMessage.Stop ->
                if not state.Stopping then
                    state.Stopping <- true
                    cancelDelay context |> ignore
                    match state.Child with
                    | ValueSome child ->
                        serve ValueNone
                        do! child.Stop()
                        finish context
                    | ValueNone when state.Launching -> ()
                    | ValueNone -> finish context
        }

        Agent.TryPrepareReliable(AgentOptions.create name, handle)
        |> Result.map (fun plan ->
            let agent = plan.Start()
            agent.TryPost SupervisorMessage.Launch |> ignore
            AgentSupervisor(agent, (fun () -> Volatile.Read(&current.contents)), fun error ->
                Volatile.Read(&exhausted.contents) |> Option.exists (fun original -> Object.ReferenceEquals(original, error))))

    let tryStartWithTimeProvider (time: TimeProvider) name policy start observe =
        if isNull time then
            Error (AgentStartError.NullArgument "time")
        elif isNull (box start) then
            Error (AgentStartError.NullArgument "start")
        elif isNull (box observe) then
            Error (AgentStartError.NullArgument "observe")
        else
            RestartPolicy.tryValidate policy
            |> Result.bind (fun policy -> startCheckedWithTimeProvider time name policy start observe)

    let tryStart name policy start observe = tryStartWithTimeProvider TimeProvider.System name policy start observe
