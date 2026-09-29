#nowarn "104" // Unknown enum numbers are guarded; FS0025 still checks every named case.

namespace Dreamsleeve.Server.Core

open System
open Google.Protobuf
open Dreamsleeve.Server.Domain

type private WireSource = Dreamsleeve.Protocol.Chat.AnnouncementSource
type private WireKind = Dreamsleeve.Protocol.Chat.AnnouncementKind
type private WireClientSource = Dreamsleeve.Protocol.Chat.ClientAnnouncementSource

[<RequireQualifiedAccess>]
module internal ChatCodec =
    let private source = function
        | AnnouncementSource.Server -> WireSource.Server
        | AnnouncementSource.TrustedClient -> WireSource.TrustedClient
        | AnnouncementSource.ThirdParty -> WireSource.ThirdParty

    let private kind = function
        | AnnouncementKind.Announcement -> WireKind.Announcement
        | AnnouncementKind.Event -> WireKind.Event
        | AnnouncementKind.Admin -> WireKind.Admin
        | AnnouncementKind.Periodic -> WireKind.Periodic

    let clientSource = function
        | ClientAnnouncementSource.TrustedClient -> WireClientSource.TrustedClient
        | ClientAnnouncementSource.ThirdParty -> WireClientSource.ThirdParty

    let message (value: ChatMessage) =
        let result =
            Dreamsleeve.Protocol.Chat.ChatMessage(
                MessageId = ChatMessageId.value value.MessageId,
                ChannelId = ChatChannelId.value value.ChannelId,
                Text = ChatMessageText.value value.MessageText,
                SentAtUnixMs = Core.toUnixMilliseconds value.SentAt)
        value.Author |> ValueOption.iter (fun author -> result.Author <- PlayerCodec.profile author)
        value.CharacterName |> ValueOption.iter (fun name -> result.CharacterName <- CharacterName.value name)
        for span in value.Flagged do
            result.Flagged.Add(Dreamsleeve.Protocol.Chat.TextSpan(Start = uint32 span.Start, Length = uint32 span.Length))
        value.Announcement |> ValueOption.iter (fun announcement ->
            result.Announcement <-
                Dreamsleeve.Protocol.Chat.Announcement(
                    Source = source announcement.Source,
                    Kind = kind announcement.Kind,
                    Signature = (announcement.Signature |> ValueOption.map AnnouncementSignature.value |> ValueOption.defaultValue "")))
        result

    let channel (value: WelcomeChannel) =
        let result =
            Dreamsleeve.Protocol.Chat.ChatChannel(
                ChannelId = ChatChannelId.value value.ChannelId,
                Kind =
                    match value.Kind with
                    | ChatChannelKind.Global -> Dreamsleeve.Protocol.Chat.ChatChannelKind.Global
                    | ChatChannelKind.System -> Dreamsleeve.Protocol.Chat.ChatChannelKind.System)
        result.RecentMessages.AddRange(value.Messages |> Seq.map message)
        result

    let decodeCommand maxText (source: Dreamsleeve.Protocol.Chat.SendChat) =
        match ChatChannelId.create source.ChannelId, ChatMessageText.create maxText source.Text with
        | Ok channel, Ok text -> Ok(ClientCommand.SendChat(channel, text))
        | Error error, _ | _, Error error -> Error(ProtocolCodecFailure.InvalidDomain error)

    // The text reuses the chat factory; its errors are renamed so the runtime
    // can tell an announcement refusal from a chat one.
    let private announcementText maxText raw =
        ChatMessageText.create maxText raw
        |> Result.mapError (function
            | DomainError.InvalidText(_, error) -> DomainError.InvalidText("AnnouncementText", error)
            | other -> other)

    /// Server-only kinds and unknown numbers are refused before any owner sees
    /// the request. A third-party request must name itself; the label is kept as sent.
    let decodeAnnouncement (limits: ChatInputLimits) (request: Dreamsleeve.Protocol.Chat.PostAnnouncement) =
        let requested =
            match request.Source with
            | WireClientSource.TrustedClient -> Ok ClientAnnouncementSource.TrustedClient
            | WireClientSource.ThirdParty -> Ok ClientAnnouncementSource.ThirdParty
            | WireClientSource.Unspecified -> Error(ProtocolCodecFailure.InvalidPayload "source")
            | unknown when not (Enum.IsDefined unknown) -> Error(ProtocolCodecFailure.InvalidPayload "source")
        let wanted =
            match request.Kind with
            | WireKind.Announcement -> Ok AnnouncementKind.Announcement
            | WireKind.Event -> Ok AnnouncementKind.Event
            | WireKind.Admin | WireKind.Periodic | WireKind.Unspecified -> Error(ProtocolCodecFailure.InvalidPayload "kind")
            | unknown when not (Enum.IsDefined unknown) -> Error(ProtocolCodecFailure.InvalidPayload "kind")
        let signature origin =
            match origin, request.Signature with
            | ClientAnnouncementSource.TrustedClient, "" -> Ok ValueNone
            | ClientAnnouncementSource.ThirdParty, "" -> Error(ProtocolCodecFailure.InvalidDomain(DomainError.InvalidText("AnnouncementSignature", TextError.Missing)))
            | _, raw ->
                AnnouncementSignature.create limits.AnnouncementSignature raw
                |> Result.map ValueSome
                |> Result.mapError ProtocolCodecFailure.InvalidDomain
        requested |> Result.bind (fun origin ->
        wanted |> Result.bind (fun announcementKind ->
        signature origin |> Result.bind (fun label ->
        ChatChannelId.create request.ChannelId |> Result.mapError ProtocolCodecFailure.InvalidDomain |> Result.bind (fun channelId ->
        announcementText limits.AnnouncementText request.Text
        |> Result.mapError ProtocolCodecFailure.InvalidDomain
        |> Result.map (fun text ->
            ClientCommand.PostAnnouncement
                { ChannelId = channelId; Text = text; Kind = announcementKind; Source = origin; Signature = label })))))

    let policy (limits: ChatInputLimits) (sources: ClientAnnouncementSource list) =
        let result =
            Dreamsleeve.Protocol.Chat.AnnouncementPolicy(
                MaxTextLength = uint32 limits.AnnouncementText,
                MaxSignatureLength = uint32 limits.AnnouncementSignature)
        result.AllowedSources.AddRange(sources |> Seq.map clientSource)
        result
