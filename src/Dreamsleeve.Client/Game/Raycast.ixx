module;

#include "Prelude.hpp"

export module Dreamsleeve.Game.Raycast;

import std;

// One havok pick against the game collision with the player's collision
// group on the line-of-sight layer. Main thread only. This is the collision
// world, not the depth buffer: transparent and modded meshes follow their own
// collision data.
namespace Raycast
{

  struct Hit
  {
    bool         valid{};  // The pick itself ran.
    bool         hit{};
    RE::NiPoint3 point;
  };

  Hit Pick(const RE::NiPoint3& from, const RE::NiPoint3& to)
  {
    auto* tes    = RE::TES::GetSingleton();
    auto* player = RE::PlayerCharacter::GetSingleton();
    if (!tes || !player || !player->GetParentCell()) return {};
    RE::bhkPickData pick{};
    const auto      scale = RE::bhkWorld::GetWorldScale();
    pick.rayInput.from    = from * scale;
    pick.rayInput.to      = to * scale;
    RE::CFilter playerFilter{};
    player->GetCollisionFilterInfo(playerFilter);
    pick.rayInput.filterInfo.filter = (playerFilter.filter & 0xFFFF0000u) | static_cast<std::uint32_t>(RE::COL_LAYER::kLineOfSight);
    tes->Pick(pick);
    if (pick.pickFailed) return {};
    Hit result{.valid = true, .hit = pick.rayOutput.HasHit()};
    if (result.hit)
    {
      // hitFraction is the fraction of the segment; the hit point is derived
      // in game units so callers never see havok scale.
      const float fraction = std::clamp(pick.rayOutput.hitFraction, 0.0f, 1.0f);
      result.point         = from + (to - from) * fraction;
    }
    return result;
  }

  // Nothing between the two points.
  export bool Clear(const RE::NiPoint3& from, const RE::NiPoint3& to)
  {
    const auto pick = Pick(from, to);
    return pick.valid && !pick.hit;
  }

  // The first collision straight below `at`, searched from `above` units over
  // it down to `below` units under it; absent when nothing is hit.
  export std::optional<float> GroundBelow(const RE::NiPoint3& at, float above, float below)
  {
    const RE::NiPoint3 from{at.x, at.y, at.z + above};
    const RE::NiPoint3 to{at.x, at.y, at.z - below};
    const auto         pick = Pick(from, to);
    if (!pick.valid || !pick.hit) return std::nullopt;
    return pick.point.z;
  }

}
