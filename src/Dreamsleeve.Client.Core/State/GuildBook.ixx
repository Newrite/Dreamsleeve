export module Dreamsleeve.Client.GuildBook;

import std;

export import Dreamsleeve.Client.Domain.Logic;

export namespace Dreamsleeve::Client
{

  // The player's guilds and invitations as the server described them: one
  // entry per guild ID, one member entry per player. Changes follow the
  // server's order, so a change naming an unknown guild breaks the protocol.
  // The network owner changes a copy and shares it read-only.
  class GuildBook final
  {
public:

    GuildBook() = default;

    // A complete replacement (GuildsSnapshot).
    static Domain::Result<GuildBook> TryCreate(
      std::vector<Domain::Guild>       guilds,
      std::vector<Domain::GuildInvite> invites,
      Domain::GuildLimits              limits)
    {
      GuildBook book;
      book.limits = limits;
      for (auto& guild : guilds)
        if (auto added = book.Add(std::move(guild)); !added) return std::unexpected{std::move(added.error())};
      for (auto& invite : invites)
      {
        if (book.FindInvite(invite.guildId))
          return std::unexpected{
              Domain::Error{Domain::ErrorCode::DuplicateKey, "guild_id"}
          };
        book.invites.push_back(std::move(invite));
      }
      return book;
    }

    std::span<const Domain::Guild> Guilds() const noexcept
    {
      return guilds;
    }

    std::span<const Domain::GuildInvite> Invites() const noexcept
    {
      return invites;
    }

    const Domain::GuildLimits& Limits() const noexcept
    {
      return limits;
    }

    const Domain::Guild* Find(Domain::GuildId guildId) const noexcept
    {
      const auto found = std::ranges::find(guilds, guildId, &Domain::Guild::guildId);
      return found == guilds.end() ? nullptr : &*found;
    }

    const Domain::Guild* FindByChannel(Domain::ChatChannelId channelId) const noexcept
    {
      const auto found = std::ranges::find(guilds, channelId, &Domain::Guild::channelId);
      return found == guilds.end() ? nullptr : &*found;
    }

    const Domain::GuildInvite* FindInvite(Domain::GuildId guildId) const noexcept
    {
      const auto found = std::ranges::find(invites, guildId, &Domain::GuildInvite::guildId);
      return found == invites.end() ? nullptr : &*found;
    }

    // The player created or joined it.
    Domain::OperationResult Add(Domain::Guild guild)
    {
      if (Find(guild.guildId) || FindByChannel(guild.channelId))
        return std::unexpected{
            Domain::Error{Domain::ErrorCode::DuplicateKey, "guild_id"}
        };
      for (auto member = guild.members.begin(); member != guild.members.end(); ++member)
        if (std::ranges::contains(guild.members.begin(), member, member->profile.playerId, MemberId))
          return std::unexpected{
              Domain::Error{Domain::ErrorCode::DuplicatePlayer, "members"}
          };
      guilds.push_back(std::move(guild));
      return {};
    }

    // The player left, was excluded, or the guild was disbanded.
    Domain::OperationResult Remove(Domain::GuildId guildId)
    {
      if (std::erase_if(guilds, [&](const Domain::Guild& guild) { return guild.guildId == guildId; }) == 0) return UnknownGuild();
      return {};
    }

    // A member joined, or their role, mute, online state or name changed.
    Domain::OperationResult PutMember(Domain::GuildId guildId, Domain::GuildMember member)
    {
      auto* guild = FindMutable(guildId);
      if (!guild) return UnknownGuild();
      auto found = std::ranges::find(guild->members, member.profile.playerId, MemberId);
      if (found == guild->members.end())
        guild->members.push_back(std::move(member));
      else
        *found = std::move(member);
      return {};
    }

    Domain::OperationResult RemoveMember(Domain::GuildId guildId, Domain::PlayerId playerId)
    {
      auto* guild = FindMutable(guildId);
      if (!guild) return UnknownGuild();
      if (std::erase_if(guild->members, [&](const Domain::GuildMember& member) { return member.profile.playerId == playerId; }) == 0)
        return std::unexpected{
            Domain::Error{Domain::ErrorCode::UnknownPlayer, "player_id"}
        };
      return {};
    }

    // A new invitation, or a repeated one after the last expired.
    void PutInvite(Domain::GuildInvite invite)
    {
      auto found = std::ranges::find(invites, invite.guildId, &Domain::GuildInvite::guildId);
      if (found == invites.end())
        invites.push_back(std::move(invite));
      else
        *found = std::move(invite);
    }

    // Accepted, declined, expired or disbanded.
    Domain::OperationResult RemoveInvite(Domain::GuildId guildId)
    {
      if (std::erase_if(invites, [&](const Domain::GuildInvite& invite) { return invite.guildId == guildId; }) == 0) return UnknownGuild();
      return {};
    }

    bool operator==(const GuildBook&) const = default;

private:

    static constexpr auto MemberId = [](const Domain::GuildMember& member) {
      return member.profile.playerId;
    };

    static Domain::OperationResult UnknownGuild()
    {
      return std::unexpected{
          Domain::Error{Domain::ErrorCode::UnknownGuild, "guild_id"}
      };
    }

    Domain::Guild* FindMutable(Domain::GuildId guildId) noexcept
    {
      const auto found = std::ranges::find(guilds, guildId, &Domain::Guild::guildId);
      return found == guilds.end() ? nullptr : &*found;
    }

    std::vector<Domain::Guild>       guilds;
    std::vector<Domain::GuildInvite> invites;
    Domain::GuildLimits              limits;
  };

}
