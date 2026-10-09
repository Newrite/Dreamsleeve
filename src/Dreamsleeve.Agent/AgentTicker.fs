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

/// A period supported by PeriodicTimer, checked before an owner starts.
[<Sealed>]
type AgentTickerInterval private (value: TimeSpan) =
    member internal _.Value = value
    static member TryCreate(value: TimeSpan) =
        let milliseconds = value.Ticks / TimeSpan.TicksPerMillisecond
        if milliseconds < 1L || milliseconds > 4294967294L then Error (AgentStartError.InvalidInterval value)
        else Ok (AgentTickerInterval value)

[<RequireQualifiedAccess>]
module AgentTicker =
    /// One tracked worker and at most one outstanding notification per owner.
    /// Complete detaches the timer; Abort cancels it. No owner state runs in the worker.
    let startWithTimeProvider (time: TimeProvider) (period: AgentTickerInterval) (context: ReliableAgentContext<'Message>) toMessage =
        let interval = period.Value
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
                            | AgentDeliveryResult.Posted -> ()
                            | AgentDeliveryResult.Closed | AgentDeliveryResult.Canceled -> running <- false
            with :? OperationCanceledException when cancel.IsCancellationRequested -> ()
        }

        context.StartDelivery run
        AgentTicker acknowledge

    let start interval context toMessage =
        startWithTimeProvider TimeProvider.System interval context toMessage


    let tryStartWithTimeProvider (time: TimeProvider) interval (context: ReliableAgentContext<'Message>) toMessage =
        if isNull time then Error (AgentStartError.NullArgument "time")
        elif isNull (box context) then Error (AgentStartError.NullArgument "context")
        elif isNull (box toMessage) then Error (AgentStartError.NullArgument "toMessage")
        else AgentTickerInterval.TryCreate interval |> Result.map (fun period -> startWithTimeProvider time period context toMessage)

    let tryStart interval context toMessage =
        tryStartWithTimeProvider TimeProvider.System interval context toMessage
