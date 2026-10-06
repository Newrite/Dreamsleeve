#include <doctest/doctest.h>
import std;
import Dreamsleeve.Client.Phantom.Worker;

namespace
{
  namespace P = Dreamsleeve::Client::Phantom;

  P::Asset Fixture(std::size_t nodes = 1, std::size_t vertices = 3)
  {
    P::Asset raw;
    raw.nodes.resize(nodes);
    for (std::size_t i = 1; i < nodes; ++i)
      raw.nodes[i].parent = {0};
    P::Geometry mesh;
    mesh.vertices.resize(vertices);
    mesh.indices = {0, 1, 2};
    raw.geometry.push_back(std::move(mesh));
    return raw;
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

TEST_CASE("Measured avatar geometry counts fit the neutral envelope with an enforced ceiling")
{
  auto raw = Fixture();
  raw.geometry.resize(267, raw.geometry.front());
  auto asset = P::ValidatedAsset::Parse(raw);
  REQUIRE(asset);
  auto encoded = P::Prepare(*asset);
  REQUIRE(encoded);
  auto decoded = P::ReadAsset(*encoded->compressed, encoded->rawBytes);
  REQUIRE(decoded);
  CHECK(decoded->Value().geometry.size() == 267);
  raw.geometry.resize(P::Limits{}.geometry + 1, raw.geometry.front());
  CHECK_FALSE(P::ValidatedAsset::Parse(std::move(raw)));
}

TEST_CASE("Decoded pose histories and interpolation remain within the admitted working debit")
{
  auto raw                = Fixture(4096, 18000);
  raw.geometry[0].dynamic = true;
  auto asset              = P::ValidatedAsset::Parse(std::move(raw));
  REQUIRE(asset);
  auto model = P::Prepare(*asset);
  REQUIRE(model);
  P::Snapshot pose;
  pose.generation  = {1};
  pose.sequence    = {1};
  pose.context     = 1;
  pose.sampledAtUs = 50000;
  pose.channels.resize(4096);
  pose.bounds.resize(1);
  P::Deformation deformation;
  deformation.positions.resize(18000);
  deformation.normals.resize(18000);
  pose.deformations.push_back(std::move(deformation));
  auto encoded = P::WriteSnapshot(pose, *asset);
  REQUIRE(encoded);
  auto decoded = P::ReadSnapshot(*encoded, *asset);
  REQUIRE(decoded);
  const auto  frameBytes = sizeof(P::Snapshot) + decoded->channels.capacity() * sizeof(P::Channel) +
                           decoded->bounds.capacity() * sizeof(P::Bound) + decoded->deformations.capacity() * sizeof(P::Deformation) +
                           (decoded->deformations[0].positions.capacity() + decoded->deformations[0].normals.capacity()) * sizeof(P::Vec3);
  P::Exchange exchange;
  const auto  budget = exchange.Settings().memoryBytes;
  P::Wire::Offer offer{
      1,
      1,
      {model->hash, {1}, P::AssetVersion, static_cast<std::uint32_t>(model->compressed->size()), model->rawBytes, 4096, 1}
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
      {model->hash, {1}, P::AssetVersion, static_cast<std::uint32_t>(model->compressed->size()), model->rawBytes, 1, 1}
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
