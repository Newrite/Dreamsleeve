#include <doctest/doctest.h>
import std;
import Dreamsleeve.Client.Phantom.Masks;
import Dreamsleeve.Client.Phantom.Codec;

namespace
{
  namespace P = Dreamsleeve::Client::Phantom;

  std::shared_ptr<const P::AlphaMask> Mask(unsigned width, unsigned height, std::uint8_t value = 7)
  {
    return std::make_shared<const P::AlphaMask>(P::AlphaMask{width, height, std::vector<std::uint8_t>(width * height, value)});
  }

}

TEST_CASE("Alpha resource budget counts identical masks once with immutable shared ownership")
{
  P::Limits limits;
  limits.maskBytes = 8;
  P::AlphaMaskPool pool(limits);
  auto             first = pool.Intern(Mask(2, 2));
  REQUIRE(first);
  auto duplicate = pool.Intern(Mask(2, 2));
  REQUIRE(duplicate);
  CHECK(first->get() == duplicate->get());
  CHECK(pool.Bytes() == 4);
  auto reshaped = pool.Intern(Mask(4, 1));
  REQUIRE(reshaped);
  CHECK(reshaped->get() != first->get());
  CHECK(pool.Bytes() == 8);
  CHECK_FALSE(pool.Intern(Mask(2, 2, 8)));
  CHECK(pool.Intern(*first));
  CHECK(pool.Bytes() == 8);
  CHECK_FALSE(pool.Intern({}));
  CHECK_FALSE(pool.Intern(Mask(0, 0)));
}

TEST_CASE("Repeated hair masks pass complete neutral model encode decode within default budgets")
{
  P::Asset raw;
  raw.nodes.push_back({});
  // The real archive uses the same 2048x1024 hair map on 11 meshes.
  // Independent copies model resources arriving through a wire decoder.
  for (unsigned i = 0; i < 11; ++i)
  {
    P::Geometry mesh;
    mesh.vertices.resize(3);
    mesh.vertices[1].position = {1, 0, 0};
    mesh.vertices[2].position = {0, 1, 0};
    mesh.indices              = {0, 1, 2};
    mesh.mask                 = Mask(2048, 1024);
    raw.geometry.push_back(std::move(mesh));
  }
  auto parsed = P::ValidatedAsset::Parse(std::move(raw));
  REQUIRE(parsed);
  const auto& meshes = parsed->Value().geometry;
  for (const auto& mesh : meshes)
    CHECK(mesh.mask == meshes.front().mask);
  // Worker reservation still covers the repeated on-wire bytes.
  CHECK(parsed->MemoryBytes() >= 11 * 2048ULL * 1024);
  auto encoded = P::Prepare(*parsed);
  REQUIRE(encoded);
  auto decoded = P::ReadAsset(*encoded->compressed, encoded->rawBytes);
  REQUIRE(decoded);
  const auto& read = decoded->Value().geometry;
  REQUIRE(read.size() == 11);
  for (const auto& mesh : read)
  {
    CHECK(mesh.mask == read.front().mask);
    CHECK(mesh.mask->width == 2048);
    CHECK(mesh.mask->height == 1024);
    CHECK(mesh.mask->pixels == meshes.front().mask->pixels);
  }
  P::Limits small;
  small.assetBytes = encoded->rawBytes - 1;
  CHECK_FALSE(P::Prepare(*parsed, small));
  CHECK_FALSE(P::ReadAsset(*encoded->compressed, encoded->rawBytes, small));
}

TEST_CASE("Distinct alpha masks cannot bypass the shared resource budget")
{
  P::AlphaMaskPool pool;
  REQUIRE(pool.Intern(Mask(4096, 4096)));
  CHECK_FALSE(pool.Intern(Mask(1, 1, 8)));
  CHECK(pool.Bytes() == 16 * 1024 * 1024);
  auto malformed   = std::make_shared<P::AlphaMask>();
  malformed->width = malformed->height = 2;
  malformed->pixels.resize(3);
  CHECK_FALSE(pool.Intern(malformed));
}
