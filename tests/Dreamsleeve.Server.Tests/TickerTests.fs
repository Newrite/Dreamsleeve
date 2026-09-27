module Dreamsleeve.Server.Tests.TickerTests

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Dreamsleeve.Agent
open Expecto
open AgentTests
open BackgroundTests

// A manual periodic callback: each pulse represents one or more elapsed periods.
// No wall-clock sleeps are needed to test mailbox coalescing or timer lifetime.
type private Clock() =
    inherit TimeProvider()
    let ready = gate<unit>()
    let mutable timestamp = 0L
    let mutable reads = 0
    let mutable disposed = 0
    let mutable callback: TimerCallback = null
    let mutable callbackState: obj = null

    member _.Ready = ready.Task
    member _.Reads = Volatile.Read &reads
    member _.Disposed = Volatile.Read(&disposed) <> 0
    member _.Advance(milliseconds) =
        Interlocked.Add(&timestamp, milliseconds) |> ignore
        if Volatile.Read(&disposed) = 0 then callback.Invoke callbackState

    override _.TimestampFrequency = 1000L
    override _.GetTimestamp() =
        Interlocked.Increment &reads |> ignore
        Volatile.Read &timestamp

    override _.CreateTimer(action, state, _, _) =
        callback <- action
        callbackState <- state
        ready.TrySetResult() |> ignore
        { new ITimer with
            member _.Change(_, _) = Volatile.Read(&disposed) = 0
            member _.Dispose() = Volatile.Write(&disposed, 1)
            member _.DisposeAsync() =
                Volatile.Write(&disposed, 1)
                ValueTask.CompletedTask }

type private Command = Start | Tick of AgentTick | Barrier of TaskCompletionSource<unit>

let private start (clock: Clock) (release: Task) (ticks: Channel<AgentTick>) =
    let mutable ticker: AgentTicker option = None
    let handle (context: AgentContext<Command>) command = task {
        match command with
        | Barrier ready -> ready.SetResult()
        | Start -> ticker <- Some (AgentTicker.startWithTimeProvider clock (TimeSpan.FromMilliseconds 100L) context Tick)
        | Tick value ->
            check (ticks.Writer.TryWrite value) "Tick output closed."
            do! release
            ticker.Value.Acknowledge()
    }
    let options = { AgentOptions.create "ticker-test" with Mailbox = AgentMailbox.boundedWait 4 }
    let agent = Agent.Start(options, handle)
    equal AgentPostResult.Posted (agent.TryPost Start)
    agent

let tests = testList "AgentTicker" [
    case "Coalesces while handler is blocked and retains cadence" (fun () -> task {
        let clock = Clock()
        let release = gate<unit>()
        let ticks = Channel.CreateUnbounded<AgentTick>()
        use agent = start clock release.Task ticks
        do! awaitResult clock.Ready
        clock.Advance 100L
        let! first = ticks.Reader.ReadAsync().AsTask() |> awaitResult
        equal 100L first.DueTimestamp

        let reads = clock.Reads
        clock.Advance 450L
        do! eventually (fun () -> clock.Reads > reads)
        equal 0 agent.QueueLength
        check (not (fst (ticks.Reader.TryPeek()))) "No second tick while the first is outstanding."

        release.SetResult()
        // A Read/Ask barrier is not needed: completing dispatch waits for the current handler.
        agent.Complete() |> ignore
        do! awaitUnit agent.Completion
        check clock.Disposed "Complete disposes a blocked timer worker."
    })
    case "Missed periods are skipped without depending on incoming updates" (fun () -> task {
        let clock = Clock()
        let ticks = Channel.CreateUnbounded<AgentTick>()
        use agent = start clock Task.CompletedTask ticks
        do! awaitResult clock.Ready
        clock.Advance 350L
        let! tick = ticks.Reader.ReadAsync().AsTask() |> awaitResult
        equal 300L tick.DueTimestamp
        equal 350L tick.QueuedTimestamp
        let processed = gate<unit>()
        equal AgentPostResult.Posted (agent.TryPost(Barrier processed))
        do! awaitResult processed.Task
        clock.Advance 50L
        let! next = ticks.Reader.ReadAsync().AsTask() |> awaitResult
        equal 400L next.DueTimestamp
        equal 400L next.QueuedTimestamp
        agent.Complete() |> ignore
        do! awaitUnit agent.Completion
        check clock.Disposed "Complete detaches ticker."
    })
    case "Abort cancels a ticker waiting for its next period" (fun () -> task {
        let clock = Clock()
        let ticks = Channel.CreateUnbounded<AgentTick>()
        use agent = start clock Task.CompletedTask ticks
        do! awaitResult clock.Ready
        agent.Abort()
        let! _ = terminal agent.Completion
        check clock.Disposed "Abort detaches ticker."
    })
]
