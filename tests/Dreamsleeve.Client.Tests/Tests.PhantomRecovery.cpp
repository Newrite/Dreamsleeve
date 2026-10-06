#include <doctest/doctest.h>
import std;
import Dreamsleeve.Game.PhantomRecovery;
import Dreamsleeve.Game.PhantomMesh;
import Dreamsleeve.Client.Phantom.Codec;
namespace P = Dreamsleeve::Client::Phantom;
namespace R = Dreamsleeve::Game::PhantomRecovery;
namespace M = Dreamsleeve::Game::PhantomMesh;

TEST_CASE("A failed geometry keeps a complete hidden pose while its neighbours advance")
{
  P::Asset asset;
  asset.nodes.resize(3);
  for (unsigned i = 1; i < 3; ++i)
    asset.nodes[i].parent = {0};
  asset.geometry.resize(3);
  std::array<R::Mesh, 3> slots;
  for (unsigned i = 0; i < 3; ++i)
  {
    auto& geometry   = asset.geometry[i];
    geometry.node    = {i};
    geometry.dynamic = true;
    geometry.vertices.resize(3);
    geometry.indices = {0, 1, 2};
    slots[i].deformation.positions.resize(3);
    slots[i].deformation.normals.resize(3, P::Vec3{0, 0, 1});
  }
  auto validated = P::ValidatedAsset::Parse(std::move(asset));
  REQUIRE(validated);
  slots[1].Failed({P::Failure::InvalidNumber, "one damaged mesh"}, 1000);
  CHECK_FALSE(slots[1].Ready(1001));
  CHECK(slots[0].Ready(1001));
  CHECK(slots[2].Ready(1001));
  for (unsigned frame = 1; frame <= 3; ++frame)
  {
    P::Snapshot pose{{1}, {frame}, 1, frame * 50000ULL};
    pose.channels.resize(3);
    pose.channels[0].world.position.x = float(frame);
    pose.channels[2].world.position.x = float(frame * 2);
    for (unsigned i = 0; i < 3; ++i)
      slots[i].Append(pose, {i}, i, true);
    CHECK_FALSE(pose.channels[0].hidden);
    CHECK(pose.channels[1].hidden);
    CHECK_FALSE(pose.channels[2].hidden);
    auto bytes = P::WriteSnapshot(pose, *validated);
    REQUIRE(bytes);
    auto read = P::ReadSnapshot(*bytes, *validated);
    REQUIRE(read);
    CHECK(read->deformations.size() == 3);
    CHECK(read->channels[2].world.position.x == frame * 2);
  }
  CHECK(slots[1].Ready(1001000));
  slots[1].Recovered();
  P::Snapshot healed;
  healed.channels.resize(3);
  slots[1].Append(healed, {1}, 1, true);
  CHECK_FALSE(healed.channels[1].hidden);
  CHECK_FALSE(slots[1].Fault());
}

TEST_CASE("A changed geometry schema waits for replacement instead of reusing obsolete vertices")
{
  R::Mesh slot;
  slot.Failed({P::Failure::Stale, "skin changed"}, 1000);
  CHECK_FALSE(slot.Ready(10000000));
  P::Snapshot pose;
  pose.channels.resize(1);
  slot.Append(pose, {0}, 0, false);
  CHECK(pose.channels[0].hidden);
  R::Mesh pending;
  pending.Failed({P::Failure::Busy, "readback pending"}, 1000);
  CHECK_FALSE(pending.Ready(50999));
  CHECK(pending.Ready(51000));
}

TEST_CASE("Bounds cover nonunit weighted skin blends without changing vertices")
{
  const P::Bound hull{
      {100, -30, 10},
      20
  };
  for (
    auto range : {
        std::pair{.97839355f, 1.001f},
        std::pair{0.f,        4.f   }
  })
  {
    auto bound = M::WeightedBound(hull, range.first, range.second);
    for (auto sum : {range.first, (range.first + range.second) * .5f, range.second})
      for (
        auto axis : {
            P::Vec3{1,  0, 0 },
            P::Vec3{-1, 0, 0 },
            P::Vec3{0,  1, 0 },
            P::Vec3{0,  0, -1}
      })
      {
        auto point = M::Mul(M::Add(hull.center, M::Mul(axis, hull.radius)), sum);
        auto delta = M::Sub(point, bound.center);
        CHECK(std::sqrt(M::Dot(delta, delta)) <= bound.radius + .0001f);
      }
  }
}

TEST_CASE("Hidden failed geometry cannot poison the pose with a stale bound after teleport")
{
  P::Asset asset;
  asset.nodes.resize(1);
  asset.geometry.resize(1);
  asset.geometry[0].vertices.resize(3);
  asset.geometry[0].indices = {0, 1, 2};
  auto validated            = P::ValidatedAsset::Parse(std::move(asset));
  REQUIRE(validated);
  P::Snapshot pose{
      {1},
      {1},
      1,
      50000,
      {100000, 0, 0}
  };
  pose.channels.push_back({P::Transform{pose.origin}});
  R::Mesh slot;
  slot.bound = {
      {0, 0, 0},
      10
  };
  slot.Failed({P::Failure::InvalidNumber, "bad optional geometry"}, 1);
  slot.Append(pose, {0}, 0, false);
  CHECK(pose.bounds[0].center == pose.origin);
  CHECK(pose.bounds[0].radius == 0);
  REQUIRE(P::WriteSnapshot(pose, *validated));
}

TEST_CASE("Shared immutable alpha masks reuse identity but replaced masks compare content")
{
  auto a    = std::make_shared<P::AlphaMask>();
  a->width  = 2;
  a->height = 1;
  a->pixels = {0, 255};
  CHECK(R::SameMask(a, a));
  auto b = std::make_shared<P::AlphaMask>(*a);
  CHECK(R::SameMask(a, b));
  b->pixels[0] = 128;
  CHECK_FALSE(R::SameMask(a, b));
  CHECK_FALSE(R::SameMask(a, {}));
  CHECK(R::SameMask({}, {}));
}
