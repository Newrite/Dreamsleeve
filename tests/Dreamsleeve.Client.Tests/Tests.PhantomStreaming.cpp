#include <doctest/doctest.h>
#include "phantom.pb.h"
import std;
import Dreamsleeve.Client.Phantom.Streaming;
import Dreamsleeve.Client.ProtocolCodec;

namespace
{
  namespace P     = Dreamsleeve::Client::Phantom;
  namespace Proto = Dreamsleeve::Protocol::Phantom;
  using Clock     = std::chrono::steady_clock;

  P::PreparedAsset Model()
  {
    P::Asset raw;
    raw.nodes.push_back({});
    P::Geometry mesh;
    mesh.vertices.resize(3);
    mesh.vertices[1].position = {1, 0, 0};
    mesh.vertices[2].position = {0, 1, 0};
    mesh.indices              = {0, 1, 2};
    raw.geometry.push_back(std::move(mesh));
    auto validated = P::ValidatedAsset::Parse(std::move(raw));
    REQUIRE(validated);
    auto result = P::Prepare(std::move(*validated));
    REQUIRE(result);
    return std::move(*result);
  }

  P::Wire::Descriptor Describe(const P::PreparedAsset& model)
  {
    return {model.hash, P::Generation{1}, P::AssetVersion, static_cast<std::uint32_t>(model.compressed->size()), model.rawBytes, 1, 1};
  }

  void Set(const P::Wire::Descriptor& asset, Proto::AssetDescriptor* out)
  {
    out->set_hash(asset.hash.data(), asset.hash.size());
    out->set_generation(asset.generation.value);
    out->set_format_version(asset.format);
    out->set_compressed_bytes(asset.compressedBytes);
    out->set_raw_bytes(asset.rawBytes);
    out->set_channels(asset.channels);
    out->set_geometry(asset.geometry);
  }

  P::Bytes Server(const std::function<void(Proto::ServerAssetPacket&)>& fill)
  {
    Proto::ServerAssetPacket packet;
    packet.set_protocol_version(Dreamsleeve::Client::Wire::Version);
    fill(packet);
    P::Bytes result(packet.ByteSizeLong());
    REQUIRE(packet.SerializeToArray(result.data(), static_cast<int>(result.size())));
    return result;
  }

  void Policy(P::Streaming& stream, std::uint32_t concurrent = 2, std::uint32_t window = 4, std::uint32_t visible = 4)
  {
    auto packet = Server([&](auto& packet) {
      auto*     p = packet.mutable_policy();
      P::Limits limits;
      p->set_enabled(true);
      p->set_raw_asset_bytes(limits.assetBytes);
      p->set_compressed_asset_bytes(limits.compressedAssetBytes);
      p->set_channels(limits.nodes);
      p->set_geometry(limits.geometry);
      p->set_pose_bytes(limits.poseBytes);
      p->set_compressed_pose_bytes(limits.compressedPoseBytes);
      p->set_sample_rate(20);
      p->set_maximum_visible(visible);
      p->set_distance(4096);
      p->set_window_chunks(window);
      p->set_concurrent_transfers(concurrent);
      p->set_model_bytes_per_second(1024 * 1024);
      p->set_pose_bytes_per_second(1024 * 1024);
    });
    REQUIRE(stream.ReceiveAsset(packet));
    stream.Context(1, true);
  }

  std::vector<Proto::ClientAssetPacket> Models(std::vector<P::Outbound> output)
  {
    std::vector<Proto::ClientAssetPacket> result;
    for (const auto& value : output)
      if (value.lane == P::Wire::ModelsLane)
      {
        Proto::ClientAssetPacket packet;
        REQUIRE(packet.ParseFromArray(value.bytes.data(), static_cast<int>(value.bytes.size())));
        result.push_back(std::move(packet));
      }
    return result;
  }

  template <class F>
  bool Until(F check)
  {
    const auto end = Clock::now() + std::chrono::seconds(2);
    do
    {
      if (check()) return true;
      std::this_thread::sleep_for(std::chrono::milliseconds(2));
    }
    while (Clock::now() < end);
    return false;
  }

  void PreparePublication(P::Exchange& exchange, std::shared_ptr<const P::PreparedAsset> model)
  {
    exchange.Context(1, true);
    REQUIRE(exchange.Submit(P::Generation{1}, model->asset));
    auto work = exchange.TakeWork();
    REQUIRE(work.capture);
    exchange.Prepared(work.epoch, work.localRevision, {P::Generation{1}, std::move(model)});
  }

  P::Wire::Pose Pose(const P::PreparedAsset& model)
  {
    P::Snapshot value;
    value.generation  = {1};
    value.sequence    = {1};
    value.context     = 1;
    value.sampledAtUs = 50000;
    value.channels.push_back({});
    value.bounds.push_back({
        {0, 0, 0},
        1
    });
    auto bytes = P::WriteSnapshot(value, model.asset);
    REQUIRE(bytes);
    return {value.generation, value.context, value.sequence, value.sampledAtUs, std::move(*bytes)};
  }

}

TEST_CASE("Phantom upload retries correlated admission and sends poses only after acceptance")
{
  P::Exchange exchange;
  auto        model = std::make_shared<const P::PreparedAsset>(Model());
  PreparePublication(exchange, model);
  P::Streaming stream(exchange, {});
  Policy(stream);
  const auto initial = Models(stream.Poll());
  REQUIRE(std::ranges::any_of(initial, [](const auto& p) { return p.has_publish(); }));
  exchange.Encoded(exchange.Epoch(), Pose(*model));
  REQUIRE(stream.ReceiveAsset(Server([](auto& p) {
    auto* c = p.mutable_complete();
    c->set_request_id(1);
    c->set_player_id(123);
    c->set_generation(1);
    c->set_upload(true);
    c->set_reason("busy");
    c->set_retry_after_ms(5);
  })));
  CHECK(std::ranges::none_of(stream.Poll(), [](const auto& p) { return p.lane == P::Wire::PosesLane; }));
  REQUIRE(Until([&] { return std::ranges::any_of(Models(stream.Poll()), [](const auto& p) { return p.has_publish(); }); }));
  const auto descriptor = Describe(*model);
  REQUIRE(stream.ReceiveAsset(Server([&](auto& p) {
    auto* t = p.mutable_transfer();
    t->set_transfer_id(10);
    t->set_request_id(2);
    t->set_player_id(123);
    t->set_upload(true);
    Set(descriptor, t->mutable_asset());
  })));
  P::Bytes received;
  REQUIRE(Until([&] {
    for (const auto& p : Models(stream.Poll()))
      if (p.has_chunk())
      {
        CHECK(p.chunk().offset() == received.size());
        received.insert(received.end(), p.chunk().data().begin(), p.chunk().data().end());
      }
    return received.size() == model->compressed->size();
  }));
  CHECK(received == *model->compressed);
  REQUIRE(stream.ReceiveAsset(Server([](auto& p) {
    p.mutable_complete()->set_request_id(2);
    p.mutable_complete()->set_transfer_id(10);
    p.mutable_complete()->set_accepted(true);
  })));
  const auto ready = stream.Poll();
  CHECK(std::ranges::any_of(ready, [](const auto& p) { return p.lane == P::Wire::PosesLane; }));
  stream.Context(2, false);
  exchange.Encoded(exchange.Epoch(), Pose(*model));
  const auto stopped = stream.Poll();
  CHECK(std::ranges::none_of(stopped, [](const auto& p) { return p.lane == P::Wire::PosesLane; }));
  CHECK(std::ranges::any_of(Models(stopped), [](const auto& p) { return p.has_withdraw(); }));
}

TEST_CASE("Phantom downloads validate complete bytes and stale worker completion cannot restore a removed view")
{
  P::Exchange  exchange;
  P::Streaming stream(exchange, {});
  Policy(stream);
  stream.Poll();
  auto       model      = Model();
  const auto descriptor = Describe(model);
  auto       offer      = Server([&](auto& p) {
    auto* o = p.mutable_offer();
    o->set_player_id(9);
    o->set_view_revision(1);
    Set(descriptor, o->mutable_asset());
  });
  REQUIRE(stream.ReceiveAsset(offer));
  REQUIRE(Until([&] { return std::ranges::any_of(Models(stream.Poll()), [](const auto& p) { return p.has_download(); }); }));
  REQUIRE(stream.ReceiveAsset(Server([&](auto& p) {
    auto* t = p.mutable_transfer();
    t->set_transfer_id(20);
    t->set_request_id(1);
    t->set_player_id(9);
    Set(descriptor, t->mutable_asset());
  })));
  REQUIRE(stream.ReceiveAsset(Server([&](auto& p) {
    auto* c = p.mutable_chunk();
    c->set_transfer_id(20);
    c->set_data(model.compressed->data(), model.compressed->size());
  })));
  REQUIRE(stream.ReceiveAsset(Server([](auto& p) {
    p.mutable_complete()->set_request_id(1);
    p.mutable_complete()->set_transfer_id(20);
    p.mutable_complete()->set_accepted(true);
  })));
  REQUIRE(Until([&] {
    const auto remote = exchange.Find(9);
    return remote && remote->Asset();
  }));
  CHECK(exchange.Find(9)->Asset()->Value().geometry[0].vertices[1].position.x == 1);
  REQUIRE(stream.ReceiveAsset(Server([](auto& p) {
    auto* r = p.mutable_remove();
    r->set_player_id(9);
    r->set_view_revision(2);
  })));
  CHECK_FALSE(exchange.Find(9));
  REQUIRE(stream.ReceiveAsset(offer));
  REQUIRE(Until([&] { return std::ranges::any_of(Models(stream.Poll()), [](const auto& p) { return p.has_download(); }); }));
  REQUIRE(stream.ReceiveAsset(Server([&](auto& p) {
    auto* t = p.mutable_transfer();
    t->set_transfer_id(21);
    t->set_request_id(2);
    t->set_player_id(9);
    Set(descriptor, t->mutable_asset());
  })));
  REQUIRE(stream.ReceiveAsset(Server([&](auto& p) {
    auto* c = p.mutable_chunk();
    c->set_transfer_id(21);
    c->set_data(model.compressed->data(), model.compressed->size() - 1);
  })));
  REQUIRE(stream.ReceiveAsset(Server([](auto& p) {
    p.mutable_complete()->set_request_id(2);
    p.mutable_complete()->set_transfer_id(21);
    p.mutable_complete()->set_accepted(true);
  })));
  CHECK_FALSE(exchange.Find(9)->Asset());
  CHECK(exchange.Find(9)->State() == P::Representation::Unavailable);
  stream.Reset();
  std::this_thread::sleep_for(std::chrono::milliseconds(20));
  CHECK(exchange.Read().remotes.empty());
}

TEST_CASE("Late phantom admission replies do not cancel a newer context with the same model")
{
  P::Exchange exchange;
  auto        model = std::make_shared<const P::PreparedAsset>(Model());
  PreparePublication(exchange, model);
  P::Streaming stream(exchange, {});
  Policy(stream);
  auto requests = Models(stream.Poll());
  REQUIRE(std::ranges::any_of(requests, [](const auto& p) { return p.has_publish() && p.publish().request_id() == 1; }));
  stream.Context(2, true);
  requests = Models(stream.Poll());
  REQUIRE(std::ranges::any_of(requests, [](const auto& p) { return p.has_publish() && p.publish().request_id() == 2; }));
  REQUIRE(stream.ReceiveAsset(Server([](auto& p) {
    auto* c = p.mutable_complete();
    c->set_request_id(1);
    c->set_player_id(123);
    c->set_generation(1);
    c->set_upload(true);
    c->set_reason("context changed");
  })));
  const auto descriptor = Describe(*model);
  REQUIRE(stream.ReceiveAsset(Server([&](auto& p) {
    auto* t = p.mutable_transfer();
    t->set_request_id(2);
    t->set_transfer_id(30);
    t->set_player_id(123);
    t->set_upload(true);
    Set(descriptor, t->mutable_asset());
  })));
  REQUIRE(Until([&] {
    return std::ranges::any_of(Models(stream.Poll()), [](const auto& p) { return p.has_chunk() && p.chunk().transfer_id() == 30; });
  }));
  CHECK(exchange.Stats().rejected == 0);
}

TEST_CASE("Scene allocations and simultaneous replacement share the phantom memory budget")
{
  P::Exchange     exchange;
  P::ViewSettings settings;
  settings.memoryBytes = 64 * 1024 * 1024;
  settings.maximum     = 4;
  exchange.Configure(settings);
  P::Wire::Offer offer{
      1,
      1,
      {P::Digest{}, P::Generation{1}, 1, 1024 * 1024, 1024 * 1024, 1, 1}
  };
  REQUIRE(exchange.Offer(offer));
  const auto free = exchange.RemainingMemory();
  REQUIRE(exchange.SceneMemory(1, free));
  CHECK(exchange.RemainingMemory() == 0);
  CHECK_FALSE(exchange.SceneMemory(1, free + 1));
  offer.asset.generation = {2};
  offer.view             = 2;
  CHECK(exchange.Offer(offer));
  CHECK(exchange.RemainingMemory() == 0);
  CHECK(exchange.SceneMemory(1, 0));
  CHECK(exchange.RemainingMemory() == free);
  exchange.Remove({1, 2});
  CHECK(exchange.RemainingMemory() == settings.memoryBytes);
}

TEST_CASE("Interrupted phantom download retries instead of becoming permanently unavailable")
{
  P::Exchange  exchange;
  P::Streaming stream(exchange, {});
  Policy(stream);
  stream.Poll();
  auto       model      = Model();
  const auto descriptor = Describe(model);
  REQUIRE(stream.ReceiveAsset(Server([&](auto& p) {
    auto* o = p.mutable_offer();
    o->set_player_id(9);
    o->set_view_revision(1);
    Set(descriptor, o->mutable_asset());
  })));
  REQUIRE(Until([&] {
    return std::ranges::any_of(Models(stream.Poll()), [](const auto& p) { return p.has_download() && p.download().request_id() == 1; });
  }));
  REQUIRE(stream.ReceiveAsset(Server([&](auto& p) {
    auto* t = p.mutable_transfer();
    t->set_transfer_id(20);
    t->set_request_id(1);
    t->set_player_id(9);
    Set(descriptor, t->mutable_asset());
  })));
  REQUIRE(stream.ReceiveAsset(Server([](auto& p) {
    auto* c = p.mutable_complete();
    c->set_request_id(1);
    c->set_transfer_id(20);
    c->set_reason("busy");
    c->set_retry_after_ms(5);
  })));
  REQUIRE(Until([&] {
    return std::ranges::any_of(Models(stream.Poll()), [](const auto& p) { return p.has_download() && p.download().request_id() == 2; });
  }));
  CHECK(exchange.Find(9)->State() == P::Representation::Loading);
}

TEST_CASE("Phantom upload and download use a shared transfer admission limit")
{
  P::Exchange exchange;
  auto        model = std::make_shared<const P::PreparedAsset>(Model());
  PreparePublication(exchange, model);
  P::Streaming stream(exchange, {});
  Policy(stream, 1);
  REQUIRE(std::ranges::any_of(Models(stream.Poll()), [](const auto& p) { return p.has_publish(); }));
  const auto descriptor = Describe(*model);
  REQUIRE(stream.ReceiveAsset(Server([&](auto& p) {
    auto* o = p.mutable_offer();
    o->set_player_id(9);
    o->set_view_revision(1);
    Set(descriptor, o->mutable_asset());
  })));
  for (int i = 0; i < 20; ++i)
  {
    CHECK(std::ranges::none_of(Models(stream.Poll()), [](const auto& p) { return p.has_download(); }));
    std::this_thread::sleep_for(std::chrono::milliseconds(2));
  }
  REQUIRE(stream.ReceiveAsset(Server([](auto& p) {
    auto* c = p.mutable_complete();
    c->set_request_id(1);
    c->set_player_id(123);
    c->set_generation(1);
    c->set_upload(true);
    c->set_reason("denied");
  })));
  REQUIRE(Until([&] { return std::ranges::any_of(Models(stream.Poll()), [](const auto& p) { return p.has_download(); }); }));
}

TEST_CASE("All accepted phantom cache misses reach the download planner")
{
  P::Exchange     exchange;
  P::ViewSettings settings;
  settings.maximum = 16;
  exchange.Configure(settings);
  P::Worker  worker(exchange, {});
  auto       model      = Model();
  const auto descriptor = Describe(model);
  for (std::uint64_t id = 1; id <= 9; ++id)
  {
    P::Wire::Offer offer{id, 1, descriptor};
    REQUIRE(exchange.Offer(offer));
    REQUIRE(worker.Queue(offer));
    std::this_thread::sleep_for(std::chrono::milliseconds(15));
  }
  std::set<std::uint64_t> missed;
  REQUIRE(Until([&] {
    for (const auto& offer : worker.TakeMissing())
      missed.insert(offer.player);
    return missed.size() == 9;
  }));
}

TEST_CASE("A local download budget paces acknowledgements fairly across transfers")
{
  P::Exchange     exchange;
  P::ViewSettings settings;
  settings.downloadBytesPerSecond = 64 * 1024;
  exchange.Configure(settings);
  P::Streaming stream(exchange, {});
  Policy(stream);
  stream.Poll();
  auto descriptor            = Describe(Model());
  descriptor.compressedBytes = 2 * P::Wire::ChunkBytes;
  descriptor.rawBytes        = 4 * P::Wire::ChunkBytes;
  for (std::uint64_t id : {9, 10})
    REQUIRE(stream.ReceiveAsset(Server([&](auto& p) {
      auto* offer = p.mutable_offer();
      offer->set_player_id(id);
      offer->set_view_revision(1);
      Set(descriptor, offer->mutable_asset());
    })));
  std::map<std::uint64_t, std::uint64_t> requests;
  REQUIRE(Until([&] {
    for (const auto& p : Models(stream.Poll()))
      if (p.has_download()) requests[p.download().player_id()] = p.download().request_id();
    return requests.size() == 2;
  }));
  for (const auto& [player, request] : requests)
  {
    REQUIRE(stream.ReceiveAsset(Server([&](auto& p) {
      auto* transfer = p.mutable_transfer();
      transfer->set_player_id(player);
      transfer->set_transfer_id(player);
      transfer->set_request_id(request);
      Set(descriptor, transfer->mutable_asset());
    })));
    REQUIRE(stream.ReceiveAsset(Server([&](auto& p) {
      p.mutable_chunk()->set_transfer_id(player);
      p.mutable_chunk()->set_data(std::string(P::Wire::ChunkBytes, 'x'));
    })));
  }
  const auto              started = Clock::now();
  std::set<std::uint64_t> acknowledged;
  REQUIRE(Until([&] {
    for (const auto& p : Models(stream.Poll()))
      if (p.has_progress())
      {
        CHECK(p.progress().next_offset() == P::Wire::ChunkBytes);
        acknowledged.insert(p.progress().transfer_id());
      }
    return acknowledged.size() == 2;
  }));
  CHECK(Clock::now() - started >= std::chrono::milliseconds(400));
}

TEST_CASE("Eight full phantom windows make partial ACK progress below the inactivity timeout")
{
  constexpr std::uint32_t count = 8, window = 16, rate = 64 * 1024;
  P::Exchange             exchange;
  P::ViewSettings         settings;
  settings.maximum                = count;
  settings.memoryBytes            = 1024ULL * 1024 * 1024;
  settings.downloadBytesPerSecond = rate;
  exchange.Configure(settings);
  P::Streaming stream(exchange, {});
  Policy(stream, count, window, count);
  stream.Poll();
  auto descriptor            = Describe(Model());
  descriptor.compressedBytes = 1024 * 1024;
  descriptor.rawBytes        = 4 * 1024 * 1024;
  for (std::uint64_t player = 1; player <= count; ++player)
    REQUIRE(stream.ReceiveAsset(Server([&](auto& p) {
      auto* offer = p.mutable_offer();
      offer->set_player_id(player);
      offer->set_view_revision(1);
      Set(descriptor, offer->mutable_asset());
    })));
  std::map<std::uint64_t, std::uint64_t> requests;
  REQUIRE(Until([&] {
    for (const auto& p : Models(stream.Poll()))
      if (p.has_download()) requests[p.download().player_id()] = p.download().request_id();
    return requests.size() == count;
  }));
  for (const auto& [player, request] : requests)
  {
    REQUIRE(stream.ReceiveAsset(Server([&](auto& p) {
      auto* transfer = p.mutable_transfer();
      transfer->set_player_id(player);
      transfer->set_transfer_id(player);
      transfer->set_request_id(request);
      Set(descriptor, transfer->mutable_asset());
    })));
    for (std::uint32_t chunk = 0; chunk < window; ++chunk)
      REQUIRE(stream.ReceiveAsset(Server([&](auto& p) {
        auto* data = p.mutable_chunk();
        data->set_transfer_id(player);
        data->set_offset(chunk * P::Wire::ChunkBytes);
        data->set_data(std::string(P::Wire::ChunkBytes, 'x'));
      })));
  }
  const auto started = Clock::now();
  std::map<std::uint64_t, std::uint32_t> acknowledged;
  std::uint64_t initialBytes{}, totalBytes{};
  for (int second = 0; second <= 31; ++second)
  {
    for (const auto& p : Models(stream.Poll(started + std::chrono::seconds(second))))
    {
      CHECK_FALSE(p.has_cancel());
      CHECK_FALSE(p.has_download());
      if (!p.has_progress()) continue;
      const auto id = p.progress().transfer_id();
      REQUIRE(requests.contains(id));
      const auto offset = p.progress().next_offset();
      CHECK(offset > acknowledged[id]);
      CHECK(offset <= window * P::Wire::ChunkBytes);
      CHECK(offset % P::Wire::ChunkBytes == 0);
      totalBytes += offset - acknowledged[id];
      acknowledged[id] = offset;
      if (second <= 2) CHECK(offset < window * P::Wire::ChunkBytes);
    }
    if (!second) initialBytes = totalBytes;
    // At most one fractional chunk of saved credit per download crosses
    // the test's initial clock boundary; aggregate pacing stays bounded.
    CHECK(totalBytes - initialBytes <= std::uint64_t(rate) * second + count * P::Wire::ChunkBytes);
    if (second == 2)
    {
      REQUIRE(acknowledged.size() == count);
      for (const auto& [id, offset] : acknowledged) CHECK(offset >= P::Wire::ChunkBytes);
    }
  }
  REQUIRE(acknowledged.size() == count);
  for (const auto& [id, offset] : acknowledged) CHECK(offset >= 15 * P::Wire::ChunkBytes);
  CHECK(totalBytes >= std::uint64_t(count) * 15 * P::Wire::ChunkBytes);
}

TEST_CASE("Phantom ACK pacing permits the final prefix off a chunk boundary")
{
  P::Exchange     exchange;
  P::ViewSettings settings;
  settings.downloadBytesPerSecond = 64 * 1024;
  exchange.Configure(settings);
  P::Streaming stream(exchange, {});
  Policy(stream);
  stream.Poll();
  auto descriptor            = Describe(Model());
  descriptor.compressedBytes = P::Wire::ChunkBytes + 123;
  descriptor.rawBytes        = 4 * P::Wire::ChunkBytes;
  REQUIRE(stream.ReceiveAsset(Server([&](auto& p) {
    auto* offer = p.mutable_offer();
    offer->set_player_id(9);
    offer->set_view_revision(1);
    Set(descriptor, offer->mutable_asset());
  })));
  std::uint64_t request{};
  REQUIRE(Until([&] {
    for (const auto& p : Models(stream.Poll()))
      if (p.has_download()) request = p.download().request_id();
    return request != 0;
  }));
  REQUIRE(stream.ReceiveAsset(Server([&](auto& p) {
    auto* transfer = p.mutable_transfer();
    transfer->set_player_id(9);
    transfer->set_transfer_id(9);
    transfer->set_request_id(request);
    Set(descriptor, transfer->mutable_asset());
  })));
  for (std::uint32_t offset : {0U, P::Wire::ChunkBytes})
    REQUIRE(stream.ReceiveAsset(Server([&](auto& p) {
      auto* chunk = p.mutable_chunk();
      chunk->set_transfer_id(9);
      chunk->set_offset(offset);
      chunk->set_data(std::string(offset ? 123 : P::Wire::ChunkBytes, 'x'));
    })));
  const auto output = Models(stream.Poll(Clock::now() + std::chrono::seconds(1)));
  CHECK(std::ranges::any_of(output, [&](const auto& p) {
    return p.has_progress() && p.progress().transfer_id() == 9 && p.progress().next_offset() == descriptor.compressedBytes;
  }));
  CHECK(std::ranges::none_of(output, [](const auto& p) { return p.has_cancel(); }));
}
