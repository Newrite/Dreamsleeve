module;

#include "Prelude.hpp"

export module Dreamsleeve.Game.GroundMarks;

import std;
import Dreamsleeve.Runtime;
import Dreamsleeve.Game.Telemetry;
import Dreamsleeve.Game.PlacedReferences;
import Dreamsleeve.Game.Raycast;
import Dreamsleeve.UI.Nameplates;
import Dreamsleeve.Host.Bridge;
import Dreamsleeve.Client.Utils;

// Ground marks in the world: a placed reference per visible note or death
// place, snapped to the floor once when spawned, with the author's name and
// the text as a nameplate. The source is the set the server shows this
// player (Host::Session::VisibleMarks); this module only chooses the nearest
// ones and draws them. Death reports leave from here too: once per death,
// from the frame, never from the event sink.
namespace GroundMarks
{

  namespace Dream = Dreamsleeve::Client;
  namespace Host  = Dreamsleeve::Host;
  using Clock     = std::chrono::steady_clock;

  // The server's default ChatInput.DeathMarkText; protocol v8 does not
  // advertise it, so the label is cut here before sending.
  constexpr std::size_t DeathLabelLimit = 64;
  constexpr float       SnapAbove       = 64.0f;   // The floor ray starts this far above the mark.
  constexpr float       SnapBelow       = 512.0f;  // And ends this far below it.
  constexpr float       LabelHeight     = 24.0f;   // Nameplate anchor above the placed reference.
  constexpr double      Hysteresis      = 1.1;     // A shown label hides only 10% past its distance.

  struct Visual
  {
    float z{};  // Where the reference stands after the floor snap.
    bool  nameShown{};
    bool  textShown{};
  };

  struct State
  {
    RE::TESObjectSTAT*                               noteBase{};
    RE::TESObjectSTAT*                               deathBase{};
    PlacedReferences::Set<Domain::GroundMarkId>      refs{"ground mark"};
    std::unordered_map<Domain::GroundMarkId, Visual> visuals;
    std::optional<Domain::FormKey>                   space;
    bool                                             deathReported{};
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
    state.visuals.clear();
    state.space.reset();
  }

  // Leaving the character context: the next death is a new one.
  export void EndContext()
  {
    ClearAll();
    Get().deathReported = false;
  }

  // kDataLoaded: each kind resolves its own form; a missing one disables that kind only.
  export void ResolveForms()
  {
    auto&       state   = Get();
    const auto& runtime = Runtime::Get();
    if (!runtime.app) return;
    const auto& config = runtime.app->Settings().client;
    state.noteBase     = PlacedReferences::ResolveStatic("ground note", config.groundNotePlugin, config.groundNoteFormId);
    state.deathBase    = PlacedReferences::ResolveStatic("death mark", config.deathMarkPlugin, config.deathMarkFormId);
  }

  export std::size_t Count()
  {
    return Get().refs.Count();
  }

  // Where the player stands now, as a mark placement; absent outside a ready world.
  export std::optional<Domain::GroundMarkPlacement> CurrentPlacement()
  {
    auto* player = RE::PlayerCharacter::GetSingleton();
    if (!player || !Telemetry::PlayerReady()) return std::nullopt;
    const auto space = Telemetry::SpaceKey(player);
    if (!space) return std::nullopt;
    const auto position = player->GetPosition();
    return Domain::GroundMarkPlacement{
        *space,
        {position.x, position.y, position.z},
        player->GetAngleZ()
    };
  }

  // One word for a death without a killer: drowning while swimming, a fall
  // otherwise. The author's client localizes the label; the server only checks
  // its length and dictionary.
  std::string CauseLabel(RE::PlayerCharacter* player)
  {
    auto* actorState = player->AsActorState();
    return actorState && actorState->IsSwimming() ? "утопление" : "падение";
  }

  // Frame handler of the death notice. The first notice (dying) fixes the
  // place and the label; the second (dead) only counts if the first was
  // missed. A repeat is possible after IsDead() returned false again.
  export void NoteDeath(RE::ObjectRefHandle killer, bool dead)
  {
    auto& runtime = Runtime::Get();
    auto& state   = Get();
    if (state.deathReported) return;
    auto* player = RE::PlayerCharacter::GetSingleton();
    if (!runtime.app || runtime.context != Runtime::GameContext::Playing || !player) return;
    const auto placement = CurrentPlacement();
    if (!placement) return;
    state.deathReported = true;

    std::string label;
    if (auto ref = killer.get()) label = Telemetry::RefName(ref.get()).value_or(std::string{});
    if (label.empty()) label = CauseLabel(player);
    // Untrusted text: bounded here, checked by the server.
    label = std::string{Dreamsleeve::Utils::Text::Prefix(label, DeathLabelLimit)};

    if (auto sent = runtime.session.ReportDeath(runtime.app->Exchange(), label, *placement))
      logger::info("Death reported (dead={}) with label of {} bytes", dead, label.size());
    else
      logger::warn("Death not reported: {}", sent.error());
  }

  struct Candidate
  {
    const Domain::GroundMark* mark{};
    double                    distance{};
  };

  // Shown within `limit`, hidden again only past limit * Hysteresis.
  bool Within(double distance, double limit, bool shown)
  {
    return distance <= (shown ? limit * Hysteresis : limit);
  }

  Nameplates::BubbleStyle Style(const Host::UiSettings& ui, bool death)
  {
    return {
        static_cast<float>(ui.groundFontSize),
        static_cast<float>(ui.groundMaxWidth),
        static_cast<float>(death ? ui.deathBackground : ui.groundBackground),
        death ? ui.deathBorder : ui.groundBorder,
        Host::ParseColor(death ? ui.deathTextColor : ui.groundTextColor).value_or(Nameplates::DefaultTextColor)
    };
  }

  void Draw(const Candidate& candidate, bool combat, Nameplates::Frame& names, std::unordered_set<Domain::GroundMarkId>& visible)
  {
    auto&       runtime = Runtime::Get();
    auto&       state   = Get();
    const auto& ui      = runtime.ui.ui.chat;
    const auto& mark    = *candidate.mark;
    const bool  death   = mark.kind == Domain::GroundMarkKind::Death;
    auto*       base    = death ? state.deathBase : state.noteBase;
    if (!base) return;

    auto& visual = state.visuals[mark.markId];
    auto  ref    = state.refs.Resolve(mark.markId);
    if (!ref)
    {
      const RE::NiPoint3 at{mark.placement.position.X, mark.placement.position.Y, mark.placement.position.Z};
      const auto         floor  = Raycast::GroundBelow(at, SnapAbove, SnapBelow);
      const float        offset = static_cast<float>(death ? ui.deathMarkOffset : ui.groundNoteOffset);
      visual.z                  = floor ? *floor + offset : at.z;
      const RE::NiPoint3 position{at.x, at.y, visual.z};
      const RE::NiPoint3 rotation{0.0f, 0.0f, mark.placement.heading};
      const auto&        client  = runtime.app->Settings().client;
      auto               spawned = PlacedReferences::Spawn(
        death ? "death mark" : "ground note",
        base,
        position,
        rotation,
        death ? client.deathMarkScale : client.groundNoteScale);
      if (!spawned) return;
      state.refs.Keep(mark.markId, *spawned);
    }
    visible.insert(mark.markId);
    if (combat && ui.combatHideGroundText) return;

    visual.nameShown = Within(candidate.distance, ui.groundNameDistance, visual.nameShown);
    visual.textShown = Within(candidate.distance, ui.groundTextDistance, visual.textShown);
    if (!visual.nameShown && !visual.textShown) return;

    const bool        own = runtime.session.SelfId() == mark.author.playerId;
    Nameplates::Label label{
        .key       = {death ? Nameplates::LabelKind::Death : Nameplates::LabelKind::Note, mark.markId},
        .nameSize  = static_cast<float>(ui.groundFontSize),
        .nameColor = Host::ParseColor(death ? ui.deathTextColor : ui.fireflyNameColor).value_or(Nameplates::DefaultTextColor),
        .style     = Style(ui, death)
    };
    if (visual.nameShown) label.name = runtime.session.PlayerNames().NameFor(mark.author.playerId, mark.author, mark.characterName, ui);
    // The same filter of server-flagged ranges as chat lines and bubbles.
    if (visual.textShown)
      if (auto text = Host::Bridge::FilterText(mark.text, mark.flagged, ui, own)) label.bubble = std::move(*text);
    const RE::NiPoint3 anchor{mark.placement.position.X, mark.placement.position.Y, visual.z + LabelHeight};
    Nameplates::Add(names, std::move(label), anchor, ui.fireflyNameOcclusion);
  }

  // Adds the labels of drawn marks to the frame the caller publishes.
  export void Tick(Clock::time_point, Nameplates::Frame& names)
  {
    auto& runtime = Runtime::Get();
    auto& state   = Get();
    if (!runtime.app) return;
    auto* player = RE::PlayerCharacter::GetSingleton();
    if (runtime.context != Runtime::GameContext::Playing || !Telemetry::PlayerReady() || !player)
    {
      ClearAll();
      return;
    }
    // Resurrection, a load or a new game: the next death may be reported again.
    if (!player->IsDead()) state.deathReported = false;

    const auto& ui     = runtime.ui.ui.chat;
    const bool  combat = player->IsInCombat();
    if ((!ui.showGroundNotes && !ui.showDeathMarks) || (combat && ui.combatHideGroundMarks) || (!state.noteBase && !state.deathBase))
    {
      ClearAll();
      return;
    }
    const auto space = Telemetry::SpaceKey(player);
    if (!space || (state.space && *state.space != *space)) ClearAll();
    if (!space) return;
    state.space = space;

    const auto             self = player->GetPosition();
    const Domain::Position origin{self.x, self.y, self.z};
    std::vector<Candidate> notes;
    std::vector<Candidate> deaths;
    auto&                  book = runtime.session.PlayerNames();
    for (const auto& [id, mark] : runtime.session.VisibleMarks())
    {
      const bool death = mark.kind == Domain::GroundMarkKind::Death;
      if (death ? !ui.showDeathMarks : !ui.showGroundNotes) continue;
      if (mark.placement.locationId != *space) continue;
      // Marks of ignored players are not drawn at all.
      if (runtime.session.SelfId() != mark.author.playerId && book.Ignored(mark.author.playerId)) continue;
      const auto distance = Domain::Spatial::Distance(origin, mark.placement.position);
      if (distance > ui.groundDrawDistance) continue;
      (death ? deaths : notes).push_back({&mark, distance});
    }
    const auto nearest = [](std::vector<Candidate>& list, double limit) {
      std::ranges::sort(list, {}, &Candidate::distance);
      if (list.size() > static_cast<std::size_t>(limit)) list.resize(static_cast<std::size_t>(limit));
    };
    nearest(notes, ui.maxVisibleNotes);
    nearest(deaths, ui.maxVisibleDeaths);

    std::unordered_set<Domain::GroundMarkId> visible;
    for (const auto& candidate : notes)
      Draw(candidate, combat, names, visible);
    for (const auto& candidate : deaths)
      Draw(candidate, combat, names, visible);

    state.refs.Retain(visible);
    std::erase_if(state.visuals, [&](const auto& entry) { return !visible.contains(entry.first); });
  }

}
