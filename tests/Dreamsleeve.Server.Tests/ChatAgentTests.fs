module Dreamsleeve.Server.Tests.ChatAgentTests

open System
open System.Threading.Channels
open System.Threading.Tasks
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain
open Expecto
open AgentTests
open BackgroundTests

let private ok = function Ok value -> value | Error error -> failwithf "%A" error
let private playerId value = PlayerId.create value |> ok
let private channelId value = ChatChannelId.create value |> ok
let private messageId value = ChatMessageId.create value |> ok
let private config = { MailboxCapacity = 4; HistoryCapacity = 2 }
let private globalId = channelId 1UL

let private player value =
    PlayerData.create (playerId value)
        (Username.create 32 $"player{value}" |> ok)
        (DisplayName.create 64 $"Player {value}" |> ok)

let private message number author =
    ChatMessage.create (messageId number) globalId author
        (ChatMessageText.create 2000 $"message {number}" |> ok) DateTimeOffset.UnixEpoch

let private request command = { OperationId = Guid.NewGuid(); Command = command }

let private collect (output: Channel<ChannelReply>) (_: AgentContext<ChannelReply>) reply = task {
    check (output.Writer.TryWrite reply) "Test output was closed."
}

let private start settings output = ChatAgent.start settings globalId output |> ok

let private stop (agent: Agent<'Message>) = task {
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
}

let private send (agent: Agent<ChannelRequest>) command = task {
    let request = request command
    let! result = agent.PostAsync request
    equal AgentPostResult.Posted result
    return request.OperationId
}

let private receive (output: Channel<ChannelReply>) =
    output.Reader.ReadAsync().AsTask().WaitAsync guard

let private exchange agent output command = task {
    let! operationId = send agent command
    let! reply = receive output
    equal operationId reply.OperationId
    equal globalId reply.ChannelId
    return reply.Result
}

let private joined = function
    | Ok (ChannelOutcome.Joined snapshot) -> snapshot
    | other -> failwithf "Expected join snapshot: %A" other

let private published = function
    | Ok (ChannelOutcome.Published(message, recipients)) -> message, recipients
    | other -> failwithf "Expected accepted publication: %A" other

let private history = function
    | Ok (ChannelOutcome.History page) -> page
    | other -> failwithf "Expected history: %A" other

type private OutputMessage =
    | HoldOutput of TaskCompletionSource<unit> * TaskCompletionSource<unit>
    | Output of ChannelReply
    | Filler

let private slowOutput (replies: Channel<ChannelReply>) (_: AgentContext<OutputMessage>) command = task {
    match command with
    | HoldOutput(entered, release) ->
        entered.SetResult()
        do! release.Task
    | Output reply -> check (replies.Writer.TryWrite reply) "Test output was closed."
    | Filler -> ()
}

let tests = testList "ChatAgent" [
    case "join snapshots and accepted publications share one ordered output" (fun () -> task {
        let replies = Channel.CreateUnbounded<ChannelReply>()
        use output = Agent.Start(AgentOptions.create "channel-output", collect replies)
        use chat = start config (output.Ref.TryReliable().Value)
        let alice, bob = player 1UL, player 2UL
        let firstMessage, secondMessage = message 1UL alice, message 2UL bob
        let commands = [
            ChannelCommand.Join alice.PlayerId
            ChannelCommand.Publish firstMessage
            ChannelCommand.Join bob.PlayerId
            ChannelCommand.Leave alice.PlayerId
            ChannelCommand.Publish secondMessage
        ]
        let ids = ResizeArray<Guid>()
        for command in commands do
            let! id = send chat command
            ids.Add id
        do! stop chat

        let received = ResizeArray<ChannelReply>()
        for _ in commands do
            let! reply = receive replies
            received.Add reply
        equal (List.ofSeq ids) (received |> Seq.map _.OperationId |> List.ofSeq)
        check (received |> Seq.forall (fun reply -> reply.ChannelId = globalId)) "Channel identity lost."

        let initial = joined received[0].Result
        equal [] initial.Messages
        equal (Set.singleton alice.PlayerId) initial.Players
        let accepted, recipients = published received[1].Result
        equal firstMessage accepted
        equal (Set.singleton alice.PlayerId) recipients

        let bobSnapshot = joined received[2].Result
        equal [firstMessage] bobSnapshot.Messages
        equal (Set.ofList [alice.PlayerId; bob.PlayerId]) bobSnapshot.Players
        equal (Ok (ChannelOutcome.Left true)) received[3].Result
        let accepted, recipients = published received[4].Result
        equal secondMessage accepted
        equal (Set.singleton bob.PlayerId) recipients
        equal [] initial.Messages
        equal (Set.singleton alice.PlayerId) initial.Players
        do! stop output
    })

    case "rejected publication leaves history intact and the actor keeps serving" (fun () -> task {
        let replies = Channel.CreateUnbounded<ChannelReply>()
        use output = Agent.Start(AgentOptions.create "channel-output", collect replies)
        use chat = start config (output.Ref.TryReliable().Value)
        let alice = player 1UL
        let valid = message 1UL alice
        let! absent = exchange chat replies (ChannelCommand.Publish valid)
        equal (Error (DomainError.NotChatMember alice.PlayerId)) absent
        let! _ = exchange chat replies (ChannelCommand.Join alice.PlayerId)
        let wrongChannel = ChatMessage.create valid.MessageId (channelId 2UL) alice valid.MessageText valid.SentAt
        let! wrong = exchange chat replies (ChannelCommand.Publish wrongChannel)
        equal (Error DomainError.ChannelMismatch) wrong
        let! accepted = exchange chat replies (ChannelCommand.Publish valid)
        equal valid (published accepted |> fst)
        let! duplicate = exchange chat replies (ChannelCommand.Publish valid)
        equal (Error (DomainError.MessageOutOfOrder(valid.MessageId, valid.MessageId))) duplicate
        let! retained = exchange chat replies (ChannelCommand.ReadHistory(ValueNone, 10))
        equal [valid] (history retained).Messages
        do! stop chat
        do! stop output
    })

    case "retained history is bounded and joins return a detached tail" (fun () -> task {
        let replies = Channel.CreateUnbounded<ChannelReply>()
        use output = Agent.Start(AgentOptions.create "channel-output", collect replies)
        use chat = start config (output.Ref.TryReliable().Value)
        let alice = player 1UL
        let! _ = exchange chat replies (ChannelCommand.Join alice.PlayerId)
        for id in 1UL..4UL do
            let! _ = exchange chat replies (ChannelCommand.Publish(message id alice))
            ()

        let! result = exchange chat replies (ChannelCommand.Join alice.PlayerId)
        let snapshot = joined result
        equal 2 snapshot.HistoryCapacity
        equal (Set.singleton alice.PlayerId) snapshot.Players
        equal [3UL; 4UL] (snapshot.Messages |> List.map (fun item -> ChatMessageId.value item.MessageId))
        let! page = exchange chat replies (ChannelCommand.ReadHistory(ValueSome(messageId 1UL), 1))
        let page = history page
        equal [message 3UL alice] page.Messages
        check page.HasGap "An evicted cursor must report a history gap."
        check page.HasMore "A limited page must report remaining messages."
        equal (ValueSome(messageId 3UL)) page.NextCursor
        let! invalid = exchange chat replies (ChannelCommand.ReadHistory(ValueNone, 0))
        equal (Error (DomainError.InvalidLimit("maxCount", 0))) invalid
        let! _ = exchange chat replies (ChannelCommand.Publish(message 5UL alice))
        equal [message 3UL alice; message 4UL alice] snapshot.Messages
        do! stop chat
        do! stop output
    })

    case "concurrent duplicate submissions accept one publication" (fun () -> task {
        let replies = Channel.CreateUnbounded<ChannelReply>()
        use output = Agent.Start(AgentOptions.create "channel-output", collect replies)
        use chat = start config (output.Ref.TryReliable().Value)
        let alice = player 1UL
        let! _ = exchange chat replies (ChannelCommand.Join alice.PlayerId)
        let requests = Array.init 16 (fun _ -> request (ChannelCommand.Publish(message 1UL alice)))
        let! admissions = requests |> Array.map chat.PostAsync |> Task.WhenAll
        check (Array.forall ((=) AgentPostResult.Posted) admissions) "Submission was not admitted."
        do! stop chat

        let outcomes = ResizeArray<ChannelReply>()
        for _ in requests do
            let! reply = receive replies
            outcomes.Add reply
        equal (requests |> Array.map _.OperationId |> Set.ofArray) (outcomes |> Seq.map _.OperationId |> Set.ofSeq)
        equal 1 (outcomes |> Seq.filter (fun reply -> Result.isOk reply.Result) |> Seq.length)
        equal 15 (outcomes |> Seq.filter (fun reply ->
            reply.Result = Error (DomainError.MessageOutOfOrder(messageId 1UL, messageId 1UL))) |> Seq.length)
        do! stop output
    })

    case "full output bounds pending commands and graceful completion drains in order" (fun () -> task {
        let replies = Channel.CreateUnbounded<ChannelReply>()
        let entered, release = gate<unit>(), gate<unit>()
        use output = Agent.Start(options "slow-output" (AgentMailbox.boundedWait 1), slowOutput replies)
        output.TryPost(HoldOutput(entered, release)) |> ignore
        do! awaitResult entered.Task
        output.TryPost Filler |> ignore
        use chat = start { config with MailboxCapacity = 1 } (output.Ref.TryReliable().Value.Map Output)
        let! first = send chat (ChannelCommand.Join(playerId 1UL))
        do! eventually (fun () -> chat.QueueLength = 0)
        let second = request (ChannelCommand.Join(playerId 2UL))
        let admitted = chat.TryPost second
        let full = chat.TryPost(request (ChannelCommand.Join(playerId 3UL)))
        chat.Complete() |> ignore
        let completedEarly = chat.Completion.IsCompleted
        release.SetResult()
        do! awaitUnit chat.Completion

        equal AgentPostResult.Posted admitted
        equal AgentPostResult.Full full
        check (not completedEarly) "Completion skipped a blocked output."
        let! a = receive replies
        let! b = receive replies
        equal first a.OperationId
        equal second.OperationId b.OperationId
        equal (Set.ofList [playerId 1UL; playerId 2UL]) (joined b.Result).Players
        equal AgentPostResult.Closed (chat.TryPost second)
        do! stop output
    })

    case "abort cancels a blocked output without waiting for the recipient" (fun () -> task {
        let replies = Channel.CreateUnbounded<ChannelReply>()
        let entered, release = gate<unit>(), gate<unit>()
        use output = Agent.Start(options "slow-output" (AgentMailbox.boundedWait 1), slowOutput replies)
        output.TryPost(HoldOutput(entered, release)) |> ignore
        do! awaitResult entered.Task
        output.TryPost Filler |> ignore
        use chat = start config (output.Ref.TryReliable().Value.Map Output)
        let! _ = send chat (ChannelCommand.Join(playerId 1UL))
        do! eventually (fun () -> chat.QueueLength = 0)
        chat.Abort()
        let! _ = terminal chat.Completion
        release.SetResult()
        check chat.Completion.IsCanceled "Abort outcome was lost."
        do! stop output
        equal 0 replies.Reader.Count
    })

    case "closing the sole output stops the channel instead of silently accepting more work" (fun () -> task {
        let replies = Channel.CreateUnbounded<ChannelReply>()
        use output = Agent.Start(AgentOptions.create "channel-output", collect replies)
        let address = output.Ref.TryReliable().Value
        do! stop output
        use chat = start config address
        let! _ = send chat (ChannelCommand.Join(playerId 1UL))
        let! _ = terminal chat.Completion
        check chat.Completion.IsCanceled "A channel without its output owner must stop."
        equal (Some AgentStopReason.Aborted) chat.StopReason
        equal AgentPostResult.Closed (chat.TryPost(request (ChannelCommand.Join(playerId 2UL))))
    })

    case "invalid capacities do not start a channel" (fun () -> task {
        let replies = Channel.CreateUnbounded<ChannelReply>()
        use output = Agent.Start(AgentOptions.create "channel-output", collect replies)
        for limit in [0; -1] do
            let mailbox = ChatAgent.start { config with MailboxCapacity = limit } globalId (output.Ref.TryReliable().Value)
            let history = ChatAgent.start { config with HistoryCapacity = limit } globalId (output.Ref.TryReliable().Value)
            check (Result.isError mailbox) "Invalid mailbox capacity was accepted."
            check (Result.isError history) "Invalid history capacity was accepted."
        do! stop output
    })
]
