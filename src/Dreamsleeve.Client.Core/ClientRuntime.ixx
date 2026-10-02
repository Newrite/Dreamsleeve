export module Dreamsleeve.Client.Runtime;

import std;
export import Dreamsleeve.Client.ProtocolCodec;
export import DreamNet.Client;
import DreamNet.Core;
import Dreamsleeve.Client.Utils;

export namespace Dreamsleeve::Client
{

  // Own on the networking thread; DreamNetRuntime must outlive this object.
  // Exchange is the only shared access to model data and session phase.
  class ClientRuntime final
  {
public:

    using Error = std::variant<DreamNetError, Wire::Error, Domain::Error>;
    template <class T>
    using Result = std::expected<T, Error>;
    using Ptr    = std::unique_ptr<ClientRuntime>;

    // The configuration passed ValidateClientSettings.
    static Ptr Create(Configuration config, ClientExchange& exchange)
    {
      Wire::ProtocolCodec codec{config};
      return Ptr{new ClientRuntime(std::move(config), std::move(codec), exchange)};
    }

    ~ClientRuntime()
    {
      if (transport) transport->Abort(DisconnectReason::ClientShutdown);
    }

    ClientRuntime(const ClientRuntime&)            = delete;
    ClientRuntime& operator=(const ClientRuntime&) = delete;

    SessionPhase Phase() const noexcept
    {
      return phase;
    }

    // While no session is open, keeps a connection to the server as a guest,
    // so the server counts this client online. The link is internal: the
    // exchange phase stays idle, and a session opens on it. Off, an idle link
    // is closed gracefully (see Closing).
    void KeepGuest(bool enabled)
    {
      keepGuest = enabled;
      if (enabled || !SessionIdle(phase) || !transport) return;
      if (transport->State() == ClientState::Connected && transport->BeginDisconnect()) return;
      if (transport->State() != ClientState::Disconnecting) DropGuest();
    }

    // A close is still being served: the session's or the guest link's.
    bool Closing() const noexcept
    {
      return phase == SessionPhase::Disconnecting || GuestLink() == ClientState::Disconnecting;
    }

    Result<void> Connect(std::string sessionTicket)
    {
      if (!SessionIdle(phase))
        return std::unexpected{DreamNetError::Make(DreamNetErrorCode::InvalidOperation, "A session is already active")};

      if (!exchange.CanAcceptReplies())
        return std::unexpected{DreamNetError::Make(DreamNetErrorCode::InvalidOperation, "Drain command results before opening a session")};

      const auto requestId = exchange.NextRequestId();
      if (!requestId) return std::unexpected{DreamNetError::Make(DreamNetErrorCode::InvalidOperation, "Request IDs exhausted")};

      // A guest link carries the session: connected, it opens at once; still
      // connecting, it opens on Connected. Otherwise a new connection is made.
      const auto link  = GuestLink();
      const bool reuse = link == ClientState::Connected || link == ClientState::Connecting;
      if (!reuse)
      {
        auto created = NewTransport();
        if (!created) return Fail(created.error());
        transport = std::move(*created);
      }

      opening     = Wire::OpenSession{*requestId, std::move(sessionTicket), exchange.HideIdentity()};
      lastRequest = *requestId;
      ResetSession();
      auto published = Publish();
      if (!published) return Fail(published.error());

      if (!reuse)
        if (auto connected = transport->BeginConnect(); !connected) return Fail(connected.error());

      SetPhase(SessionPhase::Connecting);
      if (link == ClientState::Connected)
        if (auto sent = SendOpening(); !sent) return Fail(std::move(sent.error()));

      return {};
    }

    Result<void> Disconnect()
    {
      if (!transport || SessionIdle(phase)) return {};
      if (phase == SessionPhase::Disconnecting) return {};

      if (phase == SessionPhase::Connecting)
      {
        transport->Abort(DisconnectReason::ClientShutdown);
        return Clear(SessionPhase::Disconnected);
      }

      auto result = transport->BeginDisconnect();
      if (!result) return Fail(result.error());

      return Clear(SessionPhase::Disconnecting);
    }

    // A caller-owned loop drives network work. No hidden threads or model callbacks.
    Result<void> Poll(TimeOutMs waitMs = 0)
    {
      auto commandsResult = ProcessCommands();
      if (SessionIdle(phase)) ServeGuest(waitMs);
      if (!transport || SessionIdle(phase)) return commandsResult;

      events.clear();

      if (phase == SessionPhase::Opening)
      {
        const auto left = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - Clock::now()).count();
        waitMs          = left <= 0 ? 0 : std::min(waitMs, static_cast<TimeOutMs>(left));
      }

      auto polled = transport->Poll(events, waitMs);
      // A transport failure invalidates the whole unprocessed batch.
      if (!polled) return Fail(polled.error());

      for (auto& event : events)
      {
        auto result = std::visit([this](auto& value) { return Handle(value); }, event);
        if (!result) return Fail(std::move(result.error()));
      }

      events.clear();

      if (phase == SessionPhase::Opening && Clock::now() >= deadline)
        return Fail(DreamNetError::Make(DreamNetErrorCode::ConnectTimeout, "OpenSession timed out"));

      auto sampled = SendMovement();
      return sampled ? commandsResult : sampled;
    }

private:

    using Clock = std::chrono::steady_clock;

    // Guest links are made again after these waits, doubling: a server that
    // drops guests (a full one) is not called in a loop. A session starting
    // resets them, so the guest after its end connects at once.
    static constexpr auto GuestRetryMinimum = std::chrono::seconds{5};
    static constexpr auto GuestRetryMaximum = std::chrono::seconds{60};

    // What a sent request waits for. Chat, announcements and deletions remember
    // their channel and settle with its chat.
    enum class PendingKind
    {
      Chat,
      Update,
      Mark,
      Identity,
      Name,
      Moderation,
      Deletion,
      Guild
    };

    struct PendingRequest
    {
      PendingKind           kind{};
      Domain::ChatChannelId channelId{};
    };

    ClientRuntime(Configuration settings, Wire::ProtocolCodec codec, ClientExchange& exchange)
        : config(std::move(settings)),
          codec(std::move(codec)),
          exchange(exchange),
          model(config.maxPendingMovementSamples)
    {}

    void SetPhase(SessionPhase value)
    {
      phase = value;
      exchange.PublishPhase(value);
    }

    // Forgets the previous session, its pending requests included.
    void ResetSession()
    {
      serverName.clear();
      pending.clear();
      ResetMovement();
      earlyChat.clear();
      guilds.reset();
      model.ResetSession();
      kinds.Clear();
      kindSweep = KindSweepFloor;
      exchange.PublishIdentity(std::nullopt, Domain::HiddenIdentity::None);
      exchange.PublishMute(std::nullopt);
      exchange.PublishRole(Domain::PlayerRole::Player);
    }

    // result: the terminal answer that ends the session, published with its end.
    Result<void> Clear(SessionPhase value, std::optional<CommandResult> result = std::nullopt)
    {
      ResetSession();
      phase          = value;
      auto published = Publish(true, std::move(result));
      if (!published) SetPhase(SessionPhase::Faulted);
      return published;
    }

    Result<void> Publish(bool requestSnapshot = false, std::optional<CommandResult> result = std::nullopt)
    {
      if (!exchange.Publish(model, requestSnapshot, phase, serverName, std::move(result), guilds))
        return std::unexpected{DreamNetError::Make(DreamNetErrorCode::InvalidOperation, "Command result capacity exhausted")};

      return {};
    }

    // The server's answer to a request, after the state it changed.
    Result<void> Settle(std::uint64_t requestId, CommandResult::Outcome outcome)
    {
      return Publish(false, CommandResult{model.Generation(), requestId, std::move(outcome)});
    }

    Result<void> Fail(Error error)
    {
      if (transport) transport->Abort(DisconnectReason::ProtocolError);

      auto cleared = Clear(SessionPhase::Faulted);
      if (!cleared) return cleared;
      return std::unexpected{std::move(error)};
    }

    static Result<void> Unexpected(std::string field)
    {
      return std::unexpected{
          Wire::Error{Wire::ErrorCode::InvalidEnvelope, std::move(field)}
      };
    }

    Result<void> Handle(ClientConnected&)
    {
      if (phase != SessionPhase::Connecting) return Unexpected("connected");
      return SendOpening();
    }

    Result<void> SendOpening()
    {
      if (transport->NegotiatedChannelCount() < MinimumChannels) return Unexpected("channel_count");

      auto packet = codec.Encode(opening);
      opening.sessionTicket.clear();  // A reconnect must supply a newly issued ticket.
      if (!packet) return std::unexpected{packet.error()};

      auto sent = transport->Send(std::move(*packet));
      if (!sent) return std::unexpected{sent.error()};

      deadline = Clock::now() + std::chrono::milliseconds(config.sessionTimeoutMs);
      guestRetry.Reset();
      SetPhase(SessionPhase::Opening);

      return {};
    }

    // Every new connection resolves the host again, so a changed DNS record
    // applies on the next attempt. A name blocks this thread on the resolver,
    // as the HTTP sign-in does; a failure is a failed attempt, retried as one.
    std::expected<DreamNetClient::Ptr, DreamNetError> NewTransport() const
    {
      auto address = DreamNetAddress::TryResolve(config.serverHost, config.serverPort);
      if (!address) return std::unexpected{std::move(address.error())};
      return DreamNetClient::TryCreate({config.network, *address, config.connectTimeoutMs, config.disconnectTimeoutMs});
    }

    // The state of the connection kept without a session; Disconnected when
    // there is none or a session owns the transport.
    ClientState GuestLink() const noexcept
    {
      return SessionIdle(phase) && transport ? transport->State() : ClientState::Disconnected;
    }

    // Idle phase only. Makes the link when it is due, serves it and joins as
    // a guest on Connected. Its failures stay here: the client is simply
    // offline until the next attempt, and the exchange hears nothing.
    void ServeGuest(TimeOutMs waitMs)
    {
      const auto link = GuestLink();
      if (link == ClientState::Disconnected || link == ClientState::Faulted)
      {
        if (!keepGuest || !guestRetry.Due(Clock::now())) return;
        auto created = NewTransport();
        if (!created) return;
        transport = std::move(*created);
        if (!transport->BeginConnect()) return DropGuest();
      }

      events.clear();
      bool failed = !transport->Poll(events, waitMs);
      // A guest is told nothing but a refusal, and a close needs no answer.
      for (const auto& event : events)
        if (!failed && std::holds_alternative<ClientConnected>(event)) failed = !JoinAsGuest();
      events.clear();
      if (failed) DropGuest();
    }

    bool JoinAsGuest()
    {
      if (!keepGuest || transport->NegotiatedChannelCount() < MinimumChannels) return false;
      const auto requestId = exchange.NextRequestId();
      if (!requestId) return false;
      auto packet = codec.Encode(Wire::JoinAsGuest{*requestId});
      return packet && transport->Send(std::move(*packet));
    }

    void DropGuest()
    {
      if (transport) transport->Abort(DisconnectReason::ClientShutdown);
      transport.reset();
    }

    Result<void> Handle(ClientClosed&)
    {
      return Clear(SessionPhase::Disconnected);
    }

    Result<void> Handle(ClientReceived& received)
    {
      if (phase == SessionPhase::Disconnecting || SessionIdle(phase))
        return {};  // A terminal reply may have closed the session earlier in this batch.
      if (received.channelId > 2) return Unexpected("channel");
      const auto channel = static_cast<Wire::Channel>(received.channelId);
      const auto flags   = received.packet.Flags();
      if (
        PacketFlags::HasFlag(flags, PacketFlag::Unsequenced) ||
        (channel != Wire::Channel::Realtime && !PacketFlags::HasFlag(flags, PacketFlag::Reliable)))
        return Unexpected("delivery");

      auto response = codec.Decode(received.packet.DataBytesView(), channel, kinds);
      if (!response) return std::unexpected{response.error()};

      if (auto* rejected = std::get_if<Wire::RequestRejected>(&*response))
      {
        const auto found = pending.find(rejected->requestId);
        const bool chat =
          found != pending.end() && (found->second.kind == PendingKind::Chat || found->second.kind == PendingKind::Deletion);
        const auto expected = chat ? Wire::Channel::Chat : Wire::Channel::Control;
        if (channel != expected) return Unexpected("rejection_channel");
      }
      return std::visit([this](auto& value) { return Receive(value); }, *response);
    }

    Result<void> Receive(Wire::SessionOpened& opened)
    {
      if (phase != SessionPhase::Opening || opened.requestId != opening.requestId) return Unexpected("request_id");

      const auto generation = model.Generation();
      for (const auto& channel : opened.channels)
      {
        auto registered = model.RegisterChannel(channel.channelId, config.chatCapacity, channel.kind);
        if (!registered) return std::unexpected{registered.error()};
      }

      auto online = model.Apply(generation, OnlinePlayersReplaced{std::move(opened.players)});
      if (!online) return std::unexpected{online.error()};

      if (!model.FindPlayer(opened.selfPlayerId)) return Unexpected("self_player_id");

      for (auto& channel : opened.channels)
      {
        auto chat = model.Apply(generation, ChatMessagesReceived{channel.channelId, std::move(channel.recentMessages)});
        if (!chat) return std::unexpected{chat.error()};
      }

      auto self = model.SetSelfPlayer(generation, opened.selfPlayerId);
      if (!self) return std::unexpected{self.error()};

      for (auto& kind : opened.kinds)
        kinds.Define(std::move(kind));

      serverName         = std::move(opened.serverName);
      announcementPolicy = std::move(opened.announcements);
      exchange.PublishIdentity(std::move(opened.ownPseudonym), opened.hiding);
      exchange.PublishMute(std::move(opened.mute));
      exchange.PublishRole(opened.role);
      phase = SessionPhase::Ready;
      for (const auto& change : earlyChat)
      {
        auto applied = model.Apply(generation, change);
        if (!applied) return std::unexpected{applied.error()};
      }
      earlyChat.clear();
      return Publish(true);
    }

    // Replaces the guilds; their channels are registered anew with their tails.
    Result<void> Receive(Wire::GuildsSnapshot& snapshot)
    {
      if (phase != SessionPhase::Ready) return Unexpected("guilds_snapshot");

      std::vector<Domain::Guild> list;
      list.reserve(snapshot.guilds.size());
      for (const auto& opened : snapshot.guilds)
        list.push_back(opened.guild);
      auto book = GuildBook::TryCreate(std::move(list), std::move(snapshot.invites), snapshot.limits);
      if (!book) return std::unexpected{book.error()};

      if (guilds)
        for (const auto& guild : guilds->Guilds())
          if (auto closed = model.UnregisterChannel(guild.channelId); !closed) return std::unexpected{closed.error()};
      for (auto& opened : snapshot.guilds)
        if (auto channel = OpenGuildChannel(opened); !channel) return channel;

      guilds = std::make_shared<const GuildBook>(std::move(*book));
      return Publish();
    }

    // Every change follows the snapshot and names guilds the book knows; a
    // guild's channel comes and goes with it.
    Result<void> Receive(Wire::GuildChanged& changed)
    {
      if (phase != SessionPhase::Ready || !guilds) return Unexpected("guild_changed");

      auto book   = std::make_shared<GuildBook>(*guilds);
      auto result = std::visit([&](auto& change) { return Change(*book, change); }, changed.change);
      if (!result) return result;

      guilds = std::move(book);
      return Publish();
    }

    Result<void> Change(GuildBook& book, Wire::GuildAdded& added)
    {
      if (auto stored = book.Add(added.guild.guild); !stored) return std::unexpected{stored.error()};
      return OpenGuildChannel(added.guild);
    }

    Result<void> Change(GuildBook& book, const Wire::GuildRemoved& removed)
    {
      const auto* guild = book.Find(removed.guildId);
      if (!guild) return Unexpected("guild_id");
      if (auto closed = model.UnregisterChannel(guild->channelId); !closed) return std::unexpected{closed.error()};
      return Checked(book.Remove(removed.guildId, removed.reason));
    }

    Result<void> Change(GuildBook& book, Wire::GuildMemberUpdated& updated)
    {
      return Checked(book.PutMember(updated.guildId, std::move(updated.member)));
    }

    Result<void> Change(GuildBook& book, const Wire::GuildMemberRemoved& removed)
    {
      return Checked(book.RemoveMember(removed.guildId, removed.playerId));
    }

    Result<void> Change(GuildBook& book, Wire::GuildInvited& invited)
    {
      book.PutInvite(std::move(invited.invite));
      return {};
    }

    Result<void> Change(GuildBook& book, const Wire::GuildInviteRemoved& removed)
    {
      return Checked(book.RemoveInvite(removed.guildId));
    }

    static Result<void> Checked(Domain::OperationResult result)
    {
      if (!result) return std::unexpected{std::move(result.error())};
      return {};
    }

    Result<void> OpenGuildChannel(Wire::GuildOpened& opened)
    {
      const auto channelId = opened.guild.channelId;
      if (auto registered = model.RegisterChannel(channelId, config.chatCapacity, Domain::ChatChannelKind::Guild); !registered)
        return std::unexpected{registered.error()};
      return Checked(model.Apply(model.Generation(), ChatMessagesReceived{channelId, std::move(opened.recentMessages)}));
    }

    Result<void> Receive(Wire::GuildCommandDone& done)
    {
      if (!TakePending(done.requestId, PendingKind::Guild)) return Unexpected("request_id");
      return Settle(done.requestId, GuildDone{done.guildId});
    }

    Result<void> Receive(Wire::MuteChanged& changed)
    {
      if (phase != SessionPhase::Ready) return Unexpected("mute_changed");
      exchange.PublishMute(std::move(changed.mute));
      return {};
    }

    Result<void> Receive(Wire::RoleChanged& changed)
    {
      if (phase != SessionPhase::Ready) return Unexpected("role_changed");
      exchange.PublishRole(changed.role);
      return {};
    }

    Result<void> Receive(Wire::SanctionIssued& issued)
    {
      if (!TakePending(issued.requestId, PendingKind::Moderation)) return Unexpected("request_id");
      return Settle(issued.requestId, Sanctioned{std::move(issued.sanction)});
    }

    Result<void> Receive(Wire::SanctionLifted& lifted)
    {
      if (!TakePending(lifted.requestId, PendingKind::Moderation)) return Unexpected("request_id");
      return Settle(lifted.requestId, Lifted{lifted.playerId, lifted.kind});
    }

    Result<void> Receive(Wire::PlayerKicked& kicked)
    {
      if (!TakePending(kicked.requestId, PendingKind::Moderation)) return Unexpected("request_id");
      return Settle(kicked.requestId, Kicked{kicked.playerId});
    }

    Result<void> Receive(Wire::SanctionList& listed)
    {
      if (!TakePending(listed.requestId, PendingKind::Moderation)) return Unexpected("request_id");
      return Settle(listed.requestId, SanctionsListed{std::move(listed.sanctions)});
    }

    // The list is of the named author only; any other is a protocol fault.
    Result<void> Receive(Wire::PlayerMarks& listed)
    {
      if (!TakePending(listed.requestId, PendingKind::Moderation)) return Unexpected("request_id");
      for (const auto& mark : listed.marks)
        if (mark.author.playerId != listed.playerId) return Unexpected("author");
      return Settle(listed.requestId, MarksListed{listed.playerId, std::move(listed.marks)});
    }

    Result<void> Receive(Wire::PlayerMarksCleared& cleared)
    {
      if (!TakePending(cleared.requestId, PendingKind::Moderation)) return Unexpected("request_id");
      return Settle(cleared.requestId, MarksCleared{cleared.playerId, cleared.removed});
    }

    // Everyone drops the message through the same delta; the moderator's copy
    // also settles the deletion.
    Result<void> Receive(Wire::ChatMessageRemoved& removed)
    {
      const ChatMessageDeleted deleted{removed.channelId, removed.messageId};
      if (!removed.requestId) return ReceiveChat(deleted);

      const auto request = TakePending(*removed.requestId, PendingKind::Deletion);
      if (!request || request->channelId != removed.channelId) return Unexpected("request_id");
      auto applied = model.Apply(model.Generation(), deleted);
      if (!applied) return std::unexpected{applied.error()};
      return Settle(*removed.requestId, MessageDeleted{removed.channelId, removed.messageId});
    }

    // The server closes the connection next; ClientClosed ends the session.
    Result<void> Receive(Wire::SessionEnded& ended)
    {
      if (phase != SessionPhase::Opening && phase != SessionPhase::Ready) return Unexpected("session_ended");
      exchange.PublishSessionEnd(std::move(ended.end));
      return {};
    }

    Result<void> Receive(Wire::RequestRejected& rejected)
    {
      if (phase == SessionPhase::Opening)
      {
        if (rejected.requestId != opening.requestId) return Unexpected("request_id");

        // Opening was refused: the full terminal reply is already received.
        // Notify the peer best-effort, without racing its own graceful close.
        CommandResult result{model.Generation(), rejected.requestId, std::move(rejected.rejection)};
        transport->Abort(DisconnectReason::ClientShutdown);
        return Clear(SessionPhase::Disconnected, std::move(result));
      }

      if (phase != SessionPhase::Ready || pending.erase(rejected.requestId) == 0) return Unexpected("request_id");

      if (rejected.requestId == pendingLocation) ResetMovement();
      return Settle(rejected.requestId, std::move(rejected.rejection));
    }

    Result<void> Receive(Wire::ChatAccepted& accepted)
    {
      const auto request = TakePending(accepted.requestId, PendingKind::Chat);
      if (!request) return Unexpected("request_id");
      if (accepted.changes.channelId != request->channelId) return Unexpected("channel_id");
      const auto& author = accepted.changes.messages.front().author;
      if (!author || author->playerId != model.SelfPlayerId()) return Unexpected("author");

      // The server's publication is the only source of accepted chat content.
      // Correlation settles the command; the model path is shared with broadcasts.
      const auto messageId = accepted.changes.messages.front().messageId;
      auto       applied   = model.Apply(model.Generation(), accepted.changes);
      if (!applied) return std::unexpected{applied.error()};

      return Settle(accepted.requestId, MessagePublished{messageId});
    }

    Result<void> Receive(Wire::PlayerUpdateAccepted& accepted)
    {
      if (!TakePending(accepted.requestId, PendingKind::Update)) return Unexpected("request_id");
      if (accepted.requestId == pendingLocation)
      {
        pendingLocation  = Domain::InvalidId;
        movementReady    = latestMovement.has_value();
        nextPlayerSample = {};
      }
      return {};
    }

    // Correlation settles the command; the visible set changes only through
    // the ordinary delta, so the author sees the mark the way everyone does.
    Result<void> Receive(Wire::GroundMarkPlaced& placed)
    {
      if (!TakePending(placed.requestId, PendingKind::Mark)) return Unexpected("request_id");
      if (placed.mark.author.playerId != model.SelfPlayerId()) return Unexpected("author");
      return Settle(placed.requestId, MarkPlaced{placed.mark.markId, placed.evictedId});
    }

    Result<void> Receive(Wire::GroundMarkRemoved& removed)
    {
      if (!TakePending(removed.requestId, PendingKind::Mark)) return Unexpected("request_id");
      return Settle(removed.requestId, MarkRemoved{removed.markId});
    }

    // The self entry keeps the real profile; the status carries what the others see.
    Result<void> Receive(Wire::IdentityVisibilityChanged& changed)
    {
      if (!TakePending(changed.requestId, PendingKind::Identity)) return Unexpected("request_id");
      exchange.PublishIdentity(std::move(changed.pseudonym), changed.hiding);
      return Settle(changed.requestId, IdentityChanged{changed.hiding});
    }

    // The own profile changes through the presence update that follows; this only settles the request.
    Result<void> Receive(Wire::DisplayNameChanged& changed)
    {
      if (!TakePending(changed.requestId, PendingKind::Name)) return Unexpected("request_id");
      return Settle(changed.requestId, NameChanged{std::move(changed.displayName)});
    }

    Result<void> Receive(GroundMarksChanged& value)
    {
      return Apply(value);
    }

    // The server lists only the receiver's marks; any other author is a protocol fault.
    Result<void> Receive(OwnGroundMarksReplaced& value)
    {
      for (const auto& mark : value.marks)
        if (mark.author.playerId != model.SelfPlayerId()) return Unexpected("author");
      return Apply(value);
    }

    // The kinds first, then the updates in wire order, published together.
    Result<void> Receive(Wire::PresenceChanged& changed)
    {
      if (phase != SessionPhase::Ready) return Unexpected("session_not_ready");

      // The decoder already checked these against the same table.
      for (auto& kind : changed.kinds)
        kinds.Define(std::move(kind));
      for (const auto& update : changed.updates)
      {
        auto applied = model.Apply(model.Generation(), update);
        if (!applied) return std::unexpected{applied.error()};
      }
      ForgetUnusedKinds();
      return Publish();
    }

    // Kinds no online player has are never sent again; the table drops them
    // once it has doubled since the last sweep.
    void ForgetUnusedKinds()
    {
      if (kinds.Size() < kindSweep) return;
      kinds.Retain(model.SnapshotPlayers());
      kindSweep = std::max(KindSweepFloor, 2 * kinds.Size());
    }

    Result<void> Receive(Wire::PlayersMoved& batch)
    {
      if (phase != SessionPhase::Ready) return {};  // Realtime may overtake reliable bootstrap.

      for (const auto& value : batch.players)
      {
        auto result = model.Apply(model.Generation(), value);
        if (!result) return std::unexpected{result.error()};
      }
      return Publish();
    }

    Result<void> Receive(ChatMessagesReceived& value)
    {
      return ReceiveChat(std::move(value));
    }

    // The chat lane may overtake the welcome on the control lane: its changes
    // wait for the channels to be registered.
    Result<void> ReceiveChat(ClientUpdate change)
    {
      if (phase == SessionPhase::Opening)
      {
        if (earlyChat.size() >= config.chatCapacity) return Unexpected("bootstrap_chat_capacity");
        earlyChat.push_back(std::move(change));
        return {};
      }
      return Apply(change);
    }

    template <class T>
    Result<void> Apply(const T& value)
    {
      if (phase != SessionPhase::Ready) return Unexpected("session_not_ready");

      auto result = model.Apply(model.Generation(), value);
      if (!result) return std::unexpected{result.error()};

      return Publish();
    }

    Result<void> RejectCommand(std::uint64_t generation, std::uint64_t requestId, CommandFailureCode code)
    {
      if (!exchange.PublishResult({generation, requestId, code}))
        return std::unexpected{DreamNetError::Make(DreamNetErrorCode::InvalidOperation, "Command result capacity exhausted")};

      return {};
    }

    // A reply settles only a request of its own kind, and only in a ready session.
    std::optional<PendingRequest> TakePending(std::uint64_t requestId, PendingKind kind)
    {
      const auto found = pending.find(requestId);
      if (phase != SessionPhase::Ready || found == pending.end() || found->second.kind != kind) return std::nullopt;
      const auto request = found->second;
      pending.erase(found);
      return request;
    }

    std::size_t PendingCount(PendingKind kind) const
    {
      return static_cast<std::size_t>(std::ranges::count(pending | std::views::values, kind, &PendingRequest::kind));
    }

    // Every command with a caller-supplied request ID: the current generation,
    // a ready session and an ID that was never used and is not pending.
    std::optional<CommandFailureCode> Admit(std::uint64_t generation, std::uint64_t requestId)
    {
      if (generation != model.Generation()) return CommandFailureCode::StaleGeneration;
      if (phase != SessionPhase::Ready) return CommandFailureCode::SessionNotReady;
      if (requestId <= lastRequest || pending.contains(requestId)) return CommandFailureCode::InvalidRequest;
      lastRequest = requestId;
      return std::nullopt;
    }

    // Sends an admitted command; it stays pending until its reply. The codec
    // checks the command's shape: a malformed one is an invalid request, one
    // that cannot become a packet (too large) an encoding failure.
    template <class Command>
    Result<void> SendRequest(
      std::uint64_t  generation,
      const Command& command,
      PendingRequest request,
      Wire::Channel  channel = Wire::Channel::Control)
    {
      auto packet = codec.Encode(command);
      if (!packet)
      {
        const auto code      = packet.error().code;
        const bool malformed = code == Wire::ErrorCode::InvalidPayload || code == Wire::ErrorCode::InvalidEnvelope;
        return RejectCommand(
          generation,
          command.requestId,
          malformed ? CommandFailureCode::InvalidRequest : CommandFailureCode::EncodingFailed);
      }

      auto sent = transport->Send(std::move(*packet), static_cast<ChannelId>(channel));
      if (!sent) return Fail(sent.error());

      pending.emplace(command.requestId, request);
      return {};
    }

    // Chat and announcements share the Chat lane and the pending budget. The
    // channel must exist and be of a kind that accepts the command; allowed
    // covers what the session announced for it.
    template <class Command>
    Result<void> SendToChannel(std::uint64_t generation, Command& command, std::span<const Domain::ChatChannelKind> accepting, bool allowed)
    {
      if (auto failure = Admit(generation, command.requestId)) return RejectCommand(generation, command.requestId, *failure);

      const auto channel = model.FindChatState(command.channelId);
      if (!channel || !std::ranges::contains(accepting, channel->kind) || !allowed)
        return RejectCommand(generation, command.requestId, CommandFailureCode::InvalidRequest);
      if (PendingCount(PendingKind::Chat) >= config.maxPendingChatRequests)
        return RejectCommand(generation, command.requestId, CommandFailureCode::Busy);

      return SendRequest(generation, command, {PendingKind::Chat, command.channelId}, Wire::Channel::Chat);
    }

    // Players write in the global channel and in their guilds' channels; the
    // server judges a guild mute like any other.
    Result<void> Process(std::uint64_t generation, SendChat& command)
    {
      static constexpr std::array writable{Domain::ChatChannelKind::Global, Domain::ChatChannelKind::Guild};
      return SendToChannel(generation, command, writable, true);
    }

    // The server judges rate, words and text rules; locally only what its
    // welcome announced (sources and lengths), so no doomed packet is sent.
    Result<void> Process(std::uint64_t generation, PostAnnouncement& command)
    {
      static constexpr std::array announced{Domain::ChatChannelKind::System};
      const bool allowed = Domain::Announcements::Admits(announcementPolicy, command.source, command.text, command.signature);
      return SendToChannel(generation, command, announced, allowed);
    }

    Result<void> Process(std::uint64_t, RequestSnapshot&)
    {
      return Publish(true);
    }

    // Marks travel on the control lane with their own pending set; the server
    // judges position, words, quotas and frequency.
    template <class Command>
    Result<void> SendMarkCommand(std::uint64_t generation, Command& command)
    {
      if (auto failure = Admit(generation, command.requestId)) return RejectCommand(generation, command.requestId, *failure);

      if (PendingCount(PendingKind::Mark) >= config.maxPendingChatRequests)
        return RejectCommand(generation, command.requestId, CommandFailureCode::Busy);

      return SendRequest(generation, command, {PendingKind::Mark});
    }

    Result<void> Process(std::uint64_t generation, PlaceGroundNote& command)
    {
      return SendMarkCommand(generation, command);
    }

    Result<void> Process(std::uint64_t generation, ReportDeath& command)
    {
      return SendMarkCommand(generation, command);
    }

    Result<void> Process(std::uint64_t generation, RemoveGroundMark& command)
    {
      return SendMarkCommand(generation, command);
    }

    // One switch at a time; the server judges permission and frequency.
    Result<void> Process(std::uint64_t generation, SetIdentityVisibility& command)
    {
      if (auto failure = Admit(generation, command.requestId)) return RejectCommand(generation, command.requestId, *failure);
      if (PendingCount(PendingKind::Identity) != 0) return RejectCommand(generation, command.requestId, CommandFailureCode::Busy);

      return SendRequest(generation, command, {PendingKind::Identity});
    }

    // One change at a time; the server judges the word list and how often.
    Result<void> Process(std::uint64_t generation, ChangeDisplayName& command)
    {
      if (auto failure = Admit(generation, command.requestId)) return RejectCommand(generation, command.requestId, *failure);
      if (PendingCount(PendingKind::Name) != 0) return RejectCommand(generation, command.requestId, CommandFailureCode::Busy);

      return SendRequest(generation, command, {PendingKind::Name});
    }

    // Moderator requests share one pending budget on the control lane; the
    // server judges the role, the target and the values.
    template <class Command>
    Result<void> SendModeration(std::uint64_t generation, Command& command)
    {
      if (auto failure = Admit(generation, command.requestId)) return RejectCommand(generation, command.requestId, *failure);
      if (PendingCount(PendingKind::Moderation) >= config.maxPendingChatRequests)
        return RejectCommand(generation, command.requestId, CommandFailureCode::Busy);

      return SendRequest(generation, command, {PendingKind::Moderation});
    }

    Result<void> Process(std::uint64_t generation, SanctionPlayer& command)
    {
      return SendModeration(generation, command);
    }

    Result<void> Process(std::uint64_t generation, LiftSanction& command)
    {
      return SendModeration(generation, command);
    }

    Result<void> Process(std::uint64_t generation, KickPlayer& command)
    {
      return SendModeration(generation, command);
    }

    Result<void> Process(std::uint64_t generation, ListSanctions& command)
    {
      return SendModeration(generation, command);
    }

    Result<void> Process(std::uint64_t generation, ListPlayerMarks& command)
    {
      return SendModeration(generation, command);
    }

    Result<void> Process(std::uint64_t generation, ClearPlayerMarks& command)
    {
      return SendModeration(generation, command);
    }

    // Guild requests share one pending budget on the control lane; the server
    // judges the roles, the name and the limits.
    Result<void> Process(std::uint64_t generation, GuildRequest& command)
    {
      if (auto failure = Admit(generation, command.requestId)) return RejectCommand(generation, command.requestId, *failure);
      if (PendingCount(PendingKind::Guild) >= config.maxPendingChatRequests)
        return RejectCommand(generation, command.requestId, CommandFailureCode::Busy);

      return SendRequest(generation, command, {PendingKind::Guild});
    }

    // A deletion settles with the chat of its channel, which must exist.
    Result<void> Process(std::uint64_t generation, DeleteChatMessage& command)
    {
      if (auto failure = Admit(generation, command.requestId)) return RejectCommand(generation, command.requestId, *failure);
      if (!model.FindChatState(command.channelId)) return RejectCommand(generation, command.requestId, CommandFailureCode::InvalidRequest);
      if (PendingCount(PendingKind::Deletion) >= config.maxPendingChatRequests)
        return RejectCommand(generation, command.requestId, CommandFailureCode::Busy);

      return SendRequest(generation, command, {PendingKind::Deletion, command.channelId}, Wire::Channel::Chat);
    }

    template <class T>
    Result<void> SendPlayerUpdate(std::uint64_t generation, T& command, bool locationTransition = false)
    {
      const auto requestId = exchange.NextRequestId();
      if (!requestId) return std::unexpected{DreamNetError::Make(DreamNetErrorCode::InvalidOperation, "Request IDs exhausted")};
      if (generation != model.Generation()) return RejectCommand(generation, *requestId, CommandFailureCode::StaleGeneration);
      if (phase != SessionPhase::Ready) return RejectCommand(generation, *requestId, CommandFailureCode::SessionNotReady);
      if (PendingCount(PendingKind::Update) >= config.maxPendingPlayerUpdates)
        return RejectCommand(generation, *requestId, CommandFailureCode::Busy);

      auto sent = SendRequest(generation, Wire::UpdatePlayer{*requestId, std::move(command)}, {PendingKind::Update});
      if (sent && pending.contains(*requestId) && locationTransition) pendingLocation = *requestId;
      return sent;
    }

    void ResetMovement()
    {
      latestMovement.reset();
      pendingLocation  = Domain::InvalidId;
      movementReady    = false;
      movementSequence = 0;
      nextPlayerSample = {};
    }

    Result<void> Process(std::uint64_t generation, LocalLocation& command)
    {
      if (phase != SessionPhase::Ready || generation != model.Generation())
      {
        Wire::SetLocation rejected{0, command.location};
        return SendPlayerUpdate(generation, rejected);
      }
      if (contextRevision == std::numeric_limits<std::uint64_t>::max()) return Unexpected("movement_context_exhausted");
      ResetMovement();
      latestMovement = command.location;
      Wire::SetLocation transition{++contextRevision, command.location};
      auto              result = SendPlayerUpdate(generation, transition, true);
      if (!result || pendingLocation == Domain::InvalidId) ResetMovement();
      return result;
    }

    Result<void> Process(std::uint64_t generation, LocalMovement& command)
    {
      if (phase != SessionPhase::Ready || generation != model.Generation()) return {};
      if (!command.location || !latestMovement || !Domain::Spatial::SameSpace(*command.location, *latestMovement))
      {
        LocalLocation transition{command.location};
        return Process(generation, transition);
      }
      latestMovement = std::move(command.location);
      return {};
    }

    // The latest pose goes every interval on the unreliable lane, a repeat
    // included: it covers a lost sample. The stamp is the capture time the
    // exchange gave the pose, so a repeat says when the pose was true and
    // receivers drop it as the sample they already have.
    Result<void> SendMovement()
    {
      if (phase != SessionPhase::Ready || !movementReady || !latestMovement || Clock::now() < nextPlayerSample) return {};
      if (movementSequence == std::numeric_limits<std::uint64_t>::max()) return Unexpected("movement_sequence_exhausted");
      nextPlayerSample = Clock::now() + std::chrono::milliseconds(config.playerSampleIntervalMs);
      auto packet      = codec.Encode(
        Wire::MovementSample{
            contextRevision,
            ++movementSequence,
            {latestMovement->position, latestMovement->rotation, latestMovement->sampledAtUs}
      },
        transport->MaxUnfragmentedPayloadBytes());
      if (!packet) return Fail(packet.error());
      auto sent = transport->Send(std::move(*packet), static_cast<ChannelId>(Wire::Channel::Realtime));
      if (!sent) return Fail(sent.error());
      return {};
    }

    Result<void> Process(std::uint64_t generation, LocalActorValues& command)
    {
      return SendPlayerUpdate(generation, command);
    }

    Result<void> Process(std::uint64_t generation, CharacterStarted& command)
    {
      if (generation == model.Generation()) ResetMovement();
      return SendPlayerUpdate(generation, command);
    }

    Result<void> Process(std::uint64_t generation, CharacterRenamed& command)
    {
      return SendPlayerUpdate(generation, command);
    }

    Result<void> Process(std::uint64_t generation, PlayerDetailsChanged& command)
    {
      return SendPlayerUpdate(generation, command);
    }

    Result<void> Process(std::uint64_t generation, GameExited& command)
    {
      if (generation == model.Generation()) ResetMovement();
      return SendPlayerUpdate(generation, command);
    }

    Result<void> ProcessCommands()
    {
      const auto openingReply = phase == SessionPhase::Connecting || phase == SessionPhase::Opening ? 1u : 0u;
      exchange.TakeCommands(commands, pending.size() + openingReply);
      Result<void> firstError;

      for (auto& queued : commands)
      {
        auto result = std::visit([&](auto& command) { return Process(queued.generation, command); }, queued.command);
        if (!result && firstError) firstError = std::unexpected{std::move(result.error())};
      }

      commands.clear();
      return firstError;
    }

    // Control, chat and realtime lanes.
    static constexpr std::size_t MinimumChannels = 3;
    // The kind table is swept when it reaches this size, then twice the kinds left.
    static constexpr std::size_t KindSweepFloor = 64;

    Configuration                                     config;
    Wire::ProtocolCodec                               codec;
    ClientExchange&                                   exchange;
    DreamNetClient::Ptr                               transport;
    bool                                              keepGuest{};
    Utils::Timing::Backoff                            guestRetry{GuestRetryMinimum, GuestRetryMaximum};
    ClientModel                                       model;
    SessionPhase                                      phase{SessionPhase::Disconnected};
    Wire::OpenSession                                 opening;
    std::string                                       serverName;
    Domain::AnnouncementPolicy                        announcementPolicy;
    std::uint64_t                                     lastRequest{};
    std::unordered_map<std::uint64_t, PendingRequest> pending;
    std::vector<QueuedClientCommand>                  commands;
    Clock::time_point                                 deadline{};
    Clock::time_point                                 nextPlayerSample{};
    std::optional<Domain::PlayerLocation>             latestMovement;
    std::uint64_t                                     contextRevision{};
    std::uint64_t                                     movementSequence{};
    std::uint64_t                                     pendingLocation{Domain::InvalidId};  // The location update in flight.
    bool                                              movementReady{};
    std::vector<ClientUpdate>                         earlyChat;
    std::shared_ptr<const GuildBook>                  guilds;  // Absent until the session's GuildsSnapshot.
    std::vector<ClientEvent>                          events;
    Wire::ActorValueKinds                             kinds;
    std::size_t                                       kindSweep{KindSweepFloor};
  };

}
