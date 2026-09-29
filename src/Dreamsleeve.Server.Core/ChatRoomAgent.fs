namespace Dreamsleeve.Server.Core

open System
open System.Collections.Generic
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

/// Owns one channel's ordering, history and connection-bound subscriptions.
[<RequireQualifiedAccess>]
module ChatRoomAgent =
    [<Literal>]
    let private MaxRecentPerSender = 8

    /// Admission of one account, kept across reconnects and pruned once idle.
    type private Sender = {
        mutable Tokens: float
        mutable Updated: int64
        Recent: Queue<struct (string * int64)>
    }

    /// One rate limit: chat and client announcements are admitted separately.
    type private Admission = {
        Burst: int
        RefillMs: int
        DuplicateWindowMs: int
        Senders: Dictionary<PlayerId, Sender>
        mutable NextPrune: int64
    }

    type private State = {
        Chat: Chat
        Members: Dictionary<Guid, Subscription<ChatRoomEvent>>
        Players: Dictionary<PlayerId, Guid>
        Messages: Admission
        Announcements: Admission
        /// Reserved author profile of server announcements.
        ServerAuthor: PlayerData
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

    let private admission burst refillMs duplicateWindowMs = {
        Burst = burst; RefillMs = refillMs; DuplicateWindowMs = duplicateWindowMs
        Senders = Dictionary(); NextPrune = 0L
    }

    let private refill (limits: Admission) now (sender: Sender) =
        let elapsed = float (now - sender.Updated) / float limits.RefillMs
        sender.Tokens <- min (float limits.Burst) (sender.Tokens + elapsed)
        sender.Updated <- now
        while sender.Recent.Count > 0
              && (let struct (_, sentAt) = sender.Recent.Peek() in now - sentAt >= int64 limits.DuplicateWindowMs) do
            sender.Recent.Dequeue() |> ignore

    // Entries of accounts that became idle are dropped; state stays bounded by
    // recent senders, not by everyone who ever wrote in this channel.
    let private prune (limits: Admission) now =
        if now >= limits.NextPrune then
            let idle = max (int64 limits.Burst * int64 limits.RefillMs) (int64 limits.DuplicateWindowMs)
            let expired = limits.Senders |> Seq.filter (fun entry -> now - entry.Value.Updated >= idle) |> Seq.map _.Key |> Seq.toArray
            for playerId in expired do limits.Senders.Remove playerId |> ignore
            limits.NextPrune <- now + max 1000L idle

    /// Rate and repetition are judged per stable account, so reconnecting
    /// does not reset them. Refused attempts consume nothing.
    let private admit (limits: Admission) playerId (request: ChatSubmission) =
        let now = Environment.TickCount64
        prune limits now
        let sender =
            match limits.Senders.TryGetValue playerId with
            | true, sender -> sender
            | false, _ ->
                let sender = { Tokens = float limits.Burst; Updated = now; Recent = Queue() }
                limits.Senders[playerId] <- sender
                sender
        refill limits now sender
        let repeated =
            limits.DuplicateWindowMs > 0
            && sender.Recent |> Seq.exists (fun struct (fingerprint, _) -> fingerprint = request.Fingerprint)
        if repeated then Error "The same message was sent too recently."
        elif sender.Tokens < 1.0 then Error "Too many messages. Wait a moment."
        else
            sender.Tokens <- sender.Tokens - 1.0
            if limits.DuplicateWindowMs > 0 then
                if sender.Recent.Count >= MaxRecentPerSender then sender.Recent.Dequeue() |> ignore
                sender.Recent.Enqueue(struct (request.Fingerprint, now))
            Ok ()

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

    let private publish state context (request: ChatSubmission) =
        match state.Members.TryGetValue request.ConnectionId with
        | false, _ -> reject state context request
        | true, author ->
            let limits = if request.Announcement.IsSome then state.Announcements else state.Messages
            match admit limits author.Profile.PlayerId request with
            | Error message ->
                let rejection = { Code = RequestRejectionCode.RateLimited; Message = message; Field = "text" }
                respond state context request.ConnectionId request.ReplyTo (ChatRoomEvent.Rejected(request.RequestId, rejection))
            | Ok () ->
                let create messageId sentAt =
                    let message =
                        ChatMessage.create messageId state.Chat.ChannelId author.Profile request.CharacterName request.Text sentAt
                        |> ChatMessage.withFlagged request.Flagged
                    match request.Announcement with
                    | ValueSome announcement -> ChatMessage.withAnnouncement announcement message
                    | ValueNone -> message
                append state context create (ValueSome(struct (request.ConnectionId, request.RequestId)))

    /// Server announcements share this channel's history and ID sequence, so a
    /// client that only knows the channel receives them in order.
    let private announce state context (announcement: ServerAnnouncement) =
        let create messageId sentAt =
            ChatMessage.create messageId state.Chat.ChannelId state.ServerAuthor ValueNone announcement.Text sentAt
            |> ChatMessage.withAnnouncement (Announcement.server announcement.Kind)
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

    /// Client announcements get their own rate limit; serverAuthor writes server ones.
    let startWith (config: ChatRoomOptions) (announcements: AnnouncementOptions) serverAuthor channelId (host: ReliableAgentRef<SessionHostCommand>) =
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
        elif announcements.RateBurst < 1 then
            Error (DomainError.InvalidLimit("announcementRateBurst", announcements.RateBurst))
        elif announcements.RateRefillMs < 1 then
            Error (DomainError.InvalidLimit("announcementRateRefillMs", announcements.RateRefillMs))
        elif announcements.DuplicateWindowMs < 0 then
            Error (DomainError.InvalidLimit("announcementDuplicateWindowMs", announcements.DuplicateWindowMs))
        else
            Chat.create channelId config.HistoryCapacity
            |> Result.map (fun chat ->
                let state = {
                    Chat = chat
                    Members = Dictionary()
                    Players = Dictionary()
                    Messages = admission config.RateBurst config.RateRefillMs config.DuplicateWindowMs
                    Announcements = admission announcements.RateBurst announcements.RateRefillMs announcements.DuplicateWindowMs
                    ServerAuthor = serverAuthor
                    Host = AgentOutbox(config.MaxControlDeliveries, host)
                    NextMessageId = 1UL
                }
                let options = {
                    AgentOptions.create $"chat-room-{ChatChannelId.value channelId}" with
                        Mailbox = AgentMailbox.boundedWithControl config.MailboxCapacity config.ControlReserve
                }
                Agent.Start(options, handle state, isControl = isControl))

    /// Default announcement limits; server announcements are written as "Dreamsleeve".
    let start (config: ChatRoomOptions) channelId host =
        let serverName: DisplayName = FSharp.UMX.UMX.tag ServerConfig.defaults.ServerName
        startWith config AnnouncementOptions.defaults (Announcement.serverAuthor serverName) channelId host
