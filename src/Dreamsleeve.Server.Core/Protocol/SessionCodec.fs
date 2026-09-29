namespace Dreamsleeve.Server.Core

open System
open Google.Protobuf
open Dreamsleeve.Server.Domain

[<RequireQualifiedAccess>]
module internal SessionCodec =
    // Domain factories establish scalar invariants; only cross-field consistency
    // and configured collection budgets remain to check here.
    let validWelcome (config: ServerConfig) (value: SessionWelcome) =
        let ids = value.Players |> List.map (fun player -> player.Data.PlayerId)
        let channels = value.Channels |> List.map _.ChannelId

        // Each tail belongs to its channel, ascends and fits the per-channel budget;
        // only a system channel carries announcements.
        let validChannel (channel: WelcomeChannel) =
            let messages = channel.Messages
            messages.Length <= config.MaxRecentMessages
            && (messages |> List.pairwise |> List.forall (fun (left, right) -> left.MessageId < right.MessageId))
            && (messages |> List.forall (fun message ->
                    message.ChannelId = channel.ChannelId
                    && message.Announcement.IsSome = ChatChannelKind.carriesAnnouncements channel.Kind))

        value.Players.Length <= config.MaxInitialPlayers
        && Set.count (Set.ofList ids) = ids.Length && List.contains value.SelfPlayerId ids
        && not channels.IsEmpty && Set.count (Set.ofList channels) = channels.Length
        && List.forall validChannel value.Channels

    let decodeTicket (source: Dreamsleeve.Protocol.Chat.OpenSession) =
        let ticket = source.SessionTicket
        if isNull ticket || ticket.Length <> 43
           || ticket |> Seq.exists (fun ch -> not (Char.IsAsciiLetterOrDigit ch || ch = '-' || ch = '_')) then
            Error(ProtocolCodecFailure.InvalidPayload "session_ticket")
        else Ok(ClientCommand.OpenSession ticket)

    let welcome (config: ServerConfig) (value: SessionWelcome) =
        let result = Dreamsleeve.Protocol.Chat.SessionOpened(
            ServerName = config.ServerName,
            SelfPlayerId = PlayerId.value value.SelfPlayerId,
            Announcements = ChatCodec.policy config.ChatInput value.AnnouncementSources)
        result.Players.AddRange(value.Players |> Seq.map PlayerCodec.player)
        result.Channels.AddRange(value.Channels |> Seq.map ChatCodec.channel)
        result
