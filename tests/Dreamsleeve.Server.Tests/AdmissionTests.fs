module Dreamsleeve.Server.Tests.AdmissionTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Dreamsleeve.Agent
open Expecto
open AgentTests
open BackgroundTests

type private Message =
    | Hold of control: bool * TaskCompletionSource<unit> * TaskCompletionSource<unit>
    | Data of int
    | Control of int
    | Query of int * ReplyChannel<int>
    | FailAfter of TaskCompletionSource<unit> * TaskCompletionSource<unit> * exn
    | Fail of exn

let private isControl = function
    | Hold(control, _, _) -> control
    | Control _ | FailAfter _ -> true
    | Data _ | Query _ | Fail _ -> false

let private handle (seen: ConcurrentQueue<int>) (context: AgentContext<Message>) message = task {
    match message with
    | Hold(_, entered, release) ->
        entered.TrySetResult() |> ignore
        do! release.Task.WaitAsync context.CancellationToken
    | Data value | Control value -> seen.Enqueue value
    | Query(value, reply) ->
        seen.Enqueue value
        reply.Reply value
    | FailAfter(entered, release, error) ->
        entered.TrySetResult() |> ignore
        do! release.Task.WaitAsync context.CancellationToken
        return raise error
    | Fail error -> return raise error
}

let private start ordinary reserve seen =
    Agent.Start(options "reserved-admission" (AgentMailbox.boundedWithControl ordinary reserve),
                handle seen, isControl = isControl)

let private hold control (agent: Agent<Message>) = task {
    let entered, release = gate<unit>(), gate<unit>()
    equal AgentPostResult.Posted (agent.TryPost(Hold(control, entered, release)))
    do! awaitResult entered.Task
    return release
}

let private complete (agent: Agent<Message>) = task {
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
}

let tests = testList "Admission" [
    case "ordinary saturation leaves control capacity and mapped reliable refs preserve one FIFO" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use agent = start 2 1 seen
        let reliable = agent.Ref.TryReliable().Value
        let data = reliable.Map Data
        let control = reliable.Map Control
        let! release = hold true agent

        equal AgentTryDeliveryResult.Posted (data.TryPost 1)
        equal AgentTryDeliveryResult.Posted (data.TryPost 2)
        equal AgentTryDeliveryResult.Full (data.TryPost 99)
        equal AgentTryDeliveryResult.Posted (control.TryPost 3)
        equal AgentTryDeliveryResult.Full (control.TryPost 99)
        equal 3 agent.QueueLength

        release.SetResult()
        do! complete agent
        equal [|1; 2; 3|] (seen.ToArray())
        equal 0L agent.DroppedCount
        equal AgentTryDeliveryResult.Closed (data.TryPost 4)
    })

    case "ordinary quota is released before its handler finishes" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use agent = start 1 1 seen
        let! release = hold false agent
        equal AgentPostResult.Posted (agent.TryPost(Data 1))
        equal AgentPostResult.Full (agent.TryPost(Data 99))
        equal AgentPostResult.Posted (agent.TryPost(Control 2))
        release.SetResult()
        do! complete agent
        equal [|1; 2|] (seen.ToArray())
    })

    case "failed ordinary admission to a control-filled FIFO leaks no quota" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use agent = start 2 1 seen
        let! release = hold true agent
        let enteredNext, releaseNext = gate<unit>(), gate<unit>()
        equal AgentPostResult.Posted (agent.TryPost(Control 1))
        equal AgentPostResult.Posted (agent.TryPost(Control 2))
        equal AgentPostResult.Posted (agent.TryPost(Hold(true, enteredNext, releaseNext)))
        for _ in 1 .. 8 do
            equal AgentPostResult.Full (agent.TryPost(Data 99))

        release.SetResult()
        do! awaitResult enteredNext.Task
        equal AgentPostResult.Posted (agent.TryPost(Data 3))
        equal AgentPostResult.Posted (agent.TryPost(Data 4))
        equal AgentPostResult.Full (agent.TryPost(Data 99))
        releaseNext.SetResult()
        do! complete agent
        equal [|1; 2; 3; 4|] (seen.ToArray())
    })

    case "canceling an ordinary waiter does not consume control or future ordinary capacity" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use agent = start 1 1 seen
        let! release = hold true agent
        equal AgentPostResult.Posted (agent.TryPost(Data 1))
        use cancel = new CancellationTokenSource()
        let waiting = agent.Ref.Map(Data).PostAsync(99, cancellationToken = cancel.Token)
        check (not waiting.IsCompleted) "Ordinary writer bypassed its quota."
        cancel.Cancel()
        let! canceled = awaitResult waiting
        equal AgentPostResult.Canceled canceled
        equal AgentPostResult.Posted (agent.TryPost(Control 2))
        release.SetResult()
        let! replied = agent.TryAskAsync(fun reply -> Query(3, reply)) |> awaitResult
        expectReply 3 replied
        do! complete agent
        equal [|1; 2; 3|] (seen.ToArray())
    })

    case "ordinary asynchronous admission wakes when quota changes while FIFO has spare space" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use agent = start 1 2 seen
        let! release = hold true agent
        let enteredNext, releaseNext = gate<unit>(), gate<unit>()
        equal AgentPostResult.Posted (agent.TryPost(Data 1))
        equal AgentPostResult.Posted (agent.TryPost(Hold(true, enteredNext, releaseNext)))
        let waiting = agent.Ref.TryReliable().Value.Map(Data).PostAsync 2
        check (not waiting.IsCompleted) "Ordinary quota was ignored because total space remained."

        release.SetResult()
        do! awaitResult enteredNext.Task
        let! admitted = awaitResult waiting
        equal AgentDeliveryResult.Posted admitted
        check (not releaseNext.Task.IsCompleted) "Quota was released only after the later handler finished."
        releaseNext.SetResult()
        do! complete agent
        equal [|1; 2|] (seen.ToArray())
    })

    case "concurrent ordinary writers cannot consume the control reserve" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use agent = start 8 2 seen
        let! release = hold true agent
        let data = agent.Ref.TryReliable().Value.Map Data
        let sends = [|for i in 1 .. 64 -> Task.Run(fun () -> data.TryPost i)|]
        let! admissions = Task.WhenAll sends |> awaitResult
        equal 8 (admissions |> Array.filter ((=) AgentTryDeliveryResult.Posted) |> Array.length)
        equal 56 (admissions |> Array.filter ((=) AgentTryDeliveryResult.Full) |> Array.length)
        equal AgentPostResult.Posted (agent.TryPost(Control 100))
        equal AgentPostResult.Posted (agent.TryPost(Control 101))
        equal AgentPostResult.Full (agent.TryPost(Control 102))
        equal 10 agent.QueueLength
        release.SetResult()
        do! complete agent
        equal 10 seen.Count
        equal [|100; 101|] (seen.ToArray() |> Array.skip 8)
    })

    case "Complete releases quota waiters without waiting for the current handler" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use agent = start 1 1 seen
        let! release = hold true agent
        equal AgentPostResult.Posted (agent.TryPost(Data 1))
        let waiting = agent.PostAsync(Data 99)
        check (not waiting.IsCompleted) "Expected an ordinary admission wait."
        agent.Complete() |> ignore
        let! closed = awaitResult waiting
        equal AgentPostResult.Closed closed
        check (not agent.Completion.IsCompleted) "Complete skipped its running handler."
        release.SetResult()
        do! awaitUnit agent.Completion
        equal [|1|] (seen.ToArray())
        equal 0 agent.QueueLength
    })

    case "Abort closes waiting writers and settles queued asks" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use agent = start 1 1 seen
        let! _ = hold true agent
        let reply = agent.TryAskAsync(fun reply -> Query(99, reply))
        do! eventually (fun () -> agent.QueueLength = 1)
        let waiting = agent.PostAsync(Data 99)
        equal AgentPostResult.Posted (agent.TryPost(Control 99))
        agent.Abort()
        let! closed = awaitResult waiting
        equal AgentPostResult.Closed closed
        let! canceled = awaitResult reply
        equal AgentAskResult.Canceled canceled
        let! _ = terminal agent.Completion
        equal 0 agent.QueueLength
        equal 0 seen.Count
    })

    case "handler fault closes quota waiters and preserves the queued request failure" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use agent = start 1 1 seen
        let entered, release = gate<unit>(), gate<unit>()
        let expected = InvalidOperationException("controlled handler failure")
        equal AgentPostResult.Posted (agent.TryPost(FailAfter(entered, release, expected)))
        do! awaitResult entered.Task
        let reply = agent.TryAskAsync(fun reply -> Query(99, reply))
        do! eventually (fun () -> agent.QueueLength = 1)
        let waiting = agent.PostAsync(Data 99)
        equal AgentPostResult.Posted (agent.TryPost(Control 99))
        release.SetResult()
        let! closed = awaitResult waiting
        equal AgentPostResult.Closed closed
        let! faulted = awaitResult reply
        expectFault expected faulted
        let! _ = terminal agent.Completion
        expectStopFault expected agent.StopReason
        equal 0 agent.QueueLength
        equal 0 seen.Count
    })

    case "continuing after a handler fault leaves ordinary capacity reusable" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        let settings = { options "continue-reserved" (AgentMailbox.boundedWithControl 1 1) with
                            OnError = Some(fun _ -> AgentErrorAction.Continue) }
        use agent = Agent.Start(settings, handle seen, isControl = isControl)
        let! release = hold true agent
        equal AgentPostResult.Posted (agent.TryPost(Fail(InvalidOperationException("continue"))))
        let waiting = agent.PostAsync(Data 1)
        release.SetResult()
        let! admitted = awaitResult waiting
        equal AgentPostResult.Posted admitted
        let! reply = agent.TryAskAsync(fun reply -> Query(2, reply)) |> awaitResult
        expectReply 2 reply
        do! complete agent
        equal [|1; 2|] (seen.ToArray())
    })
]
