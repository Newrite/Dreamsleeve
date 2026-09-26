namespace Dreamsleeve.Agent

open System.Collections.Generic
open System.Threading

/// Bounded, ordered admission to one destination without suspending the owner's handler.
/// All members belong to the owning agent's handler; this object has no reader or locks.
[<Sealed>]
type AgentOutbox<'Message>(capacity: int, destination: ReliableAgentRef<'Message>) =
    let pending = Queue<'Message>()
    let mutable sending = false

    do
        if capacity < 1 then
            invalidArg (nameof capacity) "Outbox capacity must be positive."

    let count () = pending.Count + if sending then 1 else 0

    let deliver message (token: CancellationToken) =
        destination.PostAsync(message, cancellationToken = token)

    /// Includes the message awaiting admission and any queued messages.
    member _.Count = count ()

    member _.IsEmpty = count () = 0

    member _.TryEnqueue(message) =
        if count () >= capacity then
            false
        else
            pending.Enqueue message
            true

    /// Starts at most one admission; repeated calls while sending are harmless.
    /// The completion mapper runs outside the handler and must not touch owner state.
    member _.Pump<'OwnerMessage>
        (context: AgentContext<'OwnerMessage>, completed: Result<AgentDeliveryResult, exn> -> 'OwnerMessage) =
        if not sending && pending.Count > 0 then
            let message = pending.Dequeue()
            sending <- true
            context.PipeToSelf(deliver message, completed)

    /// Call once from this outbox's completion message, including failed admissions.
    /// Admission acknowledges mailbox entry; it does not acknowledge processing.
    member _.Acknowledge() =
        sending <- false
