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
      std::vector<Bridge::HostEvent> events;  // In delivery order; the adapter encodes them.
      std::vector<std::string>       notes;   // Diagnostics for the host logger.
      // Live publications of other players in the global channel, confirmed by
      // the server in this Process call: never snapshot history, replays or
      // announcements. Consumed by the firefly chat bubbles.
      std::vector<Domain::ChatMessage> freshMessages;
      // Final results of announcements requested through the plugin API.
      std::vector<Announcements::Outcome> announcementResults;
      bool                                snapshot{};
      bool                                playersChanged{};
      // The set of marks the game draws changed in this Process call.
      bool visibleMarksChanged{};
      // The server confirmed a switch of "hide my name": the preference to keep.
      std::optional<Domain::HiddenIdentity> hideIdentity;
      // The server refused to open a session with hidden names; reconnecting
      // with the same preference would be refused again.
      bool identityRefused{};
    };

    using Players = std::unordered_map<Domain::PlayerId, Domain::Player>;
    using Marks   = std::map<Domain::GroundMarkId, Domain::GroundMark>;

    // One call per Drain. Posts RequestSnapshot itself when a view or a missed
    // delta requires a fresh full state. hiding is the saved hide-my-name choice.
    void Process(
      ClientExchange&        exchange,
      const ClientOutput&    output,
      const UiSettings&      settings,
      Domain::HiddenIdentity hiding,
      Frame&                 frame)
    {
      const bool ready = output.status.Ready();
      serverName       = output.status.serverName;
      // The core drops the request with the session; the server may or may not have stored it.
      if (!ready && std::erase_if(pending, [](const auto& entry) {
                      return std::holds_alternative<PendingName>(entry.second.request);
                    }) != 0)
        nameError = "Соединение прервано до ответа сервера";
      for (const auto& update : output.state.updates)
        std::visit([&](const auto& value) { Apply(value, settings, ready, frame); }, update);

      if (frame.playersChanged && !frame.snapshot) Emit(frame, Bridge::PlayersEvent{.players = PlayerList(settings)});

      for (const auto& result : output.results)
        Resolve(frame, result);
      pseudonym = output.status.pseudonym;
      if (ownMarksChanged && !frame.snapshot) Emit(frame, Bridge::GroundMarksEvent{.marks = OwnMarkList(settings)});
      ownMarksChanged = false;
      if (frame.visibleMarksChanged && !frame.snapshot && Ready()) Emit(frame, Bridge::NearbyMarksEvent{.marks = NearbyMarkList(settings)});

      PublishStatus(output.status, settings, frame);
      if (auto identity = Identity(frame.hideIdentity.value_or(hiding)); identity != lastIdentity)
      {
        Emit(frame, identity);
        lastIdentity = std::move(identity);
      }
      if (auto name = NameEvent(); name != lastName)
      {
        Emit(frame, name);
        lastName = std::move(name);
        nameChanged.reset();
      }
      RequestSnapshotIfNeeded(exchange, frame);
    }

    // The state of an own display name change as the UI shows it.
    Bridge::DisplayNameEvent NameEvent() const
    {
      return Bridge::DisplayNameEvent{.pending = Waiting<PendingName>() != nullptr, .changed = nameChanged, .error = nameError};
    }

    // A ready session asks the server; one change at a time. The own profile
    // changes through the players list once the server applies it.
    std::expected<void, std::string> ChangeDisplayName(ClientExchange& exchange, std::string displayName)
    {
      if (Waiting<PendingName>()) return std::unexpected{"Ожидание ответа сервера"};
      auto sent = Submit(exchange, PendingName{}, [&](std::uint64_t id) {
        return Dreamsleeve::Client::ChangeDisplayName{id, std::move(displayName)};
      });
      if (!sent) return std::unexpected{std::string{Refusal(sent.error())}};
      nameError.reset();
      nameChanged.reset();
      return {};
    }

    // A refusal the UI shows under the name field, e.g. from a local check.
    void SetNameError(std::string error)
    {
      nameError = error.empty() ? std::nullopt : std::optional{Bridge::ClipError(error)};
    }

    // "Hide my name from other players" as the UI shows it: the preference, or
    // the requested value while the server has not answered; the pseudonym
    // only for a ready session.
    Bridge::IdentityEvent Identity(Domain::HiddenIdentity preference) const
    {
      Bridge::IdentityEvent event;
      const auto*           waiting = Waiting<PendingIdentity>();
      event.pending                 = waiting != nullptr;
      event.mode                    = std::string{Bridge::HidingName(waiting ? waiting->hiding : preference)};
      if (Ready()) event.pseudonym = pseudonym;
      event.error = identityError;
      return event;
    }

    // A ready session asks the server; the preference changes only once the
    // server confirms (Frame::hideIdentity). One switch at a time.
    std::expected<void, std::string> SetIdentityVisibility(ClientExchange& exchange, Domain::HiddenIdentity hiding)
    {
      if (Waiting<PendingIdentity>()) return std::unexpected{"Ожидание ответа сервера"};
      auto sent =
        Submit(exchange, PendingIdentity{hiding}, [&](std::uint64_t id) { return Dreamsleeve::Client::SetIdentityVisibility{id, hiding}; });
      if (!sent) return std::unexpected{std::string{Refusal(sent.error())}};
      identityError.reset();
      return {};
    }

    // A refusal the UI shows under the switch, e.g. from a local check.
    void SetIdentityError(std::string error)
    {
      identityError = Bridge::ClipError(error);
    }

    // Marks the server currently shows this player, as Core projects them.
    const Marks& VisibleMarks() const noexcept
    {
      return visibleMarks;
    }

    // Every mark of the player wherever it stands, as the server last listed
    // them (protocol v9): placed here or in earlier sessions, far or near.
    const Marks& OwnMarks() const noexcept
    {
      return ownMarks;
    }

    // A note where the player stands, requested by the web UI.
    std::expected<void, std::string> PlaceGroundNote(
      ClientExchange&                    exchange,
      std::string                        uiRequestId,
      std::string                        text,
      const Domain::GroundMarkPlacement& placement,
      const Domain::GameDate&            gameDate)
    {
      return Posted(Submit(exchange, PendingMark{std::move(uiRequestId), MarkRequest::Note}, [&](std::uint64_t id) {
        return Dreamsleeve::Client::PlaceGroundNote{id, std::move(text), placement, gameDate};
      }));
    }

    // The place the character died, reported by the game once per death.
    std::expected<void, std::string> ReportDeath(
      ClientExchange&                    exchange,
      std::string                        label,
      const Domain::GroundMarkPlacement& placement,
      const Domain::GameDate&            gameDate)
    {
      return Posted(Submit(exchange, PendingMark{{}, MarkRequest::Death}, [&](std::uint64_t id) {
        return Dreamsleeve::Client::ReportDeath{id, std::move(label), placement, gameDate};
      }));
    }

    std::expected<void, std::string> RemoveGroundMark(ClientExchange& exchange, std::string uiRequestId, Domain::GroundMarkId markId)
    {
      return Posted(Submit(exchange, PendingMark{std::move(uiRequestId), MarkRequest::Remove}, [&](std::uint64_t id) {
        return Dreamsleeve::Client::RemoveGroundMark{id, markId};
      }));
    }

    std::size_t PendingMarkCount() const noexcept
    {
      return Count<PendingMark>();
    }

    // The next Process must deliver a full snapshot; old correlations are dropped
    // so a reply cannot reach a view that no longer exists.
    void ResetView()
    {
      needsSnapshot     = true;
      snapshotRequested = false;
      refreshing        = false;
      // Chat sends and mark requests lose their view. Death reports have no UI
      // correlation and settle silently either way.
      std::erase_if(pending, [](const auto& entry) {
        const auto* mark = std::get_if<PendingMark>(&entry.second.request);
        return std::holds_alternative<PendingChat>(entry.second.request) || (mark && mark->request != MarkRequest::Death);
      });
      lastStatus.reset();
      lastIdentity.reset();
      // The UI starts with nothing pending; only a difference is sent.
      lastName = {};
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

    // Core refuses a channel that is not the session's global one.
    std::expected<void, std::string> SendChat(ClientExchange& exchange, const Bridge::Commands::SendChat& command)
    {
      return Posted(Submit(exchange, PendingChat{command.requestId}, [&](std::uint64_t id) {
        return Dreamsleeve::Client::SendChat{id, command.channelId.value, command.text};
      }));
    }

    // Main thread. The plugin API already checked the text encoding and the
    // label; Core checks the server's limits, the server everything else.
    Announcements::Result PostAnnouncement(ClientExchange& exchange, Announcements::Request request)
    {
      const auto system = ChannelOf(Domain::ChatChannelKind::System);
      if (!system) return Announcements::Result::NotConnected;
      PendingAnnouncement waiting{*system, request.signature, request.text};
      const auto          sent = Submit(exchange, std::move(waiting), [&](std::uint64_t id) {
        return Dreamsleeve::Client::PostAnnouncement{
            id,
            *system,
            std::move(request.text),
            request.kind,
            request.source,
            std::move(request.signature)
        };
      });
      if (sent) return Announcements::Result::Queued;
      switch (sent.error())
      {
        case PostError::NotReady:
        case PostError::Closed:
          return Announcements::Result::NotConnected;
        case PostError::Full:
          return Announcements::Result::Busy;
        case PostError::NoRequestId:
          break;
      }
      return Announcements::Result::Failed;
    }

    bool Ready() const noexcept
    {
      return lastStatus && lastStatus->Ready();
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
      return Count<PendingChat>();
    }

private:

    // What a request of the UI, the game or a mod waits for. Each kind reads
    // its own success from the result and shows a refusal its own way.
    struct PendingChat
    {
      std::string uiRequestId;
    };

    struct PendingAnnouncement
    {
      Domain::ChatChannelId channelId{};
      std::string           signature;
      std::string           text;
    };

    enum class MarkRequest
    {
      Note,
      Death,
      Remove
    };

    struct PendingMark
    {
      std::string uiRequestId;  // Empty for a death report.
      MarkRequest request{};
    };

    struct PendingIdentity
    {
      Domain::HiddenIdentity hiding{Domain::HiddenIdentity::None};
    };

    struct PendingName
    {};

    using PendingRequest = std::variant<PendingChat, PendingAnnouncement, PendingMark, PendingIdentity, PendingName>;

    struct Pending
    {
      std::uint64_t  generation{};
      PendingRequest request;
    };

    enum class PostError
    {
      NotReady,
      NoRequestId,
      Full,
      Closed
    };

    static std::string_view Refusal(PostError error)
    {
      switch (error)
      {
        case PostError::NotReady:
          return "Нет соединения с сервером";
        case PostError::NoRequestId:
          return "Идентификаторы запросов исчерпаны";
        case PostError::Full:
        case PostError::Closed:
          break;
      }
      return "Очередь команд заполнена";
    }

    static std::expected<void, std::string> Posted(std::expected<void, PostError> sent)
    {
      if (!sent) return std::unexpected{std::string{Refusal(sent.error())}};
      return {};
    }

    // Posts the command make builds for a fresh request ID in a ready session;
    // its result is routed back to request.
    template <class Make>
    std::expected<void, PostError> Submit(ClientExchange& exchange, PendingRequest request, Make&& make)
    {
      if (!Ready()) return std::unexpected{PostError::NotReady};
      const auto requestId = exchange.NextRequestId();
      if (!requestId) return std::unexpected{PostError::NoRequestId};
      const auto posted = exchange.Post({generation, make(*requestId)});
      if (posted == CommandPostResult::Closed) return std::unexpected{PostError::Closed};
      if (posted != CommandPostResult::Queued) return std::unexpected{PostError::Full};
      pending.emplace(*requestId, Pending{generation, std::move(request)});
      return {};
    }

    // The first waiting request of a kind; identity and name wait one at a time.
    template <class Kind>
    const Kind* Waiting() const
    {
      for (const auto& [id, entry] : pending)
        if (const auto* request = std::get_if<Kind>(&entry.request)) return request;
      return nullptr;
    }

    template <class Kind>
    std::size_t Count() const
    {
      return static_cast<std::size_t>(
        std::ranges::count_if(pending, [](const auto& entry) { return std::holds_alternative<Kind>(entry.second.request); }));
    }

    // Routes a result to the request that waits for it. An opening refused for
    // hidden names has none: the preference itself is refused. Game updates
    // carry their own IDs and settle silently.
    void Resolve(Frame& frame, const CommandResult& result)
    {
      const auto found = pending.find(result.requestId);
      if (found == pending.end())
      {
        const auto* rejection = std::get_if<ServerRejection>(&result.outcome);
        if (rejection && rejection->code == RequestRejectionCode::HiddenIdentityNotAllowed)
        {
          identityError         = Bridge::ClipError(Reason(result.outcome));
          frame.identityRefused = true;
        }
        return;
      }
      auto request = std::move(found->second.request);
      pending.erase(found);
      std::visit([&](auto& value) { Resolve(frame, value, result.outcome); }, request);
    }

    // The text a refusal shows; a success of another kind would be a Core fault.
    static std::string Reason(const CommandResult::Outcome& outcome)
    {
      if (const auto* rejection = std::get_if<ServerRejection>(&outcome)) return Bridge::RejectionText(rejection->code, rejection->message);
      if (const auto* failure = std::get_if<CommandFailureCode>(&outcome)) return std::string{Bridge::FailureText(*failure)};
      return "Неожиданный ответ";
    }

    void Resolve(Frame& frame, PendingChat& chat, const CommandResult::Outcome& outcome)
    {
      Bridge::SendResultEvent event;
      event.requestId = std::move(chat.uiRequestId);
      if (const auto* published = std::get_if<MessagePublished>(&outcome))
        event.messageId = Bridge::Id(published->messageId);
      else
        event.error = Reason(outcome);
      Emit(frame, event);
    }

    void Resolve(Frame& frame, PendingAnnouncement& announcement, const CommandResult::Outcome& outcome)
    {
      if (std::holds_alternative<MessagePublished>(outcome))
        Finish(frame, std::move(announcement), Announcements::Result::Published, {});
      else
        Finish(frame, std::move(announcement), ResultOf(outcome), Reason(outcome));
    }

    void Resolve(Frame& frame, PendingMark& mark, const CommandResult::Outcome& outcome)
    {
      const auto* placed = std::get_if<MarkPlaced>(&outcome);
      if (mark.request == MarkRequest::Death)
      {
        frame.notes.push_back(placed ? std::format("Death mark {} placed", placed->markId) : "Death mark refused: " + Reason(outcome));
        return;
      }
      Bridge::MarkResultEvent event;
      event.requestId = std::move(mark.uiRequestId);
      if (std::holds_alternative<MarkRemoved>(outcome))
        event.removed = true;
      else if (placed)
      {
        event.markId = Bridge::Id(placed->markId);
        if (placed->evictedId) event.evictedId = Bridge::Id(*placed->evictedId);
      }
      else
        event.error = Bridge::ClipError(Reason(outcome));
      Emit(frame, event);
    }

    // A refused switch returns to the server's state; the reason stays under it.
    void Resolve(Frame& frame, PendingIdentity&, const CommandResult::Outcome& outcome)
    {
      if (const auto* changed = std::get_if<IdentityChanged>(&outcome))
      {
        identityError.reset();
        frame.hideIdentity = changed->hiding;
      }
      else
        identityError = Bridge::ClipError(Reason(outcome));
    }

    void Resolve(Frame&, PendingName&, const CommandResult::Outcome& outcome)
    {
      if (const auto* changed = std::get_if<NameChanged>(&outcome))
      {
        nameError.reset();
        nameChanged = changed->displayName;
      }
      else if (const auto* rejection = std::get_if<ServerRejection>(&outcome))
        nameError = Bridge::ClipError(Bridge::DisplayNameRejectionText(rejection->code, rejection->message));
      else
        nameError = Bridge::ClipError(Reason(outcome));
    }

    std::vector<Bridge::UiGroundMark> OwnMarkList(const UiSettings& settings) const
    {
      std::vector<Bridge::UiGroundMark> list;
      list.reserve(ownMarks.size());
      for (const auto& [id, mark] : ownMarks)
        list.push_back(Bridge::ToUiGroundMark(mark, settings, true));
      return list;
    }

    // Marks the server shows here, named like every other surface; marks of
    // ignored players are left out, as the game leaves them undrawn.
    std::vector<Bridge::UiGroundMark> NearbyMarkList(const UiSettings& settings)
    {
      std::vector<Bridge::UiGroundMark> list;
      list.reserve(visibleMarks.size());
      for (const auto& [id, mark] : visibleMarks)
      {
        const bool own = selfId && mark.author.playerId == *selfId;
        if (!own && names.Ignored(mark.author.playerId)) continue;
        auto entry   = Bridge::ToUiGroundMark(mark, settings, own);
        entry.author = names.NameFor(mark.author.playerId, mark.author, mark.characterName, settings);
        list.push_back(std::move(entry));
      }
      return list;
    }

    void ReplaceOwn(const std::vector<Domain::GroundMark>& marks)
    {
      ownMarks.clear();
      for (const auto& mark : marks)
        ownMarks.insert_or_assign(mark.markId, mark);
      ownMarksChanged = true;
    }

    static void Emit(Frame& frame, Bridge::HostEvent event)
    {
      frame.events.push_back(std::move(event));
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
      visibleMarks.clear();
      for (const auto& mark : snapshot.groundMarks.marks)
        visibleMarks.insert_or_assign(mark.markId, mark);
      ReplaceOwn(snapshot.groundMarks.own);
      ownMarksChanged           = false;
      frame.visibleMarksChanged = true;

      // A new generation cannot complete requests of the previous session.
      std::erase_if(pending, [&](const auto& entry) {
        if (entry.second.generation == generation) return false;
        if (const auto* announcement = std::get_if<PendingAnnouncement>(&entry.second.request))
          Finish(frame, *announcement, Announcements::Result::Failed, "Доставка неизвестна: сессия сменилась");
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
      event.selfId               = Bridge::Id(selfId.value_or(Domain::InvalidId));
      event.serverName           = serverName;
      event.settings             = settings;
      event.groundMarksSupported = true;  // Protocol v8: every Ready session carries marks.
      event.groundMarks          = OwnMarkList(settings);
      event.nearbyMarks          = NearbyMarkList(settings);
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
            visibleMarks.erase(id);
        else if (const auto* added = std::get_if<GroundMarksAdded>(&change))
          for (const auto& mark : added->marks)
            visibleMarks.insert_or_assign(mark.markId, mark);
      }
      if (delta.ownGroundMarks) ReplaceOwn(*delta.ownGroundMarks);

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

    static Announcements::Result ResultOf(const CommandResult::Outcome& outcome)
    {
      if (const auto* rejection = std::get_if<ServerRejection>(&outcome))
        return rejection->code == RequestRejectionCode::RateLimited ? Announcements::Result::RateLimited : Announcements::Result::Rejected;
      if (const auto* failure = std::get_if<CommandFailureCode>(&outcome)) switch (*failure)
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
    void Finish(Frame& frame, PendingAnnouncement announcement, Announcements::Result result, std::string reason)
    {
      if (result != Announcements::Result::Published)
        Emit(
          frame,
          Bridge::AnnouncementResultEvent{
              .channelId = Bridge::Id(announcement.channelId),
              .source    = Bridge::ModLabel(announcement.signature),
              .text      = announcement.text,
              .error     = Bridge::ClipError(reason)
          });
      frame.announcementResults.push_back({std::move(announcement.signature), std::move(announcement.text), result, std::move(reason)});
    }

    void PublishStatus(const ClientStatus& status, const UiSettings& settings, Frame& frame)
    {
      const bool first = !lastStatus;
      const bool connectionChanged =
        first || Bridge::PhaseOf(*lastStatus) != Bridge::PhaseOf(status) || lastStatus->serverName != status.serverName;
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

    std::uint64_t                                                      generation{};
    std::optional<Domain::PlayerId>                                    selfId;
    std::string                                                        serverName;
    std::unordered_map<Domain::ChatChannelId, Domain::ChatChannelKind> channels;
    std::optional<Domain::ChatChannelId>                               globalChannel;
    Domain::ChatMessageId                                              bubbleFloor{};
    Players                                                            players;
    Marks                                                              visibleMarks;
    Marks                                                              ownMarks;
    bool                                                               ownMarksChanged{};
    std::unordered_map<std::uint64_t, Pending>                         pending;
    std::optional<std::string>                                         pseudonym;
    std::optional<std::string>                                         identityError;
    std::optional<Bridge::IdentityEvent>                               lastIdentity;
    std::optional<std::string>                                         nameError;
    std::optional<std::string>                                         nameChanged;
    Bridge::DisplayNameEvent                                           lastName;
    std::optional<ClientStatus>                                        lastStatus;
    bool                                                               needsSnapshot{true};
    bool                                                               snapshotRequested{};
    bool                                                               refreshing{};
    std::uint64_t                                                      refreshGeneration{};
    // Profiles of retained authors, so offline players can be ignored by name.
    static constexpr std::size_t                             MaxKnownAuthors = 2048;
    std::unordered_map<Domain::PlayerId, Domain::PlayerData> authors;
    Names                                                    names;
  };

}
