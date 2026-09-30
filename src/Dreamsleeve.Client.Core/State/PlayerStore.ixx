export module Dreamsleeve.Client.PlayerStore;

export import Dreamsleeve.Client.Domain.Logic;
import Dreamsleeve.Client.Changes;

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
      const auto id       = player.data.playerId;
      const auto previous = players.find(id);
      if (
        previous != players.end() && previous->second.characterGeneration == player.characterGeneration &&
        previous->second.viewRevision != 0 &&
        (player.viewRevision == 0 ||
         (player.viewRevision == previous->second.viewRevision && player.movementSequence < previous->second.movementSequence)))
      {
        player.viewRevision     = previous->second.viewRevision;
        player.location         = previous->second.location;
        player.movementSequence = previous->second.movementSequence;
      }
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

    std::optional<Player> Find(PlayerId id) const
    {
      const auto found = players.find(id);
      if (found == players.end())
      {
        return std::nullopt;
      }

      return found->second;
    }

    MovementObservation ObserveMovement(PlayerId id, MovementClock::time_point receivedAt) const
    {
      const auto found = players.find(id);
      if (found == players.end()) return {id, 0, receivedAt, std::nullopt};

      const auto& player = found->second;
      return {id, player.characterGeneration, receivedAt, player.location, player.viewRevision};
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

    Domain::OperationResult ReplaceMetadata(
      PlayerId                                id,
      const std::optional<ActorValueStorage>& values,
      const std::optional<PlayerDetails>&     details)
    {
      const auto found = players.find(id);
      if (found == players.end()) return UnknownPlayer();
      if (values) found->second.actorValues = *values;
      if (details) found->second.details = *details;
      return {};
    }

    Domain::OperationResult UpdateLocation(
      PlayerId                      id,
      std::optional<PlayerLocation> location,
      std::uint64_t                 viewRevision = 0,
      std::uint64_t                 sequence     = 0)
    {
      const auto found = players.find(id);
      if (found == players.end())
      {
        return UnknownPlayer();
      }

      // Position updates are frequent; replace only their payload.
      found->second.location         = std::move(location);
      found->second.viewRevision     = viewRevision;
      found->second.movementSequence = sequence;
      return {};
    }

    bool CanApplyMovement(PlayerId id, std::uint64_t viewRevision, std::uint64_t sequence) const
    {
      const auto found = players.find(id);
      return found != players.end() && found->second.location && viewRevision != 0 && found->second.viewRevision == viewRevision &&
             sequence > found->second.movementSequence;
    }

    void ApplyMovement(PlayerId id, std::uint64_t sequence, const MovementPose& pose)
    {
      auto& player                 = players.at(id);
      player.location->position    = pose.position;
      player.location->rotation    = pose.rotation;
      player.location->sampledAtUs = pose.sampledAtUs;
      player.movementSequence      = sequence;
    }

private:

    static Domain::OperationResult UnknownPlayer()
    {
      return std::unexpected{
          Domain::Error{Domain::ErrorCode::UnknownPlayer, "playerId"}
      };
    }

    std::unordered_map<PlayerId, Player> players;
  };

}
