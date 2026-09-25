module;

#include <enet/enet.h>
#include <cassert>
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

export struct ClientConnected final
{};

export struct ClientClosed final
{
  // Raw ENet event data; it may contain a DisconnectReason unknown to this client.
  std::uint32_t data;
};

export struct ClientReceived final
{
  ChannelId      channelId;
  DreamNetPacket packet;
};

// Receive events own their packets. DreamNetRuntime must outlive these events,
// just as it must outlive the client and every other ENet resource.
export using ClientEvent = std::variant<ClientConnected, ClientClosed, ClientReceived>;

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
    if (config.host.maxPeers != 1 || config.host.channelLimit == 0)
    {
      return DreamNetError::MakeUnexpected(
        DreamNetErrorCode::InvalidConfig,
        "DreamNetClient requires exactly one peer slot and an explicit non-zero channel count");
    }

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

  // Success means ENet accepted the packet for sending, not remote delivery.
  // Continue polling to service outgoing data, acknowledgements and timeouts.
  NetOperationResult Send(
    const DreamNetPacket::DataBytes bytes,
    const ChannelId                 channelId = 0,
    const PacketFlag                flags     = PacketFlag::Reliable)
  {
    auto validationResult = ValidateSend(channelId);
    if (!validationResult)
    {
      return validationResult;
    }

    return serverPeer->PushSpan(bytes, channelId, flags);
  }

  // Ownership transfers only on success. On failure the caller still owns packet.
  NetOperationResult Send(DreamNetPacket&& packet, const ChannelId channelId = 0)
  {
    auto validationResult = ValidateSend(channelId);
    if (!validationResult)
    {
      return validationResult;
    }

    return serverPeer->PushPacket(std::move(packet), channelId);
  }

  // All client operations, including Poll and Send, require one serial owner.
  // The caller processes/moves/clears the previous batch before calling again.
  // Events already appended remain valid even if a later operation returns an error.
  NetOperationResult Poll(std::vector<ClientEvent>& output, TimeOutMs firstWaitMs = 0, std::size_t maxEvents = 64)
  {
    if (!output.empty())
    {
      return DreamNetError::MakeUnexpected(
        DreamNetErrorCode::InvalidOperation,
        "Poll requires an empty output batch; process and clear the previous events first");
    }

    if (maxEvents == 0)
    {
      return DreamNetError::MakeUnexpected(DreamNetErrorCode::InvalidConfig, "Poll maxEvents must be greater than zero");
    }

    if (state == ClientState::Faulted)
    {
      return DreamNetError::MakeUnexpected(DreamNetErrorCode::InvalidPeerState, "Cannot poll a faulted client");
    }

    // Allocate before servicing events so appending a bounded batch does not allocate.
    output.reserve(maxEvents);

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

      auto dispatchResult = DispatchEvent(maybeEvent.value(), output);

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

  // Immediate local teardown; does not notify the remote peer or emit ClientClosed.
  // A faulted host still requires recreation after Abort.
  void Abort() noexcept
  {
    const auto nextState = state == ClientState::Faulted ? ClientState::Faulted : ClientState::Disconnected;

    ResetConnection(nextState);
  }

  private:

  NetOperationResult ValidateSend(const ChannelId channelId) const
  {
    if (state != ClientState::Connected || !serverPeer || !serverPeer->IsConnected())
    {
      return DreamNetError::MakeUnexpected(DreamNetErrorCode::InvalidPeerState, "Send requires a connected client");
    }

    // The server can negotiate fewer channels than the client requested.
    const auto peerInfo = serverPeer->GetPeerInfo();
    if (!peerInfo || channelId >= peerInfo->channelCount)
    {
      return DreamNetError::MakeUnexpected(
        DreamNetErrorCode::InvalidOperation,
        std::format("Channel {} is outside the negotiated channel count", channelId));
    }

    return {};
  }

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

  NetOperationResult DispatchEvent(DreamNetEvent& event, std::vector<ClientEvent>& output)
  {
    switch (event.Type())
    {
      case EventType::Connect:
        return HandleConnect(event, output);

      case EventType::Disconnect:
        return HandleDisconnect(event, output);

      case EventType::Receive:
        return HandleReceive(event, output);

      case EventType::None:
        return {};

      default:
        return DreamNetError::MakeUnexpected(
          DreamNetErrorCode::FailedEventBuild,
          std::format("Unsupported ENet event type: {}", static_cast<int>(event.Type())));
    }
  }

  NetOperationResult HandleConnect(DreamNetEvent& event, std::vector<ClientEvent>& output)
  {
    assert(IsServerEvent(event));

    if (state != ClientState::Connecting)
    {
      return DreamNetError::MakeUnexpected(DreamNetErrorCode::InvalidPeerState, "Connect event outside the connecting state");
    }

    deadline.reset();
    state = ClientState::Connected;

    output.emplace_back(ClientConnected{});

    return {};
  }

  NetOperationResult HandleDisconnect(DreamNetEvent& event, std::vector<ClientEvent>& output)
  {
    // DISCONNECT may arrive after ENet has reset connectID. Compare the slot,
    // not DreamNetPeer::IsValid(), and do not reset the already-reset peer again.
    assert(IsServerEvent(event));

    const auto disconnectData = event.Info().TryData();

    serverPeer.reset();
    deadline.reset();
    state = ClientState::Disconnected;

    output.emplace_back(ClientClosed{.data = disconnectData.value_or(0)});

    return {};
  }

  NetOperationResult HandleReceive(DreamNetEvent& event, std::vector<ClientEvent>& output)
  {
    assert(IsServerEvent(event));

    if (state != ClientState::Connected && state != ClientState::Disconnecting)
    {
      return DreamNetError::MakeUnexpected(DreamNetErrorCode::InvalidPeerState, "Receive event outside an active connection");
    }

    const auto channelId = event.TryChannelId();
    auto       packet    = event.AcquirePacket();
    if (!channelId || !packet || !packet->IsValid())
    {
      return DreamNetError::MakeUnexpected(DreamNetErrorCode::FailedReceivePacket, "Receive event has no channel or owning packet");
    }

    output.emplace_back(ClientReceived{.channelId = *channelId, .packet = std::move(*packet)});
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
