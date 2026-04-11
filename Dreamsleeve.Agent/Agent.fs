namespace Dreamsleeve.Agent

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

/// Describes how an agent mailbox is created.
[<RequireQualifiedAccess>]
type AgentMailbox =
    /// Creates an unbounded mailbox.
    | Unbounded of allowSynchronousContinuations: bool
    /// Creates a bounded mailbox.
    | Bounded of capacity: int * fullMode: BoundedChannelFullMode * allowSynchronousContinuations: bool

/// Helper constructors for mailboxes.
[<RequireQualifiedAccess>]
module AgentMailbox =
    /// Creates an unbounded mailbox.
    let unbounded = AgentMailbox.Unbounded false

    /// Creates an unbounded mailbox and allows synchronous continuations.
    let unboundedAllowSync = AgentMailbox.Unbounded true

    /// Creates a bounded mailbox that waits when it is full.
    let boundedWait capacity =
        AgentMailbox.Bounded(capacity, BoundedChannelFullMode.Wait, false)

    /// Creates a bounded mailbox with an explicit full-mode policy.
    let bounded capacity fullMode =
        AgentMailbox.Bounded(capacity, fullMode, false)

/// Result of posting a message to an agent.
[<RequireQualifiedAccess>]
type AgentPostResult =
    /// The message was accepted.
    | Posted
    /// The mailbox was full and the message could not be accepted immediately.
    | Full
    /// The agent is no longer accepting messages.
    | Closed
    /// The operation was canceled by the caller.
    | Canceled

/// Result of sending a request and waiting for a reply.
[<RequireQualifiedAccess>]
type AgentAskResult<'T> =
    /// The agent replied successfully.
    | Replied of 'T
    /// The request failed with an exception.
    | Faulted of exn
    /// The mailbox was full and the request could not be accepted.
    | Full
    /// The agent is no longer accepting requests.
    | Closed
    /// Waiting for the reply timed out.
    | TimedOut
    /// The operation was canceled by the caller or by the reply channel.
    | Canceled

/// Decides what to do when a message handler throws.
[<RequireQualifiedAccess>]
type AgentErrorAction =
    /// Keep the agent alive and continue processing the next messages.
    | Continue
    /// Stop the agent.
    | Stop

/// Describes why an agent stopped.
[<RequireQualifiedAccess>]
type AgentStopReason =
    /// The agent completed gracefully after the mailbox was closed and drained.
    | Completed
    /// The agent was aborted immediately.
    | Aborted
    /// The agent stopped because a handler failed.
    | Faulted of exn

/// A one-shot reply channel used by request/reply messages.
[<Sealed>]
type ReplyChannel<'T> internal (completion: TaskCompletionSource<'T>) =
    /// Attempts to reply with a value.
    member _.TryReply(value: 'T) = completion.TrySetResult(value)

    /// Replies with a value and ignores late or duplicate replies.
    member _.Reply(value: 'T) = completion.TrySetResult(value) |> ignore

    /// Attempts to fault the reply.
    member _.TryReplyError(error: exn) = completion.TrySetException(error)

    /// Faults the reply and ignores late or duplicate replies.
    member _.ReplyError(error: exn) = completion.TrySetException(error) |> ignore

    /// Attempts to cancel the reply.
    member _.TryCancel() = completion.TrySetCanceled()

    /// Cancels the reply and ignores late or duplicate replies.
    member _.Cancel() = completion.TrySetCanceled() |> ignore

/// Configuration for a base agent.
[<CLIMutable>]
type AgentOptions =
    {
        /// Human-readable agent name used in logs and diagnostics.
        Name: string
        /// Mailbox configuration.
        Mailbox: AgentMailbox
        /// Set to true only when you can guarantee exactly one writer.
        SingleWriter: bool
        /// Default timeout used by AskAsync and TryAskAsync when a timeout is not provided explicitly.
        DefaultAskTimeout: TimeSpan option
        /// Optional callback invoked when the agent starts.
        OnStarted: (string -> unit) option
        /// Optional callback invoked exactly once when the agent stops.
        OnStopped: (string * AgentStopReason -> unit) option
        /// Optional callback that decides whether the agent should continue after a handler exception.
        OnError: (string * exn -> AgentErrorAction) option
    }

/// Helpers for building AgentOptions.
[<RequireQualifiedAccess>]
module AgentOptions =
    /// Creates a default set of options.
    let create name =
        {
            Name = name
            Mailbox = AgentMailbox.unbounded
            SingleWriter = false
            DefaultAskTimeout = None
            OnStarted = None
            OnStopped = None
            OnError = None
        }

[<AutoOpen>]
module private AgentInternals =
    let taskFromException<'T> (ex: exn) = Task.FromException<'T>(ex)

    let tryGetBaseException (task: Task) =
        if isNull task.Exception then
            InvalidOperationException("The task faulted without an exception payload.") :> exn
        else
            task.Exception.GetBaseException()

    let inline safeInvoke (f: unit -> unit) =
        try f() with _ -> ()

    let waitIndefinitelyTask (token: CancellationToken) =
        Task.Delay(Timeout.InfiniteTimeSpan, token)

/// Context passed to each message handler of a base agent.
[<Sealed>]
type AgentContext<'Message>
    internal
        (
            name: string,
            cancellationToken: CancellationToken,
            tryPostImpl: 'Message -> AgentPostResult,
            postAsyncImpl: 'Message -> CancellationToken -> Task<AgentPostResult>,
            completeImpl: unit -> bool,
            abortImpl: unit -> unit
        ) =

    /// The configured agent name.
    member _.Name = name

    /// A token that is canceled when the agent is aborted.
    member _.CancellationToken = cancellationToken

    /// Attempts to post a message immediately.
    member _.TryPost(message: 'Message) = tryPostImpl message

    /// Posts a message asynchronously.
    member _.PostAsync(message: 'Message, ?cancellationToken: CancellationToken) =
        let token = defaultArg cancellationToken CancellationToken.None
        postAsyncImpl message token

    /// Stops accepting new messages and lets the mailbox drain.
    member _.Complete() = completeImpl ()

    /// Aborts the agent immediately.
    member _.Abort() = abortImpl ()

/// A task-first agent implemented on top of Channel.
[<Sealed>]
type Agent<'Message> private (options: AgentOptions, handler: AgentContext<'Message> -> 'Message -> Task<unit>) =
    let lifetime = new CancellationTokenSource()
    let completion = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let startedEvent = Event<string>()
    let stoppedEvent = Event<string * AgentStopReason>()
    let errorEvent = Event<string * exn>()
    let syncRoot = obj()

    let mutable queueLength = 0
    let mutable accepting = 1
    let mutable aborted = 0
    let mutable finalized = 0
    let mutable stopReason: AgentStopReason option = None

    let mailboxWaitsWhenFull =
        match options.Mailbox with
        | AgentMailbox.Unbounded _ -> false
        | AgentMailbox.Bounded(_, fullMode, _) -> fullMode = BoundedChannelFullMode.Wait

    let channel : Channel<'Message> =
        match options.Mailbox with
        | AgentMailbox.Unbounded allowSynchronousContinuations ->
            let channelOptions =
                UnboundedChannelOptions(
                    SingleReader = true,
                    SingleWriter = options.SingleWriter,
                    AllowSynchronousContinuations = allowSynchronousContinuations)

            Channel.CreateUnbounded<'Message>(channelOptions)

        | AgentMailbox.Bounded(capacity, fullMode, allowSynchronousContinuations) ->
            if capacity <= 0 then
                invalidArg (nameof capacity) "Bounded mailbox capacity must be greater than zero."

            let channelOptions =
                BoundedChannelOptions(capacity,
                    SingleReader = true,
                    SingleWriter = options.SingleWriter,
                    FullMode = fullMode,
                    AllowSynchronousContinuations = allowSynchronousContinuations)

            Channel.CreateBounded<'Message>(channelOptions)

    let setStopReasonOnce reason =
        lock syncRoot (fun () ->
            match stopReason with
            | Some _ -> ()
            | None -> stopReason <- Some reason)

    let getStopReason () =
        lock syncRoot (fun () -> stopReason)

    let finalize reason =
        if Interlocked.Exchange(&finalized, 1) = 0 then
            setStopReasonOnce reason

            match reason with
            | AgentStopReason.Completed -> completion.TrySetResult() |> ignore
            | AgentStopReason.Aborted -> completion.TrySetCanceled() |> ignore
            | AgentStopReason.Faulted error -> completion.TrySetException(error) |> ignore

            safeInvoke (fun () -> stoppedEvent.Trigger(options.Name, reason))
            options.OnStopped |> Option.iter (fun callback -> safeInvoke (fun () -> callback (options.Name, reason)))

    let isAcceptingMessages () =
        Volatile.Read(&accepting) = 1 && Volatile.Read(&aborted) = 0

    let completeCore () =
        if Interlocked.Exchange(&accepting, 0) = 1 then
            channel.Writer.TryComplete()
        else
            false

    let abortCore () =
        if Interlocked.Exchange(&aborted, 1) = 0 then
            Interlocked.Exchange(&accepting, 0) |> ignore
            lifetime.Cancel()
            channel.Writer.TryComplete() |> ignore

    let classifyFailedImmediateWrite () =
        if isAcceptingMessages() then AgentPostResult.Full else AgentPostResult.Closed

    let tryPostCore (message: 'Message) =
        if not (isAcceptingMessages()) then
            AgentPostResult.Closed
        elif channel.Writer.TryWrite(message) then
            Interlocked.Increment(&queueLength) |> ignore
            AgentPostResult.Posted
        else
            classifyFailedImmediateWrite ()

    let postAsyncCore (message: 'Message) (cancellationToken: CancellationToken) =
        task {
            if cancellationToken.IsCancellationRequested then
                return AgentPostResult.Canceled
            elif not (isAcceptingMessages()) then
                return AgentPostResult.Closed
            else
                try
                    let mutable finished = false
                    let mutable result = AgentPostResult.Closed

                    while not finished do
                        let! canWrite = channel.Writer.WaitToWriteAsync(cancellationToken).AsTask()

                        if not canWrite then
                            finished <- true
                            result <- AgentPostResult.Closed
                        elif channel.Writer.TryWrite(message) then
                            Interlocked.Increment(&queueLength) |> ignore
                            finished <- true
                            result <- AgentPostResult.Posted
                        elif not (isAcceptingMessages()) then
                            finished <- true
                            result <- AgentPostResult.Closed
                        elif mailboxWaitsWhenFull then
                            ()
                        else
                            finished <- true
                            result <- AgentPostResult.Full

                    return result
                with :? OperationCanceledException ->
                    return AgentPostResult.Canceled
        }

    let context =
        AgentContext<'Message>(
            options.Name,
            lifetime.Token,
            tryPostCore,
            postAsyncCore,
            completeCore,
            abortCore)

    let signalStarted () =
        safeInvoke (fun () -> startedEvent.Trigger(options.Name))
        options.OnStarted |> Option.iter (fun callback -> safeInvoke (fun () -> callback options.Name))

    let reportHandlerError error =
        safeInvoke (fun () -> errorEvent.Trigger(options.Name, error))
        match options.OnError with
        | Some decide ->
            try decide (options.Name, error)
            with _ -> AgentErrorAction.Stop
        | None -> AgentErrorAction.Stop

    let runLoop () =
        task {
            signalStarted ()

            try
                let mutable keepRunning = true

                while keepRunning do
                    let! canRead = channel.Reader.WaitToReadAsync(lifetime.Token).AsTask()

                    if not canRead then
                        keepRunning <- false
                    else
                        let mutable draining = true

                        while draining do
                            match channel.Reader.TryRead() with
                            | true, message ->
                                Interlocked.Decrement(&queueLength) |> ignore

                                try
                                    do! handler context message
                                with error ->
                                    match reportHandlerError error with
                                    | AgentErrorAction.Continue -> ()
                                    | AgentErrorAction.Stop ->
                                        setStopReasonOnce (AgentStopReason.Faulted error)
                                        completeCore() |> ignore
                                        draining <- false
                                        keepRunning <- false
                            | false, _ ->
                                draining <- false

                if Volatile.Read(&aborted) = 1 then
                    finalize AgentStopReason.Aborted
                else
                    finalize (defaultArg (getStopReason ()) AgentStopReason.Completed)
            with
            | :? OperationCanceledException ->
                if Volatile.Read(&aborted) = 1 then
                    finalize AgentStopReason.Aborted
                else
                    finalize (defaultArg (getStopReason ()) AgentStopReason.Completed)
            | error ->
                finalize (AgentStopReason.Faulted error)
        }

    do Task.Run(fun () -> runLoop() :> Task) |> ignore

    let waitForReply (replyTask: Task<'Reply>) (timeout: TimeSpan option) (cancellationToken: CancellationToken) =
        task {
            let replyAsTask = replyTask :> Task
            let completionTask = completion.Task :> Task

            let timeoutCts =
                match timeout, cancellationToken.CanBeCanceled with
                | None, false -> None
                | Some _, false -> Some(new CancellationTokenSource())
                | None, true -> Some(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                | Some _, true -> Some(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))

            timeoutCts |> Option.iter (fun cts -> timeout |> Option.iter cts.CancelAfter)

            try
                let! winner =
                    match timeoutCts with
                    | None -> Task.WhenAny(replyAsTask, completionTask)
                    | Some cts -> Task.WhenAny(replyAsTask, completionTask, waitIndefinitelyTask cts.Token)

                if Object.ReferenceEquals(winner, replyAsTask) then
                    match replyTask.Status with
                    | TaskStatus.RanToCompletion ->
                        return AgentAskResult.Replied replyTask.Result
                    | TaskStatus.Faulted ->
                        return AgentAskResult.Faulted (tryGetBaseException replyTask)
                    | TaskStatus.Canceled ->
                        return AgentAskResult.Canceled
                    | _ ->
                        return AgentAskResult.Closed
                elif Object.ReferenceEquals(winner, completionTask) then
                    match getStopReason () with
                    | Some (AgentStopReason.Faulted error) -> return AgentAskResult.Faulted error
                    | Some AgentStopReason.Aborted -> return AgentAskResult.Canceled
                    | Some AgentStopReason.Completed
                    | None -> return AgentAskResult.Closed
                else
                    if cancellationToken.IsCancellationRequested then
                        return AgentAskResult.Canceled
                    else
                        return AgentAskResult.TimedOut
            finally
                timeoutCts |> Option.iter (fun cts -> cts.Dispose())
        }

    /// The configured agent name.
    member _.Name = options.Name

    /// The task that completes when the agent stops.
    member _.Completion : Task = completion.Task :> Task

    /// The number of messages accepted into the mailbox and not yet taken by the reader.
    member _.QueueLength = max 0 (Volatile.Read(&queueLength))

    /// Returns true while the agent still accepts new messages.
    member _.IsAcceptingMessages = isAcceptingMessages ()

    /// Returns the stop reason after the agent has stopped.
    member _.StopReason = getStopReason ()

    /// Raised exactly once when the agent starts.
    member _.Started = startedEvent.Publish

    /// Raised when a message handler throws.
    member _.Errored = errorEvent.Publish

    /// Raised exactly once when the agent stops.
    member _.Stopped = stoppedEvent.Publish

    /// Attempts to post a message immediately.
    member _.TryPost(message: 'Message) = tryPostCore message

    /// Posts a message asynchronously.
    member _.PostAsync(message: 'Message, ?cancellationToken: CancellationToken) =
        let token = defaultArg cancellationToken CancellationToken.None
        postAsyncCore message token

    /// Sends a request and returns a result union instead of throwing.
    member _.TryAskAsync<'Reply>(buildMessage: ReplyChannel<'Reply> -> 'Message, ?timeout: TimeSpan, ?cancellationToken: CancellationToken) =
        task {
            let effectiveTimeout =
                match timeout with
                | Some value -> Some value
                | None -> options.DefaultAskTimeout
            let token = defaultArg cancellationToken CancellationToken.None
            let replyCompletion = TaskCompletionSource<'Reply>(TaskCreationOptions.RunContinuationsAsynchronously)
            let reply = ReplyChannel<'Reply>(replyCompletion)

            let messageResult =
                try Ok (buildMessage reply)
                with error -> Error error

            match messageResult with
            | Error error ->
                return AgentAskResult.Faulted error
            | Ok message ->
                let! postResult = postAsyncCore message token

                match postResult with
                | AgentPostResult.Posted ->
                    let! askResult = waitForReply replyCompletion.Task effectiveTimeout token

                    match askResult with
                    | AgentAskResult.Replied _
                    | AgentAskResult.Faulted _ ->
                        return askResult
                    | AgentAskResult.Full
                    | AgentAskResult.Closed
                    | AgentAskResult.TimedOut
                    | AgentAskResult.Canceled ->
                        reply.TryCancel() |> ignore
                        return askResult
                | AgentPostResult.Full -> return AgentAskResult.Full
                | AgentPostResult.Closed -> return AgentAskResult.Closed
                | AgentPostResult.Canceled -> return AgentAskResult.Canceled
        }

    /// Sends a request and throws for non-successful outcomes.
    member this.AskAsync<'Reply>(buildMessage: ReplyChannel<'Reply> -> 'Message, ?timeout: TimeSpan, ?cancellationToken: CancellationToken) =
        task {
            let! result = this.TryAskAsync(buildMessage, ?timeout = timeout, ?cancellationToken = cancellationToken)

            match result with
            | AgentAskResult.Replied value -> return value
            | AgentAskResult.Faulted error -> return! taskFromException<'Reply> error
            | AgentAskResult.Full -> return! taskFromException<'Reply> (InvalidOperationException($"Agent '{options.Name}' mailbox is full."))
            | AgentAskResult.Closed -> return! taskFromException<'Reply> (InvalidOperationException($"Agent '{options.Name}' is not accepting new messages."))
            | AgentAskResult.TimedOut -> return! taskFromException<'Reply> (TimeoutException($"Timed out waiting for a reply from agent '{options.Name}'."))
            | AgentAskResult.Canceled -> return! taskFromException<'Reply> (OperationCanceledException($"Ask to agent '{options.Name}' was canceled."))
        }

    /// Stops accepting new messages and lets the current mailbox drain.
    member _.Complete() = completeCore()

    /// Aborts the agent immediately.
    member _.Abort() = abortCore()

    interface IDisposable with
        member this.Dispose() = this.Abort()

    interface IAsyncDisposable with
        member this.DisposeAsync() =
            this.Complete() |> ignore
            ValueTask(this.Completion)

    /// Creates and starts an agent.
    static member Start(options: AgentOptions, handler: AgentContext<'Message> -> 'Message -> Task<unit>) =
        new Agent<'Message>(options, handler)

/// State transition returned by a stateful agent command handler.
[<RequireQualifiedAccess>]
type StatefulTransition<'State> =
    /// Keep the current state.
    | Stay
    /// Replace the current state.
    | SetState of 'State
    /// Stop the agent and keep the current state.
    | Stop
    /// Replace the current state and then stop the agent.
    | StopWithState of 'State

/// Decision taken after an unhandled command exception inside a stateful agent.
[<RequireQualifiedAccess>]
type StatefulErrorAction<'State> =
    /// Keep the current state and continue.
    | KeepStateAndContinue
    /// Replace the current state and continue.
    | ReplaceStateAndContinue of 'State
    /// Stop the agent.
    | Stop
    /// Replace the current state and then stop the agent.
    | StopWithState of 'State

/// Configuration for a StatefulAgent.
[<CLIMutable>]
type StatefulAgentOptions<'State> =
    {
        /// Base agent options.
        AgentOptions: AgentOptions
        /// Optional callback invoked after an unhandled command exception.
        OnUnhandled: ('State * exn -> StatefulErrorAction<'State>) option
        /// Optional callback invoked after every successful state replacement.
        OnTransition: ('State * 'State -> unit) option
    }

/// Helpers for building StatefulAgentOptions.
[<RequireQualifiedAccess>]
module StatefulAgentOptions =
    /// Creates a default set of options.
    let create name =
        {
            AgentOptions = AgentOptions.create name
            OnUnhandled = None
            OnTransition = None
        }

type private IStateQuery<'State> =
    abstract member Execute : 'State -> unit

type private StateQuery<'State, 'Reply>(projection: 'State -> 'Reply, reply: ReplyChannel<'Reply>) =
    interface IStateQuery<'State> with
        member _.Execute(state: 'State) =
            try
                projection state |> reply.Reply
            with error ->
                reply.ReplyError error

type private StatefulEnvelope<'State, 'Command> =
    | Command of 'Command
    | Query of IStateQuery<'State>

/// Context passed to each command handler of a stateful agent.
[<Sealed>]
type StatefulAgentContext
    internal
        (
            name: string,
            cancellationToken: CancellationToken,
            completeImpl: unit -> bool,
            abortImpl: unit -> unit
        ) =

    /// The configured agent name.
    member _.Name = name

    /// A token that is canceled when the underlying agent is aborted.
    member _.CancellationToken = cancellationToken

    /// Stops accepting new commands and lets the mailbox drain.
    member _.Complete() = completeImpl ()

    /// Aborts the underlying agent immediately.
    member _.Abort() = abortImpl ()

/// A stateful agent that fully encapsulates its state.
[<Sealed>]
type StatefulAgent<'State, 'Command>
    private
        (
            options: StatefulAgentOptions<'State>,
            initialState: 'State,
            commandHandler: StatefulAgentContext -> 'State -> 'Command -> Task<StatefulTransition<'State>>
        ) =

    let mutable state = initialState

    let applyState nextState =
        let previous = state
        state <- nextState
        options.OnTransition |> Option.iter (fun callback -> safeInvoke (fun () -> callback (previous, nextState)))

    let inner =
        Agent.Start(
            { options.AgentOptions with OnError = None },
            fun agentContext envelope ->
                task {
                    match envelope with
                    | Query query ->
                        query.Execute state
                    | Command command ->
                        let ctx = StatefulAgentContext(agentContext.Name, agentContext.CancellationToken, agentContext.Complete, agentContext.Abort)

                        try
                            let! transition = commandHandler ctx state command

                            match transition with
                            | StatefulTransition.Stay -> ()
                            | StatefulTransition.SetState nextState -> applyState nextState
                            | StatefulTransition.Stop -> agentContext.Complete() |> ignore
                            | StatefulTransition.StopWithState nextState ->
                                applyState nextState
                                agentContext.Complete() |> ignore
                        with error ->
                            let decision =
                                match options.OnUnhandled with
                                | Some decide ->
                                    try decide (state, error)
                                    with _ -> StatefulErrorAction.Stop
                                | None -> StatefulErrorAction.Stop

                            match decision with
                            | StatefulErrorAction.KeepStateAndContinue -> ()
                            | StatefulErrorAction.ReplaceStateAndContinue nextState -> applyState nextState
                            | StatefulErrorAction.Stop -> agentContext.Complete() |> ignore
                            | StatefulErrorAction.StopWithState nextState ->
                                applyState nextState
                                agentContext.Complete() |> ignore
                })

    /// The configured stateful agent name.
    member _.Name = inner.Name

    /// The task that completes when the stateful agent stops.
    member _.Completion = inner.Completion

    /// The number of queued envelopes.
    member _.QueueLength = inner.QueueLength

    /// Returns true while the stateful agent still accepts new commands.
    member _.IsAcceptingMessages = inner.IsAcceptingMessages

    /// Returns the stop reason after the stateful agent has stopped.
    member _.StopReason = inner.StopReason

    /// Raised exactly once when the stateful agent starts.
    member _.Started = inner.Started

    /// Raised when the underlying runtime reports an unhandled failure.
    member _.Errored = inner.Errored

    /// Raised exactly once when the stateful agent stops.
    member _.Stopped = inner.Stopped

    /// Attempts to post a command immediately.
    member _.TryPost(command: 'Command) = inner.TryPost(Command command)

    /// Posts a command asynchronously.
    member _.PostAsync(command: 'Command, ?cancellationToken: CancellationToken) =
        inner.PostAsync(Command command, ?cancellationToken = cancellationToken)

    /// Reads a projection of the current state and returns a result union.
    member _.TryReadAsync<'Reply>(projection: 'State -> 'Reply, ?timeout: TimeSpan, ?cancellationToken: CancellationToken) =
        inner.TryAskAsync(
            (fun reply -> Query (StateQuery<'State, 'Reply>(projection, reply) :> IStateQuery<'State>)),
            ?timeout = timeout,
            ?cancellationToken = cancellationToken)

    /// Reads a projection of the current state and throws for non-successful outcomes.
    member _.ReadAsync<'Reply>(projection: 'State -> 'Reply, ?timeout: TimeSpan, ?cancellationToken: CancellationToken) =
        inner.AskAsync(
            (fun reply -> Query (StateQuery<'State, 'Reply>(projection, reply) :> IStateQuery<'State>)),
            ?timeout = timeout,
            ?cancellationToken = cancellationToken)

    /// Stops accepting new commands and lets the mailbox drain.
    member _.Complete() = inner.Complete()

    /// Aborts the stateful agent immediately.
    member _.Abort() = inner.Abort()

    interface IDisposable with
        member this.Dispose() = this.Abort()

    interface IAsyncDisposable with
        member this.DisposeAsync() =
            this.Complete() |> ignore
            ValueTask(this.Completion)

    /// Creates and starts a stateful agent.
    static member Start(options: StatefulAgentOptions<'State>, initialState: 'State, commandHandler: StatefulAgentContext -> 'State -> 'Command -> Task<StatefulTransition<'State>>) =
        new StatefulAgent<'State, 'Command>(options, initialState, commandHandler)

/// Helper functions for base agents.
[<RequireQualifiedAccess>]
module Agent =
    /// Creates and starts an agent.
    let start options handler =
        Agent.Start(options, handler)

    /// Attempts to post a message immediately.
    let tryPost message (agent: Agent<_>) =
        agent.TryPost(message)

    /// Posts a message asynchronously.
    let postAsync message (agent: Agent<_>) =
        agent.PostAsync(message)

    /// Sends a request and waits for a reply.
    let askAsync buildMessage (agent: Agent<_>) =
        agent.AskAsync(buildMessage)

    /// Sends a request and returns a result union.
    let tryAskAsync buildMessage (agent: Agent<_>) =
        agent.TryAskAsync(buildMessage)

/// Helper functions for stateful agents.
[<RequireQualifiedAccess>]
module StatefulAgent =
    /// Creates and starts a stateful agent.
    let start options initialState commandHandler =
        StatefulAgent.Start(options, initialState, commandHandler)

    /// Attempts to post a command immediately.
    let tryPost command (agent: StatefulAgent<_, _>) =
        agent.TryPost(command)

    /// Posts a command asynchronously.
    let postAsync command (agent: StatefulAgent<_, _>) =
        agent.PostAsync(command)

    /// Reads a projection of the current state.
    let readAsync projection (agent: StatefulAgent<_, _>) =
        agent.ReadAsync(projection)

    /// Reads a projection of the current state and returns a result union.
    let tryReadAsync projection (agent: StatefulAgent<_, _>) =
        agent.TryReadAsync(projection)
