#include <doctest/doctest.h>

import std;
import Dreamsleeve.Client.Domain.Logic;

using namespace Domain;

namespace
{

  const FormKey Whiterun{"skyrim.esm", 0x1A26F};
  const FormKey Riften{"skyrim.esm", 0x16BB4};

  PlayerLocation At(Position position, FormKey space = Whiterun, std::string label = "Whiterun")
  {
    return PlayerLocation{
        .location = {space, std::move(label)},
        .position = position
    };
  }

  Player Placed(std::uint32_t generation, std::uint64_t viewRevision, std::uint64_t sequence)
  {
    Player player{
        .data                = {7, "seven", "Seven"},
        .characterGeneration = generation
    };
    player.location         = At({1, 2, 3});
    player.viewRevision     = viewRevision;
    player.movementSequence = sequence;
    return player;
  }

}

TEST_SUITE_BEGIN("Client.Domain");

TEST_CASE("Domain Unix milliseconds preserve signed instants and millisecond precision")
{
  for (const std::int64_t value : {-62135596800000LL, -1LL, 0LL, 1727312345123LL, 253402300799999LL})
    CHECK(ToUnixMilliseconds(FromUnixMilliseconds(value)) == value);
}

TEST_CASE("Checks accept finite numbers, named forms and places, and the Tamriel calendar")
{
  const auto nan = std::numeric_limits<float>::quiet_NaN();
  CHECK(Checks::Finite(1.0f, -2.0, 0.0f));
  CHECK_FALSE(Checks::Finite(1.0f, std::numeric_limits<double>::infinity()));
  CHECK_FALSE(Checks::Finite(Position{0, nan, 0}));
  CHECK_FALSE(Checks::Finite(CameraDirection{0, 0, -std::numeric_limits<float>::infinity()}));

  CHECK(Checks::ValidKey(Whiterun));
  CHECK_FALSE(Checks::ValidKey({"", 0x1A26F}));
  CHECK_FALSE(Checks::ValidKey({"skyrim.esm", InvalidId}));
  CHECK(
    Checks::ValidPlacement({
        Whiterun,
        {1, 2, 3},
        1.5f
  }));
  CHECK_FALSE(
    Checks::ValidPlacement({
        Whiterun,
        {1, 2, 3},
        nan
  }));
  CHECK_FALSE(
    Checks::ValidPlacement({
        {"", 1},
        {1, 2, 3},
        0
  }));

  CHECK(Checks::ValidGameDate({4, 201, 8, 17, 2, 14, 5}));
  CHECK(Checks::ValidGameDate({99, 99999, 12, 31, 6, 23, 59}));
  for (
    const GameDate date : {
        GameDate{0, 201, 8,  17, 2, 0,  0 }, // no era 0
        GameDate{4, 201, 2,  29, 0, 0,  0 }, // no leap day
        GameDate{4, 201, 13, 1,  0, 0,  0 }, // twelve months
        GameDate{4, 201, 8,  17, 7, 0,  0 }, // seven days of the week
        GameDate{4, 201, 8,  17, 2, 24, 0 }, // no hour 24
        GameDate{4, 201, 8,  17, 2, 0,  60},
  })
    CHECK_FALSE(Checks::ValidGameDate(date));
}

TEST_CASE("Calendar months have fixed Tamriel lengths and no thirteenth")
{
  CHECK(Calendar::MonthLength(1) == 31);
  CHECK(Calendar::MonthLength(2) == 28);
  CHECK(Calendar::MonthLength(4) == 30);
  CHECK(Calendar::MonthLength(12) == 31);
  CHECK(Calendar::MonthLength(0) == 0);
  CHECK(Calendar::MonthLength(13) == 0);
}

TEST_CASE("Spatial distance widens before subtraction and multiplication")
{
  const auto largest = std::numeric_limits<float>::max();
  CHECK(std::isfinite(Spatial::DistanceSquared({largest, largest, largest}, {-largest, -largest, -largest})));
  CHECK(std::isfinite(Spatial::Distance({largest, largest, largest}, {-largest, -largest, -largest})));
  CHECK(Spatial::DistanceSquared({}, {3, 4, 0}) == 25.0);
  CHECK(Spatial::Distance({}, {3, 4, 0}) == 5.0);
}

TEST_CASE("Spatial reach is within the observer's space and radius, the boundary included")
{
  CHECK(Spatial::Reach(Whiterun, {}, Whiterun, {3, 4, 0}, 5) == std::optional<double>{5.0});
  CHECK_FALSE(Spatial::Reach(Whiterun, {}, Whiterun, {3, 4.001f, 0}, 5));
  CHECK_FALSE(Spatial::Reach(Whiterun, {}, Riften, {0, 0, 0}, 5));
  CHECK(Spatial::Reach(Whiterun, {}, Whiterun, {}, 0) == std::optional<double>{0.0});
  // A label is not part of the space.
  CHECK(Spatial::SameSpace(At({}, Whiterun, "Вайтран"), At({9, 9, 9}, Whiterun, "Whiterun")));
  CHECK_FALSE(Spatial::SameSpace(At({}, Whiterun), At({}, {"other.esm", 0x1A26F})));
}

TEST_CASE("Spatial jumps are a change of space or a move beyond the teleport threshold")
{
  CHECK_FALSE(Spatial::Jumped(At({}), At({3, 4, 0}), 5));
  CHECK(Spatial::Jumped(At({}), At({3, 4.001f, 0}), 5));
  CHECK(Spatial::Jumped(At({}), At({}, Riften), 5));
}

TEST_CASE("Spatial space changes on the first space and on another one, never on the same")
{
  CHECK(Spatial::SpaceChanged(std::nullopt, Whiterun));
  CHECK(Spatial::SpaceChanged(Riften, Whiterun));
  CHECK_FALSE(Spatial::SpaceChanged(Whiterun, Whiterun));
}

TEST_CASE("Spatial keeps the nearest items first and hides a shown one only past the hysteresis")
{
  std::vector<std::pair<char, double>> items{
      {'c', 30},
      {'a', 10},
      {'d', 40},
      {'b', 20}
  };
  Spatial::KeepNearest(items, 2, &std::pair<char, double>::second);
  REQUIRE(items.size() == 2);
  CHECK(items[0].first == 'a');
  CHECK(items[1].first == 'b');
  Spatial::KeepNearest(items, 10, &std::pair<char, double>::second);
  CHECK(items.size() == 2);

  CHECK(Spatial::ShownWithin(100, 100, false, 1.1));
  CHECK_FALSE(Spatial::ShownWithin(105, 100, false, 1.1));
  CHECK(Spatial::ShownWithin(105, 100, true, 1.1));
  CHECK_FALSE(Spatial::ShownWithin(111, 100, true, 1.1));
}

TEST_CASE("Motion blends position and retains latest camera telemetry")
{
  auto from            = At({0, 0, 0});
  from.cameraDirection = {0, 1, 0};
  from.sampledAtUs     = 100;
  auto to              = At({10, 20, -30}, Whiterun, "Вайтран");
  to.cameraDirection   = {0, -1, 0};
  to.sampledAtUs       = 200;

  const auto middle = Motion::Blend(from, to, 0.5);
  CHECK(middle.position == Position{5, 10, -15});
  CHECK(middle.location.locationName == "Вайтран");
  CHECK(middle.sampledAtUs == 0);
  CHECK(middle.cameraDirection == to.cameraDirection);
  CHECK(Motion::Blend(from, to, 0).position == from.position);
  CHECK(Motion::Blend(from, to, 1).position == to.position);
}

TEST_CASE("Motion samples replace each other within one context and move a player within its space")
{
  CHECK(Motion::SameContext(std::nullopt, std::nullopt));
  CHECK(Motion::SameContext(At({}), At({50, 0, 0}, Whiterun, "Вайтран")));
  CHECK_FALSE(Motion::SameContext(At({}), At({}, Riften)));
  CHECK_FALSE(Motion::SameContext(std::nullopt, At({})));
  CHECK_FALSE(Motion::SameContext(At({}), std::nullopt));

  auto         location = At({1, 2, 3});
  MovementPose pose;
  pose.position        = {4, 5, 6};
  pose.cameraDirection = {0, 0, 1};
  pose.sampledAtUs     = 77;
  Motion::Apply(location, pose);
  CHECK(location.position == Position{4, 5, 6});
  CHECK(location.cameraDirection == CameraDirection{0, 0, 1});
  CHECK(location.sampledAtUs == 77);
  CHECK(location.location.locationId == Whiterun);
}

TEST_CASE("Motion source time follows the source clock within the budget and breaks on a gap")
{
  using Clock = std::chrono::steady_clock;
  using std::chrono::milliseconds;
  const Clock::time_point start{};
  const auto              delay  = milliseconds{100};
  const auto              maxGap = milliseconds{500};
  const auto              at     = [&](std::uint64_t previous, std::uint64_t stamp, milliseconds received) {
    return Motion::SourceTime<Clock>(start, previous, stamp, start + received, delay, maxGap);
  };

  // The source's own 50 ms, not the 60 ms of network jitter.
  CHECK(at(1'000'000, 1'050'000, milliseconds{60}) == start + milliseconds{50});
  // Unstamped samples are timed by receipt.
  CHECK(at(0, 0, milliseconds{60}) == start + milliseconds{60});
  CHECK_FALSE(at(1'050'000, 1'000'000, milliseconds{60}));   // The source clock restarted.
  CHECK_FALSE(at(1'000'000, 1'600'000, milliseconds{600}));  // Paused beyond maxGap.
  CHECK_FALSE(at(0, 1'000'000, milliseconds{60}));           // Only one sample is stamped.
  CHECK_FALSE(at(1'000'000, 0, milliseconds{60}));
  CHECK_FALSE(at(1'000'000, 1'300'000, milliseconds{100}));  // Ahead of receipt by more than delay.
}

TEST_CASE("Players keep newer motion over a lagging profile and accept only later samples of their view")
{
  const auto known = Placed(1, 5, 10);
  CHECK(Players::KeepsMotion(known, Placed(1, 0, 0)));
  CHECK(Players::KeepsMotion(known, Placed(1, 5, 9)));
  CHECK_FALSE(Players::KeepsMotion(known, Placed(1, 5, 11)));
  CHECK_FALSE(Players::KeepsMotion(known, Placed(1, 6, 0)));
  CHECK_FALSE(Players::KeepsMotion(known, Placed(2, 0, 0)));
  CHECK_FALSE(Players::KeepsMotion(Placed(1, 0, 0), Placed(1, 0, 0)));

  CHECK(Players::AcceptsMovement(known, 5, 11));
  CHECK_FALSE(Players::AcceptsMovement(known, 5, 10));
  CHECK_FALSE(Players::AcceptsMovement(known, 6, 11));
  CHECK_FALSE(Players::AcceptsMovement(known, 0, 11));
  auto unplaced = known;
  unplaced.location.reset();
  CHECK_FALSE(Players::AcceptsMovement(unplaced, 5, 11));
}

TEST_CASE("Players merge a lagging profile with the motion already known and take the rest as sent")
{
  const auto known        = Placed(1, 5, 10);
  auto       lagged       = Placed(1, 0, 0);
  lagged.location         = At({9, 9, 9}, Riften);
  lagged.data.displayName = "Renamed";
  const auto merged       = Players::Merge(known, lagged);
  CHECK(merged.location == known.location);
  CHECK(merged.viewRevision == 5);
  CHECK(merged.movementSequence == 10);
  CHECK(merged.data.displayName == "Renamed");

  const auto newer = Placed(2, 0, 0);
  CHECK(Players::Merge(known, newer) == newer);
}

TEST_CASE("Game readings become whole points, negative ones included, within the wire range")
{
  CHECK(Players::Points(149.4f) == 149);
  CHECK(Players::Points(149.5f) == 150);
  CHECK(Players::Points(-15.6f) == -16);  // The game does not clamp a hit to zero health.
  CHECK(Players::Points(-0.4f) == 0);
  CHECK(Players::Points(1e12f) == (std::numeric_limits<std::int32_t>::max)());
  CHECK(Players::Points(-1e12f) == (std::numeric_limits<std::int32_t>::min)());
}

TEST_CASE("Actor value patches remove keys before setting readings; details patches replace and clear components")
{
  ActorValueStorage values{
      {"skyrim:health",  {"Здоровье", ResourceActorValue{100, 150}}},
      {"skyrim:magicka", {"Магия", ResourceActorValue{50, 80}}     }
  };
  Players::Apply(
    values,
    ActorValuesPatch{
        .removed = {"skyrim:health", "skyrim:magicka"},
        .set     = {{"skyrim:health", {"Health", ResourceActorValue{-5, 150}}}}
  });
  CHECK(
    values == ActorValueStorage{
                  {"skyrim:health", {"Health", ResourceActorValue{-5, 150}}}
  });

  PlayerDetails details{
      .race  = NamedForm{{"Skyrim.esm", 0x13746}, "Nord"},
      .level = 10
  };
  details.place = PlaceDescription{"Skyrim", "Whiterun", "", "", false};
  PlayerDetailsPatch patch;
  patch.level    = std::optional<std::uint32_t>{11};
  patch.place    = std::optional<PlaceDescription>{};
  patch.activity = PlayerActivity{.kind = ActivityKind::Combat};
  Players::Apply(details, patch);
  CHECK(details.level == 11u);
  CHECK_FALSE(details.place.has_value());
  CHECK(details.activity.kind == ActivityKind::Combat);
  REQUIRE(details.race.has_value());
  CHECK(details.race->name == "Nord");  // Absent from the patch: unchanged.
}

TEST_CASE("Chat channels carry announcements exactly when they are system channels")
{
  ChatMessage plain;
  ChatMessage announced;
  announced.announcement = Announcement{};
  CHECK(Chat::FitsChannel(ChatChannelKind::Global, plain));
  CHECK_FALSE(Chat::FitsChannel(ChatChannelKind::Global, announced));
  CHECK(Chat::FitsChannel(ChatChannelKind::System, announced));
  CHECK_FALSE(Chat::FitsChannel(ChatChannelKind::System, plain));
}

TEST_CASE("Announcements are admitted from an allowed source within lengths in code points")
{
  const AnnouncementPolicy policy{{ClientAnnouncementSource::ThirdParty}, 5, 3};
  CHECK(Announcements::Admits(policy, ClientAnnouncementSource::ThirdParty, "Тролл", "Мод"));
  CHECK_FALSE(Announcements::Admits(policy, ClientAnnouncementSource::TrustedClient, "Тролл", "Мод"));
  CHECK_FALSE(Announcements::Admits(policy, ClientAnnouncementSource::ThirdParty, "Тролль", "Мод"));
  CHECK_FALSE(Announcements::Admits(policy, ClientAnnouncementSource::ThirdParty, "Тролл", "Моды"));
  CHECK_FALSE(Announcements::Admits(AnnouncementPolicy{}, ClientAnnouncementSource::ThirdParty, "", ""));
}

TEST_SUITE_END();
