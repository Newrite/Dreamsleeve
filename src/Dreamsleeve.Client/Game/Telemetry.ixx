module;

#include "Prelude.hpp"
#include <magic_enum/magic_enum.hpp>

export module Dreamsleeve.Game.Telemetry;

import std;
import Dreamsleeve.Runtime;

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
  constexpr float MarkerRadius        = 16384.0f;
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

  std::string Utf8(const char* text)
  {
    return text ? std::string{text} : std::string{};
  }

  // Origin plugin plus local id; dynamic forms cannot be shared across clients.
  Domain::FormKey KeyOf(const RE::TESForm* form)
  {
    if (!form) return {};
    const auto* file = form->GetFile(0);
    if (!file || form->IsDynamicForm()) return {"runtime", form->GetFormID()};
    return {Domain::Normalize::PluginName(file->GetFilename()), form->GetLocalFormID()};
  }

  struct Space
  {
    const RE::TESForm* form{};
    std::string        name;
    bool               interior{};
  };

  std::optional<Space> CurrentSpace(RE::PlayerCharacter* player)
  {
    auto* cell = player->GetParentCell();
    if (!cell) return std::nullopt;
    if (cell->IsInteriorCell()) return Space{cell, Utf8(cell->GetFullName()), true};
    auto* world = player->GetWorldspace();
    if (!world) return std::nullopt;
    return Space{world, Utf8(world->GetFullName()), false};
  }

  std::string LocationName(RE::PlayerCharacter* player)
  {
    for (auto* location = player->GetCurrentLocation(); location; location = location->parentLoc)
    {
      auto name = Utf8(location->GetFullName());
      if (!name.empty()) return name;
    }
    return {};
  }

  RE::TESWorldSpace* OuterWorld(RE::PlayerCharacter* player)
  {
    auto* world = player->GetWorldspace();
    if (!world) world = player->GetPlayerRuntimeData().cachedWorldSpace;
    return world;
  }

  std::string WorldspaceName(RE::PlayerCharacter* player)
  {
    for (auto* world = OuterWorld(player); world; world = world->parentWorld)
    {
      auto name = Utf8(world->GetFullName());
      if (!name.empty()) return name;
    }
    return {};
  }

  // Normalized marker keys; the UI maps them to labels.
  std::string MarkerKind(RE::MARKER_TYPE type)
  {
    std::string name{magic_enum::enum_name(type)};
    if (name.size() > 1 && name.front() == 'k') name.erase(0, 1);
    std::ranges::transform(name, name.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    if (name.empty()) return "unknown";
    if (name.ends_with("castle") || name == "dlc02castlekarstaag") return "castle";
    if (name.ends_with("capitol")) return "capital";
    if (name == "dlc02miraaktemple") return "templeofmiraak";
    if (name == "dlc02ravenrock") return "redoransettlement";
    if (name == "dlc02standingstone") return "allmakerstone";
    if (name == "dlc02telvannitower") return "telvannisettlement";
    if (name == "dwemerruin") return "dwemer";
    if (name.starts_with("dlc02to")) return "travel";
    return name;
  }

  struct Marker
  {
    std::string name;
    std::string kind;
    float       distance{std::numeric_limits<float>::max()};
  };

  // Nearest visible map marker of the outer world chain; a marker named like
  // the current location wins. Throttled by the caller: it walks persistent refs.
  Marker NearestMarker(RE::PlayerCharacter* player, const std::string& locationName, bool interior)
  {
    Marker     best;
    const auto position = interior ? player->GetPlayerRuntimeData().exteriorPosition : player->GetPosition();
    for (auto* world = OuterWorld(player); world; world = world->parentWorld)
    {
      if (!world->persistentCell) continue;
      world->persistentCell->ForEachReferenceInRange(position, MarkerRadius, [&](RE::TESObjectREFR* ref) {
        if (!ref) return RE::BSContainer::ForEachResult::kContinue;
        const auto* marker = ref->extraList.GetByType<RE::ExtraMapMarker>();
        if (!marker || !marker->mapData) return RE::BSContainer::ForEachResult::kContinue;
        const auto& data = *marker->mapData;
        auto        name = Utf8(data.locationName.GetFullName());
        if (name.empty()) return RE::BSContainer::ForEachResult::kContinue;

        const bool  sameLocation = !locationName.empty() && name == locationName;
        const float distance     = sameLocation ? 0.0f : ref->GetPosition().GetDistance(position);
        if (!sameLocation && !data.flags.all(RE::MapMarkerData::Flag::kVisible)) return RE::BSContainer::ForEachResult::kContinue;
        if (distance < best.distance)
        {
          best.distance = distance;
          best.name     = std::move(name);
          best.kind     = MarkerKind(data.type.get());
        }
        return sameLocation ? RE::BSContainer::ForEachResult::kStop : RE::BSContainer::ForEachResult::kContinue;
      });
    }
    return best;
  }

  Domain::PlaceDescription DescribePlace(RE::PlayerCharacter* player, const Space& space, Clock::time_point now, bool spaceChanged)
  {
    auto& state          = Get();
    auto  place          = state.place;
    place.worldspaceName = WorldspaceName(player);
    place.locationName   = LocationName(player);
    place.isInterior     = space.interior;
    if (place.locationName.empty()) place.locationName = space.name;

    if (spaceChanged || now >= state.nextMarkers)
    {
      state.nextMarkers      = now + MarkerInterval;
      auto marker            = NearestMarker(player, place.locationName, space.interior);
      place.nearbyMarkerName = std::move(marker.name);
      place.markerKind       = place.nearbyMarkerName.empty() ? std::string{} : std::move(marker.kind);
    }
    state.place = place;
    return place;
  }

  // Owning pointer for the duration of the call; handles are the only thing kept between frames.
  RE::NiPointer<RE::TESObjectREFR> Speaker()
  {
    auto* topics = RE::MenuTopicManager::GetSingleton();
    if (!topics) return {};
    if (auto speaker = topics->speaker.get()) return speaker;
    return topics->lastSpeaker.get();
  }

  std::optional<std::string> RefName(RE::TESObjectREFR* ref)
  {
    if (!ref) return std::nullopt;
    auto name = Utf8(ref->GetName());
    if (name.empty()) return std::nullopt;
    return name;
  }

  // Hidden furniture helpers (mining, mod shrines) carry marker flags or an
  // empty name. Prefer what the player actually activated just before use.
  std::optional<std::string> FurnitureName(RE::TESObjectREFR* furniture, Clock::time_point now)
  {
    auto&      state  = Get();
    const auto handle = furniture->GetHandle();
    if (!state.furnitureLabel || state.furnitureLabel->furniture != handle)
    {
      std::optional<std::string> activated;
      if (now - state.lastActivatedAt <= ActivationMemory)
        if (auto ref = state.lastActivated.get(); ref && ref.get() != furniture) activated = RefName(ref.get());
      state.furnitureLabel = FurnitureLabel{handle, std::move(activated)};
    }

    const auto* base   = furniture->GetBaseObject();
    const bool  marker = base && (base->GetFormFlags() & RE::TESFurniture::RecordFlags::kIsMarker) != 0;
    if (!marker)
      if (auto name = RefName(furniture)) return name;
    return state.furnitureLabel->name;
  }

  Domain::LockDifficulty LockLevel(RE::LOCK_LEVEL level)
  {
    using RE::LOCK_LEVEL;
    switch (level)
    {
      case LOCK_LEVEL::kUnlocked:
        return Domain::LockDifficulty::Unlocked;
      case LOCK_LEVEL::kVeryEasy:
        return Domain::LockDifficulty::VeryEasy;
      case LOCK_LEVEL::kEasy:
        return Domain::LockDifficulty::Easy;
      case LOCK_LEVEL::kAverage:
        return Domain::LockDifficulty::Average;
      case LOCK_LEVEL::kHard:
        return Domain::LockDifficulty::Hard;
      case LOCK_LEVEL::kVeryHard:
        return Domain::LockDifficulty::VeryHard;
      case LOCK_LEVEL::kRequiresKey:
        return Domain::LockDifficulty::RequiresKey;
    }
    return Domain::LockDifficulty::Unknown;
  }

  Domain::PlayerActivity Activity(Domain::ActivityKind kind, std::optional<std::string> target = std::nullopt)
  {
    Domain::PlayerActivity activity;
    activity.kind       = kind;
    activity.targetName = std::move(target);
    return activity;
  }

  Domain::PlayerActivity MenuActivity(std::string key, std::optional<std::string> target = std::nullopt)
  {
    auto activity    = Activity(Domain::ActivityKind::Menu, std::move(target));
    activity.menuKey = std::move(key);
    return activity;
  }

  // The barter target handle can point at the player once the inventory side is
  // shown; the merchant is then the dialogue speaker.
  std::optional<std::string> BarterCounterpart(RE::PlayerCharacter* player)
  {
    auto ref = RE::TESObjectREFR::LookupByHandle(RE::BarterMenu::GetTargetRefHandle());
    if (ref && ref.get() != player) return RefName(ref.get());
    const auto speaker = Speaker();
    return RefName(speaker.get());
  }

  std::optional<Domain::PlayerActivity> MenuState(RE::PlayerCharacter* player, RE::UI* ui)
  {
    if (ui->IsMenuOpen(RE::LoadingMenu::MENU_NAME)) return Activity(Domain::ActivityKind::Loading);
    if (ui->IsMenuOpen(RE::MainMenu::MENU_NAME)) return MenuActivity("main");
    if (ui->IsMenuOpen(RE::TitleSequenceMenu::MENU_NAME)) return Activity(Domain::ActivityKind::NewGame);
    if (ui->IsMenuOpen(RE::BarterMenu::MENU_NAME)) return Activity(Domain::ActivityKind::Bartering, BarterCounterpart(player));
    if (ui->IsMenuOpen(RE::BookMenu::MENU_NAME))
    {
      auto* book = RE::BookMenu::GetTargetForm();
      return Activity(Domain::ActivityKind::Reading, book ? std::optional{Utf8(book->GetFullName())} : std::nullopt);
    }
    if (ui->IsMenuOpen(RE::CraftingMenu::MENU_NAME))
    {
      std::optional<std::string> station;
      if (auto menu = ui->GetMenu<RE::CraftingMenu>())
        if (auto* sub = menu->GetCraftingSubMenu(); sub && sub->furniture) station = Utf8(sub->furniture->GetFullName());
      return Activity(Domain::ActivityKind::Crafting, station);
    }
    if (ui->IsMenuOpen(RE::LockpickingMenu::MENU_NAME))
    {
      auto activity = Activity(Domain::ActivityKind::Lockpicking);
      if (auto target = RE::LockpickingMenu::GetTargetReference())
      {
        activity.targetName     = RefName(target.get());
        activity.lockDifficulty = LockLevel(target->GetLockLevel());
      }
      return activity;
    }
    if (ui->IsMenuOpen(RE::TrainingMenu::MENU_NAME))
    {
      const auto speaker = Speaker();
      return Activity(Domain::ActivityKind::Training, RefName(speaker.get()));
    }
    if (ui->IsMenuOpen(RE::ContainerMenu::MENU_NAME))
    {
      auto ref = RE::TESObjectREFR::LookupByHandle(RE::ContainerMenu::GetTargetRefHandle());
      return MenuActivity("container", RefName(ref.get()));
    }
    if (ui->IsMenuOpen(RE::GiftMenu::MENU_NAME))
    {
      auto ref = RE::TESObjectREFR::LookupByHandle(RE::GiftMenu::GetReceiverRefHandle());
      return MenuActivity("gift", RefName(ref.get()));
    }
    if (ui->IsMenuOpen(RE::DialogueMenu::MENU_NAME))
    {
      const auto speaker = Speaker();
      return Activity(Domain::ActivityKind::Talking, RefName(speaker.get()));
    }

    static const std::pair<std::string_view, std::string_view> plain[] = {
        {RE::InventoryMenu::MENU_NAME,    "inventory"   },
        {RE::MagicMenu::MENU_NAME,        "magic"       },
        {RE::MapMenu::MENU_NAME,          "map"         },
        {RE::JournalMenu::MENU_NAME,      "journal"     },
        {RE::StatsMenu::MENU_NAME,        "stats"       },
        {RE::TweenMenu::MENU_NAME,        "tween"       },
        {RE::SleepWaitMenu::MENU_NAME,    "sleepwait"   },
        {RE::FavoritesMenu::MENU_NAME,    "favorites"   },
        {RE::LevelUpMenu::MENU_NAME,      "levelup"     },
        {RE::Console::MENU_NAME,          "console"     },
        {RE::MessageBoxMenu::MENU_NAME,   "messagebox"  },
        {RE::RaceSexMenu::MENU_NAME,      "racesex"     },
        {RE::TutorialMenu::MENU_NAME,     "tutorial"    },
        {RE::CreationClubMenu::MENU_NAME, "creationclub"},
        {RE::ModManagerMenu::MENU_NAME,   "modmanager"  },
        {RE::CreditsMenu::MENU_NAME,      "credits"     },
    };
    for (const auto& [menu, key] : plain)
      if (ui->IsMenuOpen(menu)) return MenuActivity(std::string{key});
    return std::nullopt;
  }

  Domain::PlayerActivity ComputeActivity(RE::PlayerCharacter* player, RE::UI* ui, Clock::time_point now)
  {
    if (auto menu = MenuState(player, ui)) return *menu;

    RE::NiPointer<RE::Actor> mount;
    if (player->IsOnMount() && player->GetMount(mount) && mount) return Activity(Domain::ActivityKind::Riding, RefName(mount.get()));
    if (player->IsDead()) return Activity(Domain::ActivityKind::Dead);
    if (player->IsInRagdollState()) return Activity(Domain::ActivityKind::Ragdoll);
    if (player->IsInCombat())
    {
      std::optional<std::string> target;
      if (auto enemy = player->GetActorRuntimeData().currentCombatTarget.get()) target = RefName(enemy.get());
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

  Domain::ActorValueStorage ReadActorValues(RE::PlayerCharacter* player)
  {
    Domain::ActorValueStorage values;
    auto*                     owner = player->AsActorValueOwner();
    if (!owner) return values;
    static const std::tuple<std::string_view, RE::ActorValue, std::string_view> resources[] = {
        {"skyrim:health",  RE::ActorValue::kHealth,  "Здоровье" },
        {"skyrim:magicka", RE::ActorValue::kMagicka, "Магия"    },
        {"skyrim:stamina", RE::ActorValue::kStamina, "Запас сил"},
    };
    for (const auto& [key, value, label] : resources)
    {
      const float current = owner->GetActorValue(value);
      const float maximum =
        owner->GetPermanentActorValue(value) + player->GetActorValueModifier(RE::ACTOR_VALUE_MODIFIER::kTemporary, value);
      if (!std::isfinite(current) || !std::isfinite(maximum)) continue;
      values[std::string{
          key
      }] = {std::string{label}, Domain::ResourceActorValue{current, maximum}};
    }
    return values;
  }

  bool SameValues(const Domain::ActorValueStorage& left, const Domain::ActorValueStorage& right)
  {
    if (left.size() != right.size()) return false;
    for (const auto& [key, info] : left)
    {
      const auto found = right.find(key);
      if (found == right.end() || found->second.displayName != info.displayName) return false;
      const auto* a = std::get_if<Domain::ResourceActorValue>(&info.state);
      const auto* b = std::get_if<Domain::ResourceActorValue>(&found->second.state);
      if (!a || !b) return info.state == found->second.state;
      if (std::abs(a->current - b->current) > ValueEpsilon || std::abs(a->maximum - b->maximum) > ValueEpsilon) return false;
    }
    return true;
  }

  Domain::PlayerLocation ReadLocation(RE::PlayerCharacter* player, const Space& space)
  {
    Domain::PlayerLocation location;
    location.location   = {KeyOf(space.form), space.name};
    const auto position = player->GetPosition();
    const auto angle    = player->GetAngle();
    location.position   = {position.x, position.y, position.z};
    location.rotation   = {angle.x, angle.y, angle.z};
    return location;
  }

  template <class Command>
  bool Post(Command command)
  {
    auto&      runtime = Runtime::Get();
    const auto posted  = runtime.app->Exchange().Post({runtime.session.Generation(), std::move(command)});
    return posted == Dream::CommandPostResult::Queued || posted == Dream::CommandPostResult::Replaced;
  }

  export bool PlayerReady()
  {
    auto* player = RE::PlayerCharacter::GetSingleton();
    auto* ui     = RE::UI::GetSingleton();
    if (!player || !ui || !player->Is3DLoaded() || !player->GetParentCell()) return false;
    return !ui->IsMenuOpen(RE::LoadingMenu::MENU_NAME) && !ui->IsMenuOpen(RE::MainMenu::MENU_NAME) &&
           !ui->IsMenuOpen(RE::TitleSequenceMenu::MENU_NAME);
  }

  // A new character context: nothing sent yet, timers restart without a burst.
  export void BeginContext()
  {
    auto& state = Get();
    state       = {};
    state.gameStartedAtUnixMs =
      std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
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

  // Shared with fireflies so both use the same WRLD/CELL identity.
  export std::optional<Domain::FormKey> SpaceKey(RE::PlayerCharacter* player)
  {
    const auto space = CurrentSpace(player);
    if (!space) return std::nullopt;
    return KeyOf(space->form);
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

    if (!PlayerReady())
    {
      // Door/loading transitions must withdraw the old pose, rather than leave
      // a remote firefly at the last position until the new world is ready.
      if (state.sent.location && Post(Dream::LocalLocation{std::nullopt})) state.sent.location.reset();
      return;
    }

    auto*      player = RE::PlayerCharacter::GetSingleton();
    auto*      ui     = RE::UI::GetSingleton();
    const auto space  = CurrentSpace(player);
    if (!space) return;
    const auto spaceKey     = KeyOf(space->form);
    const bool spaceChanged = !state.lastSpace || *state.lastSpace != spaceKey;
    state.lastSpace         = spaceKey;

    const auto name = Utf8(player->GetName());
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
      state.nextMovement  = now + std::chrono::milliseconds{settings.playerSampleIntervalMs};
      auto       location = ReadLocation(player, *space);
      const bool teleport =
        state.sent.location && !spaceChanged &&
        Domain::Spatial::Distance(state.sent.location->position, location.position) > settings.movement.teleportDistance;
      const bool locationChanged = !state.sent.location || state.sent.location->location.locationId != spaceKey;
      if (locationChanged || teleport) Post(Dream::LocalLocation{location});
      if (Post(Dream::LocalMovement{location})) state.sent.location = location;
    }

    if (now >= state.nextDetails || state.deathHint)
    {
      state.nextDetails = now + DetailsInterval;
      Domain::PlayerDetails details;
      if (auto* race = player->GetRace()) details.race = Domain::NamedForm{KeyOf(race), Utf8(race->GetFullName())};
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
      auto values           = ReadActorValues(player);
      if (!state.sent.actorValues || !SameValues(*state.sent.actorValues, values))
        if (Post(Dream::LocalActorValues{values})) state.sent.actorValues = std::move(values);
    }
  }

}
