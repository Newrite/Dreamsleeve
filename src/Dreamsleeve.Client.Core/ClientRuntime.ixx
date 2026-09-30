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

    static Result<Ptr> TryCreate(Configuration config, ClientExchange& exchange)
    {
      if (auto field = config.InvalidSetting())
        return std::unexpected{DreamNetError::Make(DreamNetErrorCode::InvalidConfig, std::string{*field})};

      auto codec = Wire::ProtocolCodec::TryCreate(config);
      if (!codec) return std::unexpected{codec.error()};

      return Ptr{new ClientRuntime(std::move(config), std::move(*codec), exchange)};
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

    Result<void> Connect(std::string sessionTicket)
    {
      if (!SessionIdle(phase))
        return std::unexpected{DreamNetError::Make(DreamNetErrorCode::InvalidOperation, "A session is already active")};

      if (!exchange.CanAcceptReplies())
        return std::unexpected{DreamNetError::Make(DreamNetErrorCode::InvalidOperation, "Drain command results before opening a session")};

      const auto requestId = exchange.NextRequestId();
      if (!requestId) return std::unexpected{DreamNetError::Make(DreamNetErrorCode::InvalidOperation, "Request IDs exhausted")};

      DreamNetClientConfig transportConfig{config.network, config.serverAddress, config.connectTimeoutMs, config.disconnectTimeoutMs};
      auto                 created = DreamNetClient::TryCreate(transportConfig);
      if (!created) return Fail(created.error());

      transport   = std::move(*created);
      opening     = Wire::OpenSession{*requestId, std::move(sessionTicket), exchange.HideIdentity()};
      lastRequest = *requestId;
      ResetSession();
      auto published = Publish();
      if (!published) return Fail(published.error());

      auto connected = transport->BeginConnect();
      if (!connected) return Fail(connected.error());

      SetPhase(SessionPhase::Connecting);

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

    // What a sent request waits for. Chat and announcements remember their channel.
    enum class PendingKind
    {
      Chat,
      Update,
      Mark,
      Identity,
      Name
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
      model.ResetSession();
      exchange.PublishIdentity(std::nullopt, Domain::HiddenIdentity::None);
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
      if (!exchange.Publish(model, requestSnapshot, phase, serverName, std::move(result)))
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

      if (transport->NegotiatedChannelCount() < 3) return Unexpected("channel_count");

      auto packet = codec.Encode(opening);
      opening.sessionTicket.clear();  // A reconnect must supply a newly issued ticket.
      if (!packet) return std::unexpected{packet.error()};

      auto sent = transport->Send(std::move(*packet));
      if (!sent) return std::unexpected{sent.error()};

      deadline = Clock::now() + std::chrono::milliseconds(config.sessionTimeoutMs);
      SetPhase(SessionPhase::Opening);

      return {};
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

      auto response = codec.Decode(received.packet.DataBytesView(), channel);
      if (!response) return std::unexpected{response.error()};

      if (auto* rejected = std::get_if<Wire::RequestRejected>(&*response))
      {
        const auto found    = pending.find(rejected->requestId);
        const bool chat     = found != pending.end() && found->second.kind == PendingKind::Chat;
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

      serverName         = std::move(opened.serverName);
      announcementPolicy = std::move(opened.announcements);
      exchange.PublishIdentity(std::move(opened.ownPseudonym), opened.hiding);
      phase = SessionPhase::Ready;
      for (const auto& message : earlyChat)
      {
        auto applied = model.Apply(generation, message);
        if (!applied) return std::unexpected{applied.error()};
      }
      earlyChat.clear();
      return Publish(true);
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
        pendingLocation  = 0;
        movementReady    = latestMovement.has_value();
        nextPlayerSample = {};
      }
      return {};
    }

    Result<void> Receive(PlayerMetadataUpdated& value)
    {
      return Apply(value);
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

    // The own profile changes through the PlayerUpdated that follows; this only settles the request.
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

    Result<void> Receive(PlayerLocationUpdated& value)
    {
      return Apply(value);
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
      if (phase == SessionPhase::Opening)
      {
        if (earlyChat.size() >= config.chatCapacity) return Unexpected("bootstrap_chat_capacity");
        earlyChat.push_back(std::move(value));
        return {};
      }
      return Apply(value);
    }

    Result<void> Receive(PlayerUpserted& value)
    {
      return Apply(value);
    }

    Result<void> Receive(PlayerRemoved& value)
    {
      return Apply(value);
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

    // Sends an admitted command; it stays pending until its reply.
    template <class Command>
    Result<void> SendRequest(std::uint64_t generation, const Command& command, PendingRequest request, Wire::Channel channel = Wire::Channel::Control)
    {
      auto packet = codec.Encode(command);
      if (!packet) return RejectCommand(generation, command.requestId, CommandFailureCode::EncodingFailed);

      auto sent = transport->Send(std::move(*packet), static_cast<ChannelId>(channel));
      if (!sent) return Fail(sent.error());

      pending.emplace(command.requestId, request);
      return {};
    }

    // Chat and announcements share the Chat lane and the pending budget. The
    // channel must exist and be of the kind that accepts the command; valid
    // covers the command's own local limits.
    template <class Command>
    Result<void> SendToChannel(std::uint64_t generation, Command& command, Domain::ChatChannelKind kind, bool valid)
    {
      if (auto failure = Admit(generation, command.requestId)) return RejectCommand(generation, command.requestId, *failure);

      const auto channel = model.FindChatState(command.channelId);
      if (!channel || channel->kind != kind || !valid)
        return RejectCommand(generation, command.requestId, CommandFailureCode::InvalidRequest);
      if (PendingCount(PendingKind::Chat) >= config.maxPendingChatRequests)
        return RejectCommand(generation, command.requestId, CommandFailureCode::Busy);

      return SendRequest(generation, command, {PendingKind::Chat, command.channelId}, Wire::Channel::Chat);
    }

    Result<void> Process(std::uint64_t generation, SendChat& command)
    {
      return SendToChannel(generation, command, Domain::ChatChannelKind::Global, true);
    }

    // The server judges rate, words and text rules; locally only what its
    // welcome announced (sources and lengths), so no doomed packet is sent.
    Result<void> Process(std::uint64_t generation, PostAnnouncement& command)
    {
      using Utils::Text::CodePoints;
      const bool labelRequired = command.source == Domain::ClientAnnouncementSource::ThirdParty;
      const bool valid = announcementPolicy.Allows(command.source) && !command.text.empty() &&
                         CodePoints(command.text) <= announcementPolicy.maxTextLength && (!labelRequired || !command.signature.empty()) &&
                         CodePoints(command.signature) <= announcementPolicy.maxSignatureLength;
      return SendToChannel(generation, command, Domain::ChatChannelKind::System, valid);
    }

    Result<void> Process(std::uint64_t, RequestSnapshot&)
    {
      return Publish(true);
    }

    // Marks travel on the control lane with their own pending set; the server
    // judges position, words, quotas and frequency. Locally only the shape.
    template <class Command>
    Result<void> SendMarkCommand(std::uint64_t generation, Command& command, bool valid)
    {
      if (auto failure = Admit(generation, command.requestId)) return RejectCommand(generation, command.requestId, *failure);

      if (!valid) return RejectCommand(generation, command.requestId, CommandFailureCode::InvalidRequest);
      if (PendingCount(PendingKind::Mark) >= config.maxPendingChatRequests)
        return RejectCommand(generation, command.requestId, CommandFailureCode::Busy);

      return SendRequest(generation, command, {PendingKind::Mark});
    }

    Result<void> Process(std::uint64_t generation, PlaceGroundNote& command)
    {
      return SendMarkCommand(generation, command, !command.text.empty() && Utils::Text::ValidUtf8(command.text));
    }

    Result<void> Process(std::uint64_t generation, ReportDeath& command)
    {
      return SendMarkCommand(generation, command, Utils::Text::ValidUtf8(command.label) && !Utils::Text::HasControl(command.label));
    }

    Result<void> Process(std::uint64_t generation, RemoveGroundMark& command)
    {
      return SendMarkCommand(generation, command, command.markId != 0);
    }

    // One switch at a time; the server judges permission and frequency.
    Result<void> Process(std::uint64_t generation, SetIdentityVisibility& command)
    {
      if (auto failure = Admit(generation, command.requestId)) return RejectCommand(generation, command.requestId, *failure);
      if (PendingCount(PendingKind::Identity) != 0) return RejectCommand(generation, command.requestId, CommandFailureCode::Busy);

      return SendRequest(generation, command, {PendingKind::Identity});
    }

    // One change at a time; the server judges the word list and how often.
    // Locally only the shape: one line of valid UTF-8 that is not blank.
    Result<void> Process(std::uint64_t generation, ChangeDisplayName& command)
    {
      if (auto failure = Admit(generation, command.requestId)) return RejectCommand(generation, command.requestId, *failure);

      const bool blank = std::ranges::all_of(command.displayName, [](char value) { return value == ' ' || value == '\t'; });
      if (blank || !Utils::Text::ValidUtf8(command.displayName) || Utils::Text::HasControl(command.displayName))
        return RejectCommand(generation, command.requestId, CommandFailureCode::InvalidRequest);
      if (PendingCount(PendingKind::Name) != 0) return RejectCommand(generation, command.requestId, CommandFailureCode::Busy);

      return SendRequest(generation, command, {PendingKind::Name});
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
      pendingLocation  = 0;
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
      if (!result || pendingLocation == 0) ResetMovement();
      return result;
    }

    Result<void> Process(std::uint64_t generation, LocalMovement& command)
    {
      if (phase != SessionPhase::Ready || generation != model.Generation()) return {};
      if (!command.location || !latestMovement || command.location->location.locationId != latestMovement->location.locationId)
      {
        LocalLocation transition{command.location};
        return Process(generation, transition);
      }
      latestMovement = std::move(command.location);
      return {};
    }

    Result<void> SendMovement()
    {
      if (phase != SessionPhase::Ready || !movementReady || !latestMovement || Clock::now() < nextPlayerSample) return {};
      if (movementSequence == std::numeric_limits<std::uint64_t>::max()) return Unexpected("movement_sequence_exhausted");
      const auto now   = Clock::now();
      nextPlayerSample = now + std::chrono::milliseconds(config.playerSampleIntervalMs);
      const auto sampledAt =
        static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::microseconds>(now.time_since_epoch()).count());
      auto packet = codec.Encode(
        Wire::MovementSample{
            contextRevision,
            ++movementSequence,
            {latestMovement->position, latestMovement->rotation, sampledAt}
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

    Configuration                                            config;
    Wire::ProtocolCodec                                      codec;
    ClientExchange&                                          exchange;
    DreamNetClient::Ptr                                      transport;
    ClientModel                                              model;
    SessionPhase                                             phase{SessionPhase::Disconnected};
    Wire::OpenSession                                        opening;
    std::string                                              serverName;
    Domain::AnnouncementPolicy                               announcementPolicy;
    std::uint64_t                                            lastRequest{};
    std::unordered_map<std::uint64_t, PendingRequest>        pending;
    std::vector<QueuedClientCommand>                         commands;
    Clock::time_point                                        deadline{};
    Clock::time_point                                        nextPlayerSample{};
    std::optional<Domain::PlayerLocation>                    latestMovement;
    std::uint64_t                                            contextRevision{};
    std::uint64_t                                            movementSequence{};
    std::uint64_t                                            pendingLocation{};
    bool                                                     movementReady{};
    std::vector<ChatMessagesReceived>                        earlyChat;
    std::vector<ClientEvent>                                 events;
  };

}
