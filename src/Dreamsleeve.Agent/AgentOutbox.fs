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

    let slots = new SemaphoreSlim(capacity, capacity)
    let mutable tail: Task = Task.CompletedTask

    let deliver (previous: Task) (finished: TaskCompletionSource<unit>) (destination: ReliableAgentRef<'Reply>) reply
                (onFailure: AgentSendFailure -> Task<unit>) (token: CancellationToken) = task {
        try
            do! previous.WaitAsync token
            token.ThrowIfCancellationRequested()
            let! outcome = task {
                try
                    let! result = destination.PostAsync(reply, cancellationToken = token)
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
        finally
            finished.TrySetResult() |> ignore
            slots.Release() |> ignore
    }

    let launch (context: AgentContext<'Request>) destination createReply onFailure =
        try
            context.CancellationToken.ThrowIfCancellationRequested()
            let reply = createReply ()
            let finished = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let previous = if ordered then tail else Task.CompletedTask
            if ordered then tail <- finished.Task
            context.StartDelivery(deliver previous finished destination reply onFailure)
        with error ->
            slots.Release() |> ignore
            raise error

    member _.Count = capacity - slots.CurrentCount

    // Only the owning handler schedules sends; workers only release reservations.
    member _.TrySend(context: AgentContext<'Request>, destination, reply, onFailure) =
        if slots.Wait(0) then
            launch context destination (fun () -> reply) onFailure
            true
        else
            false

    member _.Send(context: AgentContext<'Request>, destination, createReply, onFailure) = task {
        do! slots.WaitAsync context.CancellationToken
        launch context destination createReply onFailure
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
    /// Independent bounded reply admissions. Construct once per owner.
    /// Replies may arrive out of order; correlate them by the request's operation ID.
    /// A closed caller does not undo an applied command or stop other callers.
    let createHandler capacity (replyTo: 'Request -> ReliableAgentRef<'Reply>) (execute: 'Request -> 'Reply) =
        let window = AgentDeliveryWindow(capacity, false)
        let deliveryFailure failure = task {
            match failure with
            | AgentSendFailure.Faulted error -> return raise error
            | AgentSendFailure.Closed | AgentSendFailure.Canceled -> ()
        }
        let handle (context: AgentContext<'Request>) request =
            window.Send(context, replyTo request, (fun () -> execute request), deliveryFailure)
        handle
