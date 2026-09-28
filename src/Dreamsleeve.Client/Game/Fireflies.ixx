module;

#include "Prelude.hpp"

export module Dreamsleeve.Game.Fireflies;

import std;
import Dreamsleeve.Runtime;
import Dreamsleeve.Game.Telemetry;
import Dreamsleeve.UI.Nameplates;

// Presence of other players as a glowing placed reference per visible player.
// One reference per player, moved every frame from MovementView; nothing else
// of the remote actor is reproduced. References are temporary from creation
// and deleted on context changes; save notifications must not recreate them.
namespace Fireflies
{

  namespace Dream = Dreamsleeve::Client;
  using Clock     = std::chrono::steady_clock;

  constexpr float HeightOffset = 110.0f;  // Roughly head height above the pose origin.

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
      logger::info("Removing firefly reference {:08X}", ref->GetFormID());
      // Hide immediately even if engine detachment is deferred.
      if (auto* node = ref->Get3D()) node->SetAppCulled(true);
      ref->Disable();
      ref->SetDelete(true);
    }
  }

  // SetPosition never re-parents a reference: it stays in the cell it was
  // created in. Once that cell detaches its 3D unloads while the handle stays
  // valid, so the glow has to be recreated in the player's current cell.
  bool CellAttached(RE::TESObjectREFR& ref)
  {
    auto* cell = ref.GetParentCell();
    return cell && cell->IsAttached();
  }

  export void ClearAll()
  {
    auto& state = Get();
    for (auto& [id, handle] : state.refs)
      Remove(handle);
    state.refs.clear();
    state.space.reset();
    Nameplates::Publish({});
  }

  // kDataLoaded: the base form is resolved once; a missing form disables fireflies.
  export void ResolveForms()
  {
    auto&       state   = Get();
    auto*       data    = RE::TESDataHandler::GetSingleton();
    const auto& runtime = Runtime::Get();
    if (!runtime.app) return;
    const auto& config = runtime.app->Settings().client;
    state.base         = data ? data->LookupForm<RE::TESObjectSTAT>(config.fireflyFormId, config.fireflyPlugin) : nullptr;
    if (!state.base) logger::error("Firefly STAT {:X} in {} not found; fireflies disabled", config.fireflyFormId, config.fireflyPlugin);
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
    // Dynamic FormID alone does not exclude a reference from saved changes.
    ref->SetTemporary();
    ref->SetScale(Runtime::Get().app->Settings().client.fireflyScale);
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
    Nameplates::Frame                    names;
    for (const auto& [id, remote] : runtime.session.OnlinePlayers())
    {
      if (runtime.session.SelfId() == id) continue;
      const auto pose = runtime.movement->Sample(id, now);
      if (!pose || pose->location.locationId != *space) continue;
      if (Domain::Spatial::Distance(origin, pose->position) > settings.visibilityDistance) continue;

      const RE::NiPoint3 position{pose->position.X, pose->position.Y, pose->position.Z + HeightOffset};
      auto               found = state.refs.find(id);
      auto               ref   = found == state.refs.end() ? RE::NiPointer<RE::TESObjectREFR>{} : found->second.get();
      if (ref && !CellAttached(*ref))
      {
        Remove(found->second);
        ref.reset();
      }
      if (!ref)
      {
        auto spawned = Spawn(player, position);
        if (!spawned) continue;
        state.refs[id] = *spawned;
        if (auto created = spawned->get()) logger::info("Spawned firefly player {} reference {:08X}", id, created->GetFormID());
      }
      else
      {
        ref->SetPosition(position);
        // SetPosition changes REFR coordinates, not the loaded scene node.
        // The pose is already interpolated by MovementView.
        ref->Update3DPosition(true);
      }
      visible.insert(id);
      const auto& nameSettings = runtime.ui.ui.chat;
      if (nameSettings.showFireflyNames)
      {
        auto anchor  = position;
        anchor.z    += static_cast<float>(nameSettings.fireflyNameOffset);
        Nameplates::Add(
          names,
          id,
          remote.data.displayName,
          anchor,
          static_cast<float>(nameSettings.fireflyNameFontSize),
          nameSettings.fireflyNameOcclusion);
      }
    }

    auto* ui = RE::UI::GetSingleton();
    if (!ui || ui->GameIsPaused() || !ui->menuSystemVisible) names.clear();
    Nameplates::Publish(std::move(names));

    std::erase_if(state.refs, [&](auto& entry) {
      if (visible.contains(entry.first)) return false;
      Remove(entry.second);
      return true;
    });
  }

}
