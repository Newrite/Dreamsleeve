#nowarn "104" // Unknown enum numbers are guarded; FS0025 still checks every named case.

namespace Dreamsleeve.Server.Core

open System
open Dreamsleeve.Server.Domain

type private WireGuildRole = Dreamsleeve.Protocol.Chat.GuildRole
type private WireRemoval = Dreamsleeve.Protocol.Chat.GuildRemovalReason
type private WireAction = Dreamsleeve.Protocol.Chat.GuildCommand.ActionOneofCase
type private WireSort = Dreamsleeve.Protocol.Chat.GuildCatalogSort
type private WireApplicationEnd = Dreamsleeve.Protocol.Chat.GuildApplicationEnd

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
    let private title raw = GuildTitleId.create raw |> Result.mapError ProtocolCodecFailure.InvalidDomain

    // A catalog search or cursor name compares with names in lower case; one
    // longer than any guild name can match nothing and is malformed.
    let private nameKey field (raw: string) =
        let key = raw.Trim().Normalize(Text.NormalizationForm.FormC).ToLowerInvariant()
        if Seq.length (key.EnumerateRunes()) > GuildName.MaxLength then Error(ProtocolCodecFailure.InvalidPayload field)
        else Ok key

    let private count field (raw: uint32) =
        if raw > uint32 Int32.MaxValue then Error(ProtocolCodecFailure.InvalidPayload field) else Ok(int raw)

    let private catalogSort (value: WireSort) =
        match value with
        | WireSort.Name -> Ok GuildCatalogSort.Name
        | WireSort.Members -> Ok GuildCatalogSort.Members
        | WireSort.Unspecified -> Error(ProtocolCodecFailure.InvalidPayload "sort")
        | unknown when not (Enum.IsDefined unknown) -> Error(ProtocolCodecFailure.InvalidPayload "sort")

    let private catalogAfter (after: Dreamsleeve.Protocol.Chat.GuildCatalogAfter) =
        if isNull after then Ok ValueNone
        else
            match guild after.GuildId, nameKey "after" after.Name, count "after" after.Members with
            | Ok guild, Ok key, Ok members ->
                Ok(ValueSome {
                    Guild = guild
                    NameKey = key
                    Members = members
                })
            | Error error, _, _ | _, Error error, _ | _, _, Error error -> Error error

    let private catalogQuery (query: Dreamsleeve.Protocol.Chat.QueryGuildCatalog) =
        let maxMembers =
            if not query.HasMaxMembers then Ok ValueNone
            elif query.MaxMembers < query.MinMembers then Error(ProtocolCodecFailure.InvalidPayload "max_members")
            else count "max_members" query.MaxMembers |> Result.map ValueSome
        match nameKey "search" query.Search, count "min_members" query.MinMembers, maxMembers, catalogSort query.Sort, catalogAfter query.After with
        | Ok search, Ok minimum, Ok maximum, Ok sort, Ok after ->
            Ok {
                Search = search
                MinMembers = minimum
                MaxMembers = maximum
                OpenOnly = query.OpenOnly
                Sort = sort
                After = after
            }
        | Error error, _, _, _, _ | _, Error error, _, _, _ | _, _, Error error, _, _ | _, _, _, Error error, _ | _, _, _, _, Error error ->
            Error error

    let private pair guildRaw playerRaw make =
        match guild guildRaw, player playerRaw with
        | Ok guild, Ok player -> Ok(make guild player)
        | Error error, _ | _, Error error -> Error error

    let decode (source: Dreamsleeve.Protocol.Chat.GuildCommand) =
        let action =
            match source.ActionCase with
            | WireAction.Create -> Ok(GuildAction.Create source.Create.Name)
            | WireAction.Invite ->
                pair source.Invite.GuildId source.Invite.PlayerId (fun guild player -> GuildAction.Invite(guild, player))
            | WireAction.Answer ->
                guild source.Answer.GuildId
                |> Result.map (fun guild -> GuildAction.Answer(guild, source.Answer.Accept))
            | WireAction.Leave -> guild source.Leave.GuildId |> Result.map GuildAction.Leave
            | WireAction.Exclude ->
                pair source.Exclude.GuildId source.Exclude.PlayerId (fun guild player -> GuildAction.Exclude(guild, player))
            | WireAction.SetRole ->
                pair source.SetRole.GuildId source.SetRole.PlayerId (fun guild player -> struct (guild, player))
                |> Result.bind (fun (struct (guild, player)) ->
                    decodeRole source.SetRole.Role |> Result.map (fun role -> GuildAction.SetRole(guild, player, role)))
            | WireAction.Transfer ->
                pair source.Transfer.GuildId source.Transfer.PlayerId (fun guild player -> GuildAction.Transfer(guild, player))
            | WireAction.Mute ->
                let mute = source.Mute
                let minutes =
                    if not mute.HasMinutes then
                        Ok ValueNone
                    elif mute.Minutes > uint32 Int32.MaxValue then
                        Error(ProtocolCodecFailure.InvalidPayload "minutes")
                    else
                        Ok(ValueSome(int mute.Minutes))
                match pair mute.GuildId mute.PlayerId (fun guild player -> struct (guild, player)), minutes,
                      SanctionReason.create mute.Reason |> Result.mapError ProtocolCodecFailure.InvalidDomain with
                | Ok(struct (guild, player)), Ok minutes, Ok reason ->
                    SanctionTerm.create minutes
                    |> Result.mapError ProtocolCodecFailure.InvalidDomain
                    |> Result.map (fun term -> GuildAction.Mute(guild, player, term, reason))
                | Error error, _, _
                | _, Error error, _
                | _, _, Error error -> Error error
            | WireAction.Unmute ->
                pair source.Unmute.GuildId source.Unmute.PlayerId (fun guild player -> GuildAction.Unmute(guild, player))
            | WireAction.Disband -> guild source.Disband.GuildId |> Result.map GuildAction.Disband
            | WireAction.CreateTitle ->
                guild source.CreateTitle.GuildId |> Result.map (fun guild -> GuildAction.CreateTitle(guild, source.CreateTitle.Name))
            | WireAction.RenameTitle ->
                let rename = source.RenameTitle
                match guild rename.GuildId, title rename.TitleId with
                | Ok guild, Ok title -> Ok(GuildAction.RenameTitle(guild, title, rename.Name))
                | Error error, _ | _, Error error -> Error error
            | WireAction.DeleteTitle ->
                match guild source.DeleteTitle.GuildId, title source.DeleteTitle.TitleId with
                | Ok guild, Ok title -> Ok(GuildAction.DeleteTitle(guild, title))
                | Error error, _ | _, Error error -> Error error
            | WireAction.SetTitle ->
                let assignment = source.SetTitle
                let assigned = if assignment.HasTitleId then title assignment.TitleId |> Result.map ValueSome else Ok ValueNone
                match pair assignment.GuildId assignment.PlayerId (fun guild player -> struct (guild, player)), assigned with
                | Ok(struct (guild, player)), Ok assigned -> Ok(GuildAction.SetTitle(guild, player, assigned))
                | Error error, _ | _, Error error -> Error error
            | WireAction.SetInfo -> guild source.SetInfo.GuildId |> Result.map (fun guild -> GuildAction.SetInfo(guild, source.SetInfo.Info))
            | WireAction.SetListed ->
                guild source.SetListed.GuildId |> Result.map (fun guild -> GuildAction.SetListed(guild, source.SetListed.Listed))
            | WireAction.Apply -> guild source.Apply.GuildId |> Result.map GuildAction.Apply
            | WireAction.Withdraw -> guild source.Withdraw.GuildId |> Result.map GuildAction.Withdraw
            | WireAction.AnswerRequest ->
                let answer = source.AnswerRequest
                pair answer.GuildId answer.PlayerId (fun guild player -> GuildAction.AnswerRequest(guild, player, answer.Accept))
            | WireAction.Catalog -> catalogQuery source.Catalog |> Result.map GuildAction.Catalog
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
        view.Membership.Title |> ValueOption.iter (fun title -> result.TitleId <- GuildTitleId.value title)
        view.Membership.Mute |> ValueOption.iter (fun mute ->
            let state = Dreamsleeve.Protocol.Chat.MuteState(Reason = SanctionReason.value mute.Reason)
            mute.Expires |> ValueOption.iter (fun until -> state.UntilUnixMs <- unixMs until)
            result.Mute <- state)

        result

    let requestOf (view: GuildJoinRequestView) =
        Dreamsleeve.Protocol.Chat.GuildJoinRequest(
            Profile = PlayerCodec.profile (PublicIdentity.Profile view.Profile),
            CreatedAtUnixMs = unixMs view.Request.CreatedAt,
            ExpiresAtUnixMs = unixMs view.Request.Expires)

    let applicationOf (view: GuildApplicationView) =
        Dreamsleeve.Protocol.Chat.GuildApplication(
            GuildId = GuildId.value view.Request.Guild,
            GuildName = GuildName.value view.GuildName,
            ExpiresAtUnixMs = unixMs view.Request.Expires)

    let private applicationEnd = function
        | GuildApplicationEnd.Withdrawn -> WireApplicationEnd.Withdrawn
        | GuildApplicationEnd.Accepted -> WireApplicationEnd.Accepted
        | GuildApplicationEnd.Rejected -> WireApplicationEnd.Rejected
        | GuildApplicationEnd.Expired -> WireApplicationEnd.Expired
        | GuildApplicationEnd.Disbanded -> WireApplicationEnd.Disbanded

    let titleOf (title: GuildTitle) =
        Dreamsleeve.Protocol.Chat.GuildTitle(TitleId = GuildTitleId.value title.Id, Name = GuildTitleName.value title.Name)

    let private infoText (info: GuildInfo voption) =
        match info with
        | ValueSome text -> GuildInfo.value text
        | ValueNone -> ""

    let guildOf (view: GuildView) =
        let result =
            Dreamsleeve.Protocol.Chat.Guild(
                GuildId = GuildId.value view.Guild,
                Name = GuildName.value view.Name,
                ChannelId = ChatChannelId.value view.ChannelId,
                CreatedAtUnixMs = unixMs view.CreatedAt,
                Info = infoText view.Info,
                Listed = view.Listed)
        result.Members.AddRange(view.Members |> Seq.map memberOf)
        result.RecentMessages.AddRange(view.Messages |> Seq.map ChatCodec.message)
        result.Titles.AddRange(view.Titles |> Seq.map titleOf)
        result.JoinRequests.AddRange(view.JoinRequests |> Seq.map requestOf)
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
        result.Applications.AddRange(state.Applications |> Seq.map applicationOf)
        result.Limits <-
            Dreamsleeve.Protocol.Chat.GuildLimits(
                MaxGuildsPerPlayer = uint32 state.Limits.MaxGuildsPerPlayer,
                MaxMembers = uint32 state.Limits.MaxMembers,
                NameMinLength = uint32 state.Limits.NameMinLength,
                NameMaxLength = uint32 state.Limits.NameMaxLength,
                MaxTitles = uint32 state.Limits.MaxTitles,
                InfoMaxLength = uint32 state.Limits.InfoMaxLength,
                MaxJoinRequests = uint32 state.Limits.MaxJoinRequests,
                MaxApplications = uint32 state.Limits.MaxApplicationsPerPlayer)
        result

    let changed (change: GuildChange) =
        let result = Dreamsleeve.Protocol.Chat.GuildChanged()
        match change with
        | GuildChange.Added view -> result.Added <- guildOf view
        | GuildChange.Removed(guild, reason) ->
            result.Removed <-
                Dreamsleeve.Protocol.Chat.GuildRemoved(
                    GuildId = GuildId.value guild,
                    Reason = removal reason)
        | GuildChange.MemberChanged(guild, view) ->
            result.Member <-
                Dreamsleeve.Protocol.Chat.GuildMemberUpdate(
                    GuildId = GuildId.value guild,
                    Member = memberOf view)
        | GuildChange.MemberRemoved(guild, player, reason) ->
            result.MemberRemoved <-
                Dreamsleeve.Protocol.Chat.GuildMemberRemoved(
                    GuildId = GuildId.value guild,
                    PlayerId = PlayerId.value player,
                    Reason = removal reason)
        | GuildChange.Invited view -> result.Invited <- inviteOf view
        | GuildChange.InviteRemoved guild -> result.InviteRemoved <- GuildId.value guild
        | GuildChange.Titles(guild, titles) ->
            let list = Dreamsleeve.Protocol.Chat.GuildTitles(GuildId = GuildId.value guild)
            list.Titles.AddRange(titles |> Seq.map titleOf)
            result.Titles <- list
        | GuildChange.Info(guild, info) ->
            result.Info <- Dreamsleeve.Protocol.Chat.GuildInfoChanged(GuildId = GuildId.value guild, Info = infoText info)
        | GuildChange.JoinRequests(guild, requests) ->
            let list = Dreamsleeve.Protocol.Chat.GuildJoinRequests(GuildId = GuildId.value guild)
            list.Requests.AddRange(requests |> Seq.map requestOf)
            result.JoinRequests <- list
        | GuildChange.Applied view -> result.Applied <- applicationOf view
        | GuildChange.ApplicationRemoved(guild, reason) ->
            result.ApplicationRemoved <-
                Dreamsleeve.Protocol.Chat.GuildApplicationRemoved(GuildId = GuildId.value guild, Reason = applicationEnd reason)
        | GuildChange.Listed(guild, listed) ->
            result.Listed <- Dreamsleeve.Protocol.Chat.GuildListedChanged(GuildId = GuildId.value guild, Listed = listed)
        result

    let catalogPage (view: GuildCatalogView) =
        let result = Dreamsleeve.Protocol.Chat.GuildCatalogPage(More = view.More, Total = uint32 view.Total)
        result.Guilds.AddRange(
            view.Guilds
            |> Seq.map (fun entry ->
                Dreamsleeve.Protocol.Chat.GuildCatalogEntry(
                    GuildId = GuildId.value entry.Guild,
                    Name = GuildName.value entry.Name,
                    Info = infoText entry.Info,
                    Members = uint32 entry.Members,
                    CreatedAtUnixMs = unixMs entry.CreatedAt)))
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
        | GuildChange.Invited _ | GuildChange.InviteRemoved _ | GuildChange.Titles _ | GuildChange.Info _
        | GuildChange.JoinRequests _ | GuildChange.Applied _ | GuildChange.ApplicationRemoved _ | GuildChange.Listed _ -> true
