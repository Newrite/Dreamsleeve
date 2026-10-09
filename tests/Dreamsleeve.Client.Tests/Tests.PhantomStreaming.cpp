#include <doctest/doctest.h>
#include "phantom.pb.h"
import std;
import Dreamsleeve.Client.Phantom.Streaming;
import Dreamsleeve.Client.Domain;
import Dreamsleeve.Client.ProtocolCodec;

#include "PhantomFixture.hpp"

namespace
{
  namespace P     = Dreamsleeve::Client::Phantom;
  namespace Proto = Dreamsleeve::Protocol::Phantom;
  using Clock     = std::chrono::steady_clock;

  P::PreparedAsset Model()
  {
    auto raw       = PhantomFixture::Model();
    auto validated = P::ValidatedAsset::Parse(std::move(raw));
    REQUIRE(validated);
    auto result = P::Prepare(std::move(*validated));
    REQUIRE(result);
    return std::move(*result);
  }

  P::Wire::Descriptor Describe(const P::PreparedAsset& model)
  {
    return {model.hash, P::Generation{1}, P::AssetVersion, static_cast<std::uint32_t>(model.compressed->size()), model.rawBytes, 2};
  }

  void Set(const P::Wire::Descriptor& asset, Proto::AssetDescriptor* out)
  {
    out->set_hash(asset.hash.data(), asset.hash.size());
    out->set_generation(asset.generation.value);
    out->set_format_version(asset.format);
    out->set_compressed_bytes(asset.compressedBytes);
    out->set_raw_bytes(asset.rawBytes);
    out->set_channels(asset.channels);
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

  void Policy(
    P::Streaming& stream,
    std::uint32_t concurrent = 2,
    std::uint32_t window     = 4,
    std::uint32_t visible    = 4,
    std::uint32_t rate       = 1024 * 1024)
  {
    auto packet = Server([&](auto& packet) {
      auto*     p = packet.mutable_policy();
      P::Limits limits;
      p->set_enabled(true);
      p->set_raw_asset_bytes(limits.assetBytes);
      p->set_compressed_asset_bytes(limits.compressedAssetBytes);
      p->set_channels(limits.nodes);
      p->set_pose_bytes(limits.poseBytes);
      p->set_compressed_pose_bytes(limits.compressedPoseBytes);
      p->set_sample_rate(20);
      p->set_maximum_visible(visible);
      p->set_distance(4096);
      (void)window;
      p->set_concurrent_transfers(concurrent);
      p->set_model_bytes_per_second(rate);
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

  void PreparePublication(P::Exchange& exchange, std::shared_ptr<const P::PreparedAsset> model, std::uint64_t context = 1)
  {
    exchange.Context(context, true);
    REQUIRE(exchange.Submit(context, P::Generation{1}, model->asset));
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
    value.channels.resize(2);
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
  exchange.Encoded(exchange.Epoch(), exchange.TakeWork().poseRevision, Pose(*model));
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
  REQUIRE(stream.ReceiveAsset(Server([](auto& p) {
    p.mutable_complete()->set_request_id(2);
    p.mutable_complete()->set_player_id(123);
    p.mutable_complete()->set_generation(1);
    p.mutable_complete()->set_upload(true);
    p.mutable_complete()->set_accepted(true);
  })));
  const auto ready = stream.Poll();
  CHECK(std::ranges::any_of(ready, [](const auto& p) { return p.lane == P::Wire::PosesLane; }));
  stream.Context(2, false);
  exchange.Encoded(exchange.Epoch(), exchange.TakeWork().poseRevision, Pose(*model));
  const auto stopped = stream.Poll();
  CHECK(std::ranges::none_of(stopped, [](const auto& p) { return p.lane == P::Wire::PosesLane; }));
  CHECK(std::ranges::any_of(Models(stopped), [](const auto& p) { return p.has_withdraw(); }));
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
  CHECK(std::ranges::none_of(requests, [](const auto& p) { return p.has_publish(); }));
  PreparePublication(exchange, model, 2);
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
  REQUIRE(stream.ReceiveAsset(Server([&](auto& p) {
    auto* c = p.mutable_complete();
    c->set_request_id(2);
    c->set_player_id(123);
    c->set_generation(1);
    c->set_upload(true);
    c->set_accepted(true);
  })));
  exchange.Encoded(exchange.Epoch(), exchange.TakeWork().poseRevision, Pose(*model));
  CHECK(std::ranges::none_of(Models(stream.Poll()), [](const auto& p) { return p.has_publish(); }));
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
      {P::Digest{}, P::Generation{1}, P::AssetVersion, 1024 * 1024, 1024 * 1024, 2}
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
      missed.insert(offer.offer.player);
    return missed.size() == 9;
  }));
}




TEST_CASE("Phantom replacement owns two generations and failed preparation restores the old publication")
{
  P::Exchange exchange;
  auto        model = std::make_shared<const P::PreparedAsset>(Model());
  PreparePublication(exchange, model);
  CHECK_FALSE(exchange.CanReplace());
  exchange.Settled({P::Generation{1}, 2});
  CHECK_FALSE(exchange.CanReplace());
  exchange.Settled({P::Generation{1}, 1});
  REQUIRE(exchange.CanReplace());
  REQUIRE(exchange.Submit(1, P::Generation{2}, model->asset));
  auto work = exchange.TakeWork();
  CHECK(work.previousGeneration == P::Generation{1});
  CHECK(exchange.Capturing(P::Generation{1}));
  CHECK_FALSE(exchange.Submit(1, P::Generation{3}, model->asset));

  exchange.PreparationFailed(work.epoch, work.localRevision, "test preparation failure");
  CHECK(exchange.Capturing(P::Generation{1}));
  CHECK_FALSE(exchange.Capturing(P::Generation{2}));
  CHECK(exchange.CanReplace());
  auto restored = exchange.TakeWork();
  CHECK(restored.generation == P::Generation{1});
  CHECK_FALSE(restored.previousGeneration);

  REQUIRE(exchange.Submit(1, P::Generation{2}, model->asset));
  exchange.RestartCapture();
  CHECK_FALSE(exchange.Capturing(P::Generation{1}));
  CHECK_FALSE(exchange.Capturing(P::Generation{2}));
  CHECK(exchange.CanReplace());
  CHECK_FALSE(exchange.TakeOutput().generation);
  REQUIRE(exchange.Submit(1, P::Generation{3}, model->asset));
}

TEST_CASE("Phantom remote replacement retains live previous poses and accounts its memory until display")
{
  P::Exchange exchange;
  exchange.Context(1, true);
  const auto     model = Model();
  P::Wire::Offer first{1, 1, Describe(model)};
  REQUIRE(exchange.Offer(first));
  exchange.Loaded(exchange.Epoch(), first, std::make_shared<const P::ValidatedAsset>(model.asset), false);
  const auto before       = exchange.RemainingMemory();
  auto       second       = first;
  second.view             = 2;
  second.asset.generation = {2};
  REQUIRE(exchange.Offer(second));
  REQUIRE(exchange.Find(1)->previous);
  CHECK(exchange.RemainingMemory() < before);
  const auto occupied = exchange.RemainingMemory();
  REQUIRE(exchange.SceneMemory(1, occupied));
  CHECK(exchange.RemainingMemory() == 0);
  CHECK_FALSE(exchange.SceneMemory(1, occupied + 1));
  auto wire = Pose(model);
  auto pose = P::ReadSnapshot(wire.payload, model.asset);
  REQUIRE(pose);
  exchange.Pose(exchange.Epoch(), {1, 2, wire}, std::make_shared<const P::Snapshot>(*pose), 50000);
  CHECK(exchange.Find(1)->previous->playback.At(50000, exchange.Settings()));

  exchange.Displayed({1, 1, {2}});
  CHECK(exchange.Find(1)->previous);
  exchange.Displayed({1, 2, {2}});
  CHECK_FALSE(exchange.Find(1)->previous);
  CHECK(exchange.RemainingMemory() > 0);
  CHECK(exchange.TakeOutput().displayed.size() == 1);
}

TEST_CASE("Phantom replacement waits for RAM without losing its view or old pose")
{
  P::Exchange exchange;
  exchange.Context(1, true);
  const auto     model = Model();
  P::Wire::Offer first{1, 1, Describe(model)};
  REQUIRE(exchange.Offer(first));
  exchange.Loaded(exchange.Epoch(), first, std::make_shared<const P::ValidatedAsset>(model.asset), false);
  REQUIRE(exchange.SceneMemory(1, exchange.RemainingMemory()));
  auto next              = first;
  next.view              = 2;
  next.asset.generation  = {2};
  next.asset.rawBytes   += 8 * 1024 * 1024;
  CHECK_FALSE(exchange.Offer(next));
  const auto waiting = exchange.Find(1);
  REQUIRE(waiting);
  REQUIRE(waiting->previous);
  CHECK(waiting->view == 2);
  CHECK(waiting->WaitingBudget());
  CHECK(waiting->State() == P::Representation::Loading);
  auto wire = Pose(model);
  auto pose = P::ReadSnapshot(wire.payload, model.asset);
  REQUIRE(pose);
  exchange.Pose(exchange.Epoch(), {1, 2, wire}, std::make_shared<const P::Snapshot>(*pose), 50000);
  CHECK(exchange.Find(1)->previous->playback.At(50000, exchange.Settings()));

  REQUIRE(exchange.SceneMemory(1, 0));
  REQUIRE(exchange.Offer(next));
  CHECK_FALSE(exchange.Find(1)->WaitingBudget());
  CHECK(exchange.Find(1)->previous);
}

TEST_CASE("Terminal phantom rejection retains the usable bridge and permits a later appearance")
{
  P::Exchange exchange;
  auto        model = std::make_shared<const P::PreparedAsset>(Model());
  PreparePublication(exchange, model);
  exchange.Settled({P::Generation{1}, 1});
  REQUIRE(exchange.Submit(1, P::Generation{2}, model->asset));
  auto work = exchange.TakeWork();
  exchange.Prepared(work.epoch, work.localRevision, {{2}, model});
  P::Streaming stream(exchange, {});
  Policy(stream);
  const auto packets = Models(stream.Poll());
  const auto publish = std::ranges::find_if(packets, [](const auto& p) { return p.has_publish(); });
  REQUIRE(publish != packets.end());
  REQUIRE(stream.ReceiveAsset(Server([&](auto& p) {
    auto* c = p.mutable_complete();
    c->set_request_id(publish->publish().request_id());
    c->set_player_id(123);
    c->set_generation(2);
    c->set_upload(true);
    c->set_reason("terminal rejection");
  })));
  CHECK(exchange.CanReplace());
  exchange.Settled({P::Generation{2}, 1});  // Delayed settlement cannot promote a rejected candidate.
  auto packet       = Pose(*model);
  packet.previous   = std::make_shared<const P::Wire::Pose>(packet);
  packet.generation = {2};
  exchange.Encoded(exchange.Epoch(), exchange.TakeWork().poseRevision, std::move(packet));
  const auto at       = Clock::now() + std::chrono::seconds(1);
  const auto fallback = stream.Poll(at);
  const auto sent     = std::ranges::find_if(fallback, [](const auto& p) { return p.lane == P::Wire::PosesLane; });
  REQUIRE(sent != fallback.end());
  Proto::ClientPosePacket restored;
  REQUIRE(restored.ParseFromArray(sent->bytes.data(), static_cast<int>(sent->bytes.size())));
  CHECK(restored.sample().generation() == 1);
  CHECK_FALSE(restored.has_previous_sample());

  auto fresh       = Pose(*model);
  fresh.sequence   = {2};
  fresh.previous   = std::make_shared<const P::Wire::Pose>(fresh);
  fresh.generation = {2};
  exchange.Encoded(exchange.Epoch(), exchange.TakeWork().poseRevision, std::move(fresh));
  for (int ms : {10, 20})
    CHECK(
      std::ranges::none_of(stream.Poll(at + std::chrono::milliseconds(ms)), [](const auto& p) { return p.lane == P::Wire::PosesLane; }));
  CHECK(std::ranges::count_if(stream.Poll(at + std::chrono::milliseconds(110)), [](const auto& p) {
          return p.lane == P::Wire::PosesLane;
        }) == 1);
  REQUIRE(exchange.Submit(1, P::Generation{3}, model->asset));
  const auto next = exchange.TakeWork();
  CHECK(next.previousGeneration == P::Generation{1});
  CHECK_FALSE(exchange.Capturing(P::Generation{2}));
  CHECK(exchange.Capturing(P::Generation{1}));
}

TEST_CASE("Worker retains the old prepared bridge when rollback is immediately superseded")
{
  P::Exchange exchange;
  exchange.Context(1, true);
  auto       model = Model();
  P::Worker  worker(exchange, {});
  const auto prepared = [&](P::Generation generation) {
    return Until([&] {
      auto output = exchange.TakeOutput();
      return output.publication && output.publication->generation == generation;
    });
  };
  REQUIRE(exchange.Submit(1, P::Generation{1}, model.asset));
  REQUIRE(prepared({1}));
  exchange.Settled({P::Generation{1}, 1});
  REQUIRE(exchange.Submit(1, P::Generation{2}, model.asset));
  REQUIRE(prepared({2}));
  const auto second = exchange.TakeWork();
  exchange.PreparationFailed(second.epoch, second.localRevision, "rollback before next worker pass");
  REQUIRE(exchange.Submit(1, P::Generation{3}, model.asset));
  REQUIRE(prepared({3}));
  auto pose = P::ReadSnapshot(Pose(model).payload, model.asset);
  REQUIRE(pose);
  auto previous    = std::make_shared<const P::Snapshot>(*pose);
  pose->generation = {3};
  exchange.Submit(std::make_shared<const P::Snapshot>(*pose), previous);
  REQUIRE(Until([&] {
    auto output = exchange.TakeOutput();
    return output.pose && output.pose->generation == P::Generation{3} && output.pose->previous &&
           output.pose->previous->generation == P::Generation{1};
  }));
}

TEST_CASE("Preparation rollback resumes the committed model without republishing its older manifest")
{
  P::Exchange exchange;
  auto        model = std::make_shared<const P::PreparedAsset>(Model());
  PreparePublication(exchange, model);
  P::Streaming stream(exchange, {});
  Policy(stream);
  const auto requests = Models(stream.Poll());
  const auto publish  = std::ranges::find_if(requests, [](const auto& p) { return p.has_publish(); });
  REQUIRE(publish != requests.end());
  REQUIRE(stream.ReceiveAsset(Server([&](auto& p) {
    auto* c = p.mutable_complete();

    c->set_request_id(publish->publish().request_id());
    c->set_generation(1);
    c->set_player_id(123);
    c->set_upload(true);
    c->set_accepted(true);
  })));
  exchange.Settled({P::Generation{1}, 1});
  REQUIRE(exchange.Submit(1, P::Generation{2}, model->asset));
  auto work = exchange.TakeWork();
  stream.Poll();
  exchange.Encoded(exchange.Epoch(), work.poseRevision, Pose(*model));
  const auto preparing = stream.Poll(Clock::now() + std::chrono::seconds(1));
  CHECK(std::ranges::any_of(preparing, [](const auto& p) { return p.lane == P::Wire::PosesLane; }));
  exchange.PreparationFailed(work.epoch, work.localRevision, "prepare failed");
  work = exchange.TakeWork();
  exchange.Prepared(work.epoch, work.localRevision, {{1}, model});
  exchange.Encoded(exchange.Epoch(), exchange.TakeWork().poseRevision, Pose(*model));
  const auto resumed = stream.Poll(Clock::now() + std::chrono::seconds(2));
  CHECK(std::ranges::none_of(Models(resumed), [](const auto& p) { return p.has_publish() || p.has_withdraw(); }));
  CHECK(std::ranges::any_of(resumed, [](const auto& p) { return p.lane == P::Wire::PosesLane; }));
}

TEST_CASE("Phantom audience pause keeps publication and rejects stale encoded work after resume")
{
  P::Exchange exchange;
  auto        model = std::make_shared<const P::PreparedAsset>(Model());
  exchange.Context(1, true);
  REQUIRE(exchange.Submit(1, P::Generation{1}, model->asset));
  auto work = exchange.TakeWork();
  exchange.PoseDemand({1, false});
  CHECK_FALSE(exchange.PosesRequired());
  exchange.Prepared(work.epoch, work.localRevision, {P::Generation{1}, model});
  REQUIRE(exchange.TakeOutput().publication);
  exchange.PoseDemand({0, true});
  CHECK_FALSE(exchange.PosesRequired());
  exchange.PoseDemand({1, true});
  CHECK(exchange.PosesRequired());
  exchange.Encoded(work.epoch, work.poseRevision, Pose(*model));
  CHECK_FALSE(exchange.TakeOutput().pose);
  auto fresh = exchange.TakeWork();
  exchange.Encoded(fresh.epoch, fresh.poseRevision, Pose(*model));
  CHECK(exchange.TakeOutput().pose.has_value());
  exchange.PoseDemand({1, false});
  exchange.Context(2, true);
  exchange.PoseDemand({1, false});
  CHECK(exchange.PosesRequired());
}

TEST_CASE("Phantom demand survives control and model lane reordering during bootstrap")
{
  P::Exchange  exchange;
  P::Streaming stream(exchange, {});
  stream.Context(1, false);
  REQUIRE(stream.ReceiveAsset(Server([](auto& packet) {
    auto* demand = packet.mutable_pose_demand();
    demand->set_context_revision(1);
    demand->set_required(false);
  })));
  Policy(stream);
  CHECK_FALSE(exchange.PosesRequired());
  REQUIRE(stream.ReceiveAsset(Server([](auto& packet) {
    auto* demand = packet.mutable_pose_demand();
    demand->set_context_revision(2);
    demand->set_required(false);
  })));
  stream.Context(2, true);
  CHECK_FALSE(exchange.PosesRequired());
  exchange.PoseDemand({1, true});
  CHECK_FALSE(exchange.PosesRequired());
  exchange.PoseDemand({2, true});
  CHECK(exchange.PosesRequired());
}

TEST_CASE("Model compression cannot stall remote poses or the committed local generation")
{
  P::Exchange exchange;
  auto        model = std::make_shared<const P::PreparedAsset>(Model());
  PreparePublication(exchange, model);
  exchange.TakeOutput();
  exchange.Settled({P::Generation{1}, 1});
  P::Wire::Offer offer{7, 1, Describe(*model)};
  REQUIRE(exchange.Offer(offer));
  auto asset = std::make_shared<const P::ValidatedAsset>(model->asset);
  exchange.Loaded(exchange.Epoch(), offer, asset, false);
  {
    std::promise<void> started;
    std::promise<void> release;
    auto               wait = release.get_future().share();
    P::Worker          worker(exchange, {}, [&](P::ValidatedAsset value) {
      started.set_value();
      wait.wait();
      return P::Prepare(std::move(value));
    });

    // Release before Worker destruction even when a REQUIRE throws.
    struct Unblock
    {
      std::promise<void>& value;

      ~Unblock()
      {
        value.set_value();
      }
    } unblock{release};

    REQUIRE(exchange.Submit(1, P::Generation{2}, model->asset));
    REQUIRE(started.get_future().wait_for(std::chrono::seconds(2)) == std::future_status::ready);
    auto wire = Pose(*model);
    auto pose = P::ReadSnapshot(wire.payload, model->asset);
    REQUIRE(pose);
    auto previous    = std::make_shared<const P::Snapshot>(*pose);
    pose->generation = {2};
    exchange.Submit(std::make_shared<const P::Snapshot>(*pose), previous);
    worker.Queue({7, 1, wire}, asset, 100000);
    REQUIRE(Until([&] { return exchange.Find(7)->playback.Inspect(100000, exchange.Settings()).samples == 1; }));
    REQUIRE(Until([&] {
      auto output = exchange.TakeOutput();
      return output.pose && output.pose->generation == P::Generation{1};
    }));
    SUBCASE("Session reset")
    {
      exchange.Reset();
    }
    SUBCASE("Native source replaced")
    {
      exchange.RestartCapture();
    }
  }
  CHECK_FALSE(exchange.TakeOutput().publication);
  CHECK_FALSE(exchange.Capturing(P::Generation{2}));
}

TEST_CASE("Capture waits for matching movement space and rejects late capture completion")
{
  P::Exchange              exchange;
  const Domain::LocationId interior{"skyrim.esm", 1};
  const Domain::LocationId world{"skyrim.esm", 2};
  auto                     model = std::make_shared<const P::PreparedAsset>(Model());
  exchange.Context(1, true, interior);
  REQUIRE(exchange.CaptureContext(interior) == 1);
  CHECK_FALSE(exchange.CaptureContext(world));
  REQUIRE(exchange.Submit(1, P::Generation{1}, model->asset));
  auto old = exchange.TakeWork();
  exchange.Context(2, false, world);
  CHECK_FALSE(exchange.CaptureContext(world));
  exchange.Prepared(old.epoch, old.localRevision, {P::Generation{1}, model});
  CHECK_FALSE(exchange.TakeOutput().publication);
  exchange.Context(2, true, world);
  CHECK_FALSE(exchange.Submit(1, P::Generation{2}, model->asset));
  REQUIRE(exchange.CaptureContext(world) == 2);
  REQUIRE(exchange.Submit(2, P::Generation{3}, model->asset));
  auto current = exchange.TakeWork();
  exchange.Context(2, true, world);  // Adjacent exterior CELL, same WRLD.
  exchange.Prepared(current.epoch, current.localRevision, {P::Generation{3}, model});
  CHECK(exchange.TakeOutput().publication.has_value());
  CHECK(exchange.Capturing(P::Generation{3}));
}


TEST_CASE("Delta with an evicted base requests a full body instead of rejecting the model")
{
  P::Exchange exchange;
  auto model = Model();
  P::Wire::Offer offer{42, 1, Describe(model)};
  REQUIRE(exchange.Offer(offer));
  P::Worker worker(exchange, {});
  P::Digest absent{};
  absent[0] = 123;
  P::AssetDelta delta{absent, model.hash, static_cast<std::uint32_t>(model.compressed->size())};
  REQUIRE(worker.Queue(offer, model.compressed, delta));
  REQUIRE(Until([&] {
    auto missing = worker.TakeMissing();
    if(missing.empty()) return false;
    CHECK(missing.front().offer.player == offer.player);
    CHECK_FALSE(missing.front().baseHash);
    return true;
  }));
  CHECK(exchange.Stats().rejected == 0);
}


TEST_CASE("Decoded outfit replacement releases transfer scratch before admitting its native scene")
{
  constexpr std::uint64_t MiB = 1024 * 1024;
  P::Exchange exchange;
  P::ViewSettings settings;
  settings.memoryBytes = 512 * MiB;
  exchange.Configure(settings);
  auto oldAsset = P::ValidatedAsset::Parse(PhantomFixture::Model(20, 65535));
  auto newAsset = P::ValidatedAsset::Parse(PhantomFixture::Model(56, 65535));
  REQUIRE(oldAsset);
  REQUIRE(newAsset);
  P::Wire::Offer first{1, 1, {P::Digest{}, {1}, P::AssetVersion, 9 * 1024 * 1024,
    static_cast<std::uint32_t>(oldAsset->Value().nif.size() + 12), 20}};
  REQUIRE(exchange.Offer(first));
  exchange.Loaded(exchange.Epoch(), first, std::make_shared<const P::ValidatedAsset>(*oldAsset), false);
  REQUIRE(exchange.SceneMemory(1, 80 * MiB));
  auto second = first;
  second.view = 2;
  second.asset.generation = {2};
  second.asset.rawBytes = static_cast<std::uint32_t>(newAsset->Value().nif.size() + 12);
  second.asset.compressedBytes = 32 * 1024 * 1024;
  second.asset.channels = 56;
  REQUIRE(exchange.Offer(second));
  CHECK_FALSE(exchange.Find(1)->WaitingBudget());
  // A stale worker completion must not release the current transfer's budget.
  const auto loading = exchange.RemainingMemory();
  exchange.Loaded(exchange.Epoch() + 1, second, std::make_shared<const P::ValidatedAsset>(*newAsset), false);
  CHECK(exchange.RemainingMemory() == loading);
  exchange.Loaded(exchange.Epoch(), second, std::make_shared<const P::ValidatedAsset>(*newAsset), false);
  CHECK(exchange.RemainingMemory() > loading + 64 * MiB);
  REQUIRE(exchange.SceneMemory(1, (80 + 224) * MiB));
  REQUIRE(exchange.Find(1)->previous);
  const auto replacing = exchange.RemainingMemory();
  exchange.Displayed({1, 2, {2}});
  REQUIRE(exchange.SceneMemory(1, 224 * MiB));
  CHECK(exchange.RemainingMemory() > replacing + 80 * MiB);
  CHECK_FALSE(exchange.SceneMemory(1, settings.memoryBytes));
}

TEST_CASE("Context keeps only a charged immutable delta basis and reset releases it")
{
  P::Exchange exchange;
  auto model = std::make_shared<const P::PreparedAsset>(Model());
  PreparePublication(exchange, model);
  exchange.Settled({P::Generation{1}, 1});
  exchange.Context(2, false);
  exchange.RestartCapture();
  auto work = exchange.TakeWork();
  CHECK_FALSE(work.generation);
  CHECK_FALSE(work.asset);
  CHECK(work.priorAsset == model);
  CHECK_FALSE(exchange.TakeOutput().publication);
  exchange.Context(2, true);
  REQUIRE(exchange.Submit(2, P::Generation{2}, model->asset));
  work = exchange.TakeWork();
  CHECK(work.priorAsset == model);
  CHECK_FALSE(work.previousGeneration);
  exchange.Reset();
  CHECK_FALSE(exchange.TakeWork().priorAsset);
}

TEST_CASE("Transient publication admission preserves delta on retry")
{
  P::Exchange exchange;
  auto model = std::make_shared<P::PreparedAsset>(Model());
  auto basis = model->hash;
  basis[0] ^= 1;
  model->delta = P::PreparedDelta{{basis,model->hash,1},std::make_shared<const P::Bytes>(P::Bytes{1})};
  PreparePublication(exchange, model);
  P::Streaming stream(exchange, {});
  Policy(stream);
  auto packets = Models(stream.Poll());
  auto publication = std::ranges::find_if(packets, [](const auto& p) { return p.has_publish(); });
  REQUIRE(publication != packets.end());
  REQUIRE(publication->publish().has_delta());
  const auto request = publication->publish().request_id();
  REQUIRE(stream.ReceiveAsset(Server([&](auto& p) {
    auto* c = p.mutable_complete();
    c->set_request_id(request);
    c->set_player_id(123);
    c->set_generation(1);
    c->set_upload(true);
    c->set_reason("initial display pending");
    c->set_retry_after_ms(10);
  })));
  packets = Models(stream.Poll(Clock::now()+std::chrono::seconds(1)));
  publication = std::ranges::find_if(packets, [](const auto& p) { return p.has_publish(); });
  REQUIRE(publication != packets.end());
  CHECK(publication->publish().has_delta());
}
