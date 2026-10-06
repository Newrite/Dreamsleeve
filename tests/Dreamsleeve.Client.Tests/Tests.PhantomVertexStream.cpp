#include <cstdlib>
#include <fstream>
#include <string>
#include <doctest/doctest.h>
import std;
import Dreamsleeve.Client.Phantom.Types;
import Dreamsleeve.Game.PhantomVertexStream;

namespace Stream  = Dreamsleeve::Game::PhantomVertexStream;
namespace Phantom = Dreamsleeve::Client::Phantom;

namespace
{

  // Installed SSE Warhammer_Mesh: FP32 position; UV16, normal20, tangent24.
  // VF_FULLPREC is absent. No third-party mesh bytes are shipped by this test.
  constexpr std::uint64_t Wide   = 0x1b00000650407;
  constexpr std::uint64_t Narrow = 5ULL | (2ULL << 8) | (3ULL << 16) | (4ULL << 20) | (0x1bULL << 44);

  std::string VertexFixturePath()
  {
    char*       text{};
    std::size_t size{};
    if (_dupenv_s(&text, &size, "DREAMSLEEVE_PHANTOM_VERTEX_FIXTURE") != 0) return {};
    const auto owner = std::unique_ptr<char, decltype(&std::free)>(text, &std::free);
    return owner && size > 1 && size <= 32768 ? std::string(owner.get()) : std::string{};
  }

  template <class T, std::size_t N>
  void Put(std::array<std::byte, N>& bytes, std::size_t at, T value)
  {
    std::memcpy(bytes.data() + at, &value, sizeof(value));
  }

}

TEST_CASE("SSE rigid positions decode as FP32 without the FULLPREC flag")
{
  REQUIRE((Wide & (1ULL << 54)) == 0);
  auto layout = Stream::PositionLayout::From(Wide, 28);
  REQUIRE(layout);
  std::array<std::byte, 28> bytes{};
  // Finite FP32 whose lower half is NaN if incorrectly read as FP16.
  const float x = std::bit_cast<float>(0x3f807c00U);
  Put(bytes, 0, x);
  Put(bytes, 4, -19.25f);
  Put(bytes, 8, 0.3125f);
  CHECK_FALSE(std::isfinite(Stream::Half(Stream::Read<std::uint16_t>(bytes, 0))));
  auto position = layout->Decode(bytes);
  REQUIRE(position);
  CHECK(position->x == x);
  CHECK(position->y == -19.25f);
  CHECK(position->z == 0.3125f);
}

TEST_CASE("Packed skin-style FP16 positions remain FP16")
{
  auto layout = Stream::PositionLayout::From(Narrow, 20);
  REQUIRE(layout);
  std::array<std::byte, 20> bytes{};
  Put(bytes, 0, std::uint16_t{0x3c00});
  Put(bytes, 2, std::uint16_t{0xc000});
  Put(bytes, 4, std::uint16_t{0x3800});
  auto position = layout->Decode(bytes);
  REQUIRE(position);
  CHECK(position->x == 1);
  CHECK(position->y == -2);
  CHECK(position->z == 0.5f);
}

TEST_CASE("Position layout rejects contradictory offsets and stride")
{
  CHECK(Stream::PositionLayout::From(Wide | (1ULL << 54), 28));
  CHECK(Stream::PositionLayout::From(4ULL | (1ULL << 44), 16));
  CHECK_FALSE(Stream::PositionLayout::From(Narrow | (1ULL << 54), 20));
  CHECK_FALSE(Stream::PositionLayout::From(Wide, 20));
  CHECK_FALSE(Stream::PositionLayout::From(Wide & ~(15ULL << 8), 28));
  CHECK_FALSE(Stream::PositionLayout::From((Wide & ~(15ULL << 8)) | (3ULL << 8), 28));
}

TEST_CASE("Invalid position bytes never trigger a precision fallback")
{
  auto wide   = Stream::PositionLayout::From(Wide, 28);
  auto narrow = Stream::PositionLayout::From(Narrow, 20);
  REQUIRE(wide);
  REQUIRE(narrow);
  std::array<std::byte, 28> bytes{};
  Put(bytes, 0, std::numeric_limits<float>::quiet_NaN());
  auto invalid = wide->Decode(bytes);
  REQUIRE_FALSE(invalid);
  CHECK(invalid.error().reason == Phantom::Failure::InvalidNumber);
  CHECK_FALSE(wide->Decode(std::span(bytes).first(12)));
  Put(bytes, 0, std::uint16_t{0x7c00});
  CHECK_FALSE(narrow->Decode(bytes));
  CHECK_FALSE(narrow->Decode(std::span(bytes).first(4)));
}

TEST_CASE("External packed vertex fixture matches independent position oracle" * doctest::skip(VertexFixturePath().empty()))
{
  std::ifstream file(VertexFixturePath(), std::ios::binary);
  REQUIRE(file);
  std::array<std::byte, 24> header{};
  REQUIRE(bool(file.read(reinterpret_cast<char*>(header.data()), header.size())));
  REQUIRE(std::memcmp(header.data(), "DLPVTX01", 8) == 0);
  const auto descriptor = Stream::Read<std::uint64_t>(header, 8);
  const auto stride     = Stream::Read<std::uint32_t>(header, 16);
  const auto count      = Stream::Read<std::uint32_t>(header, 20);
  REQUIRE(count > 0);
  REQUIRE(count <= 65535);
  auto layout = Stream::PositionLayout::From(descriptor, stride);
  REQUIRE(layout);
  std::vector<std::byte> vertices(std::size_t(count) * stride);
  REQUIRE(bool(file.read(reinterpret_cast<char*>(vertices.data()), vertices.size())));
  for (std::uint32_t i = 0; i < count; ++i)
  {
    std::array<float, 3> expected{};
    REQUIRE(bool(file.read(reinterpret_cast<char*>(expected.data()), sizeof(expected))));
    auto actual = layout->Decode(std::span(vertices).subspan(std::size_t(i) * stride, stride));
    REQUIRE(actual);
    CHECK(actual->x == expected[0]);
    CHECK(actual->y == expected[1]);
    CHECK(actual->z == expected[2]);
  }
  CHECK(file.peek() == std::char_traits<char>::eof());
}
