export module Dreamsleeve.Client.Runtime;

import std;
export import Dreamsleeve.Client.Codec;
export import DreamNet.Client;
import DreamNet.Core;

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
      if (config.chatCapacity == 0 || config.maxPendingChatRequests == 0 || config.sessionTimeoutMs == 0 || config.connectTimeoutMs == 0 || config.disconnectTimeoutMs == 0)
        return std::unexpected{
            DreamNetError::Make(DreamNetErrorCode::InvalidConfig, "Session timeouts, chat and pending request capacities must be positive")
        };

      auto codec = Wire::Codec::TryCreate(config);
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

    Result<void> Connect(std::string username, std::string displayName)
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

      transport = std::move(*created);
      opening   = Wire::OpenSession{*requestId, std::move(username), std::move(displayName)};
      lastRequest = *requestId;
      pendingChats.clear();
      model.ResetSession();
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

      return commandsResult;
    }

private:

    using Clock = std::chrono::steady_clock;

    ClientRuntime(Configuration settings, Wire::Codec codec, ClientExchange& exchange)
        : config(std::move(settings)),
          codec(std::move(codec)),
          exchange(exchange)
    {}

    void SetPhase(SessionPhase value)
    {
      phase = value;
      exchange.PublishPhase(value);
    }

    Result<void> Clear(SessionPhase value)
    {
      pendingChats.clear();
      model.ResetSession();
      phase = value;
      auto published = Publish(true);
      if (!published) SetPhase(SessionPhase::Faulted);
      return published;
    }

    Result<void> Publish(bool requestSnapshot = false)
    {
      if (!exchange.Publish(model, requestSnapshot, phase))
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

      auto packet = codec.Encode(opening);
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
      if (received.channelId != 0) return Unexpected("channel");

      auto response = codec.Decode(received.packet.DataBytesView());
      if (!response) return std::unexpected{response.error()};

      return std::visit([this](auto& value) { return Receive(value); }, *response);
    }

    Result<void> Receive(Wire::SessionOpened& opened)
    {
      if (phase != SessionPhase::Opening || opened.requestId != opening.requestId) return Unexpected("request_id");

      const auto generation = model.Generation();
      auto       channel    = model.RegisterChannel(opened.globalChannelId, config.chatCapacity);
      if (!channel) return std::unexpected{channel.error()};

      auto online = model.Apply(generation, OnlinePlayersReplaced{std::move(opened.players)});
      if (!online) return std::unexpected{online.error()};

      if (!model.FindPlayer(opened.selfPlayerId)) return Unexpected("self_player_id");

      auto chat = model.Apply(generation, ChatMessagesReceived{opened.globalChannelId, std::move(opened.recentMessages)});
      if (!chat) return std::unexpected{chat.error()};

      auto self = model.SetSelfPlayer(generation, opened.selfPlayerId);
      if (!self) return std::unexpected{self.error()};

      phase = SessionPhase::Ready;
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

      if (phase != SessionPhase::Ready || pendingChats.erase(rejection.requestId) == 0) return Unexpected("request_id");

      return Apply(rejection);
    }

    Result<void> Receive(Wire::ChatAccepted& accepted)
    {
      const auto found = pendingChats.find(accepted.requestId);
      if (phase != SessionPhase::Ready || found == pendingChats.end()) return Unexpected("request_id");
      if (accepted.changes.channelId != found->second) return Unexpected("channel_id");
      if (accepted.changes.messages.front().author.playerId != model.SelfPlayerId()) return Unexpected("author");

      // The server's publication is the only source of accepted chat content.
      // Correlation settles the command; the model path is shared with broadcasts.
      auto applied = Apply(accepted.changes);
      if (applied) pendingChats.erase(found);

      return applied;
    }

    Result<void> Receive(ChatMessagesReceived& value)
    {
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

    Result<void> RejectCommand(std::uint64_t generation, const SendChat& command, CommandFailureCode code)
    {
      if (!exchange.PublishCommandFailure({generation, command.requestId, code}))
        return std::unexpected{DreamNetError::Make(DreamNetErrorCode::InvalidOperation, "Command result capacity exhausted")};

      return {};
    }

    Result<void> Process(std::uint64_t generation, SendChat& command)
    {
      if (generation != model.Generation()) return RejectCommand(generation, command, CommandFailureCode::StaleGeneration);
      if (phase != SessionPhase::Ready) return RejectCommand(generation, command, CommandFailureCode::SessionNotReady);
      if (command.requestId <= lastRequest) return RejectCommand(generation, command, CommandFailureCode::InvalidRequest);

      lastRequest = command.requestId;
      if (!model.FindChatState(command.channelId)) return RejectCommand(generation, command, CommandFailureCode::InvalidRequest);
      if (pendingChats.size() >= config.maxPendingChatRequests) return RejectCommand(generation, command, CommandFailureCode::Busy);

      auto packet = codec.Encode(command);
      if (!packet) return RejectCommand(generation, command, CommandFailureCode::EncodingFailed);

      auto sent = transport->Send(std::move(*packet));
      if (!sent) return Fail(sent.error());

      pendingChats.emplace(command.requestId, command.channelId);
      return {};
    }

    Result<void> Process(std::uint64_t, RequestSnapshot&)
    {
      return Publish(true);
    }

    static Result<void> UnsupportedCommand()
    {
      return std::unexpected{DreamNetError::Make(DreamNetErrorCode::InvalidOperation, "Player telemetry has no wire contract yet")};
    }

    Result<void> Process(std::uint64_t, LocalPlayerState&) { return UnsupportedCommand(); }
    Result<void> Process(std::uint64_t, CharacterStarted&) { return UnsupportedCommand(); }
    Result<void> Process(std::uint64_t, GameExited&) { return UnsupportedCommand(); }

    Result<void> ProcessCommands()
    {
      const auto openingReply = phase == SessionPhase::Connecting || phase == SessionPhase::Opening ? 1u : 0u;
      exchange.TakeCommands(commands, pendingChats.size() + openingReply + model.PendingServerRejectionCount());
      Result<void> firstError;

      for (auto& queued : commands)
      {
        auto result = std::visit([&](auto& command) { return Process(queued.generation, command); }, queued.command);
        if (!result && firstError) firstError = std::unexpected{std::move(result.error())};
      }

      commands.clear();
      return firstError;
    }

    Configuration            config;
    Wire::Codec              codec;
    ClientExchange&          exchange;
    DreamNetClient::Ptr      transport;
    ClientModel              model;
    SessionPhase             phase{SessionPhase::Disconnected};
    Wire::OpenSession        opening;
    std::uint64_t            lastRequest{};
    std::unordered_map<std::uint64_t, Domain::ChatChannelId> pendingChats;
    std::vector<QueuedClientCommand> commands;
    Clock::time_point        deadline{};
    std::vector<ClientEvent> events;
  };

}
