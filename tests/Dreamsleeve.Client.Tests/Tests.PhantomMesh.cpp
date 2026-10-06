#include <cstdlib>
#include <fstream>
#include <doctest/doctest.h>
import std;
import Dreamsleeve.Game.PhantomMesh;
import Dreamsleeve.Game.PhantomVertexStream;
import Dreamsleeve.Client.Phantom.Codec;
#ifdef DREAMSLEEVE_DIAGNOSTICS
import Dreamsleeve.Client.Diagnostics.PhantomRecorder;
#endif

namespace M = Dreamsleeve::Game::PhantomMesh;
namespace V = Dreamsleeve::Game::PhantomVertexStream;
namespace P = Dreamsleeve::Client::Phantom;

namespace
{

  std::string FixturePath()
  {
    char*       text{};
    std::size_t size{};
    if (_dupenv_s(&text, &size, "DREAMSLEEVE_PHANTOM_MESH_FIXTURES")) return {};
    std::unique_ptr<char, decltype(&std::free)> owner(text, &std::free);
    return owner && size > 1 ? std::string(owner.get()) : std::string{};
  }

  P::Asset Asset(P::Geometry geometry, unsigned bones)
  {
    P::Asset asset;
    asset.nodes.resize(1);
    if (bones)
    {
      geometry.skin = P::Skin{};
      geometry.skin->bones.resize(bones);
    }
    asset.geometry.push_back(std::move(geometry));
    return asset;
  }

}

TEST_CASE("Authored nonunit skin weights survive validation and codec unchanged")
{
  P::Geometry geometry;
  geometry.vertices.resize(3);
  geometry.indices = {0, 1, 2};
  const std::array<float, 4> authored{0.79150390625f, 0.0775146484375f, 0.07733154296875f, 0.03204345703125f};
  for (auto& vertex : geometry.vertices)
    vertex.weights = authored;
  CHECK(std::abs(std::accumulate(authored.begin(), authored.end(), 0.f) - 1.f) > .02f);
  auto checked = P::ValidatedAsset::Parse(Asset(geometry, 5));
  REQUIRE(checked);
  auto encoded = P::Prepare(*checked);
  REQUIRE(encoded);
  auto decoded = P::ReadAsset(*encoded->compressed, encoded->rawBytes);
  REQUIRE(decoded);
  CHECK(decoded->Value().geometry[0].vertices[0].weights == authored);
  auto& v      = geometry.vertices[0];
  v.weights[0] = std::numeric_limits<float>::quiet_NaN();
  CHECK_FALSE(P::ValidatedAsset::Parse(Asset(geometry, 5)));
  v.weights[0] = -.1f;
  CHECK_FALSE(P::ValidatedAsset::Parse(Asset(geometry, 5)));
  v.weights  = authored;
  v.bones[0] = 5;
  CHECK_FALSE(P::ValidatedAsset::Parse(Asset(geometry, 5)));
  v.weights[0] = 0;
  CHECK(P::ValidatedAsset::Parse(Asset(geometry, 5)));
}

TEST_CASE("Live packed meshes run through the production decoder and asset codec" * doctest::skip(FixturePath().empty()))
{
  unsigned streams = 0;
  P::Asset combined;
  combined.nodes.resize(1);
  for (const auto& entry : std::filesystem::directory_iterator(FixturePath()))
  {
    if (entry.path().extension() != ".mesh") continue;
    INFO(entry.path().string());
    std::ifstream             file(entry.path(), std::ios::binary);
    std::array<std::byte, 32> header{};
    REQUIRE(bool(file.read(reinterpret_cast<char*>(header.data()), header.size())));
    REQUIRE(std::memcmp(header.data(), "DLPMESH1", 8) == 0);
    M::RawMesh raw;
    raw.descriptor     = V::Read<std::uint64_t>(header, 8);
    raw.stride         = V::Read<std::uint32_t>(header, 16);
    raw.vertexCount    = V::Read<std::uint32_t>(header, 20);
    const auto indices = V::Read<std::uint32_t>(header, 24);
    const auto bones   = V::Read<std::uint32_t>(header, 28);
    REQUIRE(raw.vertexCount <= 65535);
    REQUIRE(raw.stride <= 60);
    REQUIRE(indices <= 65535 * 3);
    raw.vertices.resize(std::size_t(raw.vertexCount) * raw.stride);
    raw.indices.resize(indices);
    REQUIRE(bool(file.read(reinterpret_cast<char*>(raw.vertices.data()), raw.vertices.size())));
    REQUIRE(bool(file.read(reinterpret_cast<char*>(raw.indices.data()), raw.indices.size() * 2)));
    CHECK(file.peek() == std::char_traits<char>::eof());
    auto dynamic = entry.path();
    dynamic.replace_extension(".positions");
    if (std::filesystem::exists(dynamic))
    {
      std::ifstream positions(dynamic, std::ios::binary);
      raw.positions.resize(raw.vertexCount);
      REQUIRE(bool(positions.read(reinterpret_cast<char*>(raw.positions.data()), raw.positions.size() * sizeof(P::Vec3))));
      raw.dynamic = true;
    }
    auto geometry = M::Decode(raw, {0}, bones, {});
    if (!geometry) INFO(geometry.error().field);
    REQUIRE(geometry);
    // Skin links/matrices are synthetic: this fixture verifies packed data,
    // not the live skeleton adapter, materials or game rendering.
    auto checked = P::ValidatedAsset::Parse(Asset(std::move(*geometry), bones));
    if (!checked) INFO(checked.error().field);
    REQUIRE(checked);
    auto prepared = P::Prepare(*checked);
    REQUIRE(prepared);
    auto roundtrip = P::ReadAsset(*prepared->compressed, prepared->rawBytes);
    REQUIRE(roundtrip);
    CHECK(roundtrip->Value().geometry[0].vertices.size() == raw.vertexCount);
    for (std::size_t i = 0; i < raw.vertexCount; ++i)
    {
      const auto& before = checked->Value().geometry[0].vertices[i];
      const auto& after  = roundtrip->Value().geometry[0].vertices[i];
      REQUIRE(after.weights == before.weights);
      REQUIRE(after.bones == before.bones);
    }
    if (entry.path().stem().string().ends_with("-0")) combined.geometry.push_back(checked->Value().geometry[0]);
    ++streams;
  }
  CHECK(streams > 0);
#ifdef DREAMSLEEVE_DIAGNOSTICS
  // Combine one stream per shape to exercise aggregate pose admission too.
  // Includes helpers/overlays; this is an upper-bound fixture, not live selection.
  auto whole = P::ValidatedAsset::Parse(std::move(combined));
  REQUIRE(whole);
  P::Snapshot pose{{1}, {1}, 1, 50000};
  pose.channels.resize(1);
  for (std::uint32_t i = 0; i < whole->Value().geometry.size(); ++i)
  {
    const auto& mesh = whole->Value().geometry[i];
    pose.bounds.push_back({{}, 1});
    if (mesh.dynamic)
    {
      P::Deformation deformation{i};
      for (const auto& v : mesh.vertices)
      {
        deformation.positions.push_back(v.position);
        deformation.normals.push_back(v.normal);
      }
      pose.deformations.push_back(std::move(deformation));
    }
  }
  auto local = Dreamsleeve::Client::Diagnostics::CaptureLimits();
  auto bytes = P::WriteSnapshot(pose, *whole, local);
  REQUIRE(bytes);
  auto read = P::ReadSnapshot(*bytes, *whole, local);
  REQUIRE(read);
  REQUIRE(read->deformations.size() == pose.deformations.size());
  for (std::size_t i = 0; i < read->deformations.size(); ++i)
  {
    CHECK(read->deformations[i].positions == pose.deformations[i].positions);
    CHECK(read->deformations[i].normals == pose.deformations[i].normals);
  }
#endif
}
