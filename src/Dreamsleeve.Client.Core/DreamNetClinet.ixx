module;

#include <enet/enet.h>
#include <spdlog/spdlog.h>
#include <magic_enum/magic_enum.hpp>

export module DreamNet.Client;

#undef min
#undef max

import std;

import DreamNet.Address;
import DreamNet.Core;
import DreamNet.Event;
import DreamNet.Host;
import DreamNet.Packet;
import DreamNet.Peer;
import DreamNet.Runtime;

export enum class ClientState : std::uint8_t
{
  Disconnected,
  Connecting,
  Connected,
  Disconnecting,
  Faulted
};

export struct DreamNetClientConfig final
{
  NetConfig       host;
  DreamNetAddress serverAddress;
  TimeOutMs       connectTimeoutMs    = 5000;
  TimeOutMs       disconnectTimeoutMs = 2000;

  static DreamNetClientConfig Default() noexcept
  {
    auto hostConfig     = NetConfig::Default();
    hostConfig.maxPeers = 1;

    return {
        .host          = hostConfig,
        .serverAddress = DreamNetAddress::Loopback(8778),
    };
  }
};

export class DreamNetClient final
{
  public:

  using Clock  = std::chrono::steady_clock;
  using Result = NetResult<std::unique_ptr<DreamNetClient>>;

  DreamNetClient(const DreamNetClient&)            = delete;
  DreamNetClient& operator=(const DreamNetClient&) = delete;
  DreamNetClient(DreamNetClient&&)                 = delete;
  DreamNetClient& operator=(DreamNetClient&&)      = delete;
  ~DreamNetClient()                                = default;

  static Result TryCreate(DreamNetClientConfig config)
  {
    if (config.connectTimeoutMs == 0 || config.disconnectTimeoutMs == 0)
    {
      return DreamNetError::MakeUnexpected(
        DreamNetErrorCode::InvalidConfig,
        std::format(
          "Client timeouts must be greater than zero, current config: connectTimeout = {} disconnectTimeout = {}",
          config.connectTimeoutMs,
          config.disconnectTimeoutMs));
    }

    auto clientHostResult = DreamNetHost::TryCreateClient(config.host);
    if (!clientHostResult)
    {
      return std::unexpected{std::move(clientHostResult.error())};
    }

    return std::unique_ptr<DreamNetClient>{new DreamNetClient(std::move(*clientHostResult), std::move(config))};
  }

  ClientState State() const noexcept
  {
    return state;
  }

  NetOperationResult BeginConnect()
  {
    if (state != ClientState::Disconnected || serverPeer)
    {
      return DreamNetError::MakeUnexpected(
        DreamNetErrorCode::InvalidPeerState,
        std::format("BeginConnect requires a disconnected client, current state: {}", magic_enum::enum_name(State())));
    }

    auto peerResult = host.Connect(clientConfig.serverAddress, clientConfig.host.channelLimit);

    if (!peerResult)
    {
      return std::unexpected{std::move(peerResult.error())};
    }

    serverPeer = *peerResult;

    deadline = Clock::now() + std::chrono::milliseconds{clientConfig.connectTimeoutMs};

    state = ClientState::Connecting;

    return {};
  }

  NetOperationResult BeginDisconnect(DisconnectReason reason = DisconnectReason::ClientShutdown)
  {
    if (state != ClientState::Connected || !serverPeer || !serverPeer->IsConnected())
    {
      return DreamNetError::MakeUnexpected(DreamNetErrorCode::InvalidPeerState, "BeginDisconnect requires a connected client");
    }

    serverPeer->Disconnect(DisconnectType::Later, reason);

    deadline = Clock::now() + std::chrono::milliseconds{clientConfig.disconnectTimeoutMs};

    state = ClientState::Disconnecting;

    return {};
  }

  NetOperationResult Poll(TimeOutMs firstWaitMs = 0, std::size_t maxEvents = 64)
  {
    if (maxEvents == 0)
    {
      return DreamNetError::MakeUnexpected(DreamNetErrorCode::InvalidConfig, "Poll maxEvents must be greater than zero");
    }

    if (state == ClientState::Faulted)
    {
      return DreamNetError::MakeUnexpected(DreamNetErrorCode::InvalidPeerState, "Cannot poll a faulted client");
    }

    for (std::size_t processed = 0; processed < maxEvents; ++processed)
    {
      auto deadlineResult = CheckDeadline(Clock::now());

      if (!deadlineResult)
      {
        return deadlineResult;
      }

      if (state == ClientState::Disconnected)
      {
        break;
      }

      const TimeOutMs waitMs = processed == 0 ? ClampWaitToDeadline(firstWaitMs, Clock::now()) : TimeOutMs{0};

      auto serviceResult = host.Service(waitMs);

      if (!serviceResult)
      {
        return Fail(std::move(serviceResult.error()));
      }

      auto& maybeEvent = serviceResult.value();

      if (!maybeEvent)
      {
        break;
      }

      auto dispatchResult = DispatchEvent(maybeEvent.value());

      if (!dispatchResult)
      {
        return Fail(std::move(dispatchResult.error()));
      }
    }

    if (state != ClientState::Disconnected && state != ClientState::Faulted)
    {
      host.FlushPackets();
    }

    return CheckDeadline(Clock::now());
  }

  private:

  void ResetConnection(ClientState nextState) noexcept
  {
    if (serverPeer)
    {
      serverPeer->Reset();
    }

    serverPeer.reset();
    deadline.reset();
    state = nextState;
  }

  NetOperationResult Fail(DreamNetError error)
  {
    ResetConnection(ClientState::Faulted);

    return std::unexpected{std::move(error)};
  }

  TimeOutMs ClampWaitToDeadline(TimeOutMs requestedWaitMs, Clock::time_point now) const noexcept
  {
    if (!deadline)
    {
      return requestedWaitMs;
    }

    if (now >= *deadline)
    {
      return 0;
    }

    const auto remaining = std::chrono::duration_cast<std::chrono::milliseconds>(*deadline - now);

    const auto requested = std::chrono::milliseconds{requestedWaitMs};

    const auto wait = std::min(requested, remaining);

    return static_cast<TimeOutMs>(wait.count());
  }

  bool IsServerEvent(const DreamNetEvent& event) const noexcept
  {
    const auto eventPeer = event.Peer();

    return serverPeer.has_value() && eventPeer.has_value() && serverPeer->Native() == eventPeer->Native();
  }

  NetOperationResult DispatchEvent(DreamNetEvent& event)
  {
    switch (event.Type())
    {
      case EventType::Connect:
        return HandleConnect(event);

      case EventType::Disconnect:
        return HandleDisconnect(event);

      case EventType::Receive:
        return HandleReceive(event);

      case EventType::None:
        return {};

      default:
        return DreamNetError::MakeUnexpected(
          DreamNetErrorCode::FailedEventBuild,
          std::format("Unsupported ENet event type: {}", static_cast<int>(event.Type())));
    }
  }

  NetOperationResult HandleConnect(DreamNetEvent& event)
  {
    if (!IsServerEvent(event))
    {
      return {};
    }

    if (state != ClientState::Connecting)
    {
      // Do Nothing while client isn't connecting state
      return {};
    }

    deadline.reset();
    state = ClientState::Connected;

    spdlog::info("DreamNet client connected");

    // Здесь позднее формируется выходное ClientConnected.

    return {};
  }

  NetOperationResult HandleDisconnect(DreamNetEvent& event)
  {
    if (!IsServerEvent(event))
    {
      return {};
    }

    const auto disconnectData = event.Info().TryData();

    serverPeer.reset();
    deadline.reset();
    state = ClientState::Disconnected;

    spdlog::info("DreamNet client disconnected, data {}", disconnectData.value_or(0));

    // Здесь позднее формируется выходное ClientClosed.

    return {};
  }

  NetOperationResult HandleReceive(DreamNetEvent& event)
  {
    return {};
  }

  NetOperationResult CheckDeadline(Clock::time_point now)
  {
    if (!deadline || now < *deadline)
    {
      return {};
    }

    const auto expiredState = state;

    if (expiredState != ClientState::Connecting && expiredState != ClientState::Disconnecting)
    {
      return Fail(DreamNetError::Make(DreamNetErrorCode::InvalidPeerState, "Deadline exists in an unexpected client state"));
    }

    ResetConnection(ClientState::Disconnected);

    if (expiredState == ClientState::Connecting)
    {
      return DreamNetError::MakeUnexpected(DreamNetErrorCode::ConnectTimeout, "Connection attempt timed out");
    }

    return DreamNetError::MakeUnexpected(DreamNetErrorCode::DisconnectTimeout, "Graceful disconnect timed out");
  }

  // Hard abort without recovery host
  void Abort() noexcept
  {
    const auto nextState = state == ClientState::Faulted ? ClientState::Faulted : ClientState::Disconnected;

    ResetConnection(nextState);
  }

  explicit DreamNetClient(DreamNetHost createdHost, DreamNetClientConfig clientConfig) noexcept
      : host(std::move(createdHost)),
        clientConfig(std::move(clientConfig))
  {}

  DreamNetHost               host;
  const DreamNetClientConfig clientConfig;

  std::optional<DreamNetPeer>      serverPeer;
  ClientState                      state = ClientState::Disconnected;
  std::optional<Clock::time_point> deadline;
};

export using DreamNetClientPtr = std::unique_ptr<DreamNetClient>;
