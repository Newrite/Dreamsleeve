module Dreamsleeve.Server.Tests.AsyncDispatcherTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Dreamsleeve.Agent
open Expecto
open AgentTests
open BackgroundTests

type private Request = {
    Value: int
    ReplyTo: ReliableAgentRef<AgentTests.Message>
}

type private OwnerMessage =
    | Dispatch of Request
    | Ping of TaskCompletionSource<unit>

let private start capacity execute =
    let handler = TestReplyDispatcher.createAsyncHandler capacity (fun request -> request.ReplyTo) execute
    TestAgent.StartReliable(options "async-dispatcher" (AgentMailbox.boundedWait 1), handler)

let private post (owner: Agent<Request>) (destination: Agent<AgentTests.Message>) value = task {
    let! result = owner.PostAsync {
        Value = value
        ReplyTo = destination.Ref.TryReliable().Value
    }
    equal AgentPostResult.Posted result
}

let private finish (agent: Agent<'T>) = task {
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
}

let private failingWork asynchronous = case (sprintf "%s operation failure faults the owner and joins sibling cleanup" (if asynchronous then "asynchronous" else "synchronous")) (fun () -> task {
    let entered, canceled = gate<unit>(), gate<unit>()
    let failure = InvalidOperationException("operation failed")
    let execute (token: CancellationToken) request =
        if request.Value = 1 then task {
            entered.SetResult()
            try
                do! Task.Delay(Timeout.Infinite, token)
                return AgentTests.Message.Record 1
            finally
                canceled.SetResult()
        }
        elif asynchronous then
            Task.FromException<AgentTests.Message> failure
        else
            raise failure

    use destination = TestAgent.Start(AgentOptions.create "replies", ordinaryHandler (ConcurrentQueue<int>()))
    use owner = start 2 execute
    do! post owner destination 1
    do! awaitResult entered.Task
    do! post owner destination 2
    let! _ = terminal owner.Completion
    expectStopFault failure owner.StopReason
    check canceled.Task.IsCompleted "Fault completion preceded sibling cleanup."
    do! finish destination
})

let tests = testList "AsyncDispatcher" [
    case "even a blocking synchronous prefix runs outside the mailbox and Complete joins it" (fun () -> task {
        let started, dispatched = gate<unit>(), gate<unit>()
        use release = new ManualResetEventSlim(false)
        let execute (token: CancellationToken) request =
            started.SetResult()
            release.Wait token
            Task.FromResult(AgentTests.Message.Record request.Value)

        let dispatcher = TestReplyDispatcher.createAsyncHandler 1 (fun request -> request.ReplyTo) execute
        let handle (context: ReliableAgentContext<OwnerMessage>) message = task {
            match message with
            | Dispatch request -> do! dispatcher context request
            | Ping reply -> reply.SetResult()
        }
        let seen = ConcurrentQueue<int>()
        use destination = TestAgent.Start(AgentOptions.create "replies", ordinaryHandler seen)
        use owner = TestAgent.StartReliable(AgentOptions.create "owner", handle)
        try
            let! admitted = owner.PostAsync(Dispatch {
                Value = 7
                ReplyTo = destination.Ref.TryReliable().Value
            })
            equal AgentPostResult.Posted admitted
            do! awaitResult started.Task
            let! pinged = owner.PostAsync(Ping dispatched)
            equal AgentPostResult.Posted pinged
            do! awaitResult dispatched.Task
            owner.Complete() |> ignore
            check (not owner.Completion.IsCompleted) "Completion abandoned the blocked operation."
            release.Set()
            do! awaitUnit owner.Completion
            do! finish destination
            equal [|7|] (seen.ToArray())
        finally
            release.Set()
    })

    case "sync dispatcher accepts a nested request while its owner uses a larger command type" (fun () -> task {
        let seen = ConcurrentQueue<int>()
        use destination = TestAgent.Start(AgentOptions.create "replies", ordinaryHandler seen)
        let dispatcher = TestReplyDispatcher.createHandler 1 (fun request -> request.ReplyTo)
                             (fun request -> AgentTests.Message.Record request.Value)
        let handle (context: ReliableAgentContext<OwnerMessage>) message = task {
            match message with
            | Dispatch request -> do! dispatcher context request
            | Ping reply -> reply.SetResult()
        }
        use owner = TestAgent.StartReliable(AgentOptions.create "owner", handle)
        let! admitted = owner.PostAsync(Dispatch {
            Value = 42
            ReplyTo = destination.Ref.TryReliable().Value
        })
        equal AgentPostResult.Posted admitted
        let pinged = gate<unit>()
        let! _ = owner.PostAsync(Ping pinged)
        do! awaitResult pinged.Task
        do! finish owner
        do! finish destination
        equal [|42|] (seen.ToArray())
    })

    case "one capacity reservation covers execution and reply until worker admission releases it" (fun () -> task {
        let started, releaseWork, executedFirst = gate<unit>(), gate<unit>(), gate<unit>()
        let executed, seen = ConcurrentQueue<int>(), ConcurrentQueue<int>()
        let execute (token: CancellationToken) request = task {
            executed.Enqueue request.Value
            if request.Value = 1 then
                started.SetResult()
                do! releaseWork.Task.WaitAsync token
                executedFirst.SetResult()

            return AgentTests.Message.Record request.Value
        }
        use destination = TestAgent.Start(options "full-replies" (AgentMailbox.boundedWait 1), ordinaryHandler seen)
        let! releaseReplies = holdAgent destination
        equal AgentPostResult.Posted (destination.TryPost(AgentTests.Message.Record 99))
        use owner = start 1 execute
        try
            do! post owner destination 1
            do! awaitResult started.Task
            do! post owner destination 2
            do! eventually (fun () -> owner.QueueLength = 0)
            do! post owner destination 3
            equal [|1|] (executed.ToArray())

            releaseWork.SetResult()
            do! awaitResult executedFirst.Task
            equal [|1|] (executed.ToArray())
            owner.Complete() |> ignore
            check (not owner.Completion.IsCompleted) "Completion abandoned a pending reply."
            releaseReplies.SetResult()
            do! awaitUnit owner.Completion
            do! finish destination
            equal [|1; 2; 3|] (executed.ToArray())
            equal [|99; 1; 2; 3|] (seen.ToArray())
        finally
            releaseWork.TrySetResult() |> ignore
            releaseReplies.TrySetResult() |> ignore
    })

    case "a slow or closed requester does not stop other callers while capacity remains" (fun () -> task {
        let executed, slowSeen, healthySeen = ConcurrentQueue<int>(), ConcurrentQueue<int>(), ConcurrentQueue<int>()
        let execute _ request =
            executed.Enqueue request.Value
            Task.FromResult(AgentTests.Message.Record request.Value)
        use slow = TestAgent.Start(options "slow" (AgentMailbox.boundedWait 1), ordinaryHandler slowSeen)
        let! release = holdAgent slow
        equal AgentPostResult.Posted (slow.TryPost(AgentTests.Message.Record 99))
        use closed = TestAgent.Start(AgentOptions.create "closed", ordinaryHandler (ConcurrentQueue<int>()))
        do! finish closed
        use healthy = TestAgent.Start(AgentOptions.create "healthy", ordinaryHandler healthySeen)
        use owner = start 2 execute
        try
            do! post owner slow 1
            do! eventually (fun () -> executed.Count = 1)
            do! post owner closed 2
            do! post owner healthy 3
            do! eventually (fun () -> healthySeen.Count = 1)
            equal [|3|] (healthySeen.ToArray())
            check (executed.ToArray() |> Array.contains 2) "A closed caller prevented its admitted command from executing."
            check (not release.Task.IsCompleted) "Healthy progress waited for the slow caller."
            owner.Complete() |> ignore
            check (not owner.Completion.IsCompleted) "Completion skipped the remaining reply."
            release.SetResult()
            do! awaitUnit owner.Completion
            do! finish slow
            do! finish healthy
            equal [|99; 1|] (slowSeen.ToArray())
        finally
            release.TrySetResult() |> ignore
    })

    case "Abort cancels active work, a blocked reply and capacity wait without starting queued work" (fun () -> task {
        let started, cleaned = gate<unit>(), gate<unit>()
        let executed, seen = ConcurrentQueue<int>(), ConcurrentQueue<int>()
        let execute (token: CancellationToken) request = task {
            executed.Enqueue request.Value
            if request.Value = 1 then
                started.SetResult()
                try
                    do! Task.Delay(Timeout.Infinite, token)
                finally
                    cleaned.SetResult()

            return AgentTests.Message.Record request.Value
        }
        use destination = TestAgent.Start(options "full-replies" (AgentMailbox.boundedWait 1), ordinaryHandler seen)
        let! release = holdAgent destination
        equal AgentPostResult.Posted (destination.TryPost(AgentTests.Message.Record 99))
        use owner = start 2 execute
        try
            do! post owner destination 1
            do! awaitResult started.Task
            do! post owner destination 2
            do! eventually (fun () -> executed.Count = 2)
            do! post owner destination 3
            do! eventually (fun () -> owner.QueueLength = 0)
            do! post owner destination 4

            owner.Abort()
            let! _ = terminal owner.Completion
            check owner.Completion.IsCanceled "Abort changed into a fault or success."
            check cleaned.Task.IsCompleted "Completion abandoned operation cleanup."
            equal [|1; 2|] (executed.ToArray())
            release.SetResult()
            do! finish destination
            equal [|99|] (seen.ToArray())
        finally
            release.TrySetResult() |> ignore
    })

    failingWork false
    failingWork true

    case "a reply mapper failure after Complete remains an observable fault" (fun () -> task {
        let started, release = gate<unit>(), gate<unit>()
        let failure = InvalidOperationException("reply mapper failed")
        let execute (token: CancellationToken) request = task {
            started.SetResult()
            do! release.Task.WaitAsync token
            return AgentTests.Message.Record request.Value
        }

        use destination = TestAgent.Start(AgentOptions.create "replies", ordinaryHandler (ConcurrentQueue<int>()))
        let broken = destination.Ref.TryReliable().Value.Map(fun (_: AgentTests.Message) -> raise failure)
        use owner = start 1 execute
        let! admitted = owner.PostAsync {
            Value = 1
            ReplyTo = broken
        }
        equal AgentPostResult.Posted admitted
        do! awaitResult started.Task
        owner.Complete() |> ignore
        release.SetResult()
        let! _ = terminal owner.Completion
        expectStopFault failure owner.StopReason
        do! finish destination
    })
]
