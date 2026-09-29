#nowarn "104" // Unknown enum numbers are guarded; FS0025 still checks every named case.

namespace Dreamsleeve.Server.Core

open System
open Google.Protobuf
open Dreamsleeve.Server.Domain

type private WireHiding = Dreamsleeve.Protocol.Chat.HiddenIdentity

[<RequireQualifiedAccess>]
module internal SessionCodec =
    let hiding = function
        | HiddenIdentity.Shown -> WireHiding.None
        | HiddenIdentity.Everywhere -> WireHiding.Everywhere
        | HiddenIdentity.ExceptGroundMarks -> WireHiding.ExceptGroundMarks

    let decodeHiding field (value: WireHiding) =
        match value with
        | WireHiding.None -> Ok HiddenIdentity.Shown
        | WireHiding.Everywhere -> Ok HiddenIdentity.Everywhere
        | WireHiding.ExceptGroundMarks -> Ok HiddenIdentity.ExceptGroundMarks
        | unknown when not (Enum.IsDefined unknown) -> Error(ProtocolCodecFailure.InvalidPayload field)

    // Domain factories establish scalar invariants; only cross-field consistency
    // and configured collection budgets remain to check here.
    let validWelcome (config: ServerConfig) (value: SessionWelcome) =
        let ids = value.Players |> List.map (fun player -> player.Identity.PlayerId)
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

        // The receiver sees its own real profile; only copies for others carry a pseudonym.
        let selfShown =
            value.Players |> List.exists (fun player ->
                match player.Identity with
                | PublicIdentity.Profile profile -> profile.PlayerId = value.SelfPlayerId
                | PublicIdentity.Pseudonymous _ -> false)

        value.Players.Length <= config.MaxInitialPlayers
        && Set.count (Set.ofList ids) = ids.Length && selfShown
        && not channels.IsEmpty && Set.count (Set.ofList channels) = channels.Length
        && List.forall validChannel value.Channels

    let decodeTicket (source: Dreamsleeve.Protocol.Chat.OpenSession) =
        let ticket = source.SessionTicket
        if isNull ticket || ticket.Length <> 43
           || ticket |> Seq.exists (fun ch -> not (Char.IsAsciiLetterOrDigit ch || ch = '-' || ch = '_')) then
            Error(ProtocolCodecFailure.InvalidPayload "session_ticket")
        else decodeHiding "hidden_identity" source.HiddenIdentity |> Result.map (fun value -> ClientCommand.OpenSession(ticket, value))

    let welcome (config: ServerConfig) (value: SessionWelcome) =
        let result = Dreamsleeve.Protocol.Chat.SessionOpened(
            ServerName = config.ServerName,
            SelfPlayerId = PlayerId.value value.SelfPlayerId,
            Announcements = ChatCodec.policy config.ChatInput value.AnnouncementSources)
        value.OwnPseudonym |> ValueOption.iter (fun name -> result.OwnPseudonym <- Pseudonym.value name)
        result.HiddenIdentity <- hiding value.Hiding
        result.Players.AddRange(value.Players |> Seq.map PlayerCodec.player)
        result.Channels.AddRange(value.Channels |> Seq.map ChatCodec.channel)
        result
