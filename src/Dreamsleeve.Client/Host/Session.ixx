export module Dreamsleeve.Host.Session;

import std;
export import Dreamsleeve.Client.Exchange;
export import Dreamsleeve.Host.Bridge;

// Main-thread consumer of ClientOutput: projects Core state into UI bridge events
// and correlates UI chat requests with Core RequestIds. It keeps the online list
// only because the UI contract carries full lists; it is not a second model.
export namespace Dreamsleeve::Host
{

  using namespace Dreamsleeve::Client;

  class Session final
  {
public:

    struct Frame
    {
      std::vector<std::string> events;  // Encoded JSON, in delivery order.
      std::vector<std::string> notes;   // Diagnostics for the host logger.
      bool                     snapshot{};
      bool                     playersChanged{};
    };

    using Players = std::unordered_map<Domain::PlayerId, Domain::Player>;

    // One call per Drain. Posts RequestSnapshot itself when a view or a missed
    // delta requires a fresh full state.
    void Process(ClientExchange& exchange, const ClientOutput& output, const UiSettings& settings, Frame& frame)
    {
      const bool ready = output.status.phase == SessionPhase::Ready && !output.status.stopped;
      serverName       = output.status.serverName;
      for (const auto& update : output.state.updates)
        std::visit([&](const auto& value) { Apply(value, settings, ready, frame); }, update);

      if (frame.playersChanged && !frame.snapshot) Emit(frame, Bridge::PlayersEvent{.players = PlayerList()});

      for (const auto& confirmation : output.chatConfirmations)
        Complete(frame, confirmation.requestId, Bridge::Id(confirmation.messageId), {});
      for (const auto& event : output.rejections)
        Complete(
          frame,
          event.rejection.requestId,
          {},
          event.rejection.message.empty() ? "Сервер отклонил сообщение" : event.rejection.message);
      for (const auto& failure : output.commandFailures)
        Complete(frame, failure.requestId, {}, std::string{Bridge::FailureText(failure.code)});

      PublishStatus(output.status, frame);
      RequestSnapshotIfNeeded(exchange, frame);
    }

    // The next Process must deliver a full snapshot; old correlations are dropped
    // so a reply cannot reach a view that no longer exists.
    void ResetView()
    {
      needsSnapshot     = true;
      snapshotRequested = false;
      pendingChats.clear();
      lastStatus.reset();
    }

    std::expected<void, std::string> SendChat(ClientExchange& exchange, const Bridge::UiCommand& command)
    {
      if (!Ready()) return std::unexpected{"Нет соединения с сервером"};
      const auto channel = Bridge::ParseId(command.channelId);
      if (!channel || std::ranges::find(channels, *channel) == channels.end()) return std::unexpected{"Канал недоступен"};
      const auto requestId = exchange.NextRequestId();
      if (!requestId) return std::unexpected{"Идентификаторы запросов исчерпаны"};

      const auto posted = exchange.Post({
          generation,
          Dreamsleeve::Client::SendChat{*requestId, *channel, command.text}
      });
      if (posted != CommandPostResult::Queued) return std::unexpected{"Очередь команд заполнена"};
      pendingChats.emplace(*requestId, PendingChat{command.requestId, generation});
      return {};
    }

    bool Ready() const noexcept
    {
      return lastStatus && lastStatus->phase == SessionPhase::Ready && !lastStatus->stopped;
    }

    std::uint64_t Generation() const noexcept
    {
      return generation;
    }

    std::optional<Domain::PlayerId> SelfId() const noexcept
    {
      return selfId;
    }

    const Players& OnlinePlayers() const noexcept
    {
      return players;
    }

    const std::optional<ClientStatus>& Status() const noexcept
    {
      return lastStatus;
    }

    bool NeedsSnapshot() const noexcept
    {
      return needsSnapshot;
    }

    std::size_t PendingChatCount() const noexcept
    {
      return pendingChats.size();
    }

private:

    struct PendingChat
    {
      std::string   uiRequestId;
      std::uint64_t generation{};
    };

    template <class Event>
    void Emit(Frame& frame, const Event& event)
    {
      if (auto json = Bridge::Encode(event))
        frame.events.push_back(std::move(*json));
      else
        frame.notes.push_back(json.error());
    }

    std::vector<Bridge::UiPlayer> PlayerList() const
    {
      std::vector<Bridge::UiPlayer> list;
      list.reserve(players.size());
      for (const auto& [id, player] : players)
        list.push_back(Bridge::ToUiPlayer(player));
      std::ranges::sort(list, {}, &Bridge::UiPlayer::displayName);
      return list;
    }

    // The UI treats every snapshot as a connected session, so only a Ready
    // session publishes one; disconnect snapshots update state silently.
    void Apply(const ClientSnapshot& snapshot, const UiSettings& settings, bool ready, Frame& frame)
    {
      generation = snapshot.generation;
      selfId     = snapshot.selfPlayerId;
      players.clear();
      for (const auto& player : snapshot.players)
        players.insert_or_assign(player.data.playerId, player);
      channels.clear();
      for (const auto& chat : snapshot.chats)
        channels.push_back(chat.channelId);

      // A new generation cannot complete requests of the previous session.
      std::erase_if(pendingChats, [&](const auto& entry) { return entry.second.generation != generation; });
      frame.playersChanged = false;
      snapshotRequested    = false;
      if (!ready)
      {
        // A disconnect/reconnect snapshot: the UI still needs one once Ready.
        needsSnapshot = true;
        return;
      }

      Bridge::SnapshotEvent event;
      for (const auto& chat : snapshot.chats)
        event.channels.push_back({Bridge::Id(chat.channelId), "global", "Общий", true});
      // ChatCache keeps ascending MessageId order; the UI bounds its own history.
      for (const auto& chat : snapshot.chats)
      {
        const auto first = chat.messages.size() > Bridge::MaxSnapshotRows ? chat.messages.size() - Bridge::MaxSnapshotRows : 0;
        for (std::size_t index = first; index < chat.messages.size(); ++index)
          event.messages.push_back(Bridge::ToUiMessage(chat.messages[index]));
      }
      event.players    = PlayerList();
      event.selfId     = selfId ? Bridge::Id(*selfId) : "0";
      event.serverName = serverName;
      event.settings   = settings;
      Emit(frame, event);

      frame.snapshot = true;
      needsSnapshot  = false;
    }

    void Apply(const ClientStateDelta& delta, const UiSettings&, bool, Frame& frame)
    {
      if (delta.generation != generation)
      {
        // A delta of another session means the snapshot was missed; recover.
        needsSnapshot = true;
        return;
      }

      if (delta.selfPlayerChanged) selfId = delta.selfPlayerId;
      if (delta.playersReplaced)
      {
        players.clear();
        frame.playersChanged = true;
      }
      for (const auto& player : delta.players)
      {
        players.insert_or_assign(player.data.playerId, player);
        frame.playersChanged = true;
      }
      for (const auto id : delta.removedPlayers)
      {
        players.erase(id);
        frame.playersChanged = true;
      }
      for (const auto& change : delta.chats)
      {
        if (change.state && std::ranges::find(channels, change.channelId) == channels.end()) channels.push_back(change.channelId);
        if (!change.state) std::erase(channels, change.channelId);
      }

      Bridge::MessagesEvent messages;
      for (const auto& change : delta.chatContent)
        if (const auto* added = std::get_if<ChatMessagesAdded>(&change))
          for (const auto& message : added->messages)
            messages.messages.push_back(Bridge::ToUiMessage(message));
      if (!messages.messages.empty()) Emit(frame, messages);
    }

    void Complete(Frame& frame, std::uint64_t requestId, std::optional<std::string> messageId, std::optional<std::string> error)
    {
      const auto found = pendingChats.find(requestId);
      if (found == pendingChats.end()) return;  // Game updates carry their own IDs.

      Bridge::SendResultEvent event;
      event.requestId = found->second.uiRequestId;
      event.messageId = std::move(messageId);
      event.error     = std::move(error);
      Emit(frame, event);
      pendingChats.erase(found);
    }

    void PublishStatus(const ClientStatus& status, Frame& frame)
    {
      const bool first = !lastStatus;
      const bool connectionChanged =
        first || Bridge::PhaseName(*lastStatus) != Bridge::PhaseName(status) || lastStatus->serverName != status.serverName;
      const bool authChanged = first || lastStatus->authSequence != status.authSequence ||
                               lastStatus->authenticating != status.authenticating || lastStatus->authOperation != status.authOperation ||
                               lastStatus->authFailure != status.authFailure || lastStatus->error != status.error ||
                               lastStatus->savedLogin != status.savedLogin || lastStatus->savedUsername != status.savedUsername;
      if (connectionChanged) Emit(frame, Bridge::ConnectionState(status));
      if (authChanged || connectionChanged) Emit(frame, Bridge::AuthState(status));
      lastStatus = status;
    }

    // Only a Ready session can answer with a snapshot the UI may show.
    void RequestSnapshotIfNeeded(ClientExchange& exchange, Frame& frame)
    {
      if (!needsSnapshot || snapshotRequested || !Ready()) return;
      const auto posted = exchange.Post({generation, RequestSnapshot{}});
      if (posted == CommandPostResult::Queued || posted == CommandPostResult::Replaced)
        snapshotRequested = true;
      else
        frame.notes.push_back("RequestSnapshot deferred: command queue unavailable");
    }

    std::uint64_t                                  generation{};
    std::optional<Domain::PlayerId>                selfId;
    std::string                                    serverName;
    std::vector<Domain::ChatChannelId>             channels;
    Players                                        players;
    std::unordered_map<std::uint64_t, PendingChat> pendingChats;
    std::optional<ClientStatus>                    lastStatus;
    bool                                           needsSnapshot{true};
    bool                                           snapshotRequested{};
  };

}
