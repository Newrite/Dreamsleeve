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
      if (phase != SessionPhase::Disconnected && phase != SessionPhase::Faulted)
        return std::unexpected{DreamNetError::Make(DreamNetErrorCode::InvalidOperation, "A session is already active")};

      if (model.PendingServerRejectionCount() != 0)
      {
        auto published = Publish();
        if (!published) return published;
      }

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
      serverName.clear();
      pendingChats.clear();
      pendingUpdates.clear();
      pendingMarks.clear();
      pendingIdentity.clear();
      pendingNames.clear();
      ResetMovement();
      earlyChat.clear();
      model.ResetSession();
      exchange.PublishIdentity(std::nullopt, Domain::HiddenIdentity::None);
      auto published = Publish();
      if (!published) return Fail(published.error());

      auto connected = transport->BeginConnect();
      if (!connected) return Fail(connected.error());

      SetPhase(SessionPhase::Connecting);

      return {};
    }

    Result<void> Disconnect()
    {
      if (!transport || phase == SessionPhase::Disconnected || phase == SessionPhase::Faulted) return {};
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
      if (!transport || phase == SessionPhase::Disconnected || phase == SessionPhase::Faulted) return commandsResult;

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

    Result<void> Clear(SessionPhase value)
    {
      serverName.clear();
      pendingChats.clear();
      pendingUpdates.clear();
      pendingMarks.clear();
      pendingIdentity.clear();
      pendingNames.clear();
      ResetMovement();
      earlyChat.clear();
      model.ResetSession();
      exchange.PublishIdentity(std::nullopt, Domain::HiddenIdentity::None);
      phase          = value;
      auto published = Publish(true);
      if (!published) SetPhase(SessionPhase::Faulted);
      return published;
    }

    Result<void> Publish(
      bool                                   requestSnapshot  = false,
      std::optional<ChatConfirmation>        confirmation     = std::nullopt,
      std::optional<GroundMarkConfirmation>  markConfirmation = std::nullopt,
      std::optional<IdentityConfirmation>    identity         = std::nullopt,
      std::optional<DisplayNameConfirmation> displayName      = std::nullopt)
    {
      if (!exchange.Publish(
            model,
            requestSnapshot,
            phase,
            serverName,
            confirmation,
            markConfirmation,
            std::move(identity),
            std::move(displayName)))
        return std::unexpected{DreamNetError::Make(DreamNetErrorCode::InvalidOperation, "Command result capacity exhausted")};

      return {};
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
      if (phase == SessionPhase::Disconnecting || phase == SessionPhase::Disconnected || phase == SessionPhase::Faulted)
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

      if (auto* rejection = std::get_if<ServerRejection>(&*response))
      {
        const auto expected = pendingChats.contains(rejection->requestId) ? Wire::Channel::Chat : Wire::Channel::Control;
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

    Result<void> Receive(ServerRejection& rejection)
    {
      if (phase == SessionPhase::Opening)
      {
        if (rejection.requestId != opening.requestId) return Unexpected("request_id");

        auto applied = model.Apply(model.Generation(), rejection);
        if (!applied) return std::unexpected{applied.error()};

        // Opening was refused: the full terminal reply is already received.
        // Notify the peer best-effort, without racing its own graceful close.
        transport->Abort(DisconnectReason::ClientShutdown);
        return Clear(SessionPhase::Disconnected);
      }

      if (phase != SessionPhase::Ready) return Unexpected("request_id");
      if (
        pendingChats.erase(rejection.requestId) == 0 && pendingUpdates.erase(rejection.requestId) == 0 &&
        pendingMarks.erase(rejection.requestId) == 0 && pendingIdentity.erase(rejection.requestId) == 0 &&
        pendingNames.erase(rejection.requestId) == 0)
        return Unexpected("request_id");

      if (rejection.requestId == pendingLocation) ResetMovement();
      return Apply(rejection);
    }

    Result<void> Receive(Wire::ChatAccepted& accepted)
    {
      const auto found = pendingChats.find(accepted.requestId);
      if (phase != SessionPhase::Ready || found == pendingChats.end()) return Unexpected("request_id");
      if (accepted.changes.channelId != found->second) return Unexpected("channel_id");
      const auto& author = accepted.changes.messages.front().author;
      if (!author || author->playerId != model.SelfPlayerId()) return Unexpected("author");

      // The server's publication is the only source of accepted chat content.
      // Correlation settles the command; the model path is shared with broadcasts.
      const ChatConfirmation confirmation{model.Generation(), accepted.requestId, accepted.changes.messages.front().messageId};
      auto                   applied = model.Apply(model.Generation(), accepted.changes);
      if (!applied) return std::unexpected{applied.error()};

      pendingChats.erase(found);
      return Publish(false, confirmation);
    }

    Result<void> Receive(Wire::PlayerUpdateAccepted& accepted)
    {
      if (phase != SessionPhase::Ready || pendingUpdates.erase(accepted.requestId) == 0) return Unexpected("request_id");
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
      if (phase != SessionPhase::Ready || pendingMarks.erase(placed.requestId) == 0) return Unexpected("request_id");
      if (placed.mark.author.playerId != model.SelfPlayerId()) return Unexpected("author");
      return Publish(
        false,
        std::nullopt,
        GroundMarkConfirmation{model.Generation(), placed.requestId, placed.mark.markId, placed.evictedId, false});
    }

    Result<void> Receive(Wire::GroundMarkRemoved& removed)
    {
      if (phase != SessionPhase::Ready || pendingMarks.erase(removed.requestId) == 0) return Unexpected("request_id");
      return Publish(
        false,
        std::nullopt,
        GroundMarkConfirmation{model.Generation(), removed.requestId, removed.markId, std::nullopt, true});
    }

    // The self entry keeps the real profile; the status carries what the others see.
    Result<void> Receive(Wire::IdentityVisibilityChanged& changed)
    {
      if (phase != SessionPhase::Ready || pendingIdentity.erase(changed.requestId) == 0) return Unexpected("request_id");
      exchange.PublishIdentity(changed.pseudonym, changed.hiding);
      return Publish(
        false,
        std::nullopt,
        std::nullopt,
        IdentityConfirmation{model.Generation(), changed.requestId, std::move(changed.pseudonym), changed.hiding});
    }

    // The own profile changes through the PlayerUpdated that follows; this only settles the request.
    Result<void> Receive(Wire::DisplayNameChanged& changed)
    {
      if (phase != SessionPhase::Ready || pendingNames.erase(changed.requestId) == 0) return Unexpected("request_id");
      return Publish(
        false,
        std::nullopt,
        std::nullopt,
        std::nullopt,
        DisplayNameConfirmation{model.Generation(), changed.requestId, std::move(changed.displayName)});
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
      if (!exchange.PublishCommandFailure({generation, requestId, code}))
        return std::unexpected{DreamNetError::Make(DreamNetErrorCode::InvalidOperation, "Command result capacity exhausted")};

      return {};
    }

    // Chat and announcements share the Chat lane and the pending budget. The
    // channel must exist and be of the kind that accepts the command; valid
    // covers the command's own local limits.
    template <class Command>
    Result<void> SendToChannel(std::uint64_t generation, Command& command, Domain::ChatChannelKind kind, bool valid)
    {
      if (generation != model.Generation()) return RejectCommand(generation, command.requestId, CommandFailureCode::StaleGeneration);
      if (phase != SessionPhase::Ready) return RejectCommand(generation, command.requestId, CommandFailureCode::SessionNotReady);
      if (command.requestId <= lastRequest || pendingUpdates.contains(command.requestId))
        return RejectCommand(generation, command.requestId, CommandFailureCode::InvalidRequest);

      lastRequest        = command.requestId;
      const auto channel = model.FindChatState(command.channelId);
      if (!channel || channel->kind != kind || !valid)
        return RejectCommand(generation, command.requestId, CommandFailureCode::InvalidRequest);
      if (pendingChats.size() >= config.maxPendingChatRequests)
        return RejectCommand(generation, command.requestId, CommandFailureCode::Busy);

      auto packet = codec.Encode(command);
      if (!packet) return RejectCommand(generation, command.requestId, CommandFailureCode::EncodingFailed);

      auto sent = transport->Send(std::move(*packet), static_cast<ChannelId>(Wire::Channel::Chat));
      if (!sent) return Fail(sent.error());

      pendingChats.emplace(command.requestId, command.channelId);
      return {};
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
      if (generation != model.Generation()) return RejectCommand(generation, command.requestId, CommandFailureCode::StaleGeneration);
      if (phase != SessionPhase::Ready) return RejectCommand(generation, command.requestId, CommandFailureCode::SessionNotReady);
      if (
        command.requestId <= lastRequest || pendingUpdates.contains(command.requestId) || pendingChats.contains(command.requestId) ||
        pendingIdentity.contains(command.requestId) || pendingNames.contains(command.requestId))
        return RejectCommand(generation, command.requestId, CommandFailureCode::InvalidRequest);

      lastRequest = command.requestId;
      if (!valid) return RejectCommand(generation, command.requestId, CommandFailureCode::InvalidRequest);
      if (pendingMarks.size() >= config.maxPendingChatRequests)
        return RejectCommand(generation, command.requestId, CommandFailureCode::Busy);

      auto packet = codec.Encode(command);
      if (!packet) return RejectCommand(generation, command.requestId, CommandFailureCode::EncodingFailed);

      auto sent = transport->Send(std::move(*packet));
      if (!sent) return Fail(sent.error());

      pendingMarks.insert(command.requestId);
      return {};
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
      if (generation != model.Generation()) return RejectCommand(generation, command.requestId, CommandFailureCode::StaleGeneration);
      if (phase != SessionPhase::Ready) return RejectCommand(generation, command.requestId, CommandFailureCode::SessionNotReady);
      if (
        command.requestId <= lastRequest || pendingUpdates.contains(command.requestId) || pendingChats.contains(command.requestId) ||
        pendingMarks.contains(command.requestId))
        return RejectCommand(generation, command.requestId, CommandFailureCode::InvalidRequest);
      lastRequest = command.requestId;
      if (!pendingIdentity.empty()) return RejectCommand(generation, command.requestId, CommandFailureCode::Busy);

      auto packet = codec.Encode(command);
      if (!packet) return RejectCommand(generation, command.requestId, CommandFailureCode::EncodingFailed);
      auto sent = transport->Send(std::move(*packet));
      if (!sent) return Fail(sent.error());
      pendingIdentity.insert(command.requestId);
      return {};
    }

    // One change at a time; the server judges the word list and how often.
    // Locally only the shape: one line of valid UTF-8 that is not blank.
    Result<void> Process(std::uint64_t generation, ChangeDisplayName& command)
    {
      if (generation != model.Generation()) return RejectCommand(generation, command.requestId, CommandFailureCode::StaleGeneration);
      if (phase != SessionPhase::Ready) return RejectCommand(generation, command.requestId, CommandFailureCode::SessionNotReady);
      if (
        command.requestId <= lastRequest || pendingUpdates.contains(command.requestId) || pendingChats.contains(command.requestId) ||
        pendingMarks.contains(command.requestId) || pendingIdentity.contains(command.requestId))
        return RejectCommand(generation, command.requestId, CommandFailureCode::InvalidRequest);
      lastRequest      = command.requestId;
      const bool blank = std::ranges::all_of(command.displayName, [](char value) { return value == ' ' || value == '\t'; });
      if (blank || !Utils::Text::ValidUtf8(command.displayName) || Utils::Text::HasControl(command.displayName))
        return RejectCommand(generation, command.requestId, CommandFailureCode::InvalidRequest);
      if (!pendingNames.empty()) return RejectCommand(generation, command.requestId, CommandFailureCode::Busy);

      auto packet = codec.Encode(command);
      if (!packet) return RejectCommand(generation, command.requestId, CommandFailureCode::EncodingFailed);
      auto sent = transport->Send(std::move(*packet));
      if (!sent) return Fail(sent.error());
      pendingNames.insert(command.requestId);
      return {};
    }

    template <class T>
    Result<void> SendPlayerUpdate(std::uint64_t generation, T& command, bool locationTransition = false)
    {
      const auto requestId = exchange.NextRequestId();
      if (!requestId) return std::unexpected{DreamNetError::Make(DreamNetErrorCode::InvalidOperation, "Request IDs exhausted")};
      if (generation != model.Generation()) return RejectCommand(generation, *requestId, CommandFailureCode::StaleGeneration);
      if (phase != SessionPhase::Ready) return RejectCommand(generation, *requestId, CommandFailureCode::SessionNotReady);
      if (pendingUpdates.size() >= config.maxPendingPlayerUpdates) return RejectCommand(generation, *requestId, CommandFailureCode::Busy);

      auto packet = codec.Encode(Wire::UpdatePlayer{*requestId, std::move(command)});
      if (!packet) return RejectCommand(generation, *requestId, CommandFailureCode::EncodingFailed);
      auto sent = transport->Send(std::move(*packet));
      if (!sent) return Fail(sent.error());

      pendingUpdates.insert(*requestId);
      if (locationTransition) pendingLocation = *requestId;
      return {};
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
      exchange.TakeCommands(
        commands,
        pendingChats.size() + pendingUpdates.size() + pendingMarks.size() + pendingIdentity.size() + pendingNames.size() + openingReply +
          model.PendingServerRejectionCount());
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
    std::unordered_map<std::uint64_t, Domain::ChatChannelId> pendingChats;
    std::unordered_set<std::uint64_t>                        pendingUpdates;
    std::unordered_set<std::uint64_t>                        pendingMarks;
    std::unordered_set<std::uint64_t>                        pendingIdentity;
    std::unordered_set<std::uint64_t>                        pendingNames;
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
