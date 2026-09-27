namespace Dreamsleeve.Agent

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

[<AutoOpen>]
module AgentLifetimeExtensions =
    type AgentContext<'Message> with
        /// Own a child from this handler: parent cancellation aborts it, and parent
        /// Completion joins its actual cleanup. Graceful child Stop remains domain-specific.
        member context.Own(child: Agent<'Child>, stopped: Result<unit, exn> -> 'Message) =
            let observe (token: CancellationToken) = task {
                use registration = token.Register(fun () -> child.Abort())
                do! child.Completion
            }
            context.PipeToSelf(observe, stopped)

        /// Observe a shared dependency without controlling its lifetime. Detaches when
        /// the owner stops dispatching, including Complete; it never stops the target.
        member context.Watch(completion: Task, stopped: Result<unit, exn> -> 'Message) =
            let observe (token: CancellationToken) = task {
                use cancel = CancellationTokenSource.CreateLinkedTokenSource(token, context.DispatchStopped)
                let! outcome = task {
                    try
                        do! completion.WaitAsync cancel.Token
                        return Ok ()
                    with error -> return Error error
                }
                if not cancel.IsCancellationRequested then
                    let! delivered = context.PostAsync(stopped outcome, cancellationToken = cancel.Token)
                    match delivered with
                    | AgentPostResult.Posted | AgentPostResult.Closed | AgentPostResult.Canceled -> ()
                    | AgentPostResult.Full | AgentPostResult.Dropped ->
                        invalidOp "Lifecycle observation requires a non-dropping owner mailbox."
            }
            context.StartDelivery observe

        member context.Watch(target: Agent<'Target>, stopped: Result<unit, exn> -> 'Message) =
            context.Watch(target.Completion, stopped)

/// Bounded ownership of forwarded reply channels. A library lock coordinates only
/// reply settlement with target/owner termination; application state stays in handlers.
[<Sealed>]
type AgentReplyScope<'Reply> internal (capacity: int, closedReply: 'Reply, busyReply: 'Reply) =
    let gate = obj()
    let pending = HashSet<ReplyChannel<'Reply>>()
    let mutable closed = false

    do
        if capacity < 1 then invalidArg (nameof capacity) "Pending reply capacity must be positive."

    member _.Close() =
        lock gate (fun () ->
            closed <- true
            for reply in pending do
                reply.Reply closedReply
            pending.Clear())

    /// Call from the owner's handler. send must synchronously schedule delivery and
    /// return false if refused. It runs outside the library lock and may close the scope.
    /// Closing settles waits; it does not retract a command already scheduled.
    member _.Forward(reply: ReplyChannel<'Reply>, send: ReplyChannel<'Reply> -> bool) =
        let accepted = lock gate (fun () ->
            pending.RemoveWhere(fun item -> item.IsCompleted) |> ignore
            if reply.IsCompleted || pending.Contains reply then
                false
            elif closed then
                reply.Reply closedReply
                false
            elif pending.Count >= capacity then
                reply.Reply busyReply
                false
            else
                pending.Add reply)

        if accepted then
            try
                if not (send reply) then
                    reply.Reply closedReply
                    lock gate (fun () -> pending.Remove reply |> ignore)
            with error ->
                reply.ReplyError error
                lock gate (fun () -> pending.Remove reply |> ignore)
                reraise ()

[<RequireQualifiedAccess>]
module AgentReplyScope =
    /// Attach to one route. Either endpoint stopping closes the scope, even if the
    /// owner cannot process a termination message. Target faults do not fault the owner.
    let create (context: AgentContext<'Owner>) (target: Agent<'Target>) capacity closedReply busyReply =
        let scope = AgentReplyScope(capacity, closedReply, busyReply)
        let observe (token: CancellationToken) = task {
            use cancel = CancellationTokenSource.CreateLinkedTokenSource(token, context.DispatchStopped)
            try
                try do! target.Completion.WaitAsync cancel.Token
                with _ -> ()
            finally
                scope.Close()
        }
        context.StartDelivery observe
        scope
