#include <doctest/doctest.h>

import std;
import Dreamsleeve.Client.Domain.Logic;

using namespace Domain;

namespace DomainTests
{
  PlayerLocation Location(LocalFormId id = 1, Position position = {})
  {
    return PlayerLocation{
      .location = {.locationId = {"skyrim.esm", id}, .locationName = "Whiterun"},
      .position = position
    };
  }

  Player Character()
  {
    return Player{
      .data = {1, "newrite", "Неревар"},
      .characterName = "Nerevar",
      .location = Location(),
      .actorValues = {{"skyrim:health", {"Health", ResourceActorValue{80, 100}}}}
    };
  }
}

TEST_SUITE_BEGIN("Client.Domain");

TEST_CASE("Domain key helpers fold ASCII while preserving opaque filename and value bytes")
{
  CHECK(Normalize::PluginName(" Skyrim.ESM ") == " skyrim.esm ");
  CHECK(Normalize::PluginName("Caf\xC3\xA9.ESM") == "caf\xC3\xA9.esm");
  CHECK(Normalize::PluginName("Caf\xC3\xA9.ESM") != Normalize::PluginName("Cafe\xCC\x81.ESM"));
  CHECK(Normalize::ActorValueKey("SKYRIM:Health") == "skyrim:health");
  CHECK(Normalize::ActorValueKey("AVG:Custom:Value ") == "avg:custom:value ");
  CHECK(Normalize::ActorValueKey("avg:Caf\xC3\xA9") != Normalize::ActorValueKey("avg:Cafe\xCC\x81"));
}

TEST_CASE("Domain distance calculations widen before subtraction and multiplication")
{
  const auto largest = std::numeric_limits<float>::max();
  CHECK(std::isfinite(Spatial::DistanceSquared({largest, largest, largest}, {-largest, -largest, -largest})));
  CHECK(std::isfinite(Spatial::Distance({largest, largest, largest}, {-largest, -largest, -largest})));
  CHECK(Spatial::DistanceSquared({}, {3, 4, 0}) == 25.0);
  CHECK(Spatial::Distance({}, {3, 4, 0}) == 5.0);
}

TEST_CASE("Domain coordinate space uses the supplied FormKey independently of display labels")
{
  const auto left = DomainTests::Location();
  auto right = DomainTests::Location(1, {3, 4, 0});
  right.location.locationName = "Вайтран";
  CHECK(Spatial::IsSameSpace(left, right));
  CHECK(Spatial::TryDistance(left, right) == std::optional<double>{5.0});
  right.location.locationId.localFormId = 2;
  CHECK_FALSE(Spatial::TryDistance(left, right).has_value());
  right.location.locationId = {"Other.esp", 1};
  CHECK_FALSE(Spatial::IsSameSpace(left, right));
}

TEST_CASE("Domain radius checks include the boundary and reject negative radius")
{
  const auto left = DomainTests::Location();
  const auto right = DomainTests::Location(1, {3, 4, 0});
  REQUIRE(Spatial::IsWithinRadius(5, left, right).has_value());
  CHECK(*Spatial::IsWithinRadius(5, left, right));
  CHECK_FALSE(*Spatial::IsWithinRadius(4, left, right));
  CHECK(*Spatial::IsWithinRadius(0, left, left));
  CHECK_FALSE(*Spatial::IsWithinRadius(500, left, DomainTests::Location(2)));
  // Finite coordinates/radii are a boundary precondition after 36ea57e.
  // The client no longer duplicates the server's IsFinite validation.
  const auto result = Spatial::IsWithinRadius(-1.0f, left, right);
  REQUIRE_FALSE(result.has_value());
  CHECK(result.error().code == ErrorCode::InvalidRadius);
}

TEST_CASE("Domain Actor Value writes preserve alternatives and maxima without gameplay validation")
{
  ActorValueState scalar = ScalarActorValue{-20};
  ActorValueState resource = ResourceActorValue{120, -50};
  CHECK_FALSE(ActorValues::TryGetMaximum(scalar).has_value());
  CHECK(ActorValues::TryGetMaximum(resource) == std::optional<float>{-50.0f});
  ActorValues::SetCurrent(scalar, -3);
  ActorValues::SetCurrent(resource, 150);
  CHECK(std::holds_alternative<ScalarActorValue>(scalar));
  CHECK(ActorValues::GetCurrent(scalar) == -3);
  CHECK(std::holds_alternative<ResourceActorValue>(resource));
  CHECK(ActorValues::GetCurrent(resource) == 150);
  CHECK(ActorValues::TryGetMaximum(resource) == std::optional<float>{-50.0f});
  ActorValues::SetCurrent(resource, std::numeric_limits<float>::infinity());
  CHECK(std::isinf(ActorValues::GetCurrent(resource)));
  CHECK(ActorValues::TryGetMaximum(resource) == std::optional<float>{-50.0f});
}

TEST_CASE("Domain beginning a character resets old telemetry even if the name did not change")
{
  auto player = DomainTests::Character();
  const auto profile = player.data;
  Players::BeginCharacter(player, "Nerevar");
  CHECK(player.data == profile);
  CHECK(player.characterName == std::optional<std::string>{"Nerevar"});
  CHECK_FALSE(player.location.has_value());
  CHECK(player.actorValues.empty());
  Players::BeginCharacter(player, "");
  CHECK(player.characterName == std::optional<std::string>{""});
}

TEST_CASE("Domain leaving the game clears character data without losing the online profile")
{
  auto player = DomainTests::Character();
  const auto profile = player.data;
  Players::ClearGameState(player);
  CHECK(player.data == profile);
  CHECK_FALSE(player.characterName.has_value());
  CHECK_FALSE(player.location.has_value());
  CHECK(player.actorValues.empty());
}

TEST_CASE("Domain Unix milliseconds preserve signed instants and millisecond precision")
{
  for (const std::int64_t value : {-62135596800000LL, -1LL, 0LL, 1727312345123LL, 253402300799999LL})
    CHECK(ToUnixMilliseconds(FromUnixMilliseconds(value)) == value);
}

TEST_SUITE_END();
