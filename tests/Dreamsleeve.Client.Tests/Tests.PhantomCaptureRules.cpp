#include <doctest/doctest.h>
import std;
import Dreamsleeve.Game.PhantomCaptureRules;

namespace C = Dreamsleeve::Game::PhantomCapture;

TEST_CASE("Neutral capture excludes weapon blood and unskinned decals before inspecting vertices")
{
  // EdgeBlood12 in the game log: a lighting decal, not skinned. Invalid
  // inactive data from this surface must never enter vertex decoding.
  CHECK(C::Surface{false, true, false, false}.Auxiliary());
  CHECK(C::Surface{false, false, true, true}.Auxiliary());
  CHECK(C::Surface{true, false, false, false}.Auxiliary());
}

TEST_CASE("Neutral capture retains the body and clothing with projected or decal lighting")
{
  CHECK_FALSE(C::Surface{false, false, true, false}.Auxiliary());
  CHECK_FALSE(C::Surface{false, true, true, false}.Auxiliary());
  CHECK_FALSE(C::Surface{false, false, false, false}.Auxiliary());
  // Unsupported skinned effects must fail explicitly, not silently lose hair.
  CHECK_FALSE(C::Surface{true, true, true, false}.Auxiliary());
}

TEST_CASE("Initial capture excludes hidden geometry but ignores first-person camera suppression")
{
  CHECK_FALSE(C::Visibility::Capture(true, false, false));
  CHECK(C::Visibility::Capture(false, false, false));
  CHECK(C::Visibility::Capture(true, false, true));
  CHECK_FALSE(C::Visibility::Capture(false, true, false));
  CHECK_FALSE(C::Visibility::Capture(true, true, true));
}

TEST_CASE("Cached visibility survives first person and resumes authored visibility in third person")
{
  C::Visibility visible;
  CHECK_FALSE(visible.Sample(true, false, false));
  CHECK_FALSE(visible.Sample(true, true, true));
  CHECK(visible.Sample(false, false, true));
  CHECK_FALSE(visible.Sample(true, true, true));
  CHECK(visible.Sample(true, true, false));
  CHECK(visible.Sample(true, false, true));
  CHECK_FALSE(visible.Sample(true, false, false));
}

TEST_CASE("Neutral ghost omits RaceMenu overlays and non-rendering helper geometry")
{
  CHECK(C::Surface{.skinned = true, .name = "Body [Ovl1]"}.Auxiliary());
  CHECK(C::Surface{.skinned = true, .name = "Feet [SOvl0]"}.Auxiliary());
  CHECK(C::Surface{.hasShader = false}.Auxiliary());
  CHECK(C::Surface{.skinned = true, .materialAlpha = 0}.Auxiliary());
  CHECK(C::Surface{.skinned = true, .shaderAlpha = 0}.Auxiliary());
}

TEST_CASE("Auxiliary selection preserves visible skin, hair, weapon and invalid alpha diagnostics")
{
  for (const auto name : {"Body", "Hands", "Feet", "00UBE_FemaleHead", "Warhammer_Mesh", "Hair"})
    CHECK_FALSE(C::Surface{.decalMaterial = true, .skinned = true, .name = name}.Auxiliary());
  CHECK_FALSE(C::Surface{.skinned = true, .materialAlpha = 0.01f}.Auxiliary());
  CHECK_FALSE(C::Surface{.skinned = true, .materialAlpha = -1.f}.Auxiliary());
}

TEST_CASE("Phantom recording waits through missing 3D without losing its coordinate space")
{
  C::Context context;
  using O = C::Context::Observation;
  CHECK(context.Observe({}) == O::Waiting);
  CHECK(context.Observe(C::Context::Space{60, false}) == O::Ready);
  CHECK(context.Observe({}) == O::Waiting);
  CHECK(context.Observe({}) == O::Waiting);
  CHECK(context.Observe(C::Context::Space{60, false}) == O::Ready);
  // Exterior CELL ids never form the key: walking in the same WRLD continues.
  CHECK(context.Observe(C::Context::Space{60, false}) == O::Ready);
  CHECK(context.Observe(C::Context::Space{90, true}) == O::Changed);
  CHECK(context.Observe({}) == O::Waiting);
  CHECK(context.Observe(C::Context::Space{91, true}) == O::Changed);
  CHECK(context.Observe(C::Context::Space{91, false}) == O::Changed);

  context.Reset();
  CHECK(context.Observe(C::Context::Space{60, false}) == O::Ready);
}

TEST_CASE("Phantom cadence maintains 20 Hz across frame quantization instead of drifting to 14 Hz")
{
  using namespace std::chrono;
  C::Cadence cadence;
  const auto start    = C::Cadence::Clock::time_point{seconds(100)};
  unsigned   captured = 0;
  // At 41.7 FPS two frames take 48 ms, three take 72 ms. Scheduling from
  // now + 50 ms would capture only 209 frames here, instead of 301.
  for (auto elapsed = 0ms; elapsed <= 15s; elapsed += 24ms)
    if (cadence.Due(start + elapsed, 20)) ++captured;
  CHECK(captured == 301);
}

TEST_CASE("Phantom cadence skips missed slots without replaying a frame and supports backoff and rate changes")
{
  using namespace std::chrono;
  C::Cadence cadence;
  const auto start = C::Cadence::Clock::time_point{seconds(100)};
  CHECK(cadence.Due(start, 20));
  CHECK(cadence.Due(start + 825ms, 20));
  CHECK_FALSE(cadence.Due(start + 825ms, 20));
  CHECK_FALSE(cadence.Due(start + 849ms, 20));
  CHECK(cadence.Due(start + 850ms, 20));

  cadence.Defer(start + 850ms);
  CHECK_FALSE(cadence.Due(start + 1849ms, 20));
  CHECK(cadence.Due(start + 1850ms, 20));
  CHECK(cadence.Due(start + 1900ms, 40));
  CHECK_FALSE(cadence.Due(start + 1924ms, 40));
  CHECK(cadence.Due(start + 1925ms, 40));

  cadence.Reset();
  CHECK(cadence.Due(start + 1926ms, 40));
}

TEST_CASE("Phantom cadence captures only once per game frame when FPS is below target")
{
  using namespace std::chrono;
  C::Cadence cadence;
  const auto start = C::Cadence::Clock::time_point{seconds(100)};
  for (auto elapsed = 0ms; elapsed <= 1s; elapsed += 100ms)
  {
    CHECK(cadence.Due(start + elapsed, 20));
    CHECK_FALSE(cadence.Due(start + elapsed, 20));
  }
}

TEST_CASE("Face animation crossing rounded coordinate bins does not republish the character")
{
  C::AppearanceRevision revision({
      17,
      {0.03124f, 0, 0}
  });
  for (std::uint64_t time = 0; time < 15000000; time += 250000)
    CHECK(
      revision.Observe(
        {
            17,
            {time % 500000 ? 0.03126f : 0.11f, 0, 0}
    },
        time) == C::AppearanceChange::None);
}

TEST_CASE("Appearance audit compares accumulated deformation with the accepted asset")
{
  C::AppearanceRevision revision({
      17,
      {0, 0, 0}
  });
  CHECK(
    revision.Observe(
      {
          17,
          {0.1f, 0, 0}
  },
      250000) == C::AppearanceChange::None);
  CHECK(
    revision.Observe(
      {
          17,
          {0.2f, 0, 0}
  },
      500000) == C::AppearanceChange::None);
  CHECK(
    revision.Observe(
      {
          17,
          {0.3f, 0, 0}
  },
      750000) == C::AppearanceChange::None);
  CHECK(
    revision.Observe(
      {
          17,
          {0.31f, 0, 0}
  },
      1500000) == C::AppearanceChange::None);
  CHECK(
    revision.Observe(
      {
          17,
          {0.31f, 0, 0}
  },
      1750000) == C::AppearanceChange::Deformation);
}

TEST_CASE("Equipment composition settles independently of harmless dynamic drift")
{
  C::AppearanceRevision revision({
      17,
      {0, 0, 0}
  });
  CHECK(
    revision.Observe(
      {
          18,
          {0.03f, 0, 0}
  },
      1000000) == C::AppearanceChange::None);
  CHECK(
    revision.Observe(
      {
          18,
          {0.04f, 0, 0}
  },
      1250000) == C::AppearanceChange::Structure);
  CHECK(
    revision.Observe(
      {
          17,
          {0, 0, 0}
  },
      1500000) == C::AppearanceChange::None);
  CHECK(
    revision.Observe(
      {
          19,
          {0, 0, 0}
  },
      2000000) == C::AppearanceChange::None);
  CHECK(
    revision.Observe(
      {
          20,
          {0, 0, 0}
  },
      2250000) == C::AppearanceChange::None);
  CHECK(
    revision.Observe(
      {
          20,
          {0, 0, 0}
  },
      2500000) == C::AppearanceChange::Structure);
}
