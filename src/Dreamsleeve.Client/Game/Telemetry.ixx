module;

#include "Prelude.hpp"

export module Dreamsleeve.Game.Telemetry;

import std;
import Dreamsleeve.Runtime;
import Dreamsleeve.Game.World;

// Reads the local player on the game thread and posts self-contained domain
// values to Core. Movement goes at the configured sample rate; reliable
// details/actor values go only when they changed. No game pointer leaves this module.
namespace Telemetry
{

  namespace Dream = Dreamsleeve::Client;
  using Clock     = std::chrono::steady_clock;

  constexpr auto  DetailsInterval     = std::chrono::milliseconds{250};
  constexpr auto  ActorValuesInterval = std::chrono::milliseconds{250};
  constexpr auto  MarkerInterval      = std::chrono::seconds{5};
  constexpr auto  ActivationMemory    = std::chrono::seconds{3};
  constexpr float ValueEpsilon        = 0.05f;

  struct Sent
  {
    bool                                     characterStarted{};
    std::string                              characterName;
    std::optional<Domain::PlayerDetails>     details;
    std::optional<Domain::ActorValueStorage> actorValues;
    std::optional<Domain::PlayerLocation>    location;
  };

  struct FurnitureLabel
  {
    RE::ObjectRefHandle        furniture;
    std::optional<std::string> name;
  };

  struct State
  {
    Sent                           sent;
    std::uint64_t                  sentGeneration{};
    Clock::time_point              nextMovement{};
    Clock::time_point              nextDetails{};
    Clock::time_point              nextActorValues{};
    Clock::time_point              nextMarkers{};
    std::int64_t                   gameStartedAtUnixMs{};
    std::optional<Domain::FormKey> lastSpace;
    Domain::PlaceDescription       place;
    RE::ObjectRefHandle            lastActivated;
    Clock::time_point              lastActivatedAt{};
    std::optional<FurnitureLabel>  furnitureLabel;
    bool                           deathHint{};
  };

  State& Get()
  {
    static State state;
    return state;
  }

  Domain::PlaceDescription DescribePlace(RE::PlayerCharacter* player, const World::Space& space, Clock::time_point now, bool spaceChanged)
  {
    auto& state          = Get();
    auto  place          = state.place;
    place.worldspaceName = World::WorldspaceName(player);
    place.locationName   = World::LocationName(player);
    place.isInterior     = space.interior;
    if (place.locationName.empty()) place.locationName = space.name;

    if (spaceChanged || now >= state.nextMarkers)
    {
      state.nextMarkers      = now + MarkerInterval;
      auto marker            = World::NearestMarker(player, place.locationName, space.interior);
      place.nearbyMarkerName = std::move(marker.name);
      place.markerKind       = place.nearbyMarkerName.empty() ? std::string{} : std::move(marker.kind);
    }
    state.place = place;
    return place;
  }

  // Prefer what the player actually activated just before use (the ore vein,
  // a shrine): a hidden helper only ever names itself by a placeholder.
  std::optional<std::string> FurnitureName(RE::TESObjectREFR* furniture, Clock::time_point now)
  {
    auto&      state  = Get();
    const auto handle = furniture->GetHandle();
    if (!state.furnitureLabel || state.furnitureLabel->furniture != handle)
    {
      std::optional<std::string> activated;
      if (now - state.lastActivatedAt <= ActivationMemory)
        if (auto ref = state.lastActivated.get(); ref && ref.get() != furniture) activated = World::RefName(ref.get());
      state.furnitureLabel = FurnitureLabel{handle, std::move(activated)};
    }

    if (!World::HiddenFurniture(furniture->GetBaseObject()))
      if (auto name = World::RefName(furniture)) return name;
    return state.furnitureLabel->name;
  }

  Domain::PlayerActivity ComputeActivity(RE::PlayerCharacter* player, RE::UI* ui, Clock::time_point now)
  {
    using World::Activity;
    if (auto menu = World::MenuActivity(player, ui)) return *menu;

    RE::NiPointer<RE::Actor> mount;
    if (player->IsOnMount() && player->GetMount(mount) && mount) return Activity(Domain::ActivityKind::Riding, World::RefName(mount.get()));
    if (player->IsDead()) return Activity(Domain::ActivityKind::Dead);
    if (player->IsInRagdollState()) return Activity(Domain::ActivityKind::Ragdoll);
    if (player->IsInCombat())
    {
      std::optional<std::string> target;
      if (auto enemy = player->GetActorRuntimeData().currentCombatTarget.get()) target = World::RefName(enemy.get());
      return Activity(Domain::ActivityKind::Combat, target);
    }
    if (player->IsSneaking()) return Activity(Domain::ActivityKind::Sneaking);
    if (auto* state = player->AsActorState())
    {
      if (state->IsFlying()) return Activity(Domain::ActivityKind::Flying);
      if (state->IsSwimming()) return Activity(Domain::ActivityKind::Swimming);
      // A stale furniture handle after leaving is not "using" without the sit state.
      if (state->GetSitSleepState() != RE::SIT_SLEEP_STATE::kNormal)
        if (auto furniture = player->GetOccupiedFurniture().get(); furniture && furniture->Is3DLoaded())
          return Activity(Domain::ActivityKind::UsingObject, FurnitureName(furniture.get(), now));
    }
    Get().furnitureLabel.reset();
    return Activity(Domain::ActivityKind::Exploring);
  }

  template <class Command>
  bool Post(Command command)
  {
    auto&      runtime = Runtime::Get();
    const auto posted  = runtime.app->Exchange().Post({runtime.session.Generation(), std::move(command)});
    return posted == Dream::CommandPostResult::Queued || posted == Dream::CommandPostResult::Replaced;
  }

  // A new character context: nothing sent yet, timers restart without a burst.
  export void BeginContext()
  {
    auto& state = Get();
    state       = {};
    state.gameStartedAtUnixMs =
      Domain::ToUnixMilliseconds(std::chrono::time_point_cast<std::chrono::milliseconds>(std::chrono::system_clock::now()));
  }

  // Leaving the character: tell the server once if it knew this character.
  export void EndContext()
  {
    auto& state   = Get();
    auto& runtime = Runtime::Get();
    if (state.sent.characterStarted && runtime.app && runtime.session.Ready()) Post(Dream::GameExited{});
    state = {};
  }

  export void NoteActivated(RE::FormID formId)
  {
    auto& state = Get();
    if (auto* ref = RE::TESForm::LookupByID<RE::TESObjectREFR>(formId))
    {
      state.lastActivated   = ref->GetHandle();
      state.lastActivatedAt = Clock::now();
    }
  }

  export void NoteDeath()
  {
    Get().deathHint = true;
  }

  export void Tick(Clock::time_point now)
  {
    auto& runtime = Runtime::Get();
    auto& state   = Get();
    if (!runtime.app || runtime.context != Runtime::GameContext::Playing) return;
    auto& session = runtime.session;
    if (!session.Ready()) return;
    if (session.Generation() != state.sentGeneration)
    {
      state.sent           = {};
      state.sentGeneration = session.Generation();
    }

    if (!World::PlayerReady())
    {
      // Door/loading transitions must withdraw the old pose, rather than leave
      // a remote firefly at the last position until the new world is ready.
      if (state.sent.location && Post(Dream::LocalLocation{std::nullopt})) state.sent.location.reset();
      return;
    }

    auto*      player = RE::PlayerCharacter::GetSingleton();
    auto*      ui     = RE::UI::GetSingleton();
    const auto space  = World::CurrentSpace(player);
    if (!space) return;
    const auto spaceKey     = World::KeyOf(space->form);
    const bool spaceChanged = Domain::Spatial::SpaceChanged(state.lastSpace, spaceKey);
    state.lastSpace         = spaceKey;

    const auto name = World::Text(player->GetName());
    if (!state.sent.characterStarted)
    {
      if (!Post(Dream::CharacterStarted{name})) return;
      state.sent.characterStarted = true;
      state.sent.characterName    = name;
      state.nextDetails = state.nextActorValues = state.nextMovement = now;
    }
    else if (name != state.sent.characterName && Post(Dream::CharacterRenamed{name}))
      state.sent.characterName = name;

    const auto& settings = runtime.app->Settings().client;
    if (now >= state.nextMovement)
    {
      state.nextMovement = now + std::chrono::milliseconds{settings.playerSampleIntervalMs};
      auto location      = World::ReadLocation(player, *space);
      // A new space or a teleport starts a new motion context on the server.
      if (!state.sent.location || Domain::Spatial::Jumped(*state.sent.location, location, settings.movement.teleportDistance))
        Post(Dream::LocalLocation{location});
      if (Post(Dream::LocalMovement{location})) state.sent.location = location;
    }

    if (now >= state.nextDetails || state.deathHint)
    {
      state.nextDetails = now + DetailsInterval;
      Domain::PlayerDetails details;
      if (auto* race = player->GetRace()) details.race = Domain::NamedForm{World::KeyOf(race), World::Text(race->GetFullName())};
      details.level               = player->GetLevel();
      details.activity            = ComputeActivity(player, ui, now);
      details.place               = DescribePlace(player, *space, now, spaceChanged);
      details.gameStartedAtUnixMs = state.gameStartedAtUnixMs;
      if (!state.sent.details || *state.sent.details != details)
        if (Post(Dream::PlayerDetailsChanged{details})) state.sent.details = std::move(details);
    }

    if (now >= state.nextActorValues || state.deathHint)
    {
      state.nextActorValues = now + ActorValuesInterval;
      state.deathHint       = false;
      auto values           = World::ActorValues(player);
      if (!state.sent.actorValues || !Domain::Players::SameActorValues(*state.sent.actorValues, values, ValueEpsilon))
        if (Post(Dream::LocalActorValues{values})) state.sent.actorValues = std::move(values);
    }
  }

}
