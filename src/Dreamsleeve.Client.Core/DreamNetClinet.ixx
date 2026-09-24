module;

#include <enet/enet.h>
#include <spdlog/spdlog.h>

export module DreamNet.Client;

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

  static DreamNetClientConfig Default() noexcept
  {
    auto hostConfig = NetConfig::Default();
    hostConfig.maxPeers = 1;

    return
    {
      .host = hostConfig,
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
    auto clientHostResult = DreamNetHost::TryCreateClient(config.host);
    if (!clientHostResult)
    {
      return std::unexpected{std::move(clientHostResult.error())};
    }

    return std::unique_ptr<DreamNetClient>{new DreamNetClient(std::move(*clientHostResult), std::move(config))};
  }
  
  ClientState State()                  const noexcept { return state; }
  void        SetState(ClientState newState) noexcept { state = newState; }
  
  NetOperationResult BeginConnect()
  {
    auto initiateConnectResult = host.Connect(clientConfig.serverAddress, clientConfig.host.channelLimit);
    if (!initiateConnectResult)
    {
      SetState(ClientState::Faulted);
      return std::unexpected{std::move(initiateConnectResult.error())};
    }
    
    serverPeer = *initiateConnectResult;
    SetState(ClientState::Connecting);
    
    return {};
  }
  
  NetOperationResult Poll(TimeOutMs firstWaitMs = 0, std::size_t maxEvents = 64)
  {
    return {};
  }
  
private:
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
            std::format(
                "Unsupported ENet event type: {}",
                static_cast<int>(event.Type())
            )
        );
    }
  }
  
  NetOperationResult HandleConnect(DreamNetEvent& event)
  {
    SetState(ClientState::Connected);
    return {};
  }
  
  NetOperationResult HandleDisconnect(DreamNetEvent& event)
  {
    SetState(ClientState::Disconnected);
    return {};
  }
  
  NetOperationResult HandleReceive(DreamNetEvent& event)
  {
    return {};
  }

  void CheckDeadline(Clock::time_point now);
  
  explicit DreamNetClient(DreamNetHost createdHost, DreamNetClientConfig clientConfig) noexcept
    : host(std::move(createdHost)), clientConfig(std::move(clientConfig)) {}
  
  DreamNetHost                     host;
  const DreamNetClientConfig       clientConfig;
  
  std::optional<DreamNetPeer>      serverPeer;
  ClientState                      state = ClientState::Disconnected;
  std::optional<Clock::time_point> deadline;
};

export using DreamNetClientPtr = std::unique_ptr<DreamNetClient>;
