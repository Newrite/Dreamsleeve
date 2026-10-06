#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <doctest/doctest.h>
#include <enet/enet.h>
#include <cstdlib>
#include "protocol.pb.h"
#include "phantom.pb.h"
import std;
import DreamNet.Peer;
import Dreamsleeve.Client.Phantom.Streaming;
import Dreamsleeve.Client.ProtocolCodec;

namespace
{
  namespace P                     = Dreamsleeve::Client::Phantom;
  namespace Models                = Dreamsleeve::Protocol::Phantom;
  namespace Chat                  = Dreamsleeve::Protocol::Chat;
  using Clock                     = std::chrono::steady_clock;
  constexpr std::size_t NodeCount = 1024;

  std::optional<std::string> Environment(const char* name, std::size_t maximum = 4096)
  {
    char*       text{};
    std::size_t length{};
    if (_dupenv_s(&text, &length, name) != 0) return {};
    const auto value = std::unique_ptr<char, decltype(&std::free)>(text, &std::free);
    if (!value || length == 0 || length > maximum + 1) return {};
    return std::string(value.get());
  }

  std::uint64_t NowUs()
  {
    return static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::microseconds>(Clock::now().time_since_epoch()).count());
  }

  P::ValidatedAsset Model()
  {
    P::Asset raw;
    raw.nodes.resize(NodeCount);
    for (std::size_t index = 1; index < raw.nodes.size(); ++index)
      raw.nodes[index].parent = P::NodeId{0};
    P::Geometry mesh;
    mesh.vertices.resize(3);
    mesh.vertices[1].position = {1, 0, 0};
    mesh.vertices[2].position = {0, 1, 0};
    mesh.indices              = {0, 1, 2};
    mesh.mask                 = P::AlphaMask{512, 512, std::vector<std::uint8_t>(512 * 512)};
    std::mt19937 random(21);
    for (auto& value : mesh.mask->pixels)
      value = static_cast<std::uint8_t>(random());
    raw.geometry.push_back(std::move(mesh));
    auto parsed = P::ValidatedAsset::Parse(std::move(raw));
    REQUIRE(parsed);
    return std::move(*parsed);
  }

  std::shared_ptr<const P::Snapshot> Pose(std::uint64_t generation, std::uint64_t sequence)
  {
    P::Snapshot pose;
    pose.generation  = {generation};
    pose.sequence    = {sequence};
    pose.context     = 10;
    pose.sampledAtUs = NowUs();
    pose.channels.resize(NodeCount);
    std::mt19937 random(21 + static_cast<unsigned>(sequence));
    for (auto& channel : pose.channels)
      channel.world.position = {
          static_cast<float>(random() % 10000) / 10000,
          static_cast<float>(random() % 10000) / 10000,
          static_cast<float>(random() % 10000) / 10000
      };
    pose.bounds.push_back({
        {0, 0, 0},
        4
    });
    return std::make_shared<const P::Snapshot>(std::move(pose));
  }

  bool dropFragment{}, fragmentDropped{};

  int DropOnePoseFragment(ENetHost* host, ENetEvent*)
  {
    if (!dropFragment || host->receivedDataLength < sizeof(ENetProtocolHeader) + sizeof(ENetProtocolCommandHeader)) return 0;
    enet_uint16 peer{};
    std::memcpy(&peer, host->receivedData, sizeof(peer));
    const auto header = (ENET_NET_TO_HOST_16(peer) & ENET_PROTOCOL_HEADER_FLAG_SENT_TIME) ? sizeof(ENetProtocolHeader)
                                                                                          : offsetof(ENetProtocolHeader, sentTime);
    if (
      (host->receivedData[header] & ENET_PROTOCOL_COMMAND_MASK) != ENET_PROTOCOL_COMMAND_SEND_UNRELIABLE_FRAGMENT ||
      host->receivedData[header + 1] != P::Wire::PosesLane)
      return 0;
    dropFragment    = false;
    fragmentDropped = true;
    return 1;
  }

  struct Client
  {
    P::Exchange                                             exchange;
    P::Streaming                                            stream;
    std::unique_ptr<ENetHost, decltype(&enet_host_destroy)> host{nullptr, &enet_host_destroy};
    ENetPeer*                                               peer{};
    bool                                                    connected{}, welcomed{}, located{}, policy{}, chatReceived{};
    std::uint64_t                                           self{}, readyGeneration{}, offerRevision{}, removeRevision{}, poseSequence{};
    std::uint64_t                                           chunksSent{}, downloadedBytes{}, uploadId{}, uploadSent{}, uploadAcknowledged{};
    std::uint32_t                                           windowChunks{};
    std::size_t                                             largestPose{};
    std::string                                             modelHash;
    std::unordered_set<std::uint64_t>                       requests;

    Client(std::uint16_t port, const std::filesystem::path& cache, bool publish) : stream(exchange, cache)
    {
      P::ViewSettings settings;
      settings.publish   = publish;
      settings.maximum   = 1;
      settings.timeoutMs = 3000;
      exchange.Configure(settings);
      host.reset(enet_host_create(nullptr, 1, 5, 0, 0));
      REQUIRE(host);
      ENetAddress address{};
      REQUIRE(enet_address_set_host_ip(&address, "127.0.0.1") == 0);
      address.port = port;
      peer         = enet_host_connect(host.get(), &address, 5, 0);
      REQUIRE(peer);
    }

    void Send(std::uint8_t lane, std::span<const std::uint8_t> bytes, enet_uint32 flags)
    {
      auto* packet = enet_packet_create(bytes.data(), bytes.size(), flags);
      REQUIRE(packet);
      if (enet_peer_send(peer, lane, packet) != 0)
      {
        enet_packet_destroy(packet);
        FAIL("Native ENet rejected smoke packet");
      }
    }

    template <class F>
    void Command(std::uint64_t id, F fill, std::uint8_t lane = 0)
    {
      Chat::ClientPacket packet;
      packet.set_protocol_version(Dreamsleeve::Client::Wire::Version);
      packet.set_request_id(id);
      fill(packet);
      P::Bytes bytes(packet.ByteSizeLong());
      REQUIRE(packet.SerializeToArray(bytes.data(), static_cast<int>(bytes.size())));
      Send(lane, bytes, ENET_PACKET_FLAG_RELIABLE);
    }

    void Open(std::string name)
    {
      name.resize(43, '_');
      Command(1, [&](auto& packet) { packet.mutable_open_session()->set_session_ticket(name); });
    }

    void Locate()
    {
      Command(2, [](auto& packet) { packet.mutable_update_player()->mutable_begin_character()->set_name("Native UDP"); });
      Command(10, [](auto& packet) {
        auto* update = packet.mutable_update_player()->mutable_set_location();
        update->set_context_revision(10);
        auto* location = update->mutable_location();
        auto* space    = location->mutable_location();
        space->mutable_location_id()->set_plugin_name("Skyrim.esm");
        space->mutable_location_id()->set_local_form_id(60);
        space->set_location_name("Whiterun");
        location->mutable_position();
        location->mutable_rotation();
      });
    }

    void Receive(const ENetEvent& event)
    {
      auto       packet   = std::unique_ptr<ENetPacket, decltype(&enet_packet_destroy)>(event.packet, &enet_packet_destroy);
      const auto reliable = (packet->flags & ENET_PACKET_FLAG_RELIABLE) != 0;
      const std::span<const std::uint8_t> bytes(packet->data, packet->dataLength);
      if (event.channelID == P::Wire::PosesLane)
      {
        if (bytes.empty())
        {
          REQUIRE(reliable);
          return;
        }
        REQUIRE_FALSE(reliable);
        REQUIRE((packet->flags & ENET_PACKET_FLAG_UNSEQUENCED) == 0);
        Models::ServerPosePacket value;
        REQUIRE(value.ParseFromArray(bytes.data(), static_cast<int>(bytes.size())));
        REQUIRE(value.protocol_version() == 21);
        REQUIRE(value.player_id() == 1);
        largestPose  = (std::max)(largestPose, bytes.size());
        poseSequence = value.sample().sequence();
        REQUIRE(stream.ReceivePose(bytes, NowUs()));
      }
      else if (event.channelID == P::Wire::ModelsLane)
      {
        REQUIRE(reliable);
        Models::ServerAssetPacket value;
        REQUIRE(value.ParseFromArray(bytes.data(), static_cast<int>(bytes.size())));
        REQUIRE(value.protocol_version() == 21);
        if (value.has_policy())
        {
          policy       = true;
          windowChunks = value.policy().window_chunks();
        }
        if (value.has_transfer())
        {
          REQUIRE(requests.contains(value.transfer().request_id()));
          REQUIRE(value.transfer().player_id() == 1);
          if (value.transfer().upload())
          {
            uploadId   = value.transfer().transfer_id();
            uploadSent = uploadAcknowledged = 0;
          }
        }
        if (value.has_progress() && value.progress().transfer_id() == uploadId)
        {
          REQUIRE(value.progress().next_offset() <= uploadSent);
          uploadAcknowledged = value.progress().next_offset();
        }
        if (value.has_complete())
        {
          REQUIRE(requests.contains(value.complete().request_id()));
          if (value.complete().accepted() && value.complete().upload()) readyGeneration = value.complete().generation();
        }
        if (value.has_offer()) offerRevision = value.offer().view_revision();
        if (value.has_remove()) removeRevision = value.remove().view_revision();
        if (value.has_chunk()) downloadedBytes += value.chunk().data().size();
        REQUIRE(stream.ReceiveAsset(bytes));
      }
      else if (event.channelID <= 1)
      {
        REQUIRE(reliable);
        Chat::ServerPacket value;
        REQUIRE(value.ParseFromArray(bytes.data(), static_cast<int>(bytes.size())));
        REQUIRE(value.protocol_version() == 21);
        if (value.has_session_opened())
        {
          welcomed = true;
          self     = value.session_opened().self_player_id();
        }
        if (value.request_id() == 10 && value.has_player_update_accepted())
        {
          located = true;
          stream.Context(10, true);
        }
        if (value.has_chat_published() && value.chat_published().message().text() == "native-streaming-udp") chatReceived = true;
      }
    }

    void Pump()
    {
      ENetEvent event{};
      for (int count = 0; count < 128; ++count)
      {
        const auto result = enet_host_service(host.get(), &event, 0);
        REQUIRE(result >= 0);
        if (result == 0) break;
        if (event.type == ENET_EVENT_TYPE_CONNECT) connected = true;
        if (event.type == ENET_EVENT_TYPE_DISCONNECT)
        {
          connected = false;
          FAIL("Server disconnected smoke peer");
        }
        if (event.type == ENET_EVENT_TYPE_RECEIVE) Receive(event);
      }
      if (welcomed && policy)
        for (const auto& value : stream.Poll())
        {
          if (value.lane == P::Wire::ModelsLane)
          {
            Models::ClientAssetPacket request;
            REQUIRE(request.ParseFromArray(value.bytes.data(), static_cast<int>(value.bytes.size())));
            if (request.has_publish())
            {
              REQUIRE(request.publish().request_id() > 0);
              requests.insert(request.publish().request_id());
              P::Digest digest{};
              REQUIRE(request.publish().asset().hash().size() == digest.size());
              std::memcpy(digest.data(), request.publish().asset().hash().data(), digest.size());
              modelHash = P::Hex(digest);
            }
            if (request.has_download())
            {
              REQUIRE(request.download().request_id() > 0);
              requests.insert(request.download().request_id());
            }
            if (request.has_chunk())
            {
              REQUIRE(request.chunk().transfer_id() == uploadId);
              REQUIRE(request.chunk().offset() == uploadSent);
              uploadSent += request.chunk().data().size();
              REQUIRE(uploadSent - uploadAcknowledged <= P::Wire::ChunkBytes * windowChunks);
              ++chunksSent;
            }
            Send(value.lane, value.bytes, ENET_PACKET_FLAG_RELIABLE);
          }
          else
          {
            REQUIRE(value.lane == P::Wire::PosesLane);
            auto wrapped = DreamNetPeer::TryFromNative(peer);
            REQUIRE(wrapped);
            REQUIRE(wrapped->RotateUnreliableSequence(value.lane));
            Send(value.lane, value.bytes, ENET_PACKET_FLAG_UNRELIABLE_FRAGMENT);
          }
        }
      enet_host_flush(host.get());
    }
  };

  template <class F>
  void Await(Client& alice, Client& bob, std::string_view phase, F ready)
  {
    const auto deadline = Clock::now() + std::chrono::seconds(20);
    while (!ready() && Clock::now() < deadline)
    {
      alice.Pump();
      bob.Pump();
      std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    INFO("phase: ", phase, "; alice error: ", alice.exchange.Stats().error, "; bob error: ", bob.exchange.Stats().error);
    REQUIRE(ready());
  }

  bool Loaded(Client& client, std::uint64_t generation)
  {
    const auto remote = client.exchange.Find(1);
    return remote && remote->State() == P::Representation::Ready && remote->Asset() && remote->descriptor.generation.value == generation;
  }

  bool Played(Client& client, std::uint64_t sequence)
  {
    const auto remote = client.exchange.Find(1);
    if (!remote) return false;
    const auto pose = remote->playback.At(NowUs(), client.exchange.Settings());
    return pose && pose->sequence.value == sequence;
  }

}

// Opt-in: normal pure test runs need no listening server. run.ps1 starts the
// controlled-auth production server fixture and requires the success sentinel.
TEST_CASE("Phantom production Streaming real UDP smoke" * doctest::skip(!Environment("DREAMSLEEVE_PHANTOM_SMOKE_PORT", 5)))
{
  REQUIRE(Dreamsleeve::Client::Wire::Version == 21);
  const auto portText = Environment("DREAMSLEEVE_PHANTOM_SMOKE_PORT", 5);
  REQUIRE(portText);
  const auto port = std::stoul(*portText);
  REQUIRE(port > 0);
  REQUIRE(port <= 65535);
  const auto statePath = Environment("DREAMSLEEVE_PHANTOM_SMOKE_STATE");
  REQUIRE(statePath);

  struct Network
  {
    Network()
    {
      REQUIRE(enet_initialize() == 0);
    }

    ~Network()
    {
      enet_deinitialize();
    }
  } network;

  const std::filesystem::path directory(*statePath);
  Client                      alice(static_cast<std::uint16_t>(port), directory / "alice-cache", true);
  Client                      bob(static_cast<std::uint16_t>(port), directory / "bob-cache", false);
  Await(alice, bob, "connect", [&] { return alice.connected && bob.connected; });
  CHECK_FALSE(alice.policy);
  CHECK_FALSE(bob.policy);
  alice.Open("alice");
  bob.Open("bob");
  Await(alice, bob, "auth/policy", [&] { return alice.welcomed && bob.welcomed && alice.policy && bob.policy; });
  REQUIRE(alice.self == 1);
  REQUIRE(bob.self == 2);
  alice.Locate();
  bob.Locate();
  Await(alice, bob, "movement authority", [&] { return alice.located && bob.located; });
  // No hand-made Prepared completion: Streaming's own Worker consumes admitted
  // Submit and calls Prepared(epoch, localRevision, ...) using production codecs.
  REQUIRE(alice.exchange.Submit(P::Generation{1}, Model()));
  bob.Command(
    11,
    [](auto& packet) {
      auto* chat = packet.mutable_send_chat();
      chat->set_channel_id(1);
      chat->set_text("native-streaming-udp");
    },
    1);
  Await(alice, bob, "cold native prepare/upload/download/decode", [&] {
    return alice.readyGeneration == 1 && Loaded(bob, 1) && bob.chatReceived;
  });
  REQUIRE(alice.uploadSent > P::Wire::ChunkBytes * alice.windowChunks);
  REQUIRE(alice.chunksSent > alice.windowChunks);
  REQUIRE(bob.downloadedBytes == alice.uploadSent);
  CHECK(bob.exchange.Find(1)->Asset()->Value().nodes.size() == NodeCount);
  CHECK(bob.exchange.Find(1)->Asset()->Value().geometry[0].vertices[1].position.x == 1);
  Await(alice, bob, "atomic cache completion", [&] {
    return std::filesystem::exists(directory / "server-cache" / (alice.modelHash + ".zst")) &&
           std::filesystem::exists(directory / "bob-cache" / (alice.modelHash + ".zst"));
  });

  alice.peer->channels[P::Wire::PosesLane].outgoingUnreliableSequenceNumber = 0xFFFF;
  alice.exchange.Submit(Pose(1, 1));
  Await(alice, bob, "native epoch rollover + fragmented pose decode/playback", [&] { return Played(bob, 1); });
  REQUIRE(bob.largestPose > bob.peer->mtu);
  bob.host->intercept = DropOnePoseFragment;
  dropFragment        = true;
  fragmentDropped     = false;
  alice.exchange.Submit(Pose(1, 2));
  Await(alice, bob, "real incoming fragment loss", [&] { return fragmentDropped; });
  const auto quiet = Clock::now() + std::chrono::milliseconds(200);
  while (Clock::now() < quiet)
  {
    alice.Pump();
    bob.Pump();
    std::this_thread::sleep_for(std::chrono::milliseconds(1));
  }
  CHECK(bob.poseSequence == 1);
  bob.host->intercept = nullptr;
  alice.exchange.Submit(Pose(1, 3));
  Await(alice, bob, "next independent complete pose after loss", [&] { return Played(bob, 3); });

  const auto oldView = bob.offerRevision, downloaded = bob.downloadedBytes;
  auto       settings = bob.exchange.Settings();
  settings.receive    = false;
  bob.exchange.Configure(settings);
  Await(alice, bob, "receive=false authority revocation", [&] { return bob.removeRevision > oldView; });
  CHECK_FALSE(bob.exchange.Find(1));
  settings.receive = true;
  bob.exchange.Configure(settings);
  Await(alice, bob, "reentry uses native disk cache", [&] { return Loaded(bob, 1) && bob.offerRevision > bob.removeRevision; });
  CHECK(bob.downloadedBytes == downloaded);
  REQUIRE(bob.exchange.Stats().cacheHits > 0);

  const auto sent = alice.chunksSent;
  REQUIRE(alice.exchange.Submit(P::Generation{2}, Model()));
  Await(alice, bob, "warm server publication and native cached generation", [&] { return alice.readyGeneration == 2 && Loaded(bob, 2); });
  CHECK(alice.chunksSent == sent);
  CHECK(bob.downloadedBytes == downloaded);
  alice.exchange.Submit(Pose(2, 1));
  Await(alice, bob, "new generation pose", [&] { return Played(bob, 1); });
  CHECK(alice.exchange.Stats().rejected == 0);
  CHECK(bob.exchange.Stats().rejected == 0);
  std::cout << "PHANTOM_NATIVE_UDP_PASS coldBytes=" << downloaded << " windowChunks=" << alice.windowChunks
            << " fragmentedPoseBytes=" << bob.largestPose << " requestIds=positive loss=discarded rollover=unreliable warmChunks=0\n";
}
