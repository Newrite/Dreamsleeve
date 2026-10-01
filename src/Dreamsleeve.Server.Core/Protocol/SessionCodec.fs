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
        && PlayerCodec.numberedPlayers value.Kinds value.Players
        && not channels.IsEmpty && Set.count (Set.ofList channels) = channels.Length
        && List.forall validChannel value.Channels

    let decodeTicket (source: Dreamsleeve.Protocol.Chat.OpenSession) =
        let ticket = source.SessionTicket
        if isNull ticket || ticket.Length <> 43
           || ticket |> Seq.exists (fun ch -> not (Char.IsAsciiLetterOrDigit ch || ch = '-' || ch = '_')) then
            Error(ProtocolCodecFailure.InvalidPayload "session_ticket")
        else decodeHiding "hidden_identity" source.HiddenIdentity |> Result.map (fun value -> ClientCommand.OpenSession(ticket, value))

    let decodeDisplayName (config: ServerConfig) (source: Dreamsleeve.Protocol.Chat.ChangeDisplayName) =
        DisplayName.create config.ChatInput.DisplayName source.DisplayName
        |> Result.map ClientCommand.ChangeDisplayName
        |> Result.mapError ProtocolCodecFailure.InvalidDomain

    let private untilMs (expires: DateTimeOffset voption) = expires |> ValueOption.map _.ToUnixTimeMilliseconds()

    /// A mute as its player reads it: the reason and when it ends.
    let mute (sanction: Sanction) =
        let state = Dreamsleeve.Protocol.Chat.MuteState(Reason = SanctionReason.value sanction.Reason)
        untilMs sanction.Expires |> ValueOption.iter (fun until -> state.UntilUnixMs <- until)
        state

    let ended (value: SessionEnd) =
        let result = Dreamsleeve.Protocol.Chat.SessionEnded()
        match value with
        | SessionEnd.AccessRevoked -> result.Reason <- Dreamsleeve.Protocol.Chat.SessionEndReason.AccessRevoked
        | SessionEnd.Banned ban ->
            result.Reason <- Dreamsleeve.Protocol.Chat.SessionEndReason.Banned
            result.Text <- SanctionReason.value ban.Reason
            untilMs ban.Expires |> ValueOption.iter (fun until -> result.UntilUnixMs <- until)
        | SessionEnd.Kicked reason ->
            result.Reason <- Dreamsleeve.Protocol.Chat.SessionEndReason.Kicked
            result.Text <- SanctionReason.value reason
        | SessionEnd.AddressBanned ban ->
            result.Reason <- Dreamsleeve.Protocol.Chat.SessionEndReason.AddressBanned
            result.Text <- SanctionReason.value ban.Reason
            untilMs ban.Expires |> ValueOption.iter (fun until -> result.UntilUnixMs <- until)
        result

    let welcome (config: ServerConfig) (value: SessionWelcome) =
        let result = Dreamsleeve.Protocol.Chat.SessionOpened(
            ServerName = config.ServerName,
            SelfPlayerId = PlayerId.value value.SelfPlayerId,
            Announcements = ChatCodec.policy config.ChatInput value.AnnouncementSources)
        value.OwnPseudonym |> ValueOption.iter (fun name -> result.OwnPseudonym <- Pseudonym.value name)
        value.Mute |> ValueOption.iter (fun sanction -> result.Mute <- mute sanction)
        result.Role <- ModerationCodec.role value.Role
        result.HiddenIdentity <- hiding value.Hiding
        result.ActorValueKinds.AddRange(value.Kinds.Defined |> Seq.map PlayerCodec.kind)
        result.Players.AddRange(value.Players |> Seq.map (PlayerCodec.player value.Kinds))
        result.Channels.AddRange(value.Channels |> Seq.map ChatCodec.channel)
        result
