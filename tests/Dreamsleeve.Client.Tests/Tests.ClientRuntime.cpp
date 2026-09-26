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
    ClientExchange::Ptr               exchange;
    Configuration                     config;
    ClientRuntime::Ptr                client;
    std::optional<DreamNetPeer>       peer;
    std::vector<P::ClientPacket>      requests;
    std::vector<ClientRuntime::Error> errors;
    bool                              closed{};

    explicit Fixture(TimeOutMs sessionTimeout = 2000, std::size_t maxPending = 2, std::size_t packetBytes = 1024 * 1024, std::size_t resultCapacity = 8)
        : exchange{Value(ClientExchange::TryCreate(resultCapacity, 16))}
    {
      config.serverAddress       = Value(server.GetHostInfo()).address;
      config.sessionTimeoutMs    = sessionTimeout;
      config.connectTimeoutMs    = 100;
      config.disconnectTimeoutMs = 100;
      config.chatCapacity        = 1;
      config.maxPendingChatRequests = maxPending;
      config.network.maxPacketBytes = packetBytes;
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

    ClientOutput ReceiveOutput()
    {
      ClientOutput result;
      Until([&] {
        if (!result.state.updates.empty() || !result.rejections.empty() || !result.commandFailures.empty()) return true;
        exchange->Drain(result);
        return !result.state.updates.empty() || !result.rejections.empty() || !result.commandFailures.empty();
      });
      return result;
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

  std::uint64_t Ready(Fixture& fixture)
  {
    fixture.Send(Welcome(fixture.Open()));
    fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Ready; });
    const auto output = fixture.Drain();
    REQUIRE(output.state.updates.size() == 1);
    return std::get<ClientSnapshot>(output.state.updates.front()).generation;
  }

  std::uint64_t Queue(Fixture& fixture, std::uint64_t generation, std::string text = "outbound")
  {
    const auto id = Value(fixture.exchange->NextRequestId());
    REQUIRE(fixture.exchange->Post({generation, SendChat{id, 1, std::move(text)}}) == CommandPostResult::Queued);
    return id;
  }

  P::ServerPacket Publication(std::uint64_t requestId, std::uint64_t messageId, std::uint64_t author = 7)
  {
    P::ServerPacket packet;
    packet.set_protocol_version(1);
    if (requestId != 0) packet.set_request_id(requestId);
    auto* message = packet.mutable_chat_published()->mutable_message();
    message->set_message_id(messageId);
    message->set_channel_id(1);
    message->mutable_author()->set_player_id(author);
    message->mutable_author()->set_username("canonical");
    message->mutable_author()->set_display_name("Server Author");
    message->set_text("accepted by server");
    message->set_sent_at_unix_ms(123);
    return packet;
  }

  P::ServerPacket Rejection(std::uint64_t requestId)
  {
    P::ServerPacket packet;
    packet.set_protocol_version(1);
    packet.set_request_id(requestId);
    packet.mutable_request_rejected()->set_code(P::REQUEST_REJECTION_CODE_OVERLOADED);
    packet.mutable_request_rejected()->set_message("Busy");
    return packet;
  }

  std::vector<Domain::ChatMessage> Added(const ClientOutput& output)
  {
    std::vector<Domain::ChatMessage> messages;
    for (const auto& update : output.state.updates)
    {
      REQUIRE(std::holds_alternative<ClientStateDelta>(update));
      for (const auto& change : std::get<ClientStateDelta>(update).chatContent)
        if (const auto* added = std::get_if<ChatMessagesAdded>(&change))
          messages.insert(messages.end(), added->messages.begin(), added->messages.end());
    }
    return messages;
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

TEST_CASE("Terminal opening rejection does not start a competing graceful disconnect")
{
  Fixture fixture;
  const auto id = fixture.Open();
  P::ServerPacket rejected;
  rejected.set_protocol_version(1);
  rejected.set_request_id(id);
  rejected.mutable_request_rejected()->set_code(P::REQUEST_REJECTION_CODE_SESSION_ALREADY_OPEN);
  rejected.mutable_request_rejected()->set_message("Player already has a session");
  fixture.Send(rejected);
  fixture.peer->Disconnect(DisconnectType::Later, DisconnectReason::ServerShutdown);
  fixture.server.FlushPackets();

  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Disconnected; });
  const auto output = fixture.Drain();
  Empty(output);
  REQUIRE(output.rejections.size() == 1);
  CHECK(output.rejections.front().rejection.requestId == id);
  CHECK(output.rejections.front().rejection.code == RequestRejectionCode::SessionAlreadyOpen);
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
  SUBCASE("pending chat capacity")
  {
    config.maxPendingChatRequests = 0;
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

TEST_CASE("SendChat has no local echo and own publications use the broadcast delta path")
{
  Fixture fixture;
  const auto generation = Ready(fixture);
  const auto requestId = Queue(fixture, generation, "local text");
  fixture.Until([&] { return fixture.requests.size() == 2; });

  const auto& request = fixture.requests.back();
  CHECK(request.request_id() == requestId);
  CHECK(request.send_chat().channel_id() == 1);
  CHECK(request.send_chat().text() == "local text");
  CHECK(fixture.Drain().state.updates.empty());

  fixture.Send(Publication(requestId, 3));
  const auto own = Added(fixture.ReceiveOutput());
  REQUIRE(own.size() == 1);
  CHECK(own.front().messageId == 3);
  CHECK(own.front().messageText == "accepted by server");
  CHECK(own.front().author.displayName == "Server Author");

  fixture.Send(Publication(0, 4, 8));
  const auto other = Added(fixture.ReceiveOutput());
  REQUIRE(other.size() == 1);
  CHECK(other.front().messageId == 4);
  CHECK(other.front().author.playerId == 8);
  CHECK(fixture.client->Phase() == SessionPhase::Ready);
  CHECK(fixture.errors.empty());
}

TEST_CASE("Pending chat is bounded and a correlated server rejection frees only that request")
{
  Fixture fixture;
  const auto generation = Ready(fixture);
  const auto first = Queue(fixture, generation);
  const auto second = Queue(fixture, generation);
  const auto excess = Queue(fixture, generation);
  fixture.Until([&] { return fixture.requests.size() == 3; });

  const auto busy = fixture.Drain();
  REQUIRE(busy.commandFailures.size() == 1);
  CHECK(busy.commandFailures.front().requestId == excess);
  CHECK(busy.commandFailures.front().code == CommandFailureCode::Busy);
  CHECK(busy.rejections.empty());
  CHECK(busy.state.updates.empty());

  P::ServerPacket rejected;
  rejected.set_protocol_version(1);
  rejected.set_request_id(second);
  rejected.mutable_request_rejected()->set_code(P::REQUEST_REJECTION_CODE_OVERLOADED);
  rejected.mutable_request_rejected()->set_message("Channel busy");
  fixture.Send(rejected);
  const auto output = fixture.ReceiveOutput();
  REQUIRE(output.rejections.size() == 1);
  CHECK(output.rejections.front().rejection.requestId == second);
  CHECK(output.rejections.front().rejection.code == RequestRejectionCode::Overloaded);
  CHECK(output.phase == SessionPhase::Ready);
  CHECK(output.state.updates.empty());

  const auto next = Queue(fixture, generation);
  fixture.Until([&] { return fixture.requests.size() == 4; });
  fixture.Send(Publication(first, 3));
  REQUIRE(Added(fixture.ReceiveOutput()).size() == 1);
  fixture.Send(Publication(next, 4));
  REQUIRE(Added(fixture.ReceiveOutput()).size() == 1);
  CHECK(fixture.errors.empty());
}

TEST_CASE("Old queued commands and correlations cannot enter a replacement session")
{
  Fixture fixture{2000, 1};
  const auto oldGeneration = Ready(fixture);
  const auto oldRequest = Queue(fixture, oldGeneration);
  fixture.Until([&] { return fixture.requests.size() == 2; });
  REQUIRE(fixture.client->Disconnect());
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Disconnected && fixture.closed; });
  fixture.Drain();

  const auto generation = Ready(fixture);
  CHECK(generation != oldGeneration);
  const auto stale = Queue(fixture, oldGeneration);
  const auto current = Queue(fixture, generation);
  CHECK(current > oldRequest);
  fixture.Until([&] { return fixture.requests.size() == 4; });
  CHECK(fixture.requests.back().request_id() == current);

  const auto output = fixture.Drain();
  REQUIRE(output.commandFailures.size() == 1);
  CHECK(output.commandFailures.front().requestId == stale);
  CHECK(output.commandFailures.front().generation == oldGeneration);
  CHECK(output.commandFailures.front().code == CommandFailureCode::StaleGeneration);

  SUBCASE("new request works after old pending state was cleared")
  {
    fixture.Send(Publication(current, 3));
    REQUIRE(Added(fixture.ReceiveOutput()).size() == 1);
    CHECK(fixture.errors.empty());
  }
  SUBCASE("old reply on new connection is a protocol fault")
  {
    fixture.Send(Publication(oldRequest, 3));
    fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Faulted; });
    Empty(fixture.Drain());
    REQUIRE(fixture.errors.size() == 1);
    CHECK(std::get<Wire::Error>(fixture.errors.front()).field == "request_id");
  }
}

TEST_CASE("Chat commands while opening are refused locally without reaching the server")
{
  Fixture fixture;
  const auto opening = fixture.Open();
  const auto initial = fixture.Drain();
  const auto generation = std::get<ClientSnapshot>(initial.state.updates.front()).generation;
  const auto requestId = Queue(fixture, generation);
  const auto output = fixture.ReceiveOutput();
  REQUIRE(output.commandFailures.size() == 1);
  CHECK(output.commandFailures.front().requestId == requestId);
  CHECK(output.commandFailures.front().code == CommandFailureCode::SessionNotReady);
  CHECK(fixture.requests.size() == 1);

  fixture.Send(Welcome(opening));
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Ready; });
  CHECK(fixture.errors.empty());
}

TEST_CASE("Reusing a request ID cannot replace an outstanding chat command")
{
  Fixture fixture;
  const auto generation = Ready(fixture);
  const auto requestId = Queue(fixture, generation);
  REQUIRE(fixture.exchange->Post({generation, SendChat{requestId, 1, "duplicate"}}) == CommandPostResult::Queued);
  fixture.Until([&] { return fixture.requests.size() == 2; });
  const auto output = fixture.Drain();
  REQUIRE(output.commandFailures.size() == 1);
  CHECK(output.commandFailures.front().code == CommandFailureCode::InvalidRequest);
  CHECK(output.state.updates.empty());

  fixture.Send(Publication(requestId, 3));
  REQUIRE(Added(fixture.ReceiveOutput()).size() == 1);
  CHECK(fixture.errors.empty());
}

TEST_CASE("Wrong chat response identity fails before publication")
{
  Fixture fixture;
  const auto generation = Ready(fixture);
  const auto requestId = Queue(fixture, generation);
  fixture.Until([&] { return fixture.requests.size() == 2; });
  auto packet = Publication(requestId, 3);
  SUBCASE("unknown request") { packet.set_request_id(requestId + 99); }
  SUBCASE("different channel") { packet.mutable_chat_published()->mutable_message()->set_channel_id(2); }
  SUBCASE("different author") { packet.mutable_chat_published()->mutable_message()->mutable_author()->set_player_id(8); }
  fixture.Send(packet);
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Faulted; });
  Empty(fixture.Drain());
  REQUIRE(fixture.errors.size() == 1);
}

TEST_CASE("Oversized outgoing chat stays local and does not occupy the pending slot")
{
  Fixture fixture{2000, 1, 256};
  const auto generation = Ready(fixture);
  const auto tooLarge = Queue(fixture, generation, std::string(512, 'x'));
  const auto output = fixture.ReceiveOutput();
  REQUIRE(output.commandFailures.size() == 1);
  CHECK(output.commandFailures.front().requestId == tooLarge);
  CHECK(output.commandFailures.front().code == CommandFailureCode::EncodingFailed);
  CHECK(fixture.requests.size() == 1);
  CHECK(fixture.client->Phase() == SessionPhase::Ready);

  const auto valid = Queue(fixture, generation);
  fixture.Until([&] { return fixture.requests.size() == 2; });
  fixture.Send(Publication(valid, 3));
  REQUIRE(Added(fixture.ReceiveOutput()).size() == 1);
  CHECK(fixture.errors.empty());
}

TEST_CASE("Undrained server rejections stop outgoing commands while network close still progresses")
{
  Fixture fixture{2000, 2, 1024 * 1024, 2};
  const auto generation = Ready(fixture);
  const auto first = Queue(fixture, generation);
  const auto second = Queue(fixture, generation);
  fixture.Until([&] { return fixture.requests.size() == 3; });

  const auto waiting = Queue(fixture, generation);
  const auto otherWaiting = Queue(fixture, generation);
  REQUIRE(fixture.client->Poll());
  fixture.Send(Rejection(first));
  fixture.Send(Rejection(second));
  fixture.peer->Disconnect(DisconnectType::Later, DisconnectReason::ServerShutdown);
  fixture.server.FlushPackets();
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Disconnected && fixture.closed; });

  CHECK(fixture.requests.size() == 3);
  CHECK_FALSE(fixture.client->Connect("user", "Player"));
  CHECK(fixture.client->Phase() == SessionPhase::Disconnected);
  const auto rejected = fixture.Drain();
  REQUIRE(rejected.rejections.size() == 2);
  CHECK(rejected.rejections[0].rejection.requestId == first);
  CHECK(rejected.rejections[1].rejection.requestId == second);
  CHECK(rejected.commandFailures.empty());

  REQUIRE(fixture.client->Poll());
  const auto stale = fixture.Drain();
  REQUIRE(stale.commandFailures.size() == 2);
  CHECK(stale.commandFailures[0].requestId == waiting);
  CHECK(stale.commandFailures[1].requestId == otherWaiting);
  CHECK(stale.commandFailures[0].code == CommandFailureCode::StaleGeneration);

  const auto fresh = Queue(fixture, Ready(fixture));
  fixture.Until([&] { return fixture.requests.size() == 5; });
  fixture.Send(Publication(fresh, 3));
  REQUIRE(Added(fixture.ReceiveOutput()).size() == 1);
  CHECK(fixture.errors.empty());
}

TEST_CASE("Opening reserves its terminal rejection slot before queued commands or reconnect")
{
  Fixture fixture{2000, 1, 1024 * 1024, 1};
  REQUIRE(fixture.client->Connect("user", "Player"));
  const auto queued = Queue(fixture, 0);
  fixture.Until([&] { return fixture.requests.size() == 1; });
  const auto opening = fixture.requests.front().request_id();
  fixture.Send(Rejection(opening));
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Disconnected && fixture.closed; });
  REQUIRE(fixture.client->Poll());

  CHECK_FALSE(fixture.client->Connect("user", "Player"));
  const auto rejected = fixture.Drain();
  REQUIRE(rejected.rejections.size() == 1);
  CHECK(rejected.rejections.front().rejection.requestId == opening);
  CHECK(rejected.commandFailures.empty());

  REQUIRE(fixture.client->Poll());
  const auto stale = fixture.Drain();
  REQUIRE(stale.commandFailures.size() == 1);
  CHECK(stale.commandFailures.front().requestId == queued);
  CHECK(stale.commandFailures.front().code == CommandFailureCode::StaleGeneration);
  REQUIRE(fixture.client->Connect("user", "Player"));
  CHECK(fixture.client->Phase() == SessionPhase::Connecting);
  CHECK(fixture.errors.empty());
}

TEST_SUITE_END();
