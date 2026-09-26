export module Dreamsleeve.Client.Model;

import std;

export import Dreamsleeve.Client.ChatCache;
export import Dreamsleeve.Client.PlayerStore;

export namespace Dreamsleeve::Client
{

  using namespace Domain;
  
  // Application updates after decoding. These are not protobuf or ENet events.
  struct SelfPlayerAssigned
  {
    std::optional<PlayerId> playerId;
  };

  struct OnlinePlayersReplaced
  {
    std::vector<Player> players;
  };

  struct PlayerUpserted
  {
    Player player;
  };

  struct PlayerRemoved
  {
    PlayerId playerId;
  };

  struct PlayerProfileUpdated
  {
    PlayerId   playerId;
    PlayerData profile;
  };

  struct PlayerLocationUpdated
  {
    PlayerId                      playerId;
    std::optional<PlayerLocation> location;
  };

  struct PlayerCharacterRenamed
  {
    PlayerId                     playerId;
    std::optional<CharacterName> characterName;
  };

  struct PlayerCharacterStarted
  {
    PlayerId      playerId;
    CharacterName characterName;
  };

  struct PlayerGameStateCleared
  {
    PlayerId playerId;
  };

  struct PlayerActorValuesUpdated
  {
    PlayerId                   playerId;
    ActorValueStorage          values;
    std::vector<ActorValueKey> removedKeys;
  };

  struct ChatMessagesReceived
  {
    ChatChannelId            channelId;
    std::vector<ChatMessage> messages;
  };

  struct ChatHistoryReceived
  {
    ChatHistoryPage page;
  };

  using ClientUpdate = std::variant<
    SelfPlayerAssigned,
    OnlinePlayersReplaced,
    PlayerUpserted,
    PlayerRemoved,
    PlayerProfileUpdated,
    PlayerLocationUpdated,
    PlayerCharacterRenamed,
    PlayerCharacterStarted,
    PlayerGameStateCleared,
    PlayerActorValuesUpdated,
    ChatMessagesReceived,
    ChatHistoryReceived>;

  struct ClientSnapshot
  {
    std::uint64_t                  generation{};
    std::uint64_t                  revision{};
    std::optional<PlayerId>        selfPlayerId;
    std::vector<Player>            players;
    std::vector<ChatCacheSnapshot> chats;
  };

  // All methods, including Snapshot/Generation, belong to one serial owner.
  // Publish only the resulting detached snapshot to game/UI threads.
  class ClientModel final
  {
public:

    ClientModel()                              = default;
    ClientModel(const ClientModel&)            = delete;
    ClientModel& operator=(const ClientModel&) = delete;
    ClientModel(ClientModel&&)                 = default;
    ClientModel& operator=(ClientModel&&)      = default;

    Domain::OperationResult RegisterChannel(ChatChannelId channelId, std::size_t capacity)
    {
      if (chats.contains(channelId))
      {
        return std::unexpected(Domain::Error{Domain::ErrorCode::DuplicateKey, "channelId"});
      }

      auto cache = ChatCache::TryCreate(channelId, capacity);
      if (!cache)
      {
        return std::unexpected(std::move(cache.error()));
      }

      chats.emplace(channelId, std::move(*cache));
      ++revision;
      return {};
    }

    bool RemoveChannel(ChatChannelId channelId)
    {
      if (chats.erase(channelId) == 0)
      {
        return false;
      }

      ++revision;
      return true;
    }

    Domain::OperationResult SetSelfPlayer(std::uint64_t updateGeneration, std::optional<PlayerId> playerId)
    {
      return Apply(updateGeneration, SelfPlayerAssigned{playerId});
    }

    Domain::Result<std::uint64_t> BeginHistory(ChatChannelId channelId, std::optional<ChatMessageId> after = std::nullopt)
    {
      auto found = chats.find(channelId);
      if (found == chats.end())
      {
        return std::unexpected(Domain::Error{Domain::ErrorCode::UnknownChannel, "channelId"});
      }

      if (historyRound == std::numeric_limits<std::uint64_t>::max())
      {
        return std::unexpected(Domain::Error{Domain::ErrorCode::InvalidCursor, "round"});
      }
      // The token belongs to this model, so removing and re-registering a
      // channel cannot reuse a canceled request's token in the same session.
      auto result = found->second.BeginHistory(after, historyRound + 1);
      if (result)
      {
        historyRound = *result;
        ++revision;
      }
      return result;
    }

    // Capture this generation when starting an async request/decoder job, not
    // when its result finishes. Late work from the previous session is rejected.
    Domain::OperationResult Apply(std::uint64_t updateGeneration, const ClientUpdate& update)
    {
      if (updateGeneration != generation)
      {
        return std::unexpected(Domain::Error{Domain::ErrorCode::StaleGeneration, "generation"});
      }

      auto result = std::visit([this](const auto& value) { return ApplyOne(value); }, update);
      if (result)
      {
        // A successful operation advances revision even if it was an idempotent
        // duplicate. This is a local publication marker, not a server sequence.
        ++revision;
      }
      return result;
    }

    // A disconnect to the same server can retain accepted chat messages.
    // The session adapter must still negotiate a valid history-resume cursor.
    void ClearOnlineState()
    {
      players.Clear();
      selfPlayerId.reset();
      for (auto& [channelId, cache] : chats)
      {
        cache.CancelHistory();
      }
      ++generation;
      ++revision;
    }

    // Use for a server switch or an announced reset of server identity/history.
    // Re-register channels before accepting messages from the new server.
    void ResetSession()
    {
      players.Clear();
      chats.clear();
      selfPlayerId.reset();
      ++generation;
      ++revision;
    }

    std::uint64_t Generation() const noexcept
    {
      return generation;
    }

    ClientSnapshot Snapshot() const
    {
      ClientSnapshot
        result{.generation = generation, .revision = revision, .selfPlayerId = selfPlayerId, .players = players.Snapshot(), .chats = {}};
      result.chats.reserve(chats.size());
      for (const auto& [channelId, cache] : chats)
      {
        result.chats.push_back(cache.Snapshot());
      }
      return result;
    }

private:

    Domain::OperationResult ApplyOne(const SelfPlayerAssigned& update)
    {
      if (update.playerId && *update.playerId == 0)
      {
        return std::unexpected(Domain::Error{Domain::ErrorCode::InvalidId, "selfPlayerId"});
      }
      selfPlayerId = update.playerId;
      return {};
    }

    Domain::OperationResult ApplyOne(const OnlinePlayersReplaced& update)
    {
      return players.ReplaceAll(update.players);
    }

    Domain::OperationResult ApplyOne(const PlayerUpserted& update)
    {
      auto result = players.Upsert(update.player);
      if (!result)
      {
        return std::unexpected(std::move(result.error()));
      }
      return {};
    }

    Domain::OperationResult ApplyOne(const PlayerRemoved& update)
    {
      if (update.playerId == 0)
      {
        return std::unexpected(Domain::Error{Domain::ErrorCode::InvalidId, "playerId"});
      }
      players.Remove(update.playerId);
      return {};
    }

    Domain::OperationResult ApplyOne(const PlayerProfileUpdated& update)
    {
      return players.UpdateProfile(update.playerId, update.profile);
    }

    Domain::OperationResult ApplyOne(const PlayerLocationUpdated& update)
    {
      return players.UpdateLocation(update.playerId, update.location);
    }

    Domain::OperationResult ApplyOne(const PlayerCharacterRenamed& update)
    {
      return players.RenameCharacter(update.playerId, update.characterName);
    }

    Domain::OperationResult ApplyOne(const PlayerCharacterStarted& update)
    {
      return players.BeginCharacter(update.playerId, update.characterName);
    }

    Domain::OperationResult ApplyOne(const PlayerGameStateCleared& update)
    {
      return players.ClearGameState(update.playerId);
    }

    Domain::OperationResult ApplyOne(const PlayerActorValuesUpdated& update)
    {
      return players.ApplyActorValues(update.playerId, update.values, update.removedKeys);
    }

    Domain::OperationResult ApplyOne(const ChatMessagesReceived& update)
    {
      auto found = chats.find(update.channelId);
      if (found == chats.end())
      {
        return std::unexpected(Domain::Error{Domain::ErrorCode::UnknownChannel, "channelId"});
      }
      auto result = found->second.Merge(update.messages);
      if (!result)
      {
        return std::unexpected(std::move(result.error()));
      }
      return {};
    }

    Domain::OperationResult ApplyOne(const ChatHistoryReceived& update)
    {
      auto found = chats.find(update.page.channelId);
      if (found == chats.end())
      {
        return std::unexpected(Domain::Error{Domain::ErrorCode::UnknownChannel, "channelId"});
      }
      auto result = found->second.ApplyHistoryPage(update.page);
      if (!result)
      {
        return std::unexpected(std::move(result.error()));
      }
      return {};
    }

    PlayerStore                        players;
    std::map<ChatChannelId, ChatCache> chats;
    std::optional<PlayerId>            selfPlayerId;
    std::uint64_t                      generation{1};
    std::uint64_t                      revision{};
    std::uint64_t                      historyRound{};
  };

}
