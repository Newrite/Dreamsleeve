namespace Dreamsleeve.Server.Domain

open System
open System.Collections.Generic
open FSharp.UMX

/// A range of message text in UTF-8 bytes, the unit of the wire string.
[<Struct>]
type TextSpan = { Start: int; Length: int }

/// Trust origin of a system-stream message. Only the server assigns it.
[<RequireQualifiedAccess>]
type AnnouncementSource =
    /// Configuration, schedule or administrator; never accepted from a client.
    | Server
    /// Requested by the Dreamsleeve client itself.
    | TrustedClient
    /// Requested by another mod through the client API.
    | ThirdParty

/// Origins a client may claim. Server is not representable in a request.
[<RequireQualifiedAccess>]
type ClientAnnouncementSource =
    | TrustedClient
    | ThirdParty

[<RequireQualifiedAccess>]
type AnnouncementKind =
    | Announcement
    | Event
    /// Administrator notice; server only.
    | Admin
    /// Scheduled repetition; server only.
    | Periodic

/// Marks a message of the system stream. The signature is the requesting mod's
/// own label, stored as received; it never raises trust above Source.
type Announcement = {
    Source: AnnouncementSource
    Kind: AnnouncementKind
    Signature: AnnouncementSignature voption
}

[<RequireQualifiedAccess>]
module Announcement =
    /// Account IDs are positive SQLite integers, so the top uint64 never names a player.
    let serverAuthorId: PlayerId = UMX.tag UInt64.MaxValue

    /// Author profile of server announcements. Older clients, unaware of the
    /// announcement field, show such messages as written by this profile.
    let serverAuthor (serverName: DisplayName) =
        PlayerData.create serverAuthorId (UMX.tag "server") serverName

    let server kind = { Source = AnnouncementSource.Server; Kind = kind; Signature = ValueNone }

    let fromClient source kind signature =
        let origin =
            match source with
            | ClientAnnouncementSource.TrustedClient -> AnnouncementSource.TrustedClient
            | ClientAnnouncementSource.ThirdParty -> AnnouncementSource.ThirdParty
        { Source = origin; Kind = kind; Signature = signature }

    /// Kinds a client may request; administrator and scheduled notices belong to the server.
    let clientMayRequest kind =
        match kind with
        | AnnouncementKind.Announcement | AnnouncementKind.Event -> true
        | AnnouncementKind.Admin | AnnouncementKind.Periodic -> false

/// An immutable message with the author's profile at the time of sending.
type ChatMessage =
    private {
        messageId: ChatMessageId
        channelId: ChatChannelId
        author: PlayerData
        characterName: CharacterName voption
        messageText: ChatMessageText
        sentAt: DateTimeOffset
        flagged: TextSpan list
        announcement: Announcement voption
    }

    member this.MessageId = this.messageId
    member this.ChannelId = this.channelId
    member this.Author = this.author
    /// Published character name when the message was sent; never updated later.
    member this.CharacterName = this.characterName
    member this.MessageText = this.messageText
    member this.SentAt = this.sentAt
    /// Ranges the server word list marks without refusing the message; ascending,
    /// non-overlapping. Clients decide whether to show, mask or hide them.
    member this.Flagged = this.flagged
    /// Present on messages of the system stream; absent on player chat.
    member this.Announcement = this.announcement

[<RequireQualifiedAccess>]
module ChatMessage =
    /// Components have already passed their own domain validation.
    /// The server supplies the ID and timestamp; the author's profile and
    /// character name are snapshots taken at sending and are immutable.
    let create messageId channelId author characterName messageText (sentAt: DateTimeOffset) =
        {
            messageId = messageId
            channelId = channelId
            author = author
            characterName = characterName
            messageText = messageText
            sentAt = sentAt.ToUniversalTime()
            flagged = []
            announcement = ValueNone
        }

    /// Spans come from moderation of this exact text.
    let withFlagged spans (message: ChatMessage) = { message with flagged = spans }

    /// Moves the message into the system stream of its channel.
    let withAnnouncement announcement (message: ChatMessage) = { message with announcement = ValueSome announcement }

    /// Server announcements are written by the reserved server profile, not a member.
    let isServerAnnouncement (message: ChatMessage) =
        match message.announcement with
        | ValueSome announcement -> announcement.Source = AnnouncementSource.Server
        | ValueNone -> false

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
    /// Server announcements share the history and IDs without membership.
    /// Validation failures leave membership and history unchanged.
    let append (message: ChatMessage) (chat: Chat) =
        if message.ChannelId <> chat.channelId then
            Error DomainError.ChannelMismatch
        elif not (ChatMessage.isServerAnnouncement message) && not (chat.players.Contains message.Author.PlayerId) then
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

            use mutable messages = chat.messages.GetEnumerator()

            while not hasMore && messages.MoveNext() do
                let message = messages.Current

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
