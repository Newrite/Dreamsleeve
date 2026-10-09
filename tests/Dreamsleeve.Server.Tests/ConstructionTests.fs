module Dreamsleeve.Server.Tests.ConstructionTests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Dreamsleeve.Agent
open Expecto
open AgentTests

let private expectError expected = function
    | Error actual -> equal expected actual
    | Ok _ -> failtestf "Expected construction rejection %A." expected

let private handle (_: AgentContext<int>) _ = Task.FromResult()

let tests = testList "Agent construction" [
    testCase "raw invalid construction never starts callbacks" (fun () ->
        let started = ref 0
        let options = {
            AgentOptions.create "invalid-construction" with
                OnStarted = Some(fun _ -> Interlocked.Increment(&started.contents) |> ignore)
        }
        for mailbox, error in [
            AgentMailbox.boundedWait 0, AgentStartError.InvalidCapacity("capacity", 0)
            AgentMailbox.boundedWithControl 1 0, AgentStartError.InvalidCapacity("controlReserve", 0)
            AgentMailbox.boundedWithControl Int32.MaxValue 1, AgentStartError.CapacityOverflow(Int32.MaxValue, 1)
            AgentMailbox.boundedWithControl 1 1, AgentStartError.MissingControlClassifier
            AgentMailbox.bounded 1 (enum<BoundedChannelFullMode> 99), AgentStartError.UnknownFullMode(enum<BoundedChannelFullMode> 99)
        ] do
            Agent.TryStart({ options with Mailbox = mailbox }, handle) |> expectError error
        Agent.TryStart(Unchecked.defaultof<AgentOptions>, handle)
        |> expectError (AgentStartError.NullArgument "options")
        Agent.TryStart(options, Unchecked.defaultof<AgentContext<int> -> int -> Task<unit>>)
        |> expectError (AgentStartError.NullArgument "handler")
        Agent.TryStart({ options with DefaultAskTimeout = Some(TimeSpan.FromMilliseconds -2.) }, handle)
        |> expectError (AgentStartError.InvalidTimeout(TimeSpan.FromMilliseconds -2.))
        equal 0 started.contents)

    testCase "reliable capability rejects dropping before execution" (fun () ->
        let mutable executed = false
        let handler (_: ReliableAgentContext<int>) _ = task { executed <- true }
        for mode in [BoundedChannelFullMode.DropWrite; BoundedChannelFullMode.DropOldest; BoundedChannelFullMode.DropNewest] do
            Agent.TryStartReliable(options "dropping" (AgentMailbox.bounded 1 mode), handler)
            |> expectError AgentStartError.DroppingMailbox
        check (not executed) "A rejected reliable owner invoked its handler.")

    testTask "prepared construction starts only at the trusted start boundary" {
        let ready = gate<unit>()
        let options = { AgentOptions.create "prepared" with OnStarted = Some(fun _ -> ready.TrySetResult() |> ignore) }
        let prepared = Agent.TryPrepare(options, handle) |> expectStarted
        check (not ready.Task.IsCompleted) "Preparing configuration started the owner."
        use agent = prepared.Start()
        do! awaitResult ready.Task
        agent.Complete() |> ignore
        do! awaitUnit agent.Completion
    }

    testTask "null wrapper builders and projections remain InvalidRequest" {
        let stateHandler (_: StatefulAgentContext) state (_: int) = Task.FromResult(StatefulTransition.SetState(state + 1))
        let mutableHandler (_: StatefulAgentContext) (state: ResizeArray<int>) value = task {
            state.Add value
            return MutableStatefulTransition.Stay
        }
        use state = TestStatefulAgent.Start(StatefulAgentOptions.create "checked-state", 0, stateHandler)
        use mutableState = TestMutableAgent.Start(MutableStatefulAgentOptions.create "checked-mutable", ResizeArray<int>(), mutableHandler)
        let builder = Unchecked.defaultof<ReplyChannel<int> -> int>
        let projection = Unchecked.defaultof<int -> int>
        let mutableProjection = Unchecked.defaultof<ResizeArray<int> -> int>
        let! stateAsk = state.TryAskAsync builder
        let! mutableAsk = mutableState.TryAskAsync builder
        let! stateRead = state.TryReadAsync projection
        let! mutableRead = mutableState.TryReadAsync mutableProjection
        equal (AgentAskResult.InvalidRequest(AgentRequestError.NullArgument "buildMessage")) stateAsk
        equal (AgentAskResult.InvalidRequest(AgentRequestError.NullArgument "buildMessage")) mutableAsk
        equal (AgentAskResult.InvalidRequest(AgentRequestError.NullArgument "projection")) stateRead
        equal (AgentAskResult.InvalidRequest(AgentRequestError.NullArgument "projection")) mutableRead
        let! current = state.TryReadAsync id
        let! count = mutableState.TryReadAsync(fun values -> values.Count)
        expectReply 0 current
        expectReply 0 count
        state.Complete() |> ignore
        mutableState.Complete() |> ignore
        do! awaitUnit state.Completion
        do! awaitUnit mutableState.Completion
    }

    testTask "null reply error is rejected without settling the request" {
        let captured = gate<ReplyChannel<int>>()
        use agent = TestAgent.Start(AgentOptions.create "reply-error", ordinaryHandler (System.Collections.Concurrent.ConcurrentQueue<int>()))
        let request = agent.TryAskAsync(fun reply -> CaptureReply(captured, reply))
        let! (reply: ReplyChannel<int>) = awaitResult captured.Task
        equal (Error(AgentRequestError.NullArgument "error")) (reply.TryReplyError null)
        check (not reply.IsCompleted) "An invalid error settled the request."
        equal true (reply.TryReply 42)
        let! result = awaitResult request
        expectReply 42 result
        agent.Complete() |> ignore
        do! awaitUnit agent.Completion
    }

    testCase "delivery and timer budgets use actual resource limits" (fun () ->
        AgentDeliveryCapacity.TryCreate 0 |> expectError (AgentStartError.InvalidCapacity("deliveryCapacity", 0))
        check (AgentDeliveryCapacity.TryCreate Int32.MaxValue |> Result.isOk) "No arbitrary delivery cap is permitted."
        for milliseconds, valid in [0., false; 0.9999, false; 1., true; 4294967294.9, true; 4294967295., false] do
            equal valid (AgentTickerInterval.TryCreate(TimeSpan.FromMilliseconds milliseconds) |> Result.isOk))
]

