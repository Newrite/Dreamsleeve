#nowarn "104" // Unknown enum numbers are guarded; FS0025 still checks every named case.

namespace Dreamsleeve.Server.Core

open System
open Dreamsleeve.Server.Domain

type private WireGuildRole = Dreamsleeve.Protocol.Chat.GuildRole
type private WireRemoval = Dreamsleeve.Protocol.Chat.GuildRemovalReason
type private WireAction = Dreamsleeve.Protocol.Chat.GuildCommand.ActionOneofCase

/// Guild messages (guild.proto). Who may do what is the guild owner's: the
/// codec checks shapes and IDs, the owner the name, roles and limits.
[<RequireQualifiedAccess>]
module internal GuildCodec =
    let role = function
        | GuildRole.Member -> WireGuildRole.Member
        | GuildRole.Officer -> WireGuildRole.Officer
        | GuildRole.Master -> WireGuildRole.Master

    let removal = function
        | GuildRemoval.Left -> WireRemoval.Left
        | GuildRemoval.Excluded -> WireRemoval.Excluded
        | GuildRemoval.Disbanded -> WireRemoval.Disbanded

    // Only these two are assigned; the master's role passes by TransferGuild.
    let private decodeRole (value: WireGuildRole) =
        match value with
        | WireGuildRole.Member -> Ok GuildRole.Member
        | WireGuildRole.Officer -> Ok GuildRole.Officer
        | WireGuildRole.Master | WireGuildRole.Unspecified -> Error(ProtocolCodecFailure.InvalidPayload "role")
        | unknown when not (Enum.IsDefined unknown) -> Error(ProtocolCodecFailure.InvalidPayload "role")

    let private guild raw = GuildId.create raw |> Result.mapError ProtocolCodecFailure.InvalidDomain
    let private player raw = PlayerId.create raw |> Result.mapError ProtocolCodecFailure.InvalidDomain

    let private pair guildRaw playerRaw make =
        match guild guildRaw, player playerRaw with
        | Ok guild, Ok player -> Ok(make guild player)
        | Error error, _ | _, Error error -> Error error

    let decode (source: Dreamsleeve.Protocol.Chat.GuildCommand) =
        let action =
            match source.ActionCase with
            | WireAction.Create -> Ok(GuildAction.Create source.Create.Name)
            | WireAction.Invite -> pair source.Invite.GuildId source.Invite.PlayerId (fun guild player -> GuildAction.Invite(guild, player))
            | WireAction.Answer -> guild source.Answer.GuildId |> Result.map (fun guild -> GuildAction.Answer(guild, source.Answer.Accept))
            | WireAction.Leave -> guild source.Leave.GuildId |> Result.map GuildAction.Leave
            | WireAction.Exclude -> pair source.Exclude.GuildId source.Exclude.PlayerId (fun guild player -> GuildAction.Exclude(guild, player))
            | WireAction.SetRole ->
                pair source.SetRole.GuildId source.SetRole.PlayerId (fun guild player -> struct (guild, player))
                |> Result.bind (fun (struct (guild, player)) ->
                    decodeRole source.SetRole.Role |> Result.map (fun role -> GuildAction.SetRole(guild, player, role)))
            | WireAction.Transfer -> pair source.Transfer.GuildId source.Transfer.PlayerId (fun guild player -> GuildAction.Transfer(guild, player))
            | WireAction.Mute ->
                let mute = source.Mute
                let minutes =
                    if not mute.HasMinutes then Ok ValueNone
                    elif mute.Minutes > uint32 Int32.MaxValue then Error(ProtocolCodecFailure.InvalidPayload "minutes")
                    else Ok(ValueSome(int mute.Minutes))
                match pair mute.GuildId mute.PlayerId (fun guild player -> struct (guild, player)), minutes,
                      SanctionReason.create mute.Reason |> Result.mapError ProtocolCodecFailure.InvalidDomain with
                | Ok(struct (guild, player)), Ok minutes, Ok reason ->
                    SanctionTerm.create minutes
                    |> Result.mapError ProtocolCodecFailure.InvalidDomain
                    |> Result.map (fun term -> GuildAction.Mute(guild, player, term, reason))
                | Error error, _, _ | _, Error error, _ | _, _, Error error -> Error error
            | WireAction.Unmute -> pair source.Unmute.GuildId source.Unmute.PlayerId (fun guild player -> GuildAction.Unmute(guild, player))
            | WireAction.Disband -> guild source.Disband.GuildId |> Result.map GuildAction.Disband
            | WireAction.None -> Error(ProtocolCodecFailure.InvalidPayload "action")
            | unknown when not (Enum.IsDefined unknown) -> Error(ProtocolCodecFailure.InvalidPayload "action")
        action |> Result.map ClientCommand.Guild

    let private unixMs (time: DateTimeOffset) = time.ToUnixTimeMilliseconds()

    let memberOf (view: GuildMemberView) =
        let result =
            Dreamsleeve.Protocol.Chat.GuildMember(
                Profile = PlayerCodec.profile (PublicIdentity.Profile view.Profile),
                Role = role view.Membership.Role,
                Online = view.Online,
                JoinedAtUnixMs = unixMs view.Membership.JoinedAt)
        view.Membership.Mute |> ValueOption.iter (fun mute ->
            let state = Dreamsleeve.Protocol.Chat.MuteState(Reason = SanctionReason.value mute.Reason)
            mute.Expires |> ValueOption.iter (fun until -> state.UntilUnixMs <- unixMs until)
            result.Mute <- state)
        result

    let guildOf (view: GuildView) =
        let result =
            Dreamsleeve.Protocol.Chat.Guild(
                GuildId = GuildId.value view.Guild,
                Name = GuildName.value view.Name,
                ChannelId = ChatChannelId.value view.ChannelId,
                CreatedAtUnixMs = unixMs view.CreatedAt)
        result.Members.AddRange(view.Members |> Seq.map memberOf)
        result.RecentMessages.AddRange(view.Messages |> Seq.map ChatCodec.message)
        result

    let inviteOf (view: GuildInviteView) =
        Dreamsleeve.Protocol.Chat.GuildInvite(
            GuildId = GuildId.value view.Invite.Guild,
            GuildName = GuildName.value view.GuildName,
            InvitedByPlayerId = PlayerId.value view.Invite.InvitedBy,
            ExpiresAtUnixMs = unixMs view.Invite.Expires)

    let snapshot (state: GuildState) =
        let result = Dreamsleeve.Protocol.Chat.GuildsSnapshot()
        result.Guilds.AddRange(state.Guilds |> Seq.map guildOf)
        result.Invites.AddRange(state.Invites |> Seq.map inviteOf)
        result.Limits <-
            Dreamsleeve.Protocol.Chat.GuildLimits(
                MaxGuildsPerPlayer = uint32 state.Limits.MaxGuildsPerPlayer,
                MaxMembers = uint32 state.Limits.MaxMembers,
                NameMinLength = uint32 state.Limits.NameMinLength,
                NameMaxLength = uint32 state.Limits.NameMaxLength)
        result

    let changed (change: GuildChange) =
        let result = Dreamsleeve.Protocol.Chat.GuildChanged()
        match change with
        | GuildChange.Added view -> result.Added <- guildOf view
        | GuildChange.Removed(guild, reason) ->
            result.Removed <- Dreamsleeve.Protocol.Chat.GuildRemoved(GuildId = GuildId.value guild, Reason = removal reason)
        | GuildChange.MemberChanged(guild, view) ->
            result.Member <- Dreamsleeve.Protocol.Chat.GuildMemberUpdate(GuildId = GuildId.value guild, Member = memberOf view)
        | GuildChange.MemberRemoved(guild, player, reason) ->
            result.MemberRemoved <-
                Dreamsleeve.Protocol.Chat.GuildMemberRemoved(GuildId = GuildId.value guild, PlayerId = PlayerId.value player, Reason = removal reason)
        | GuildChange.Invited view -> result.Invited <- inviteOf view
        | GuildChange.InviteRemoved guild -> result.InviteRemoved <- GuildId.value guild
        result

    // A guild's tail belongs to its channel, ascends and fits the per-channel budget.
    let private validGuild (config: ServerConfig) (view: GuildView) =
        view.ChannelId = ChatChannels.ofGuild view.Guild
        && view.Messages.Length <= config.MaxRecentMessages
        && (view.Messages |> List.pairwise |> List.forall (fun (left, right) -> left.MessageId < right.MessageId))
        && (view.Messages |> List.forall (fun message -> message.ChannelId = view.ChannelId && message.Announcement.IsNone))

    let validSnapshot config (state: GuildState) = state.Guilds |> List.forall (validGuild config)

    let validChange config (change: GuildChange) =
        match change with
        | GuildChange.Added view -> validGuild config view
        | GuildChange.Removed _ | GuildChange.MemberChanged _ | GuildChange.MemberRemoved _
        | GuildChange.Invited _ | GuildChange.InviteRemoved _ -> true
