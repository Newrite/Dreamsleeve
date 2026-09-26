export module Dreamsleeve.Client.PlayerStore;

export import Dreamsleeve.Client.Domain.Logic;

import std;

export namespace Dreamsleeve::Client
{

  using namespace Domain;

  // The network thread owns this store. All calls, including const reads, must
  // stay on that owner; Find/Snapshot return detached values for publication.
  class PlayerStore final
  {
public:

    PlayerStore() = default;

    PlayerStore(const PlayerStore&)            = delete;
    PlayerStore& operator=(const PlayerStore&) = delete;
    PlayerStore(PlayerStore&&)                 = default;
    PlayerStore& operator=(PlayerStore&&)      = default;

    // A complete server snapshot replaces every field, including absent game
    // data. The result distinguishes a newly online player from a replacement.
    bool Upsert(Player player)
    {
      const auto id = player.data.playerId;
      return players.insert_or_assign(id, std::move(player)).second;
    }

    // An authoritative online snapshot removes players absent from the input.
    // Build a replacement first so duplicate IDs leave the store intact.
    Domain::OperationResult ReplaceAll(std::span<const Player> snapshot)
    {
      std::unordered_map<PlayerId, Player> replacement;
      replacement.reserve(snapshot.size());

      for (const auto& player : snapshot)
      {
        const auto id = player.data.playerId;
        if (!replacement.try_emplace(id, player).second)
        {
          return std::unexpected{
              Domain::Error{Domain::ErrorCode::DuplicatePlayer, "playerId"}
          };
        }
      }

      players.swap(replacement);
      return {};
    }

    bool Remove(PlayerId id)
    {
      return players.erase(id) != 0;
    }

    void Clear() noexcept
    {
      players.clear();
    }

    std::size_t Count() const noexcept
    {
      return players.size();
    }

    bool Contains(PlayerId id) const
    {
      return players.contains(id);
    }

    std::optional<Player> Find(PlayerId id) const
    {
      const auto found = players.find(id);
      if (found == players.end())
      {
        return std::nullopt;
      }

      return found->second;
    }

    std::vector<Player> Snapshot() const
    {
      std::vector<Player> snapshot;
      snapshot.reserve(players.size());

      for (const auto& [id, player] : players)
      {
        snapshot.push_back(player);
      }

      std::ranges::sort(snapshot, {}, [](const Player& player) { return player.data.playerId; });
      return snapshot;
    }

    Domain::OperationResult UpdateProfile(PlayerId id, PlayerData profile)
    {
      const auto found = players.find(id);
      if (found == players.end())
      {
        return UnknownPlayer();
      }

      if (profile.playerId != id)
      {
        return std::unexpected{
            Domain::Error{Domain::ErrorCode::IdentityMismatch, "playerId"}
        };
      }

      found->second.data = std::move(profile);
      return {};
    }

    Domain::OperationResult UpdateLocation(PlayerId id, std::optional<PlayerLocation> location)
    {
      const auto found = players.find(id);
      if (found == players.end())
      {
        return UnknownPlayer();
      }

      // Position updates are frequent; replace only their payload.
      found->second.location = std::move(location);
      return {};
    }

    Domain::OperationResult RenameCharacter(PlayerId id, std::optional<CharacterName> characterName)
    {
      const auto found = players.find(id);
      if (found == players.end())
      {
        return UnknownPlayer();
      }

      found->second.characterName = std::move(characterName);
      return {};
    }

    Domain::OperationResult BeginCharacter(PlayerId id, CharacterName name)
    {
      const auto found = players.find(id);
      if (found == players.end())
      {
        return UnknownPlayer();
      }

      Domain::Players::BeginCharacter(found->second, std::move(name));
      return {};
    }

    Domain::OperationResult ClearGameState(PlayerId id)
    {
      const auto found = players.find(id);
      if (found == players.end())
      {
        return UnknownPlayer();
      }

      Domain::Players::ClearGameState(found->second);
      return {};
    }

    // Apply one delta atomically with respect to local consistency errors.
    // Keys arrive canonicalized by the server and are matched exactly as sent.
    Domain::OperationResult ApplyActorValues(PlayerId id, ActorValueStorage values, std::span<const ActorValueKey> removedKeys = {})
    {
      const auto found = players.find(id);
      if (found == players.end())
      {
        return UnknownPlayer();
      }

      if (values.empty() && removedKeys.empty())
      {
        return {};
      }

      for (const auto& key : removedKeys)
      {
        if (values.contains(key))
        {
          return DuplicateKey();
        }
      }

      auto&       target = found->second.actorValues;
      std::size_t newCount{};
      for (const auto& [key, value] : values)
      {
        if (!target.contains(key))
        {
          ++newCount;
        }
      }

      if (newCount != 0)
      {
        if (newCount > target.max_size() - target.size())
        {
          throw std::length_error{"Actor-value delta exceeds storage capacity"};
        }

        // Reserve before removing or replacing anything. Allocation failures
        // propagate as standard exceptions and leave stored values unchanged.
        target.reserve(target.size() + newCount);
      }

      // Only the incoming delta is staged. Existing entries are move-assigned;
      // new entries reuse preallocated nodes, with rehash prevented above.
      // The storage uses std::allocator and the nonthrowing string hash; this
      // assertion also prevents adding a throwing value move to the commit.
      static_assert(std::is_nothrow_move_assignable_v<ActorValueInfo>);

      // Repeated removals are idempotent. No extra removal set is needed.
      for (const auto& key : removedKeys)
      {
        target.erase(key);
      }

      while (!values.empty())
      {
        const auto incoming = values.begin();
        const auto existing = target.find(incoming->first);
        if (existing != target.end())
        {
          existing->second = std::move(incoming->second);
          values.erase(incoming);
        }
        else
        {
          target.insert(values.extract(incoming));
        }
      }

      return {};
    }

private:

    static Domain::OperationResult UnknownPlayer()
    {
      return std::unexpected{
          Domain::Error{Domain::ErrorCode::UnknownPlayer, "playerId"}
      };
    }

    static Domain::OperationResult DuplicateKey()
    {
      return std::unexpected{
          Domain::Error{Domain::ErrorCode::DuplicateKey, "actorValues"}
      };
    }

    std::unordered_map<PlayerId, Player> players;
  };

}
