#include <doctest/doctest.h>
#include <enet/enet.h>
import std;
import DreamNet.Peer;

namespace
{

  struct Hosts
  {
    ENetHost* server{};
    ENetHost* client{};
    ENetPeer* outgoing{};
    ENetPeer* incoming{};

    Hosts()
    {
      REQUIRE(enet_initialize() == 0);

      ENetAddress address{ENET_HOST_ANY, 0};
      server = enet_host_create(&address, 1, 5, 0, 0);
      client = enet_host_create(nullptr, 1, 5, 0, 0);
      REQUIRE(server);
      REQUIRE(client);

      ENetAddress target{};
      enet_address_set_host_ip(&target, "127.0.0.1");
      target.port = server->address.port;
      outgoing    = enet_host_connect(client, &target, 5, 0);
      REQUIRE(outgoing);

      const auto deadline  = std::chrono::steady_clock::now() + std::chrono::seconds(2);
      bool       connected = false;
      while ((!incoming || !connected) && std::chrono::steady_clock::now() < deadline)
      {
        ENetEvent event{};
        if (enet_host_service(server, &event, 1) > 0 && event.type == ENET_EVENT_TYPE_CONNECT) incoming = event.peer;
        if (enet_host_service(client, &event, 1) > 0 && event.type == ENET_EVENT_TYPE_CONNECT) connected = true;
      }
      REQUIRE(incoming);
      REQUIRE(connected);

      enet_host_flush(server);
      enet_host_flush(client);
    }

    ~Hosts()
    {
      if (client) enet_host_destroy(client);
      if (server) enet_host_destroy(server);
      enet_deinitialize();
    }

    void Send(std::span<const std::uint8_t> bytes, enet_uint32 flags)
    {
      auto* packet = enet_packet_create(bytes.data(), bytes.size(), flags);
      REQUIRE(packet);
      REQUIRE(enet_peer_send(outgoing, 4, packet) == 0);
      enet_host_flush(client);
    }

    std::optional<std::pair<std::vector<std::uint8_t>, enet_uint32>> Read(std::chrono::milliseconds timeout)
    {
      const auto deadline = std::chrono::steady_clock::now() + timeout;
      while (std::chrono::steady_clock::now() < deadline)
      {
        ENetEvent event{};
        if (enet_host_service(server, &event, 1) > 0 && event.type == ENET_EVENT_TYPE_RECEIVE)
        {
          auto value =
            std::pair{std::vector<std::uint8_t>(event.packet->data, event.packet->data + event.packet->dataLength), event.packet->flags};
          enet_packet_destroy(event.packet);
          return value;
        }
        if (enet_host_service(client, &event, 0) > 0 && event.type == ENET_EVENT_TYPE_RECEIVE) enet_packet_destroy(event.packet);
      }
      return {};
    }
  };

  bool dropFragment{};

  int Intercept(ENetHost* host, ENetEvent*)
  {
    if (!dropFragment || host->receivedDataLength < sizeof(ENetProtocolHeader) + sizeof(ENetProtocolCommandHeader)) return 0;
    enet_uint16 peer{};
    std::memcpy(&peer, host->receivedData, sizeof(peer));
    const auto header  = (ENET_NET_TO_HOST_16(peer) & ENET_PROTOCOL_HEADER_FLAG_SENT_TIME) ? sizeof(ENetProtocolHeader)
                                                                                           : offsetof(ENetProtocolHeader, sentTime);
    const auto command = host->receivedData[header] & ENET_PROTOCOL_COMMAND_MASK;
    if (command != ENET_PROTOCOL_COMMAND_SEND_UNRELIABLE_FRAGMENT) return 0;
    dropFragment = false;
    return 1;
  }

}

TEST_CASE("Phantom ENet fragments deliver only complete unreliable snapshots")
{
  using namespace std::chrono_literals;
  Hosts                     hosts;
  std::vector<std::uint8_t> large(220000);
  for (std::size_t i = 0; i < large.size(); ++i)
    large[i] = static_cast<std::uint8_t>(i * 31);
  hosts.Send(large, ENET_PACKET_FLAG_UNRELIABLE_FRAGMENT);
  auto received = hosts.Read(1s);
  REQUIRE(received);
  CHECK(received->first == large);
  CHECK((received->second & ENET_PACKET_FLAG_RELIABLE) == 0);

  hosts.server->intercept = Intercept;
  dropFragment            = true;
  hosts.Send(large, ENET_PACKET_FLAG_UNRELIABLE_FRAGMENT);
  CHECK_FALSE(hosts.Read(150ms));
  CHECK_FALSE(dropFragment);

  std::array<std::uint8_t, 3> next{1, 2, 3};
  hosts.Send(next, ENET_PACKET_FLAG_UNRELIABLE_FRAGMENT);
  received = hosts.Read(1s);
  REQUIRE(received);
  CHECK(received->first == std::vector<std::uint8_t>(next.begin(), next.end()));
  CHECK((received->second & ENET_PACKET_FLAG_RELIABLE) == 0);
}

TEST_CASE("Phantom ENet sequence rollover requires a reliable channel barrier")
{
  using namespace std::chrono_literals;
  Hosts hosts;
  hosts.outgoing->channels[4].outgoingUnreliableSequenceNumber = 0xFFFF;
  std::vector<std::uint8_t> large(4096, 42);
  hosts.Send(large, ENET_PACKET_FLAG_UNRELIABLE_FRAGMENT);
  auto received = hosts.Read(1s);
  REQUIRE(received);
  CHECK((received->second & ENET_PACKET_FLAG_RELIABLE) != 0);
}

TEST_CASE("Phantom transport rotates the ENet epoch without making a pose reliable")
{
  using namespace std::chrono_literals;
  Hosts hosts;
  hosts.outgoing->channels[4].outgoingUnreliableSequenceNumber = 0xFFFF;
  auto peer                                                    = DreamNetPeer::TryFromNative(hosts.outgoing);
  REQUIRE(peer);
  REQUIRE(peer->RotateUnreliableSequence(4));
  std::vector<std::uint8_t> large(4096, 77);
  hosts.Send(large, ENET_PACKET_FLAG_UNRELIABLE_FRAGMENT);
  auto barrier = hosts.Read(1s);
  REQUIRE(barrier);
  CHECK(barrier->first.empty());
  CHECK((barrier->second & ENET_PACKET_FLAG_RELIABLE) != 0);
  auto pose = hosts.Read(1s);
  REQUIRE(pose);
  CHECK(pose->first == large);
  CHECK((pose->second & ENET_PACKET_FLAG_RELIABLE) == 0);
}
