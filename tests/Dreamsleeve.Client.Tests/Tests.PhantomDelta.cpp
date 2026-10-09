#include <doctest/doctest.h>
#include <zstd.h>
#include <cstdlib>
import std;
import Dreamsleeve.Client.Phantom.Delta;
namespace P = Dreamsleeve::Client::Phantom;

namespace
{

  P::Bytes Pack(const P::Bytes& raw)
  {
    P::Bytes bytes(ZSTD_compressBound(raw.size()));
    auto     size = ZSTD_compress(bytes.data(), bytes.size(), raw.data(), raw.size(), 3);
    REQUIRE_FALSE(ZSTD_isError(size));
    bytes.resize(size);
    return bytes;
  }

}

TEST_CASE("Asset delta preserves exact canonical compressed identity and rejects malformed frames")
{
  P::Bytes      raw(256 * 1024);
  std::uint32_t state = 42;
  for (auto& b : raw)
  {
    state = state * 1664525 + 1013904223;
    b     = static_cast<std::uint8_t>(state >> 24);
  }

  auto base    = Pack(raw);
  raw[17000]  ^= 0x55;
  auto target  = Pack(raw);
  auto patch   = P::Delta::Create(base, target);
  REQUIRE(patch);
  CHECK(patch->size() < target.size() / 10);

  auto full = P::Delta::Apply(base, *patch, static_cast<std::uint32_t>(raw.size()));
  REQUIRE(full);
  CHECK(*full == target);

  CHECK_FALSE(P::Delta::Apply(base, *patch, 1));
  patch->push_back(0);
  CHECK_FALSE(P::Delta::Apply(base, *patch, static_cast<std::uint32_t>(raw.size())));
  CHECK_FALSE(P::Delta::Create({}, target));
}

TEST_CASE("Asset delta recorded fixture reconstructs byte identical target")
{
  char*       path{};
  std::size_t length{};
  if (_dupenv_s(&path, &length, "DREAMSLEEVE_DELTA_FIXTURE") != 0 || !path) return;
  const std::filesystem::path root(path);
  std::free(path);

  auto read = [](const std::filesystem::path& name) {
    std::ifstream f(name, std::ios::binary);
    REQUIRE(f);
    return P::Bytes(std::istreambuf_iterator<char>(f), {});
  };

  auto       base = read(root / "base.zst");
  auto       target = read(root / "target.zst");
  const auto raw   = static_cast<std::uint32_t>(ZSTD_getFrameContentSize(target.data(), target.size()));

  const auto begin = std::chrono::steady_clock::now();
  auto       patch = P::Delta::Create(base, target);
  REQUIRE(patch);
  const auto encoded = std::chrono::steady_clock::now();
  auto       full    = P::Delta::Apply(base, *patch, raw);
  REQUIRE(full);
  REQUIRE(*full == target);
  std::println(
    "DELTA full={} patch={} encode_ms={} apply_ms={}",
    target.size(),
    patch->size(),
    std::chrono::duration<double, std::milli>(encoded - begin).count(),
    std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - encoded).count());

  std::ofstream f(root / "patch.zst", std::ios::binary);
  f.write(reinterpret_cast<const char*>(patch->data()), patch->size());
  REQUIRE(f);
}
