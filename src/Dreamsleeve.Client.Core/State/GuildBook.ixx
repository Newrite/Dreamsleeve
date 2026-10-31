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

    // A guild the player is no longer in, and why, at the revision it left.
    struct Removal
    {
      std::uint64_t              revision{};
      Domain::GuildId            guildId{};
      std::string                name;
      Domain::GuildRemovalReason reason{Domain::GuildRemovalReason::Left};

      bool operator==(const Removal&) const = default;
    };

    // A join request of the player that ended, and why, at the revision it ended.
    struct ApplicationEnd
    {
      std::uint64_t               revision{};
      Domain::GuildId             guildId{};
      std::string                 name;
      Domain::GuildApplicationEnd reason{Domain::GuildApplicationEnd::Withdrawn};

      bool operator==(const ApplicationEnd&) const = default;
    };

    // Removals and ended requests kept: a consumer that reads every few frames misses none.
    static constexpr std::size_t RecentRemovals = 16;

    GuildBook() = default;

    // A complete replacement (GuildsSnapshot).
    static Domain::Result<GuildBook> TryCreate(
      std::vector<Domain::Guild>            guilds,
      std::vector<Domain::GuildInvite>      invites,
      Domain::GuildLimits                   limits,
      std::vector<Domain::GuildApplication> applications = {})
    {
      GuildBook book;
      book.limits       = limits;
      book.applications = std::move(applications);
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

      book.revision = 0;
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

    // The player's pending join requests, oldest first.
    std::span<const Domain::GuildApplication> Applications() const noexcept
    {
      return applications;
    }

    // The latest ended join requests, oldest first, like Removals.
    std::span<const ApplicationEnd> ApplicationEnds() const noexcept
    {
      return applicationEnds;
    }

    const Domain::GuildApplication* FindApplication(Domain::GuildId guildId) const noexcept
    {
      const auto found = std::ranges::find(applications, guildId, &Domain::GuildApplication::guildId);
      return found == applications.end() ? nullptr : &*found;
    }

    // Grows with every change; a replacement starts again from zero.
    std::uint64_t Revision() const noexcept
    {
      return revision;
    }

    // The latest removals, oldest first: those above the revision a consumer
    // saw last are news to it.
    std::span<const Removal> Removals() const noexcept
    {
      return removals;
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
      ++revision;
      return {};
    }

    // The player left, was excluded, or the guild was disbanded.
    Domain::OperationResult Remove(Domain::GuildId guildId, Domain::GuildRemovalReason reason)
    {
      const auto found = std::ranges::find(guilds, guildId, &Domain::Guild::guildId);
      if (found == guilds.end()) return UnknownGuild();

      if (removals.size() == RecentRemovals) removals.erase(removals.begin());
      removals.push_back({++revision, guildId, std::move(found->name), reason});
      guilds.erase(found);
      return {};
    }

    // The guild's custom roles, complete; a deleted role's holders came before.
    Domain::OperationResult PutTitles(Domain::GuildId guildId, std::vector<Domain::GuildTitle> titles)
    {
      auto* guild = FindMutable(guildId);
      if (!guild) return UnknownGuild();
      guild->titles = std::move(titles);
      ++revision;
      return {};
    }

    // The guild's pending join requests, complete.
    Domain::OperationResult PutJoinRequests(Domain::GuildId guildId, std::vector<Domain::GuildJoinRequest> requests)
    {
      auto* guild = FindMutable(guildId);
      if (!guild) return UnknownGuild();
      guild->joinRequests = std::move(requests);
      ++revision;
      return {};
    }

    Domain::OperationResult PutListed(Domain::GuildId guildId, bool listed)
    {
      auto* guild = FindMutable(guildId);
      if (!guild) return UnknownGuild();
      guild->listed = listed;
      ++revision;
      return {};
    }

    // The player asked to join a guild.
    Domain::OperationResult PutApplication(Domain::GuildApplication application)
    {
      if (FindApplication(application.guildId))
        return std::unexpected{
            Domain::Error{Domain::ErrorCode::DuplicateKey, "guild_id"}
        };
      applications.push_back(std::move(application));
      ++revision;
      return {};
    }

    // A join request of the player ended: withdrawn, answered, expired or the guild disbanded.
    Domain::OperationResult RemoveApplication(Domain::GuildId guildId, Domain::GuildApplicationEnd reason)
    {
      const auto found = std::ranges::find(applications, guildId, &Domain::GuildApplication::guildId);
      if (found == applications.end()) return UnknownGuild();
      if (applicationEnds.size() == RecentRemovals) applicationEnds.erase(applicationEnds.begin());
      applicationEnds.push_back({++revision, guildId, std::move(found->guildName), reason});
      applications.erase(found);
      return {};
    }

    // The master wrote or cleared (empty) the guild information.
    Domain::OperationResult PutInfo(Domain::GuildId guildId, std::string info)
    {
      auto* guild = FindMutable(guildId);
      if (!guild) return UnknownGuild();
      guild->info = std::move(info);
      ++revision;
      return {};
    }

    // A member joined, or their role, custom role, mute, online state or name changed.
    Domain::OperationResult PutMember(Domain::GuildId guildId, Domain::GuildMember member)
    {
      auto* guild = FindMutable(guildId);
      if (!guild) return UnknownGuild();

      auto found = std::ranges::find(guild->members, member.profile.playerId, MemberId);
      if (found == guild->members.end())
        guild->members.push_back(std::move(member));
      else
        *found = std::move(member);
      ++revision;
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
      ++revision;
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
      ++revision;
    }

    // Accepted, declined, expired or disbanded.
    Domain::OperationResult RemoveInvite(Domain::GuildId guildId)
    {
      if (std::erase_if(invites, [&](const Domain::GuildInvite& invite) { return invite.guildId == guildId; }) == 0) return UnknownGuild();
      ++revision;
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

    std::vector<Domain::Guild>            guilds;
    std::vector<Domain::GuildInvite>      invites;
    Domain::GuildLimits                   limits;
    std::uint64_t                         revision{};
    std::vector<Removal>                  removals;
    std::vector<Domain::GuildApplication> applications;
    std::vector<ApplicationEnd>           applicationEnds;
  };

}
