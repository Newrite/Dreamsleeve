#include <doctest/doctest.h>
import std;
import Dreamsleeve.Client.Phantom.Codec;
import Dreamsleeve.Client.Phantom.Playback;
import Dreamsleeve.Client.Phantom.Streaming;

#include "PhantomFixture.hpp"

namespace
{
  namespace P = Dreamsleeve::Client::Phantom;

  P::Asset Triangle()
  {
    return PhantomFixture::Model();
  }

  P::Snapshot Pose(std::uint64_t sequence, std::uint64_t sampled, float x)
  {
    P::Snapshot p;
    p.generation  = {1};
    p.sequence    = {sequence};
    p.context     = 1;
    p.sampledAtUs = sampled;
    p.origin      = {x, 0, 0};
    p.channels.resize(2, {P::Transform{{x, 0, 0}}, false});
    p.bounds.push_back({
        {x, 0, 0},
        1
    });
    return p;
  }

}

TEST_CASE("Phantom asset rejects malformed native input")
{
  auto a = Triangle();
  a.nif.pop_back();
  CHECK_FALSE(P::ValidatedAsset::Parse(std::move(a)));
}

TEST_CASE("Native phantom model has an exact bounded compressed frame")
{
  auto a = P::ValidatedAsset::Parse(Triangle());
  REQUIRE(a);
  auto prepared = P::Prepare(*a);
  REQUIRE(prepared);
  auto read = P::ReadAsset(*prepared->compressed, prepared->rawBytes);
  REQUIRE(read);
  CHECK(read->Value().nif == a->Value().nif);
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
  p.channels[0].world.position.x = 200000000;
  CHECK_FALSE(P::WriteSnapshot(p, *a));
}

TEST_CASE("Phantom playback drops stale samples and bounds extrapolation")
{
  P::Playback     playback;
  P::ViewSettings settings;
  settings.delayMs         = 50;
  settings.extrapolationMs = 50;
  settings.timeoutMs       = 250;
  CHECK(playback.Push(std::make_shared<const P::Snapshot>(Pose(1, 1000000, 0)), 2000000, settings));
  CHECK(playback.Push(std::make_shared<const P::Snapshot>(Pose(2, 1050000, 10)), 2050000, settings));
  CHECK_FALSE(playback.Push(std::make_shared<const P::Snapshot>(Pose(1, 1000000, 0)), 2100000, settings));
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

TEST_CASE("A faster arriving phantom sample does not move the playout clock")
{
  P::Playback     playback;
  P::ViewSettings settings;
  REQUIRE(playback.Push(std::make_shared<const P::Snapshot>(Pose(1, 1000000, 0)), 2000000, settings));
  REQUIRE(playback.Push(std::make_shared<const P::Snapshot>(Pose(2, 1100000, 10)), 2100000, settings));
  const auto before = playback.At(2150000, settings);
  REQUIRE(before);
  CHECK(before->origin.x == doctest::Approx(5));
  // One packet saves 50 ms of network/server delay. Previously this advanced
  // every buffered sample by 50 ms and snapped the rendered pose in one frame.
  REQUIRE(playback.Push(std::make_shared<const P::Snapshot>(Pose(3, 1200000, 20)), 2150000, settings));
  const auto after = playback.At(2150000, settings);
  REQUIRE(after);
  CHECK(after->origin.x == doctest::Approx(before->origin.x));
  CHECK(playback.At(2166000, settings)->origin.x == doctest::Approx(6.6));
}

TEST_CASE("Phantom clock mapping retains microsecond range and resets after a long receive gap")
{
  P::Playback     playback;
  P::ViewSettings settings;
  settings.delayMs = 50;
  const auto end   = P::MaximumSampleTime;
  REQUIRE(playback.Push(std::make_shared<const P::Snapshot>(Pose(1, 1000000, 0)), end - 100000, settings));
  REQUIRE(playback.Push(std::make_shared<const P::Snapshot>(Pose(2, 1100000, 10)), end, settings));
  const auto sample = playback.At(end, settings);
  REQUIRE(sample);
  CHECK(sample->origin.x == doctest::Approx(5));
  playback.Clear();
  REQUIRE(playback.Push(std::make_shared<const P::Snapshot>(Pose(1, 1000000, 0)), 2000000, settings));
  REQUIRE(playback.Push(std::make_shared<const P::Snapshot>(Pose(2, 1100000, 10)), 8000000, settings));
  CHECK(playback.Inspect(8000000, settings).samples == 1);
  CHECK(playback.At(8000000, settings)->origin.x == doctest::Approx(10));
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
      {P::Digest{}, P::Generation{1}, P::AssetVersion, 32, 128, 2}
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

TEST_CASE("Phantom admission reserves aggregate working memory and server sampling rate")
{
  P::Exchange     exchange;
  P::ViewSettings settings;
  settings.memoryBytes = 128 * 1024 * 1024;
  settings.maximum     = 4;
  settings.sampleRate  = 50;
  exchange.Configure(settings);
  exchange.SampleRate(20);
  CHECK(exchange.Settings().sampleRate == 20);
  CHECK(exchange.Read().settings.sampleRate == 20);
  P::Wire::Offer offer{
      1,
      1,
      {P::Digest{}, P::Generation{1}, P::AssetVersion, 1024 * 1024, 1024 * 1024, 2}
  };
  REQUIRE(exchange.Offer(offer));
  settings.memoryBytes = 2 * (settings.memoryBytes - exchange.RemainingMemory());
  exchange.Configure(settings);
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
  CHECK_FALSE(playback.Push(std::make_shared<const P::Snapshot>(pose), 50000, P::ViewSettings{}));
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
  auto raw   = Triangle();
  raw        = PhantomFixture::Model(2, 60000);
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
