#include <doctest/doctest.h>
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
