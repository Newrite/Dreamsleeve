#include <doctest/doctest.h>

import std;
import Dreamsleeve.Client.Domain.Logic;

using namespace Domain;

TEST_SUITE_BEGIN("Client.Domain");

TEST_CASE("Domain key helpers fold ASCII while preserving opaque filename bytes")
{
  CHECK(Normalize::PluginName(" Skyrim.ESM ") == " skyrim.esm ");
  CHECK(Normalize::PluginName("Caf\xC3\xA9.ESM") == "caf\xC3\xA9.esm");
  CHECK(Normalize::PluginName("Caf\xC3\xA9.ESM") != Normalize::PluginName("Cafe\xCC\x81.ESM"));
}

TEST_CASE("Domain distance calculations widen before subtraction and multiplication")
{
  const auto largest = std::numeric_limits<float>::max();
  CHECK(std::isfinite(Spatial::DistanceSquared({largest, largest, largest}, {-largest, -largest, -largest})));
  CHECK(std::isfinite(Spatial::Distance({largest, largest, largest}, {-largest, -largest, -largest})));
  CHECK(Spatial::DistanceSquared({}, {3, 4, 0}) == 25.0);
  CHECK(Spatial::Distance({}, {3, 4, 0}) == 5.0);
}

TEST_CASE("Domain Unix milliseconds preserve signed instants and millisecond precision")
{
  for (const std::int64_t value : {-62135596800000LL, -1LL, 0LL, 1727312345123LL, 253402300799999LL})
    CHECK(ToUnixMilliseconds(FromUnixMilliseconds(value)) == value);
}

TEST_SUITE_END();
