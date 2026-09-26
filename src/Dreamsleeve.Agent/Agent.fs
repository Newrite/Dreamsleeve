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
    /// Creates a bounded mailbox. Wait provides backpressure; drop modes discard messages when full.
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
    /// Creates a bounded mailbox whose asynchronous writers wait for space without blocking a thread.
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
    /// The message was accepted now. DropOldest and DropNewest may evict it on a later write.
    /// </summary>
    | Posted
    /// <summary>
    /// The mailbox's DropWrite policy discarded this message instead of enqueuing it.
    /// </summary>
    | Dropped
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
    /// A bounded mailbox policy discarded or evicted this request before it could be processed.
    /// </summary>
    | Dropped
    /// <summary>
    /// The mailbox was full and the request could not be accepted into the queue.
    /// </summary>
    | Full
    /// <summary>
    /// The agent is closed and no longer accepting requests.
    /// </summary>
    | Closed
    /// <summary>
    /// The shared timeout for admission and reply expired. Admitted commands are not retracted.
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
    /// The agent was aborted without draining its mailbox. An in-flight handler must finish cooperatively.
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
type ReplyChannel<'T> internal (completion: TaskCompletionSource<AgentAskResult<'T>>) =
    /// <summary>
    /// True once any reply, failure, cancellation, timeout, or shutdown result has won.
    /// This is advisory: use the boolean returned by TryReply for atomic settlement.
    /// </summary>
    member _.IsCompleted = completion.Task.IsCompleted

    /// <summary>Attempts to reply with a value. Only the first terminal result wins.</summary>
    member _.TryReply(value: 'T) = completion.TrySetResult(AgentAskResult.Replied value)

    /// <summary>Replies with a value, ignoring an already completed request.</summary>
    member this.Reply(value: 'T) = this.TryReply(value) |> ignore

    /// <summary>Attempts to return a request-local error without faulting an abandoned task.</summary>
    member _.TryReplyError(error: exn) =
        if isNull error then nullArg (nameof error)
        completion.TrySetResult(AgentAskResult.Faulted error)

    /// <summary>Returns a request-local error, ignoring an already completed request.</summary>
    member this.ReplyError(error: exn) = this.TryReplyError(error) |> ignore

    /// <summary>Attempts to complete the request as canceled.</summary>
    member _.TryCancel() = completion.TrySetResult(AgentAskResult.Canceled)

    /// <summary>Completes the request as canceled, ignoring an already completed request.</summary>
    member this.Cancel() = this.TryCancel() |> ignore

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
        /// Default admission-plus-reply timeout used by AskAsync and TryAskAsync.
        /// None and Timeout.InfiniteTimeSpan mean no deadline; zero expires without posting.
        /// </summary>
        DefaultAskTimeout: TimeSpan option
        /// <summary>
        /// Optional callback invoked on the background processing loop when it starts.
        /// Receives the agent's name.
        /// </summary>
        OnStarted: (string -> unit) option
        /// <summary>
        /// Optional callback invoked exactly once after processing and cleanup stop, before Completion settles.
        /// Receives the agent's name and the reason it stopped. Must not wait for Completion.
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
    let taskFromException<'T> (error: exn) = Task.FromException<'T>(error)

    let inline safeInvoke (callback: unit -> unit) =
        try callback () with _ -> ()

    // CancellationTokenSource timers support at most UInt32.MaxValue - 1 milliseconds.
    let validateTimeout name timeout =
        match timeout with
        | Some value when value <> Timeout.InfiniteTimeSpan &&
                          (value < TimeSpan.Zero || value > TimeSpan.FromMilliseconds(4294967294.0)) ->
            raise (ArgumentOutOfRangeException(name, value,
                "The timeout must be nonnegative, no greater than 4294967294 milliseconds, or Timeout.InfiniteTimeSpan."))
        | _ -> ()

    let resultForStop reason =
        match reason with
        | AgentStopReason.Completed -> AgentAskResult.Closed
        | AgentStopReason.Aborted -> AgentAskResult.Canceled
        | AgentStopReason.Faulted error -> AgentAskResult.Faulted error

    // Envelopes retain request settlement even when the user handler throws before replying.
    // Every callback below is internal and only settles a RunContinuationsAsynchronously TCS.
    type MailboxEnvelope<'Message>
        (message: 'Message,
         onError: (exn -> unit) option,
         onDrop: (unit -> unit) option,
         onDiscard: (AgentStopReason -> unit) option) =
        let mutable dropped = 0
        member _.Message = message
        member _.WasDropped = Volatile.Read(&dropped) <> 0
        member _.Fault(error) = onError |> Option.iter (fun settle -> settle error)
        member _.Drop() =
            Interlocked.Exchange(&dropped, 1) |> ignore
            onDrop |> Option.iter (fun settle -> settle ())
        member _.Discard(reason) = onDiscard |> Option.iter (fun settle -> settle reason)

/// Admission to a mailbox that waits for space and never evicts accepted messages.
[<RequireQualifiedAccess>]
type AgentDeliveryResult =
    | Posted
    | Closed
    | Canceled

/// An asynchronous send-only address, available only for non-dropping mailboxes.
/// Posted acknowledges admission, not processing or persistence.
[<Sealed>]
type ReliableAgentRef<'Message> internal
    (postAsync: 'Message -> CancellationToken -> Task<AgentDeliveryResult>) =

    member _.PostAsync(message, ?cancellationToken: CancellationToken) =
        postAsync message (defaultArg cancellationToken CancellationToken.None)

    /// The mapping runs on the sender and must not access receiver-owned state.
    member _.Map<'Input>(map: 'Input -> 'Message) =
        ReliableAgentRef<'Input>(fun value token -> postAsync (map value) token)

/// A send-only address. Admission is not processing or persistence acknowledgement.
[<Sealed>]
type AgentRef<'Message> internal
    (reliable: bool,
     tryPost: 'Message -> AgentPostResult,
     postAsync: 'Message -> CancellationToken -> Task<AgentPostResult>) =

    member _.IsNonDropping = reliable

    member _.TryPost(message) = tryPost message

    member _.PostAsync(message, ?cancellationToken: CancellationToken) =
        postAsync message (defaultArg cancellationToken CancellationToken.None)

    /// Validate mailbox policy once when wiring a protocol that requires reliable replies.
    /// DropWrite, DropOldest and DropNewest cannot provide this address.
    member _.TryReliable() =
        if not reliable then
            None
        else
            let deliver message token = task {
                let! result = postAsync message token

                match result with
                | AgentPostResult.Posted -> return AgentDeliveryResult.Posted
                | AgentPostResult.Closed -> return AgentDeliveryResult.Closed
                | AgentPostResult.Canceled -> return AgentDeliveryResult.Canceled
                | AgentPostResult.Full
                | AgentPostResult.Dropped ->
                    return invalidOp "Non-dropping PostAsync violated its admission contract."
            }

            Some (ReliableAgentRef<'Message>(deliver))

    /// Narrow an address to one part of the receiving agent's protocol.
    /// The mapping runs on the sender and must not access receiver-owned state.
    member _.Map<'Input>(map: 'Input -> 'Message) =
        AgentRef<'Input>(reliable, (map >> tryPost), fun value token -> postAsync (map value) token)

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
            abortImpl: unit -> unit,
            startBackgroundImpl: (CancellationToken -> Task<unit>) -> unit,
            reliableMailbox: bool
        ) =

    /// <summary>
    /// The configured agent name.
    /// </summary>
    member _.Name = name

    member _.Ref = AgentRef<'Message>(reliableMailbox, tryPostImpl, postAsyncImpl)

    /// Call only from this agent's handler. The operation and mapper execute outside
    /// the mailbox: capture immutable inputs, never mutate agent state there.
    /// Results re-enter the mailbox; cancellation and exceptions are explicit results.
    /// Requires a non-dropping mailbox. Completion waits for all launched work.
    member _.PipeToSelf<'Result>
        (operation: CancellationToken -> Task<'Result>, toMessage: Result<'Result, exn> -> 'Message) =
        let deliver token = task {
            let! result = task {
                try
                    let! value = operation token
                    return Ok value
                with error ->
                    return Error error
            }

            if not token.IsCancellationRequested then
                let! posted = postAsyncImpl (toMessage result) token

                match posted with
                | AgentPostResult.Posted
                | AgentPostResult.Closed
                | AgentPostResult.Canceled -> ()
                | AgentPostResult.Full
                | AgentPostResult.Dropped ->
                    invalidOp "A background completion could not enter its non-dropping mailbox."
        }

        startBackgroundImpl deliver

    /// <summary>
    /// A cancellation token that is triggered when the agent is aborted or faults.
    /// Useful for passing to long-running asynchronous operations within the handler.
    /// Cancellation callbacks must return promptly and must not wait for this agent's Completion.
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
    do
        if isNull (box options) then nullArg (nameof options)
        if isNull (box handler) then nullArg (nameof handler)
        validateTimeout "DefaultAskTimeout" options.DefaultAskTimeout

        match options.Mailbox with
        | AgentMailbox.Bounded(capacity, _, _) when capacity <= 0 ->
            invalidArg "capacity" "Bounded mailbox capacity must be greater than zero."
        | AgentMailbox.Bounded(_, fullMode, _) when not (Enum.IsDefined(fullMode)) ->
            invalidArg "fullMode" "Unknown bounded mailbox full-mode policy."
        | _ -> ()

    let lifetime = new CancellationTokenSource()
    // Capture the token while its source is alive. Contexts never access CTS.Token after disposal.
    let lifetimeToken = lifetime.Token
    let completion = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let termination = TaskCompletionSource<AgentStopReason>(TaskCreationOptions.RunContinuationsAsynchronously)
    let startedEvent = Event<string>()
    let stoppedEvent = Event<string * AgentStopReason>()
    let errorEvent = Event<string * exn>()
    let lifecycleGate = obj()

    let mutable queueLength = 0
    let mutable droppedCount = 0L
    let mutable accepting = 1
    let mutable immediateStop = 0
    // Protected by lifecycleGate. A cancellation reserved before finishing must complete
    // before CTS disposal; no additional cancellation may be requested after finishing starts.
    let mutable finishing = false
    let mutable stopReason: AgentStopReason option = None
    let mutable cancellationTask: Task = Task.CompletedTask

    let mailboxDropsWrites =
        match options.Mailbox with
        | AgentMailbox.Bounded(_, BoundedChannelFullMode.DropWrite, _) -> true
        | _ -> false

    let itemDropped (envelope: MailboxEnvelope<'Message>) =
        Interlocked.Decrement(&queueLength) |> ignore
        Interlocked.Increment(&droppedCount) |> ignore
        envelope.Drop()

    let channel : Channel<MailboxEnvelope<'Message>> =
        match options.Mailbox with
        | AgentMailbox.Unbounded allowSynchronousContinuations ->
            let settings =
                UnboundedChannelOptions(
                    SingleReader = true,
                    SingleWriter = options.SingleWriter,
                    AllowSynchronousContinuations = allowSynchronousContinuations)
            Channel.CreateUnbounded<MailboxEnvelope<'Message>>(settings)
        | AgentMailbox.Bounded(capacity, fullMode, allowSynchronousContinuations) ->
            let settings =
                BoundedChannelOptions(capacity,
                    SingleReader = true,
                    SingleWriter = options.SingleWriter,
                    FullMode = fullMode,
                    AllowSynchronousContinuations = allowSynchronousContinuations)
            Channel.CreateBounded<MailboxEnvelope<'Message>>(settings, Action<_>(itemDropped))

    let getStopReason () = lock lifecycleGate (fun () -> stopReason)
    let isAcceptingMessages () = Volatile.Read(&accepting) = 1
    let isImmediateStopRequested () = Volatile.Read(&immediateStop) <> 0
    let isAbortRequested () =
        match getStopReason () with
        | Some AgentStopReason.Aborted -> true
        | _ -> false

    let completeCore () =
        if Interlocked.Exchange(&accepting, 0) = 1 then
            channel.Writer.TryComplete()
        else
            false

    let requestImmediateStop reason =
        let selected, cancellation =
            lock lifecycleGate (fun () ->
                // A completion mapper can fail while graceful shutdown joins work.
                // Preserve that fault and cancel sibling work rather than reporting success.
                let lateBackgroundFault =
                    match stopReason, reason with
                    | Some AgentStopReason.Completed, AgentStopReason.Faulted _ -> true
                    | _ -> false

                if (finishing || stopReason.IsSome) && not lateBackgroundFault then
                    false, None
                else
                    stopReason <- Some reason
                    Volatile.Write(&immediateStop, 1)
                    Interlocked.Exchange(&accepting, 0) |> ignore
                    let pendingCancellation =
                        match reason with
                        | AgentStopReason.Aborted
                        | AgentStopReason.Faulted _ ->
                            let pending = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                            // Reserve before closing the writer: a synchronous channel continuation
                            // may enter finish before CancelAsync has actually been called.
                            cancellationTask <- pending.Task :> Task
                            Some pending
                        | _ -> None
                    true, pendingCancellation)

        if selected then
            // Close admission first. Even a throwing cancellation callback cannot strand writers.
            // Do not hold the gate here: channels may allow synchronous continuations.
            channel.Writer.TryComplete() |> ignore

            match cancellation with
            | None -> ()
            | Some pending ->
                let cancelAndObserve =
                    task {
                        try
                            let callbacks = lock lifecycleGate (fun () -> lifetime.CancelAsync())
                            do! callbacks
                        with _ -> ()
                        pending.TrySetResult() |> ignore
                    }
                // Every exception from user cancellation callbacks is observed above. The finish
                // path awaits pending before disposal, including when CancelAsync starts late.
                cancelAndObserve |> ignore
            termination.TrySetResult(reason) |> ignore

    let abortCore () = requestImmediateStop AgentStopReason.Aborted

    let tryWriteEnvelope (envelope: MailboxEnvelope<'Message>) =
        if not (isAcceptingMessages ()) then
            AgentPostResult.Closed
        else
            // Reserve before publishing: a reader or drop callback may run before TryWrite returns.
            Interlocked.Increment(&queueLength) |> ignore
            if channel.Writer.TryWrite(envelope) then
                if mailboxDropsWrites && envelope.WasDropped then AgentPostResult.Dropped
                else AgentPostResult.Posted
            else
                Interlocked.Decrement(&queueLength) |> ignore
                if isAcceptingMessages () then AgentPostResult.Full else AgentPostResult.Closed

    let postEnvelopeAsync (envelope: MailboxEnvelope<'Message>) (token: CancellationToken) =
        task {
            let mutable result = AgentPostResult.Full
            let mutable waiting = true
            while waiting do
                if token.IsCancellationRequested then
                    result <- AgentPostResult.Canceled
                    waiting <- false
                else
                    result <- tryWriteEnvelope envelope

                    match result with
                    | AgentPostResult.Full ->
                        try
                            let! canWrite = channel.Writer.WaitToWriteAsync(token).AsTask()
                            if not canWrite then
                                result <- AgentPostResult.Closed
                                waiting <- false
                        with :? OperationCanceledException ->
                            result <- AgentPostResult.Canceled
                            waiting <- false
                    | _ -> waiting <- false
            return result
        }

    let tryPostCore message =
        tryWriteEnvelope (MailboxEnvelope(message, None, None, None))

    let postAsyncCore message token =
        postEnvelopeAsync (MailboxEnvelope(message, None, None, None)) token

    // Only the handler adds work; finish reads after dispatch has stopped.
    let background = ResizeArray<Task>()

    let startBackground operation =
        match options.Mailbox with
        | AgentMailbox.Bounded(_, mode, _) when mode <> BoundedChannelFullMode.Wait ->
            invalidOp "PipeToSelf requires an unbounded or bounded-wait mailbox."
        | _ -> ()

        background.RemoveAll(fun work -> work.IsCompleted) |> ignore

        let runWork () : Task = task {
            try
                do! operation lifetimeToken
            with error ->
                requestImmediateStop (AgentStopReason.Faulted error)
        }

        background.Add(Task.Run(Func<Task>(runWork)))

    let reliableMailbox =
        match options.Mailbox with
        | AgentMailbox.Unbounded _
        | AgentMailbox.Bounded(_, BoundedChannelFullMode.Wait, _) -> true
        | AgentMailbox.Bounded _ -> false

    let context =
        AgentContext<'Message>(
            options.Name, lifetimeToken, tryPostCore, postAsyncCore, completeCore, abortCore, startBackground, reliableMailbox)

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

    let tryTakeNext () =
        // Dispatch and Abort are ordered at this gate. At most the current dispatched handler
        // remains in flight after Abort; cancellation is cooperative, not thread interruption.
        lock lifecycleGate (fun () ->
            if isImmediateStopRequested () then
                None
            else
                match channel.Reader.TryRead() with
                | true, envelope ->
                    Interlocked.Decrement(&queueLength) |> ignore
                    Some envelope
                | false, _ -> None)

    let finish () =
        task {
            let reason =
                lock lifecycleGate (fun () ->
                    finishing <- true
                    Interlocked.Exchange(&accepting, 0) |> ignore
                    let reason = defaultArg stopReason AgentStopReason.Completed
                    stopReason <- Some reason
                    reason)

            channel.Writer.TryComplete() |> ignore
            // The sole reader is now done dispatching. Drop references and settle queued requests.
            let mutable draining = true
            while draining do
                match channel.Reader.TryRead() with
                | true, envelope ->
                    Interlocked.Decrement(&queueLength) |> ignore
                    envelope.Discard reason
                | false, _ -> draining <- false
            termination.TrySetResult(reason) |> ignore

            do! Task.WhenAll(background)

            // Joining work can escalate a graceful stop to a background mapper fault.
            // Capture cancellation after the join so its callbacks cannot outlive the CTS.
            let reason, callbacks =
                lock lifecycleGate (fun () -> defaultArg stopReason reason, cancellationTask)

            try do! callbacks with _ -> ()
            lock lifecycleGate (fun () -> lifetime.Dispose())

            safeInvoke (fun () -> stoppedEvent.Trigger(options.Name, reason))
            options.OnStopped |> Option.iter (fun callback -> safeInvoke (fun () -> callback (options.Name, reason)))

            match reason with
            | AgentStopReason.Completed -> completion.TrySetResult() |> ignore
            | AgentStopReason.Aborted -> completion.TrySetCanceled(lifetimeToken) |> ignore
            | AgentStopReason.Faulted error -> completion.TrySetException(error) |> ignore
        }

    let runLoop () =
        task {
            signalStarted ()
            try
                let mutable running = true
                while running && not (isImmediateStopRequested ()) do
                    let! canRead = channel.Reader.WaitToReadAsync(lifetimeToken).AsTask()
                    if not canRead then
                        running <- false
                    else
                        let mutable draining = true
                        while draining && not (isImmediateStopRequested ()) do
                            match tryTakeNext () with
                            | None -> draining <- false
                            | Some envelope ->
                                try
                                    do! handler context envelope.Message
                                with
                                | :? OperationCanceledException when isAbortRequested () ->
                                    envelope.Discard AgentStopReason.Aborted
                                | error ->
                                    envelope.Fault error

                                    match reportHandlerError error with
                                    | AgentErrorAction.Continue -> ()
                                    | AgentErrorAction.Stop ->
                                        requestImmediateStop (AgentStopReason.Faulted error)
            with
            | :? OperationCanceledException when isAbortRequested () -> ()
            | error -> requestImmediateStop (AgentStopReason.Faulted error)
            do! finish ()
        }

    do Task.Run(fun () -> runLoop () :> Task) |> ignore

    let tryAskCore
        (buildMessage: ReplyChannel<'Reply> -> 'Message)
        (timeout: TimeSpan option)
        (token: CancellationToken) =
        task {
            let effectiveTimeout = Option.orElse options.DefaultAskTimeout timeout
            let validationError =
                try
                    validateTimeout "timeout" effectiveTimeout
                    if isNull (box buildMessage) then nullArg (nameof buildMessage)
                    None
                with error -> Some error

            match validationError with
            | Some error -> return AgentAskResult.Faulted error
            | None when token.IsCancellationRequested -> return AgentAskResult.Canceled
            | None when effectiveTimeout = Some TimeSpan.Zero -> return AgentAskResult.TimedOut
            | None when not (isAcceptingMessages ()) -> return AgentAskResult.Closed
            | None ->
                let replyCompletion =
                    TaskCompletionSource<AgentAskResult<'Reply>>(TaskCreationOptions.RunContinuationsAsynchronously)
                let reply = ReplyChannel<'Reply>(replyCompletion)
                let settle result = replyCompletion.TrySetResult(result) |> ignore
                let cancellationResult () =
                    if token.IsCancellationRequested then AgentAskResult.Canceled
                    else AgentAskResult.TimedOut

                use waitCts =
                    if token.CanBeCanceled then CancellationTokenSource.CreateLinkedTokenSource(token)
                    else new CancellationTokenSource()
                use registration = waitCts.Token.Register(fun () -> settle (cancellationResult ()))

                match effectiveTimeout with
                | Some value when value <> Timeout.InfiniteTimeSpan -> waitCts.CancelAfter(value)
                | _ -> ()

                let messageResult =
                    try Ok (buildMessage reply)
                    with error -> Error error

                match messageResult with
                | Error error -> settle (AgentAskResult.Faulted error)
                | Ok message ->
                    let envelope =
                        MailboxEnvelope(
                            message,
                            Some (fun error -> settle (AgentAskResult.Faulted error)),
                            Some (fun () -> settle AgentAskResult.Dropped),
                            Some (fun reason -> settle (resultForStop reason)))
                    let! postResult = postEnvelopeAsync envelope waitCts.Token

                    match postResult with
                    | AgentPostResult.Posted -> ()
                    | AgentPostResult.Dropped -> settle AgentAskResult.Dropped
                    | AgentPostResult.Full -> settle AgentAskResult.Full
                    | AgentPostResult.Closed -> settle AgentAskResult.Closed
                    | AgentPostResult.Canceled -> settle (cancellationResult ())

                if not replyCompletion.Task.IsCompleted then
                    let! winner = Task.WhenAny(replyCompletion.Task :> Task, termination.Task :> Task)
                    if Object.ReferenceEquals(winner, termination.Task) then
                        settle (resultForStop termination.Task.Result)
                return! replyCompletion.Task
        }

    /// <summary>The configured agent name.</summary>
    member _.Name = options.Name

    /// Send-only address; does not expose lifecycle operations.
    member _.Ref = context.Ref

    /// <summary>
    /// Completes after dispatch, queue cleanup, cancellation callbacks, and stopped callbacks finish.
    /// Succeeds for Completed, is canceled for Aborted, and faults for Faulted.
    /// Lifecycle callbacks must not block waiting for this task.
    /// </summary>
    member _.Completion : Task = completion.Task :> Task

    /// <summary>
    /// Approximate queued count, excluding the current handler and writers waiting for admission.
    /// On channel implementations without Count, a concurrent write reservation can be included briefly.
    /// </summary>
    member _.QueueLength =
        if channel.Reader.CanCount then channel.Reader.Count
        else max 0 (Volatile.Read(&queueLength))

    /// <summary>Total messages discarded by the bounded full-mode policy; excludes shutdown cleanup.</summary>
    member _.DroppedCount = Interlocked.Read(&droppedCount)

    /// <summary>True while the mailbox accepts new messages.</summary>
    member _.IsAcceptingMessages = isAcceptingMessages ()

    /// <summary>The selected shutdown reason, or None before shutdown has been selected.</summary>
    member _.StopReason = getStopReason ()

    /// <summary>Raised on the background loop at startup. Use OnStarted to avoid subscription races.</summary>
    member _.Started = startedEvent.Publish

    /// <summary>Raised for unhandled handler failures before the configured error policy runs.</summary>
    member _.Errored = errorEvent.Publish

    /// <summary>Raised exactly once during shutdown, before Completion becomes complete.</summary>
    member _.Stopped = stoppedEvent.Publish

    /// <summary>
    /// Attempts immediate admission. Posted means accepted now, not processed or durably retained:
    /// a later write using DropOldest or DropNewest may evict an accepted message.
    /// </summary>
    member _.TryPost(message: 'Message) = tryPostCore message

    /// <summary>
    /// Posts a message, asynchronously waiting for space only with a bounded Wait mailbox.
    /// Cancellation stops waiting for admission; an already accepted message is not rolled back.
    /// </summary>
    member _.PostAsync(message: 'Message, ?cancellationToken: CancellationToken) =
        postAsyncCore message (defaultArg cancellationToken CancellationToken.None)

    /// <summary>
    /// Constructs a request, waits for admission, and waits for its reply under one timeout budget.
    /// The first terminal result wins. Invalid timeout arguments return Faulted before construction.
    /// Timeout or cancellation does not retract an admitted command or undo its effects.
    /// BuildMessage must only construct the message; it runs synchronously on the caller.
    /// </summary>
    member _.TryAskAsync<'Reply>(buildMessage: ReplyChannel<'Reply> -> 'Message, ?timeout: TimeSpan, ?cancellationToken: CancellationToken) =
        tryAskCore buildMessage timeout (defaultArg cancellationToken CancellationToken.None)

    /// <summary>Sends a request and returns the reply, throwing on any unsuccessful request result.</summary>
    member this.AskAsync<'Reply>(buildMessage: ReplyChannel<'Reply> -> 'Message, ?timeout: TimeSpan, ?cancellationToken: CancellationToken) =
        task {
            let! result = this.TryAskAsync(buildMessage, ?timeout = timeout, ?cancellationToken = cancellationToken)

            match result with
            | AgentAskResult.Replied value -> return value
            | AgentAskResult.Faulted error -> return! taskFromException<'Reply> error
            | AgentAskResult.Dropped -> return! taskFromException<'Reply> (InvalidOperationException($"Agent '{options.Name}' mailbox dropped the request."))
            | AgentAskResult.Full -> return! taskFromException<'Reply> (InvalidOperationException($"Agent '{options.Name}' mailbox is full."))
            | AgentAskResult.Closed -> return! taskFromException<'Reply> (InvalidOperationException($"Agent '{options.Name}' is not accepting new messages."))
            | AgentAskResult.TimedOut -> return! taskFromException<'Reply> (TimeoutException($"Request to agent '{options.Name}' timed out."))
            | AgentAskResult.Canceled -> return! taskFromException<'Reply> (OperationCanceledException($"Ask to agent '{options.Name}' was canceled."))
        }

    /// <summary>Closes admission and gracefully drains every accepted message.</summary>
    member _.Complete() = completeCore ()

    /// <summary>
    /// Closes admission, stops queued dispatch, and requests cooperative in-flight cancellation.
    /// Returns immediately; await Completion to observe finished cleanup. Safe after shutdown.
    /// </summary>
    member _.Abort() = abortCore ()

    interface IDisposable with
        member this.Dispose() = this.Abort()

    interface IAsyncDisposable with
        member this.DisposeAsync() =
            this.Complete() |> ignore
            ValueTask(this.Completion)

    /// <summary>Validates configuration and starts a sequential background message loop.</summary>
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
        /// When present this policy takes precedence over AgentOptions.OnError; otherwise the base policy is used.
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
    /// A cancellation token that is triggered when the underlying agent is aborted or faults.
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
/// A sequential agent whose handler returns explicit replacements of the current state value.
/// Prefer immutable state. Serialization protects state only while mutable references stay inside
/// the agent; return immutable values or detached snapshots from read projections.
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

    let onError (name, error) =
        match options.OnUnhandled with
        | None ->
            match options.AgentOptions.OnError with
            | Some decide -> decide (name, error)
            | None -> AgentErrorAction.Stop
        | Some decide ->
            match decide (state, error) with
            | StatefulErrorAction.KeepStateAndContinue -> AgentErrorAction.Continue
            | StatefulErrorAction.ReplaceStateAndContinue nextState ->
                applyState nextState
                AgentErrorAction.Continue
            | StatefulErrorAction.Stop -> AgentErrorAction.Stop
            | StatefulErrorAction.StopWithState nextState ->
                applyState nextState
                AgentErrorAction.Stop

    let inner =
        Agent.Start(
            { options.AgentOptions with OnError = Some onError },
            fun agentContext envelope ->
                task {
                    match envelope with
                    | Query query -> query.Execute state
                    | Command command ->
                        let ctx =
                            StatefulAgentContext(
                                agentContext.Name, agentContext.CancellationToken,
                                agentContext.Complete, agentContext.Abort)
                        let! transition = commandHandler ctx state command

                        match transition with
                        | StatefulTransition.Stay -> ()
                        | StatefulTransition.SetState nextState -> applyState nextState
                        | StatefulTransition.Stop -> agentContext.Complete() |> ignore
                        | StatefulTransition.StopWithState nextState ->
                            applyState nextState
                            agentContext.Complete() |> ignore
                })

    /// <summary>
    /// The configured stateful agent name.
    /// </summary>
    member _.Name = inner.Name

    member _.Ref = inner.Ref.Map(Command)

    /// <summary>
    /// A task that completes when the stateful agent has fully stopped.
    /// </summary>
    member _.Completion = inner.Completion

    /// <summary>
    /// The approximate number of queued envelopes (commands and queries).
    /// </summary>
    member _.QueueLength = inner.QueueLength

    /// <summary>Total envelopes discarded by the bounded mailbox policy.</summary>
    member _.DroppedCount = inner.DroppedCount

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
    /// Enqueues an atomic command/request and waits for its handler to reply.
    /// Timeout includes admission and reply; an admitted command may still execute after timeout.
    /// </summary>
    member _.TryAskAsync<'Reply>(buildMessage: ReplyChannel<'Reply> -> 'Command, ?timeout: TimeSpan, ?cancellationToken: CancellationToken) =
        inner.TryAskAsync(
            (fun reply -> Command (buildMessage reply)),
            ?timeout = timeout,
            ?cancellationToken = cancellationToken)

    /// <summary>Enqueues a command/request and returns the reply, throwing on unsuccessful results.</summary>
    member _.AskAsync<'Reply>(buildMessage: ReplyChannel<'Reply> -> 'Command, ?timeout: TimeSpan, ?cancellationToken: CancellationToken) =
        inner.AskAsync(
            (fun reply -> Command (buildMessage reply)),
            ?timeout = timeout,
            ?cancellationToken = cancellationToken)

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

    /// <summary>Sends a command/request and returns its reply, throwing on failure.</summary>
    let askAsync buildMessage (agent: StatefulAgent<_, _>) =
        agent.AskAsync(buildMessage)

    /// <summary>Sends a command/request and returns a result union.</summary>
    let tryAskAsync buildMessage (agent: StatefulAgent<_, _>) =
        agent.TryAskAsync(buildMessage)

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
    /// Stop with a Faulted reason immediately after this handler; discard queued commands.
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
        /// When present this policy takes precedence over AgentOptions.OnError; otherwise the base policy is used.
        /// Continuing does not roll back partial in-place mutations made before the error.
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

    let onError (name, error) =
        match options.OnUnhandled with
        | None ->
            match options.AgentOptions.OnError with
            | Some decide -> decide (name, error)
            | None -> AgentErrorAction.Stop
        | Some decide ->
            match decide (state, error) with
            | MutableStatefulErrorAction.Continue -> AgentErrorAction.Continue
            | MutableStatefulErrorAction.Stop -> AgentErrorAction.Stop

    let inner =
        Agent.Start(
            { options.AgentOptions with OnError = Some onError },
            fun agentContext envelope ->
                task {
                    match envelope with
                    | Query query -> query.Execute state
                    | Command command ->
                        let ctx =
                            StatefulAgentContext(
                                agentContext.Name, agentContext.CancellationToken,
                                agentContext.Complete, agentContext.Abort)
                        let! transition = commandHandler ctx state command

                        match transition with
                        | MutableStatefulTransition.Stay -> ()
                        | MutableStatefulTransition.Stop -> agentContext.Complete() |> ignore
                })

    /// <summary>
    /// The configured mutable stateful agent name.
    /// </summary>
    member _.Name = inner.Name

    member _.Ref = inner.Ref.Map(Command)

    /// <summary>
    /// A task that completes when the mutable stateful agent has fully stopped.
    /// </summary>
    member _.Completion = inner.Completion

    /// <summary>
    /// The approximate number of queued envelopes (commands and queries).
    /// </summary>
    member _.QueueLength = inner.QueueLength

    /// <summary>Total envelopes discarded by the bounded mailbox policy.</summary>
    member _.DroppedCount = inner.DroppedCount

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
    /// Enqueues an atomic command/request and waits for its handler to reply.
    /// Timeout includes admission and reply; an admitted command may still execute after timeout.
    /// </summary>
    member _.TryAskAsync<'Reply>(buildMessage: ReplyChannel<'Reply> -> 'Command, ?timeout: TimeSpan, ?cancellationToken: CancellationToken) =
        inner.TryAskAsync(
            (fun reply -> Command (buildMessage reply)),
            ?timeout = timeout,
            ?cancellationToken = cancellationToken)

    /// <summary>Enqueues a command/request and returns the reply, throwing on unsuccessful results.</summary>
    member _.AskAsync<'Reply>(buildMessage: ReplyChannel<'Reply> -> 'Command, ?timeout: TimeSpan, ?cancellationToken: CancellationToken) =
        inner.AskAsync(
            (fun reply -> Command (buildMessage reply)),
            ?timeout = timeout,
            ?cancellationToken = cancellationToken)

    /// <summary>
    /// Enqueues a read query to safely project data from the current mutable state.
    /// The projection runs sequentially inside the agent loop.
    /// Return immutable values or detached snapshots, never live mutable objects or lazy views:
    /// even a read-only caller would otherwise race later mutations inside the agent.
    /// </summary>
    /// <param name="projection">A function that extracts data from the current state.</param>
    /// <param name="timeout">An optional timeout for the query.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    member _.TryReadAsync<'Reply>(projection: 'State -> 'Reply, ?timeout: TimeSpan, ?cancellationToken: CancellationToken) =
        inner.TryAskAsync(
            (fun reply -> Query (StateQuery<'State, 'Reply>(projection, reply) :> IStateQuery<'State>)),
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
            (fun reply -> Query (StateQuery<'State, 'Reply>(projection, reply) :> IStateQuery<'State>)),
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

    /// <summary>Sends a command/request and returns its reply, throwing on failure.</summary>
    let askAsync buildMessage (agent: MutableStatefulAgent<_, _>) =
        agent.AskAsync(buildMessage)

    /// <summary>Sends a command/request and returns a result union.</summary>
    let tryAskAsync buildMessage (agent: MutableStatefulAgent<_, _>) =
        agent.TryAskAsync(buildMessage)

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
