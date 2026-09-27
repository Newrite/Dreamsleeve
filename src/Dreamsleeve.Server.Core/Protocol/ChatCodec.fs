namespace Dreamsleeve.Server.Core

open System
open Google.Protobuf
open Dreamsleeve.Server.Domain

[<RequireQualifiedAccess>]
module internal ChatCodec =
    let message (value: ChatMessage) =
        Dreamsleeve.Protocol.Chat.ChatMessage(
            MessageId = ChatMessageId.value value.MessageId,
            ChannelId = ChatChannelId.value value.ChannelId,
            Author = PlayerCodec.profile value.Author,
            Text = ChatMessageText.value value.MessageText,
            SentAtUnixMs = Core.toUnixMilliseconds value.SentAt)

    let decodeCommand maxText (source: Dreamsleeve.Protocol.Chat.SendChat) =
        match ChatChannelId.create source.ChannelId, ChatMessageText.create maxText source.Text with
        | Ok channel, Ok text -> Ok(ClientCommand.SendChat(channel, text))
        | Error error, _ | _, Error error -> Error(ProtocolCodecFailure.InvalidDomain error)
