module;

#include "Prelude.hpp"
#include <magic_enum/magic_enum.hpp>

export module Dreamsleeve.Game.World;

import std;
export import Dreamsleeve.Client.Domain.Logic;
import Dreamsleeve.Client.Utils;

// The game world read as domain values, on the game thread. No state and no
// sending: telemetry, fireflies, ground marks and the UI host ask the same
// readers, and no game pointer they return outlives the frame. The pure rules
// applied to these values are Domain.Logic's, tested without the game; the
// engine's own encodings (marker enumerators, raw calendar values, plugin file
// names) are turned into domain values here and never reach Core.
export namespace World
{

  // Game strings are UTF-8 by convention, but a plugin may carry another
  // encoding; repaired here, every name the client sends is valid UTF-8.
  std::string Text(const char* text)
  {
    return text ? Dreamsleeve::Utils::Text::Repair(text) : std::string{};
  }

  // A plugin file name as keys carry it: ASCII case folded, otherwise opaque
  // (no trimming or Unicode changes). Server-accepted state already carries
  // canonical keys; nothing normalizes them again.
  std::string PluginName(std::string_view file)
  {
    std::string result{file};
    std::ranges::transform(result, result.begin(), Dreamsleeve::Utils::Text::AsciiLower);
    return result;
  }

  // Origin plugin plus local id; dynamic forms cannot be shared across clients.
  Domain::FormKey KeyOf(const RE::TESForm* form)
  {
    if (!form) return {};
    const auto* file = form->GetFile(0);
    if (!file || form->IsDynamicForm()) return {"runtime", form->GetFormID()};
    return {PluginName(file->GetFilename()), form->GetLocalFormID()};
  }

  // The player's WRLD, or the CELL indoors, with its display name.
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
    if (cell->IsInteriorCell()) return Space{cell, Text(cell->GetFullName()), true};
    auto* world = player->GetWorldspace();
    if (!world) return std::nullopt;
    return Space{world, Text(world->GetFullName()), false};
  }

  // The world can be read: the player's 3D and cell are loaded and no loading,
  // main or title menu covers it.
  bool PlayerReady()
  {
    auto* player = RE::PlayerCharacter::GetSingleton();
    auto* ui     = RE::UI::GetSingleton();
    if (!player || !ui || !player->Is3DLoaded() || !player->GetParentCell()) return false;
    return !ui->IsMenuOpen(RE::LoadingMenu::MENU_NAME) && !ui->IsMenuOpen(RE::MainMenu::MENU_NAME) &&
           !ui->IsMenuOpen(RE::TitleSequenceMenu::MENU_NAME);
  }

  // Where fireflies and marks are measured from: the player's space and position.
  struct Observer
  {
    Domain::LocationId space;
    Domain::Position   position;
  };

  std::optional<Observer> Observe(RE::PlayerCharacter* player)
  {
    const auto space = CurrentSpace(player);
    if (!space) return std::nullopt;
    const auto position = player->GetPosition();
    return Observer{
        KeyOf(space->form),
        {position.x, position.y, position.z}
    };
  }

  // The player's location in a space, turned as the character is.
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

  // The display name of a reference: a combat target, a speaker, a killer.
  std::optional<std::string> RefName(RE::TESObjectREFR* ref)
  {
    if (!ref) return std::nullopt;
    auto name = Text(ref->GetName());
    if (name.empty()) return std::nullopt;
    return name;
  }

  // Where the player stands now, as a mark placement; absent outside a ready world.
  std::optional<Domain::GroundMarkPlacement> Placement()
  {
    auto* player = RE::PlayerCharacter::GetSingleton();
    if (!player || !PlayerReady()) return std::nullopt;
    const auto observer = Observe(player);
    if (!observer) return std::nullopt;
    return Domain::GroundMarkPlacement{observer->space, observer->position, player->GetAngleZ()};
  }

  // The game calendar now. The vanilla calendar has no era variable (its date
  // line prints "4E"), so the era is the Fourth. Values a mod pushed out of
  // range are clamped rather than refused: the date is flavour.
  std::optional<Domain::GameDate> GameDate()
  {
    auto* calendar = RE::Calendar::GetSingleton();
    if (!calendar) return std::nullopt;
    const auto month = std::min<std::uint32_t>(calendar->GetMonth(), 11) + 1;
    const auto hours = std::clamp(calendar->GetHour(), 0.0f, 24.0f);
    const auto hour  = std::min(static_cast<std::uint32_t>(hours), 23u);
    const auto day   = static_cast<std::uint32_t>(std::max(calendar->GetDay(), 1.0f));
    return Domain::GameDate{
        .era       = 4,
        .year      = std::clamp<std::uint32_t>(calendar->GetYear(), 1, 99999),
        .month     = month,
        .day       = std::clamp<std::uint32_t>(day, 1, Domain::Calendar::MonthLength(month)),
        .dayOfWeek = calendar->GetDayOfWeek() % 7,
        .hour      = hour,
        .minute    = std::min(static_cast<std::uint32_t>((hours - static_cast<float>(hour)) * 60.0f), 59u)
    };
  }

  // Where a ground mark would stand now, with the in-game date.
  std::optional<Domain::MarkSpot> Spot()
  {
    auto placement = Placement();
    auto gameDate  = GameDate();
    if (!placement || !gameDate) return std::nullopt;
    return Domain::MarkSpot{*placement, *gameDate};
  }

  // The innermost named location of the player (a hold, a city, a dungeon).
  std::string LocationName(RE::PlayerCharacter* player)
  {
    for (auto* location = player->GetCurrentLocation(); location; location = location->parentLoc)
    {
      auto name = Text(location->GetFullName());
      if (!name.empty()) return name;
    }
    return {};
  }

  // The worldspace the player is in, or left for the current interior.
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
      auto name = Text(world->GetFullName());
      if (!name.empty()) return name;
    }
    return {};
  }

  // How far around the player map markers are searched.
  constexpr float MarkerRadius = 16384.0f;

  // The marker key the UI names, from the engine's enumerator ("kNordicRuin"
  // -> "nordicruin"); DLC variants fold into their base kinds.
  std::string MarkerKind(RE::MARKER_TYPE type)
  {
    std::string name{magic_enum::enum_name(type)};
    if (name.size() > 1 && name.front() == 'k') name.erase(0, 1);
    std::ranges::transform(name, name.begin(), Dreamsleeve::Utils::Text::AsciiLower);
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
    std::string kind;  // Normalized key; the UI maps it to a label.
    float       distance{std::numeric_limits<float>::max()};
  };

  // Nearest visible map marker of the outer world chain; a marker named like
  // the current location wins. Callers throttle it: it walks persistent refs.
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
        auto        name = Text(data.locationName.GetFullName());
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

  // The actor the player talks to, or talked to last. Owning pointer for the
  // duration of the call; handles are the only thing kept between frames.
  RE::NiPointer<RE::TESObjectREFR> Speaker()
  {
    auto* topics = RE::MenuTopicManager::GetSingleton();
    if (!topics) return {};
    if (auto speaker = topics->speaker.get()) return speaker;
    return topics->lastSpeaker.get();
  }

  // Hidden furniture helpers are not always flagged: Skyrim.esm's mining
  // markers (PickaxeMining*Marker) have plain record flags, a *Marker.nif model
  // and the placeholder name "This should not be visible".
  bool HiddenFurniture(const RE::TESBoundObject* base)
  {
    using Dreamsleeve::Utils::Text::ContainsAsciiInsensitive;
    if (!base) return true;
    if ((base->GetFormFlags() & RE::TESFurniture::RecordFlags::kIsMarker) != 0) return true;
    if (const auto* model = base->As<RE::TESModel>(); model && model->GetModel() && ContainsAsciiInsensitive(model->GetModel(), "marker"))
      return true;
    const auto* full = base->As<RE::TESFullName>();
    if (!full || full->GetFullNameLength() == 0) return true;
    return ContainsAsciiInsensitive(full->GetFullName(), "should not be visible");
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

  // A menu without an activity of its own, by the key the UI names.
  Domain::PlayerActivity InMenu(std::string key, std::optional<std::string> target = std::nullopt)
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

  // What an open menu says the player does; absent with no known menu open.
  std::optional<Domain::PlayerActivity> MenuActivity(RE::PlayerCharacter* player, RE::UI* ui)
  {
    if (ui->IsMenuOpen(RE::LoadingMenu::MENU_NAME)) return Activity(Domain::ActivityKind::Loading);
    if (ui->IsMenuOpen(RE::MainMenu::MENU_NAME)) return InMenu("main");
    if (ui->IsMenuOpen(RE::TitleSequenceMenu::MENU_NAME)) return Activity(Domain::ActivityKind::NewGame);
    if (ui->IsMenuOpen(RE::BarterMenu::MENU_NAME)) return Activity(Domain::ActivityKind::Bartering, BarterCounterpart(player));
    if (ui->IsMenuOpen(RE::BookMenu::MENU_NAME))
    {
      auto* book = RE::BookMenu::GetTargetForm();
      return Activity(Domain::ActivityKind::Reading, book ? std::optional{Text(book->GetFullName())} : std::nullopt);
    }
    if (ui->IsMenuOpen(RE::CraftingMenu::MENU_NAME))
    {
      std::optional<std::string> station;
      if (auto menu = ui->GetMenu<RE::CraftingMenu>())
        if (auto* sub = menu->GetCraftingSubMenu(); sub && sub->furniture) station = Text(sub->furniture->GetFullName());
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
      return InMenu("container", RefName(ref.get()));
    }
    if (ui->IsMenuOpen(RE::GiftMenu::MENU_NAME))
    {
      auto ref = RE::TESObjectREFR::LookupByHandle(RE::GiftMenu::GetReceiverRefHandle());
      return InMenu("gift", RefName(ref.get()));
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
      if (ui->IsMenuOpen(menu)) return InMenu(std::string{key});
    return std::nullopt;
  }

  // The player's resources: current and maximum with temporary modifiers.
  Domain::ActorValueStorage ActorValues(RE::PlayerCharacter* player)
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
      if (!Domain::Checks::Finite(current, maximum)) continue;
      values.insert_or_assign(
        std::string{
            key
      },
        Domain::ActorValueInfo{
          std::string{label}, Domain::ResourceActorValue{Domain::Players::Points(current), Domain::Players::Points(maximum)}});
    }
    return values;
  }

}
