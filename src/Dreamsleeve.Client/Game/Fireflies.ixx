module;

#include "Prelude.hpp"

export module Dreamsleeve.Game.Fireflies;

import std;
import Dreamsleeve.Runtime;
import Dreamsleeve.Game.World;
import Dreamsleeve.Game.PlacedReferences;
import Dreamsleeve.Host.Hud;
import Dreamsleeve.UI.Nameplates;
import Dreamsleeve.Game.Phantoms;
import Dreamsleeve.Game.PlayerLabels;

// Presence of other players as a glowing placed reference per visible player.
// One reference per player, moved every frame from MovementView; nothing else
// of the remote actor is reproduced. References are temporary from creation
// and deleted on context changes; save notifications must not recreate them.
namespace Fireflies
{

  namespace Dream = Dreamsleeve::Client;
  using Clock     = std::chrono::steady_clock;

  struct State
  {
    RE::TESBoundObject*                     base{};
    PlacedReferences::Set<Domain::PlayerId> refs{"firefly"};
    std::optional<Domain::FormKey>          space;
  };

  State& Get()
  {
    static State state;
    return state;
  }

  export void ClearAll()
  {
    auto& state = Get();
    state.refs.Clear();
    state.space.reset();
  }

  // kDataLoaded: the base form is resolved once; a missing form disables fireflies.
  export void ResolveForms()
  {
    auto&       state   = Get();
    const auto& runtime = Runtime::Get();
    if (!runtime.app) return;
    const auto& config = runtime.app->Settings().client;
    state.base         = PlacedReferences::ResolveStatic("firefly", config.fireflyPlugin, config.fireflyFormId);
  }

  export std::size_t Count()
  {
    return Get().refs.Count();
  }

  // Adds the labels of visible players to the frame the caller publishes.
  export void Tick(Clock::time_point now, Nameplates::Frame& names)
  {
    auto& runtime = Runtime::Get();
    auto& state   = Get();
    if (!runtime.app || !state.base) return;

    const auto& settings = runtime.app->Settings().client;
    const auto& ui       = runtime.ui.ui.chat;
    auto*       player   = RE::PlayerCharacter::GetSingleton();
    // Combat hiding per surface. Hidden fireflies take names and bubbles with
    // them, there is no other anchor; bubble timers keep running meanwhile.
    const bool combat = player && player->IsInCombat();
    if (
      !settings.showFireflies || (combat && ui.combatHideFireflies) || runtime.context != Runtime::GameContext::Playing ||
      !World::PlayerReady() || !player)
    {
      ClearAll();
      return;
    }

    const auto observer = World::Observe(player);
    if (!observer || (state.space && *state.space != observer->space)) ClearAll();
    if (!observer) return;
    state.space        = observer->space;
    const auto& space  = observer->space;
    const auto& origin = observer->position;

    const float height = static_cast<float>(ui.fireflyHeightOffset);

    std::unordered_set<Domain::PlayerId> visible;
    for (const auto& [id, remote] : runtime.session.OnlinePlayers())
    {
      if (runtime.session.HidesPlayerRepresentation(id, ui.fireflyGuildmatesOnly) || !Phantoms::UseFirefly(id)) continue;
      const auto pose = runtime.movement->Sample(id, now);
      if (!pose || !Domain::Spatial::Reach(space, origin, pose->location.locationId, pose->position, settings.visibilityDistance)) continue;

      const RE::NiPoint3 position{pose->position.X, pose->position.Y, pose->position.Z + height};
      auto               ref = state.refs.Resolve(id);
      if (!ref)
      {
        auto spawned = PlacedReferences::Spawn("firefly", state.base, position, RE::NiPoint3{}, settings.fireflyScale);
        if (!spawned) continue;
        state.refs.Keep(id, *spawned);
      }
      else
      {
        ref->SetPosition(position);
        // SetPosition changes REFR coordinates, not the loaded scene node.
        // The pose is already interpolated by MovementView.
        ref->Update3DPosition(true);
      }
      visible.insert(id);
      PlayerLabels::Add(names, id, position, remote, now);
    }

    state.refs.Retain(visible);
  }

}
