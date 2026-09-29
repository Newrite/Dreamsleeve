namespace Dreamsleeve.Server.Core

open System
open System.Collections.Generic
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

/// Owns one channel's ordering, history and connection-bound subscriptions.
/// The channel kind decides what may be published (see ChatChannelKind).
[<RequireQualifiedAccess>]
module ChatRoomAgent =
    type private State = {
        Chat: Chat
        Members: Dictionary<Guid, Subscription<ChatRoomEvent>>
        Players: Dictionary<PlayerId, Guid>
        /// Admission per account, kept across reconnects and pruned once idle.
        Senders: RateLimit.State
        Options: ChatRoomOptions
        Host: AgentOutbox<SessionHostCommand>
        mutable NextMessageId: uint64
    }

    let private isControl = function
        | ChatRoomCommand.Join _ | ChatRoomCommand.Detach _ -> true
        | ChatRoomCommand.Publish _ | ChatRoomCommand.Announce _ | ChatRoomCommand.ReadHistory _ -> false

    let private notifyHost state context command =
        if not (state.Host.TrySend(context, command)) then
            // The owner observes Completion and contains failure of this source.
            context.Abort()

    let private remove state connectionId =
        match state.Members.TryGetValue connectionId with
        | false, _ -> ()
        | true, memberState ->
            state.Members.Remove connectionId |> ignore
            state.Players.Remove memberState.Profile.PlayerId |> ignore
            Chat.leave memberState.Profile.PlayerId state.Chat |> ignore

    let private deliver state context (subscriber: Subscription<ChatRoomEvent>) event =
        match subscriber.Events.TryPost event with
        | AgentTryDeliveryResult.Posted -> true
        | AgentTryDeliveryResult.Closed ->
            remove state subscriber.ConnectionId
            false
        | AgentTryDeliveryResult.Full ->
            remove state subscriber.ConnectionId
            notifyHost state context (SessionHostCommand.SlowConsumer subscriber.ConnectionId)
            false

    let private respond state context connectionId (replyTo: ReliableAgentRef<ChatRoomEvent>) response =
        match replyTo.TryPost response with
        | AgentTryDeliveryResult.Posted | AgentTryDeliveryResult.Closed -> ()
        | AgentTryDeliveryResult.Full ->
            notifyHost state context (SessionHostCommand.Close(connectionId, "chat_response_overflow"))

    let private join state context (subscription: Subscription<ChatRoomEvent>) =
        let sameConnection =
            match state.Members.TryGetValue subscription.ConnectionId with
            | true, existing -> existing.Profile = subscription.Profile
            | false, _ -> true
        let samePlayer =
            match state.Players.TryGetValue subscription.Profile.PlayerId with
            | true, owner -> owner = subscription.ConnectionId
            | false, _ -> true

        if not sameConnection || not samePlayer then
            respond state context subscription.ConnectionId subscription.Events
                (ChatRoomEvent.JoinFailed "Player identity already belongs to another subscription.")
        else
            state.Members[subscription.ConnectionId] <- subscription
            state.Players[subscription.Profile.PlayerId] <- subscription.ConnectionId
            Chat.join subscription.Profile.PlayerId state.Chat |> ignore

            // Snapshot is admitted before any later publication from this owner.
            deliver state context subscription (ChatRoomEvent.Joined(Chat.snapshot state.Chat)) |> ignore

    let private reject state context (request: ChatSubmission) =
        let rejection = {
            Code = RequestRejectionCode.NotChannelMember
            Message = "Player is not a member of this channel."
            Field = ""
        }
        respond state context request.ConnectionId request.ReplyTo
            (ChatRoomEvent.Rejected(request.RequestId, rejection))

    /// Rate and repetition are judged per stable account, so reconnecting
    /// does not reset them. Refused attempts consume nothing.
    let private admit state playerId (request: ChatSubmission) =
        match RateLimit.admit state.Senders Environment.TickCount64 playerId request.Fingerprint with
        | Ok () -> Ok ()
        | Error RateLimit.Refusal.Repeated -> Error "The same message was sent too recently."
        | Error RateLimit.Refusal.Exhausted -> Error "Too many messages. Wait a moment."

    /// Assigns the next ID and time, stores and relays. The requesting
    /// connection, when there is one, receives the correlated acceptance.
    let private append (state: State) (context: AgentContext<ChatRoomCommand>) (create: ChatMessageId -> DateTimeOffset -> ChatMessage) (requester: struct (Guid * uint64) voption) =
        match ChatMessageId.create state.NextMessageId with
        | Error _ -> context.Abort()
        | Ok messageId ->
            let now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            let message = create messageId now

            match Chat.append message state.Chat with
            | Error _ -> context.Abort()
            | Ok () ->
                state.NextMessageId <-
                    if state.NextMessageId = UInt64.MaxValue then 0UL else state.NextMessageId + 1UL

                // Delivery may remove a slow subscriber; enumerate the accepted audience.
                let recipients = state.Members.Values |> Seq.toArray
                for recipient in recipients do
                    let event =
                        match requester with
                        | ValueSome(struct (connectionId, requestId)) when recipient.ConnectionId = connectionId ->
                            ChatRoomEvent.Accepted(requestId, message)
                        | ValueSome _ | ValueNone -> ChatRoomEvent.Published message
                    deliver state context recipient event |> ignore

    /// The session decides the author's public identity; it must be this member's.
    let private publish state context (request: ChatSubmission) =
        match state.Members.TryGetValue request.ConnectionId with
        | true, author when author.Profile.PlayerId <> request.Author.PlayerId ->
            notifyHost state context (SessionHostCommand.Close(request.ConnectionId, "chat_author_mismatch"))
        | false, _ -> reject state context request
        | true, author ->
            match admit state author.Profile.PlayerId request with
            | Error message ->
                let rejection = { Code = RequestRejectionCode.RateLimited; Message = message; Field = "text" }
                respond state context request.ConnectionId request.ReplyTo (ChatRoomEvent.Rejected(request.RequestId, rejection))
            | Ok () ->
                let create messageId sentAt =
                    let message =
                        ChatMessage.create messageId state.Chat.ChannelId request.Author request.CharacterName request.Text sentAt
                        |> ChatMessage.withFlagged request.Flagged
                    request.Announcement |> ValueOption.fold (fun message announcement -> ChatMessage.withAnnouncement announcement message) message
                append state context create (ValueSome(struct (request.ConnectionId, request.RequestId)))

    /// Only a system channel accepts it. The session routes by channel kind, so
    /// a refused append here is a broken invariant and the owner stops.
    let private announce state context (announcement: ServerAnnouncement) =
        let create messageId sentAt =
            ChatMessage.serverAnnouncement messageId state.Chat.ChannelId announcement.Kind announcement.Text sentAt
        append state context create ValueNone

    let private detach state (context: AgentContext<ChatRoomCommand>) (request: SessionDetach) =
        remove state request.ConnectionId

        // The recipient reserves control admission. If even that is exhausted, stop
        // visibly instead of losing cleanup acknowledgement or waiting in the handler.
        match request.ReplyTo.TryPost request.ConnectionId with
        | AgentTryDeliveryResult.Posted | AgentTryDeliveryResult.Closed -> ()
        | AgentTryDeliveryResult.Full -> context.Abort()

    let private handle state (context: AgentContext<ChatRoomCommand>) command = task {
        match command with
        | ChatRoomCommand.Join subscription -> join state context subscription
        | ChatRoomCommand.Publish request -> publish state context request
        | ChatRoomCommand.Announce announcement -> announce state context announcement
        | ChatRoomCommand.Detach request -> detach state context request
        | ChatRoomCommand.ReadHistory(cursor, count, reply) ->
            reply.Reply(Chat.historyAfter cursor count state.Chat)
    }

    /// One owner per channel; its ID follows from the kind.
    let start (config: ChatRoomOptions) kind (host: ReliableAgentRef<SessionHostCommand>) =
        if config.MailboxCapacity < 1 then
            Error (DomainError.InvalidLimit("mailboxCapacity", config.MailboxCapacity))
        elif config.ControlReserve < 1 || int64 config.MailboxCapacity + int64 config.ControlReserve > int64 Int32.MaxValue then
            Error (DomainError.InvalidLimit("controlReserve", config.ControlReserve))
        elif config.MaxControlDeliveries < 1 then
            Error (DomainError.InvalidLimit("maxControlDeliveries", config.MaxControlDeliveries))
        elif config.RateBurst < 1 then
            Error (DomainError.InvalidLimit("rateBurst", config.RateBurst))
        elif config.RateRefillMs < 1 then
            Error (DomainError.InvalidLimit("rateRefillMs", config.RateRefillMs))
        elif config.DuplicateWindowMs < 0 then
            Error (DomainError.InvalidLimit("duplicateWindowMs", config.DuplicateWindowMs))
        else
            Chat.create kind config.HistoryCapacity
            |> Result.map (fun chat ->
                let state = {
                    Chat = chat
                    Members = Dictionary()
                    Players = Dictionary()
                    Senders = RateLimit.create { Burst = config.RateBurst; RefillMs = config.RateRefillMs; DuplicateWindowMs = config.DuplicateWindowMs }
                    Options = config
                    Host = AgentOutbox(config.MaxControlDeliveries, host)
                    NextMessageId = 1UL
                }
                let options = {
                    AgentOptions.create $"chat-room-{ChatChannelId.value (ChatChannelKind.channelId kind)}" with
                        Mailbox = AgentMailbox.boundedWithControl config.MailboxCapacity config.ControlReserve
                }
                Agent.Start(options, handle state, isControl = isControl))
