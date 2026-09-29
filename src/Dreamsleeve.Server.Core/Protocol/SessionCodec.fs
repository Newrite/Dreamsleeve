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

        let ordered =
            value.RecentMessages |> List.pairwise
            |> List.forall (fun (left, right) -> left.MessageId < right.MessageId)

        value.Players.Length <= config.MaxInitialPlayers && value.RecentMessages.Length <= config.MaxRecentMessages
        && Set.count (Set.ofList ids) = ids.Length && List.contains value.SelfPlayerId ids
        && ordered && (value.RecentMessages |> List.forall (fun msg -> msg.ChannelId = value.GlobalChannelId))

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
            GlobalChannelId = ChatChannelId.value value.GlobalChannelId)
        result.Players.AddRange(value.Players |> Seq.map PlayerCodec.player)
        result.RecentMessages.AddRange(value.RecentMessages |> Seq.map ChatCodec.message)
        // Always present: it also tells the client this server accepts PostAnnouncement.
        result.Announcements <- ChatCodec.policy config.ChatInput value.AnnouncementSources
        result
