module;
#include "Prelude.hpp"
export module Dreamsleeve.Game.Phantom.Display;
import std;
import Dreamsleeve.Runtime;
import Dreamsleeve.Game.World;
import Dreamsleeve.Game.Phantom.Capture;
import Dreamsleeve.Game.Phantom.Scene;
import Dreamsleeve.Game.Phantom.Log;
import Dreamsleeve.Game.PlayerLabels;
import Dreamsleeve.UI.Nameplates;
import Dreamsleeve.Client.Phantom.Exchange;
import Dreamsleeve.Client.Diagnostics.PhantomTrace;

// Remote phantoms on the game thread: per visible player a current native
// scene and at most one candidate being built, posed from the remote's
// playback, recoloured and labelled. Building is budgeted per frame.
export namespace Dreamsleeve::Game::PhantomDisplay
{
  namespace P       = Dreamsleeve::Client::Phantom;
  namespace Capture = Dreamsleeve::Game::PhantomCapture;
  namespace Scene   = Dreamsleeve::Game::PhantomScene;
  namespace Trace   = Dreamsleeve::Client::Diagnostics::Trace;
  using Clock       = PhantomLog::Clock;
  using PhantomLog::Micros;

  // Native scenes built per frame, and pose channels applied per frame.
  constexpr std::uint32_t BuildStepsPerFrame = 1;
  constexpr std::uint64_t ChannelsPerFrame   = 65536;

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
    // This frame's playback has a pose for the remote's model.
    bool posed{};
    // The last scene started for the remote did not fit the memory budget.
    bool outOfMemory{};

    std::uint64_t MemoryBytes() const
    {
      return (current ? current->scene->MemoryBytes() : 0) + (candidate ? candidate->scene->MemoryBytes() : 0);
    }
  };

  struct State
  {
    std::unordered_map<Domain::PlayerId, Visual> visuals;
    // The player built or shown last; the next frame starts after them.
    std::uint64_t cursor{};
    // Offered players of this frame and what to show about their phantoms;
    // Show fills it, and until it runs nothing is known about anybody.
    std::unordered_map<Domain::PlayerId, std::optional<Nameplates::PhantomStatus>> statuses;
    bool                                                                           reported{};
  };

  State& Get()
  {
    static auto* state = new State;
    return *state;
  }

  // What one frame of display knows and spends.
  struct Pass
  {
    P::Exchange&           exchange;
    const P::ViewSettings& settings;
    const Capture::Engine& engine;
    RE::PlayerCharacter&   player;
    RE::NiNode&            parent;
    PhantomSpace           space;
    Nameplates::Frame&     names;
    Clock::time_point      now;
    std::uint32_t          buildSteps{BuildStepsPerFrame};
    Scene::FrameBudget     frame{ChannelsPerFrame};
  };

  bool Shown(Domain::PlayerId id)
  {
    const auto& visuals = Get().visuals;
    const auto  found   = visuals.find(id);
    return found != visuals.end() && found->second.active;
  }

  // A player the frame did not reach has no phantom coming.
  std::optional<Nameplates::PhantomStatus> Status(Domain::PlayerId id)
  {
    const auto& state = Get();
    if (!state.reported) return std::nullopt;
    const auto found = state.statuses.find(id);
    return found == state.statuses.end() ? Nameplates::PhantomStatus{Nameplates::PhantomMark::NotComing} : found->second;
  }

  std::size_t Count()
  {
    return std::ranges::count_if(Get().visuals, [](const auto& entry) { return entry.second.active; });
  }

  bool Empty()
  {
    return Get().visuals.empty();
  }

  // The generation on screen for a player, zero for none.
  std::uint64_t DisplayedGeneration(Domain::PlayerId id)
  {
    const auto& visuals = Get().visuals;
    const auto  found   = visuals.find(id);
    return found != visuals.end() && found->second.current ? found->second.current->generation.value : 0;
  }

  void BeginFrame()
  {
    auto& state = Get();
    state.statuses.clear();
    state.reported = false;
  }

  // Scene destruction detaches each root on this game thread.
  void Reset(P::Exchange* exchange)
  {
    auto& visuals = Get().visuals;
    if (exchange)
      for (const auto& [id, visual] : visuals)
        exchange->SceneMemory(id, 0);
    visuals.clear();
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
      if (!hidden) PhantomLog::Warn(hidden.error(), now);
    }
    visual.active = false;
  }

  // Scenes are kept loaded and reappear when shown again.
  void HideAll(Clock::time_point now)
  {
    for (auto& [id, visual] : Get().visuals)
      Hide(visual, now);
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

  // Commit an already posed and attached candidate on the game thread.
  // The current scene stays owned until this accepted replacement is promoted.
  void PromoteRemoteScene(Visual& visual, const P::Remote& remote, P::Exchange& exchange)
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

  // Online, moving, not hidden by privacy or guild filters and within reach.
  bool Visible(const P::Remote& remote, const World::Observer& observer, const Pass& pass)
  {
    auto&       runtime  = Runtime::Get();
    const auto& ui       = runtime.ui.ui.chat;
    const auto  online   = runtime.session.OnlinePlayers().find(remote.player);
    const auto  movement = runtime.movement ? runtime.movement->Sample(remote.player, pass.now) : std::nullopt;
    return online != runtime.session.OnlinePlayers().end() && movement &&
           !runtime.session.HidesPlayerRepresentation(remote.player, ui.fireflyGuildmatesOnly) &&
           Domain::Spatial::Reach(
             observer.space,
             observer.position,
             movement->location.locationId,
             movement->position,
             pass.settings.distance);
  }

  // While the next generation builds, the shown one follows its own poses.
  void Bridge(Visual& visual, const P::Remote& remote, Pass& pass)
  {
    if (!remote.previous || !visual.current || visual.current->generation != remote.previous->descriptor.generation) return;
    const auto previousPose = remote.previous->playback.At(Micros(pass.now), pass.settings);
    if (!previousPose) return;
    const Scene::Context context{previousPose->context, pass.space};
    if (auto applied = visual.current->scene->Apply(*previousPose, context, pass.frame); applied)
    {
      visual.applied = pass.now;
      visual.active  = true;
    }
  }

  // A scene for the remote's model and pose: the current one, or a candidate
  // built within the frame budget, posed, attached and then promoted.
  void Build(Visual& visual, const P::Remote& remote, const P::Snapshot& pose, const P::Vec3& color, Pass& pass)
  {
    auto&                exchange = pass.exchange;
    const auto           revision = std::pair{remote.view, remote.descriptor.generation};
    const Scene::Context context{pose.context, pass.space};
    if (visual.current && visual.current->context != context)
    {
      visual.current.reset();
      visual.active = false;
      visual.look.reset();
    }
    if (visual.candidate && !Matches(*visual.candidate, remote, context)) visual.candidate.reset();
    exchange.SceneMemory(remote.player, visual.MemoryBytes());
    if ((!visual.current || !Matches(*visual.current, remote, context)) && !visual.candidate && !visual.rejected && pass.buildSteps)
    {
      visual.outOfMemory = false;
      auto scene         = Scene::Scene::Begin(
        *remote.Asset(),
        pass.engine,
        context,
        remote.descriptor.generation,
        {color, pass.settings.opacity},
        Scene::Budget{exchange.RemainingMemory()});
      if (scene && exchange.SceneMemory(remote.player, visual.MemoryBytes() + (*scene)->MemoryBytes()))
        visual.candidate = Slot{remote.view, remote.descriptor.generation, context, std::move(*scene)};
      else if (scene)
        visual.outOfMemory = true;
      else
      {
        PhantomLog::Warn(scene.error(), pass.now);
        visual.outOfMemory = scene.error().reason == P::Failure::LimitExceeded;
      }
    }

    // A refused candidate is not retried for this revision; a refused current scene hides.
    const auto failedTarget = [&](const P::Error& error) {
      PhantomLog::Warn(error, pass.now);
      if (visual.candidate)
      {
        visual.rejected = revision;
        visual.candidate.reset();
        exchange.SceneMemory(remote.player, visual.MemoryBytes());
      }
      else
        Hide(visual, pass.now);
    };

    Slot* target = visual.candidate                                            ? &*visual.candidate
                 : visual.current && Matches(*visual.current, remote, context) ? &*visual.current
                                                                               : nullptr;
    if (!target) return;
    if (visual.candidate && pass.buildSteps)
    {
      auto built = target->scene->Advance(context);
      --pass.buildSteps;
      Get().cursor = remote.player;
      if (!built)
      {
        PhantomLog::Warn(built.error(), pass.now);
        // A rejected native asset must not invoke NiStream Load every frame.
        // A new view/generation or context reset permits another attempt.
        if (built.error().reason != P::Failure::Busy) visual.rejected = revision;
        visual.candidate.reset();
        exchange.SceneMemory(remote.player, visual.MemoryBytes());
        return;
      }
    }

    if (pass.settings.hideInCombat && pass.player.IsInCombat()) return;
    auto applied = target->scene->Apply(pose, context, pass.frame);
    if (!applied)
    {
      if (applied.error().reason != P::Failure::Busy) failedTarget(applied.error());
      return;
    }
    auto attached = target->scene->Attach(pass.parent, context);
    if (!attached)
    {
      failedTarget(attached.error());
      return;
    }
    if (visual.candidate)
    {
      Trace::Event("displayed", [&] {
        return std::format(
          "\"player\":{},\"view\":{},\"generation\":{},\"context\":{},\"space\":{}",
          remote.player,
          remote.view,
          remote.descriptor.generation.value,
          context.epoch,
          context.space.id);
      });
      PromoteRemoteScene(visual, remote, exchange);
    }
    visual.applied = pass.now;
    visual.active  = true;
    Get().cursor   = remote.player;
  }

  // A shown phantom without fresh poses or in combat hides; false when it is not shown.
  bool Settle(Visual& visual, const P::Remote& remote, Pass& pass)
  {
    if (pass.now - visual.applied > std::chrono::milliseconds(pass.settings.timeoutMs))
    {
      Hide(visual, pass.now);
      ForgetCleared(visual, pass.exchange, remote.player);
      return false;
    }
    if (pass.settings.hideInCombat && pass.player.IsInCombat())
    {
      Hide(visual, pass.now);
      ForgetCleared(visual, pass.exchange, remote.player);
      visual.active = visual.current && visual.current->scene->Ready();
      return false;
    }
    return visual.active && visual.current;
  }

  // A guild joined or left recolours the phantom like a changed setting.
  bool Recolor(Visual& visual, const P::Remote& remote, const P::Vec3& color, Pass& pass)
  {
    const auto look = std::pair{color, pass.settings.opacity};
    if (visual.look == look) return true;
    auto changed = visual.current->scene->SetLook({color, pass.settings.opacity}, visual.current->context);
    if (!changed)
    {
      PhantomLog::Warn(changed.error(), pass.now);
      Hide(visual, pass.now);
      ForgetCleared(visual, pass.exchange, remote.player);
      return false;
    }
    visual.look = look;
    return true;
  }

  // What to show about a remote's phantom. A shown one speaks only of failures
  // of its next model: replacing it in peace needs no mark.
  std::optional<Nameplates::PhantomStatus> StatusOf(const P::Remote& remote, const Visual& visual, const Pass& pass)
  {
    using Mark          = Nameplates::PhantomMark;
    using Status        = Nameplates::PhantomStatus;
    const auto progress = [&](Status status) {
      return visual.active ? std::nullopt : std::optional{status};
    };
    switch (remote.Stage())
    {
      case P::ModelStage::Queued:
        return progress({Mark::Queued});
      case P::ModelStage::WaitingMemory:
        return progress({Mark::WaitingMemory});
      case P::ModelStage::Receiving:
        return progress({Mark::Receiving, remote.Downloaded().value_or(0.0f)});
      case P::ModelStage::Checking:
        return progress({Mark::Checking});
      case P::ModelStage::Retrying:
        return progress({Mark::Retrying});
      case P::ModelStage::Refused:
        return Status{Mark::Refused};
      case P::ModelStage::TransferFailed:
        return Status{Mark::TransferFailed};
      case P::ModelStage::InvalidModel:
        return Status{Mark::InvalidModel};
      case P::ModelStage::Ready:
        break;
    }

    if (visual.rejected) return Status{Mark::SceneFailed};
    // Combat hides phantoms on purpose, whatever their scenes are doing.
    if (visual.active || (pass.settings.hideInCombat && pass.player.IsInCombat())) return std::nullopt;
    if (visual.outOfMemory) return Status{Mark::WaitingMemory};
    if (visual.candidate) return Status{Mark::Building};
    return Status{visual.posed ? Mark::Building : Mark::WaitingPose};
  }

  void ShowRemote(const P::Remote& remote, Pass& pass)
  {
    auto&       runtime  = Runtime::Get();
    const auto& color    = runtime.session.Guildmate(remote.player) ? pass.settings.guildColor : pass.settings.color;
    auto&       visual   = Get().visuals[remote.player];
    const auto  revision = std::pair{remote.view, remote.descriptor.generation};
    if (visual.rejected && *visual.rejected != revision) visual.rejected.reset();
    ForgetCleared(visual, pass.exchange, remote.player);
    Bridge(visual, remote, pass);

    const auto pose = remote.playback.At(Micros(pass.now), pass.settings);
    visual.posed    = pose.has_value();
    if (remote.Asset() && pose) Build(visual, remote, *pose, color, pass);
    ForgetCleared(visual, pass.exchange, remote.player);

    const bool shown  = Settle(visual, remote, pass) && Recolor(visual, remote, color, pass);
    const auto status = StatusOf(remote, visual, pass);
    Get().statuses.insert_or_assign(remote.player, status);
    if (!shown) return;
    const auto online = runtime.session.OnlinePlayers().find(remote.player);
    PlayerLabels::Add(pass.names, remote.player, visual.current->scene->LabelAnchor(), online->second, pass.now, status);
  }

  // Every remote in turn, starting after the one served last so a slow build
  // does not starve the others; visuals of players out of view go.
  void Show(std::vector<P::Remote> remotes, const World::Observer& observer, Pass& pass)
  {
    auto&                                state = Get();
    std::unordered_set<Domain::PlayerId> retained;
    std::ranges::sort(remotes, {}, &P::Remote::player);
    const auto first = std::ranges::upper_bound(remotes, state.cursor, {}, &P::Remote::player);
    std::rotate(remotes.begin(), first, remotes.end());

    for (const auto& remote : remotes)
    {
      if (!Visible(remote, observer, pass)) continue;
      retained.insert(remote.player);
      ShowRemote(remote, pass);
    }

    state.reported = true;
    std::erase_if(state.visuals, [&](const auto& entry) {
      if (retained.contains(entry.first)) return false;
      Trace::Event("scene_removed", [&] { return std::format("\"player\":{},\"reason\":\"not-retained\"", entry.first); });
      pass.exchange.SceneMemory(entry.first, 0);
      return true;
    });
  }

}
