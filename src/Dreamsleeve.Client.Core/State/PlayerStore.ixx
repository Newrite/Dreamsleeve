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
    Domain::Result<bool> Upsert(Player player)
    {
      auto normalized = Domain::Normalize::PlayerValue(std::move(player));
      if (!normalized)
      {
        return std::unexpected{std::move(normalized.error())};
      }

      const auto id = normalized->data.playerId;
      return players.insert_or_assign(id, std::move(*normalized)).second;
    }

    // An authoritative online snapshot removes players absent from the input.
    // Build a replacement first so validation failures leave the store intact.
    Domain::OperationResult ReplaceAll(std::span<const Player> snapshot)
    {
      std::unordered_map<PlayerId, Player> replacement;
      replacement.reserve(snapshot.size());

      for (const auto& player : snapshot)
      {
        auto normalized = Domain::Normalize::PlayerValue(player);
        if (!normalized)
        {
          return std::unexpected{std::move(normalized.error())};
        }

        const auto id = normalized->data.playerId;
        if (!replacement.try_emplace(id, std::move(*normalized)).second)
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

      auto normalized = Domain::Normalize::Profile(std::move(profile));
      if (!normalized)
      {
        return std::unexpected{std::move(normalized.error())};
      }

      found->second.data = std::move(*normalized);
      return {};
    }

    Domain::OperationResult UpdateLocation(PlayerId id, std::optional<PlayerLocation> location)
    {
      const auto found = players.find(id);
      if (found == players.end())
      {
        return UnknownPlayer();
      }

      if (location)
      {
        auto& pluginName = location->location.locationId.pluginName;
        pluginName       = Domain::Normalize::PluginName(pluginName);

        auto valid = Domain::Validation::Validate(*location);
        if (!valid)
        {
          return valid;
        }
      }

      // Position updates are frequent: validate their own payload instead of
      // copying the player's profile and potentially large actor-value map.
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

      // Character metadata changes are infrequent. A staged value lets the
      // common domain validation enforce all rules before the commit.
      auto staged          = found->second;
      staged.characterName = std::move(characterName);
      return Commit(found->second, std::move(staged));
    }

    Domain::OperationResult BeginCharacter(PlayerId id, CharacterName name)
    {
      const auto found = players.find(id);
      if (found == players.end())
      {
        return UnknownPlayer();
      }

      Player staged{.data = found->second.data};
      auto   result = Domain::Players::BeginCharacter(staged, std::move(name));
      if (!result)
      {
        return result;
      }

      return Commit(found->second, std::move(staged));
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

    // Apply one delta atomically with respect to domain errors. Canonicalize
    // keys before matching so casing cannot create or remove a second entry.
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

      ActorValueStorage canonicalValues;
      canonicalValues.reserve(values.size());

      for (auto& [key, value] : values)
      {
        auto canonicalKey = Domain::Normalize::ActorValueKey(key);
        if (auto valid = Domain::Validation::ActorValueKey(canonicalKey); !valid)
        {
          return valid;
        }

        if (auto valid = Domain::Validation::Validate(value); !valid)
        {
          return valid;
        }

        if (!canonicalValues.try_emplace(std::move(canonicalKey), std::move(value)).second)
        {
          return DuplicateKey();
        }
      }

      std::unordered_set<ActorValueKey> canonicalRemovals;
      canonicalRemovals.reserve(removedKeys.size());

      for (const auto& key : removedKeys)
      {
        auto canonicalKey = Domain::Normalize::ActorValueKey(key);
        auto valid        = Domain::Validation::ActorValueKey(canonicalKey);
        if (!valid)
        {
          return valid;
        }

        if (canonicalValues.contains(canonicalKey))
        {
          return DuplicateKey();
        }

        // Repeated removals are idempotent; only remove/upsert overlap is
        // ambiguous and rejected above.
        canonicalRemovals.insert(std::move(canonicalKey));
      }

      auto&       target = found->second.actorValues;
      std::size_t newCount{};
      for (const auto& [key, value] : canonicalValues)
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

      for (const auto& key : canonicalRemovals)
      {
        target.erase(key);
      }

      while (!canonicalValues.empty())
      {
        const auto incoming = canonicalValues.begin();
        const auto existing = target.find(incoming->first);
        if (existing != target.end())
        {
          existing->second = std::move(incoming->second);
          canonicalValues.erase(incoming);
        }
        else
        {
          target.insert(canonicalValues.extract(incoming));
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

    static Domain::OperationResult Commit(Player& target, Player staged)
    {
      auto normalized = Domain::Normalize::PlayerValue(std::move(staged));
      if (!normalized)
      {
        return std::unexpected{std::move(normalized.error())};
      }

      target = std::move(*normalized);
      return {};
    }

    std::unordered_map<PlayerId, Player> players;
  };

}
