#include <doctest/doctest.h>

import std;

import DreamNet.Address;
import DreamNet.Client;
import DreamNet.Core;
import DreamNet.Event;
import DreamNet.Host;
import DreamNet.Packet;
import DreamNet.Peer;
import DreamNet.Runtime;

TEST_CASE("DreamNetClient requires one peer slot and an explicit channel count")
{
  auto config = DreamNetClientConfig::Default();

  SUBCASE("Multiple peer slots are rejected")
  {
    config.host.maxPeers = 2;
  }
  SUBCASE("Implicit channel count is rejected")
  {
    config.host.channelLimit = 0;
  }

  auto result = DreamNetClient::TryCreate(config);
  REQUIRE_FALSE(result.has_value());
  CHECK(result.error().code == DreamNetErrorCode::InvalidConfig);
}

TEST_CASE("DreamNetClient guards state and preserves caller-owned packets on failed send")
{
  auto runtime = DreamNetRuntime::TryInitialize();
  REQUIRE(runtime.has_value());
  auto created = DreamNetClient::TryCreate(DreamNetClientConfig::Default());
  REQUIRE(created.has_value());
  auto client = std::move(*created);

  constexpr std::array payload{std::byte{0x00}, std::byte{0xFF}};
  auto                 packet = DreamNetPacket::TryFromSpan(std::span<const std::byte>{payload});
  REQUIRE(packet.has_value());
  auto sent = client->Send(std::move(*packet));
  REQUIRE_FALSE(sent.has_value());
  CHECK(sent.error().code == DreamNetErrorCode::InvalidPeerState);
  CHECK(packet->IsValid());
  CHECK(std::ranges::equal(packet->DataBytesView(), payload));
  CHECK_FALSE(client->BeginDisconnect().has_value());

  std::vector<ClientEvent> events;
  events.emplace_back(ClientConnected{});
  auto retainedBatch = client->Poll(events);
  REQUIRE_FALSE(retainedBatch.has_value());
  CHECK(retainedBatch.error().code == DreamNetErrorCode::InvalidOperation);
  CHECK(events.size() == 1);
  events.clear();

  auto invalidBudget = client->Poll(events, 0, 0);
  REQUIRE_FALSE(invalidBudget.has_value());
  CHECK(invalidBudget.error().code == DreamNetErrorCode::InvalidConfig);

  REQUIRE(client->BeginConnect().has_value());
  CHECK(client->State() == ClientState::Connecting);
  CHECK_FALSE(client->BeginConnect().has_value());
  client->Abort();
  CHECK(client->State() == ClientState::Disconnected);
}

TEST_CASE("DreamNetClient owns received packets and preserves disconnect data after ENet peer reset")
{
  using namespace std::chrono_literals;
  using Clock = std::chrono::steady_clock;

  auto runtime = DreamNetRuntime::TryInitialize();
  REQUIRE(runtime.has_value());

  auto serverConfig         = ServerConfig::Default();
  serverConfig.address      = DreamNetAddress::Loopback(0);  // OS chooses an available port.
  serverConfig.channelLimit = 1;
  auto server               = DreamNetHost::TryCreateServer(serverConfig);
  REQUIRE(server.has_value());
  const auto serverInfo = server->GetHostInfo();
  REQUIRE(serverInfo.has_value());
  REQUIRE(serverInfo->address.GetPort() != 0);

  auto clientConfig              = DreamNetClientConfig::Default();
  clientConfig.serverAddress     = serverInfo->address;
  clientConfig.host.channelLimit = 2;  // Server will negotiate this down to one.
  auto created                   = DreamNetClient::TryCreate(clientConfig);
  REQUIRE(created.has_value());
  auto client = std::move(*created);
  REQUIRE(client->BeginConnect().has_value());

  std::optional<DreamNetPeer>   serverPeer;
  std::optional<DreamNetPacket> retainedPacket;
  std::vector<ClientEvent>      events;
  bool                          connected = false;
  std::optional<std::uint32_t>  disconnectData;

  auto pumpUntil = [&](auto&& complete) {
    const auto deadline = Clock::now() + 3s;
    while (!complete() && Clock::now() < deadline)
    {
      events.clear();
      auto polled = client->Poll(events, 1, 8);
      REQUIRE(polled.has_value());
      for (auto& event : events)
      {
        if (std::holds_alternative<ClientConnected>(event))
        {
          connected = true;
        }
        else if (auto* received = std::get_if<ClientReceived>(&event))
        {
          CHECK(received->channelId == 0);
          retainedPacket = std::move(received->packet);
        }
        else if (const auto* closed = std::get_if<ClientClosed>(&event))
        {
          disconnectData = closed->data;
        }
      }

      auto serviced = server->Service(1);
      REQUIRE(serviced.has_value());
      if (*serviced && (*serviced)->IsConnect())
      {
        serverPeer = (*serviced)->Peer();
      }
    }
    REQUIRE(complete());
  };

  pumpUntil([&] { return connected && serverPeer.has_value(); });
  CHECK(client->State() == ClientState::Connected);

  constexpr std::array payload{std::byte{0x00}, std::byte{0x42}, std::byte{0xFF}};
  auto                 invalidChannel = client->Send(std::span<const std::byte>{payload}, 1);
  REQUIRE_FALSE(invalidChannel.has_value());
  CHECK(invalidChannel.error().code == DreamNetErrorCode::InvalidOperation);

  REQUIRE(serverPeer->PushSpan(std::span<const std::byte>{payload}, 0).has_value());
  server->FlushPackets();
  pumpUntil([&] { return retainedPacket.has_value(); });
  events.clear();  // Destroys the original moved-from ClientReceived event.
  CHECK(std::ranges::equal(retainedPacket->DataBytesView(), payload));

  serverPeer->Disconnect(DisconnectType::Normal, DisconnectReason::Kicked);
  server->FlushPackets();
  pumpUntil([&] { return disconnectData.has_value(); });
  CHECK(*disconnectData == static_cast<std::uint32_t>(DisconnectReason::Kicked));
  CHECK(client->State() == ClientState::Disconnected);

  client.reset();
  CHECK(std::ranges::equal(retainedPacket->DataBytesView(), payload));
}
