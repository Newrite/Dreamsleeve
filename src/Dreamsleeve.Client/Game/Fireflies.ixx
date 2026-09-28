module;

#include "Prelude.hpp"

export module Dreamsleeve.Game.Fireflies;

import std;
import Dreamsleeve.Runtime;
import Dreamsleeve.Game.Telemetry;

// Presence of other players as a glowing placed reference per visible player.
// One reference per player, moved every frame from MovementView; nothing else
// of the remote actor is reproduced. References are deleted before saves and
// on every context change so they never persist.
namespace Fireflies
{

  namespace Dream = Dreamsleeve::Client;
  using Clock     = std::chrono::steady_clock;

  // Skyrim.esm STAT FXGlowFillRoundXBrt: a soft round glow with no collision.
  constexpr RE::FormID       BaseFormId   = 0x02EB0F;
  constexpr std::string_view BasePlugin   = "Skyrim.esm"sv;
  constexpr float            HeightOffset = 110.0f;  // Roughly head height above the pose origin.
  constexpr float            Scale        = 0.25f;

  struct State
  {
    RE::TESBoundObject*                                       base{};
    std::unordered_map<Domain::PlayerId, RE::ObjectRefHandle> refs;
    std::optional<Domain::FormKey>                            space;
  };

  State& Get()
  {
    static State state;
    return state;
  }

  void Remove(RE::ObjectRefHandle handle)
  {
    if (auto ref = handle.get())
    {
      ref->Disable();
      ref->SetDelete(true);
    }
  }

  export void ClearAll()
  {
    auto& state = Get();
    for (auto& [id, handle] : state.refs)
      Remove(handle);
    state.refs.clear();
    state.space.reset();
  }

  // kDataLoaded: the base form is resolved once; a missing form disables fireflies.
  export void ResolveForms()
  {
    auto& state = Get();
    auto* data  = RE::TESDataHandler::GetSingleton();
    state.base  = data ? data->LookupForm<RE::TESObjectSTAT>(BaseFormId, BasePlugin) : nullptr;
    if (!state.base) logger::error("Firefly base form {:X} in {} not found; fireflies disabled", BaseFormId, BasePlugin);
  }

  std::optional<RE::ObjectRefHandle> Spawn(RE::PlayerCharacter* player, const RE::NiPoint3& position)
  {
    auto* data = RE::TESDataHandler::GetSingleton();
    auto* cell = player->GetParentCell();
    if (!data || !cell) return std::nullopt;
    auto* world  = cell->IsExteriorCell() ? player->GetWorldspace() : nullptr;
    auto  handle = data->CreateReferenceAtLocation(
      Get().base,
      position,
      RE::NiPoint3{},
      cell,
      world,
      nullptr,
      nullptr,
      RE::ObjectRefHandle{},
      false,
      true);
    auto ref = handle.get();
    if (!ref) return std::nullopt;
    ref->SetScale(Scale);
    return handle;
  }

  export std::size_t Count()
  {
    return Get().refs.size();
  }

  export void Tick(Clock::time_point now)
  {
    auto& runtime = Runtime::Get();
    auto& state   = Get();
    if (!runtime.app || !state.base) return;

    const auto& settings = runtime.app->Settings().client;
    auto*       player   = RE::PlayerCharacter::GetSingleton();
    if (!settings.showFireflies || runtime.context != Runtime::GameContext::Playing || !Telemetry::PlayerReady() || !player)
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
    std::unordered_set<Domain::PlayerId> visible;
    for (const auto& [id, remote] : runtime.session.OnlinePlayers())
    {
      if (runtime.session.SelfId() == id) continue;
      const auto pose = runtime.movement->Sample(id, now);
      if (!pose || pose->location.locationId != *space) continue;
      if (Domain::Spatial::Distance(origin, pose->position) > settings.visibilityDistance) continue;

      const RE::NiPoint3 position{pose->position.X, pose->position.Y, pose->position.Z + HeightOffset};
      auto               found = state.refs.find(id);
      auto               ref   = found == state.refs.end() ? RE::NiPointer<RE::TESObjectREFR>{} : found->second.get();
      if (!ref)
      {
        auto spawned = Spawn(player, position);
        if (!spawned) continue;
        state.refs[id] = *spawned;
      }
      else
      {
        ref->SetPosition(position);
        // SetPosition changes REFR coordinates, not the loaded scene node.
        // The pose is already interpolated by MovementView.
        ref->Update3DPosition(true);
      }
      visible.insert(id);
    }

    std::erase_if(state.refs, [&](auto& entry) {
      if (visible.contains(entry.first)) return false;
      Remove(entry.second);
      return true;
    });
  }

}
