#include <doctest/doctest.h>
import std;
import Dreamsleeve.Client.Phantom.Codec;
import Dreamsleeve.Client.Phantom.Playback;
import Dreamsleeve.Client.Phantom.Streaming;

namespace
{
  namespace P = Dreamsleeve::Client::Phantom;

  P::Asset Triangle()
  {
    P::Asset a;
    a.nodes.push_back({});
    P::Geometry m;
    m.vertices.resize(3);
    m.vertices[1].position = {1, 0, 0};
    m.vertices[2].position = {0, 1, 0};
    m.indices              = {0, 1, 2};
    a.geometry.push_back(std::move(m));
    return a;
  }

  P::Snapshot Pose(std::uint64_t sequence, std::uint64_t sampled, float x)
  {
    P::Snapshot p;
    p.generation  = {1};
    p.sequence    = {sequence};
    p.context     = 1;
    p.sampledAtUs = sampled;
    p.origin      = {x, 0, 0};
    p.channels.push_back({P::Transform{{x, 0, 0}}, false});
    p.bounds.push_back({
        {x, 0, 0},
        1
    });
    return p;
  }

}

TEST_CASE("Phantom asset rejects cycles, bad indices and invalid skin")
{
  auto a = Triangle();
  a.nodes.push_back({P::NodeId{1}, {}});
  CHECK_FALSE(P::ValidatedAsset::Parse(std::move(a)));
  a                        = Triangle();
  a.geometry[0].indices[2] = 3;
  CHECK_FALSE(P::ValidatedAsset::Parse(std::move(a)));
  a                                    = Triangle();
  a.geometry[0].vertices[0].weights[0] = 1;
  CHECK_FALSE(P::ValidatedAsset::Parse(std::move(a)));
  a                                    = Triangle();
  a.geometry[0].vertices[0].position.x = std::numeric_limits<float>::quiet_NaN();
  CHECK_FALSE(P::ValidatedAsset::Parse(std::move(a)));
}

TEST_CASE("Neutral phantom model has an exact bounded compressed frame")
{
  auto a = P::ValidatedAsset::Parse(Triangle());
  REQUIRE(a);
  auto prepared = P::Prepare(*a);
  REQUIRE(prepared);
  auto read = P::ReadAsset(*prepared->compressed, prepared->rawBytes);
  REQUIRE(read);
  CHECK(read->Value().geometry[0].vertices[1].position.x == 1);
  CHECK(P::Hex(prepared->hash).size() == 64);
  CHECK_FALSE(P::ReadAsset(*prepared->compressed, prepared->rawBytes + 1));
  auto bytes = *prepared->compressed;
  bytes.push_back(0);
  CHECK_FALSE(P::ReadAsset(bytes, prepared->rawBytes));
  P::Limits small;
  small.assetBytes = prepared->rawBytes - 1;
  CHECK_FALSE(P::ReadAsset(*prepared->compressed, prepared->rawBytes, small));
}

TEST_CASE("Phantom poses require complete matching channels and bounds")
{
  auto a = P::ValidatedAsset::Parse(Triangle());
  REQUIRE(a);
  auto p                       = Pose(1, 50000, 0);
  p.channels[0].world.position = {12.24f, 0, 0};
  auto bytes                   = P::WriteSnapshot(p, *a);
  REQUIRE(bytes);
  auto read = P::ReadSnapshot(*bytes, *a);
  REQUIRE(read);
  CHECK(read->channels[0].world.position.x == doctest::Approx(12.25));
  auto broken = *bytes;
  broken.resize(broken.size() / 2);
  CHECK_FALSE(P::ReadSnapshot(broken, *a));
  p.bounds.clear();
  CHECK_FALSE(P::WriteSnapshot(p, *a));
  p                              = Pose(1, 50000, 0);
  p.channels[0].world.position.x = 5000;
  CHECK_FALSE(P::WriteSnapshot(p, *a));
}

TEST_CASE("Phantom playback drops stale samples and bounds extrapolation")
{
  P::Playback     playback;
  P::ViewSettings settings;
  settings.delayMs         = 50;
  settings.extrapolationMs = 50;
  settings.timeoutMs       = 250;
  CHECK(playback.Push(std::make_shared<const P::Snapshot>(Pose(1, 1000000, 0)), 2000000));
  CHECK(playback.Push(std::make_shared<const P::Snapshot>(Pose(2, 1050000, 10)), 2050000));
  CHECK_FALSE(playback.Push(std::make_shared<const P::Snapshot>(Pose(1, 1000000, 0)), 2100000));
  auto p = playback.At(2075000, settings);
  REQUIRE(p);
  CHECK(p->origin.x == doctest::Approx(5));
  p = playback.At(2200000, settings);
  REQUIRE(p);
  CHECK(p->origin.x == doctest::Approx(20));
  CHECK_FALSE(playback.At(2400000, settings));
  CHECK(
    P::Motion::Cover(
      {
          {0, 0, 0},
          2
  },
      {{10, 0, 0}, 3})
      .radius >= 8);
}

TEST_CASE("Phantom exchange clears old work and bounds remote admission")
{
  P::Exchange     exchange;
  P::ViewSettings settings;
  settings.maximum = 1;
  exchange.Configure(settings);
  exchange.Context(1, true);
  auto a = P::ValidatedAsset::Parse(Triangle());
  REQUIRE(a);
  CHECK(exchange.Submit(P::Generation{1}, *a));
  auto work = exchange.TakeWork();
  REQUIRE(work.capture);
  P::Wire::Offer offer{
      1,
      1,
      {P::Digest{}, P::Generation{1}, 1, 32, 128, 1, 1}
  };
  CHECK(exchange.Offer(offer));
  offer.player = 2;
  CHECK_FALSE(exchange.Offer(offer));
  exchange.Reset();
  CHECK(exchange.Read().remotes.empty());
  CHECK_FALSE(exchange.Read().available);
  auto prepared = P::Prepare(*a);
  REQUIRE(prepared);
  exchange.Prepared(work.epoch, work.localRevision, {P::Generation{1}, std::make_shared<const P::PreparedAsset>(std::move(*prepared))});
  CHECK_FALSE(exchange.TakeOutput().publication);
}

TEST_CASE("Phantom atomic poses cannot omit a deforming mesh")
{
  auto raw                = Triangle();
  raw.geometry[0].dynamic = true;
  auto asset              = P::ValidatedAsset::Parse(std::move(raw));
  REQUIRE(asset);
  auto pose = Pose(1, 50000, 0);
  CHECK_FALSE(P::CheckSnapshot(pose, *asset));
  CHECK_FALSE(P::WriteSnapshot(pose, *asset));
  P::Deformation deformation;
  deformation.geometry = 0;
  for (const auto& vertex : asset->Value().geometry[0].vertices)
  {
    deformation.positions.push_back(vertex.position);
    deformation.normals.push_back(vertex.normal);
  }
  pose.deformations.push_back(deformation);
  REQUIRE(P::CheckSnapshot(pose, *asset));
  REQUIRE(P::WriteSnapshot(pose, *asset));
  pose.deformations.push_back(deformation);
  CHECK_FALSE(P::CheckSnapshot(pose, *asset));
}

TEST_CASE("Phantom admission reserves aggregate working memory and server sampling rate")
{
  P::Exchange     exchange;
  P::ViewSettings settings;
  settings.memoryBytes = 16 * 1024 * 1024;
  settings.maximum     = 4;
  settings.sampleRate  = 50;
  exchange.Configure(settings);
  exchange.SampleRate(20);
  CHECK(exchange.Settings().sampleRate == 20);
  CHECK(exchange.Read().settings.sampleRate == 20);
  P::Wire::Offer offer{
      1,
      1,
      {P::Digest{}, P::Generation{1}, 1, 1024 * 1024, 1024 * 1024, 1, 1}
  };
  REQUIRE(exchange.Offer(offer));
  offer.player = 2;
  REQUIRE(exchange.Offer(offer));
  offer.player = 3;
  CHECK_FALSE(exchange.Offer(offer));
  settings.maximum = 1;
  exchange.Configure(settings);
  CHECK(exchange.Read().remotes.size() == 1);
}

TEST_CASE("Phantom time arithmetic rejects timestamps outside its signed domain")
{
  auto asset = P::ValidatedAsset::Parse(Triangle());
  REQUIRE(asset);
  auto pose = Pose(1, P::MaximumSampleTime + 1, 0);
  CHECK_FALSE(P::CheckSnapshot(pose, *asset));
  CHECK_FALSE(P::WriteSnapshot(pose, *asset));
  P::Playback playback;
  CHECK_FALSE(playback.Push(std::make_shared<const P::Snapshot>(pose), 50000));
}

TEST_CASE("Publication revisions reject stale preparation across a fast off on toggle")
{
  P::Exchange exchange;
  exchange.Context(1, true);
  auto asset = P::ValidatedAsset::Parse(Triangle());
  REQUIRE(asset);
  REQUIRE(exchange.Submit(P::Generation{1}, *asset));
  auto work        = exchange.TakeWork();
  auto settings    = exchange.Settings();
  settings.publish = false;
  exchange.Configure(settings);
  settings.publish = true;
  exchange.Configure(settings);
  auto model = P::Prepare(*asset);
  REQUIRE(model);
  exchange.Prepared(work.epoch, work.localRevision, {P::Generation{1}, std::make_shared<const P::PreparedAsset>(std::move(*model))});
  CHECK_FALSE(exchange.TakeOutput().publication);
  CHECK_FALSE(exchange.Capturing(P::Generation{1}));
}

TEST_CASE("Shrinking phantom RAM releases local preparation and invalidates its source")
{
  P::Exchange exchange;
  exchange.Context(1, true);
  auto raw = Triangle();
  raw.geometry[0].vertices.resize(60000);
  auto asset = P::ValidatedAsset::Parse(std::move(raw));
  REQUIRE(asset);
  REQUIRE(exchange.Submit(P::Generation{1}, *asset));
  auto work            = exchange.TakeWork();
  auto settings        = exchange.Settings();
  settings.memoryBytes = 8 * 1024 * 1024;
  exchange.Configure(settings);
  CHECK_FALSE(exchange.Capturing(P::Generation{1}));
  CHECK(exchange.RemainingMemory() == settings.memoryBytes);
  exchange.PreparationFailed(work.epoch, work.localRevision, "stale failure");
  CHECK(exchange.Stats().rejected == 0);
}
