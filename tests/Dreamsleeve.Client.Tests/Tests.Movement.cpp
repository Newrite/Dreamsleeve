#include <doctest/doctest.h>

import std;
import Dreamsleeve.Client.MovementView;
import Dreamsleeve.Client.Exchange;

namespace
{
  using namespace Dreamsleeve::Client;
  using namespace std::chrono_literals;

  MovementClock::time_point At(int milliseconds)
  {
    return MovementClock::time_point{} + std::chrono::milliseconds{milliseconds};
  }

  Domain::PlayerLocation MovementLocation(float x, std::uint64_t stamp = 1000000)
  {
    return {{{"skyrim.esm", 0x3c}, "Tamriel"}, {x, 0, 0}, {}, stamp};
  }

  struct MovementFixture
  {
    ClientModel model;
    ClientExchange::Ptr exchange = std::move(*ClientExchange::TryCreate(8, 8));
    MovementView::Ptr view = std::move(*MovementView::TryCreate());
    ClientOutput output;

    MovementFixture()
    {
      Domain::Player player{.data = {7, "player", "Player"}, .characterGeneration = 1};
      REQUIRE(model.Apply(model.Generation(), PlayerUpserted{player}, At(0)));
      REQUIRE(exchange->Publish(model));
      Drain(0);
    }

    void Drain(int now)
    {
      exchange->Drain(output);
      view->Apply(output.state, At(now));
    }

    void Move(float x, int sourceMs, int receivedMs, bool drain = true)
    {
      auto location = MovementLocation(x, 1000000 + static_cast<std::uint64_t>(sourceMs) * 1000);
      REQUIRE(model.Apply(model.Generation(), PlayerLocationUpdated{7, location}, At(receivedMs)));
      if (drain)
      {
        REQUIRE(exchange->Publish(model));
        Drain(receivedMs);
      }
    }
  };
}

TEST_SUITE_BEGIN("Client.Movement");

TEST_CASE("Source sample intervals survive delivery jitter and a late game-thread drain")
{
  MovementFixture fixture;
  fixture.Move(0, 0, 100, false);
  fixture.Move(10, 100, 250, false);
  fixture.Move(20, 200, 260, false);
  REQUIRE(fixture.exchange->Publish(fixture.model));
  fixture.Drain(300);

  REQUIRE(fixture.output.state.updates.size() == 1);
  const auto& delta = std::get<ClientStateDelta>(fixture.output.state.updates.front());
  CHECK(delta.players.size() == 1);
  CHECK(delta.movement.size() == 3);
  REQUIRE(fixture.view->Sample(7, At(300)));
  CHECK(fixture.view->Sample(7, At(300))->position.X == doctest::Approx(5));
  CHECK(fixture.view->Sample(7, At(350))->position.X == doctest::Approx(10));
  CHECK(fixture.view->Sample(7, At(400))->position.X == doctest::Approx(15));
  CHECK(fixture.view->Sample(7, At(5000))->position.X == 20);
}

TEST_CASE("Rotation crosses the wrap boundary by the shortest arc")
{
  MovementFixture fixture;
  auto first = MovementLocation(0);
  auto second = MovementLocation(10, 1100000);
  first.rotation.Z = static_cast<float>(std::numbers::pi * 179 / 180);
  second.rotation.Z = -first.rotation.Z;
  REQUIRE(fixture.model.Apply(1, PlayerLocationUpdated{7, first}, At(100)));
  REQUIRE(fixture.model.Apply(1, PlayerLocationUpdated{7, second}, At(200)));
  REQUIRE(fixture.exchange->Publish(fixture.model));
  fixture.Drain(200);

  REQUIRE(fixture.view->Sample(7, At(300)));
  CHECK(std::abs(fixture.view->Sample(7, At(300))->rotation.Z) == doctest::Approx(std::numbers::pi));
}

TEST_CASE("Visibility loss and restoration inside one batch do not bridge old movement")
{
  MovementFixture fixture;
  fixture.Move(0, 0, 100);
  fixture.Move(10, 100, 200);
  REQUIRE(fixture.model.Apply(1, PlayerLocationUpdated{7, std::nullopt}, At(210)));
  fixture.Move(30, 200, 220, false);
  REQUIRE(fixture.exchange->Publish(fixture.model));
  fixture.Drain(220);

  CHECK(fixture.view->HistorySize(7) == 1);
  CHECK(fixture.view->Sample(7, At(250))->position.X == 30);
  REQUIRE(fixture.model.Apply(1, PlayerLocationUpdated{7, std::nullopt}, At(230)));
  REQUIRE(fixture.exchange->Publish(fixture.model));
  fixture.Drain(230);
  CHECK_FALSE(fixture.view->Sample(7, At(1000)));
}

TEST_CASE("Space character teleport and long gaps snap instead of interpolating")
{
  MovementFixture fixture;
  fixture.Move(0, 0, 100);
  fixture.Move(10, 100, 200);
  auto next = MovementLocation(20, 1200000);

  SUBCASE("Different plugin with the same form number") { next.location.locationId.pluginName = "other.esm"; }
  SUBCASE("Different cell") { next.location.locationId.localFormId = 0x42; }
  SUBCASE("Teleport") { next.position.X = 5000; }
  SUBCASE("Source clock restart") { next.sampledAtUs = 100; }
  SUBCASE("Source gap") { next.sampledAtUs = 9000000; }
  SUBCASE("Huge untrusted source timestamp") { next.sampledAtUs = std::numeric_limits<std::uint64_t>::max(); }
  SUBCASE("New character")
  {
    REQUIRE(fixture.model.Apply(1, PlayerCharacterStarted{7, "Another"}, At(250)));
  }
  REQUIRE(fixture.model.Apply(1, PlayerLocationUpdated{7, next}, At(300)));
  REQUIRE(fixture.exchange->Publish(fixture.model));
  fixture.Drain(300);

  CHECK(fixture.view->HistorySize(7) == 1);
  CHECK(fixture.view->Sample(7, At(300))->position.X == next.position.X);
}

TEST_CASE("Arrival gaps also reset and zero stamps use receive time")
{
  MovementFixture fixture;
  REQUIRE(fixture.model.Apply(1, PlayerLocationUpdated{7, MovementLocation(0, 0)}, At(100)));
  REQUIRE(fixture.model.Apply(1, PlayerLocationUpdated{7, MovementLocation(10, 0)}, At(200)));
  REQUIRE(fixture.exchange->Publish(fixture.model));
  fixture.Drain(250);
  CHECK(fixture.view->Sample(7, At(300))->position.X == doctest::Approx(5));

  REQUIRE(fixture.model.Apply(1, PlayerLocationUpdated{7, MovementLocation(20, 0)}, At(2000)));
  REQUIRE(fixture.exchange->Publish(fixture.model));
  fixture.Drain(2000);
  CHECK(fixture.view->HistorySize(7) == 1);
}

TEST_CASE("Metadata updates and duplicate state batches do not add measurements")
{
  MovementFixture fixture;
  fixture.Move(0, 0, 100);
  fixture.Move(10, 100, 200);
  fixture.view->Apply(fixture.output.state, At(210));
  REQUIRE(fixture.model.Apply(1, PlayerMetadataUpdated{7, Domain::ActorValueStorage{}, std::nullopt}));
  REQUIRE(fixture.exchange->Publish(fixture.model));
  fixture.Drain(220);
  CHECK(fixture.view->HistorySize(7) == 2);
}

TEST_CASE("Co-timed measurements replace without a zero interpolation denominator")
{
  MovementFixture fixture;
  fixture.Move(0, 0, 100);
  fixture.Move(5, 0, 110);
  CHECK(fixture.view->HistorySize(7) == 1);
  CHECK(fixture.view->Sample(7, At(300))->position.X == 5);
}

TEST_CASE("History capacity bounds a long moving stream")
{
  MovementFixture fixture;
  MovementSettings settings;
  settings.historyCapacity = 3;
  fixture.view = std::move(*MovementView::TryCreate(settings));
  REQUIRE(fixture.exchange->Publish(fixture.model, true));
  fixture.Drain(0);

  for (int index = 0; index < 100; ++index) fixture.Move(static_cast<float>(index), index * 100, index * 100 + 10);
  CHECK(fixture.view->HistorySize(7) == 3);
  CHECK(fixture.view->Sample(7, At(20000))->position.X == 99);
}

TEST_CASE("Player removal session reset and stale batches cannot resurrect a track")
{
  MovementFixture fixture;
  fixture.Move(0, 0, 100);
  auto stale = fixture.output.state;

  SUBCASE("Removal")
  {
    REQUIRE(fixture.model.Apply(1, PlayerRemoved{7}));
    REQUIRE(fixture.exchange->Publish(fixture.model));
  }
  SUBCASE("Disconnect")
  {
    fixture.model.ClearOnlineState();
    REQUIRE(fixture.exchange->Publish(fixture.model));
  }
  fixture.Drain(200);
  fixture.view->Apply(stale, At(210));
  CHECK_FALSE(fixture.view->Sample(7, At(300)));
}

TEST_CASE("Observation overflow recovers with a snapshot and resets history")
{
  MovementFixture fixture;
  fixture.model = ClientModel{2};
  Domain::Player player{.data = {7, "player", "Player"}};
  REQUIRE(fixture.model.Apply(1, PlayerUpserted{player}));
  REQUIRE(fixture.exchange->Publish(fixture.model, true));
  fixture.Drain(0);

  fixture.Move(0, 0, 100, false);
  fixture.Move(10, 100, 200, false);
  fixture.Move(20, 200, 300, false);
  REQUIRE(fixture.exchange->Publish(fixture.model));
  fixture.Drain(300);
  REQUIRE(fixture.output.state.updates.size() == 1);
  CHECK(std::holds_alternative<ClientSnapshot>(fixture.output.state.updates.front()));
  CHECK(fixture.view->HistorySize(7) == 1);
  CHECK(fixture.view->Sample(7, At(500))->position.X == 20);
}

TEST_CASE("State queue overflow resets interpolation instead of connecting across lost deltas")
{
  MovementFixture fixture;
  fixture.exchange = std::move(*ClientExchange::TryCreate(8, 1));
  REQUIRE(fixture.exchange->Publish(fixture.model));
  fixture.Drain(0);
  fixture.Move(0, 0, 100);
  fixture.Move(10, 100, 200, false);
  REQUIRE(fixture.exchange->Publish(fixture.model));
  fixture.Move(20, 200, 300, false);
  REQUIRE(fixture.exchange->Publish(fixture.model));
  fixture.Drain(300);
  CHECK(std::holds_alternative<ClientSnapshot>(fixture.output.state.updates.front()));
  CHECK(fixture.view->HistorySize(7) == 1);
}

TEST_CASE("Posting movement stamps before queue coalescing and retains adapter timestamps")
{
  auto exchange = ClientExchange::TryCreate(8, 8);
  REQUIRE(exchange);
  REQUIRE((*exchange)->Post({1, LocalMovement{MovementLocation(0, 0)}}) == CommandPostResult::Queued);
  std::vector<QueuedClientCommand> commands;
  REQUIRE((*exchange)->TakeCommands(commands));
  CHECK(std::get<LocalMovement>(commands.front().command).location->sampledAtUs != 0);

  REQUIRE((*exchange)->Post({1, LocalMovement{MovementLocation(10, 12345)}}) == CommandPostResult::Queued);
  REQUIRE((*exchange)->TakeCommands(commands));
  CHECK(std::get<LocalMovement>(commands.front().command).location->sampledAtUs == 12345);
}

TEST_CASE("Invalid history configuration returns an error")
{
  MovementSettings settings;
  SUBCASE("Capacity") { settings.historyCapacity = 1; }
  SUBCASE("Negative delay") { settings.delay = -1ms; }
  SUBCASE("Gap shorter than delay") { settings.maxGap = settings.delay; }
  SUBCASE("Unbounded time conversion") { settings.maxGap = std::chrono::milliseconds::max(); }
  SUBCASE("Infinite teleport threshold") { settings.teleportDistance = std::numeric_limits<double>::infinity(); }
  SUBCASE("Zero teleport threshold") { settings.teleportDistance = 0; }
  CHECK_FALSE(MovementView::TryCreate(settings));
}

TEST_CASE("Stationary samples retain the stop interval before movement resumes")
{
  MovementFixture fixture;
  fixture.Move(0, 0, 100);
  fixture.Move(0, 100, 200);
  fixture.Move(10, 200, 300);
  CHECK(fixture.view->HistorySize(7) == 3);
  CHECK(fixture.view->Sample(7, At(300))->position.X == 0);
  CHECK(fixture.view->Sample(7, At(400))->position.X == doctest::Approx(5));
}

TEST_CASE("Snapshot timeline starts at publication rather than delayed consumption")
{
  auto view = MovementView::TryCreate();
  REQUIRE(view);
  Domain::Player player{.data = {7, "player", "Player"}, .location = MovementLocation(0), .characterGeneration = 1};
  ClientSnapshot snapshot{.generation = 1, .revision = 1, .players = {player}, .observedAt = At(100)};
  ClientStateDelta delta{.generation = 1, .revision = 2};
  delta.movement.push_back({7, 1, At(240), MovementLocation(10, 1100000)});
  StateUpdateBatch batch{{snapshot, delta}};
  (*view)->Apply(batch, At(300));
  CHECK((*view)->Sample(7, At(300))->position.X == doctest::Approx(5));
}

TEST_CASE("A missing new-session snapshot clears history and rejects old-session recovery")
{
  MovementFixture fixture;
  fixture.Move(0, 0, 100);
  auto old = fixture.model.Snapshot();
  ClientStateDelta next{.generation = 2, .revision = 50};
  fixture.view->Apply(StateUpdateBatch{{next}}, At(200));
  fixture.view->Apply(StateUpdateBatch{{old}}, At(210));
  CHECK_FALSE(fixture.view->Sample(7, At(300)));

  ClientSnapshot recovered{.generation = 2, .revision = 50};
  recovered.players.push_back(Domain::Player{.data = {7, "player", "Player"}, .location = MovementLocation(50)});
  fixture.view->Apply(StateUpdateBatch{{recovered}}, At(220));
  REQUIRE(fixture.view->Sample(7, At(300)));
  CHECK(fixture.view->Sample(7, At(300))->position.X == 50);
}

TEST_CASE("An old visibility seed cannot stretch the next sample into the future")
{
  MovementFixture fixture;
  fixture.Move(0, 0, 1000);
  fixture.Move(10, 800, 1100);
  CHECK(fixture.view->HistorySize(7) == 1);
  CHECK(fixture.view->Sample(7, At(1100))->position.X == 10);
}

TEST_SUITE_END();
