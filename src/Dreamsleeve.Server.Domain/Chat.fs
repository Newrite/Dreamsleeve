namespace Dreamsleeve.Server.Domain

open System
open System.Collections.Generic
open FSharp.UMX

/// A range of message text in UTF-8 bytes, the unit of the wire string.
[<Struct>]
type TextSpan = { Start: int; Length: int }

/// Channel entities. The client "all" view aggregates channels and is not one.
/// Party, guild and direct channels are planned kinds with their own targets.
[<RequireQualifiedAccess>]
type ChatChannelKind =
    /// Players write chat messages.
    | Global
    /// System events: server announcements and admitted client announcements.
    | System

/// Trust origin of an announcement. Only the server assigns it.
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

/// Marks a message of a system channel. The signature is the requesting
/// mod's own label, stored as received; it never raises trust above Source.
type Announcement = {
    Source: AnnouncementSource
    Kind: AnnouncementKind
    Signature: AnnouncementSignature voption
}

[<RequireQualifiedAccess>]
module Announcement =
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

[<RequireQualifiedAccess>]
module ChatChannelKind =
    /// Server-wide kinds exist once, so their channel ID follows from the kind.
    let channelId kind : ChatChannelId =
        match kind with
        | ChatChannelKind.Global -> UMX.tag 1UL
        | ChatChannelKind.System -> UMX.tag 2UL

    let tryOfChannelId (id: ChatChannelId) =
        [ ChatChannelKind.Global; ChatChannelKind.System ]
        |> List.tryFind (fun kind -> channelId kind = id)

    /// A system channel carries only announcements; other channels never do.
    let carriesAnnouncements kind =
        match kind with
        | ChatChannelKind.System -> true
        | ChatChannelKind.Global -> false

/// An immutable message with the author's public identity at the time of
/// sending: the profile, or the pseudonym the author was shown under then.
/// Only a server announcement has no author: the system is not a player.
type ChatMessage =
    private {
        messageId: ChatMessageId
        channelId: ChatChannelId
        author: PublicIdentity voption
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
    /// Present exactly on messages of a system channel.
    member this.Announcement = this.announcement

[<RequireQualifiedAccess>]
module ChatMessage =
    /// Components have already passed their own domain validation.
    /// The server supplies the ID and timestamp; the author's public identity
    /// and character name are snapshots taken at sending and are immutable.
    let create messageId channelId author characterName messageText (sentAt: DateTimeOffset) =
        {
            messageId = messageId
            channelId = channelId
            author = ValueSome author
            characterName = characterName
            messageText = messageText
            sentAt = sentAt.ToUniversalTime()
            flagged = []
            announcement = ValueNone
        }

    /// Spans come from moderation of this exact text.
    let withFlagged spans (message: ChatMessage) = { message with flagged = spans }

    /// A client announcement keeps its author: the player whose client asked for it.
    let withAnnouncement announcement (message: ChatMessage) = { message with announcement = ValueSome announcement }

    /// Written by the server itself, without an author.
    let serverAnnouncement messageId channelId kind messageText (sentAt: DateTimeOffset) =
        {
            messageId = messageId
            channelId = channelId
            author = ValueNone
            characterName = ValueNone
            messageText = messageText
            sentAt = sentAt.ToUniversalTime()
            flagged = []
            announcement = ValueSome(Announcement.server kind)
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
    Kind: ChatChannelKind
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
        kind: ChatChannelKind
        historyCapacity: int
        players: HashSet<PlayerId>
        messages: Queue<ChatMessage>
        mutable lastAcceptedId: ChatMessageId voption
        mutable lastEvictedId: ChatMessageId voption
    }

    member this.ChannelId = this.channelId
    member this.Kind = this.kind
    member this.HistoryCapacity = this.historyCapacity

[<RequireQualifiedAccess>]
module Chat =
    /// The channel ID follows from the kind; see ChatChannelKind.channelId.
    let create kind historyCapacity =
        if historyCapacity <= 0 then
            Error (DomainError.InvalidLimit ("historyCapacity", historyCapacity))
        else
            Ok {
                channelId = ChatChannelKind.channelId kind
                kind = kind
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
    /// A system channel takes only announcements, other kinds only chat. A server
    /// announcement has no author and needs no membership. Validation failures
    /// leave membership and history unchanged.
    let append (message: ChatMessage) (chat: Chat) =
        let outsider =
            match message.Author with
            | ValueSome author when not (chat.players.Contains author.PlayerId) -> ValueSome author.PlayerId
            | ValueSome _ | ValueNone -> ValueNone
        if message.ChannelId <> chat.channelId
           || message.Announcement.IsSome <> ChatChannelKind.carriesAnnouncements chat.kind then
            Error DomainError.ChannelMismatch
        elif outsider.IsSome then
            Error (DomainError.NotChatMember outsider.Value)
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

    /// A moderator's removal of one retained message; absent when it is not
    /// retained (never accepted, evicted or removed already). Its ID is never
    /// accepted again, and history pages report no gap for it: nothing was
    /// lost that a reader should fetch.
    let remove messageId (chat: Chat) =
        match chat.messages |> Seq.tryFind (fun message -> message.MessageId = messageId) with
        | None -> ValueNone
        | Some removed ->
            let kept = chat.messages |> Seq.filter (fun message -> message.MessageId <> messageId) |> Seq.toArray
            chat.messages.Clear()
            for message in kept do chat.messages.Enqueue message
            ValueSome removed

    /// Removes retained messages but preserves accepted/evicted cursor tracking.
    /// Clearing history does not permit old IDs to be accepted again.
    let clearHistory (chat: Chat) =
        if chat.messages.Count > 0 then
            chat.lastEvictedId <- chat.lastAcceptedId
            chat.messages.Clear()

    let snapshot (chat: Chat) : ChatSnapshot =
        {
            ChannelId = chat.channelId
            Kind = chat.kind
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
    let create kind channelName historyCapacity =
        Chat.create kind historyCapacity
        |> Result.map (fun chat -> { channelName = channelName; chat = chat })

    /// Changes channel metadata while retaining the same agent-owned chat state.
    let withName channelName (channel: ChatChannel) =
        { channel with channelName = channelName }
