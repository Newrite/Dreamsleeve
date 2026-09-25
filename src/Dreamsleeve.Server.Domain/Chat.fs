namespace Dreamsleeve.Server.Domain

open System
open System.Collections.Generic

/// An immutable message with the author's profile at the time of sending.
type ChatMessage =
    private {
        messageId: ChatMessageId
        channelId: ChatChannelId
        author: PlayerData
        messageText: ChatMessageText
        sentAt: DateTimeOffset
    }

    member this.MessageId = this.messageId
    member this.ChannelId = this.channelId
    member this.Author = this.author
    member this.MessageText = this.messageText
    member this.SentAt = this.sentAt

[<RequireQualifiedAccess>]
module ChatMessage =
    /// Components have already passed their own domain validation.
    /// The server supplies the ID and timestamp; the author's profile is immutable.
    let create messageId channelId author messageText (sentAt: DateTimeOffset) =
        {
            messageId = messageId
            channelId = channelId
            author = author
            messageText = messageText
            sentAt = sentAt.ToUniversalTime()
        }

/// A detached page of retained history, ordered by increasing message ID.
type ChatHistoryPage = {
    Messages: ChatMessage list
    /// A supplied cursor predates a message removed from this channel's history.
    /// A fresh request without a cursor never reports a gap.
    HasGap: bool
    HasMore: bool
    /// Last returned message ID, or the supplied cursor when no messages were returned.
    NextCursor: ChatMessageId voption
}

/// A detached view that can safely leave the agent owning the chat.
type ChatSnapshot = {
    ChannelId: ChatChannelId
    Players: Set<PlayerId>
    Messages: ChatMessage list
    HistoryCapacity: int
}

/// Mutable state owned exclusively by one agent. All operations, including reads,
/// must run inside that owner. No function exposes its live collections.
[<NoEquality; NoComparison>]
type Chat =
    private {
        channelId: ChatChannelId
        historyCapacity: int
        players: HashSet<PlayerId>
        messages: Queue<ChatMessage>
        mutable lastAcceptedId: ChatMessageId voption
        mutable lastEvictedId: ChatMessageId voption
    }

    member this.ChannelId = this.channelId
    member this.HistoryCapacity = this.historyCapacity

[<RequireQualifiedAccess>]
module Chat =
    let create channelId historyCapacity =
        if historyCapacity <= 0 then
            Error (DomainError.InvalidLimit ("historyCapacity", historyCapacity))
        else
            Ok {
                channelId = channelId
                historyCapacity = historyCapacity
                players = HashSet<PlayerId>()
                messages = Queue<ChatMessage>()
                lastAcceptedId = ValueNone
                lastEvictedId = ValueNone
            }

    /// Returns true only when membership was added.
    let join playerId (chat: Chat) =
        chat.players.Add playerId

    /// Returns true only when membership existed and was removed.
    let leave playerId (chat: Chat) =
        chat.players.Remove playerId

    let contains playerId (chat: Chat) =
        chat.players.Contains playerId

    let memberCount (chat: Chat) =
        chat.players.Count

    let messageCount (chat: Chat) =
        chat.messages.Count

    let membersSnapshot (chat: Chat) =
        Set.ofSeq chat.players

    /// Accepts a member's message for this channel. IDs must strictly increase;
    /// gaps are allowed because the server can allocate IDs across all channels.
    /// Validation failures leave membership and history unchanged.
    let append (message: ChatMessage) (chat: Chat) =
        if message.ChannelId <> chat.channelId then
            Error DomainError.ChannelMismatch
        elif not (chat.players.Contains message.Author.PlayerId) then
            Error (DomainError.NotChatMember message.Author.PlayerId)
        else
            match chat.lastAcceptedId with
            | ValueSome previous when message.MessageId <= previous ->
                Error (DomainError.MessageOutOfOrder (previous, message.MessageId))
            | _ ->
                chat.messages.Enqueue message
                chat.lastAcceptedId <- ValueSome message.MessageId

                if chat.messages.Count > chat.historyCapacity then
                    let removed = chat.messages.Dequeue()
                    chat.lastEvictedId <- ValueSome removed.MessageId

                Ok ()

    /// Reads retained messages strictly after the cursor, or starts at the oldest
    /// retained message when no cursor is supplied. Future cursors return an empty page.
    /// HasGap is based on actual removals from this channel, not numerical ID gaps.
    let historyAfter (cursor: ChatMessageId voption) maxCount (chat: Chat) =
        if maxCount <= 0 then
            Error (DomainError.InvalidLimit ("maxCount", maxCount))
        else
            let selected = ResizeArray<ChatMessage>(min maxCount chat.messages.Count)
            let mutable hasMore = false

            for message in chat.messages do
                let followsCursor =
                    match cursor with
                    | ValueNone -> true
                    | ValueSome messageId -> message.MessageId > messageId

                if followsCursor then
                    if selected.Count < maxCount then
                        selected.Add message
                    else
                        hasMore <- true

            let hasGap =
                match cursor, chat.lastEvictedId with
                | ValueSome messageId, ValueSome lastEvicted -> messageId < lastEvicted
                | _ -> false

            let nextCursor =
                if selected.Count = 0 then
                    cursor
                else
                    ValueSome selected[selected.Count - 1].MessageId

            Ok {
                Messages = List.ofSeq selected
                HasGap = hasGap
                HasMore = hasMore
                NextCursor = nextCursor
            }

    /// Removes retained messages but preserves accepted/evicted cursor tracking.
    /// Clearing history does not permit old IDs to be accepted again.
    let clearHistory (chat: Chat) =
        if chat.messages.Count > 0 then
            chat.lastEvictedId <- chat.lastAcceptedId
            chat.messages.Clear()

    let snapshot (chat: Chat) : ChatSnapshot =
        {
            ChannelId = chat.channelId
            Players = membersSnapshot chat
            Messages = List.ofSeq chat.messages
            HistoryCapacity = chat.historyCapacity
        }

/// Named channel backed by a chat owned by the same agent.
[<NoEquality; NoComparison>]
type ChatChannel =
    private {
        channelName: ChatChannelName
        chat: Chat
    }

    member this.ChannelId = this.chat.ChannelId
    member this.ChannelName = this.channelName
    member this.ChannelChat = this.chat

[<RequireQualifiedAccess>]
module ChatChannel =
    let create channelId channelName historyCapacity =
        Chat.create channelId historyCapacity
        |> Result.map (fun chat -> { channelName = channelName; chat = chat })

    /// Changes channel metadata while retaining the same agent-owned chat state.
    let withName channelName (channel: ChatChannel) =
        { channel with channelName = channelName }
