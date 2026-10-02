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
    };

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

      Domain::GuildMember result{std::move(*profile), static_cast<Domain::GuildRole>(source.role()), source.online()};
      if (source.has_mute()) result.mute = Mute(source.mute());
      result.joinedAtUnixMs = source.joined_at_unix_ms();
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
          {guildId, source.name(), source.channel_id(), source.created_at_unix_ms()}
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
        {limits.max_guilds_per_player(), limits.max_members(), limits.name_min_length(), limits.name_max_length()}
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
      case P::GuildChanged::CHANGE_NOT_SET:
        return Invalid("change");
      default:
        return Invalid("change");
    }
  }

}
