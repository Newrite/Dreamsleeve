#include <doctest/doctest.h>
#include "protocol.pb.h"
import std;
import Dreamsleeve.Client.Runtime;
import DreamNet.Runtime;
import DreamNet.Event;
import DreamNet.Peer;

#include "Results.h"

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
    config.channelLimit = 3;
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
    std::vector<P::ClientMovementPacket> samples;
    std::vector<ClientRuntime::Error> errors;
    bool                              closed{};
    int                               connects{};

    explicit Fixture(TimeOutMs sessionTimeout = 2000, std::size_t maxPending = 2, std::size_t packetBytes = 1024 * 1024, std::size_t resultCapacity = 8)
        : exchange{Value(ClientExchange::TryCreate(resultCapacity, 16))}
    {
      config.serverPort          = Value(server.GetHostInfo()).address.GetPort();
      config.sessionTimeoutMs    = sessionTimeout;
      config.connectTimeoutMs    = 100;
      config.disconnectTimeoutMs = 100;
      config.chatCapacity        = 1;
      config.maxPendingChatRequests = maxPending;
      config.maxPendingPlayerUpdates = maxPending;
      config.network.maxPacketBytes = packetBytes;
      client                     = ClientRuntime::Create(config, *exchange);
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
          ++connects;
        }
        else if (event->IsDisconnect())
        {
          closed = true;
          peer.reset();
        }
        else if (event->IsReceive())
        {
          const auto      bytes = event->ViewPacket()->DataBytesView();
          if (event->TryChannelId() == 2)
          {
            CHECK_FALSE(PacketFlags::HasFlag(event->ViewPacket()->Flags(), PacketFlag::Reliable));
            P::ClientMovementPacket packet;
            REQUIRE(packet.ParseFromArray(bytes.data(), static_cast<int>(bytes.size())));
            samples.push_back(std::move(packet));
          }
          else
          {
            P::ClientPacket packet;
            REQUIRE(packet.ParseFromArray(bytes.data(), static_cast<int>(bytes.size())));
            CHECK(
              event->TryChannelId() ==
              (packet.has_send_chat() || packet.has_post_announcement() || packet.has_delete_chat_message() ? 1 : 0));
            requests.push_back(std::move(packet));
          }
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

    std::uint64_t Open(char ticketCharacter = 'A')
    {
      const auto count = requests.size();
      REQUIRE(client->Connect(std::string(43, ticketCharacter)));
      CHECK_FALSE(client->Connect(std::string(43, 'A')));
      Until([&] { return requests.size() > count; });
      CHECK(client->Phase() == SessionPhase::Opening);
      CHECK(requests.back().protocol_version() == Wire::Version);
      CHECK(requests.back().open_session().session_ticket() == std::string(43, ticketCharacter));
      CHECK(requests.back().request_id() != 0);
      return requests.back().request_id();
    }

    void Send(const P::ServerPacket& message)
    {
      REQUIRE(peer);
      auto packet = Value(DreamNetPacket::TryAllocateWith(message.ByteSizeLong(), [&](std::span<std::byte> bytes) {
        return message.SerializeToArray(bytes.data(), static_cast<int>(bytes.size()));
      }));
      // A guild channel's chat comes on the control lane, like the server sends it.
      const auto chat =
        message.has_chat_published() ? message.chat_published().message().channel_id() : message.chat_message_removed().channel_id();
      auto channel = (message.has_chat_published() || message.has_chat_message_removed()) && !Domain::IsGuildChannel(chat) ? 1 : 0;
      if (message.has_request_rejected())
      {
        const auto request = std::ranges::find(requests, message.request_id(), &P::ClientPacket::request_id);
        if (
          request != requests.end() && (request->has_send_chat() || request->has_post_announcement() || request->has_delete_chat_message()))
          channel = 1;
      }
      REQUIRE(peer->PushPacket(std::move(packet), static_cast<ChannelId>(channel)));
      server.FlushPackets();
    }

    void Send(const P::ServerMovementPacket& message)
    {
      auto packet = Value(DreamNetPacket::TryAllocateWith(message.ByteSizeLong(), [&](std::span<std::byte> bytes) {
        return message.SerializeToArray(bytes.data(), static_cast<int>(bytes.size()));
      }, PacketFlag::None));
      REQUIRE(peer->PushPacket(std::move(packet), 2));
      server.FlushPackets();
    }

    ClientOutput ReceiveOutput()
    {
      ClientOutput result;
      Until([&] {
        if (!result.state.updates.empty() || !ResultsOf<ServerRejection>(result).empty() || !ResultsOf<CommandFailureCode>(result).empty()) return true;
        exchange->Drain(result);
        return !result.state.updates.empty() || !ResultsOf<ServerRejection>(result).empty() || !ResultsOf<CommandFailureCode>(result).empty();
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
    packet.set_protocol_version(Wire::Version);
    packet.set_request_id(requestId);
    auto* welcome = packet.mutable_session_opened();
    welcome->set_self_player_id(7);
    welcome->set_server_name("Tamriel Test Server");
    auto* global = welcome->add_channels();
    global->set_channel_id(1);
    global->set_kind(P::CHAT_CHANNEL_KIND_GLOBAL);
    auto* system = welcome->add_channels();
    system->set_channel_id(2);
    system->set_kind(P::CHAT_CHANNEL_KIND_SYSTEM);
    auto* policy = welcome->mutable_announcements();
    policy->add_allowed_sources(P::CLIENT_ANNOUNCEMENT_SOURCE_THIRD_PARTY);
    policy->set_max_text_length(10);
    policy->set_max_signature_length(8);
    auto* player = welcome->add_players()->mutable_profile();
    player->set_player_id(7);
    player->set_username("user");
    player->set_display_name("Player");
    for (std::uint64_t id : {1, 2})
    {
      auto* message = global->add_recent_messages();
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
    packet.set_protocol_version(Wire::Version);
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
    packet.set_protocol_version(Wire::Version);
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
  CHECK(waiting.status.phase == SessionPhase::Opening);
  Empty(waiting);
  fixture.Send(Welcome(firstId));
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Ready; });
  auto ready = fixture.Drain();
  CHECK(ready.status.phase == SessionPhase::Ready);
  CHECK(ready.status.serverName == "Tamriel Test Server");
  REQUIRE(ready.state.updates.size() == 1);
  const auto& snapshot = std::get<ClientSnapshot>(ready.state.updates[0]);
  CHECK(snapshot.selfPlayerId == 7);
  REQUIRE(snapshot.players.size() == 1);
  REQUIRE(snapshot.chats.size() == 2);
  const auto global = std::ranges::find(snapshot.chats, Domain::ChatChannelKind::Global, &ChatCacheSnapshot::kind);
  REQUIRE(global != snapshot.chats.end());
  REQUIRE(global->messages.size() == 1);
  CHECK(global->messages[0].messageId == 2);
  REQUIRE(fixture.client->Disconnect());
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Disconnected && fixture.closed; });
  Empty(fixture.Drain());
  const auto secondId = fixture.Open('B');
  CHECK(secondId > firstId);
  fixture.Send(Welcome(secondId));
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Ready; });
  CHECK(fixture.errors.empty());
}

TEST_CASE("A DNS name as the server host is resolved when the connection is made")
{
  Fixture fixture;
  fixture.config.serverHost = "localhost";
  fixture.client            = ClientRuntime::Create(fixture.config, *fixture.exchange);
  const auto requestId      = fixture.Open();
  fixture.Send(Welcome(requestId));
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Ready; });
  CHECK(fixture.errors.empty());
}

TEST_CASE("Session rejection retains correlation and permits another connection")
{
  Fixture         fixture;
  const auto      id = fixture.Open();
  P::ServerPacket rejected;
  rejected.set_protocol_version(Wire::Version);
  rejected.set_request_id(id);
  rejected.mutable_request_rejected()->set_code(P::REQUEST_REJECTION_CODE_USERNAME_TAKEN);
  rejected.mutable_request_rejected()->set_message("Username taken");
  fixture.Send(rejected);
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Disconnected && fixture.closed; });
  const auto output = fixture.Drain();
  Empty(output);
  REQUIRE(ResultsOf<ServerRejection>(output).size() == 1);
  CHECK(ResultsOf<ServerRejection>(output)[0].requestId == id);
  CHECK(ResultsOf<ServerRejection>(output)[0].value.code == RequestRejectionCode::UsernameTaken);
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
  rejected.set_protocol_version(Wire::Version);
  rejected.set_request_id(id);
  rejected.mutable_request_rejected()->set_code(P::REQUEST_REJECTION_CODE_SESSION_ALREADY_OPEN);
  rejected.mutable_request_rejected()->set_message("Player already has a session");
  fixture.Send(rejected);
  fixture.peer->Disconnect(DisconnectType::Later, DisconnectReason::ServerShutdown);
  fixture.server.FlushPackets();

  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Disconnected; });
  const auto output = fixture.Drain();
  Empty(output);
  REQUIRE(ResultsOf<ServerRejection>(output).size() == 1);
  CHECK(ResultsOf<ServerRejection>(output).front().requestId == id);
  CHECK(ResultsOf<ServerRejection>(output).front().value.code == RequestRejectionCode::SessionAlreadyOpen);
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
    packet.mutable_session_opened()->mutable_channels(0)->mutable_recent_messages(1)->set_channel_id(2);
  }
  SUBCASE("chat in the system channel")
  {
    auto* message = packet.mutable_session_opened()->mutable_channels(1)->add_recent_messages();
    *message      = packet.session_opened().channels(0).recent_messages(0);
    message->set_channel_id(2);
  }
  SUBCASE("presence before welcome")
  {
    packet.clear_request_id();
    packet.mutable_presence_changed()->add_left(7);
  }
  SUBCASE("unknown version")
  {
    packet.set_protocol_version(Wire::Version + 1);
  }
  fixture.Send(packet);
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Faulted; });
  const auto output = fixture.Drain();
  CHECK(output.status.phase == SessionPhase::Faulted);
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

TEST_CASE("Transport connection can be cancelled or time out without opening a session")
{
  Fixture fixture;
  REQUIRE(fixture.client->Connect(std::string(43, 'A')));
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
  const auto confirmed = fixture.ReceiveOutput();
  REQUIRE(ResultsOf<MessagePublished>(confirmed).size() == 1);
  CHECK(ResultsOf<MessagePublished>(confirmed)[0].requestId == requestId);
  CHECK(ResultsOf<MessagePublished>(confirmed)[0].value.messageId == 3);
  CHECK(ResultsOf<MessagePublished>(confirmed)[0].generation == generation);
  CHECK(ResultsOf<MessagePublished>(fixture.Drain()).empty());
  const auto own = Added(confirmed);
  REQUIRE(own.size() == 1);
  CHECK(own.front().messageId == 3);
  CHECK(own.front().messageText == "accepted by server");
  CHECK(own.front().author->displayName == "Server Author");

  fixture.Send(Publication(0, 4, 8));
  const auto other = Added(fixture.ReceiveOutput());
  REQUIRE(other.size() == 1);
  CHECK(other.front().messageId == 4);
  CHECK(other.front().author->playerId == 8);
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
  REQUIRE(ResultsOf<CommandFailureCode>(busy).size() == 1);
  CHECK(ResultsOf<CommandFailureCode>(busy).front().requestId == excess);
  CHECK(ResultsOf<CommandFailureCode>(busy).front().value == CommandFailureCode::Busy);
  CHECK(ResultsOf<ServerRejection>(busy).empty());
  CHECK(busy.state.updates.empty());

  P::ServerPacket rejected;
  rejected.set_protocol_version(Wire::Version);
  rejected.set_request_id(second);
  rejected.mutable_request_rejected()->set_code(P::REQUEST_REJECTION_CODE_OVERLOADED);
  rejected.mutable_request_rejected()->set_message("Channel busy");
  fixture.Send(rejected);
  const auto output = fixture.ReceiveOutput();
  REQUIRE(ResultsOf<ServerRejection>(output).size() == 1);
  CHECK(ResultsOf<ServerRejection>(output).front().requestId == second);
  CHECK(ResultsOf<ServerRejection>(output).front().value.code == RequestRejectionCode::Overloaded);
  CHECK(output.status.phase == SessionPhase::Ready);
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
  REQUIRE(ResultsOf<CommandFailureCode>(output).size() == 1);
  CHECK(ResultsOf<CommandFailureCode>(output).front().requestId == stale);
  CHECK(ResultsOf<CommandFailureCode>(output).front().generation == oldGeneration);
  CHECK(ResultsOf<CommandFailureCode>(output).front().value == CommandFailureCode::StaleGeneration);

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
  REQUIRE(ResultsOf<CommandFailureCode>(output).size() == 1);
  CHECK(ResultsOf<CommandFailureCode>(output).front().requestId == requestId);
  CHECK(ResultsOf<CommandFailureCode>(output).front().value == CommandFailureCode::SessionNotReady);
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
  REQUIRE(ResultsOf<CommandFailureCode>(output).size() == 1);
  CHECK(ResultsOf<CommandFailureCode>(output).front().value == CommandFailureCode::InvalidRequest);
  CHECK(output.state.updates.empty());

  fixture.Send(Publication(requestId, 3));
  REQUIRE(Added(fixture.ReceiveOutput()).size() == 1);
  CHECK(fixture.errors.empty());
}

TEST_CASE("Chat goes only to a known global channel and a refusal stays local")
{
  Fixture               fixture;
  const auto            generation = Ready(fixture);
  Domain::ChatChannelId channel{};
  SUBCASE("system channel") { channel = 2; }
  SUBCASE("unknown channel") { channel = 99; }
  const auto id = Value(fixture.exchange->NextRequestId());
  REQUIRE(fixture.exchange->Post({generation, SendChat{id, channel, "hello"}}) == CommandPostResult::Queued);
  const auto output = fixture.ReceiveOutput();
  REQUIRE(ResultsOf<CommandFailureCode>(output).size() == 1);
  CHECK(ResultsOf<CommandFailureCode>(output).front().requestId == id);
  CHECK(ResultsOf<CommandFailureCode>(output).front().value == CommandFailureCode::InvalidRequest);
  CHECK(fixture.requests.size() == 1);
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
  REQUIRE(ResultsOf<CommandFailureCode>(output).size() == 1);
  CHECK(ResultsOf<CommandFailureCode>(output).front().requestId == tooLarge);
  CHECK(ResultsOf<CommandFailureCode>(output).front().value == CommandFailureCode::EncodingFailed);
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
  CHECK_FALSE(fixture.client->Connect(std::string(43, 'A')));
  CHECK(fixture.client->Phase() == SessionPhase::Disconnected);
  const auto rejected = fixture.Drain();
  REQUIRE(ResultsOf<ServerRejection>(rejected).size() == 2);
  CHECK(ResultsOf<ServerRejection>(rejected)[0].requestId == first);
  CHECK(ResultsOf<ServerRejection>(rejected)[1].requestId == second);
  CHECK(ResultsOf<CommandFailureCode>(rejected).empty());

  REQUIRE(fixture.client->Poll());
  const auto stale = fixture.Drain();
  REQUIRE(ResultsOf<CommandFailureCode>(stale).size() == 2);
  CHECK(ResultsOf<CommandFailureCode>(stale)[0].requestId == waiting);
  CHECK(ResultsOf<CommandFailureCode>(stale)[1].requestId == otherWaiting);
  CHECK(ResultsOf<CommandFailureCode>(stale)[0].value == CommandFailureCode::StaleGeneration);

  const auto fresh = Queue(fixture, Ready(fixture));
  fixture.Until([&] { return fixture.requests.size() == 5; });
  fixture.Send(Publication(fresh, 3));
  REQUIRE(Added(fixture.ReceiveOutput()).size() == 1);
  CHECK(fixture.errors.empty());
}

TEST_CASE("Opening reserves its terminal rejection slot before queued commands or reconnect")
{
  Fixture fixture{2000, 1, 1024 * 1024, 1};
  REQUIRE(fixture.client->Connect(std::string(43, 'A')));
  const auto queued = Queue(fixture, 0);
  fixture.Until([&] { return fixture.requests.size() == 1; });
  const auto opening = fixture.requests.front().request_id();
  fixture.Send(Rejection(opening));
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Disconnected && fixture.closed; });
  REQUIRE(fixture.client->Poll());

  CHECK_FALSE(fixture.client->Connect(std::string(43, 'A')));
  const auto rejected = fixture.Drain();
  REQUIRE(ResultsOf<ServerRejection>(rejected).size() == 1);
  CHECK(ResultsOf<ServerRejection>(rejected).front().requestId == opening);
  CHECK(ResultsOf<CommandFailureCode>(rejected).empty());

  REQUIRE(fixture.client->Poll());
  const auto stale = fixture.Drain();
  REQUIRE(ResultsOf<CommandFailureCode>(stale).size() == 1);
  CHECK(ResultsOf<CommandFailureCode>(stale).front().requestId == queued);
  CHECK(ResultsOf<CommandFailureCode>(stale).front().value == CommandFailureCode::StaleGeneration);
  REQUIRE(fixture.client->Connect(std::string(43, 'A')));
  CHECK(fixture.client->Phase() == SessionPhase::Connecting);
  CHECK(fixture.errors.empty());
}


TEST_CASE("Player commands and acknowledgements wait for authoritative replication including the author")
{
  Fixture fixture;
  const auto generation = Ready(fixture);
  // The UI allocated this ID before the networking owner allocates the begin ID.
  const auto chatId = Value(fixture.exchange->NextRequestId());
  REQUIRE(fixture.exchange->Post({generation, CharacterStarted{"Same save name"}}) == CommandPostResult::Queued);
  REQUIRE(fixture.exchange->Post({generation, SendChat{chatId, 1, "mixed batch"}}) == CommandPostResult::Queued);
  fixture.Until([&] { return fixture.requests.size() == 3; });
  CHECK(fixture.requests[1].update_player().begin_character().name() == "Same save name");
  CHECK(fixture.requests[1].request_id() > chatId);
  CHECK(fixture.requests[2].request_id() == chatId);
  CHECK(fixture.Drain().state.updates.empty());

  P::ServerPacket ack;
  ack.set_protocol_version(Wire::Version);
  ack.set_request_id(fixture.requests[1].request_id());
  ack.mutable_player_update_accepted();
  fixture.Send(ack);
  fixture.Send(Publication(chatId, 3));
  const auto chat = fixture.ReceiveOutput();
  REQUIRE(Added(chat).size() == 1);
  for (const auto& update : chat.state.updates)
    CHECK(std::get<ClientStateDelta>(update).players.empty());

  P::ServerPacket replication;
  replication.set_protocol_version(Wire::Version);
  auto* player = replication.mutable_presence_changed()->add_updated();
  player->mutable_profile()->set_player_id(7);
  player->set_character_name("Server canonical name");
  player->set_character_generation(2);
  fixture.Send(replication);
  const auto output = fixture.ReceiveOutput();
  REQUIRE(output.state.updates.size() == 1);
  const auto& accepted = std::get<ClientStateDelta>(output.state.updates[0]).players;
  REQUIRE(accepted.size() == 1);
  CHECK(accepted[0].characterName == "Server canonical name");
  CHECK(accepted[0].characterGeneration == 2);
  CHECK(fixture.errors.empty());
}

TEST_CASE("Player rejection settles only its bounded request and keeps the session usable")
{
  Fixture fixture{2000, 1};
  const auto generation = Ready(fixture);
  REQUIRE(fixture.exchange->Post({generation, CharacterStarted{"Name"}}) == CommandPostResult::Queued);
  REQUIRE(fixture.exchange->Post({generation, CharacterRenamed{"Later"}}) == CommandPostResult::Queued);
  fixture.Until([&] { return fixture.requests.size() == 2; });
  const auto failed = fixture.Drain();
  REQUIRE(ResultsOf<CommandFailureCode>(failed).size() == 1);
  CHECK(ResultsOf<CommandFailureCode>(failed)[0].value == CommandFailureCode::Busy);
  fixture.Send(Rejection(fixture.requests.back().request_id()));
  const auto rejected = fixture.ReceiveOutput();
  REQUIRE(ResultsOf<ServerRejection>(rejected).size() == 1);
  CHECK(fixture.client->Phase() == SessionPhase::Ready);

  REQUIRE(fixture.exchange->Post({generation, GameExited{}}) == CommandPostResult::Queued);
  fixture.Until([&] { return fixture.requests.size() == 3; });
  CHECK(fixture.requests.back().update_player().has_leave_game());
  CHECK(fixture.errors.empty());
}

TEST_CASE("Sample cadence retains the latest sample and preserves transitions while ACKs progress")
{
  Fixture fixture;
  const auto generation = Ready(fixture);
  REQUIRE(fixture.exchange->Post({generation, LocalMovement{}}) == CommandPostResult::Queued);
  fixture.Until([&] { return fixture.requests.size() == 2; });
  P::ServerPacket ack;
  ack.set_protocol_version(Wire::Version);
  ack.set_request_id(fixture.requests.back().request_id());
  ack.mutable_player_update_accepted();
  fixture.Send(ack);
  for (int i = 0; i < 10; ++i) fixture.Step();

  LocalMovement stale;
  stale.location = Domain::PlayerLocation{};
  REQUIRE(fixture.exchange->Post({generation, stale}) == CommandPostResult::Queued);
  auto latest = stale;
  latest.location->position.X = 2;
  REQUIRE(fixture.exchange->Post({generation, latest}) == CommandPostResult::Replaced);
  REQUIRE(fixture.exchange->Post({generation, GameExited{}}) == CommandPostResult::Queued);
  fixture.Until([&] { return fixture.requests.size() == 4; });
  const auto& sample = fixture.requests[2].update_player().set_location();
  REQUIRE(sample.has_location());
  CHECK(sample.location().position().x() == 2);
  CHECK(fixture.requests[3].update_player().has_leave_game());
  CHECK(fixture.errors.empty());
}

TEST_CASE("Undrained player rejections share the outcome bound and stale samples settle after disconnect")
{
  Fixture fixture{2000, 1, 1024 * 1024, 1};
  const auto generation = Ready(fixture);
  REQUIRE(fixture.exchange->Post({generation, CharacterStarted{"First"}}) == CommandPostResult::Queued);
  fixture.Until([&] { return fixture.requests.size() == 2; });
  REQUIRE(fixture.exchange->Post({generation, LocalMovement{}}) == CommandPostResult::Queued);
  fixture.Send(Rejection(fixture.requests.back().request_id()));
  fixture.peer->Disconnect(DisconnectType::Later, DisconnectReason::ServerShutdown);
  fixture.server.FlushPackets();
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Disconnected; });
  CHECK(fixture.requests.size() == 2);
  const auto rejected = fixture.Drain();
  REQUIRE(ResultsOf<ServerRejection>(rejected).size() == 1);
  REQUIRE(fixture.client->Poll());
  const auto stale = fixture.Drain();
  CHECK(ResultsOf<CommandFailureCode>(stale).empty()); // Stale measurements have no request outcome.
  CHECK(fixture.errors.empty());
}

TEST_CASE("Compact movement only changes location and full reset replaces game state")
{
  Fixture fixture;
  auto welcome = Welcome(fixture.Open());
  auto* player = welcome.mutable_session_opened()->mutable_players(0);
  player->set_character_name("Nerevar");
  player->set_character_generation(1);
  auto* kind = welcome.mutable_session_opened()->add_actor_value_kinds();
  kind->set_id(1);
  kind->set_key("health");
  auto* value = player->add_actor_values();
  value->set_kind(1);
  value->mutable_resource()->set_current(0);
  value->mutable_resource()->set_maximum(100);
  player->mutable_details()->set_level(12);
  fixture.Send(welcome);
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Ready; });
  fixture.Drain();

  P::ServerPacket moved;
  moved.set_protocol_version(Wire::Version);
  auto* space = moved.mutable_presence_changed()->mutable_space();
  space->mutable_location_id()->set_plugin_name("skyrim.esm");
  space->mutable_location_id()->set_local_form_id(0x123);
  auto* baseline = moved.mutable_presence_changed()->add_visibility();
  baseline->set_player_id(7);
  baseline->set_view_revision(1);
  baseline->mutable_pose()->mutable_position()->set_x(42);
  fixture.Send(moved);
  auto output = fixture.ReceiveOutput();
  auto changed = std::get<ClientStateDelta>(output.state.updates.back()).players.front();
  REQUIRE(changed.location);
  CHECK(changed.location->position.X == 42);
  CHECK(changed.actorValues.size() == 1);
  CHECK(changed.details.level == 12);
  CHECK(changed.characterGeneration == 1);

  moved.mutable_presence_changed()->clear_space();
  baseline->clear_pose();
  baseline->set_view_revision(2);
  fixture.Send(moved);
  output = fixture.ReceiveOutput();
  CHECK_FALSE(std::get<ClientStateDelta>(output.state.updates.back()).players.front().location);

  P::ServerPacket reset;
  reset.set_protocol_version(Wire::Version);
  auto* restarted = reset.mutable_presence_changed()->add_updated();
  restarted->mutable_profile()->set_player_id(7);
  restarted->set_character_generation(2);
  fixture.Send(reset);
  output = fixture.ReceiveOutput();
  changed = std::get<ClientStateDelta>(output.state.updates.back()).players.front();
  CHECK(changed.characterGeneration == 2);
  CHECK_FALSE(changed.characterName);
  CHECK(changed.actorValues.empty());
  CHECK_FALSE(changed.details.level);
  CHECK(fixture.errors.empty());
}

TEST_CASE("Actor value kinds of the welcome and of later batches resolve patches; an unknown one faults")
{
  Fixture fixture;
  auto    welcome = Welcome(fixture.Open());
  auto*   kind    = welcome.mutable_session_opened()->add_actor_value_kinds();
  kind->set_id(1);
  kind->set_key("skyrim:health");
  kind->set_display_name("Health");
  auto* value = welcome.mutable_session_opened()->mutable_players(0)->add_actor_values();
  value->set_kind(1);
  value->mutable_resource()->set_current(50);
  value->mutable_resource()->set_maximum(100);
  fixture.Send(welcome);
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Ready; });
  fixture.Drain();

  // A new key appears later: its kind comes with the batch that first uses it.
  P::ServerPacket batch;
  batch.set_protocol_version(Wire::Version);
  auto* added = batch.mutable_presence_changed()->add_actor_value_kinds();
  added->set_id(2);
  added->set_key("skyrim:stamina");
  added->set_display_name("Stamina");
  auto* patch = batch.mutable_presence_changed()->add_metadata();
  patch->set_player_id(7);
  auto* stamina = patch->add_actor_values();
  stamina->set_kind(2);
  stamina->mutable_resource()->set_current(-4);
  stamina->mutable_resource()->set_maximum(90);
  auto* health = patch->add_actor_values();
  health->set_kind(1);
  health->mutable_resource()->set_current(51);
  health->mutable_resource()->set_maximum(100);
  fixture.Send(batch);
  auto output  = fixture.ReceiveOutput();
  auto changed = std::get<ClientStateDelta>(output.state.updates.back()).players.front();
  CHECK(
    changed.actorValues.at("skyrim:stamina").state == Domain::ActorValueState{
                                                          Domain::ResourceActorValue{-4, 90}
  });
  CHECK(
    changed.actorValues.at("skyrim:health").state == Domain::ActorValueState{
                                                         Domain::ResourceActorValue{51, 100}
  });

  // Known by now: a later batch uses kind 2 without defining it again.
  batch.mutable_presence_changed()->clear_actor_value_kinds();
  patch->clear_actor_values();
  patch->add_removed_actor_values(2);
  fixture.Send(batch);
  output  = fixture.ReceiveOutput();
  changed = std::get<ClientStateDelta>(output.state.updates.back()).players.front();
  CHECK_FALSE(changed.actorValues.contains("skyrim:stamina"));
  CHECK(fixture.errors.empty());

  patch->clear_removed_actor_values();
  patch->add_removed_actor_values(3);
  fixture.Send(batch);
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Faulted; });
}

TEST_CASE("Periodic movement repeats the last pose with its capture time after local sampling stops")
{
  Fixture fixture;
  const auto generation = Ready(fixture);
  Domain::PlayerLocation location{{{"skyrim.esm", 0x3c}, "Tamriel"}, {42, 0, 0}, {}, 100};
  REQUIRE(fixture.exchange->Post({generation, LocalMovement{location}}) == CommandPostResult::Queued);
  fixture.Until([&] { return fixture.requests.size() == 2; });
  CHECK(fixture.requests.back().update_player().has_set_location());
  CHECK(fixture.samples.empty());
  P::ServerPacket ack;
  ack.set_protocol_version(Wire::Version);
  ack.set_request_id(fixture.requests.back().request_id());
  ack.mutable_player_update_accepted();
  fixture.Send(ack);
  fixture.Until([&] { return fixture.samples.size() >= 3; });
  CHECK(fixture.requests.size() == 2);
  CHECK(fixture.samples[0].sample().sequence() < fixture.samples[2].sample().sequence());
  CHECK(fixture.samples[2].sample().pose().position().x() == 42);
  // A repeat says when the pose was true, not when it was sent.
  for (const auto& sample : fixture.samples)
    CHECK(sample.sample().pose().sampled_at_us() == 100);
  CHECK(fixture.errors.empty());
}

TEST_CASE("A guest link joins at once, carries the session opened on it and comes back when the session ends")
{
  Fixture fixture;
  fixture.client->KeepGuest(true);
  fixture.Until([&] { return fixture.requests.size() == 1; });
  CHECK(fixture.requests[0].has_join_as_guest());
  CHECK(fixture.requests[0].request_id() != 0);
  CHECK(fixture.client->Phase() == SessionPhase::Disconnected);
  // The guest is invisible to the exchange: nothing was published.
  CHECK(fixture.Drain().state.updates.empty());

  const auto request = fixture.Open();
  CHECK(fixture.connects == 1);
  fixture.Send(Welcome(request));
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Ready; });

  REQUIRE(fixture.client->Disconnect());
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Disconnected; });
  fixture.Until([&] { return fixture.connects == 2 && fixture.requests.back().has_join_as_guest(); });
  CHECK(fixture.errors.empty());
}

TEST_CASE("A sign-in while the guest link connects opens on that connection instead of joining")
{
  Fixture fixture;
  fixture.client->KeepGuest(true);
  REQUIRE(fixture.client->Poll(0));
  fixture.Open();
  CHECK(fixture.connects == 1);
  CHECK(std::ranges::none_of(fixture.requests, &P::ClientPacket::has_join_as_guest));
  CHECK(fixture.errors.empty());
}

TEST_CASE("A refused sign-in leaves the client a guest")
{
  Fixture fixture;
  fixture.client->KeepGuest(true);
  fixture.Until([&] { return fixture.requests.size() == 1; });
  fixture.Send(Rejection(fixture.Open()));
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Disconnected; });
  fixture.Until([&] { return fixture.connects == 2 && fixture.requests.back().has_join_as_guest(); });
}

TEST_CASE("Leaving closes the guest link gracefully and nothing connects again")
{
  Fixture fixture;
  fixture.client->KeepGuest(true);
  fixture.Until([&] { return fixture.requests.size() == 1; });
  fixture.client->KeepGuest(false);
  CHECK(fixture.client->Closing());
  fixture.Until([&] { return fixture.closed && !fixture.client->Closing(); });
  for (int step = 0; step < 20; ++step)
    fixture.Step();
  CHECK(fixture.connects == 1);
  CHECK(fixture.requests.size() == 1);
  CHECK(fixture.errors.empty());
}

TEST_CASE("A mute and the end of a session reach the exchange status; the end of the session takes the mute along")
{
  Fixture fixture;
  Ready(fixture);
  P::ServerPacket muted;
  muted.set_protocol_version(Wire::Version);
  muted.mutable_mute_changed()->mutable_mute()->set_reason("Флуд");
  fixture.Send(muted);
  fixture.Until([&] { return fixture.exchange->Status().mute.has_value(); });
  CHECK(fixture.exchange->Status().mute->reason == "Флуд");
  CHECK_FALSE(fixture.exchange->Status().mute->untilUnixMs);

  P::ServerPacket ended;
  ended.set_protocol_version(Wire::Version);
  ended.mutable_session_ended()->set_reason(P::SESSION_END_REASON_KICKED);
  ended.mutable_session_ended()->set_text("Остынь");
  fixture.Send(ended);
  fixture.Until([&] { return fixture.exchange->Status().sessionEnd.has_value(); });
  CHECK(fixture.exchange->Status().sessionEnd == Domain::SessionEnd{Domain::SessionEndReason::Kicked, "Остынь", std::nullopt});
  CHECK(fixture.exchange->Status().sessionEndSequence == 1);

  REQUIRE(fixture.client->Disconnect());
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Disconnected; });
  CHECK_FALSE(fixture.exchange->Status().mute);
  // Kept for the page until the next sign-in.
  CHECK(fixture.exchange->Status().sessionEnd);
  CHECK(fixture.errors.empty());
}

TEST_CASE("Chat can arrive before bootstrap on its independent reliable channel")
{
  Fixture fixture;
  const auto request = fixture.Open();
  fixture.Send(Publication(0, 30, 8));
  for (int i = 0; i < 10; ++i) fixture.Step();
  CHECK(fixture.client->Phase() == SessionPhase::Opening);
  fixture.Send(Welcome(request));
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Ready; });
  CHECK(fixture.errors.empty());
}

TEST_CASE("Unexpected or reused update ACK is a protocol failure")
{
  Fixture fixture;
  Ready(fixture);
  P::ServerPacket ack;
  ack.set_protocol_version(Wire::Version);
  ack.set_request_id(999);
  ack.mutable_player_update_accepted();
  fixture.Send(ack);
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Faulted; });
  Empty(fixture.Drain());
  REQUIRE(fixture.errors.size() == 1);
}

TEST_CASE("Explicit location transitions report invalid session generation while samples remain disposable")
{
  Fixture fixture;
  const auto generation = Ready(fixture);
  REQUIRE(fixture.exchange->Post({generation - 1, LocalLocation{}}) == CommandPostResult::Queued);
  REQUIRE(fixture.exchange->Post({generation - 1, LocalMovement{}}) == CommandPostResult::Queued);
  REQUIRE(fixture.client->Poll());
  const auto output = fixture.Drain();
  REQUIRE(ResultsOf<CommandFailureCode>(output).size() == 1);
  CHECK(ResultsOf<CommandFailureCode>(output)[0].value == CommandFailureCode::StaleGeneration);
  CHECK(ResultsOf<CommandFailureCode>(output)[0].requestId != 0);
  CHECK(fixture.requests.size() == 1);
  CHECK(fixture.samples.empty());
}

TEST_CASE("Announcements go to the system channel within the welcome policy and settle like chat")
{
  Fixture    fixture;
  const auto announce = [&](std::uint64_t generation, std::string text, std::string label, Domain::ChatChannelId channel = 2) {
    const auto id = Value(fixture.exchange->NextRequestId());
    REQUIRE(
      fixture.exchange->Post({
          generation,
          PostAnnouncement{
                           id, channel,
                           std::move(text),
                           Domain::AnnouncementKind::Event,
                           Domain::ClientAnnouncementSource::ThirdParty,
                           std::move(label)
          }
    }) == CommandPostResult::Queued);
    return id;
  };

  fixture.Send(Welcome(fixture.Open()));
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Ready; });
  const auto opened = fixture.Drain();
  const auto& snapshot = std::get<ClientSnapshot>(opened.state.updates.front());
  REQUIRE(snapshot.chats.size() == 2);
  CHECK(std::ranges::count(snapshot.chats, Domain::ChatChannelKind::System, &ChatCacheSnapshot::kind) == 1);
  const auto generation = snapshot.generation;

  // Beyond the announced lengths and the global channel stay local.
  for (
    const auto& [text, label, channel] : {
        std::tuple{"Одиннадцать", "Mod",          2},
        std::tuple{"event",       "TooLongLabel", 2},
        std::tuple{"event",       "Mod",          1}
  })
  {
    const auto id     = announce(generation, text, label, static_cast<Domain::ChatChannelId>(channel));
    const auto output = fixture.ReceiveOutput();
    REQUIRE(ResultsOf<CommandFailureCode>(output).size() == 1);
    CHECK(ResultsOf<CommandFailureCode>(output)[0].requestId == id);
    CHECK(ResultsOf<CommandFailureCode>(output)[0].value == CommandFailureCode::InvalidRequest);
  }

  // A source the welcome does not allow stays local as well.
  const auto trusted = Value(fixture.exchange->NextRequestId());
  REQUIRE(
    fixture.exchange->Post({
        generation,
        PostAnnouncement{
                         trusted, 2,
                         "event", Domain::AnnouncementKind::Event,
                         Domain::ClientAnnouncementSource::TrustedClient,
                         "Mod"
        }
  }) == CommandPostResult::Queued);
  const auto untrusted = fixture.ReceiveOutput();
  REQUIRE(ResultsOf<CommandFailureCode>(untrusted).size() == 1);
  CHECK(ResultsOf<CommandFailureCode>(untrusted)[0].requestId == trusted);
  CHECK(ResultsOf<CommandFailureCode>(untrusted)[0].value == CommandFailureCode::InvalidRequest);
  CHECK(fixture.requests.size() == 1);

  const auto id = announce(generation, "Пал в бою", "Мод");
  fixture.Until([&] { return fixture.requests.size() == 2; });
  const auto& request = fixture.requests.back().post_announcement();
  CHECK(fixture.requests.back().request_id() == id);
  CHECK(request.channel_id() == 2);
  CHECK(request.text() == "Пал в бою");
  CHECK(request.kind() == P::ANNOUNCEMENT_KIND_EVENT);
  CHECK(request.source() == P::CLIENT_ANNOUNCEMENT_SOURCE_THIRD_PARTY);
  CHECK(request.signature() == "Мод");

  auto published = Publication(id, 3);
  published.mutable_chat_published()->mutable_message()->set_channel_id(2);
  auto* announcement = published.mutable_chat_published()->mutable_message()->mutable_announcement();
  announcement->set_source(P::ANNOUNCEMENT_SOURCE_THIRD_PARTY);
  announcement->set_kind(P::ANNOUNCEMENT_KIND_EVENT);
  announcement->set_signature("Мод");
  fixture.Send(published);
  const auto confirmed = fixture.ReceiveOutput();
  REQUIRE(ResultsOf<MessagePublished>(confirmed).size() == 1);
  CHECK(ResultsOf<MessagePublished>(confirmed)[0].requestId == id);
  const auto added = Added(confirmed);
  REQUIRE(added.size() == 1);
  REQUIRE(added[0].announcement);
  CHECK(added[0].announcement->source == Domain::AnnouncementSource::ThirdParty);
  CHECK(added[0].announcement->signature == "Мод");
  CHECK(fixture.errors.empty());

  // A refusal on the chat lane settles the request without a fault.
  const auto refused = announce(generation, "ещё", "Мод");
  fixture.Until([&] { return fixture.requests.size() == 3; });
  auto rejection = Rejection(refused);
  rejection.mutable_request_rejected()->set_code(P::REQUEST_REJECTION_CODE_ANNOUNCEMENT_NOT_ALLOWED);
  fixture.Send(rejection);
  const auto output = fixture.ReceiveOutput();
  REQUIRE(ResultsOf<ServerRejection>(output).size() == 1);
  CHECK(ResultsOf<ServerRejection>(output)[0].value.code == RequestRejectionCode::AnnouncementNotAllowed);
  CHECK(fixture.client->Phase() == SessionPhase::Ready);

  REQUIRE(fixture.client->Disconnect());
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Disconnected; });
}

TEST_CASE("Ground mark commands settle by request ID on the control lane and visible deltas feed the model")
{
  Fixture                           fixture;
  const auto                        generation = Ready(fixture);
  const Domain::GroundMarkPlacement placement{{"skyrim.esm", 0x1A26F}, {1, 2, 3}, 0.5f};
  const auto                        noteId = Value(fixture.exchange->NextRequestId());
  REQUIRE(fixture.exchange->Post({generation, PlaceGroundNote{noteId, "praise", placement, {4, 201, 8, 17, 2, 14, 5}}}) == CommandPostResult::Queued);
  fixture.Until([&] { return fixture.requests.size() == 2; });
  CHECK(fixture.requests.back().has_place_ground_note());
  CHECK(fixture.requests.back().request_id() == noteId);
  CHECK(fixture.requests.back().place_ground_note().placement().position().z() == 3);

  const auto writeMark = [](P::GroundMark& mark, std::uint64_t id) {
    mark.set_mark_id(id);
    mark.mutable_author()->set_player_id(7);
    mark.mutable_author()->set_username("user");
    mark.mutable_author()->set_display_name("Player");
    mark.set_kind(P::GROUND_MARK_KIND_NOTE);
    mark.set_text("praise");
    mark.mutable_placement()->mutable_location_id()->set_plugin_name("skyrim.esm");
    mark.mutable_placement()->mutable_location_id()->set_local_form_id(0x1A26F);
    mark.set_created_at_unix_ms(123);
  };
  // Until evaluates its predicate once more after success, so the drained result is kept.
  const auto confirmations = [&] {
    ClientOutput found;
    fixture.Until([&] {
      if (!found.results.empty()) return true;
      fixture.exchange->Drain(found);
      return !found.results.empty();
    });
    return found;
  };

  // The confirmation settles the request; the mark reaches the model only through the delta.
  P::ServerPacket placed;
  placed.set_protocol_version(Wire::Version);
  placed.set_request_id(noteId);
  writeMark(*placed.mutable_ground_mark_placed()->mutable_mark(), 9);
  placed.mutable_ground_mark_placed()->set_evicted_id(2);
  fixture.Send(placed);
  const auto confirmed = confirmations();
  REQUIRE(ResultsOf<MarkPlaced>(confirmed).size() == 1);
  CHECK(ResultsOf<MarkPlaced>(confirmed)[0].requestId == noteId);
  CHECK(ResultsOf<MarkPlaced>(confirmed)[0].value.markId == 9);
  CHECK(ResultsOf<MarkPlaced>(confirmed)[0].value.evictedId == 2);
  CHECK(ResultsOf<MarkPlaced>(confirmed)[0].generation == generation);
  CHECK(confirmed.state.updates.empty());

  P::ServerPacket changed;
  changed.set_protocol_version(Wire::Version);
  changed.mutable_ground_marks_changed()->set_view_revision(1);
  changed.mutable_ground_marks_changed()->set_clear(true);
  writeMark(*changed.mutable_ground_marks_changed()->add_added(), 9);
  fixture.Send(changed);
  const auto visible = fixture.ReceiveOutput();
  REQUIRE(visible.state.updates.size() == 1);
  const auto& delta = std::get<ClientStateDelta>(visible.state.updates.front());
  REQUIRE(delta.groundMarks.size() == 2);
  CHECK(std::holds_alternative<GroundMarksCleared>(delta.groundMarks[0]));
  CHECK(std::get<GroundMarksAdded>(delta.groundMarks[1]).marks[0].markId == 9);
  CHECK(delta.chatContent.empty());

  const auto removeId = Value(fixture.exchange->NextRequestId());
  REQUIRE(fixture.exchange->Post({generation, RemoveGroundMark{removeId, 9}}) == CommandPostResult::Queued);
  fixture.Until([&] { return fixture.requests.size() == 3; });
  CHECK(fixture.requests.back().remove_ground_mark().mark_id() == 9);
  P::ServerPacket removed;
  removed.set_protocol_version(Wire::Version);
  removed.set_request_id(removeId);
  removed.mutable_ground_mark_removed()->set_mark_id(9);
  fixture.Send(removed);
  const auto settled = confirmations();
  REQUIRE(ResultsOf<MarkRemoved>(settled).size() == 1);
  CHECK(ResultsOf<MarkRemoved>(settled)[0].value.markId == 9);

  // A refusal on the control lane settles a death report without a fault.
  const auto deathId = Value(fixture.exchange->NextRequestId());
  REQUIRE(fixture.exchange->Post({generation, ReportDeath{deathId, "", placement, {4, 201, 8, 17, 2, 14, 5}}}) == CommandPostResult::Queued);
  fixture.Until([&] { return fixture.requests.size() == 4; });
  CHECK(fixture.requests.back().has_report_death());
  auto rejection = Rejection(deathId);
  rejection.mutable_request_rejected()->set_code(P::REQUEST_REJECTION_CODE_GROUND_MARK_AREA_FULL);
  fixture.Send(rejection);
  const auto refused = confirmations();
  REQUIRE(ResultsOf<ServerRejection>(refused).size() == 1);
  CHECK(ResultsOf<ServerRejection>(refused)[0].value.code == RequestRejectionCode::GroundMarkAreaFull);
  CHECK(fixture.client->Phase() == SessionPhase::Ready);
  CHECK(fixture.errors.empty());

  // A repeated revision is a protocol fault: deltas are reliable and ordered.
  fixture.Send(changed);
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Faulted; });
  REQUIRE(fixture.errors.size() == 1);
}

TEST_CASE("Hidden identity opens from the first packet and a switch settles with the pseudonym")
{
  Fixture fixture;
  fixture.exchange->SetHideIdentity(Domain::HiddenIdentity::ExceptGroundMarks);
  auto welcome = Welcome(fixture.Open());
  CHECK(fixture.requests.back().open_session().hidden_identity() == P::HIDDEN_IDENTITY_EXCEPT_GROUND_MARKS);
  welcome.mutable_session_opened()->set_own_pseudonym("Страж");
  welcome.mutable_session_opened()->set_hidden_identity(P::HIDDEN_IDENTITY_EXCEPT_GROUND_MARKS);
  fixture.Send(welcome);
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Ready; });
  const auto opened = fixture.Drain();
  REQUIRE(opened.status.pseudonym);
  CHECK(*opened.status.pseudonym == "Страж");
  CHECK(opened.status.hiding == Domain::HiddenIdentity::ExceptGroundMarks);
  const auto generation = std::get<ClientSnapshot>(opened.state.updates.front()).generation;

  const auto confirmations = [&] {
    ClientOutput found;
    fixture.Until([&] {
      if (!ResultsOf<IdentityChanged>(found).empty() || !ResultsOf<ServerRejection>(found).empty()) return true;
      fixture.exchange->Drain(found);
      return !ResultsOf<IdentityChanged>(found).empty() || !ResultsOf<ServerRejection>(found).empty();
    });
    return found;
  };

  // Showing the names again: the status forgets the pseudonym with the confirmation.
  const auto showId = Value(fixture.exchange->NextRequestId());
  REQUIRE(fixture.exchange->Post({generation, SetIdentityVisibility{showId, Domain::HiddenIdentity::None}}) == CommandPostResult::Queued);
  fixture.Until([&] { return fixture.requests.size() == 2; });
  CHECK(fixture.requests.back().has_set_identity_visibility());
  CHECK(fixture.requests.back().set_identity_visibility().hidden() == P::HIDDEN_IDENTITY_NONE);
  P::ServerPacket shown;
  shown.set_protocol_version(Wire::Version);
  shown.set_request_id(showId);
  shown.mutable_identity_visibility_changed();
  fixture.Send(shown);
  const auto settled = confirmations();
  REQUIRE(ResultsOf<IdentityChanged>(settled).size() == 1);
  CHECK(ResultsOf<IdentityChanged>(settled)[0].requestId == showId);
  CHECK(ResultsOf<IdentityChanged>(settled)[0].value.hiding == Domain::HiddenIdentity::None);
  CHECK_FALSE(settled.status.pseudonym);
  CHECK(settled.status.hiding == Domain::HiddenIdentity::None);

  // A refusal settles the switch without a fault; the session stays usable.
  const auto hideId = Value(fixture.exchange->NextRequestId());
  REQUIRE(fixture.exchange->Post({generation, SetIdentityVisibility{hideId, Domain::HiddenIdentity::Everywhere}}) == CommandPostResult::Queued);
  fixture.Until([&] { return fixture.requests.size() == 3; });
  auto limited = Rejection(hideId);
  limited.mutable_request_rejected()->set_code(P::REQUEST_REJECTION_CODE_RATE_LIMITED);
  fixture.Send(limited);
  const auto refused = confirmations();
  REQUIRE(ResultsOf<ServerRejection>(refused).size() == 1);
  CHECK(ResultsOf<ServerRejection>(refused)[0].value.code == RequestRejectionCode::RateLimited);
  CHECK(fixture.client->Phase() == SessionPhase::Ready);

  // An unknown settlement is a protocol fault, and a new session starts without a pseudonym.
  fixture.Send(shown);
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Faulted; });
  CHECK_FALSE(fixture.Drain().status.pseudonym);
}

TEST_CASE("Moderator requests settle by request ID; a removed message leaves every model and the role follows the server")
{
  Fixture    fixture;
  const auto generation = Ready(fixture);
  CHECK(fixture.exchange->Status().role == Domain::PlayerRole::Player);

  P::ServerPacket role;
  role.set_protocol_version(Wire::Version);
  role.mutable_role_changed()->set_role(P::PLAYER_ROLE_MODERATOR);
  fixture.Send(role);
  fixture.Until([&] { return fixture.exchange->Status().role == Domain::PlayerRole::Moderator; });

  // Until repeats the predicate once more: keep what was drained.
  const auto settled = [&] {
    ClientOutput found;
    fixture.Until([&] {
      if (!found.results.empty()) return true;
      fixture.exchange->Drain(found);
      return !found.results.empty();
    });
    return found;
  };
  const auto deleted = [](const ClientOutput& output) {
    std::vector<Domain::ChatMessageId> ids;
    for (const auto& update : output.state.updates)
      if (const auto* delta = std::get_if<ClientStateDelta>(&update))
        for (const auto& change : delta->chatContent)
          if (const auto* removal = std::get_if<ChatMessagesDeleted>(&change))
            ids.insert(ids.end(), removal->messageIds.begin(), removal->messageIds.end());
    return ids;
  };

  const auto muteId = Value(fixture.exchange->NextRequestId());
  REQUIRE(
    fixture.exchange->Post({
        generation,
        SanctionPlayer{muteId, 9, Domain::SanctionKind::Mute, 15, "Флуд"}
  }) == CommandPostResult::Queued);
  fixture.Until([&] { return fixture.requests.size() == 2; });
  CHECK(fixture.requests.back().sanction_player().minutes() == 15);
  P::ServerPacket issued;
  issued.set_protocol_version(Wire::Version);
  issued.set_request_id(muteId);
  auto* entry = issued.mutable_sanction_issued()->mutable_sanction();
  entry->set_player_id(9);
  entry->set_kind(P::SANCTION_KIND_MUTE);
  entry->set_reason("Флуд");
  entry->set_issued_at_unix_ms(1000);
  fixture.Send(issued);
  const auto sanctioned = settled();
  REQUIRE(ResultsOf<Sanctioned>(sanctioned).size() == 1);
  CHECK(ResultsOf<Sanctioned>(sanctioned)[0].value.sanction == Domain::Sanction{9, Domain::SanctionKind::Mute, "Флуд", 1000, std::nullopt});

  // The moderator's copy of a removal settles the deletion and drops the message.
  const auto deleteId = Value(fixture.exchange->NextRequestId());
  REQUIRE(
    fixture.exchange->Post({
        generation,
        DeleteChatMessage{deleteId, 1, 2}
  }) == CommandPostResult::Queued);
  fixture.Until([&] { return fixture.requests.size() == 3; });
  P::ServerPacket removed;
  removed.set_protocol_version(Wire::Version);
  removed.set_request_id(deleteId);
  removed.mutable_chat_message_removed()->set_channel_id(1);
  removed.mutable_chat_message_removed()->set_message_id(2);
  fixture.Send(removed);
  const auto own = settled();
  REQUIRE(ResultsOf<MessageDeleted>(own).size() == 1);
  CHECK(deleted(own) == std::vector<Domain::ChatMessageId>{2});

  // Another moderator's removal is a notification; the ID leaves even a history the cache no longer holds.
  removed.clear_request_id();
  removed.mutable_chat_message_removed()->set_message_id(1);
  fixture.Send(removed);
  CHECK(deleted(fixture.ReceiveOutput()) == std::vector<Domain::ChatMessageId>{1});

  // A mark list names one author; any other is a protocol fault.
  const auto listMarks = [&](std::uint64_t author) {
    const auto marksId = Value(fixture.exchange->NextRequestId());
    const auto count   = fixture.requests.size();
    REQUIRE(
      fixture.exchange->Post({
          generation,
          ListPlayerMarks{marksId, 9}
    }) == CommandPostResult::Queued);
    fixture.Until([&] { return fixture.requests.size() == count + 1; });
    P::ServerPacket marks;
    marks.set_protocol_version(Wire::Version);
    marks.set_request_id(marksId);
    marks.mutable_player_marks()->set_player_id(9);
    auto* mark = marks.mutable_player_marks()->add_marks();
    mark->set_mark_id(4);
    mark->mutable_author()->set_player_id(author);
    mark->mutable_author()->set_username("nine");
    mark->mutable_author()->set_display_name("Nine");
    mark->set_kind(P::GROUND_MARK_KIND_NOTE);
    mark->set_text("note");
    mark->mutable_placement()->mutable_location_id()->set_plugin_name("skyrim.esm");
    mark->mutable_placement()->mutable_location_id()->set_local_form_id(0x1A26F);
    fixture.Send(marks);
  };
  listMarks(9);
  const auto listed = settled();
  REQUIRE(ResultsOf<MarksListed>(listed).size() == 1);
  CHECK(ResultsOf<MarksListed>(listed)[0].value.marks.size() == 1);
  listMarks(8);
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Faulted; });
  CHECK(fixture.exchange->Status().role == Domain::PlayerRole::Player);
}

TEST_CASE("Guilds follow the welcome with their channels, change in the server's order and settle requests by ID")
{
  Fixture    fixture;
  const auto generation = Ready(fixture);
  CHECK_FALSE(fixture.exchange->Status().guilds);
  constexpr auto first  = Domain::GuildChannelBase + 4;
  constexpr auto second = Domain::GuildChannelBase + 5;

  const auto fill = [](P::Guild& guild, std::uint64_t guildId, std::uint64_t messageId) {
    guild.set_guild_id(guildId);
    guild.set_name("Guild" + std::to_string(guildId));
    guild.set_channel_id(Domain::GuildChannelBase + guildId);
    auto* member = guild.add_members();
    member->mutable_profile()->set_player_id(7);
    member->mutable_profile()->set_username("user");
    member->mutable_profile()->set_display_name("Player");
    member->set_role(P::GUILD_ROLE_MASTER);
    if (messageId == 0) return;
    auto* message = guild.add_recent_messages();
    message->set_message_id(messageId);
    message->set_channel_id(Domain::GuildChannelBase + guildId);
    *message->mutable_author() = member->profile();
    message->set_text("guild");
  };
  const auto change = [&](auto write) {
    P::ServerPacket packet;
    packet.set_protocol_version(Wire::Version);
    write(*packet.mutable_guild_changed());
    fixture.Send(packet);
  };
  // Until repeats the predicate once more: keep everything drained so far.
  const auto collect = [&](auto done) {
    ClientOutput all;
    fixture.Until([&] {
      if (done(all)) return true;
      ClientOutput next;
      fixture.exchange->Drain(next);
      std::ranges::move(next.state.updates, std::back_inserter(all.state.updates));
      std::ranges::move(next.results, std::back_inserter(all.results));
      all.status = std::move(next.status);
      return done(all);
    });
    return all;
  };
  const auto chatState = [](const ClientOutput& output, Domain::ChatChannelId channel) {
    std::optional<std::optional<ChatCacheState>> found;
    for (const auto& update : output.state.updates)
      for (const auto& state : std::get<ClientStateDelta>(update).chats)
        if (state.channelId == channel) found = state.state;
    return found;
  };

  P::ServerPacket snapshot;
  snapshot.set_protocol_version(Wire::Version);
  fill(*snapshot.mutable_guilds_snapshot()->add_guilds(), 4, 1);
  auto* invite = snapshot.mutable_guilds_snapshot()->add_invites();
  invite->set_guild_id(5);
  invite->set_guild_name("Guild5");
  invite->set_invited_by_player_id(9);
  snapshot.mutable_guilds_snapshot()->mutable_limits()->set_max_guilds_per_player(3);
  fixture.Send(snapshot);
  const auto opened = collect([](const ClientOutput& output) { return output.status.guilds != nullptr; });
  REQUIRE(opened.status.guilds->Guilds().size() == 1);
  CHECK(opened.status.guilds->Invites().size() == 1);
  CHECK(opened.status.guilds->Limits().maxGuildsPerPlayer == 3);
  const auto firstState = chatState(opened, first);
  REQUIRE((firstState && *firstState));
  CHECK((*firstState)->kind == Domain::ChatChannelKind::Guild);
  const auto history = Added(opened);
  REQUIRE(history.size() == 1);
  CHECK(history[0].channelId == first);

  // Accepting an invitation: the invitation goes, the guild comes, then the answer.
  const auto answerId = Value(fixture.exchange->NextRequestId());
  REQUIRE(
    fixture.exchange->Post({
        generation,
        GuildRequest{answerId, AnswerGuildInvite{5, true}}
  }) == CommandPostResult::Queued);
  fixture.Until([&] { return fixture.requests.size() == 2; });
  CHECK(fixture.requests.back().guild_command().answer().guild_id() == 5);
  change([](P::GuildChanged& changed) { changed.set_invite_removed(5); });
  change([&](P::GuildChanged& changed) { fill(*changed.mutable_added(), 5, 0); });
  P::ServerPacket done;
  done.set_protocol_version(Wire::Version);
  done.set_request_id(answerId);
  done.mutable_guild_command_done()->set_guild_id(5);
  fixture.Send(done);
  const auto joined = collect([](const ClientOutput& output) { return !output.results.empty(); });
  REQUIRE(ResultsOf<GuildDone>(joined).size() == 1);
  CHECK(ResultsOf<GuildDone>(joined)[0].value.guildId == 5);
  CHECK(joined.status.guilds->Guilds().size() == 2);
  CHECK(joined.status.guilds->Invites().empty());
  CHECK(joined.status.guilds != opened.status.guilds);
  CHECK(chatState(joined, second));

  // Guild chat goes out on the chat lane and its acceptance comes on the control lane.
  const auto chatId = Value(fixture.exchange->NextRequestId());
  REQUIRE(
    fixture.exchange->Post({
        generation,
        SendChat{chatId, second, "в гильдию"}
  }) == CommandPostResult::Queued);
  fixture.Until([&] { return fixture.requests.size() == 3; });
  CHECK(fixture.requests.back().send_chat().channel_id() == second);
  auto accepted = Publication(chatId, 1);
  accepted.mutable_chat_published()->mutable_message()->set_channel_id(second);
  fixture.Send(accepted);
  const auto published = collect([](const ClientOutput& output) { return !output.results.empty(); });
  REQUIRE(ResultsOf<MessagePublished>(published).size() == 1);

  // Leaving takes the channel along.
  change([](P::GuildChanged& changed) {
    changed.mutable_removed()->set_guild_id(4);
    changed.mutable_removed()->set_reason(P::GUILD_REMOVAL_REASON_LEFT);
  });
  const auto left = collect([&](const ClientOutput& output) { return output.status.guilds && output.status.guilds->Guilds().size() == 1; });
  const auto gone = chatState(left, first);
  REQUIRE(gone);
  CHECK_FALSE(*gone);
  const auto refusedId = Value(fixture.exchange->NextRequestId());
  REQUIRE(
    fixture.exchange->Post({
        generation,
        SendChat{refusedId, first, "поздно"}
  }) == CommandPostResult::Queued);
  const auto refused = collect([](const ClientOutput& output) { return !output.results.empty(); });
  REQUIRE(ResultsOf<CommandFailureCode>(refused).size() == 1);
  CHECK(ResultsOf<CommandFailureCode>(refused)[0].value == CommandFailureCode::InvalidRequest);

  // A change to a guild the player is not in breaks the protocol.
  change([](P::GuildChanged& changed) {
    changed.mutable_member_removed()->set_guild_id(4);
    changed.mutable_member_removed()->set_player_id(9);
    changed.mutable_member_removed()->set_reason(P::GUILD_REMOVAL_REASON_LEFT);
  });
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Faulted; });
  CHECK_FALSE(fixture.exchange->Status().guilds);
}

TEST_CASE("A display name change settles once, one at a time, and a refusal keeps the session")
{
  Fixture fixture;
  auto    welcome = Welcome(fixture.Open());
  fixture.Send(welcome);
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Ready; });
  const auto opened     = fixture.Drain();
  const auto generation = std::get<ClientSnapshot>(opened.state.updates.front()).generation;

  // Until repeats the predicate once more: keep what was drained.
  const auto results = [&] {
    ClientOutput found;
    const auto   any = [&] {
      return !ResultsOf<NameChanged>(found).empty() || !ResultsOf<ServerRejection>(found).empty() || !ResultsOf<CommandFailureCode>(found).empty();
    };
    fixture.Until([&] {
      if (any()) return true;
      fixture.exchange->Drain(found);
      return any();
    });
    return found;
  };

  const auto changeId = Value(fixture.exchange->NextRequestId());
  REQUIRE(
    fixture.exchange->Post({
        generation,
        ChangeDisplayName{changeId, "Новое Имя"}
  }) == CommandPostResult::Queued);
  fixture.Until([&] { return fixture.requests.size() == 2; });
  CHECK(fixture.requests.back().request_id() == changeId);
  CHECK(fixture.requests.back().change_display_name().display_name() == "Новое Имя");

  // A second change while the first waits never leaves the client.
  const auto busyId = Value(fixture.exchange->NextRequestId());
  REQUIRE(
    fixture.exchange->Post({
        generation,
        ChangeDisplayName{busyId, "Другое"}
  }) == CommandPostResult::Queued);
  const auto busy = results();
  REQUIRE(ResultsOf<CommandFailureCode>(busy).size() == 1);
  CHECK(ResultsOf<CommandFailureCode>(busy)[0].requestId == busyId);
  CHECK(ResultsOf<CommandFailureCode>(busy)[0].value == CommandFailureCode::Busy);

  P::ServerPacket changed;
  changed.set_protocol_version(Wire::Version);
  changed.set_request_id(changeId);
  changed.mutable_display_name_changed()->set_display_name("Новое Имя");
  fixture.Send(changed);
  const auto settled = results();
  REQUIRE(ResultsOf<NameChanged>(settled).size() == 1);
  CHECK(ResultsOf<NameChanged>(settled)[0].requestId == changeId);
  CHECK(ResultsOf<NameChanged>(settled)[0].value.displayName == "Новое Имя");

  // A name that is not UTF-8 never leaves: the server would close the connection.
  const auto blankId = Value(fixture.exchange->NextRequestId());
  REQUIRE(
    fixture.exchange->Post({
        generation,
        ChangeDisplayName{blankId, "\xFF\xFE"}
  }) == CommandPostResult::Queued);
  const auto blank = results();
  REQUIRE(ResultsOf<CommandFailureCode>(blank).size() == 1);
  CHECK(ResultsOf<CommandFailureCode>(blank)[0].value == CommandFailureCode::InvalidRequest);
  CHECK(fixture.requests.size() == 2);

  // A server refusal settles the change; the session stays ready.
  const auto limitedId = Value(fixture.exchange->NextRequestId());
  REQUIRE(
    fixture.exchange->Post({
        generation,
        ChangeDisplayName{limitedId, "Третье"}
  }) == CommandPostResult::Queued);
  fixture.Until([&] { return fixture.requests.size() == 3; });
  auto limited = Rejection(limitedId);
  limited.mutable_request_rejected()->set_code(P::REQUEST_REJECTION_CODE_RATE_LIMITED);
  fixture.Send(limited);
  const auto refused = results();
  REQUIRE(ResultsOf<ServerRejection>(refused).size() == 1);
  CHECK(ResultsOf<ServerRejection>(refused)[0].value.code == RequestRejectionCode::RateLimited);
  CHECK(fixture.client->Phase() == SessionPhase::Ready);

  // An unknown settlement is a protocol fault.
  fixture.Send(changed);
  fixture.Until([&] { return fixture.client->Phase() == SessionPhase::Faulted; });
}

TEST_SUITE_END();
