module;

#include "Prelude.hpp"

export module Dreamsleeve.Game.Fireflies;

import std;
import Dreamsleeve.Runtime;
import Dreamsleeve.Game.Telemetry;
import Dreamsleeve.Game.PlacedReferences;
import Dreamsleeve.UI.Nameplates;

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
    Runtime::Get().bubbles.Clear();
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

  // The bubble look of players, from the live UI settings.
  export Nameplates::BubbleStyle BubbleStyle(const Dreamsleeve::Host::UiSettings& ui)
  {
    return {
        static_cast<float>(ui.bubbleFontSize),
        static_cast<float>(ui.bubbleMaxWidth),
        static_cast<float>(ui.bubbleBackground),
        ui.bubbleBorder,
        Dreamsleeve::Host::ParseColor(ui.bubbleTextColor).value_or(Nameplates::DefaultTextColor)
    };
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
      !Telemetry::PlayerReady() || !player)
    {
      ClearAll();
      return;
    }

    const auto space = Telemetry::SpaceKey(player);
    if (!space || (state.space && *state.space != *space)) ClearAll();
    if (!space) return;
    state.space = space;

    const auto                           self = player->GetPosition();
    const Domain::Position               origin{self.x, self.y, self.z};
    const float                          height    = static_cast<float>(ui.fireflyHeightOffset);
    const auto                           style     = BubbleStyle(ui);
    const auto                           nameColor = Dreamsleeve::Host::ParseColor(ui.fireflyNameColor).value_or(Nameplates::DefaultTextColor);
    std::unordered_set<Domain::PlayerId> visible;
    // Expired texts and those of players who left are dropped here, once per
    // frame; a message is never kept waiting for its author to appear.
    runtime.bubbles.Prune(now, ui, [&](Domain::PlayerId id) { return runtime.session.OnlinePlayers().contains(id); });
    for (const auto& [id, remote] : runtime.session.OnlinePlayers())
    {
      if (runtime.session.SelfId() == id) continue;
      const auto pose = runtime.movement->Sample(id, now);
      if (!pose || pose->location.locationId != *space) continue;
      if (Domain::Spatial::Distance(origin, pose->position) > settings.visibilityDistance) continue;

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
      // Name and bubble share one anchor, projection and occlusion pick. The
      // name size is passed even when names are hidden: it fixes the baseline
      // above which the bubble sits.
      Nameplates::Label label{
          .key = {Nameplates::LabelKind::Player, id},
          .nameSize = static_cast<float>(ui.fireflyNameFontSize),
          .nameColor = nameColor,
          .style = style
      };
      // Same resolver as the web UI, so a pseudonym matches on both surfaces.
      if (ui.showFireflyNames && !(combat && ui.combatHideNames))
        label.name = runtime.session.PlayerNames().NameFor(id, remote.data, remote.characterName, ui);
      if (ui.showBubbles && !(combat && ui.combatHideBubbles))
        if (const auto active = runtime.bubbles.Find(id, now, ui))
        {
          label.bubble      = std::string{active->text};
          label.bubbleAlpha = active->alpha;
        }
      auto anchor  = position;
      anchor.z    += static_cast<float>(ui.fireflyNameOffset);
      Nameplates::Add(names, std::move(label), anchor, ui.fireflyNameOcclusion);
    }

    state.refs.Retain(visible);
  }

}
