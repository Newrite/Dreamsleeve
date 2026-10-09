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
    /// One FIFO with a limit on ordinary queued messages and additional control capacity.
    /// Control messages use the total capacity without overtaking accepted ordinary messages.
    | BoundedWithControl of ordinaryCapacity: int * controlReserve: int

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

    /// Bound ordinary admission separately while retaining one shared FIFO.
    /// The handler currently executing is not included in either queued capacity.
    let boundedWithControl ordinaryCapacity controlReserve =
        AgentMailbox.BoundedWithControl(ordinaryCapacity, controlReserve)

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

/// Expected rejection at construction, before an agent or worker starts.
[<RequireQualifiedAccess>]
type AgentStartError =
    | NullArgument of name: string
    | InvalidTimeout of value: TimeSpan
    | InvalidCapacity of name: string * value: int
    | CapacityOverflow of ordinaryCapacity: int * controlReserve: int
    | MissingControlClassifier
    | UnknownFullMode of value: BoundedChannelFullMode
    | DroppingMailbox
    | InvalidInterval of value: TimeSpan
    | InvalidRestartPolicy of field: string

/// Malformed request input is rejected before building or admitting a message.
[<RequireQualifiedAccess>]
type AgentRequestError =
    | NullArgument of name: string
    | InvalidTimeout of value: TimeSpan

/// <summary>
/// The result of sending a request (Ask) and waiting for a reply, encapsulating possible operational failures.
/// </summary>
[<RequireQualifiedAccess>]
type AgentAskResult<'T> =
    | InvalidRequest of AgentRequestError
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
        if isNull error then
            Error (AgentRequestError.NullArgument(nameof error))
        else
            Ok (completion.TrySetResult(AgentAskResult.Faulted error))

    // Runtime catch sites already hold a nonnull original exception.
    member internal _.ReplyFault(error: exn) = completion.TrySetResult(AgentAskResult.Faulted error) |> ignore

    /// <summary>Returns a request-local error, ignoring an already completed request.</summary>
    member this.ReplyError(error: exn) = this.TryReplyError(error) |> Result.map ignore

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
        /// Default admission-plus-reply timeout used by TryAskAsync.
        /// None and Timeout.InfiniteTimeSpan mean no deadline; zero expires without posting.
        /// </summary>
        DefaultAskTimeout: TimeSpan option
        /// <summary>
        /// Optional callback invoked on the background processing loop when it starts.
        /// Receives the agent's name. A notification fault stops the owner before dispatch.
        /// </summary>
        OnStarted: (string -> unit) option
        /// <summary>
        /// Optional callback invoked exactly once after processing and cleanup stop, before Completion settles.
        /// Receives the name and sealed stop reason. A failure faults Completion without
        /// changing that reason. Must not wait for Completion.
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
module internal AgentInternals =
    // Preserve the public Ask deadline contract; dependency timers truncate milliseconds.
    let invalidTimeout timeout =
        match timeout with
        | Some value when value <> Timeout.InfiniteTimeSpan &&
                          (value < TimeSpan.Zero || value > TimeSpan.FromMilliseconds(4294967294.0)) ->
            Some value
        | Some _ | None -> None

    type CheckedAgentOptions<'Message> = {
        Options: AgentOptions
        IsControl: ('Message -> bool) option
        IsReliable: bool
    }

    let checkOptions (options: AgentOptions) (handler: 'Handler) (isControl: ('Message -> bool) option) =
        let missingCallback =
            if isNull (box options) then None
            else
                [ options.OnStarted |> Option.map (fun callback -> "OnStarted", box callback)
                  options.OnStopped |> Option.map (fun callback -> "OnStopped", box callback)
                  options.OnError |> Option.map (fun callback -> "OnError", box callback)
                  isControl |> Option.map (fun callback -> "isControl", box callback) ]
                |> List.tryPick (function
                    | Some(name, callback) when isNull callback -> Some name
                    | _ -> None)

        let error =
            if isNull (box options) then Some (AgentStartError.NullArgument "options")
            elif isNull (box handler) then Some (AgentStartError.NullArgument "handler")
            elif isNull (box options.Mailbox) then Some (AgentStartError.NullArgument "Mailbox")
            else
                match missingCallback, invalidTimeout options.DefaultAskTimeout, options.Mailbox with
                | Some name, _, _ -> Some (AgentStartError.NullArgument name)
                | None, Some value, _ -> Some (AgentStartError.InvalidTimeout value)
                | None, None, AgentMailbox.BoundedWithControl(ordinary, _) when ordinary < 1 ->
                    Some (AgentStartError.InvalidCapacity("ordinaryCapacity", ordinary))
                | None, None, AgentMailbox.BoundedWithControl(_, reserve) when reserve < 1 ->
                    Some (AgentStartError.InvalidCapacity("controlReserve", reserve))
                | None, None, AgentMailbox.BoundedWithControl(ordinary, reserve) when int64 ordinary + int64 reserve > int64 Int32.MaxValue ->
                    Some (AgentStartError.CapacityOverflow(ordinary, reserve))
                | None, None, AgentMailbox.BoundedWithControl _ when isControl.IsNone -> Some AgentStartError.MissingControlClassifier
                | None, None, AgentMailbox.Bounded(capacity, _, _) when capacity < 1 ->
                    Some (AgentStartError.InvalidCapacity("capacity", capacity))
                | None, None, AgentMailbox.Bounded(_, mode, _) when not (Enum.IsDefined mode) ->
                    Some (AgentStartError.UnknownFullMode mode)
                | None, None, (AgentMailbox.Unbounded _ | AgentMailbox.Bounded _ | AgentMailbox.BoundedWithControl _) -> None

        match error with
        | Some failure -> Error failure
        | None ->
            // CLIMutable input must not remain an alias into a prepared construction.
            let snapshot = {
                Name = options.Name
                Mailbox = options.Mailbox
                SingleWriter = options.SingleWriter
                DefaultAskTimeout = options.DefaultAskTimeout

                OnStarted = options.OnStarted
                OnStopped = options.OnStopped
                OnError = options.OnError
            }
            let reliable =
                match snapshot.Mailbox with
                | AgentMailbox.Unbounded _ | AgentMailbox.BoundedWithControl _ -> true
                | AgentMailbox.Bounded(_, mode, _) -> mode = BoundedChannelFullMode.Wait
            Ok {
                Options = snapshot
                IsControl = isControl
                IsReliable = reliable
            }

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
        member val IsOrdinary = false with get, set
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

/// Immediate admission to a mailbox that never evicts accepted messages.
[<RequireQualifiedAccess>]
type AgentTryDeliveryResult =
    | Posted
    | Full
    | Closed

module private AdmissionTasks =
    let private posted = Task.FromResult AgentPostResult.Posted
    let private closed = Task.FromResult AgentPostResult.Closed
    let private canceled = Task.FromResult AgentPostResult.Canceled
    let private dropped = Task.FromResult AgentPostResult.Dropped
    let private full = Task.FromResult AgentPostResult.Full

    let post = function
        | AgentPostResult.Posted -> posted
        | AgentPostResult.Closed -> closed
        | AgentPostResult.Canceled -> canceled
        | AgentPostResult.Dropped -> dropped
        | AgentPostResult.Full -> full

    let private delivered = Task.FromResult AgentDeliveryResult.Posted
    let private deliveryClosed = Task.FromResult AgentDeliveryResult.Closed
    let private deliveryCanceled = Task.FromResult AgentDeliveryResult.Canceled

    let delivery = function
        | AgentDeliveryResult.Posted -> delivered
        | AgentDeliveryResult.Closed -> deliveryClosed
        | AgentDeliveryResult.Canceled -> deliveryCanceled

    let generalTryDelivery = function
        | AgentTryDeliveryResult.Posted -> AgentPostResult.Posted
        | AgentTryDeliveryResult.Full -> AgentPostResult.Full
        | AgentTryDeliveryResult.Closed -> AgentPostResult.Closed

    let generalDelivery = function
        | AgentDeliveryResult.Posted -> AgentPostResult.Posted
        | AgentDeliveryResult.Closed -> AgentPostResult.Closed
        | AgentDeliveryResult.Canceled -> AgentPostResult.Canceled

/// A send-only address, available only for non-dropping mailboxes.
/// Posted acknowledges admission, not processing or persistence.
[<Sealed>]
type ReliableAgentRef<'Message> internal
    (tryPost: 'Message -> AgentTryDeliveryResult,
     postAsync: 'Message -> CancellationToken -> Task<AgentDeliveryResult>) =

    member _.TryPost(message) = tryPost message

    member _.PostAsync(message, ?cancellationToken: CancellationToken) =
        postAsync message (defaultArg cancellationToken CancellationToken.None)

    /// The mapping runs on the sender and must not access receiver-owned state.
    member _.Map<'Input>(map: 'Input -> 'Message) =
        ReliableAgentRef<'Input>((map >> tryPost), fun value token -> postAsync (map value) token)

/// A send-only address. Admission is not processing or persistence acknowledgement.
[<Sealed>]
type AgentRef<'Message> internal
    (reliable: ReliableAgentRef<'Message> option,
     tryPost: 'Message -> AgentPostResult,
     postAsync: 'Message -> CancellationToken -> Task<AgentPostResult>) =

    member _.IsNonDropping = reliable.IsSome

    member _.TryPost(message) = tryPost message

    member _.PostAsync(message, ?cancellationToken: CancellationToken) =
        postAsync message (defaultArg cancellationToken CancellationToken.None)

    /// Validate mailbox policy once when wiring a protocol that requires reliable replies.
    /// DropWrite, DropOldest and DropNewest cannot provide this address.
    member _.TryReliable() = reliable

    /// Narrow an address to one part of the receiving agent's protocol.
    /// The mapping runs on the sender and must not access receiver-owned state.
    member _.Map<'Input>(map: 'Input -> 'Message) =
        AgentRef<'Input>(reliable |> Option.map (fun address -> address.Map map), (map >> tryPost), fun value token -> postAsync (map value) token)

/// <summary>
/// Context passed to each message handler of a base agent, providing access to its lifecycle and self-messaging capabilities.
/// </summary>
[<Sealed>]
type AgentContext<'Message>
    internal
        (
            name: string,
            cancellationToken: CancellationToken,
            dispatchStoppedToken: CancellationToken,
            tryPostImpl: 'Message -> AgentPostResult,
            postAsyncImpl: 'Message -> CancellationToken -> Task<AgentPostResult>,
            completeImpl: unit -> bool,
            abortImpl: unit -> unit,
            failImpl: exn -> unit,
            startBackgroundImpl: (CancellationToken -> Task<unit>) -> unit,
            reliableRef: ReliableAgentRef<'Message> option
        ) =

    /// <summary>
    /// The configured agent name.
    /// </summary>
    member _.Name = name

    member _.Ref = AgentRef<'Message>(reliableRef, tryPostImpl, postAsyncImpl)

    /// A supported capability; dropping mailboxes legitimately have none.
    member this.TryReliable() = reliableRef |> Option.map (fun address -> ReliableAgentContext<'Message>(this, address))

    member internal _.DispatchStopped = dispatchStoppedToken
    member internal _.Fail(error: exn) = failImpl error

    // Library workers only. Application state remains in the handler.
    member internal _.StartDelivery(operation: CancellationToken -> Task<unit>) =
        startBackgroundImpl operation

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

/// The same owner context with admission and tracked work restricted to non-dropping mailboxes.
and [<Sealed>] ReliableAgentContext<'Message> internal (context: AgentContext<'Message>, address: ReliableAgentRef<'Message>) =
    member _.Name = context.Name
    member _.Ref = address
    member _.CancellationToken = context.CancellationToken
    member _.Complete() = context.Complete()
    member _.Abort() = context.Abort()
    member _.TryPost(message) = address.TryPost message
    member _.PostAsync(message, ?cancellationToken: CancellationToken) = address.PostAsync(message, ?cancellationToken = cancellationToken)
    member internal _.DispatchStopped = context.DispatchStopped
    member internal _.Fail(error: exn) = context.Fail error
    member internal _.StartDelivery(operation) = context.StartDelivery operation

    /// Call only from this owner's handler. Work receives detached immutable inputs.
    /// Completion joins every launched operation, including cleanup after Abort.
    member this.PipeToSelf<'Result>(operation: CancellationToken -> Task<'Result>, toMessage: Result<'Result, exn> -> 'Message) =
        let deliver token = task {
            let! result = task {
                try
                    let! value = operation token
                    return Ok value
                with error -> return Error error
            }
            if not token.IsCancellationRequested then
                let! delivered = address.PostAsync(toMessage result, cancellationToken = token)
                match delivered with
                | AgentDeliveryResult.Posted | AgentDeliveryResult.Closed | AgentDeliveryResult.Canceled -> ()
        }
        this.StartDelivery deliver

/// <summary>
/// A task-first asynchronous agent implemented on top of System.Threading.Channels.
/// Processes messages sequentially without built-in state management.
/// </summary>
[<Sealed>]
type Agent<'Message> private (checkedOptions: CheckedAgentOptions<'Message>, handler: AgentContext<'Message> -> ReliableAgentContext<'Message> -> 'Message -> Task<unit>) =
    let options = checkedOptions.Options
    let isControl = checkedOptions.IsControl

    let ordinaryLimit =
        match options.Mailbox with
        | AgentMailbox.BoundedWithControl(ordinary, _) -> Some ordinary
        | AgentMailbox.Unbounded _ | AgentMailbox.Bounded _ -> None

    // This boundary lock covers admission bookkeeping, not application state.
    // Both classes share the channel; a notification changes only after capacity or closure.
    let admissionGate = obj()
    let mutable ordinaryQueued = 0
    let mutable admissionChanged: TaskCompletionSource<unit> = null

    let notifyAdmissionLocked () =
        if not (isNull admissionChanged) then
            let previous = admissionChanged
            admissionChanged <- null
            previous.TrySetResult() |> ignore

    let notifyAdmission () =
        if ordinaryLimit.IsSome then
            lock admissionGate notifyAdmissionLocked

    let releaseAdmission (envelope: MailboxEnvelope<'Message>) =
        if ordinaryLimit.IsSome then
            lock admissionGate (fun () ->
                if envelope.IsOrdinary then ordinaryQueued <- ordinaryQueued - 1
                notifyAdmissionLocked ())

    let classify (envelope: MailboxEnvelope<'Message>) =
        match ordinaryLimit, isControl with
        | Some _, Some control -> envelope.IsOrdinary <- not (control envelope.Message)
        | Some _, None | None, _ -> ()
        envelope

    let lifetime = new CancellationTokenSource()
    let dispatchStopped = new CancellationTokenSource()
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
    // before CTS disposal. Graceful draining can still be escalated until stopSealed.
    let mutable finishing = false
    let mutable stopSealed = false
    let mutable stopReason: AgentStopReason option = None
    let mutable cancellationTask: Task = Task.CompletedTask
    // Protected by lifecycleGate. The same original failure can reach the sink
    // through delivery and a later join; retain it once, by reference identity.
    let failures = ResizeArray<exn>()
    let recordFailure error =
        if not (failures |> Seq.exists (fun previous -> Object.ReferenceEquals(previous, error))) then
            failures.Add error

    let mailboxDropsWrites =
        match options.Mailbox with
        | AgentMailbox.Bounded(_, BoundedChannelFullMode.DropWrite, _) -> true
        | _ -> false

    let itemDropped (envelope: MailboxEnvelope<'Message>) =
        Interlocked.Decrement(&queueLength) |> ignore
        Interlocked.Increment(&droppedCount) |> ignore
        envelope.Drop()

    let channel : Channel<MailboxEnvelope<'Message>> =
        let bounded capacity fullMode allowSynchronousContinuations =
            let settings =
                BoundedChannelOptions(capacity,
                    SingleReader = true,
                    SingleWriter = options.SingleWriter,
                    FullMode = fullMode,
                    AllowSynchronousContinuations = allowSynchronousContinuations)
            Channel.CreateBounded<MailboxEnvelope<'Message>>(settings, Action<_>(itemDropped))

        match options.Mailbox with
        | AgentMailbox.Unbounded allowSynchronousContinuations ->
            let settings =
                UnboundedChannelOptions(
                    SingleReader = true,
                    SingleWriter = options.SingleWriter,
                    AllowSynchronousContinuations = allowSynchronousContinuations)
            Channel.CreateUnbounded<MailboxEnvelope<'Message>>(settings)
        | AgentMailbox.Bounded(capacity, fullMode, allowSynchronousContinuations) ->
            bounded capacity fullMode allowSynchronousContinuations
        | AgentMailbox.BoundedWithControl(ordinary, reserve) ->
            bounded (ordinary + reserve) BoundedChannelFullMode.Wait false

    let getStopReason () = lock lifecycleGate (fun () -> stopReason)
    let isAcceptingMessages () = Volatile.Read(&accepting) = 1
    let isImmediateStopRequested () = Volatile.Read(&immediateStop) <> 0
    let completeCore () =
        if Interlocked.Exchange(&accepting, 0) = 1 then
            let completed = channel.Writer.TryComplete()
            notifyAdmission ()
            completed
        else
            false

    let requestImmediateStop reason =
        let selected, cancellation =
            lock lifecycleGate (fun () ->
                match reason with
                | AgentStopReason.Faulted error -> recordFailure error
                | AgentStopReason.Completed | AgentStopReason.Aborted -> ()
                // Deliveries can fail or be aborted while graceful shutdown joins work.
                // Escalate until the final reason and cancellation callbacks are sealed.
                let escalation =
                    match stopReason, reason with
                    | Some AgentStopReason.Completed, (AgentStopReason.Faulted _ | AgentStopReason.Aborted) -> true
                    | _ -> false

                if stopSealed || ((finishing || stopReason.IsSome) && not escalation) then
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
            notifyAdmission ()

            match cancellation with
            | None -> ()
            | Some pending ->
                let cancelAndObserve =
                    task {
                        try
                            let callbacks = lock lifecycleGate (fun () -> lifetime.CancelAsync())
                            do! callbacks
                        with error ->
                            // CancelAsync's aggregate owns all callback faults, not just
                            // the exception selected by an await.
                            lock lifecycleGate (fun () ->
                                match error with
                                | :? AggregateException as aggregate ->
                                    for failure in aggregate.InnerExceptions do recordFailure failure
                                | error -> recordFailure error)
                        pending.TrySetResult() |> ignore
                    }
                // Every exception from user cancellation callbacks is retained above. The finish
                // path awaits pending before disposal, including when CancelAsync starts late.
                cancelAndObserve |> ignore
            termination.TrySetResult(reason) |> ignore

    let abortCore () = requestImmediateStop AgentStopReason.Aborted
    let failCore error = requestImmediateStop (AgentStopReason.Faulted error)

    let attempt (operation: unit -> unit) =
        try
            operation ()
        with error ->
            failCore error

    let attemptAsync (operation: unit -> Task) = task {
        let mutable work: Task = null
        try
            work <- operation ()
            do! work
        with error ->
            if not (isNull work) && work.IsFaulted then
                for failure in work.Exception.InnerExceptions do
                    failCore failure
            else
                failCore error
    }

    let tryWriteReliable (envelope: MailboxEnvelope<'Message>) =
        if not (isAcceptingMessages ()) then
            AgentTryDeliveryResult.Closed
        else
            Interlocked.Increment(&queueLength) |> ignore
            if channel.Writer.TryWrite envelope then
                AgentTryDeliveryResult.Posted
            else
                Interlocked.Decrement(&queueLength) |> ignore
                if isAcceptingMessages () then AgentTryDeliveryResult.Full else AgentTryDeliveryResult.Closed

    let tryWriteReserved limit (envelope: MailboxEnvelope<'Message>) =
        if not (isAcceptingMessages ()) then
            AgentTryDeliveryResult.Closed
        elif envelope.IsOrdinary && ordinaryQueued >= limit then
            AgentTryDeliveryResult.Full
        else
            if envelope.IsOrdinary then ordinaryQueued <- ordinaryQueued + 1
            let result = tryWriteReliable envelope
            if result <> AgentTryDeliveryResult.Posted && envelope.IsOrdinary then
                ordinaryQueued <- ordinaryQueued - 1
            result

    let tryWriteReliableEnvelope envelope =
        match ordinaryLimit with
        | Some limit -> lock admissionGate (fun () -> tryWriteReserved limit envelope)
        | None -> tryWriteReliable envelope

    let tryWriteEnvelope (envelope: MailboxEnvelope<'Message>) =
        if checkedOptions.IsReliable then
            tryWriteReliableEnvelope envelope |> AdmissionTasks.generalTryDelivery
        elif not (isAcceptingMessages ()) then
            AgentPostResult.Closed
        else
            Interlocked.Increment(&queueLength) |> ignore
            if channel.Writer.TryWrite envelope then
                if mailboxDropsWrites && envelope.WasDropped then AgentPostResult.Dropped
                else AgentPostResult.Posted
            else
                Interlocked.Decrement(&queueLength) |> ignore
                AgentPostResult.Closed

    let postReliableEnvelopeAsync (envelope: MailboxEnvelope<'Message>) (token: CancellationToken) = task {
        let mutable result = AgentDeliveryResult.Closed
        let mutable waiting = true
        while waiting do
            if token.IsCancellationRequested then
                result <- AgentDeliveryResult.Canceled
                waiting <- false
            else
                let mutable changed: Task = null
                let admission =
                    match ordinaryLimit with
                    | Some limit ->
                        lock admissionGate (fun () ->
                            let admission = tryWriteReserved limit envelope
                            if admission = AgentTryDeliveryResult.Full then
                                if isNull admissionChanged then
                                    admissionChanged <- TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                                changed <- admissionChanged.Task
                            admission)
                    | None -> tryWriteReliable envelope
                match admission with
                | AgentTryDeliveryResult.Posted ->
                    result <- AgentDeliveryResult.Posted
                    waiting <- false
                | AgentTryDeliveryResult.Closed ->
                    result <- AgentDeliveryResult.Closed
                    waiting <- false
                | AgentTryDeliveryResult.Full ->
                    try
                        let! canWrite = task {
                            if not (isNull changed) then
                                do! changed.WaitAsync token
                                return true
                            else
                                return! channel.Writer.WaitToWriteAsync(token)
                        }
                        if not canWrite then
                            result <- AgentDeliveryResult.Closed
                            waiting <- false
                    with :? OperationCanceledException ->
                        result <- AgentDeliveryResult.Canceled
                        waiting <- false
        return result
    }

    let postEnvelopeAsync envelope token = task {
        if checkedOptions.IsReliable then
            let! result = postReliableEnvelopeAsync envelope token
            return AdmissionTasks.generalDelivery result
        elif token.IsCancellationRequested then return AgentPostResult.Canceled
        else return tryWriteEnvelope envelope
    }

    let tryPostCore message =
        tryWriteEnvelope (MailboxEnvelope(message, None, None, None) |> classify)

    let tryReliablePostCore message =
        tryWriteReliableEnvelope (MailboxEnvelope(message, None, None, None) |> classify)

    let postReliableAsyncCore message (token: CancellationToken) =
        let envelope = MailboxEnvelope(message, None, None, None) |> classify
        if token.IsCancellationRequested then AdmissionTasks.delivery AgentDeliveryResult.Canceled
        else
            match tryWriteReliableEnvelope envelope with
            | AgentTryDeliveryResult.Posted -> AdmissionTasks.delivery AgentDeliveryResult.Posted
            | AgentTryDeliveryResult.Closed -> AdmissionTasks.delivery AgentDeliveryResult.Closed
            | AgentTryDeliveryResult.Full -> postReliableEnvelopeAsync envelope token

    let postAsyncCore message (token: CancellationToken) =
        let envelope = MailboxEnvelope(message, None, None, None) |> classify
        if token.IsCancellationRequested then AdmissionTasks.post AgentPostResult.Canceled
        else
            match tryWriteEnvelope envelope with
            | AgentPostResult.Full -> postEnvelopeAsync envelope token
            | (AgentPostResult.Posted | AgentPostResult.Closed | AgentPostResult.Canceled | AgentPostResult.Dropped) as result -> AdmissionTasks.post result

    // Only the handler adds work; finish reads after dispatch has stopped.
    let background = ResizeArray<Task>()

    let startBackground operation =
        background.RemoveAll(fun work -> work.IsCompleted) |> ignore

        let runWork () : Task = task {
            try
                do! operation lifetimeToken
            with
            | :? OperationCanceledException when lifetimeToken.IsCancellationRequested -> ()
            | error -> failCore error
        }

        background.Add(Task.Run(Func<Task>(runWork)))

    let reliableAddress =
        if checkedOptions.IsReliable then Some (ReliableAgentRef<'Message>(tryReliablePostCore, postReliableAsyncCore))
        else None

    let context =
        AgentContext<'Message>(
            options.Name, lifetimeToken, dispatchStopped.Token, tryPostCore, postAsyncCore, completeCore, abortCore, failCore, startBackground, reliableAddress)

    let reliableContext = ReliableAgentContext<'Message>(context, ReliableAgentRef<'Message>(tryReliablePostCore, postReliableAsyncCore))

    let signalStarted () =
        attempt (fun () -> startedEvent.Trigger(options.Name))
        options.OnStarted |> Option.iter (fun callback -> attempt (fun () -> callback options.Name))

    let reportHandlerError error =
        let reportFailure callbackError =
            failCore error
            failCore callbackError
        try errorEvent.Trigger(options.Name, error)
        with callbackError -> reportFailure callbackError

        let decision =
            match options.OnError with
            | Some decide ->
                try decide (options.Name, error)
                with callbackError ->
                    reportFailure callbackError
                    AgentErrorAction.Stop
            | None -> AgentErrorAction.Stop
        if isImmediateStopRequested () then AgentErrorAction.Stop else decision

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
                    releaseAdmission envelope
                    Some envelope
                | false, _ -> None)

    let finish () = task {
        let reason =
            lock lifecycleGate (fun () ->
                finishing <- true
                Interlocked.Exchange(&accepting, 0) |> ignore
                let reason = defaultArg stopReason AgentStopReason.Completed
                stopReason <- Some reason
                reason)

        channel.Writer.TryComplete() |> ignore
        notifyAdmission ()
        // Independently settle every queued request and release its reservation.
        let mutable draining = true
        while draining do
            match channel.Reader.TryRead() with
            | true, envelope ->
                Interlocked.Decrement(&queueLength) |> ignore
                attempt (fun () -> releaseAdmission envelope)
                attempt (fun () -> envelope.Discard reason)
            | false, _ -> draining <- false
        termination.TrySetResult(reason) |> ignore

        // A failing detach/cancellation callback cannot bypass child joins.
        do! attemptAsync (fun () -> dispatchStopped.CancelAsync())
        do! attemptAsync (fun () -> Task.WhenAll(background))

        // Seal only after all launched work has returned through the fault sink.
        let reason, callbacks =
            lock lifecycleGate (fun () ->
                stopSealed <- true
                defaultArg stopReason reason, cancellationTask)
        do! attemptAsync (fun () -> callbacks)
        attempt (fun () -> dispatchStopped.Dispose())
        attempt (fun () -> lock lifecycleGate (fun () -> lifetime.Dispose()))

        // Every notification sees this same sealed reason. Later notification
        // faults affect Completion, without rewriting a reason already delivered.
        attempt (fun () -> stoppedEvent.Trigger(options.Name, reason))
        options.OnStopped |> Option.iter (fun callback -> attempt (fun () -> callback (options.Name, reason)))

        let errors = lock lifecycleGate (fun () -> failures.ToArray())
        if errors.Length > 0 then
            completion.TrySetException(errors) |> ignore
        else
            match reason with
            | AgentStopReason.Completed -> completion.TrySetResult() |> ignore
            | AgentStopReason.Aborted -> completion.TrySetCanceled(lifetimeToken) |> ignore
            | AgentStopReason.Faulted error -> completion.TrySetException(error) |> ignore
    }

    let runLoop () =
        task {
            try
                signalStarted ()
                let mutable running = true
                while running && not (isImmediateStopRequested ()) do
                    let! canRead = channel.Reader.WaitToReadAsync(lifetimeToken)
                    if not canRead then
                        running <- false
                    else
                        let mutable draining = true
                        while draining && not (isImmediateStopRequested ()) do
                            match tryTakeNext () with
                            | None -> draining <- false
                            | Some envelope ->
                                try
                                    do! handler context reliableContext envelope.Message
                                with
                                | :? OperationCanceledException as error when isImmediateStopRequested () && lifetimeToken.IsCancellationRequested ->
                                    // A lifetime fault can cancel the current handler just as
                                    // Abort can. Settle with that chosen cause, not its OCE.
                                    match getStopReason () with
                                    | Some reason -> envelope.Discard reason
                                    | None ->
                                        envelope.Fault error
                                        failCore error
                                | error ->
                                    envelope.Fault error

                                    match reportHandlerError error with
                                    | AgentErrorAction.Continue -> ()
                                    | AgentErrorAction.Stop ->
                                        requestImmediateStop (AgentStopReason.Faulted error)
            with
            | :? OperationCanceledException when isImmediateStopRequested () && lifetimeToken.IsCancellationRequested -> ()
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
                match invalidTimeout effectiveTimeout with
                | Some value -> Some (AgentRequestError.InvalidTimeout value)
                | None when isNull (box buildMessage) -> Some (AgentRequestError.NullArgument "buildMessage")
                | None -> None

            match validationError with
            | Some error -> return AgentAskResult.InvalidRequest error
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
                        |> classify
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

    member internal _.ReliableAddress = reliableContext.Ref

    /// <summary>
    /// Completes after dispatch, queue cleanup, cancellation callbacks, and stopped callbacks finish.
    /// Retains original and secondary lifecycle failures. With no such failures,
    /// succeeds for Completed or is canceled for Aborted. StopReason is sealed before notifications.
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
    /// The first terminal result wins. Invalid arguments return InvalidRequest before construction.
    /// Timeout or cancellation does not retract an admitted command or undo its effects.
    /// BuildMessage must only construct the message; it runs synchronously on the caller.
    /// </summary>
    member _.TryAskAsync<'Reply>(buildMessage: ReplyChannel<'Reply> -> 'Message, ?timeout: TimeSpan, ?cancellationToken: CancellationToken) =
        tryAskCore buildMessage timeout (defaultArg cancellationToken CancellationToken.None)

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
    /// <param name="isControl">Required for BoundedWithControl. Runs at the posting boundary;
    /// it must classify immutable messages without reading or mutating agent-owned state.</param>
    static member internal StartChecked(checkedOptions: CheckedAgentOptions<'Message>, handler: AgentContext<'Message> -> 'Message -> Task<unit>) =
        new Agent<'Message>(checkedOptions, fun context _ message -> handler context message)

    static member TryPrepare(options: AgentOptions, handler: AgentContext<'Message> -> 'Message -> Task<unit>,
                             ?isControl: 'Message -> bool) =
        checkOptions options handler isControl
        |> Result.map (fun checkedOptions -> AgentPlan<'Message>(fun () -> new Agent<'Message>(checkedOptions, fun context _ message -> handler context message)))

    /// Preflight actual derived configuration before starting dependent workers.
    /// The resulting token accepts only trusted local handlers; raw handlers use TryStartReliable.
    static member TryCheckReliable(options: AgentOptions, ?isControl: 'Message -> bool) =
        checkOptions options (fun () -> ()) isControl
        |> Result.bind (fun checkedOptions ->
            if not checkedOptions.IsReliable then Error AgentStartError.DroppingMailbox
            else Ok (ReliableAgentConfiguration<'Message> checkedOptions))

    static member internal StartReliableChecked(checkedOptions: CheckedAgentOptions<'Message>, handler: ReliableAgentContext<'Message> -> 'Message -> Task<unit>) =
        let agent = new Agent<'Message>(checkedOptions, fun _ context message -> handler context message)
        new ReliableAgent<'Message>(agent, agent.ReliableAddress)

    static member TryPrepareReliable(options: AgentOptions, handler: ReliableAgentContext<'Message> -> 'Message -> Task<unit>,
                                     ?isControl: 'Message -> bool) =
        if isNull (box handler) then Error (AgentStartError.NullArgument "handler")
        else
            Agent<'Message>.TryCheckReliable(options, ?isControl = isControl)
            |> Result.map (fun configuration -> ReliableAgentPlan<'Message>(fun () -> configuration.Start handler))

    static member TryStartReliable(options: AgentOptions, handler: ReliableAgentContext<'Message> -> 'Message -> Task<unit>,
                                   ?isControl: 'Message -> bool) =
        Agent<'Message>.TryPrepareReliable(options, handler, ?isControl = isControl)
        |> Result.map (fun plan -> plan.Start())

    static member TryStart(options: AgentOptions, handler: AgentContext<'Message> -> 'Message -> Task<unit>,
                           ?isControl: 'Message -> bool) =
        Agent<'Message>.TryPrepare(options, handler, ?isControl = isControl)
        |> Result.map (fun plan -> plan.Start())

/// Fully checked construction. Start creates one owner without further raw-input validation.
and [<Sealed>] AgentPlan<'Message> internal (start: unit -> Agent<'Message>) =
    member _.Start() = start ()

/// A non-dropping facade over one Agent owner; it adds no queue or lifecycle state.
and [<Sealed>] ReliableAgent<'Message> internal (agent: Agent<'Message>, address: ReliableAgentRef<'Message>) =
    member _.Ref = address
    member _.GeneralRef = agent.Ref
    member _.Owner = agent
    member _.Name = agent.Name
    member _.Completion = agent.Completion
    member _.QueueLength = agent.QueueLength
    member _.DroppedCount = agent.DroppedCount
    member _.IsAcceptingMessages = agent.IsAcceptingMessages
    member _.StopReason = agent.StopReason
    member _.Started = agent.Started
    member _.Errored = agent.Errored
    member _.Stopped = agent.Stopped
    member _.TryPost(message) = agent.TryPost message
    member _.PostAsync(message, ?cancellationToken: CancellationToken) = agent.PostAsync(message, ?cancellationToken = cancellationToken)
    member _.TryAskAsync<'Reply>(buildMessage: ReplyChannel<'Reply> -> 'Message, ?timeout: TimeSpan, ?cancellationToken: CancellationToken) =
        agent.TryAskAsync(buildMessage, ?timeout = timeout, ?cancellationToken = cancellationToken)
    member _.Complete() = agent.Complete()
    member _.Abort() = agent.Abort()
    member internal _.Agent = agent
    interface IDisposable with
        member _.Dispose() = (agent :> IDisposable).Dispose()
    interface IAsyncDisposable with
        member _.DisposeAsync() = (agent :> IAsyncDisposable).DisposeAsync()

and [<Sealed>] ReliableAgentPlan<'Message> internal (start: unit -> ReliableAgent<'Message>) =
    member _.Start() = start ()

/// Checked reusable configuration for resource-ordering preflight. Start is a trusted
/// construction path for local handlers already known nonnull, not a raw input boundary.
and [<Sealed>] ReliableAgentConfiguration<'Message> internal (checkedOptions: CheckedAgentOptions<'Message>) =
    member _.Start(handler: ReliableAgentContext<'Message> -> 'Message -> Task<unit>) =
        Agent<'Message>.StartReliableChecked(checkedOptions, handler)

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
                reply.ReplyFault error

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
            commandHandler: StatefulAgentContext -> 'State -> 'Command -> Task<StatefulTransition<'State>>,
            checkedOptions: CheckedAgentOptions<StatefulEnvelope<'State, 'Command>>
        ) =

    let mutable state = initialState

    let applyState nextState =
        let previous = state
        state <- nextState
        options.OnTransition |> Option.iter (fun callback -> callback (previous, nextState))

    let applyCommandState (context: AgentContext<StatefulEnvelope<'State, 'Command>>) nextState =
        try applyState nextState
        with error -> context.Fail error

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
        Agent.StartChecked(
            { checkedOptions with Options = { checkedOptions.Options with OnError = Some onError } },
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
                        | StatefulTransition.SetState nextState ->
                            applyCommandState agentContext nextState
                        | StatefulTransition.Stop -> agentContext.Complete() |> ignore
                        | StatefulTransition.StopWithState nextState ->
                            applyCommandState agentContext nextState
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
        if isNull (box buildMessage) then
            Task.FromResult (AgentAskResult.InvalidRequest (AgentRequestError.NullArgument "buildMessage"))
        else
            inner.TryAskAsync(
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
        if isNull (box projection) then
            Task.FromResult (AgentAskResult.InvalidRequest (AgentRequestError.NullArgument "projection"))
        else
            inner.TryAskAsync(
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
    static member TryStart(options: StatefulAgentOptions<'State>, initialState: 'State, commandHandler: StatefulAgentContext -> 'State -> 'Command -> Task<StatefulTransition<'State>>) =
        if isNull (box options) then Error (AgentStartError.NullArgument "options")
        elif options.OnUnhandled |> Option.exists (box >> isNull) then Error (AgentStartError.NullArgument "OnUnhandled")
        elif options.OnTransition |> Option.exists (box >> isNull) then Error (AgentStartError.NullArgument "OnTransition")
        else
            checkOptions options.AgentOptions commandHandler None
            |> Result.map (fun checkedOptions ->
                new StatefulAgent<'State, 'Command>({ options with AgentOptions = checkedOptions.Options }, initialState, commandHandler, checkedOptions))

/// <summary>
/// Helper module providing functional pipelines for base Agent operations.
/// </summary>
[<RequireQualifiedAccess>]
module Agent =
    /// <summary>
    /// Creates and starts an agent.
    /// </summary>
    let tryStart options handler =
        Agent.TryStart(options, handler)

    let tryStartReliable options handler =
        Agent.TryStartReliable(options, handler)

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
    let tryStart options initialState commandHandler =
        StatefulAgent.TryStart(options, initialState, commandHandler)

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

    /// <summary>Sends a command/request and returns a result union.</summary>
    let tryAskAsync buildMessage (agent: StatefulAgent<_, _>) =
        agent.TryAskAsync(buildMessage)

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
            commandHandler: StatefulAgentContext -> 'State -> 'Command -> Task<MutableStatefulTransition>,
            checkedOptions: CheckedAgentOptions<StatefulEnvelope<'State, 'Command>>
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
        Agent.StartChecked(
            { checkedOptions with Options = { checkedOptions.Options with OnError = Some onError } },
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
        if isNull (box buildMessage) then
            Task.FromResult (AgentAskResult.InvalidRequest (AgentRequestError.NullArgument "buildMessage"))
        else
            inner.TryAskAsync(
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
        if isNull (box projection) then
            Task.FromResult (AgentAskResult.InvalidRequest (AgentRequestError.NullArgument "projection"))
        else
            inner.TryAskAsync(
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
    static member TryStart
        (
            options: MutableStatefulAgentOptions<'State>,
            initialState: 'State,
            commandHandler: StatefulAgentContext -> 'State -> 'Command -> Task<MutableStatefulTransition>
        ) =
        if isNull (box options) then Error (AgentStartError.NullArgument "options")
        elif options.OnUnhandled |> Option.exists (box >> isNull) then Error (AgentStartError.NullArgument "OnUnhandled")
        else
            checkOptions options.AgentOptions commandHandler None
            |> Result.map (fun checkedOptions ->
                new MutableStatefulAgent<'State, 'Command>({ options with AgentOptions = checkedOptions.Options }, initialState, commandHandler, checkedOptions))

/// <summary>
/// Helper module providing functional pipelines for MutableStatefulAgent operations.
/// </summary>
[<RequireQualifiedAccess>]
module MutableStatefulAgent =
    /// <summary>
    /// Creates and starts a mutable stateful agent.
    /// </summary>
    let tryStart options initialState commandHandler =
        MutableStatefulAgent.TryStart(options, initialState, commandHandler)

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

    /// <summary>Sends a command/request and returns a result union.</summary>
    let tryAskAsync buildMessage (agent: MutableStatefulAgent<_, _>) =
        agent.TryAskAsync(buildMessage)

    /// <summary>
    /// Reads a projection of the current mutable state and returns a result union.
    /// </summary>
    let tryReadAsync projection (agent: MutableStatefulAgent<_, _>) =
        agent.TryReadAsync(projection)
