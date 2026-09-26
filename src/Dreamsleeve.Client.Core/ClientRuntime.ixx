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
      if (config.chatCapacity == 0 || config.sessionTimeoutMs == 0 || config.connectTimeoutMs == 0 || config.disconnectTimeoutMs == 0)
        return std::unexpected{
            DreamNetError::Make(DreamNetErrorCode::InvalidConfig, "Session timeouts and chat capacity must be positive")
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

      if (nextRequest == 0) return std::unexpected{DreamNetError::Make(DreamNetErrorCode::InvalidOperation, "Request IDs exhausted")};

      DreamNetClientConfig transportConfig{config.network, config.serverAddress, config.connectTimeoutMs, config.disconnectTimeoutMs};
      auto                 created = DreamNetClient::TryCreate(transportConfig);
      if (!created) return Fail(created.error());

      transport = std::move(*created);
      opening   = Wire::OpenSession{nextRequest++, std::move(username), std::move(displayName)};
      model.ResetSession();
      exchange.Publish(model);

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
        Clear(SessionPhase::Disconnected);
        return {};
      }

      auto result = transport->BeginDisconnect();
      if (!result) return Fail(result.error());

      Clear(SessionPhase::Disconnecting);

      return {};
    }

    // A caller-owned loop drives network work. No hidden threads or model callbacks.
    Result<void> Poll(TimeOutMs waitMs = 0)
    {
      if (!transport || phase == SessionPhase::Disconnected || phase == SessionPhase::Faulted) return {};

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

      return {};
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

    void Clear(SessionPhase value)
    {
      model.ResetSession();
      phase = value;
      exchange.Publish(model, true, phase);
    }

    Result<void> Fail(Error error)
    {
      if (transport) transport->Abort(DisconnectReason::ProtocolError);

      Clear(SessionPhase::Faulted);
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
      Clear(SessionPhase::Disconnected);
      return {};
    }

    Result<void> Handle(ClientReceived& received)
    {
      if (phase == SessionPhase::Disconnecting) return {};  // Late packets cannot reopen a closing session.
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
      exchange.Publish(model, true, phase);

      return {};
    }

    Result<void> Receive(ServerRejection& rejection)
    {
      if (phase != SessionPhase::Opening || rejection.requestId != opening.requestId) return Unexpected("request_id");

      auto applied = model.Apply(model.Generation(), rejection);
      if (!applied) return std::unexpected{applied.error()};

      return Disconnect();  // Clear publishes the correlated rejection alongside empty state.
    }

    Result<void> Receive(Wire::ChatAccepted&)
    {
      return Unexpected("unsolicited_chat_ack");
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

      exchange.Publish(model);

      return {};
    }

    Configuration            config;
    Wire::Codec              codec;
    ClientExchange&          exchange;
    DreamNetClient::Ptr      transport;
    ClientModel              model;
    SessionPhase             phase{SessionPhase::Disconnected};
    Wire::OpenSession        opening;
    std::uint64_t            nextRequest{1};
    Clock::time_point        deadline{};
    std::vector<ClientEvent> events;
  };

}
