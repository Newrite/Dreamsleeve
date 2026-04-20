namespace Dreamsleeve.Agent

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

/// <summary>
/// Describes how an agent mailbox (message queue) is created and bounded.
/// </summary>
[<RequireQualifiedAccess>]
type AgentMailbox =
    /// <summary>
    /// Creates an unbounded mailbox. It will never block on post, but may consume unbounded memory if messages are posted faster than processed.
    /// </summary>
    /// <param name="allowSynchronousContinuations">If true, allows synchronous execution of continuations on the thread that completes the operation.</param>
    | Unbounded of allowSynchronousContinuations: bool
    /// <summary>
    /// Creates a bounded mailbox with a strict capacity. It provides backpressure.
    /// </summary>
    /// <param name="capacity">The maximum number of items the mailbox can hold.</param>
    /// <param name="fullMode">The behavior when the mailbox is full (e.g., Wait, DropNewest, DropOldest, DropWrite).</param>
    /// <param name="allowSynchronousContinuations">If true, allows synchronous execution of continuations.</param>
    | Bounded of capacity: int * fullMode: BoundedChannelFullMode * allowSynchronousContinuations: bool

/// <summary>
/// Helper constructors for creating mailboxes.
/// </summary>
[<RequireQualifiedAccess>]
module AgentMailbox =
    /// <summary>
    /// Creates an unbounded mailbox with default settings (synchronous continuations disabled).
    /// </summary>
    let unbounded = AgentMailbox.Unbounded false

    /// <summary>
    /// Creates an unbounded mailbox and allows synchronous continuations for better performance in some scenarios.
    /// </summary>
    let unboundedAllowSync = AgentMailbox.Unbounded true

    /// <summary>
    /// Creates a bounded mailbox that asynchronously waits (blocks writers) when it is full.
    /// </summary>
    /// <param name="capacity">The maximum number of items the mailbox can hold.</param>
    let boundedWait capacity =
        AgentMailbox.Bounded(capacity, BoundedChannelFullMode.Wait, false)

    /// <summary>
    /// Creates a bounded mailbox with an explicit full-mode policy.
    /// </summary>
    /// <param name="capacity">The maximum number of items the mailbox can hold.</param>
    /// <param name="fullMode">The behavior when the mailbox is full (e.g., Wait, DropNewest, DropOldest, DropWrite).</param>
    let bounded capacity fullMode =
        AgentMailbox.Bounded(capacity, fullMode, false)

/// <summary>
/// The result of posting a message to an agent, encapsulating success or failure states without throwing exceptions.
/// </summary>
[<RequireQualifiedAccess>]
type AgentPostResult =
    /// <summary>
    /// The message was successfully accepted into the mailbox.
    /// </summary>
    | Posted
    /// <summary>
    /// The mailbox was full and the message could not be accepted immediately.
    /// </summary>
    | Full
    /// <summary>
    /// The agent is no longer accepting messages because it has been completed or aborted.
    /// </summary>
    | Closed
    /// <summary>
    /// The asynchronous post operation was canceled by the caller.
    /// </summary>
    | Canceled

/// <summary>
/// The result of sending a request (Ask) and waiting for a reply, encapsulating possible operational failures.
/// </summary>
[<RequireQualifiedAccess>]
type AgentAskResult<'T> =
    /// <summary>
    /// The agent replied successfully with the expected value.
    /// </summary>
    | Replied of 'T
    /// <summary>
    /// The request failed with an exception, typically because the handler explicitly replied with an error.
    /// </summary>
    | Faulted of exn
    /// <summary>
    /// The mailbox was full and the request could not be accepted into the queue.
    /// </summary>
    | Full
    /// <summary>
    /// The agent is closed and no longer accepting requests.
    /// </summary>
    | Closed
    /// <summary>
    /// Waiting for the reply timed out.
    /// </summary>
    | TimedOut
    /// <summary>
    /// The wait operation was canceled by the caller, or the internal reply channel was canceled.
    /// </summary>
    | Canceled

/// <summary>
/// Decides the action an agent should take when its message handler throws an unhandled exception.
/// </summary>
[<RequireQualifiedAccess>]
type AgentErrorAction =
    /// <summary>
    /// Keep the agent alive and continue processing the next messages in the queue.
    /// </summary>
    | Continue
    /// <summary>
    /// Stop the agent immediately. Unprocessed messages will be discarded.
    /// </summary>
    | Stop

/// <summary>
/// Describes the reason an agent stopped processing messages.
/// </summary>
[<RequireQualifiedAccess>]
type AgentStopReason =
    /// <summary>
    /// The agent completed gracefully after its mailbox was closed and fully drained.
    /// </summary>
    | Completed
    /// <summary>
    /// The agent was aborted immediately without draining its mailbox.
    /// </summary>
    | Aborted
    /// <summary>
    /// The agent stopped unexpectedly because a message handler threw an exception and the error policy dictated a stop.
    /// </summary>
    | Faulted of exn

/// <summary>
/// A one-shot reply channel used for request/reply (Ask) messaging patterns.
/// Wraps a TaskCompletionSource to provide thread-safe, single-use reply mechanisms.
/// </summary>
[<Sealed>]
type ReplyChannel<'T> internal (completion: TaskCompletionSource<'T>) =
    /// <summary>
    /// Attempts to reply with a value.
    /// </summary>
    /// <param name="value">The reply value.</param>
    /// <returns>True if the reply was set successfully, false if the channel was already replied to or canceled.</returns>
    member _.TryReply(value: 'T) = completion.TrySetResult(value)

    /// <summary>
    /// Replies with a value and ignores if the channel was already replied to or canceled.
    /// </summary>
    /// <param name="value">The reply value.</param>
    member _.Reply(value: 'T) = completion.TrySetResult(value) |> ignore

    /// <summary>
    /// Attempts to fault the reply with an exception.
    /// </summary>
    /// <param name="error">The exception to propagate back to the caller.</param>
    /// <returns>True if the exception was set successfully.</returns>
    member _.TryReplyError(error: exn) = completion.TrySetException(error)

    /// <summary>
    /// Faults the reply with an exception and ignores if the channel was already replied to.
    /// </summary>
    /// <param name="error">The exception to propagate back to the caller.</param>
    member _.ReplyError(error: exn) = completion.TrySetException(error) |> ignore

    /// <summary>
    /// Attempts to cancel the reply.
    /// </summary>
    /// <returns>True if canceled successfully.</returns>
    member _.TryCancel() = completion.TrySetCanceled()

    /// <summary>
    /// Cancels the reply and ignores if the channel was already replied to.
    /// </summary>
    member _.Cancel() = completion.TrySetCanceled() |> ignore

/// <summary>
/// Configuration options for initializing a standard Agent.
/// </summary>
[<CLIMutable>]
type AgentOptions =
    {
        /// <summary>
        /// Human-readable agent name used in logs, diagnostics, and debugging.
        /// </summary>
        Name: string
        /// <summary>
        /// Mailbox configuration (Bounded or Unbounded).
        /// </summary>
        Mailbox: AgentMailbox
        /// <summary>
        /// Set to true only when you can guarantee exactly one writer thread posting to the agent.
        /// This enables minor performance optimizations in the underlying channel. Defaults to false.
        /// </summary>
        SingleWriter: bool
        /// <summary>
        /// Default timeout used by AskAsync and TryAskAsync when a timeout is not explicitly provided.
        /// </summary>
        DefaultAskTimeout: TimeSpan option
        /// <summary>
        /// Optional callback invoked synchronously when the agent successfully starts processing.
        /// Receives the agent's name.
        /// </summary>
        OnStarted: (string -> unit) option
        /// <summary>
        /// Optional callback invoked exactly once when the agent stops processing.
        /// Receives the agent's name and the reason it stopped.
        /// </summary>
        OnStopped: (string * AgentStopReason -> unit) option
        /// <summary>
        /// Optional callback invoked when the message handler throws an unhandled exception.
        /// Receives the agent's name and the exception. Must return an AgentErrorAction to decide whether to continue or stop.
        /// </summary>
        OnError: (string * exn -> AgentErrorAction) option
    }

/// <summary>
/// Helpers for building AgentOptions.
/// </summary>
[<RequireQualifiedAccess>]
module AgentOptions =
    /// <summary>
    /// Creates a default set of options with an unbounded mailbox and no callbacks.
    /// </summary>
    /// <param name="name">The name of the agent.</param>
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

/// <summary>
/// Context passed to each message handler of a base agent, providing access to its lifecycle and self-messaging capabilities.
/// </summary>
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

    /// <summary>
    /// The configured agent name.
    /// </summary>
    member _.Name = name

    /// <summary>
    /// A cancellation token that is triggered when the agent is aborted.
    /// Useful for passing to long-running asynchronous operations within the handler.
    /// </summary>
    member _.CancellationToken = cancellationToken

    /// <summary>
    /// Attempts to post a message to this agent immediately. Does not block.
    /// </summary>
    /// <param name="message">The message to post.</param>
    member _.TryPost(message: 'Message) = tryPostImpl message

    /// <summary>
    /// Posts a message to this agent asynchronously. May wait if the mailbox is bounded and full.
    /// </summary>
    /// <param name="message">The message to post.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    member _.PostAsync(message: 'Message, ?cancellationToken: CancellationToken) =
        let token = defaultArg cancellationToken CancellationToken.None
        postAsyncImpl message token

    /// <summary>
    /// Signals the agent to stop accepting new messages and to shut down gracefully after processing the current queue.
    /// </summary>
    member _.Complete() = completeImpl ()

    /// <summary>
    /// Signals the agent to stop immediately, discarding unprocessed messages and canceling ongoing operations.
    /// </summary>
    member _.Abort() = abortImpl ()

/// <summary>
/// A task-first asynchronous agent implemented on top of System.Threading.Channels.
/// Processes messages sequentially without built-in state management.
/// </summary>
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

    /// <summary>
    /// The configured agent name.
    /// </summary>
    member _.Name = options.Name

    /// <summary>
    /// A task that completes when the agent has fully stopped processing.
    /// </summary>
    member _.Completion : Task = completion.Task :> Task

    /// <summary>
    /// The approximate number of messages currently accepted into the mailbox and waiting to be processed.
    /// </summary>
    member _.QueueLength = max 0 (Volatile.Read(&queueLength))

    /// <summary>
    /// True if the agent is still accepting new messages (i.e., Complete or Abort has not been called).
    /// </summary>
    member _.IsAcceptingMessages = isAcceptingMessages ()

    /// <summary>
    /// Gets the reason the agent stopped, or None if it is still running.
    /// </summary>
    member _.StopReason = getStopReason ()

    /// <summary>
    /// An event raised exactly once when the agent starts its processing loop.
    /// </summary>
    member _.Started = startedEvent.Publish

    /// <summary>
    /// An event raised when a message handler throws an unhandled exception.
    /// </summary>
    member _.Errored = errorEvent.Publish

    /// <summary>
    /// An event raised exactly once when the agent stops processing.
    /// </summary>
    member _.Stopped = stoppedEvent.Publish

    /// <summary>
    /// Attempts to enqueue a message immediately. Returns a result indicating success or failure. Does not block.
    /// </summary>
    /// <param name="message">The message to post.</param>
    member _.TryPost(message: 'Message) = tryPostCore message

    /// <summary>
    /// Asynchronously posts a message to the agent.
    /// If the mailbox is bounded and full, this operation will wait asynchronously until space is available or the token is canceled.
    /// </summary>
    /// <param name="message">The message to post.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    member _.PostAsync(message: 'Message, ?cancellationToken: CancellationToken) =
        let token = defaultArg cancellationToken CancellationToken.None
        postAsyncCore message token

    /// <summary>
    /// Sends a request message and waits for a reply, returning an AgentAskResult instead of throwing exceptions on failure.
    /// </summary>
    /// <param name="buildMessage">A function that constructs the request message, given a ReplyChannel.</param>
    /// <param name="timeout">An optional timeout for the reply. Defaults to the agent's DefaultAskTimeout.</param>
    /// <param name="cancellationToken">An optional cancellation token to cancel the wait.</param>
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

    /// <summary>
    /// Sends a request message and waits for a reply asynchronously.
    /// Throws an exception if the agent is full, closed, timed out, or if the handler explicitly faults the reply.
    /// </summary>
    /// <param name="buildMessage">A function that constructs the request message, given a ReplyChannel.</param>
    /// <param name="timeout">An optional timeout for the reply.</param>
    /// <param name="cancellationToken">An optional cancellation token to cancel the wait.</param>
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

    /// <summary>
    /// Signals the agent to stop accepting new messages and to process all currently queued messages before shutting down gracefully.
    /// </summary>
    member _.Complete() = completeCore()

    /// <summary>
    /// Signals the agent to stop immediately. The internal cancellation token is triggered, and pending messages are discarded.
    /// </summary>
    member _.Abort() = abortCore()

    interface IDisposable with
        member this.Dispose() = this.Abort()

    interface IAsyncDisposable with
        member this.DisposeAsync() =
            this.Complete() |> ignore
            ValueTask(this.Completion)

    /// <summary>
    /// Creates and starts a new Agent with the specified options and message handler.
    /// </summary>
    /// <param name="options">The configuration options for the agent.</param>
    /// <param name="handler">The asynchronous function that processes incoming messages.</param>
    static member Start(options: AgentOptions, handler: AgentContext<'Message> -> 'Message -> Task<unit>) =
        new Agent<'Message>(options, handler)

/// <summary>
/// Represents a state transition returned by a stateful agent's command handler.
/// </summary>
[<RequireQualifiedAccess>]
type StatefulTransition<'State> =
    /// <summary>
    /// Keep the current state unmodified.
    /// </summary>
    | Stay
    /// <summary>
    /// Replace the agent's current state with a new one.
    /// </summary>
    | SetState of 'State
    /// <summary>
    /// Stop the agent gracefully while keeping the current state.
    /// </summary>
    | Stop
    /// <summary>
    /// Replace the agent's current state with a new one, and then stop the agent gracefully.
    /// </summary>
    | StopWithState of 'State

/// <summary>
/// Defines the decision taken after an unhandled command exception occurs inside a stateful agent.
/// </summary>
[<RequireQualifiedAccess>]
type StatefulErrorAction<'State> =
    /// <summary>
    /// Ignore the error, keep the current state, and continue processing the next command.
    /// </summary>
    | KeepStateAndContinue
    /// <summary>
    /// Replace the current state with a fallback or error state, and continue processing.
    /// </summary>
    | ReplaceStateAndContinue of 'State
    /// <summary>
    /// Stop the agent immediately due to the error.
    /// </summary>
    | Stop
    /// <summary>
    /// Replace the current state (e.g., to record the failure) and then stop the agent.
    /// </summary>
    | StopWithState of 'State

/// <summary>
/// Configuration options for a StatefulAgent.
/// </summary>
[<CLIMutable>]
type StatefulAgentOptions<'State> =
    {
        /// <summary>
        /// The underlying base agent configuration options.
        /// </summary>
        AgentOptions: AgentOptions
        /// <summary>
        /// Optional callback invoked after an unhandled exception in the command handler.
        /// Receives the current state and the exception, and must return a StatefulErrorAction to determine the next step.
        /// </summary>
        OnUnhandled: ('State * exn -> StatefulErrorAction<'State>) option
        /// <summary>
        /// Optional callback invoked synchronously after every successful state replacement.
        /// Receives a tuple of (previousState, newState).
        /// </summary>
        OnTransition: ('State * 'State -> unit) option
    }

/// <summary>
/// Helpers for building StatefulAgentOptions.
/// </summary>
[<RequireQualifiedAccess>]
module StatefulAgentOptions =
    /// <summary>
    /// Creates a default set of stateful options with an unbounded mailbox.
    /// </summary>
    /// <param name="name">The name of the stateful agent.</param>
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

/// <summary>
/// Context passed to each command handler of a stateful agent, allowing lifecycle control and self-messaging.
/// </summary>
[<Sealed>]
type StatefulAgentContext
    internal
        (
            name: string,
            cancellationToken: CancellationToken,
            completeImpl: unit -> bool,
            abortImpl: unit -> unit
        ) =

    /// <summary>
    /// The configured stateful agent name.
    /// </summary>
    member _.Name = name

    /// <summary>
    /// A cancellation token that is triggered when the underlying agent is aborted.
    /// </summary>
    member _.CancellationToken = cancellationToken

    /// <summary>
    /// Signals the agent to stop accepting new commands and to shut down gracefully.
    /// </summary>
    member _.Complete() = completeImpl ()

    /// <summary>
    /// Signals the agent to stop immediately, discarding unprocessed commands.
    /// </summary>
    member _.Abort() = abortImpl ()

/// <summary>
/// A stateful agent that strictly encapsulates a mutable state and models a finite-state machine.
/// All state mutations and reads happen sequentially within the agent's processing loop, eliminating data races.
/// </summary>
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

    /// <summary>
    /// The configured stateful agent name.
    /// </summary>
    member _.Name = inner.Name

    /// <summary>
    /// A task that completes when the stateful agent has fully stopped.
    /// </summary>
    member _.Completion = inner.Completion

    /// <summary>
    /// The approximate number of queued envelopes (commands and queries).
    /// </summary>
    member _.QueueLength = inner.QueueLength

    /// <summary>
    /// True if the stateful agent is still accepting new commands and queries.
    /// </summary>
    member _.IsAcceptingMessages = inner.IsAcceptingMessages

    /// <summary>
    /// Gets the reason the agent stopped, or None if it is still running.
    /// </summary>
    member _.StopReason = inner.StopReason

    /// <summary>
    /// An event raised exactly once when the stateful agent starts.
    /// </summary>
    member _.Started = inner.Started

    /// <summary>
    /// An event raised when the underlying runtime reports an unhandled failure.
    /// </summary>
    member _.Errored = inner.Errored

    /// <summary>
    /// An event raised exactly once when the stateful agent stops.
    /// </summary>
    member _.Stopped = inner.Stopped

    /// <summary>
    /// Attempts to enqueue a command immediately. Returns a result indicating success or failure. Does not block.
    /// </summary>
    /// <param name="command">The command to post.</param>
    member _.TryPost(command: 'Command) = inner.TryPost(Command command)

    /// <summary>
    /// Asynchronously posts a command to the stateful agent.
    /// Will wait if the mailbox is bounded and currently full.
    /// </summary>
    /// <param name="command">The command to post.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    member _.PostAsync(command: 'Command, ?cancellationToken: CancellationToken) =
        inner.PostAsync(Command command, ?cancellationToken = cancellationToken)

    /// <summary>
    /// Enqueues a read query to safely project data from the current state.
    /// The projection runs sequentially inside the agent loop.
    /// Returns a result union encapsulating success or failure.
    /// </summary>
    /// <param name="projection">A pure function that extracts data from the agent's state.</param>
    /// <param name="timeout">An optional timeout for the query.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    member _.TryReadAsync<'Reply>(projection: 'State -> 'Reply, ?timeout: TimeSpan, ?cancellationToken: CancellationToken) =
        inner.TryAskAsync(
            (fun reply -> Query (StateQuery<'State, 'Reply>(projection, reply) :> IStateQuery<'State>)),
            ?timeout = timeout,
            ?cancellationToken = cancellationToken)

    /// <summary>
    /// Enqueues a read query to safely project data from the current state asynchronously.
    /// The projection runs sequentially inside the agent loop.
    /// Throws if the query fails, times out, or the agent is closed.
    /// </summary>
    /// <param name="projection">A pure function that extracts data from the agent's state.</param>
    /// <param name="timeout">An optional timeout for the query.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    member _.ReadAsync<'Reply>(projection: 'State -> 'Reply, ?timeout: TimeSpan, ?cancellationToken: CancellationToken) =
        inner.AskAsync(
            (fun reply -> Query (StateQuery<'State, 'Reply>(projection, reply) :> IStateQuery<'State>)),
            ?timeout = timeout,
            ?cancellationToken = cancellationToken)

    /// <summary>
    /// Signals the stateful agent to stop accepting new commands and let the current mailbox drain gracefully.
    /// </summary>
    member _.Complete() = inner.Complete()

    /// <summary>
    /// Signals the stateful agent to stop immediately.
    /// </summary>
    member _.Abort() = inner.Abort()

    interface IDisposable with
        member this.Dispose() = this.Abort()

    interface IAsyncDisposable with
        member this.DisposeAsync() =
            this.Complete() |> ignore
            ValueTask(this.Completion)

    /// <summary>
    /// Creates and starts a stateful agent.
    /// </summary>
    /// <param name="options">The configuration options for the agent.</param>
    /// <param name="initialState">The initial state of the agent.</param>
    /// <param name="commandHandler">The asynchronous function that processes commands and returns state transitions.</param>
    static member Start(options: StatefulAgentOptions<'State>, initialState: 'State, commandHandler: StatefulAgentContext -> 'State -> 'Command -> Task<StatefulTransition<'State>>) =
        new StatefulAgent<'State, 'Command>(options, initialState, commandHandler)

/// <summary>
/// Helper module providing functional pipelines for base Agent operations.
/// </summary>
[<RequireQualifiedAccess>]
module Agent =
    /// <summary>
    /// Creates and starts an agent.
    /// </summary>
    let start options handler =
        Agent.Start(options, handler)

    /// <summary>
    /// Attempts to post a message immediately.
    /// </summary>
    let tryPost message (agent: Agent<_>) =
        agent.TryPost(message)

    /// <summary>
    /// Posts a message asynchronously.
    /// </summary>
    let postAsync message (agent: Agent<_>) =
        agent.PostAsync(message)

    /// <summary>
    /// Sends a request and waits for a reply, throwing on failure.
    /// </summary>
    let askAsync buildMessage (agent: Agent<_>) =
        agent.AskAsync(buildMessage)

    /// <summary>
    /// Sends a request and returns a result union.
    /// </summary>
    let tryAskAsync buildMessage (agent: Agent<_>) =
        agent.TryAskAsync(buildMessage)

/// <summary>
/// Helper module providing functional pipelines for StatefulAgent operations.
/// </summary>
[<RequireQualifiedAccess>]
module StatefulAgent =
    /// <summary>
    /// Creates and starts a stateful agent.
    /// </summary>
    let start options initialState commandHandler =
        StatefulAgent.Start(options, initialState, commandHandler)

    /// <summary>
    /// Attempts to post a command immediately.
    /// </summary>
    let tryPost command (agent: StatefulAgent<_, _>) =
        agent.TryPost(command)

    /// <summary>
    /// Posts a command asynchronously.
    /// </summary>
    let postAsync command (agent: StatefulAgent<_, _>) =
        agent.PostAsync(command)

    /// <summary>
    /// Reads a projection of the current state, throwing on failure.
    /// </summary>
    let readAsync projection (agent: StatefulAgent<_, _>) =
        agent.ReadAsync(projection)

    /// <summary>
    /// Reads a projection of the current state and returns a result union.
    /// </summary>
    let tryReadAsync projection (agent: StatefulAgent<_, _>) =
        agent.TryReadAsync(projection)


[<AutoOpen>]
module private MutableStatefulAgentInternals =
    let inline privateSafeInvoke (f: unit -> unit) =
        try f() with _ -> ()

/// <summary>
/// Represents a transition returned by a mutable stateful agent's command handler.
/// The state object itself is expected to be mutated in place inside the handler.
/// </summary>
[<RequireQualifiedAccess>]
type MutableStatefulTransition =
    /// <summary>
    /// Keep running after the command handler finishes.
    /// </summary>
    | Stay
    /// <summary>
    /// Stop the agent gracefully after the current mailbox is drained.
    /// </summary>
    | Stop

/// <summary>
/// Defines the action taken after an unhandled command exception in a mutable stateful agent.
/// </summary>
[<RequireQualifiedAccess>]
type MutableStatefulErrorAction =
    /// <summary>
    /// Keep the current in-memory state instance and continue processing subsequent commands.
    /// </summary>
    | Continue
    /// <summary>
    /// Stop the agent gracefully after the error.
    /// </summary>
    | Stop

/// <summary>
/// Configuration options for a MutableStatefulAgent.
/// </summary>
[<CLIMutable>]
type MutableStatefulAgentOptions<'State> =
    {
        /// <summary>
        /// The underlying base agent configuration options.
        /// </summary>
        AgentOptions: AgentOptions

        /// <summary>
        /// Optional callback invoked after an unhandled exception in the command handler.
        /// Receives the current mutable state instance and the exception, and must decide whether to continue or stop.
        /// </summary>
        OnUnhandled: ('State * exn -> MutableStatefulErrorAction) option
    }

/// <summary>
/// Helpers for building MutableStatefulAgentOptions.
/// </summary>
[<RequireQualifiedAccess>]
module MutableStatefulAgentOptions =
    /// <summary>
    /// Creates a default set of options with an unbounded mailbox.
    /// </summary>
    /// <param name="name">The name of the mutable stateful agent.</param>
    let create name =
        {
            AgentOptions = AgentOptions.create name
            OnUnhandled = None
        }

type private IMutableStateQuery<'State> =
    abstract member Execute : 'State -> unit

type private MutableStateQuery<'State, 'Reply>(projection: 'State -> 'Reply, reply: ReplyChannel<'Reply>) =
    interface IMutableStateQuery<'State> with
        member _.Execute(state: 'State) =
            try
                projection state |> reply.Reply
            with error ->
                reply.ReplyError error

type private MutableStatefulEnvelope<'State, 'Command> =
    | Command of 'Command
    | Query of IMutableStateQuery<'State>

/// <summary>
/// A mutable stateful agent that fully encapsulates a mutable state object.
/// All command handling and state reads are serialized through the agent mailbox,
/// which makes in-place mutation safe as long as the state reference is not leaked outside.
/// </summary>
[<Sealed>]
type MutableStatefulAgent<'State, 'Command>
    private
        (
            options: MutableStatefulAgentOptions<'State>,
            initialState: 'State,
            commandHandler: StatefulAgentContext -> 'State -> 'Command -> Task<MutableStatefulTransition>
        ) =

    let state = initialState

    let inner =
        Agent.Start(
            { options.AgentOptions with OnError = None },
            fun agentContext envelope ->
                task {
                    match envelope with
                    | Query query ->
                        query.Execute state

                    | Command command ->
                        let ctx =
                            StatefulAgentContext(
                                agentContext.Name,
                                agentContext.CancellationToken,
                                agentContext.Complete,
                                agentContext.Abort)

                        try
                            let! transition = commandHandler ctx state command

                            match transition with
                            | MutableStatefulTransition.Stay -> ()
                            | MutableStatefulTransition.Stop ->
                                agentContext.Complete() |> ignore
                        with error ->
                            let action =
                                match options.OnUnhandled with
                                | Some decide ->
                                    try decide (state, error)
                                    with _ -> MutableStatefulErrorAction.Stop
                                | None ->
                                    MutableStatefulErrorAction.Stop

                            match action with
                            | MutableStatefulErrorAction.Continue -> ()
                            | MutableStatefulErrorAction.Stop ->
                                agentContext.Complete() |> ignore
                })

    /// <summary>
    /// The configured mutable stateful agent name.
    /// </summary>
    member _.Name = inner.Name

    /// <summary>
    /// A task that completes when the mutable stateful agent has fully stopped.
    /// </summary>
    member _.Completion = inner.Completion

    /// <summary>
    /// The approximate number of queued envelopes (commands and queries).
    /// </summary>
    member _.QueueLength = inner.QueueLength

    /// <summary>
    /// True if the mutable stateful agent is still accepting new commands and queries.
    /// </summary>
    member _.IsAcceptingMessages = inner.IsAcceptingMessages

    /// <summary>
    /// Gets the reason the agent stopped, or None if it is still running.
    /// </summary>
    member _.StopReason = inner.StopReason

    /// <summary>
    /// An event raised exactly once when the mutable stateful agent starts.
    /// </summary>
    member _.Started = inner.Started

    /// <summary>
    /// An event raised when the underlying runtime reports an unhandled failure.
    /// </summary>
    member _.Errored = inner.Errored

    /// <summary>
    /// An event raised exactly once when the mutable stateful agent stops.
    /// </summary>
    member _.Stopped = inner.Stopped

    /// <summary>
    /// Attempts to enqueue a command immediately. Returns a result indicating success or failure. Does not block.
    /// </summary>
    /// <param name="command">The command to post.</param>
    member _.TryPost(command: 'Command) =
        inner.TryPost(Command command)

    /// <summary>
    /// Asynchronously posts a command to the mutable stateful agent.
    /// Will wait if the mailbox is bounded and currently full.
    /// </summary>
    /// <param name="command">The command to post.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    member _.PostAsync(command: 'Command, ?cancellationToken: CancellationToken) =
        inner.PostAsync(Command command, ?cancellationToken = cancellationToken)

    /// <summary>
    /// Enqueues a read query to safely project data from the current mutable state.
    /// The projection runs sequentially inside the agent loop.
    /// Do not return live mutable references unless the caller is trusted not to mutate them concurrently.
    /// </summary>
    /// <param name="projection">A function that extracts data from the current state.</param>
    /// <param name="timeout">An optional timeout for the query.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    member _.TryReadAsync<'Reply>(projection: 'State -> 'Reply, ?timeout: TimeSpan, ?cancellationToken: CancellationToken) =
        inner.TryAskAsync(
            (fun reply -> Query (MutableStateQuery<'State, 'Reply>(projection, reply) :> IMutableStateQuery<'State>)),
            ?timeout = timeout,
            ?cancellationToken = cancellationToken)

    /// <summary>
    /// Enqueues a read query to safely project data from the current mutable state asynchronously.
    /// The projection runs sequentially inside the agent loop.
    /// Throws if the query fails, times out, or the agent is closed.
    /// </summary>
    /// <param name="projection">A function that extracts data from the current state.</param>
    /// <param name="timeout">An optional timeout for the query.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    member _.ReadAsync<'Reply>(projection: 'State -> 'Reply, ?timeout: TimeSpan, ?cancellationToken: CancellationToken) =
        inner.AskAsync(
            (fun reply -> Query (MutableStateQuery<'State, 'Reply>(projection, reply) :> IMutableStateQuery<'State>)),
            ?timeout = timeout,
            ?cancellationToken = cancellationToken)

    /// <summary>
    /// Signals the mutable stateful agent to stop accepting new commands and let the current mailbox drain gracefully.
    /// </summary>
    member _.Complete() = inner.Complete()

    /// <summary>
    /// Signals the mutable stateful agent to stop immediately.
    /// </summary>
    member _.Abort() = inner.Abort()

    interface IDisposable with
        member this.Dispose() = this.Abort()

    interface IAsyncDisposable with
        member this.DisposeAsync() =
            this.Complete() |> ignore
            ValueTask(this.Completion)

    /// <summary>
    /// Creates and starts a mutable stateful agent.
    /// </summary>
    /// <param name="options">The configuration options for the agent.</param>
    /// <param name="initialState">The initial mutable state instance. Ownership is transferred to the agent.</param>
    /// <param name="commandHandler">The asynchronous function that processes commands and may mutate the state in place.</param>
    static member Start
        (
            options: MutableStatefulAgentOptions<'State>,
            initialState: 'State,
            commandHandler: StatefulAgentContext -> 'State -> 'Command -> Task<MutableStatefulTransition>
        ) =
        new MutableStatefulAgent<'State, 'Command>(options, initialState, commandHandler)

/// <summary>
/// Helper module providing functional pipelines for MutableStatefulAgent operations.
/// </summary>
[<RequireQualifiedAccess>]
module MutableStatefulAgent =
    /// <summary>
    /// Creates and starts a mutable stateful agent.
    /// </summary>
    let start options initialState commandHandler =
        MutableStatefulAgent.Start(options, initialState, commandHandler)

    /// <summary>
    /// Attempts to post a command immediately.
    /// </summary>
    let tryPost command (agent: MutableStatefulAgent<_, _>) =
        agent.TryPost(command)

    /// <summary>
    /// Posts a command asynchronously.
    /// </summary>
    let postAsync command (agent: MutableStatefulAgent<_, _>) =
        agent.PostAsync(command)

    /// <summary>
    /// Reads a projection of the current mutable state, throwing on failure.
    /// </summary>
    let readAsync projection (agent: MutableStatefulAgent<_, _>) =
        agent.ReadAsync(projection)

    /// <summary>
    /// Reads a projection of the current mutable state and returns a result union.
    /// </summary>
    let tryReadAsync projection (agent: MutableStatefulAgent<_, _>) =
        agent.TryReadAsync(projection)
