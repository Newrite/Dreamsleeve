export module Dreamsleeve.Host.Session;

import std;
export import Dreamsleeve.Client.Exchange;
export import Dreamsleeve.Host.Bridge;
export import Dreamsleeve.Host.Announcements;

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
      // Live publications of other players in the global channel, confirmed by
      // the server in this Process call: never snapshot history, replays or
      // announcements. Consumed by the firefly chat bubbles.
      std::vector<Domain::ChatMessage> freshMessages;
      // Final results of announcements requested through the plugin API.
      std::vector<Announcements::Outcome> announcementResults;
      bool                             snapshot{};
      bool                             playersChanged{};
      // The set of marks the game draws changed in this Process call.
      bool visibleMarksChanged{};
    };

    using Players = std::unordered_map<Domain::PlayerId, Domain::Player>;
    using Marks   = std::map<Domain::GroundMarkId, Domain::GroundMark>;

    // One call per Drain. Posts RequestSnapshot itself when a view or a missed
    // delta requires a fresh full state.
    void Process(ClientExchange& exchange, const ClientOutput& output, const UiSettings& settings, Frame& frame)
    {
      const bool ready = output.status.phase == SessionPhase::Ready && !output.status.stopped;
      serverName       = output.status.serverName;
      for (const auto& update : output.state.updates)
        std::visit([&](const auto& value) { Apply(value, settings, ready, frame); }, update);

      if (frame.playersChanged && !frame.snapshot) Emit(frame, Bridge::PlayersEvent{.players = PlayerList(settings)});

      for (const auto& confirmation : output.chatConfirmations)
        if (!Settle(frame, confirmation.requestId, Announcements::Result::Published, {}))
          Complete(frame, confirmation.requestId, Bridge::Id(confirmation.messageId), {});
      for (const auto& confirmation : output.groundMarkConfirmations)
        SettleMark(frame, confirmation);
      for (const auto& event : output.rejections)
      {
        auto reason = Bridge::RejectionText(event.rejection.code, event.rejection.message);
        if (!Settle(frame, event.rejection.requestId, ResultOf(event.rejection), reason) && !FailMark(frame, event.rejection.requestId, reason))
          Complete(frame, event.rejection.requestId, {}, std::move(reason));
      }
      for (const auto& failure : output.commandFailures)
      {
        std::string reason{Bridge::FailureText(failure.code)};
        if (!Settle(frame, failure.requestId, ResultOf(failure.code), reason) && !FailMark(frame, failure.requestId, reason))
          Complete(frame, failure.requestId, {}, std::move(reason));
      }
      if (ownMarksChanged && !frame.snapshot) Emit(frame, Bridge::GroundMarksEvent{.marks = OwnMarkList(settings)});
      ownMarksChanged = false;

      PublishStatus(output.status, settings, frame);
      RequestSnapshotIfNeeded(exchange, frame);
    }

    // Marks the server currently shows this player, as Core projects them.
    const Marks& VisibleMarks() const noexcept
    {
      return visibleMarks;
    }

    // The player's own marks this session has seen: placed here, or delivered
    // while nearby. The server keeps no separate list of own marks, so a mark
    // placed in an earlier session far from here is unknown until met again.
    const Marks& OwnMarks() const noexcept
    {
      return ownMarks;
    }

    // A note where the player stands, requested by the web UI.
    std::expected<void, std::string>
      PlaceGroundNote(ClientExchange& exchange, std::string uiRequestId, std::string text, const Domain::GroundMarkPlacement& placement)
    {
      if (!Ready()) return std::unexpected{"Нет соединения с сервером"};
      const auto requestId = exchange.NextRequestId();
      if (!requestId) return std::unexpected{"Идентификаторы запросов исчерпаны"};
      const auto posted = exchange.Post({
          generation,
          Dreamsleeve::Client::PlaceGroundNote{*requestId, std::move(text), placement}
      });
      if (posted != CommandPostResult::Queued) return std::unexpected{"Очередь команд заполнена"};
      pendingMarks.emplace(*requestId, PendingMark{std::move(uiRequestId), MarkRequest::Note, generation});
      return {};
    }

    // The place the character died, reported by the game once per death.
    std::expected<void, std::string> ReportDeath(ClientExchange& exchange, std::string label, const Domain::GroundMarkPlacement& placement)
    {
      if (!Ready()) return std::unexpected{"session not ready"};
      const auto requestId = exchange.NextRequestId();
      if (!requestId) return std::unexpected{"request ids exhausted"};
      const auto posted = exchange.Post({
          generation,
          Dreamsleeve::Client::ReportDeath{*requestId, std::move(label), placement}
      });
      if (posted != CommandPostResult::Queued) return std::unexpected{"command queue full"};
      pendingMarks.emplace(*requestId, PendingMark{{}, MarkRequest::Death, generation});
      return {};
    }

    std::expected<void, std::string> RemoveGroundMark(ClientExchange& exchange, std::string uiRequestId, Domain::GroundMarkId markId)
    {
      if (!Ready()) return std::unexpected{"Нет соединения с сервером"};
      const auto requestId = exchange.NextRequestId();
      if (!requestId) return std::unexpected{"Идентификаторы запросов исчерпаны"};
      const auto posted = exchange.Post({
          generation,
          Dreamsleeve::Client::RemoveGroundMark{*requestId, markId}
      });
      if (posted != CommandPostResult::Queued) return std::unexpected{"Очередь команд заполнена"};
      pendingMarks.emplace(*requestId, PendingMark{std::move(uiRequestId), MarkRequest::Remove, generation});
      return {};
    }

    std::size_t PendingMarkCount() const noexcept
    {
      return pendingMarks.size();
    }

    // The next Process must deliver a full snapshot; old correlations are dropped
    // so a reply cannot reach a view that no longer exists.
    void ResetView()
    {
      needsSnapshot     = true;
      snapshotRequested = false;
      refreshing        = false;
      pendingChats.clear();
      // Death reports have no UI correlation and settle silently either way.
      std::erase_if(pendingMarks, [](const auto& entry) { return entry.second.request != MarkRequest::Death; });
      lastStatus.reset();
    }

    // Names or the ignore list changed: the same session is projected again.
    // Pending sends keep their correlation; status events are re-sent too.
    void Refresh()
    {
      needsSnapshot     = true;
      snapshotRequested = false;
      refreshing        = true;
      lastStatus.reset();
    }

    Names& PlayerNames() noexcept
    {
      return names;
    }

    // Returns whether the list changed. A player need not be online: authors
    // of retained history can be ignored by ID as well.
    bool Ignore(Domain::PlayerId id)
    {
      const Domain::PlayerData* known = nullptr;
      if (const auto online = players.find(id); online != players.end())
        known = &online->second.data;
      else if (const auto author = authors.find(id); author != authors.end())
        known = &author->second;
      return names.Ignore(id, selfId, known);
    }

    bool Unignore(Domain::PlayerId id)
    {
      return names.Unignore(id);
    }

    Bridge::IgnoredEvent IgnoredList(const UiSettings& settings)
    {
      Bridge::IgnoredEvent event;
      for (auto& entry : names.IgnoredList(settings))
        event.players.push_back({Bridge::Id(entry.id), std::move(entry.name)});
      return event;
    }

    std::expected<void, std::string> SendChat(ClientExchange& exchange, const Bridge::UiCommand& command)
    {
      if (!Ready()) return std::unexpected{"Нет соединения с сервером"};
      const auto channel = Bridge::ParseId(command.channelId);
      const auto found   = channel ? channels.find(*channel) : channels.end();
      if (found == channels.end() || found->second != Domain::ChatChannelKind::Global) return std::unexpected{"Канал недоступен"};
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

    // Main thread. The plugin API already checked the text encoding and the
    // label; Core checks the server's limits, the server everything else.
    Announcements::Result PostAnnouncement(ClientExchange& exchange, Announcements::Request request)
    {
      const auto system = ChannelOf(Domain::ChatChannelKind::System);
      if (!Ready() || !system) return Announcements::Result::NotConnected;
      const auto requestId = exchange.NextRequestId();
      if (!requestId) return Announcements::Result::Failed;

      PendingAnnouncement pending{*system, request.signature, request.text, generation};
      const auto          posted = exchange.Post({
          generation,
          Dreamsleeve::Client::
            PostAnnouncement{*requestId, *system, std::move(request.text), request.kind, request.source, std::move(request.signature)}
      });
      if (posted == CommandPostResult::Closed) return Announcements::Result::NotConnected;
      if (posted != CommandPostResult::Queued) return Announcements::Result::Busy;
      pendingAnnouncements.emplace(*requestId, std::move(pending));
      return Announcements::Result::Queued;
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

    struct PendingAnnouncement
    {
      Domain::ChatChannelId channelId{};
      std::string           signature;
      std::string   text;
      std::uint64_t generation{};
    };

    enum class MarkRequest
    {
      Note,
      Death,
      Remove
    };

    struct PendingMark
    {
      std::string   uiRequestId;  // Empty for a death report.
      MarkRequest   request{};
      std::uint64_t generation{};
    };

    std::vector<Bridge::UiGroundMark> OwnMarkList(const UiSettings& settings) const
    {
      std::vector<Bridge::UiGroundMark> list;
      list.reserve(ownMarks.size());
      for (const auto& [id, mark] : ownMarks)
        list.push_back(Bridge::ToUiGroundMark(mark, settings));
      return list;
    }

    void SeeMark(const Domain::GroundMark& mark)
    {
      visibleMarks.insert_or_assign(mark.markId, mark);
      if (selfId && mark.author.playerId == *selfId)
      {
        ownMarks.insert_or_assign(mark.markId, mark);
        ownMarksChanged = true;
      }
    }

    void ForgetMark(Domain::GroundMarkId id)
    {
      visibleMarks.erase(id);
      if (ownMarks.erase(id) != 0) ownMarksChanged = true;
    }

    void SettleMark(Frame& frame, const GroundMarkConfirmation& confirmation)
    {
      const auto found = pendingMarks.find(confirmation.requestId);
      if (found == pendingMarks.end()) return;
      auto pending = std::move(found->second);
      pendingMarks.erase(found);
      if (confirmation.removed) ForgetMark(confirmation.markId);
      if (confirmation.evictedId) ForgetMark(*confirmation.evictedId);
      if (pending.request == MarkRequest::Death)
      {
        frame.notes.push_back(std::format("Death mark {} placed", confirmation.markId));
        return;
      }
      Bridge::MarkResultEvent event;
      event.requestId = std::move(pending.uiRequestId);
      if (confirmation.removed)
        event.removed = true;
      else
      {
        event.markId = Bridge::Id(confirmation.markId);
        if (confirmation.evictedId) event.evictedId = Bridge::Id(*confirmation.evictedId);
      }
      Emit(frame, event);
    }

    bool FailMark(Frame& frame, std::uint64_t requestId, std::string reason)
    {
      const auto found = pendingMarks.find(requestId);
      if (found == pendingMarks.end()) return false;
      auto pending = std::move(found->second);
      pendingMarks.erase(found);
      if (pending.request == MarkRequest::Death)
      {
        frame.notes.push_back("Death mark refused: " + reason);
        return true;
      }
      Emit(frame, Bridge::MarkResultEvent{.requestId = std::move(pending.uiRequestId), .error = Bridge::ClipError(reason)});
      return true;
    }

    template <class Event>
    void Emit(Frame& frame, const Event& event)
    {
      if (auto json = Bridge::Encode(event))
        frame.events.push_back(std::move(*json));
      else
        frame.notes.push_back(json.error());
    }

    std::vector<Bridge::UiPlayer> PlayerList(const UiSettings& settings)
    {
      std::vector<Bridge::UiPlayer> list;
      list.reserve(players.size());
      for (const auto& [id, player] : players)
        list.push_back(Bridge::ToUiPlayer(player, names, settings));
      std::ranges::sort(list, {}, &Bridge::UiPlayer::name);
      return list;
    }

    // Ignored authors are dropped from every UI projection, including their
    // clients' announcements; self and the server are never filtered.
    bool Hidden(const Domain::ChatMessage& message) const
    {
      return message.author && message.author->playerId != selfId && names.Ignored(message.author->playerId);
    }

    void Remember(const Domain::ChatMessage& message)
    {
      if (!message.author) return;
      if (authors.size() >= MaxKnownAuthors && !authors.contains(message.author->playerId)) authors.clear();
      authors.insert_or_assign(message.author->playerId, *message.author);
    }

    std::optional<Domain::ChatChannelId> ChannelOf(Domain::ChatChannelKind kind) const
    {
      for (const auto& [id, channelKind] : channels)
        if (channelKind == kind) return id;
      return std::nullopt;
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
        channels.insert_or_assign(chat.channelId, chat.kind);
      // Bubbles follow the global channel; retained history sets the floor:
      // only later IDs are live.
      globalChannel      = ChannelOf(Domain::ChatChannelKind::Global);
      const bool refresh = std::exchange(refreshing, false) && snapshot.generation == refreshGeneration;
      refreshGeneration  = snapshot.generation;
      authors.clear();
      bubbleFloor = 0;
      for (const auto& chat : snapshot.chats)
        if (globalChannel && chat.channelId == *globalChannel && !chat.messages.empty())
          bubbleFloor = std::max(bubbleFloor, chat.messages.back().messageId);

      // Marks of another generation are gone with it; a refresh of the same
      // session keeps the own marks met earlier and re-reads the visible set.
      if (generation != marksGeneration) ownMarks.clear();
      marksGeneration = generation;
      visibleMarks.clear();
      for (const auto& mark : snapshot.groundMarks.marks)
        SeeMark(mark);
      ownMarksChanged           = false;
      frame.visibleMarksChanged = true;

      // A new generation cannot complete requests of the previous session.
      std::erase_if(pendingChats, [&](const auto& entry) { return entry.second.generation != generation; });
      std::erase_if(pendingMarks, [&](const auto& entry) { return entry.second.generation != generation; });
      std::erase_if(pendingAnnouncements, [&](const auto& entry) {
        if (entry.second.generation == generation) return false;
        Finish(frame, entry.second, Announcements::Result::Failed, "Доставка неизвестна: сессия сменилась");
        return true;
      });
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
        event.channels.push_back(Bridge::ToUiChannel(chat.channelId, chat.kind));
      // ChatCache keeps ascending MessageId order; the UI bounds its own history.
      for (const auto& chat : snapshot.chats)
      {
        const auto first = chat.messages.size() > Bridge::MaxSnapshotRows ? chat.messages.size() - Bridge::MaxSnapshotRows : 0;
        for (std::size_t index = first; index < chat.messages.size(); ++index)
        {
          Remember(chat.messages[index]);
          if (Hidden(chat.messages[index])) continue;
          if (auto shown = Bridge::ToShownMessage(chat.messages[index], names, settings, selfId))
            event.messages.push_back(std::move(*shown));
        }
      }
      event.players              = PlayerList(settings);
      event.refresh              = refresh;
      event.selfId               = selfId ? Bridge::Id(*selfId) : "0";
      event.serverName           = serverName;
      event.settings             = settings;
      event.groundMarksSupported = true;  // Protocol v8: every Ready session carries marks.
      event.groundMarks          = OwnMarkList(settings);
      Emit(frame, event);

      frame.snapshot = true;
      needsSnapshot  = false;
    }

    void Apply(const ClientStateDelta& delta, const UiSettings& settings, bool, Frame& frame)
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
        if (change.state)
          channels.insert_or_assign(change.channelId, change.state->kind);
        else
          channels.erase(change.channelId);
      }
      // Ordered transitions of the visible set. A clear (space change) keeps
      // the own marks: they still exist, only out of sight.
      for (const auto& change : delta.groundMarks)
      {
        frame.visibleMarksChanged = true;
        if (std::holds_alternative<GroundMarksCleared>(change))
          visibleMarks.clear();
        else if (const auto* removed = std::get_if<GroundMarksRemoved>(&change))
          for (const auto id : removed->markIds)
            ForgetMark(id);
        else if (const auto* added = std::get_if<GroundMarksAdded>(&change))
          for (const auto& mark : added->marks)
            SeeMark(mark);
      }

      Bridge::MessagesEvent messages;
      for (const auto& change : delta.chatContent)
        if (const auto* added = std::get_if<ChatMessagesAdded>(&change))
          for (const auto& message : added->messages)
          {
            Remember(message);
            // Fresh() runs first: it advances the bubble floor even for an
            // ignored author, so unignoring never replays old messages.
            if (Fresh(message) && !Hidden(message))
              if (auto text = Bridge::ShownText(message, settings, false))
              {
                // Bubbles carry the filtered text: a hidden message never shows above a firefly.
                auto bubble        = message;
                bubble.messageText = std::move(*text);
                frame.freshMessages.push_back(std::move(bubble));
              }
            if (Hidden(message)) continue;
            if (auto shown = Bridge::ToShownMessage(message, names, settings, selfId)) messages.messages.push_back(std::move(*shown));
          }
      if (!messages.messages.empty()) Emit(frame, messages);
    }

    // Bubble admission: global channel, newer than anything seen (history
    // pages and repeated events stay out), a player other than self.
    bool Fresh(const Domain::ChatMessage& message)
    {
      if (!globalChannel || message.channelId != *globalChannel || message.messageId <= bubbleFloor) return false;
      bubbleFloor = message.messageId;
      return message.author && message.author->playerId != selfId;
    }

    static Announcements::Result ResultOf(const ServerRejection& rejection)
    {
      using Code = Dreamsleeve::Client::RequestRejectionCode;
      return rejection.code == Code::RateLimited ? Announcements::Result::RateLimited : Announcements::Result::Rejected;
    }

    static Announcements::Result ResultOf(CommandFailureCode code)
    {
      switch (code)
      {
        case CommandFailureCode::StaleGeneration:
        case CommandFailureCode::SessionNotReady:
          return Announcements::Result::NotConnected;
        case CommandFailureCode::Busy:
          return Announcements::Result::Busy;
        case CommandFailureCode::InvalidRequest:
          return Announcements::Result::Rejected;
        case CommandFailureCode::EncodingFailed:
          break;
      }
      return Announcements::Result::Failed;
    }

    // Reports a plugin API request; a refusal also becomes a failed UI row.
    void Finish(Frame& frame, PendingAnnouncement pending, Announcements::Result result, std::string reason)
    {
      if (result != Announcements::Result::Published)
        Emit(
          frame,
          Bridge::AnnouncementResultEvent{
              .channelId = Bridge::Id(pending.channelId),
              .source    = Bridge::ModLabel(pending.signature),
              .text      = pending.text,
              .error     = Bridge::ClipError(reason)
          });
      frame.announcementResults.push_back({std::move(pending.signature), std::move(pending.text), result, std::move(reason)});
    }

    bool Settle(Frame& frame, std::uint64_t requestId, Announcements::Result result, std::string reason)
    {
      const auto found = pendingAnnouncements.find(requestId);
      if (found == pendingAnnouncements.end()) return false;
      auto pending = std::move(found->second);
      pendingAnnouncements.erase(found);
      Finish(frame, std::move(pending), result, std::move(reason));
      return true;
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

    void PublishStatus(const ClientStatus& status, const UiSettings& settings, Frame& frame)
    {
      const bool first = !lastStatus;
      const bool connectionChanged =
        first || Bridge::PhaseName(*lastStatus) != Bridge::PhaseName(status) || lastStatus->serverName != status.serverName;
      const bool authChanged = first || lastStatus->authSequence != status.authSequence ||
                               lastStatus->authenticating != status.authenticating || lastStatus->authOperation != status.authOperation ||
                               lastStatus->authFailure != status.authFailure || lastStatus->error != status.error ||
                               lastStatus->savedLogin != status.savedLogin || lastStatus->savedUsername != status.savedUsername;
      if (connectionChanged) Emit(frame, Bridge::ConnectionState(status));
      if (authChanged || connectionChanged) Emit(frame, Bridge::AuthState(status, settings.streamerMode));
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
    std::unordered_map<Domain::ChatChannelId, Domain::ChatChannelKind> channels;
    std::optional<Domain::ChatChannelId>           globalChannel;
    Domain::ChatMessageId                          bubbleFloor{};
    Players                                        players;
    Marks                                          visibleMarks;
    Marks                                          ownMarks;
    bool                                           ownMarksChanged{};
    std::uint64_t                                  marksGeneration{};
    std::unordered_map<std::uint64_t, PendingChat> pendingChats;
    std::unordered_map<std::uint64_t, PendingMark> pendingMarks;
    std::unordered_map<std::uint64_t, PendingAnnouncement> pendingAnnouncements;
    std::optional<ClientStatus>                    lastStatus;
    bool                                           needsSnapshot{true};
    bool                                           snapshotRequested{};
    bool                                           refreshing{};
    std::uint64_t                                  refreshGeneration{};
    // Profiles of retained authors, so offline players can be ignored by name.
    static constexpr std::size_t                             MaxKnownAuthors = 2048;
    std::unordered_map<Domain::PlayerId, Domain::PlayerData> authors;
    Names                                                    names;
  };

}
