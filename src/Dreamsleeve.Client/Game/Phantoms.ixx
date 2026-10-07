module;
#include "Prelude.hpp"
export module Dreamsleeve.Game.Phantoms;
import std;
import Dreamsleeve.Runtime;
import Dreamsleeve.Game.World;
import Dreamsleeve.Game.PhantomCapture;
import Dreamsleeve.Game.PhantomCaptureRules;
import Dreamsleeve.Game.PhantomScene;
import Dreamsleeve.Game.PlayerLabels;
import Dreamsleeve.UI.Nameplates;
import Dreamsleeve.Host.PhantomSettings;
import Dreamsleeve.Client.Phantom.Exchange;
#ifdef DREAMSLEEVE_DIAGNOSTICS
import Dreamsleeve.Client.Diagnostics.PhantomRecorder;
#endif

namespace Phantoms
{
  namespace P       = Dreamsleeve::Client::Phantom;
  namespace Capture = Dreamsleeve::Game::PhantomCapture;
  namespace Scene   = Dreamsleeve::Game::PhantomScene;
  using Clock       = std::chrono::steady_clock;

  struct Slot
  {
    std::uint64_t                 view{};
    P::Generation                 generation;
    Scene::Context                context;
    std::unique_ptr<Scene::Scene> scene;
  };

  struct Visual
  {
    std::optional<Slot>                                    current, candidate;
    std::optional<std::pair<std::uint64_t, P::Generation>> rejected;
    Clock::time_point                                      applied{};
    bool                                                   active{};
    std::optional<std::pair<P::Vec3, float>>               look;

    std::uint64_t MemoryBytes() const
    {
      return (current ? current->scene->MemoryBytes() : 0) + (candidate ? candidate->scene->MemoryBytes() : 0);
    }
  };

  struct RetainedSource
  {
    P::Generation                    generation;
    P::Sequence                      sequence;
    std::unique_ptr<Capture::Source> source;
  };

  struct State
  {
    Capture::Engine                              engine;
    std::unique_ptr<Capture::Source>             source;
    std::optional<RetainedSource>                previous;
    std::unordered_map<Domain::PlayerId, Visual> visuals;
    P::Generation                                generation;
    // Allocation high-watermark never rolls back with the retained native Source.
    std::uint64_t                  lastGeneration{};
    P::Sequence                    sequence;
    std::optional<P::ViewSettings> settings;
    Capture::Cadence               cadence;
    Clock::time_point              nextFailure{};
    std::uint32_t                  omittedGeometry{}, hiddenGeometry{};
    Capture::Context               context;
    bool                           waiting{};
    std::uint64_t                  cursor{};
#ifdef DREAMSLEEVE_DIAGNOSTICS
    std::shared_ptr<const P::ValidatedAsset> diagnosticAsset;
    bool                                     publishing{};
    Clock::time_point                        nextNetworkReport{};
#endif
  };

  State& Get()
  {
    static auto* state = new State;
    return *state;
  }

  std::uint64_t Micros(Clock::time_point now)
  {
    return static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::microseconds>(now.time_since_epoch()).count());
  }

  void Error(const P::Error& error, Clock::time_point now)
  {
    auto& state = Get();
    if (now < state.nextFailure) return;
    state.nextFailure = now + std::chrono::seconds(5);
    logger::warn("Phantom: {} ({})", error.field, static_cast<int>(error.reason));
  }

  export void Install(Capture::Engine engine)
  {
    Get().engine = engine;
  }

  void ResetResources()
  {
    auto& state   = Get();
    auto& runtime = Runtime::Get();
    if (runtime.app)
      for (const auto& [id, visual] : state.visuals)
        runtime.app->Exchange().Phantoms().SceneMemory(id, 0);
    state.visuals.clear();
    state.source.reset();
    state.previous.reset();
#ifdef DREAMSLEEVE_DIAGNOSTICS
    state.diagnosticAsset.reset();
#endif
  }

  export void Clear(std::string_view reason = "game-context-ended")
  {
#ifdef DREAMSLEEVE_DIAGNOSTICS
    auto& recorder = Dreamsleeve::Client::Diagnostics::Phantoms();
    if (recorder.Active()) logger::info("Phantom recording stopped: {}", reason);
    recorder.Stop(reason);
    Dreamsleeve::Game::PhantomReplay::Stop(reason);
#endif
    ResetResources();
    Get().context.Reset();
    Get().waiting = false;
  }

#ifdef DREAMSLEEVE_DIAGNOSTICS
  export void StartRecording(std::uint32_t scenario, bool thirtySeconds)
  {
    Dreamsleeve::Game::PhantomReplay::Stop();
    const auto logs    = SKSE::log::log_directory();
    auto&      runtime = Runtime::Get();
    if (!logs || !runtime.app || runtime.context != Runtime::GameContext::Playing) return;
    if (
      Dreamsleeve::Client::Diagnostics::Phantoms().Start(
        *logs / "DreamsleevePhantomDiagnostics",
        scenario,
        thirtySeconds ? 30 : 15,
        runtime.app->Exchange().Phantoms().Settings().sampleRate))
    {
      Get().source.reset();
      Get().previous.reset();
      Get().diagnosticAsset.reset();
      Get().cadence.Reset();
      logger::info("Phantom diagnostics recording requested");
    }
  }

  export void StartReplay(std::uint32_t scenario)
  {
    const auto logs = SKSE::log::log_directory();
    if (!logs || Runtime::Get().context != Runtime::GameContext::Playing || Dreamsleeve::Client::Diagnostics::Phantoms().Active()) return;
    if (Dreamsleeve::Game::PhantomReplay::Start(*logs / "DreamsleevePhantomDiagnostics", scenario, Get().engine)) ResetResources();
  }

  void Record(std::shared_ptr<const P::Snapshot> pose, RE::PlayerCharacter& player, Clock::time_point start, bool firstPerson)
  {
    auto& recorder = Dreamsleeve::Client::Diagnostics::Phantoms();
    if (!recorder.Active()) return;
    const auto position = player.GetPosition(), angle = player.GetAngle();
    recorder.Sample(
      Get().diagnosticAsset,
      std::move(pose),
      {
          0,
          0,
          Micros(start),
          {position.x, position.y, position.z},
          {angle.x,    angle.y,    angle.z   }
    },
      std::chrono::duration<double, std::milli>(Clock::now() - start).count(),
      firstPerson);
  }
#endif

  void ReportCaptureHealth()
  {
    auto&      state  = Get();
    const auto status = state.source->ReadStatus();
    if (state.omittedGeometry != status.omitted || state.hiddenGeometry != status.hidden)
    {
      logger::info("Phantom capture health: omitted={}, hidden={}, {}", status.omitted, status.hidden, status.detail);
      state.omittedGeometry = status.omitted;
      state.hiddenGeometry  = status.hidden;
    }
#ifdef DREAMSLEEVE_DIAGNOSTICS
    Dreamsleeve::Client::Diagnostics::Phantoms().Partial(status.omitted, status.hidden, status.detail);
#endif
  }

  // Tick owns capture and playback on the same main-loop thread, after the
  // game's frame update. No actor-update callback touches this state.
  void CapturePlayer(RE::PlayerCharacter& player)
  {
    auto&      runtime    = Runtime::Get();
    auto&      state      = Get();
    const auto now        = Clock::now();
    auto&      exchange   = runtime.app->Exchange().Phantoms();
    const auto settings   = exchange.Settings();
    bool       publishing = exchange.Available() && settings.publish;
    P::Limits  captureLimits;
#ifdef DREAMSLEEVE_DIAGNOSTICS
    const bool recording = Dreamsleeve::Client::Diagnostics::Phantoms().Active();
    if (recording)
    {
      publishing    = false;
      captureLimits = Dreamsleeve::Client::Diagnostics::CaptureLimits();
    }
    if (state.source && state.publishing != publishing)
    {
      state.source.reset();
      state.previous.reset();
    }
    state.publishing = publishing;
    if (!publishing && !recording)
#else
    if (!publishing)
#endif
    {
      state.source.reset();
      state.previous.reset();
#ifdef DREAMSLEEVE_DIAGNOSTICS
      state.diagnosticAsset.reset();
#endif
      return;
    }
    if (state.source && publishing && !exchange.Capturing(state.generation))
    {
      if (state.previous && exchange.Capturing(state.previous->generation))
      {
        state.source     = std::move(state.previous->source);
        state.generation = state.previous->generation;
        state.sequence   = state.previous->sequence;
        state.previous.reset();
      }
      else
        state.source.reset();
      state.cadence.Defer(now);
      return;
    }
    if (!state.source && publishing && exchange.Capturing(state.generation)) exchange.RestartCapture();
    if (!state.cadence.Due(now, settings.sampleRate)) return;
    const auto* camera      = RE::PlayerCamera::GetSingleton();
    const bool  firstPerson = camera && camera->IsInFirstPerson();
    if (state.previous && !exchange.Capturing(state.previous->generation)) state.previous.reset();
    const auto priorPose = [&]() -> std::shared_ptr<const P::Snapshot> {
      if (!publishing || !state.previous) return {};
      auto& old = *state.previous;
      if (old.sequence.value == std::numeric_limits<std::uint64_t>::max()) return {};
      auto pose = old.source->Sample(player, firstPerson, {old.generation, {++old.sequence.value}, 1, Micros(now)}, true);
      return pose ? std::make_shared<const P::Snapshot>(std::move(*pose)) : nullptr;
    };
    if (!state.source || ((!publishing || exchange.CanReplace()) && state.source->RebuildDue(Micros(now))))
    {
      const auto changeReason = state.source ? state.source->ChangeReason() : std::string_view{"initial"};
      if (state.source) state.source->DeferRebuild(Micros(now));
      const auto replace = [&]() -> bool {
        if (state.lastGeneration == std::numeric_limits<std::uint64_t>::max()) return false;
        const P::Generation nextGeneration{state.lastGeneration + 1};
        auto                opened = Capture::Open(state.engine, player, firstPerson, {nextGeneration, {1}, 1, Micros(now)}, captureLimits);
        if (!opened)
        {
#ifdef DREAMSLEEVE_DIAGNOSTICS
          Dreamsleeve::Client::Diagnostics::Phantoms().Failed(opened.error());
#endif
          Error(opened.error(), now);
          return false;
        }
        auto asset = std::move(opened->asset);
        logger::info(
          "Phantom capture: generation {}, {} channels, {} geometry, {:.2f} MiB native NIF, {:.2f} ms, reason={}",
          nextGeneration.value,
          asset.Layout().requiredChannels.size(),
          asset.Layout().bounds.size(),
          asset.MemoryBytes() / 1048576.0,
          std::chrono::duration<double, std::milli>(Clock::now() - now).count(),
          changeReason);
        if (publishing && !exchange.Submit(nextGeneration, asset)) return false;
        if (publishing && state.source && exchange.Capturing(state.generation))
          state.previous = RetainedSource{state.generation, state.sequence, std::move(state.source)};
        state.generation     = nextGeneration;
        state.lastGeneration = nextGeneration.value;
        state.sequence       = {1};
#ifdef DREAMSLEEVE_DIAGNOSTICS
        state.diagnosticAsset = std::make_shared<const P::ValidatedAsset>(std::move(asset));
#endif
        state.source = std::move(opened->source);
        ReportCaptureHealth();
        auto initial = std::make_shared<const P::Snapshot>(std::move(opened->initial));
#ifdef DREAMSLEEVE_DIAGNOSTICS
        Record(initial, player, now, firstPerson);
#endif
        if (publishing) exchange.Submit(std::move(initial), priorPose());
        return true;
      };
      if (replace()) return;
      if (!state.source)
      {
        state.cadence.Defer(now);
        return;
      }
    }
    if (publishing && !exchange.PosesRequired()) return;
    if (state.sequence.value == std::numeric_limits<std::uint64_t>::max())
    {
      state.source.reset();
      return;
    }
    auto pose =
      state.source->Sample(player, firstPerson, {state.generation, P::Sequence{++state.sequence.value}, 1, Micros(now)}, publishing);
    if (!pose)
    {
#ifdef DREAMSLEEVE_DIAGNOSTICS
      Dreamsleeve::Client::Diagnostics::Phantoms().Failed(pose.error());
#endif
      Error(pose.error(), now);
      if (pose.error().reason == P::Failure::Stale)
        state.source.reset();
      else
        state.cadence.Defer(now);
      return;
    }
    ReportCaptureHealth();
    auto sampled = std::make_shared<const P::Snapshot>(std::move(*pose));
#ifdef DREAMSLEEVE_DIAGNOSTICS
    Record(sampled, player, now, firstPerson);
#endif
    if (publishing) exchange.Submit(std::move(sampled), priorPose());
  }

  export bool UseFirefly(Domain::PlayerId id)
  {
    const auto& state = Get();
    if (!state.settings || !state.settings->receive) return true;
    const auto found = state.visuals.find(id);
    if (found != state.visuals.end() && found->second.active) return false;
    return state.settings->fallback;
  }

  export std::size_t Count()
  {
    return std::ranges::count_if(Get().visuals, [](const auto& entry) { return entry.second.active; });
  }

  bool Matches(const Slot& slot, const P::Remote& remote, Scene::Context context)
  {
    return slot.view == remote.view && slot.generation == remote.descriptor.generation && slot.context == context;
  }

  void Hide(Visual& visual, Clock::time_point now)
  {
    if (visual.current)
    {
      auto hidden = visual.current->scene->Hide(visual.current->context);
      if (!hidden) Error(hidden.error(), now);
    }
    visual.active = false;
  }

  void ForgetCleared(Visual& visual, P::Exchange& exchange, Domain::PlayerId id)
  {
    if (visual.candidate && visual.candidate->scene->MemoryBytes() == 0) visual.candidate.reset();
    if (visual.current && visual.current->scene->MemoryBytes() == 0)
    {
      visual.current.reset();
      visual.look.reset();
      visual.active = false;
    }
    if (visual.current && !visual.current->scene->Ready()) visual.active = false;
    exchange.SceneMemory(id, visual.MemoryBytes());
  }

  export void Tick(Clock::time_point now, Nameplates::Frame& names)
  {
    auto& runtime = Runtime::Get();
    auto& state   = Get();
    if (!runtime.app || !state.engine.mainThread) return;
    auto&      exchange = runtime.app->Exchange().Phantoms();
    const auto settings = Dreamsleeve::Host::PhantomSettings(runtime.ui.ui.chat);
    if (!state.settings || settings != *state.settings)
    {
      if (state.settings && (settings.memoryBytes < state.settings->memoryBytes || settings.publish != state.settings->publish))
        ResetResources();
      state.settings = settings;
      exchange.Configure(settings);
    }
    auto* player = RE::PlayerCharacter::GetSingleton();
    auto* cell   = player ? player->GetParentCell() : nullptr;
    auto* root   = player ? player->Get3D(false) : nullptr;
    auto* parent = root ? root->parent : nullptr;
    if (runtime.context != Runtime::GameContext::Playing)
    {
      Clear("game-context-ended");
      return;
    }
    const auto space = player && cell ? World::CurrentSpace(player) : std::nullopt;
    const bool ready = World::PlayerReady() && cell && cell->IsAttached() && parent && space;
    const auto observed = state.context.Observe(ready
      ? std::optional<Capture::Context::Space>{{space->form->GetFormID(), space->interior}}
      : std::nullopt);
    if (observed == Capture::Context::Observation::Waiting)
    {
#ifdef DREAMSLEEVE_DIAGNOSTICS
      Dreamsleeve::Game::PhantomReplay::Pause();
#endif
      if (!state.waiting)
      {
        logger::info(
          "Phantom capture waiting: player={}, cell={}, attached={}, root={}, parent={}, space={}",
          player != nullptr,
          cell != nullptr,
          cell && cell->IsAttached(),
          root != nullptr,
          parent != nullptr,
          space.has_value());
        // A transient missing cell/3D is not an appearance change. Retain
        // owned capture bindings and masks; only hide remote visuals.
        for (auto& [id, visual] : state.visuals)
          Hide(visual, now);
        state.waiting = true;
      }
      return;
    }
    if (state.waiting) logger::info("Phantom capture resumed in {} {:08X}", space->interior ? "CELL" : "WRLD", space->form->GetFormID());
    state.waiting = false;
    if (observed == Capture::Context::Observation::Changed)
    {
      logger::info("Phantom coordinate space changed to {} {:08X}", space->interior ? "CELL" : "WRLD", space->form->GetFormID());
      Clear("space-changed");
      state.context.Observe(Capture::Context::Space{space->form->GetFormID(), space->interior});
    }
#ifdef DREAMSLEEVE_DIAGNOSTICS
    if (Dreamsleeve::Game::PhantomReplay::Active())
    {
      Dreamsleeve::Game::PhantomReplay::Tick(*parent, {space->form->GetFormID(), space->interior}, {settings.color, settings.opacity}, now);
      return;
    }
#endif
    CapturePlayer(*player);
    auto display = exchange.Read();
#ifdef DREAMSLEEVE_DIAGNOSTICS
    if (now >= state.nextNetworkReport)
    {
      state.nextNetworkReport = now + std::chrono::seconds(5);
      for (const auto& remote : display.remotes)
      {
        const auto timing    = remote.playback.Inspect(Micros(now), settings);
        const auto visual    = state.visuals.find(remote.player);
        const auto displayed = visual != state.visuals.end() && visual->second.current ? visual->second.current->generation.value : 0;
        logger::info(
          "[Phantom network] player={} generation={} displayed={} asset={} samples={} seq={} source_gap_ms={:.1f} arrival_gap_ms={:.1f} age_ms={:.1f} ahead_ms={:.1f} target_delay_ms={:.1f} speed={:.3f}",
          remote.player,
          remote.descriptor.generation.value,
          displayed,
          static_cast<int>(remote.State()),
          timing.samples,
          timing.sequence,
          timing.sourceGapUs / 1000.0,
          timing.arrivalGapUs / 1000.0,
          timing.ageUs / 1000.0,
          timing.aheadUs / 1000.0,
          timing.delayUs / 1000.0,
          timing.speed);
      }
    }
#endif
    if (!display.available)
    {
#ifdef DREAMSLEEVE_DIAGNOSTICS
      if (Dreamsleeve::Client::Diagnostics::Phantoms().Active())
      {
        for (const auto& [id, visual] : state.visuals)
          exchange.SceneMemory(id, 0);
        state.visuals.clear();
        return;
      }
#endif
      ResetResources();
      return;
    }
    if (!settings.receive)
    {
      for (const auto& [id, visual] : state.visuals)
        exchange.SceneMemory(id, 0);
      state.visuals.clear();
      return;
    }
    const auto observer = World::Observe(player);
    if (!observer) return;
    const auto&                          ui = runtime.ui.ui.chat;
    std::unordered_set<Domain::PlayerId> retained;
    std::ranges::sort(display.remotes, {}, &P::Remote::player);
    const auto first = std::ranges::upper_bound(display.remotes, state.cursor, {}, &P::Remote::player);
    std::rotate(display.remotes.begin(), first, display.remotes.end());
    std::uint32_t      buildSteps = 1;
    Scene::FrameBudget frame{65536};
    for (const auto& remote : display.remotes)
    {
      const auto online   = runtime.session.OnlinePlayers().find(remote.player);
      const auto movement = runtime.movement ? runtime.movement->Sample(remote.player, now) : std::nullopt;
      if (
        online == runtime.session.OnlinePlayers().end() || !movement ||
        runtime.session.HidesPlayerRepresentation(remote.player, ui.fireflyGuildmatesOnly) ||
        !Domain::Spatial::Reach(observer->space, observer->position, movement->location.locationId, movement->position, settings.distance))
        continue;
      retained.insert(remote.player);
      auto&      visual   = state.visuals[remote.player];
      const auto revision = std::pair{remote.view, remote.descriptor.generation};
      if (visual.rejected && *visual.rejected != revision) visual.rejected.reset();
      ForgetCleared(visual, exchange, remote.player);
      if (remote.previous && visual.current && visual.current->generation == remote.previous->descriptor.generation)
      {
        if (auto previousPose = remote.previous->playback.At(Micros(now), settings))
        {
          const Scene::Context c{
              previousPose->context,
              {space->form->GetFormID(), space->interior}
          };
          if (auto applied = visual.current->scene->Apply(*previousPose, c, frame); applied)
          {
            visual.applied = now;
            visual.active  = true;
          }
        }
      }
      auto pose = remote.playback.At(Micros(now), settings);
      if (remote.Asset() && pose)
      {
        const Scene::Context context{
            pose->context,
            {space->form->GetFormID(), space->interior}
        };
        if (visual.current && visual.current->context != context)
        {
          visual.current.reset();
          visual.active = false;
          visual.look.reset();
        }
        if (visual.candidate && !Matches(*visual.candidate, remote, context)) visual.candidate.reset();
        exchange.SceneMemory(remote.player, visual.MemoryBytes());
        if ((!visual.current || !Matches(*visual.current, remote, context)) && !visual.candidate && !visual.rejected && buildSteps)
        {
          auto scene = Scene::Scene::Begin(
            *remote.Asset(),
            state.engine,
            context,
            remote.descriptor.generation,
            {settings.color, settings.opacity},
            Scene::Budget{exchange.RemainingMemory()});
          if (scene && exchange.SceneMemory(remote.player, visual.MemoryBytes() + (*scene)->MemoryBytes()))
            visual.candidate = Slot{remote.view, remote.descriptor.generation, context, std::move(*scene)};
          else if (!scene)
            Error(scene.error(), now);
        }
        const auto failedTarget = [&](const P::Error& error) {
          Error(error, now);
          if (visual.candidate)
          {
            visual.rejected = revision;
            visual.candidate.reset();
            exchange.SceneMemory(remote.player, visual.MemoryBytes());
          }
          else
            Hide(visual, now);
        };
        Slot* target = visual.candidate ? &*visual.candidate
                                        : (visual.current && Matches(*visual.current, remote, context) ? &*visual.current : nullptr);
        if (target)
        {
          if (visual.candidate && buildSteps)
          {
            auto built = target->scene->Advance(context);
            --buildSteps;
            state.cursor = remote.player;
            if (!built)
            {
              Error(built.error(), now);
              // A rejected native asset must not invoke NiStream Load every frame.
              // A new view/generation or context reset permits another attempt.
              if (built.error().reason != P::Failure::Busy) visual.rejected = revision;
              visual.candidate.reset();
              exchange.SceneMemory(remote.player, visual.MemoryBytes());
              target = nullptr;
            }
          }
          if (target && !(settings.hideInCombat && player->IsInCombat()))
          {
            auto applied = target->scene->Apply(*pose, context, frame);
            if (applied)
            {
              auto attached = target->scene->Attach(*parent, context);
              if (attached)
              {
                if (visual.candidate)
                {
                  visual.current = std::move(visual.candidate);
                  exchange.Displayed({remote.player, remote.view, remote.descriptor.generation});
                  logger::info(
                    "[Phantom] native scene displayed: player={} generation={} bytes={}",
                    remote.player,
                    remote.descriptor.generation.value,
                    visual.current->scene->MemoryBytes());
                  visual.candidate.reset();
                  visual.look.reset();
                  exchange.SceneMemory(remote.player, visual.MemoryBytes());
                }
                visual.applied = now;
                visual.active  = true;
                state.cursor   = remote.player;
              }
              else
              {
                failedTarget(attached.error());
              }
            }
            else if (applied.error().reason != P::Failure::Busy)
            {
              failedTarget(applied.error());
            }
          }
        }
      }
      ForgetCleared(visual, exchange, remote.player);
      if (now - visual.applied > std::chrono::milliseconds(settings.timeoutMs))
      {
        Hide(visual, now);
        ForgetCleared(visual, exchange, remote.player);
        continue;
      }
      if (settings.hideInCombat && player->IsInCombat())
      {
        Hide(visual, now);
        ForgetCleared(visual, exchange, remote.player);
        visual.active = visual.current && visual.current->scene->Ready();
        continue;
      }
      if (!visual.active || !visual.current) continue;
      const auto look = std::pair{settings.color, settings.opacity};
      if (visual.look != look)
      {
        auto changed = visual.current->scene->SetLook({settings.color, settings.opacity}, visual.current->context);
        if (!changed)
        {
          Error(changed.error(), now);
          Hide(visual, now);
          ForgetCleared(visual, exchange, remote.player);
          continue;
        }
        visual.look = look;
      }
      const auto         bound = visual.current->scene->BodyBound();
      const RE::NiPoint3 anchor{bound.center.x, bound.center.y, bound.center.z + bound.radius};
      PlayerLabels::Add(names, remote.player, anchor, online->second, now);
    }
    std::erase_if(state.visuals, [&](const auto& entry) {
      if (retained.contains(entry.first)) return false;
      exchange.SceneMemory(entry.first, 0);
      return true;
    });
  }

}
