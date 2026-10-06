module;
#include "Prelude.hpp"
export module Dreamsleeve.Game.Phantoms;
import std;
import Dreamsleeve.Runtime;
import Dreamsleeve.Game.World;
import Dreamsleeve.Game.PhantomCapture;
import Dreamsleeve.Game.PhantomScene;
import Dreamsleeve.Game.PhantomGraphics;
import Dreamsleeve.Game.PlayerLabels;
import Dreamsleeve.UI.Nameplates;
import Dreamsleeve.Host.PhantomSettings;
import Dreamsleeve.Client.Phantom.Exchange;

namespace Phantoms
{
  namespace P        = Dreamsleeve::Client::Phantom;
  namespace Capture  = Dreamsleeve::Game::PhantomCapture;
  namespace Scene    = Dreamsleeve::Game::PhantomScene;
  namespace Graphics = Dreamsleeve::Game::PhantomGraphics;
  using Clock        = std::chrono::steady_clock;

  struct Slot
  {
    std::uint64_t                 view{};
    P::Generation                 generation;
    Scene::Context                context;
    std::unique_ptr<Scene::Scene> scene;
  };

  struct Visual
  {
    std::optional<Slot>                      current, candidate;
    Clock::time_point                        applied{};
    bool                                     active{};
    std::optional<std::pair<P::Vec3, float>> look;

    std::uint64_t MemoryBytes() const
    {
      return (current ? current->scene->MemoryBytes() : 0) + (candidate ? candidate->scene->MemoryBytes() : 0);
    }
  };

  struct State
  {
    Capture::Engine                              capture;
    Scene::Engine                                renderer;
    std::unique_ptr<Capture::Source>             source;
    std::unordered_map<Domain::PlayerId, Visual> visuals;
    P::Generation                                generation;
    P::Sequence                                  sequence;
    std::optional<P::ViewSettings>               settings;
    Clock::time_point                            nextCapture{}, nextFailure{};
    std::uint32_t                                cell{}, world{};
    std::uint64_t                                cursor{};
    bool                                         wasAvailable{};
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

  export void Install(Capture::Engine capture, Scene::Engine renderer)
  {
    Get().capture  = capture;
    Get().renderer = renderer;
  }

  export void Clear()
  {
    auto& state   = Get();
    auto& runtime = Runtime::Get();
    if (runtime.app)
      for (const auto& [id, visual] : state.visuals)
        runtime.app->Exchange().Phantoms().SceneMemory(id, 0);
    state.visuals.clear();
    state.source.reset();
    state.cell = state.world = 0;
    state.wasAvailable       = false;
    if (state.capture.mainThread && state.capture.mainThread())
    {
      auto cleared = Graphics::ClearReadbacks();
      if (!cleared) Error(cleared.error(), Clock::now());
    }
  }

  export void CapturePlayer(RE::PlayerCharacter& player)
  {
    auto&      runtime = Runtime::Get();
    auto&      state   = Get();
    const auto now     = Clock::now();
    if (!runtime.app || runtime.shutdown || runtime.context != Runtime::GameContext::Playing || !World::PlayerReady()) return;
    auto&      exchange = runtime.app->Exchange().Phantoms();
    const auto settings = exchange.Settings();
    if (!exchange.Available() || !settings.publish)
    {
      state.source.reset();
      return;
    }
    if (state.source && !exchange.Capturing(state.generation))
    {
      state.source.reset();
      state.nextCapture = now + std::chrono::seconds(1);
      return;
    }
    if (now < state.nextCapture) return;
    state.nextCapture       = now + std::chrono::microseconds(1000000 / std::max(settings.sampleRate, 1u));
    const auto* camera      = RE::PlayerCamera::GetSingleton();
    const bool  firstPerson = camera && camera->IsInFirstPerson();
    if (!state.source)
    {
      if (state.generation.value == std::numeric_limits<std::uint64_t>::max()) return;
      ++state.generation.value;
      state.sequence = {1};
      auto opened    = Capture::Open(state.capture, player, firstPerson, {state.generation, state.sequence, 1, Micros(now)});
      if (!opened)
      {
        Error(opened.error(), now);
        if (opened.error().reason != P::Failure::Busy) state.nextCapture = now + std::chrono::seconds(1);
        return;
      }
      auto asset = P::ValidatedAsset::Parse(std::move(opened->asset));
      if (!asset)
      {
        Error(asset.error(), now);
        state.nextCapture = now + std::chrono::seconds(1);
        return;
      }
      logger::info(
        "Phantom capture: generation {}, {} channels, {} geometry, {:.2f} MiB neutral, {:.2f} ms",
        state.generation.value,
        asset->Value().nodes.size(),
        asset->Value().geometry.size(),
        asset->MemoryBytes() / 1048576.0,
        std::chrono::duration<double, std::milli>(Clock::now() - now).count());
      if (!exchange.Submit(state.generation, std::move(*asset)))
      {
        state.nextCapture = now + std::chrono::seconds(1);
        return;
      }
      state.source = std::move(opened->source);
      exchange.Submit(std::make_shared<const P::Snapshot>(std::move(opened->initial)));
      return;
    }
    if (state.sequence.value == std::numeric_limits<std::uint64_t>::max())
    {
      state.source.reset();
      return;
    }
    auto pose = state.source->Sample(player, firstPerson, {state.generation, P::Sequence{++state.sequence.value}, 1, Micros(now)});
    if (!pose)
    {
      Error(pose.error(), now);
      if (pose.error().reason == P::Failure::Stale)
        state.source.reset();
      else
        state.nextCapture = now + std::chrono::seconds(1);
      return;
    }
    exchange.Submit(std::make_shared<const P::Snapshot>(std::move(*pose)));
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
    if (!runtime.app) return;
    auto&      exchange = runtime.app->Exchange().Phantoms();
    const auto settings = Dreamsleeve::Host::PhantomSettings(runtime.ui.ui.chat);
    if (!state.settings || settings != *state.settings)
    {
      if (state.settings && (settings.memoryBytes < state.settings->memoryBytes || settings.publish != state.settings->publish)) Clear();
      state.settings = settings;
      exchange.Configure(settings);
    }
    auto* player = RE::PlayerCharacter::GetSingleton();
    auto* cell   = player ? player->GetParentCell() : nullptr;
    auto* root   = player ? player->Get3D(false) : nullptr;
    auto* parent = root ? root->parent : nullptr;
    auto* world  = cell && cell->IsExteriorCell() ? player->GetWorldspace() : nullptr;
    if (runtime.context != Runtime::GameContext::Playing || !World::PlayerReady() || !cell || !cell->IsAttached() || !parent)
    {
      Clear();
      return;
    }
    const auto cellId = cell->GetFormID(), worldId = world ? world->GetFormID() : 0;
    if (state.cell != cellId || state.world != worldId)
    {
      Clear();
      state.cell  = cellId;
      state.world = worldId;
    }
    auto display = exchange.Read();
    if (!display.available)
    {
      Clear();
      return;
    }
    if (!state.wasAvailable)
    {
      state.source.reset();
      state.wasAvailable = true;
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
    Scene::FrameBudget frame{4000000};
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
      auto& visual = state.visuals[remote.player];
      ForgetCleared(visual, exchange, remote.player);
      auto pose = remote.playback.At(Micros(now), settings);
      if (remote.Asset() && pose)
      {
        const Scene::Context context{pose->context, cellId, worldId};
        if (visual.current && visual.current->context != context)
        {
          visual.current.reset();
          visual.active = false;
          visual.look.reset();
        }
        if (visual.candidate && !Matches(*visual.candidate, remote, context)) visual.candidate.reset();
        exchange.SceneMemory(remote.player, visual.MemoryBytes());
        if ((!visual.current || !Matches(*visual.current, remote, context)) && !visual.candidate && buildSteps)
        {
          auto scene = Scene::Scene::Begin(
            *remote.Asset(),
            state.renderer,
            context,
            remote.descriptor.generation,
            {settings.color, settings.opacity},
            Scene::Budget{exchange.RemainingMemory(), 4000000});
          if (scene && exchange.SceneMemory(remote.player, visual.MemoryBytes() + (*scene)->MemoryBytes()))
            visual.candidate = Slot{remote.view, remote.descriptor.generation, context, std::move(*scene)};
          else if (!scene)
            Error(scene.error(), now);
        }
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
                Error(attached.error(), now);
                Hide(visual, now);
              }
            }
            else if (applied.error().reason != P::Failure::Busy)
            {
              Error(applied.error(), now);
              Hide(visual, now);
            }
          }
        }
      }
      // Stale checks can destroy the tree, and a failed GPU upload can leave
      // it hidden/unposed even when Busy is returned. Neither represents a
      // visible phantom or may prevent the next frame from rebuilding it.
      ForgetCleared(visual, exchange, remote.player);
      if (settings.hideInCombat && player->IsInCombat())
      {
        Hide(visual, now);
        ForgetCleared(visual, exchange, remote.player);
        visual.active = visual.current && visual.current->scene->Ready();
        continue;
      }
      if (now - visual.applied > std::chrono::milliseconds(settings.timeoutMs))
      {
        Hide(visual, now);
        ForgetCleared(visual, exchange, remote.player);
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
