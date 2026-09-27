namespace Dreamsleeve.Agent

open System
open System.Threading
open System.Threading.Tasks

/// Monotonic timestamps from the ticker's TimeProvider.
[<Struct>]
type AgentTick = { DueTimestamp: int64; QueuedTimestamp: int64 }

/// Acknowledge only after handling a tick. Until then further periods are coalesced.
[<Sealed>]
type AgentTicker internal (acknowledge: unit -> unit) =
    member _.Acknowledge() = acknowledge ()

[<RequireQualifiedAccess>]
module AgentTicker =
    /// One tracked worker and at most one outstanding notification per owner.
    /// Complete detaches the timer; Abort cancels it. No owner state runs in the worker.
    let startWithTimeProvider (time: TimeProvider) interval (context: AgentContext<'Message>) toMessage =
        if interval <= TimeSpan.Zero then invalidArg (nameof interval) "Ticker interval must be positive."
        if context.Ref.TryReliable().IsNone then invalidArg (nameof context) "A ticker requires a non-dropping mailbox."

        let mutable pending = 0
        let acknowledge () = Volatile.Write(&pending, 0)
        let period = max 1L (int64 (interval.TotalSeconds * float time.TimestampFrequency))

        let run (token: CancellationToken) = task {
            use cancel = CancellationTokenSource.CreateLinkedTokenSource(token, context.DispatchStopped)
            let mutable due = time.GetTimestamp() + period
            use timer = new PeriodicTimer(interval, time)
            let mutable running = true

            try
                while running do
                    let! elapsed = timer.WaitForNextTickAsync(cancel.Token)
                    if not elapsed then
                        running <- false
                    else
                        let queued = time.GetTimestamp()
                        // Keep the original cadence and skip missed periods, never catch up in a burst.
                        let latestDue = due + max 0L ((queued - due) / period) * period
                        due <- latestDue + period

                        if Interlocked.CompareExchange(&pending, 1, 0) = 0 then
                            let tick = { DueTimestamp = latestDue; QueuedTimestamp = queued }
                            let! posted = context.PostAsync(toMessage tick, cancellationToken = cancel.Token)

                            match posted with
                            | AgentPostResult.Posted -> ()
                            | AgentPostResult.Closed | AgentPostResult.Canceled -> running <- false
                            | AgentPostResult.Full | AgentPostResult.Dropped ->
                                invalidOp "A ticker requires reliable admission."
            with :? OperationCanceledException when cancel.IsCancellationRequested -> ()
        }

        context.StartDelivery run
        AgentTicker acknowledge

    let start interval context toMessage =
        startWithTimeProvider TimeProvider.System interval context toMessage
