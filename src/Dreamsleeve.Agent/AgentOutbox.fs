namespace Dreamsleeve.Agent

open System
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type AgentSendFailure =
    | Closed
    | Canceled
    | Faulted of exn

/// Library boundary: capacity is released by delivery workers, never by the owner's
/// mailbox. Thus a saturated request handler can await capacity without a reply cycle.
type internal AgentDeliveryWindow(capacity: int, ordered: bool) =
    do
        if capacity < 1 then invalidArg (nameof capacity) "Delivery capacity must be positive."

    let requireReliableOwner (context: AgentContext<'Request>) =
        if not context.IsNonDropping then
            invalidOp "Tracked delivery requires a non-dropping owner mailbox."

    let slots = new SemaphoreSlim(capacity, capacity)
    let mutable tail: Task = Task.CompletedTask

    let observe (admission: Task<AgentDeliveryResult>) onFailure (token: CancellationToken) = task {
        let! outcome = task {
            try
                let! result = admission
                return
                    match result with
                    | AgentDeliveryResult.Posted -> None
                    | AgentDeliveryResult.Closed -> Some AgentSendFailure.Closed
                    | AgentDeliveryResult.Canceled -> Some AgentSendFailure.Canceled
            with error -> return Some (AgentSendFailure.Faulted error)
        }

        match outcome with
        | Some failure when not token.IsCancellationRequested -> do! onFailure failure
        | Some _ | None -> ()
    }

    let admit (destination: ReliableAgentRef<'Reply>) reply token =
        try destination.PostAsync(reply, cancellationToken = token)
        with error -> Task.FromException<AgentDeliveryResult>(error)

    let deliver (previous: Task) (finished: TaskCompletionSource<unit>) operation (token: CancellationToken) = task {
        try
            do! previous.WaitAsync token
            do! operation token
        finally
            finished.TrySetResult() |> ignore
            slots.Release() |> ignore
    }

    let launch (context: AgentContext<'Request>) operation =
        let finished = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let previous = if ordered then tail else Task.CompletedTask
        if ordered then tail <- finished.Task

        try
            context.StartDelivery(deliver previous finished operation)
        with error ->
            finished.TrySetResult() |> ignore
            slots.Release() |> ignore
            raise error

    // Reservation is already owned. Map/post once, including the transition to waiting.
    let sendReady (context: AgentContext<'Request>) destination reply onFailure =
        let token = context.CancellationToken
        if token.IsCancellationRequested then
            slots.Release() |> ignore
            token.ThrowIfCancellationRequested()

        if not ordered || tail.IsCompleted then
            let admission = admit destination reply token
            if admission.IsCompletedSuccessfully && admission.Result = AgentDeliveryResult.Posted then
                slots.Release() |> ignore
            else
                // Even if Abort races us, observe the operation that already started.
                launch context (observe admission onFailure)
        else
            let send (token: CancellationToken) = task {
                token.ThrowIfCancellationRequested()
                do! observe (admit destination reply token) onFailure token
            }
            launch context send

    member _.Count = capacity - slots.CurrentCount

    // Only the owning handler schedules sends; workers only release reservations.
    member _.TrySend(context: AgentContext<'Request>, destination, reply, onFailure) =
        requireReliableOwner context
        if slots.Wait(0) then
            sendReady context destination reply onFailure
            true
        else
            false

    member _.Send(context: AgentContext<'Request>, destination, createReply, onFailure) = task {
        requireReliableOwner context
        do! slots.WaitAsync context.CancellationToken
        let reply =
            try
                // A released reservation can race Abort after WaitAsync succeeds.
                context.CancellationToken.ThrowIfCancellationRequested()
                createReply ()
            with error ->
                slots.Release() |> ignore
                raise error

        sendReady context destination reply onFailure
    }

    member _.SendAsync(context: AgentContext<'Request>, destination, execute, onFailure) = task {
        requireReliableOwner context
        do! slots.WaitAsync context.CancellationToken
        let run (token: CancellationToken) = task {
            token.ThrowIfCancellationRequested()
            let! reply = execute token
            do! observe (admit destination reply token) onFailure token
        }

        // I/O (including its synchronous prefix) still starts outside the owning handler.
        launch context run
    }

    member _.AbortAfterDrain(context: AgentContext<'Request>) =
        let pending = tail
        let finish (token: CancellationToken) = task {
            do! pending.WaitAsync token
            context.Abort()
        }
        context.StartDelivery finish

module private DeliveryFailure =
    let stop (context: AgentContext<'Message>) failure = task {
        match failure with
        | AgentSendFailure.Faulted error -> return raise error
        | AgentSendFailure.Closed | AgentSendFailure.Canceled -> context.Abort()
    }

    let notify (context: AgentContext<'Message>) toMessage failure = task {
        let! admission = context.PostAsync(toMessage failure, cancellationToken = context.CancellationToken)
        match admission with
        | AgentPostResult.Posted -> ()
        // Complete may close admission while a delivery is still finishing.
        // Do not turn an undeliverable error into a successful Completion.
        | AgentPostResult.Closed | AgentPostResult.Canceled -> do! stop context failure
        | AgentPostResult.Full | AgentPostResult.Dropped ->
            invalidOp "Delivery failures require a non-dropping owner mailbox."
    }

/// Bounded FIFO sends owned by one handler. Successful delivery is library-internal.
/// Complete joins accepted sends; Abort cancels them. No Pump or acknowledgments.
[<Sealed>]
type AgentOutbox<'Message>(capacity: int, destination: ReliableAgentRef<'Message>) =
    let window = AgentDeliveryWindow(capacity, true)

    member _.Count = window.Count
    member _.IsEmpty = window.Count = 0

    /// False means the local limit is full and the message was not scheduled.
    /// By default an unavailable destination aborts the owner; exceptions fault it.
    member _.TrySend(context: AgentContext<'Owner>, message) =
        window.TrySend(context, destination, message, DeliveryFailure.stop context)

    /// Only failures enter the owner mailbox. The mapper must not mutate owner state.
    member _.TrySend(context: AgentContext<'Owner>, message, onFailure: AgentSendFailure -> 'Owner) =
        window.TrySend(context, destination, message, DeliveryFailure.notify context onFailure)

    /// Terminal failure path: flush already accepted notifications, then abort.
    /// Call once from the handler and schedule no further sends through this outbox.
    member _.AbortAfterDrain(context: AgentContext<'Owner>) = window.AbortAfterDrain context

[<RequireQualifiedAccess>]
module AgentOutbox =
    /// Request/reply form of an ordered outbox. Construct once per owner.
    /// Completion drains all accepted replies; closing the sole output aborts the owner.
    let createHandler capacity (output: ReliableAgentRef<'Reply>) (execute: 'Request -> 'Reply) =
        let window = AgentDeliveryWindow(capacity, true)
        let handle (context: AgentContext<'Request>) request =
            window.Send(context, output, (fun () -> execute request), DeliveryFailure.stop context)
        handle

[<RequireQualifiedAccess>]
module AgentReplyDispatcher =
    let private deliveryFailure failure = task {
        match failure with
        | AgentSendFailure.Faulted error -> return raise error
        | AgentSendFailure.Closed | AgentSendFailure.Canceled -> ()
    }

    /// Independent bounded reply admissions. Construct once per owner.
    /// Replies may arrive out of order; correlate them by the request's operation ID.
    /// A closed caller does not undo an applied command or stop other callers.
    let createHandler capacity (replyTo: 'Request -> ReliableAgentRef<'Reply>) (execute: 'Request -> 'Reply) =
        let window = AgentDeliveryWindow(capacity, false)
        let handle (context: AgentContext<'Owner>) (request: 'Request) =
            window.Send(context, replyTo request, (fun () -> execute request), deliveryFailure)
        handle

    /// Reserves capacity for the whole operation and reply before starting tracked work.
    /// Execute runs outside the handler, including its synchronous prefix. Pass immutable
    /// requests and dependencies safe for concurrent use; never capture owner state.
    /// Complete joins work and reply admissions; Abort requests cooperative cancellation.
    let createAsyncHandler capacity (replyTo: 'Request -> ReliableAgentRef<'Reply>)
                           (execute: CancellationToken -> 'Request -> Task<'Reply>) =
        let window = AgentDeliveryWindow(capacity, false)
        let handle (context: AgentContext<'Owner>) (request: 'Request) =
            window.SendAsync(context, replyTo request, (fun token -> execute token request), deliveryFailure)
        handle
