module;
#include "protocol.pb.h"

module Dreamsleeve.Client.ProtocolCodec;
#include "CodecParts.h"

// Unknown wire values are handled; every newly generated named case must be listed.
#pragma warning(error : 4061 4062)

namespace Dreamsleeve::Client::Wire::Detail
{

  namespace
  {

    // No catch-all overload: adding a GuildAction alternative must fail to compile.
    struct GuildWriter
    {
      P::GuildCommand& target;

      void operator()(const CreateGuild& value) const
      {
        target.mutable_create()->set_name(value.name);
      }

      void operator()(const InviteToGuild& value) const
      {
        auto& invite = *target.mutable_invite();
        invite.set_guild_id(value.guildId);
        invite.set_player_id(value.playerId);
      }

      void operator()(const AnswerGuildInvite& value) const
      {
        auto& answer = *target.mutable_answer();
        answer.set_guild_id(value.guildId);
        answer.set_accept(value.accept);
      }

      void operator()(const LeaveGuild& value) const
      {
        target.mutable_leave()->set_guild_id(value.guildId);
      }

      void operator()(const ExcludeGuildMember& value) const
      {
        auto& exclude = *target.mutable_exclude();
        exclude.set_guild_id(value.guildId);
        exclude.set_player_id(value.playerId);
      }

      void operator()(const SetGuildRole& value) const
      {
        auto& role = *target.mutable_set_role();
        role.set_guild_id(value.guildId);
        role.set_player_id(value.playerId);
        role.set_role(static_cast<P::GuildRole>(value.role));
      }

      void operator()(const TransferGuild& value) const
      {
        auto& transfer = *target.mutable_transfer();
        transfer.set_guild_id(value.guildId);
        transfer.set_player_id(value.playerId);
      }

      void operator()(const MuteGuildMember& value) const
      {
        auto& mute = *target.mutable_mute();
        mute.set_guild_id(value.guildId);
        mute.set_player_id(value.playerId);
        if (value.minutes) mute.set_minutes(*value.minutes);
        mute.set_reason(value.reason);
      }

      void operator()(const UnmuteGuildMember& value) const
      {
        auto& unmute = *target.mutable_unmute();
        unmute.set_guild_id(value.guildId);
        unmute.set_player_id(value.playerId);
      }

      void operator()(const DisbandGuild& value) const
      {
        target.mutable_disband()->set_guild_id(value.guildId);
      }

      void operator()(const CreateGuildTitle& value) const
      {
        auto& create = *target.mutable_create_title();
        create.set_guild_id(value.guildId);
        create.set_name(value.name);
      }

      void operator()(const RenameGuildTitle& value) const
      {
        auto& rename = *target.mutable_rename_title();
        rename.set_guild_id(value.guildId);
        rename.set_title_id(value.titleId);
        rename.set_name(value.name);
      }

      void operator()(const DeleteGuildTitle& value) const
      {
        auto& removal = *target.mutable_delete_title();
        removal.set_guild_id(value.guildId);
        removal.set_title_id(value.titleId);
      }

      void operator()(const SetGuildMemberTitle& value) const
      {
        auto& assignment = *target.mutable_set_title();
        assignment.set_guild_id(value.guildId);
        assignment.set_player_id(value.playerId);
        if (value.titleId) assignment.set_title_id(*value.titleId);
      }

      void operator()(const SetGuildInfo& value) const
      {
        auto& info = *target.mutable_set_info();
        info.set_guild_id(value.guildId);
        info.set_info(value.info);
      }

      void operator()(const SetGuildListed& value) const
      {
        auto& listed = *target.mutable_set_listed();
        listed.set_guild_id(value.guildId);
        listed.set_listed(value.listed);
      }

      void operator()(const ApplyToGuild& value) const
      {
        target.mutable_apply()->set_guild_id(value.guildId);
      }

      void operator()(const WithdrawGuildApplication& value) const
      {
        target.mutable_withdraw()->set_guild_id(value.guildId);
      }

      void operator()(const AnswerGuildJoinRequest& value) const
      {
        auto& answer = *target.mutable_answer_request();
        answer.set_guild_id(value.guildId);
        answer.set_player_id(value.playerId);
        answer.set_accept(value.accept);
      }
    };

    Result<Domain::GuildApplicationEnd> ApplicationEnd(int value)
    {
      if (!P::GuildApplicationEnd_IsValid(value) || value == P::GUILD_APPLICATION_END_UNSPECIFIED) return Invalid("reason");
      return static_cast<Domain::GuildApplicationEnd>(value);
    }

    // Like a member's: the real profile, never a pseudonym.
    Result<std::vector<Domain::GuildJoinRequest>> JoinRequests(const google::protobuf::RepeatedPtrField<P::GuildJoinRequest>& source)
    {
      std::vector<Domain::GuildJoinRequest> result;
      result.reserve(static_cast<std::size_t>(source.size()));
      for (const auto& request : source)
      {
        if (!request.has_profile()) return Invalid("profile");
        auto profile = Profile(request.profile());
        if (!profile) return std::unexpected{profile.error()};
        if (profile->pseudonymous) return Invalid("pseudonymous");
        if (std::ranges::contains(result, profile->playerId, [](const auto& entry) { return entry.profile.playerId; }))
          return Invalid("profile");
        if (!ValidUnixMs(request.created_at_unix_ms()) || !ValidUnixMs(request.expires_at_unix_ms())) return Invalid("expires_at_unix_ms");
        result.push_back({std::move(*profile), request.created_at_unix_ms(), request.expires_at_unix_ms()});
      }
      return result;
    }

    Result<Domain::GuildApplication> Application(const P::GuildApplication& source)
    {
      if (source.guild_id() == Domain::InvalidId) return Invalid("guild_id");
      if (source.guild_name().empty()) return Invalid("guild_name");
      if (!ValidUnixMs(source.expires_at_unix_ms())) return Invalid("expires_at_unix_ms");
      return Domain::GuildApplication{source.guild_id(), source.guild_name(), source.expires_at_unix_ms()};
    }

    Result<Domain::GuildRemovalReason> Removal(int value)
    {
      if (!P::GuildRemovalReason_IsValid(value) || value == P::GUILD_REMOVAL_REASON_UNSPECIFIED) return Invalid("reason");
      return static_cast<Domain::GuildRemovalReason>(value);
    }

    // Guildmates see the real profile, never a pseudonym.
    Result<Domain::GuildMember> Member(const P::GuildMember& source)
    {
      if (!source.has_profile()) return Invalid("profile");
      auto profile = Profile(source.profile());
      if (!profile) return std::unexpected{profile.error()};
      if (profile->pseudonymous) return Invalid("pseudonymous");
      if (!P::GuildRole_IsValid(source.role()) || source.role() == P::GUILD_ROLE_UNSPECIFIED) return Invalid("role");
      if (!ValidUnixMs(source.joined_at_unix_ms())) return Invalid("joined_at_unix_ms");

      if (source.has_title_id() && source.title_id() == Domain::InvalidId) return Invalid("title_id");

      Domain::GuildMember result{std::move(*profile), static_cast<Domain::GuildRole>(source.role()), source.online()};
      if (source.has_mute()) result.mute = Mute(source.mute());
      result.joinedAtUnixMs = source.joined_at_unix_ms();
      if (source.has_title_id()) result.titleId = source.title_id();
      return result;
    }

    // A guild's custom roles: each ID once, each with a name.
    Result<std::vector<Domain::GuildTitle>> Titles(const google::protobuf::RepeatedPtrField<P::GuildTitle>& source)
    {
      std::vector<Domain::GuildTitle> result;
      result.reserve(static_cast<std::size_t>(source.size()));
      for (const auto& title : source)
      {
        if (title.title_id() == Domain::InvalidId || std::ranges::contains(result, title.title_id(), &Domain::GuildTitle::titleId))
          return Invalid("title_id");
        if (title.name().empty()) return Invalid("name");
        result.push_back({title.title_id(), title.name()});
      }
      return result;
    }

    // The channel follows from the guild; the model checks the messages
    // against the channel when it merges them.
    Result<GuildOpened> Opened(const Configuration& config, const P::Guild& source)
    {
      const auto guildId = source.guild_id();
      if (guildId == Domain::InvalidId || guildId > std::numeric_limits<std::uint64_t>::max() - Domain::GuildChannelBase)
        return Invalid("guild_id");
      if (source.channel_id() != Domain::GuildChannelBase + guildId) return Invalid("channel_id");
      if (source.name().empty()) return Invalid("name");
      if (!ValidUnixMs(source.created_at_unix_ms())) return Invalid("created_at_unix_ms");
      if (static_cast<std::size_t>(source.recent_messages_size()) > config.maxRecentMessages) return Invalid("initial_count");

      GuildOpened result{
          {guildId, source.name(), source.channel_id(), source.created_at_unix_ms(), {}, {}, source.info()}
      };
      for (const auto& value : source.members())
      {
        auto member = Member(value);
        if (!member) return std::unexpected{member.error()};
        result.guild.members.push_back(std::move(*member));
      }
      for (const auto& value : source.recent_messages())
      {
        auto message = Message(value);
        if (!message) return std::unexpected{message.error()};
        result.recentMessages.push_back(std::move(*message));
      }
      auto titles = Titles(source.titles());
      if (!titles) return std::unexpected{titles.error()};
      result.guild.titles = std::move(*titles);
      auto requests       = JoinRequests(source.join_requests());
      if (!requests) return std::unexpected{requests.error()};
      result.guild.joinRequests = std::move(*requests);
      result.guild.listed       = source.listed();
      return result;
    }

    Result<Domain::GuildInvite> Invite(const P::GuildInvite& source)
    {
      if (source.guild_id() == Domain::InvalidId) return Invalid("guild_id");
      if (source.guild_name().empty()) return Invalid("guild_name");
      if (source.invited_by_player_id() == Domain::InvalidId) return Invalid("invited_by_player_id");
      if (!ValidUnixMs(source.expires_at_unix_ms())) return Invalid("expires_at_unix_ms");
      return Domain::GuildInvite{source.guild_id(), source.guild_name(), source.invited_by_player_id(), source.expires_at_unix_ms()};
    }

  }

  void WriteGuild(P::GuildCommand& target, const GuildAction& action)
  {
    std::visit(GuildWriter{target}, action);
  }

  Result<GuildsSnapshot> ReadGuilds(const Configuration& config, const P::GuildsSnapshot& source)
  {
    if (!source.has_limits()) return Invalid("limits");
    const auto&    limits = source.limits();
    GuildsSnapshot result{
        {},
        {},
        {limits.max_guilds_per_player(),
         limits.max_members(),
         limits.name_min_length(),
         limits.name_max_length(),
         limits.max_titles(),
         limits.info_max_length(),
         limits.max_join_requests(),
         limits.max_applications()}
    };
    for (const auto& value : source.guilds())
    {
      auto guild = Opened(config, value);
      if (!guild) return std::unexpected{guild.error()};
      result.guilds.push_back(std::move(*guild));
    }
    for (const auto& value : source.invites())
    {
      auto invite = Invite(value);
      if (!invite) return std::unexpected{invite.error()};
      result.invites.push_back(std::move(*invite));
    }
    for (const auto& value : source.applications())
    {
      auto application = Application(value);
      if (!application) return std::unexpected{application.error()};
      if (std::ranges::contains(result.applications, application->guildId, &Domain::GuildApplication::guildId)) return Invalid("guild_id");
      result.applications.push_back(std::move(*application));
    }
    return result;
  }

  void WriteGuildCatalog(P::GuildCommand& target, const Domain::GuildCatalogQuery& query)
  {
    auto& catalog = *target.mutable_catalog();
    catalog.set_search(query.search);
    catalog.set_min_members(query.minMembers);
    if (query.maxMembers) catalog.set_max_members(*query.maxMembers);
    catalog.set_open_only(query.openOnly);
    catalog.set_sort(static_cast<P::GuildCatalogSort>(query.sort));
    if (query.after)
    {
      auto& after = *catalog.mutable_after();
      after.set_guild_id(query.after->guildId);
      after.set_name(query.after->name);
      after.set_members(query.after->members);
    }
  }

  Result<Domain::GuildCatalogPage> ReadGuildCatalog(const P::GuildCatalogPage& source)
  {
    Domain::GuildCatalogPage result{{}, source.more(), source.total()};
    result.guilds.reserve(static_cast<std::size_t>(source.guilds_size()));
    for (const auto& entry : source.guilds())
    {
      if (entry.guild_id() == Domain::InvalidId) return Invalid("guild_id");
      if (entry.name().empty()) return Invalid("name");
      if (!ValidUnixMs(entry.created_at_unix_ms())) return Invalid("created_at_unix_ms");
      result.guilds.push_back({entry.guild_id(), entry.name(), entry.info(), entry.members(), entry.created_at_unix_ms()});
    }
    if (result.guilds.size() > result.total) return Invalid("total");
    return result;
  }

  Result<GuildChanged> ReadGuildChanged(const Configuration& config, const P::GuildChanged& source)
  {
    switch (source.change_case())
    {
      case P::GuildChanged::kAdded: {
        auto guild = Opened(config, source.added());
        if (!guild) return std::unexpected{guild.error()};
        return GuildChanged{GuildAdded{std::move(*guild)}};
      }
      case P::GuildChanged::kRemoved: {
        const auto& removed = source.removed();
        if (removed.guild_id() == Domain::InvalidId) return Invalid("guild_id");
        auto reason = Removal(removed.reason());
        if (!reason) return std::unexpected{reason.error()};
        return GuildChanged{
            GuildRemoved{removed.guild_id(), *reason}
        };
      }
      case P::GuildChanged::kMember: {
        const auto& updated = source.member();
        if (updated.guild_id() == Domain::InvalidId) return Invalid("guild_id");
        if (!updated.has_member()) return Invalid("member");
        auto member = Member(updated.member());
        if (!member) return std::unexpected{member.error()};
        return GuildChanged{
            GuildMemberUpdated{updated.guild_id(), std::move(*member)}
        };
      }
      case P::GuildChanged::kMemberRemoved: {
        const auto& removed = source.member_removed();
        if (removed.guild_id() == Domain::InvalidId) return Invalid("guild_id");
        if (removed.player_id() == Domain::InvalidId) return Invalid("player_id");
        auto reason = Removal(removed.reason());
        if (!reason) return std::unexpected{reason.error()};
        return GuildChanged{
            GuildMemberRemoved{removed.guild_id(), removed.player_id(), *reason}
        };
      }
      case P::GuildChanged::kInvited: {
        auto invite = Invite(source.invited());
        if (!invite) return std::unexpected{invite.error()};
        return GuildChanged{GuildInvited{std::move(*invite)}};
      }
      case P::GuildChanged::kInviteRemoved:
        if (source.invite_removed() == Domain::InvalidId) return Invalid("invite_removed");
        return GuildChanged{GuildInviteRemoved{source.invite_removed()}};
      case P::GuildChanged::kTitles: {
        const auto& changed = source.titles();
        if (changed.guild_id() == Domain::InvalidId) return Invalid("guild_id");
        auto titles = Titles(changed.titles());
        if (!titles) return std::unexpected{titles.error()};
        return GuildChanged{
            GuildTitlesChanged{changed.guild_id(), std::move(*titles)}
        };
      }
      case P::GuildChanged::kInfo: {
        const auto& changed = source.info();
        if (changed.guild_id() == Domain::InvalidId) return Invalid("guild_id");
        return GuildChanged{
            GuildInfoChanged{changed.guild_id(), changed.info()}
        };
      }
      case P::GuildChanged::kJoinRequests: {
        const auto& changed = source.join_requests();
        if (changed.guild_id() == Domain::InvalidId) return Invalid("guild_id");
        auto requests = JoinRequests(changed.requests());
        if (!requests) return std::unexpected{requests.error()};
        return GuildChanged{
            GuildJoinRequestsChanged{changed.guild_id(), std::move(*requests)}
        };
      }
      case P::GuildChanged::kApplied: {
        auto application = Application(source.applied());
        if (!application) return std::unexpected{application.error()};
        return GuildChanged{GuildApplied{std::move(*application)}};
      }
      case P::GuildChanged::kApplicationRemoved: {
        const auto& removed = source.application_removed();
        if (removed.guild_id() == Domain::InvalidId) return Invalid("guild_id");
        auto reason = ApplicationEnd(removed.reason());
        if (!reason) return std::unexpected{reason.error()};
        return GuildChanged{
            GuildApplicationRemoved{removed.guild_id(), *reason}
        };
      }
      case P::GuildChanged::kListed: {
        const auto& listed = source.listed();
        if (listed.guild_id() == Domain::InvalidId) return Invalid("guild_id");
        return GuildChanged{
            GuildListedChanged{listed.guild_id(), listed.listed()}
        };
      }
      case P::GuildChanged::CHANGE_NOT_SET:
        return Invalid("change");
      default:
        return Invalid("change");
    }
  }

}
