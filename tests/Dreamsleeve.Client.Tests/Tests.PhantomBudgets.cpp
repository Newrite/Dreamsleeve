#include <doctest/doctest.h>
import std;
import Dreamsleeve.Client.Phantom.Worker;

#include "PhantomFixture.hpp"

namespace
{
  namespace P = Dreamsleeve::Client::Phantom;

  P::Asset Fixture(std::size_t nodes = 1, std::size_t vertices = 3)
  {
    return PhantomFixture::Model(nodes, static_cast<std::uint16_t>(vertices));
  }

  bool Until(const std::function<bool()>& condition)
  {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(2);
    while (!condition())
    {
      if (std::chrono::steady_clock::now() >= deadline) return false;
      std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
    return true;
  }

}

TEST_CASE("Native channel budget bounds the asset before engine loading")
{
  auto raw   = Fixture(268);
  auto asset = P::ValidatedAsset::Parse(raw);
  REQUIRE(asset);
  auto encoded = P::Prepare(*asset);
  REQUIRE(encoded);
  auto decoded = P::ReadAsset(*encoded->compressed, encoded->rawBytes);
  REQUIRE(decoded);
  CHECK(decoded->Layout().bounds.size() == 267);

  P::Limits limits;
  limits.nodes = 267;
  CHECK_FALSE(P::ValidatedAsset::Parse(std::move(raw), limits));
}

TEST_CASE("Decoded pose histories and interpolation remain within the admitted working debit")
{
  auto raw   = Fixture(4096);
  auto asset = P::ValidatedAsset::Parse(std::move(raw));
  REQUIRE(asset);
  auto model = P::Prepare(*asset);
  REQUIRE(model);

  P::Snapshot pose;
  pose.generation  = {1};
  pose.sequence    = {1};
  pose.context     = 1;
  pose.sampledAtUs = 50000;
  pose.channels.resize(4096);
  pose.bounds.resize(4095);

  auto encoded = P::WriteSnapshot(pose, *asset);
  REQUIRE(encoded);
  auto decoded = P::ReadSnapshot(*encoded, *asset);
  REQUIRE(decoded);
  const auto frameBytes =
    sizeof(P::Snapshot) + decoded->channels.capacity() * sizeof(P::Channel) + decoded->bounds.capacity() * sizeof(P::Bound);

  P::Exchange    exchange;
  const auto     budget = exchange.Settings().memoryBytes;
  P::Wire::Offer offer{
      1,
      1,
      {model->hash, {1}, P::AssetVersion, static_cast<std::uint32_t>(model->compressed->size()), model->rawBytes, 4096}
  };
  REQUIRE(exchange.Offer(offer));
  const auto debit = budget - exchange.RemainingMemory();
  // Independent allocation lower bound: reader histories stay alive while
  // live playback advances; codec and interpolation each own another frame.
  CHECK(debit >= (4 * P::BufferedPoseCount + 2) * frameBytes + asset->MemoryBytes() + model->rawBytes + encoded->size());

  auto settings        = exchange.Settings();
  settings.memoryBytes = debit - 1;
  exchange.Reset();
  exchange.Configure(settings);
  CHECK_FALSE(exchange.Offer(offer));
}

TEST_CASE("RAM cache metadata obeys the same validation as cold model decode")
{
  auto asset = P::ValidatedAsset::Parse(Fixture());
  REQUIRE(asset);
  auto model = P::Prepare(*asset);
  REQUIRE(model);

  P::Exchange    exchange;
  P::Worker      worker(exchange, {});
  P::Wire::Offer original{
      1,
      1,
      {model->hash, {1}, P::AssetVersion, static_cast<std::uint32_t>(model->compressed->size()), model->rawBytes, 2}
  };
  REQUIRE(exchange.Offer(original));
  REQUIRE(worker.Queue(original, model->compressed));
  REQUIRE(Until([&] { return exchange.Find(1)->State() == P::Representation::Ready; }));

  auto next             = original;
  next.player           = 2;
  next.asset.generation = {2};
  REQUIRE(exchange.AssetFor(next.asset));
  SUBCASE("inconsistent raw size")
  {
    ++next.asset.rawBytes;
  }
  SUBCASE("inconsistent channel count")
  {
    ++next.asset.channels;
  }

  CHECK_FALSE(exchange.AssetFor(next.asset));
  REQUIRE(exchange.Offer(next));
  REQUIRE(worker.Queue(next, model->compressed));
  REQUIRE(Until([&] { return exchange.Find(2)->State() == P::Representation::Unavailable; }));
  CHECK_FALSE(exchange.Find(2)->Asset());
}
