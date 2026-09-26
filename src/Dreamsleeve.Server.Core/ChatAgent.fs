namespace Dreamsleeve.Server.Core

open System
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

type ChatAgentConfig = {
    MailboxCapacity: int
    HistoryCapacity: int
}

[<RequireQualifiedAccess>]
type ChannelCommand =
    | Join of PlayerId
    | Leave of PlayerId
    /// The server supplies identity, message ID and time before dispatching.
    | Publish of ChatMessage
    | ReadHistory of cursor: ChatMessageId voption * maxCount: int

[<RequireQualifiedAccess>]
type ChannelOutcome =
    | Joined of ChatSnapshot
    | Left of removed: bool
    /// The recipients belong to the instant of acceptance, including the author.
    | Published of message: ChatMessage * recipients: Set<PlayerId>
    | History of ChatHistoryPage

type ChannelRequest = {
    OperationId: Guid
    Command: ChannelCommand
}

type ChannelReply = {
    ChannelId: ChatChannelId
    OperationId: Guid
    Result: Result<ChannelOutcome, DomainError>
}

/// One sequential owner of channel membership and retained history.
[<RequireQualifiedAccess>]
module ChatAgent =
    let private execute chat command =
        match command with
        | ChannelCommand.Join playerId ->
            Chat.join playerId chat |> ignore
            Ok (ChannelOutcome.Joined (Chat.snapshot chat))

        | ChannelCommand.Leave playerId ->
            Ok (ChannelOutcome.Left (Chat.leave playerId chat))

        | ChannelCommand.Publish message ->
            Chat.append message chat
            |> Result.map (fun () ->
                ChannelOutcome.Published(message, Chat.membersSnapshot chat))

        | ChannelCommand.ReadHistory(cursor, maxCount) ->
            Chat.historyAfter cursor maxCount chat
            |> Result.map ChannelOutcome.History

    let private handle (chat: Chat) (output: ReliableAgentRef<ChannelReply>)
                       (context: AgentContext<ChannelRequest>) (request: ChannelRequest) = task {
        if not context.CancellationToken.IsCancellationRequested then
            let reply = {
                ChannelId = chat.ChannelId
                OperationId = request.OperationId
                Result = execute chat request.Command
            }

            let! delivered = output.PostAsync(reply, cancellationToken = context.CancellationToken)

            match delivered with
            | AgentDeliveryResult.Posted -> ()
            | AgentDeliveryResult.Closed -> context.Abort()
            | AgentDeliveryResult.Canceled -> ()
    }

    /// A single output preserves ordering between join snapshots and later publications.
    /// Its owner routes accepted messages to sessions; this agent never handles peers.
    let start config channelId output =
        if config.MailboxCapacity < 1 then
            Error (DomainError.InvalidLimit ("mailboxCapacity", config.MailboxCapacity))
        else
            Chat.create channelId config.HistoryCapacity
            |> Result.map (fun chat ->
                let options = {
                    AgentOptions.create $"chat-{ChatChannelId.value channelId}" with
                        Mailbox = AgentMailbox.boundedWait config.MailboxCapacity
                }

                Agent.Start(options, handle chat output))
