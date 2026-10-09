namespace Dreamsleeve.Agent

open System
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type AgentSendFailure =
    | Closed
    | Canceled
    | Faulted of exn

/// A positive outstanding-delivery budget, checked before creating owner resources.
[<Sealed>]
type AgentDeliveryCapacity private (value: int) =
    member internal _.Value = value
    static member TryCreate(value: int) =
        if value < 1 then Error (AgentStartError.InvalidCapacity("deliveryCapacity", value))
        else Ok (AgentDeliveryCapacity value)

/// Library boundary: capacity is released by delivery workers, never by the owner's
/// mailbox. Thus a saturated request handler can await capacity without a reply cycle.
type internal AgentDeliveryWindow(budget: AgentDeliveryCapacity, ordered: bool) =
    let capacity = budget.Value
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
        | Some (AgentSendFailure.Faulted _ as failure) -> do! onFailure failure
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

    let launch (context: ReliableAgentContext<'Request>) operation =
        let finished = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let previous = if ordered then tail else Task.CompletedTask
        if ordered then tail <- finished.Task

        try
            context.StartDelivery(deliver previous finished operation)
            true
        with error ->
            finished.TrySetResult() |> ignore
            slots.Release() |> ignore
            context.Fail error
            false

    // Reservation is already owned. Map/post once, including the transition to waiting.
    let sendReady (context: ReliableAgentContext<'Request>) destination reply onFailure =
        let token = context.CancellationToken
        if token.IsCancellationRequested then
            slots.Release() |> ignore
            false
        elif not ordered || tail.IsCompleted then
            let admission = admit destination reply token
            if admission.IsCompletedSuccessfully && admission.Result = AgentDeliveryResult.Posted then
                slots.Release() |> ignore
                true
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
    member _.TrySend(context: ReliableAgentContext<'Request>, destination, reply, onFailure) =
        if slots.Wait(0) then
            sendReady context destination reply onFailure
        else
            false

    member _.Send(context: ReliableAgentContext<'Request>, destination, createReply, onFailure) = task {
        do! slots.WaitAsync context.CancellationToken
        let reply =
            try
                // A released reservation can race Abort after WaitAsync succeeds.
                context.CancellationToken.ThrowIfCancellationRequested()
                Ok (createReply ())
            with error -> Error error

        match reply with
        | Error error ->
            slots.Release() |> ignore
            // Synchronous execution belongs to the handler's business error policy.
            return! Task.FromException<unit>(error)
        | Ok value -> sendReady context destination value onFailure |> ignore
    }

    member _.SendAsync(context: ReliableAgentContext<'Request>, destination, execute, onFailure) = task {
        do! slots.WaitAsync context.CancellationToken
        let run (token: CancellationToken) = task {
            token.ThrowIfCancellationRequested()
            let! reply = execute token
            do! observe (admit destination reply token) onFailure token
        }

        // I/O (including its synchronous prefix) still starts outside the owning handler.
        launch context run |> ignore
    }

    member _.AbortAfterDrain(context: ReliableAgentContext<'Request>) =
        let pending = tail
        let finish (token: CancellationToken) = task {
            do! pending.WaitAsync token
            context.Abort()
        }
        context.StartDelivery finish

module private DeliveryFailure =
    let stop (context: ReliableAgentContext<'Message>) failure = task {
        match failure with
        | AgentSendFailure.Faulted error -> context.Fail error
        | AgentSendFailure.Closed | AgentSendFailure.Canceled -> context.Abort()
    }

    let notify (context: ReliableAgentContext<'Message>) toMessage failure = task {
        let retain original =
            match failure with
            | AgentSendFailure.Faulted error -> context.Fail error
            | AgentSendFailure.Closed | AgentSendFailure.Canceled -> ()
            context.Fail original
        let message =
            try Ok (toMessage failure)
            with error -> Error error
        match message with
        | Error error -> retain error
        | Ok value ->
            let! admission = task {
                try
                    let! delivered = context.PostAsync(value, cancellationToken = context.CancellationToken)
                    return Ok delivered
                with error -> return Error error
            }
            match admission with
            | Ok AgentDeliveryResult.Posted -> ()
            // Complete may close admission while a delivery is still finishing.
            // Do not turn an undeliverable error into a successful Completion.
            | Ok AgentDeliveryResult.Closed | Ok AgentDeliveryResult.Canceled -> do! stop context failure
            | Error error -> retain error
    }

/// Bounded FIFO sends owned by one handler. Successful delivery is library-internal.
/// Complete joins accepted sends; Abort cancels them. No Pump or acknowledgments.
[<Sealed>]
type AgentOutbox<'Message> private (capacity: AgentDeliveryCapacity, destination: ReliableAgentRef<'Message>) =
    let window = AgentDeliveryWindow(capacity, true)

    /// Trusted construction from a checked budget and an address produced by a reliable owner.
    static member Create(capacity: AgentDeliveryCapacity, destination: ReliableAgentRef<'Message>) =
        AgentOutbox<'Message>(capacity, destination)

    static member TryCreate(capacity: int, destination: ReliableAgentRef<'Message>) =
        if isNull (box destination) then Error (AgentStartError.NullArgument "destination")
        else AgentDeliveryCapacity.TryCreate capacity |> Result.map (fun budget -> AgentOutbox<'Message>.Create(budget, destination))

    member _.Count = window.Count
    member _.IsEmpty = window.Count = 0

    /// False means the local limit is full or the owner stopped before scheduling.
    /// By default an unavailable destination aborts the owner; exceptions fault it.
    member _.TrySend(context: ReliableAgentContext<'Owner>, message) =
        window.TrySend(context, destination, message, DeliveryFailure.stop context)

    /// Only failures enter the owner mailbox. The mapper must not mutate owner state.
    member _.TrySend(context: ReliableAgentContext<'Owner>, message, onFailure: AgentSendFailure -> 'Owner) =
        window.TrySend(context, destination, message, DeliveryFailure.notify context onFailure)

    /// Terminal failure path: flush already accepted notifications, then abort.
    /// Call once from the handler and schedule no further sends through this outbox.
    member _.AbortAfterDrain(context: ReliableAgentContext<'Owner>) = window.AbortAfterDrain context

[<RequireQualifiedAccess>]
module AgentOutbox =
    /// Request/reply form of an ordered outbox. Construct once per owner.
    /// Completion drains all accepted replies; closing the sole output aborts the owner.
    let createHandler (capacity: AgentDeliveryCapacity) (output: ReliableAgentRef<'Reply>) (execute: 'Request -> 'Reply) =
        let window = AgentDeliveryWindow(capacity, true)
        let handle (context: ReliableAgentContext<'Request>) request =
            window.Send(context, output, (fun () -> execute request), DeliveryFailure.stop context)
        handle

    let tryCreateHandler capacity (output: ReliableAgentRef<'Reply>) (execute: 'Request -> 'Reply) =
        if isNull (box output) then Error (AgentStartError.NullArgument "output")
        elif isNull (box execute) then Error (AgentStartError.NullArgument "execute")
        else AgentDeliveryCapacity.TryCreate capacity |> Result.map (fun budget -> createHandler budget output execute)

[<RequireQualifiedAccess>]
module AgentReplyDispatcher =
    let private deliveryFailure (context: ReliableAgentContext<'Owner>) failure = task {
        match failure with
        | AgentSendFailure.Faulted error -> context.Fail error
        | AgentSendFailure.Closed | AgentSendFailure.Canceled -> ()
    }

    /// Independent bounded reply admissions. Construct once per owner.
    /// Replies may arrive out of order; correlate them by the request's operation ID.
    /// A closed caller does not undo an applied command or stop other callers.
    let createHandler (capacity: AgentDeliveryCapacity) (replyTo: 'Request -> ReliableAgentRef<'Reply>) (execute: 'Request -> 'Reply) =
        let window = AgentDeliveryWindow(capacity, false)
        let handle (context: ReliableAgentContext<'Owner>) (request: 'Request) =
            window.Send(context, replyTo request, (fun () -> execute request), deliveryFailure context)
        handle

    /// Reserves capacity for the whole operation and reply before starting tracked work.
    /// Execute runs outside the handler, including its synchronous prefix. Pass immutable
    /// requests and dependencies safe for concurrent use; never capture owner state.
    /// Complete joins work and reply admissions; Abort requests cooperative cancellation.
    let createAsyncHandler (capacity: AgentDeliveryCapacity) (replyTo: 'Request -> ReliableAgentRef<'Reply>)
                           (execute: CancellationToken -> 'Request -> Task<'Reply>) =
        let window = AgentDeliveryWindow(capacity, false)
        let handle (context: ReliableAgentContext<'Owner>) (request: 'Request) =
            window.SendAsync(context, replyTo request, (fun token -> execute token request), deliveryFailure context)
        handle


    let tryCreateHandler capacity (replyTo: 'Request -> ReliableAgentRef<'Reply>) (execute: 'Request -> 'Reply) =
        if isNull (box replyTo) then Error (AgentStartError.NullArgument "replyTo")
        elif isNull (box execute) then Error (AgentStartError.NullArgument "execute")
        else AgentDeliveryCapacity.TryCreate capacity |> Result.map (fun budget -> createHandler budget replyTo execute)

    let tryCreateAsyncHandler capacity (replyTo: 'Request -> ReliableAgentRef<'Reply>) (execute: CancellationToken -> 'Request -> Task<'Reply>) =
        if isNull (box replyTo) then Error (AgentStartError.NullArgument "replyTo")
        elif isNull (box execute) then Error (AgentStartError.NullArgument "execute")
        else AgentDeliveryCapacity.TryCreate capacity |> Result.map (fun budget -> createAsyncHandler budget replyTo execute)
