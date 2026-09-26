#include <doctest/doctest.h>
#include "chat.pb.h"

import Dreamsleeve.Client.Codec;
import DreamNet.Address;
import DreamNet.Core;
import DreamNet.Event;
import DreamNet.Host;
import DreamNet.Packet;
import DreamNet.Peer;
import DreamNet.Runtime;
import std;

namespace
{

  using namespace std::chrono_literals;

  Port NextTestPort() noexcept
  {
    static std::atomic_uint16_t nextPort = 19000;
    return nextPort.fetch_add(1);
  }

  std::optional<DreamNetEvent> ServiceHost(DreamNetHost& host, const TimeOutMs timeoutMs = 5)
  {
    auto result = host.Service(timeoutMs);
    if (!result.has_value())
    {
      FAIL(result.error().ToLogString());
    }

    return std::move(result.value());
  }

  template <typename Predicate>
  std::optional<DreamNetEvent> TryWaitForEvent(
    DreamNetHost&   host,
    Predicate&&     predicate,
    const int       maxAttempts = 200,
    const TimeOutMs timeoutMs   = 5)
  {
    for (int attempt = 0; attempt < maxAttempts; ++attempt)
    {
      auto maybeEvent = ServiceHost(host, timeoutMs);
      if (maybeEvent && predicate(*maybeEvent))
      {
        return std::move(maybeEvent);
      }
    }

    return std::nullopt;
  }

  struct ConnectedHosts final
  {
    DreamNetRuntime runtime;
    DreamNetHost    serverHost;
    DreamNetHost    clientHost;
    DreamNetPeer    clientPeer;
    DreamNetPeer    serverPeer;
    Port            serverPort;
  };

  ConnectedHosts CreateConnectedHosts(std::size_t maxPacketBytes = NetConfig::Default().maxPacketBytes)
  {
    auto runtimeResult = DreamNetRuntime::TryInitialize();
    REQUIRE(runtimeResult.has_value());

    ServerConfig serverConfig = ServerConfig::Default();
    serverConfig.address      = DreamNetAddress::Loopback(NextTestPort());
    serverConfig.maxPeers     = 4;
    serverConfig.channelLimit = 2;
    serverConfig.maxPacketBytes = maxPacketBytes;
    serverConfig.maxWaitingData = maxPacketBytes * 2;

    auto serverResult = DreamNetHost::TryCreateServer(serverConfig);
    if (!serverResult.has_value())
    {
      FAIL(serverResult.error().ToLogString());
    }

    const auto serverInfo = serverResult->GetHostInfo();
    REQUIRE(serverInfo.has_value());

    auto clientConfig = NetConfig::Default();
    clientConfig.maxPacketBytes = maxPacketBytes;
    clientConfig.maxWaitingData = maxPacketBytes * 2;
    auto clientResult = DreamNetHost::TryCreateClient(clientConfig);
    if (!clientResult.has_value())
    {
      FAIL(clientResult.error().ToLogString());
    }

    auto clientPeerResult = clientResult->Connect(serverConfig.address, 1);
    if (!clientPeerResult.has_value())
    {
      FAIL(clientPeerResult.error().ToLogString());
    }

    std::optional<DreamNetPeer> serverPeer           = std::nullopt;
    bool                        clientConnectedEvent = false;

    for (int attempt = 0; attempt < 200 && (!serverPeer || !clientConnectedEvent); ++attempt)
    {
      if (auto maybeServerEvent = ServiceHost(serverResult.value(), 5); maybeServerEvent)
      {
        if (maybeServerEvent->IsConnect())
        {
          auto peer = maybeServerEvent->Peer();
          REQUIRE(peer.has_value());
          serverPeer = std::move(peer.value());
        }
      }

      if (auto maybeClientEvent = ServiceHost(clientResult.value(), 5); maybeClientEvent)
      {
        if (maybeClientEvent->IsConnect())
        {
          clientConnectedEvent = true;
        }
      }
    }

    REQUIRE(serverPeer.has_value());
    REQUIRE(clientConnectedEvent);

    return ConnectedHosts{
        .runtime    = std::move(runtimeResult.value()),
        .serverHost = std::move(serverResult.value()),
        .clientHost = std::move(clientResult.value()),
        .clientPeer = std::move(clientPeerResult.value()),
        .serverPeer = std::move(serverPeer.value()),
        .serverPort = serverInfo->address.GetPort(),
    };
  }

}

TEST_SUITE_BEGIN("DreamNet.Network");

TEST_CASE("DreamNetHost.TryCreateClient - invalid config contains detailed cause")
{
  ClientConfig config = NetConfig::Default();
  config.maxPeers     = 0;

  auto result = DreamNetHost::TryCreateClient(config);
  REQUIRE_FALSE(result.has_value());
  REQUIRE(result.error().code == DreamNetErrorCode::InvalidConfig);
  REQUIRE(result.error().HasCause());
  REQUIRE(result.error().Cause() != nullptr);
  REQUIRE(result.error().Cause()->message.find("maxPeers") != std::string::npos);
}

TEST_CASE("DreamNetHost.ApplyRuntimeConfig - invalid channel limit is rejected")
{
  auto runtimeResult = DreamNetRuntime::TryInitialize();
  REQUIRE(runtimeResult.has_value());

  auto hostResult = DreamNetHost::TryCreateClient(NetConfig::Default());
  REQUIRE(hostResult.has_value());

  HostRuntimeConfig config{
      .bandwidthLimit = std::nullopt,
      .channelLimit   = std::numeric_limits<ChannelLimit>::max(),
  };

  auto applyResult = hostResult->ApplyRuntimeConfig(config);
  REQUIRE_FALSE(applyResult.has_value());
  REQUIRE(applyResult.error().code == DreamNetErrorCode::InvalidConfig);
  REQUIRE(applyResult.error().message.find("channelLimit") != std::string::npos);
}

TEST_CASE("DreamNet client server connect populates host and peer state")
{
  auto connected = CreateConnectedHosts();

  const auto serverInfo = connected.serverHost.GetHostInfo();
  REQUIRE(serverInfo.has_value());
  CHECK(serverInfo->peerCount == 4);
  CHECK(serverInfo->channelLimit == 2);
  CHECK(serverInfo->address.GetPort() == connected.serverPort);

  const auto serverTelemetry = connected.serverHost.GetHostTelemetry();
  REQUIRE(serverTelemetry.has_value());
  CHECK(serverTelemetry->connectedPeers == 1);

  const auto clientTelemetry = connected.clientHost.GetHostTelemetry();
  REQUIRE(clientTelemetry.has_value());
  CHECK(clientTelemetry->connectedPeers == 1);

  const auto clientPeerInfo = connected.clientPeer.GetPeerInfo();
  REQUIRE(clientPeerInfo.has_value());
  CHECK(clientPeerInfo->state == ENET_PEER_STATE_CONNECTED);
  CHECK(clientPeerInfo->channelCount == 1);

  const auto serverPeerInfo = connected.serverPeer.GetPeerInfo();
  REQUIRE(serverPeerInfo.has_value());
  CHECK(serverPeerInfo->state == ENET_PEER_STATE_CONNECTED);

  const auto clientPeerTelemetry = connected.clientPeer.GetPeerTelemetry();
  REQUIRE(clientPeerTelemetry.has_value());
  CHECK(clientPeerTelemetry->transportInfo.state == ENET_PEER_STATE_CONNECTED);
}

TEST_CASE("DreamNetPeer.PushSpan delivers receive event with packet payload")
{
  auto connected = CreateConnectedHosts();

  constexpr std::array<std::byte, 4> payload = {
      std::byte{0x01},
      std::byte{0x02},
      std::byte{0x03},
      std::byte{0x04},
  };

  auto sendResult = connected.clientPeer.PushSpan(std::span{payload.data(), payload.size()}, 0);
  if (!sendResult.has_value())
  {
    FAIL(sendResult.error().ToLogString());
  }
  connected.clientHost.FlushPackets();

  auto maybeReceiveEvent = TryWaitForEvent(connected.serverHost, [](const DreamNetEvent& event) { return event.IsReceive(); });

  REQUIRE(maybeReceiveEvent.has_value());
  CHECK(maybeReceiveEvent->HasPeer());
  CHECK(maybeReceiveEvent->HasPacket());
  REQUIRE(maybeReceiveEvent->TryChannelId().has_value());
  CHECK(maybeReceiveEvent->TryChannelId().value() == 0);
  CHECK_FALSE(maybeReceiveEvent->TryDisconnectReason().has_value());

  const auto* packet = maybeReceiveEvent->ViewPacket();
  REQUIRE(packet != nullptr);
  REQUIRE(packet->DataBytesView().size() == payload.size());
  CHECK(packet->DataBytesView()[0] == payload[0]);
  CHECK(packet->DataBytesView()[1] == payload[1]);
  CHECK(packet->DataBytesView()[2] == payload[2]);
  CHECK(packet->DataBytesView()[3] == payload[3]);

  const auto serverTelemetry = connected.serverHost.GetHostTelemetry();
  REQUIRE(serverTelemetry.has_value());
  CHECK(serverTelemetry->totalReceivedPackets > 0);
  CHECK(serverTelemetry->totalReceivedData >= payload.size());
}

TEST_CASE("DreamNetHost.BroadcastPushPacketSpan sends packet to connected client")
{
  auto connected = CreateConnectedHosts();

  constexpr std::array<std::byte, 3> payload = {
      std::byte{0xAA},
      std::byte{0xBB},
      std::byte{0xCC},
  };

  auto broadcastResult = connected.serverHost.BroadcastPushPacketSpan(std::span{payload.data(), payload.size()}, 0);
  if (!broadcastResult.has_value())
  {
    FAIL(broadcastResult.error().ToLogString());
  }
  connected.serverHost.FlushPackets();

  auto maybeReceiveEvent = TryWaitForEvent(connected.clientHost, [](const DreamNetEvent& event) { return event.IsReceive(); });

  REQUIRE(maybeReceiveEvent.has_value());
  REQUIRE(maybeReceiveEvent->HasPacket());

  const auto* packet = maybeReceiveEvent->ViewPacket();
  REQUIRE(packet != nullptr);
  REQUIRE(packet->DataBytesView().size() == payload.size());
  CHECK(packet->DataBytesView()[0] == payload[0]);
  CHECK(packet->DataBytesView()[1] == payload[1]);
  CHECK(packet->DataBytesView()[2] == payload[2]);

  const auto serverTelemetry = connected.serverHost.GetHostTelemetry();
  REQUIRE(serverTelemetry.has_value());
  CHECK(serverTelemetry->totalSentPackets > 0);
  CHECK(serverTelemetry->totalSentData >= payload.size());
}

TEST_CASE("DreamNet disconnect produces disconnect event with reason")
{
  auto connected = CreateConnectedHosts();

  connected.clientPeer.Disconnect(DisconnectType::Normal, DisconnectReason::Unspecified);
  connected.clientHost.FlushPackets();

  std::optional<DreamNetEvent> serverDisconnectEvent = std::nullopt;

  for (int attempt = 0; attempt < 200 && !serverDisconnectEvent; ++attempt)
  {
    if (auto maybeServerEvent = ServiceHost(connected.serverHost, 5); maybeServerEvent)
    {
      if (maybeServerEvent->IsDisconnect())
      {
        serverDisconnectEvent = std::move(maybeServerEvent);
      }
    }

    auto maybeClientEvent = ServiceHost(connected.clientHost, 5);
    if (maybeClientEvent && maybeClientEvent->IsDisconnect())
    {
      // drain client-side disconnect event so ENet can finish the cycle
    }
  }

  REQUIRE(serverDisconnectEvent.has_value());
  CHECK(serverDisconnectEvent->HasPeer());
  CHECK_FALSE(serverDisconnectEvent->HasPacket());
  REQUIRE(serverDisconnectEvent->TryDisconnectReason().has_value());
  CHECK(serverDisconnectEvent->TryDisconnectReason().value() == DisconnectReason::Unspecified);
}

TEST_CASE("DreamNetPeer.ApplyRuntimeConfig - invalid ping interval is rejected")
{
  auto connected = CreateConnectedHosts();

  PeerRuntimeConfig config{
      .timeout        = std::nullopt,
      .throttle       = std::nullopt,
      .pingIntervalMs = 0,
  };

  auto applyResult = connected.clientPeer.ApplyRuntimeConfig(config);
  REQUIRE_FALSE(applyResult.has_value());
  REQUIRE(applyResult.error().code == DreamNetErrorCode::InvalidPingInterval);
}

TEST_CASE("DreamNet disconnect carries a non-zero reason across the wire")
{
  // Unspecified is 0, which is also what enet_protocol_notify_disconnect writes
  // on a timeout - so only a non-zero reason proves the value actually travelled.
  auto connected = CreateConnectedHosts();

  connected.clientPeer.Disconnect(DisconnectType::Normal, DisconnectReason::Kicked);
  connected.clientHost.FlushPackets();

  std::optional<DreamNetEvent> serverDisconnectEvent = std::nullopt;

  for (int attempt = 0; attempt < 200 && !serverDisconnectEvent; ++attempt)
  {
    if (auto maybeServerEvent = ServiceHost(connected.serverHost, 5); maybeServerEvent)
    {
      if (maybeServerEvent->IsDisconnect())
      {
        serverDisconnectEvent = std::move(maybeServerEvent);
      }
    }

    auto maybeClientEvent = ServiceHost(connected.clientHost, 5);
    if (maybeClientEvent && maybeClientEvent->IsDisconnect())
    {
      // drain client-side disconnect event so ENet can finish the cycle
    }
  }

  REQUIRE(serverDisconnectEvent.has_value());
  REQUIRE(serverDisconnectEvent->TryDisconnectReason().has_value());
  CHECK(serverDisconnectEvent->TryDisconnectReason().value() == DisconnectReason::Kicked);
}

TEST_CASE("DreamNet disconnect event peer is already reset by ENet")
{
  // enet_protocol_dispatch_incoming_commands calls enet_peer_reset before the
  // disconnect event is returned, so channelCount is gone by the time we see it.
  // The address survives the reset, which is what makes it loggable.
  auto connected = CreateConnectedHosts();

  const auto clientAddressBefore = connected.serverPeer.GetPeerInfo();
  REQUIRE(clientAddressBefore.has_value());

  connected.clientPeer.Disconnect(DisconnectType::Normal, DisconnectReason::ClientShutdown);
  connected.clientHost.FlushPackets();

  std::optional<DreamNetEvent> serverDisconnectEvent = std::nullopt;

  for (int attempt = 0; attempt < 200 && !serverDisconnectEvent; ++attempt)
  {
    if (auto maybeServerEvent = ServiceHost(connected.serverHost, 5); maybeServerEvent)
    {
      if (maybeServerEvent->IsDisconnect())
      {
        serverDisconnectEvent = std::move(maybeServerEvent);
      }
    }

    auto maybeClientEvent = ServiceHost(connected.clientHost, 5);
    if (maybeClientEvent && maybeClientEvent->IsDisconnect())
    {
      // drain client-side disconnect event so ENet can finish the cycle
    }
  }

  REQUIRE(serverDisconnectEvent.has_value());
  REQUIRE(serverDisconnectEvent->HasPeer());

  auto peer = serverDisconnectEvent->Peer();
  REQUIRE(peer.has_value());

  const auto info = peer->GetPeerInfo();
  REQUIRE(info.has_value());
  CHECK(peer->IsDisconnected());
  CHECK(info->channelCount == 0);
  CHECK(info->address.HostRaw() == clientAddressBefore->address.HostRaw());
}

TEST_CASE("DreamNetPacket.TryAllocateWith delivers a serialized payload end to end")
{
  // The zero-copy send path: allocate the ENet packet first, write straight into
  // its buffer, then hand it to the peer - no staging buffer anywhere.
  auto connected = CreateConnectedHosts();

  constexpr std::size_t payloadSize = 6;

  auto packet = DreamNetPacket::TryAllocateWith(payloadSize, [](std::span<std::byte> buffer) {
    if (buffer.size() != payloadSize)
    {
      return false;
    }

    for (std::size_t index = 0; index < buffer.size(); ++index)
    {
      buffer[index] = static_cast<std::byte>(index + 1);
    }

    return true;
  });

  if (!packet.has_value())
  {
    FAIL(packet.error().ToLogString());
  }

  auto sendResult = connected.clientPeer.PushPacket(std::move(packet.value()), 0);
  if (!sendResult.has_value())
  {
    FAIL(sendResult.error().ToLogString());
  }
  connected.clientHost.FlushPackets();

  auto maybeReceiveEvent = TryWaitForEvent(connected.serverHost, [](const DreamNetEvent& event) { return event.IsReceive(); });

  REQUIRE(maybeReceiveEvent.has_value());

  const auto* received = maybeReceiveEvent->ViewPacket();
  REQUIRE(received != nullptr);
  REQUIRE(received->DataBytesView().size() == payloadSize);

  for (std::size_t index = 0; index < payloadSize; ++index)
  {
    CHECK(received->DataBytesView()[index] == static_cast<std::byte>(index + 1));
  }
}

TEST_CASE("Configured host packet limits apply to clients servers and broadcast ownership")
{
  constexpr std::size_t limit = 4096;
  auto connected = CreateConnectedHosts(limit);
  for (auto* host : {&connected.serverHost, &connected.clientHost})
  {
    auto info = host->GetHostInfo();
    REQUIRE(info);
    CHECK(info->maxPacketBytes == limit);
    CHECK(info->maxWaitingData == limit * 2);
  }
  const std::vector<std::byte> bytes(limit, std::byte{0x2a});
  REQUIRE(connected.clientPeer.PushSpan(bytes, 0));
  connected.clientHost.FlushPackets();
  auto received = TryWaitForEvent(connected.serverHost, [](const DreamNetEvent& event) { return event.IsReceive(); });
  REQUIRE(received);
  REQUIRE(received->ViewPacket());
  CHECK(received->ViewPacket()->Size() == limit);

  const std::vector<std::byte> oversized(limit + 1);
  CHECK_FALSE(connected.clientPeer.PushSpan(oversized, 0));
  CHECK_FALSE(connected.serverHost.BroadcastPushPacketSpan(oversized, 0));
  auto packet = DreamNetPacket::TryFromSpan(oversized);
  REQUIRE(packet);
  CHECK_FALSE(connected.clientPeer.PushPacket(std::move(*packet), 0));
  CHECK(packet->IsValid());
  CHECK_FALSE(connected.serverHost.BroadcastPushPacket(std::move(*packet), 0));
  CHECK(packet->IsValid());
}

TEST_CASE("Invalid host packet budgets are rejected before creating a socket")
{
  auto config = NetConfig::Default();
  config.maxPacketBytes = 0;
  CHECK_FALSE(DreamNetHost::TryCreateClient(config));
  config.maxPacketBytes = 2048;
  config.maxWaitingData = 2047;
  CHECK_FALSE(DreamNetHost::TryCreateClient(config));
  config.maxPacketBytes = DreamNetPacket::MaxDataSize + 1;
  config.maxWaitingData = config.maxPacketBytes;
  CHECK_FALSE(DreamNetHost::TryCreateClient(config));
}



TEST_CASE("Chat codec serializes directly into a transferable reliable ENet packet")
{
  auto connected = CreateConnectedHosts();
  auto codec = Dreamsleeve::Client::Wire::Codec::TryCreate({});
  REQUIRE(codec);
  auto packet = codec->Encode(Dreamsleeve::Client::SendChat{42, 1, "Привет"});
  REQUIRE(packet);
  CHECK(packet->Flags() == PacketFlag::Reliable);
  REQUIRE(connected.clientPeer.PushPacket(std::move(*packet), 0));
  CHECK_FALSE(packet->IsValid()); // Successful send transferred ownership.
  connected.clientHost.FlushPackets();

  auto received = TryWaitForEvent(connected.serverHost, [](const DreamNetEvent& event) { return event.IsReceive(); });
  REQUIRE(received);
  REQUIRE(received->ViewPacket());
  const auto bytes = received->ViewPacket()->DataBytesView();
  Dreamsleeve::Protocol::Chat::ClientPacket decoded;
  REQUIRE(decoded.ParseFromArray(bytes.data(), static_cast<int>(bytes.size())));
  CHECK(decoded.request_id() == 42);
  CHECK(decoded.send_chat().channel_id() == 1);
  CHECK(decoded.send_chat().text() == "Привет");
}

TEST_SUITE_END();
