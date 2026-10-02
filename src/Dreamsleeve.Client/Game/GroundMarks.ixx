module;

#include "Prelude.hpp"

export module Dreamsleeve.Game.GroundMarks;

import std;
import Dreamsleeve.Runtime;
import Dreamsleeve.Game.World;
import Dreamsleeve.Game.PlacedReferences;
import Dreamsleeve.Game.Raycast;
import Dreamsleeve.UI.Nameplates;
import Dreamsleeve.Host.Bridge;
import Dreamsleeve.Host.Hud;
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

  // One word for a death without a killer: drowning while swimming, a fall
  // otherwise. The author's client localizes the label; the server only checks
  // its length and dictionary.
  // The label the server stores; readers see it as written, so it names what
  // it holds: "Убийца: <name>" or "Причина смерти: <cause>". A console kill
  // has no killer and no water either, so it reads as a fall.
  constexpr std::string_view KillerPrefix = "Убийца: ";
  constexpr std::string_view CausePrefix  = "Причина смерти: ";

  std::string CauseLabel(RE::PlayerCharacter* player)
  {
    auto* actorState = player->AsActorState();
    return std::string{CausePrefix} + (actorState && actorState->IsSwimming() ? "Утопление" : "Падение");
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
    const auto spot = World::Spot();
    if (!spot) return;
    state.deathReported = true;

    using Dreamsleeve::Utils::Text::CodePoints;
    using Dreamsleeve::Utils::Text::Prefix;
    std::string label;
    if (auto ref = killer.get())
      if (auto name = World::RefName(ref.get()))
        // Untrusted text: the name is cut so the whole label fits the server limit.
        label = std::string{KillerPrefix} + std::string{Prefix(*name, DeathLabelLimit - CodePoints(KillerPrefix))};
    if (label.empty()) label = CauseLabel(player);

    if (auto sent = runtime.session.ReportDeath(runtime.app->Exchange(), label, *spot))
      logger::info("Death reported (dead={}) with label of {} bytes", dead, label.size());
    else
      logger::warn("Death not reported: {}", sent.error());
  }

  struct Candidate
  {
    const Domain::GroundMark* mark{};
    double                    distance{};
  };

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

    using Domain::Spatial::ShownWithin;
    visual.nameShown = ShownWithin(candidate.distance, ui.groundNameDistance, visual.nameShown, Hysteresis);
    visual.textShown = ShownWithin(candidate.distance, ui.groundTextDistance, visual.textShown, Hysteresis);
    if (!visual.nameShown && !visual.textShown) return;

    const bool        own = runtime.session.SelfId() == mark.author.playerId;
    Nameplates::Label label{
        .key       = {death ? Nameplates::LabelKind::Death : Nameplates::LabelKind::Note, mark.markId},
        .nameSize  = static_cast<float>(ui.groundFontSize),
        .nameColor = Host::Hud::NameColor(ui, death),
        .style     = Host::Hud::MarkBubble(ui, death)
    };
    if (visual.nameShown)
      label.name = Host::Names::PlateName(
        runtime.session.PlayerNames().NameFor(mark.author.playerId, mark.author, mark.characterName, ui),
        mark.author);
    // The same filter of server-flagged ranges as chat lines and bubbles. The
    // date header comes and goes with the text.
    if (visual.textShown)
    {
      if (auto text = Host::Bridge::FilterText(mark.text, mark.flagged, ui, own)) label.bubble = std::move(*text);
      if (mark.gameDate && (death ? ui.deathDateHeader : ui.noteDateHeader))
        label.header = Host::FormatGameDate(*mark.gameDate, ui.markDateStyle);
    }
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
    if (runtime.context != Runtime::GameContext::Playing || !World::PlayerReady() || !player)
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
    const auto observer = World::Observe(player);
    if (!observer || (state.space && *state.space != observer->space)) ClearAll();
    if (!observer) return;
    state.space        = observer->space;
    const auto& space  = observer->space;
    const auto& origin = observer->position;

    std::vector<Candidate> notes;
    std::vector<Candidate> deaths;
    const auto&            book = runtime.session.PlayerNames();
    const auto             self = runtime.session.SelfId();
    for (const auto& [id, mark] : runtime.session.VisibleMarks())
    {
      const bool death = mark.kind == Domain::GroundMarkKind::Death;
      if (death ? !ui.showDeathMarks : !ui.showGroundNotes) continue;
      // Marks of ignored players are not drawn at all, nor others' under "guildmates only".
      if (book.Hides(mark.author.playerId, self) || runtime.session.GuildmatesOnlyHides(mark.author.playerId, ui.markGuildmatesOnly))
        continue;
      const auto distance =
        Domain::Spatial::Reach(space, origin, mark.placement.locationId, mark.placement.position, ui.groundDrawDistance);
      if (!distance) continue;
      (death ? deaths : notes).push_back({&mark, *distance});
    }
    Domain::Spatial::KeepNearest(notes, static_cast<std::size_t>(ui.maxVisibleNotes), &Candidate::distance);
    Domain::Spatial::KeepNearest(deaths, static_cast<std::size_t>(ui.maxVisibleDeaths), &Candidate::distance);

    std::unordered_set<Domain::GroundMarkId> visible;
    for (const auto& candidate : notes)
      Draw(candidate, combat, names, visible);
    for (const auto& candidate : deaths)
      Draw(candidate, combat, names, visible);

    state.refs.Retain(visible);
    std::erase_if(state.visuals, [&](const auto& entry) { return !visible.contains(entry.first); });
  }

}
