#include <doctest/doctest.h>
#include "chat.pb.h"
import std;
import Dreamsleeve.Client.Runtime;
import DreamNet.Runtime;
import DreamNet.Event;
import DreamNet.Peer;

namespace
{

  using namespace Dreamsleeve::Client;
  namespace P = Dreamsleeve::Protocol::Chat;
  using namespace std::chrono_literals;

  template <class T>
  auto Value(T result)
  {
    REQUIRE(result);
    return std::move(*result);
  }

  DreamNetHost Server()
  {
    auto config         = ServerConfig::Default();
    config.address      = DreamNetAddress::Loopback(0);
    config.channelLimit = 1;
    return Value(DreamNetHost::TryCreateServer(config));
  }

  struct Fixture
  {
    DreamNetRuntime                   enet{Value(DreamNetRuntime::TryInitialize())};
    DreamNetHost                      server{Server()};
    ClientExchange::Ptr               exchange{Value(ClientExchange::TryCreate(8, 16))};
    Configuration                     config;
    ClientRuntime::Ptr                client;
    std::optional<DreamNetPeer>       peer;
    std::vector<P::ClientPacket>      requests;
    std::vector<ClientRuntime::Error> errors;
    bool                              closed{};

    explicit Fixture(TimeOutMs sessionTimeout = 2000)
    {
      config.serverAddress       = Value(server.GetHostInfo()).address;
      config.sessionTimeoutMs    = sessionTimeout;
      config.connectTimeoutMs    = 100;
      config.disconnectTimeoutMs = 100;
      config.chatCapacity        = 1;
      client                     = Value(ClientRuntime::TryCreate(config, *exchange));
    }

    void Step()
    {
      auto polled = client->Poll(1);
      if (!polled) errors.push_back(polled.error());
      auto event = Value(server.Service(1));
      if (event)
      {
        if (event->IsConnect())
        {
          peer   = event->Peer();
          closed = false;
        }
        else if (event->IsDisconnect())
        {
          closed = true;
          peer.reset();
        }
        else if (event->IsReceive())
        {
          const auto      bytes = event->ViewPacket()->DataBytesView();
          P::ClientPacket packet;
          REQUIRE(packet.ParseFromArray(bytes.data(), static_cast<int>(bytes.size())));
          requests.push_back(std::move(packet));
        }
      }
      server.FlushPackets();
    }

    template <class Predicate>
    void Until(Predicate done)
    {
      const auto deadline = std::chrono::steady_clock::now() + 3s;
      while (!done() && std::chrono::steady_clock::now() < deadline)
        Step();
      REQUIRE(done());
    }

    std::uint64_t Open()
    {
      const auto count = requests.size();
      REQUIRE(client->Connect(" USER ", "Player"));
      CHECK_FALSE(client->Connect("second", "second"));
      Until([&] { return requests.size() > count; });
      CHECK(client->Phase() == SessionPhase::Opening);
      CHECK(requests.back().protocol_version() == 1);
      CHECK(requests.back().open_session().username() == " USER ");
      CHECK(requests.back().open_session().display_name() == "Player");
      CHECK(requests.back().request_id() != 0);
      return requests.back().request_id();
    }

    void Send(const P::ServerPacket& message)
    {
      REQUIRE(peer);
      auto packet = Value(DreamNetPacket::TryAllocateWith(message.ByteSizeLong(), [&](std::span<std::byte> bytes) {
        return message.SerializeToArray(bytes.data(), static_cast<int>(bytes.size()));
      }));
      REQUIRE(peer->PushPacket(std::move(packet), 0));
      server.FlushPackets();
    }

    ClientOutput Drain()
    {
      ClientOutput result;
      exchange->Drain(result);
      return result;
    }
  };

  P::ServerPacket Welcome(std::uint64_t requestId)
  {
    P::ServerPacket packet;
    packet.set_protocol_version(1);
    packet.set_request_id(requestId);
    auto* welcome = packet.mutable_session_opened();
    welcome->set_self_player_id(7);
    welcome->set_global_channel_id(1);
    auto* player = welcome->add_players();
    player->set_player_id(7);
    player->set_username("user");
    player->set_display_name("Player");
    for (std::uint64_t id : {1, 2})
    {
      auto* message = welcome->add_recent_messages();
      message->set_message_id(id);
      message->set_channel_id(1);
      *message->mutable_author() = *player;
      message->set_text("accepted");
    }
    return packet;
  }

  void Empty(const ClientOutput& output)
  {
    for (const auto& update : output.state.updates)
    {
      REQUIRE(std::holds_alternative<ClientSnapshot>(update));
      const auto& snapshot = std::get<ClientSnapshot>(update);
      CHECK(snapshot.players.empty());
      CHECK(snapshot.chats.empty());
      CHECK_FALSE(snapshot.selfPlayerId);
    }
  }

}

TEST_SUITE_BEGIN("Client.Runtime");

TEST_CASE("Real transport opens publishes a complete session and reconnects with a fresh request ID")
{
  Fixture    fixture;
  const auto firstId = fixture.Open();
  auto       waiting = fixture.Drain();
  CHECK(waiting.phase == SessionPhase::Opening);
  Empty(waiting);
  fixture.Send(Welcome(firstId));
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Ready; });
  auto ready = fixture.Drain();
  CHECK(ready.phase == SessionPhase::Ready);
  REQUIRE(ready.state.updates.size() == 1);
  const auto& snapshot = std::get<ClientSnapshot>(ready.state.updates[0]);
  CHECK(snapshot.selfPlayerId == 7);
  REQUIRE(snapshot.players.size() == 1);
  REQUIRE(snapshot.chats.size() == 1);
  REQUIRE(snapshot.chats[0].messages.size() == 1);
  CHECK(snapshot.chats[0].messages[0].messageId == 2);
  REQUIRE(fixture.client->Disconnect());
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Disconnected && fixture.closed; });
  Empty(fixture.Drain());
  const auto secondId = fixture.Open();
  CHECK(secondId > firstId);
  fixture.Send(Welcome(secondId));
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Ready; });
  CHECK(fixture.errors.empty());
}

TEST_CASE("Session rejection retains correlation and permits another connection")
{
  Fixture         fixture;
  const auto      id = fixture.Open();
  P::ServerPacket rejected;
  rejected.set_protocol_version(1);
  rejected.set_request_id(id);
  rejected.mutable_request_rejected()->set_code(P::REQUEST_REJECTION_CODE_USERNAME_TAKEN);
  rejected.mutable_request_rejected()->set_message("Username taken");
  fixture.Send(rejected);
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Disconnected && fixture.closed; });
  const auto output = fixture.Drain();
  Empty(output);
  REQUIRE(output.rejections.size() == 1);
  CHECK(output.rejections[0].rejection.requestId == id);
  CHECK(output.rejections[0].rejection.code == RequestRejectionCode::UsernameTaken);
  const auto next = fixture.Open();
  fixture.Send(Welcome(next));
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Ready; });
  CHECK(fixture.errors.empty());
}

TEST_CASE("Invalid session responses never publish partially initialized state")
{
  Fixture    fixture;
  const auto id     = fixture.Open();
  auto       packet = Welcome(id);
  SUBCASE("wrong correlation")
  {
    packet.set_request_id(id + 1);
  }
  SUBCASE("missing self")
  {
    packet.mutable_session_opened()->set_self_player_id(99);
  }
  SUBCASE("duplicate player")
  {
    auto duplicate                                  = packet.session_opened().players(0);
    *packet.mutable_session_opened()->add_players() = duplicate;
  }
  SUBCASE("wrong history channel")
  {
    packet.mutable_session_opened()->mutable_recent_messages(1)->set_channel_id(2);
  }
  SUBCASE("presence before welcome")
  {
    packet.clear_request_id();
    packet.mutable_player_left()->set_player_id(7);
  }
  SUBCASE("unknown version")
  {
    packet.set_protocol_version(9);
  }
  fixture.Send(packet);
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Faulted; });
  const auto output = fixture.Drain();
  CHECK(output.phase == SessionPhase::Faulted);
  Empty(output);
  REQUIRE(fixture.errors.size() == 1);
  // A failed entry does not poison the next host or application attempt.
  fixture.Until([&] { return fixture.closed; });
  const auto next = fixture.Open();
  fixture.Send(Welcome(next));
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Ready; });
}

TEST_CASE("An unanswered OpenSession times out and clears the published session")
{
  Fixture fixture{40};
  fixture.Open();
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Faulted; });
  REQUIRE(fixture.errors.size() == 1);
  REQUIRE(std::holds_alternative<DreamNetError>(fixture.errors[0]));
  CHECK(std::get<DreamNetError>(fixture.errors[0]).code == DreamNetErrorCode::ConnectTimeout);
  Empty(fixture.Drain());
}

TEST_CASE("Remote disconnect clears ready state and closing ignores late session packets")
{
  Fixture fixture;
  auto    id = fixture.Open();
  fixture.Send(Welcome(id));
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Ready; });
  fixture.Drain();
  fixture.peer->Disconnect(DisconnectType::Normal, DisconnectReason::ServerShutdown);
  fixture.server.FlushPackets();
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Disconnected; });
  Empty(fixture.Drain());
  id = fixture.Open();
  REQUIRE(fixture.client->Disconnect());
  fixture.Send(Welcome(id));
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Disconnected; });
  Empty(fixture.Drain());
  CHECK(fixture.errors.empty());
}

TEST_CASE("Invalid runtime settings fail before any connection")
{
  auto          exchange = Value(ClientExchange::TryCreate(1, 1));
  Configuration config;
  SUBCASE("capacity")
  {
    config.chatCapacity = 0;
  }
  SUBCASE("session timeout")
  {
    config.sessionTimeoutMs = 0;
  }
  SUBCASE("packet budget")
  {
    config.network.maxPacketBytes = 0;
  }
  CHECK_FALSE(ClientRuntime::TryCreate(config, *exchange));
}

TEST_CASE("Transport connection can be cancelled or time out without opening a session")
{
  Fixture fixture;
  REQUIRE(fixture.client->Connect("user", "Player"));
  SUBCASE("cancel")
  {
    REQUIRE(fixture.client->Disconnect());
    CHECK(fixture.client->Phase() == SessionPhase::Disconnected);
    CHECK(fixture.requests.empty());
  }
  SUBCASE("transport timeout")
  {
    const auto deadline = std::chrono::steady_clock::now() + 2s;
    while (fixture.client->Phase() != SessionPhase::Faulted && std::chrono::steady_clock::now() < deadline)
    {
      auto result = fixture.client->Poll(5);  // Bound server socket deliberately receives no Service calls.
      if (!result) fixture.errors.push_back(result.error());
    }
    REQUIRE(fixture.client->Phase() == SessionPhase::Faulted);
    REQUIRE(fixture.errors.size() == 1);
    CHECK(std::get<DreamNetError>(fixture.errors[0]).code == DreamNetErrorCode::ConnectTimeout);
  }
  Empty(fixture.Drain());
}

TEST_CASE("A duplicate welcome cannot reset a ready session")
{
  Fixture    fixture;
  const auto id = fixture.Open();
  fixture.Send(Welcome(id));
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Ready; });
  fixture.Drain();
  fixture.Send(Welcome(id));
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Faulted; });
  Empty(fixture.Drain());
  REQUIRE(fixture.errors.size() == 1);
  CHECK(std::get<Wire::Error>(fixture.errors[0]).field == "request_id");
}

TEST_SUITE_END();
