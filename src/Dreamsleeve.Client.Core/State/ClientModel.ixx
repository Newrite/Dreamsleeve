export module Dreamsleeve.Client.Model;

import std;

export import Dreamsleeve.Client.ChatCache;
export import Dreamsleeve.Client.Changes;
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

  // A server business rejection after decoding. The code is opaque until the
  // application protocol defines it; unknown codes still carry a message.
  struct ServerRejection
  {
    std::optional<std::uint64_t> requestId;
    std::uint32_t                code{};
    std::string                  message;
    std::string                  field;
  };

  struct ServerRejectionEvent
  {
    std::uint64_t   generation{};
    ServerRejection rejection;
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
    ChatHistoryReceived,
    ServerRejection>;

  struct ClientSnapshot
  {
    std::uint64_t                  generation{};
    std::uint64_t                  revision{};
    std::optional<PlayerId>        selfPlayerId;
    std::vector<Player>            players;
    std::vector<ChatCacheSnapshot> chats;
  };

  // All methods, including Snapshot/Generation, belong to one serial owner.
  // Resolve ChangeBatch on that owner; publish only detached values/snapshots.
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
      MarkChatState(channelId, true);
      return {};
    }

    bool RemoveChannel(ChatChannelId channelId)
    {
      if (chats.erase(channelId) == 0)
      {
        return false;
      }

      ++revision;
      ForgetChatContent(channelId);
      MarkChatState(channelId);
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
        MarkChatState(channelId);
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
        std::visit([this](const auto& value) { MarkUpdate(value); }, update);
      }
      return result;
    }

    // A disconnect to the same server can retain accepted chat messages.
    // The session adapter must still negotiate a valid history-resume cursor.
    // Pending rejections survive: the server can reject and then disconnect.
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
      RequireSnapshot();
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
      RequireSnapshot();
    }

    std::uint64_t Generation() const noexcept
    {
      return generation;
    }

    std::optional<PlayerId> SelfPlayerId() const noexcept
    {
      return selfPlayerId;
    }

    std::optional<Player> FindPlayer(PlayerId playerId) const
    {
      return players.Find(playerId);
    }

    std::vector<Player> SnapshotPlayers() const
    {
      return players.Snapshot();
    }

    std::optional<ChatCacheSnapshot> FindChat(ChatChannelId channelId) const
    {
      const auto found = chats.find(channelId);
      if (found == chats.end()) return std::nullopt;
      return found->second.Snapshot();
    }

    std::optional<ChatCacheState> FindChatState(ChatChannelId channelId) const noexcept
    {
      const auto found = chats.find(channelId);
      if (found == chats.end()) return std::nullopt;
      return found->second.State();
    }

    // One owner drains once, resolves player/chat-state invalidations, then
    // forwards the owning chat-content deltas in their stored order. Drain
    // regularly even when no UI is subscribed. Reusing output preserves the
    // capacities of the top-level vectors exchanged with pendingChanges.
    void TakeChanges(ChangeBatch& output) noexcept
    {
      output.Clear();
      std::swap(output, pendingChanges);
      output.generation = generation;
      output.revision   = revision;
    }

    // Owner-only drain. Forward these owning events through the application's
    // synchronized UI queue; snapshots can skip intermediate publications.
    // Already accepted events survive either reset until explicitly taken.
    std::vector<ServerRejectionEvent> TakeServerRejections()
    {
      std::vector<ServerRejectionEvent> result;
      result.swap(serverRejections);
      return result;
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

    void RequireSnapshot() noexcept
    {
      pendingChanges.Clear();
      pendingChanges.requiresSnapshot = true;
    }

    template <class Id>
    void MarkId(std::vector<Id>& ids, Id id)
    {
      if (std::ranges::find(ids, id) != ids.end()) return;
      ids.push_back(id);
    }

    void MarkPlayer(PlayerId playerId)
    {
      if (!pendingChanges.requiresSnapshot && !pendingChanges.playersReplaced)
      {
        MarkId(pendingChanges.players, playerId);
      }
    }

    void MarkChatState(ChatChannelId channelId, bool resetContent = false)
    {
      if (!pendingChanges.requiresSnapshot)
      {
        MarkId(pendingChanges.chats, channelId);
        if (resetContent) MarkId(pendingChanges.resetChats, channelId);
      }
    }

    void AppendChatContent(ChatChannelId channelId, std::vector<ChatMessageId> messageIds)
    {
      if (messageIds.empty()) return;

      if (!pendingChanges.chatContent.empty())
      {
        if (auto* last = std::get_if<ChatMessagesRemoved>(&pendingChanges.chatContent.back()); last && last->channelId == channelId)
        {
          last->messageIds.reserve(last->messageIds.size() + messageIds.size());
          last->messageIds.insert(
            last->messageIds.end(),
            std::make_move_iterator(messageIds.begin()),
            std::make_move_iterator(messageIds.end()));
          return;
        }
      }

      pendingChanges.chatContent.emplace_back(ChatMessagesRemoved{.channelId = channelId, .messageIds = std::move(messageIds)});
    }

    void AppendChatContent(ChatChannelId channelId, std::vector<ChatMessage> messages)
    {
      if (messages.empty()) return;

      if (!pendingChanges.chatContent.empty())
      {
        if (auto* last = std::get_if<ChatMessagesAdded>(&pendingChanges.chatContent.back()); last && last->channelId == channelId)
        {
          last->messages.reserve(last->messages.size() + messages.size());
          last->messages.insert(last->messages.end(), std::make_move_iterator(messages.begin()), std::make_move_iterator(messages.end()));
          return;
        }
      }

      pendingChanges.chatContent.emplace_back(ChatMessagesAdded{.channelId = channelId, .messages = std::move(messages)});
    }

    void RecordChatMerge(ChatChannelId channelId, ChatMergeResult result)
    {
      if (pendingChanges.requiresSnapshot) return;

      // Preserve cache transition order across multiple Merge calls between
      // TakeChanges invocations. Removals precede additions for one merge.
      AppendChatContent(channelId, std::move(result.removedMessageIds));
      AppendChatContent(channelId, std::move(result.addedMessages));
    }

    void ForgetChatContent(ChatChannelId channelId)
    {
      std::erase_if(pendingChanges.chatContent, [channelId](const ChatContentChange& change) {
        return std::visit([channelId](const auto& value) { return value.channelId == channelId; }, change);
      });
    }

    template <class Update>
    void MarkUpdate(const Update& update)
    {
      if (pendingChanges.requiresSnapshot) return;

      if constexpr (std::is_same_v<Update, SelfPlayerAssigned>)
      {
        pendingChanges.selfPlayerChanged = true;
      }
      else if constexpr (std::is_same_v<Update, OnlinePlayersReplaced>)
      {
        pendingChanges.players.clear();
        pendingChanges.playersReplaced = true;
      }
      else if constexpr (std::is_same_v<Update, PlayerUpserted>)
      {
        MarkPlayer(update.player.data.playerId);
      }
      else if constexpr (
        !std::is_same_v<Update, ChatMessagesReceived> && !std::is_same_v<Update, ChatHistoryReceived> &&
        !std::is_same_v<Update, ServerRejection>)
      {
        MarkPlayer(update.playerId);
      }
    }

    Domain::OperationResult ApplyOne(const SelfPlayerAssigned& update)
    {
      selfPlayerId = update.playerId;
      return {};
    }

    Domain::OperationResult ApplyOne(const OnlinePlayersReplaced& update)
    {
      return players.ReplaceAll(update.players);
    }

    Domain::OperationResult ApplyOne(const PlayerUpserted& update)
    {
      players.Upsert(update.player);
      return {};
    }

    Domain::OperationResult ApplyOne(const PlayerRemoved& update)
    {
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

      RecordChatMerge(update.channelId, std::move(*result));
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

      RecordChatMerge(update.page.channelId, std::move(*result));
      MarkChatState(update.page.channelId);
      return {};
    }

    Domain::OperationResult ApplyOne(const ServerRejection& update)
    {
      serverRejections.push_back(ServerRejectionEvent{generation, update});
      // Success means the notification was handled, not that the server
      // accepted the originating request. Accepted game/chat state is intact.
      return {};
    }

    PlayerStore                        players;
    std::map<ChatChannelId, ChatCache> chats;
    std::vector<ServerRejectionEvent>  serverRejections;
    std::optional<PlayerId>            selfPlayerId;
    ChangeBatch                        pendingChanges;
    std::uint64_t                      generation{1};
    std::uint64_t                      revision{};
    std::uint64_t                      historyRound{};
  };

}
