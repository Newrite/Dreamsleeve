namespace Dreamsleeve.Server.Core

open System
open System.Collections.Generic
open Dreamsleeve.Server.Domain

/// A decoded command bound to its originating connection by the transport owner.
type ChatSendRequest = {
    ConnectionId: Guid
    RequestId: uint64
    ChannelId: ChatChannelId
    Text: ChatMessageText
}

/// Publication bookkeeping owned by SessionRegistry, separate from membership operations.
[<RequireQualifiedAccess>]
module internal ChatPublication =
    type AdmissionFailure =
        | Overloaded
        | IdExhausted

    type Completion =
        | Accepted of ChatSendRequest * ChatMessage * Set<PlayerId>
        | NotMember of ChatSendRequest
        | InvalidReply of string

    type private Pending = {
        Request: ChatSendRequest
        Message: ChatMessage
    }

    type State = private {
        Pending: Dictionary<Guid, Pending>
        MaxPending: int
        MaxPerConnection: int
        mutable NextMessageId: uint64
    }

    let create maxPending maxPerConnection = {
        Pending = Dictionary()
        MaxPending = maxPending
        MaxPerConnection = maxPerConnection
        NextMessageId = 1UL
    }

    let isEmpty state = state.Pending.Count = 0

    let send state request author enqueue =
        let pendingForConnection =
            state.Pending.Values
            |> Seq.filter (fun pending -> pending.Request.ConnectionId = request.ConnectionId)
            |> Seq.length

        if state.Pending.Count >= state.MaxPending || pendingForConnection >= state.MaxPerConnection then
            Error Overloaded
        else
            match ChatMessageId.create state.NextMessageId with
            | Error _ -> Error IdExhausted
            | Ok messageId ->
                let now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                let message = ChatMessage.create messageId request.ChannelId author request.Text now
                let operationId = Guid.NewGuid()

                if enqueue { OperationId = operationId; Command = ChannelCommand.Publish message } then
                    state.Pending.Add(operationId, { Request = request; Message = message })
                    state.NextMessageId <- if state.NextMessageId = UInt64.MaxValue then 0UL else state.NextMessageId + 1UL
                    Ok ()
                else
                    Error Overloaded

    /// Consume each reply once; retain disconnected senders until their publication settles.
    let complete state (reply: ChannelReply) =
        match state.Pending.TryGetValue reply.OperationId with
        | false, _ -> None
        | true, pending ->
            state.Pending.Remove reply.OperationId |> ignore

            let completion =
                if reply.ChannelId <> pending.Request.ChannelId then
                    InvalidReply "publication channel identity"
                else
                    match reply.Result with
                    | Ok (ChannelOutcome.Published(message, recipients)) ->
                        if message <> pending.Message || not (recipients.Contains message.Author.PlayerId) then
                            InvalidReply "accepted publication"
                        else
                            Accepted(pending.Request, message, recipients)

                    | Error (DomainError.NotChatMember playerId) when playerId = pending.Message.Author.PlayerId ->
                        NotMember pending.Request

                    | Ok (ChannelOutcome.Joined _ | ChannelOutcome.Left _ | ChannelOutcome.History _)
                    | Error (DomainError.NotChatMember _ | DomainError.ChannelMismatch | DomainError.MessageOutOfOrder _
                           | DomainError.InvalidText _ | DomainError.InvalidLimit _ | DomainError.InvalidId _
                           | DomainError.InvalidLocalFormId _ | DomainError.NonFiniteNumber _ | DomainError.InvalidRadius
                           | DomainError.PlayerIdentityMismatch) ->
                        InvalidReply "publication outcome"

            Some completion
