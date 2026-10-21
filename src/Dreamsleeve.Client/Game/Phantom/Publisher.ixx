module;
#include "Prelude.hpp"
export module Dreamsleeve.Game.Phantom.Publisher;
import std;
import Dreamsleeve.Game.World;
import Dreamsleeve.Game.Phantom.Capture;
import Dreamsleeve.Game.Phantom.Log;
import Dreamsleeve.Client.Phantom.Exchange;
import Dreamsleeve.Client.Diagnostics.PhantomTrace;
#ifdef DREAMSLEEVE_DIAGNOSTICS
import Dreamsleeve.Client.Diagnostics.PhantomRecorder;
#endif

// The own phantom on the game thread: captures the player's model once per
// appearance and a pose at the sampling rate, and submits both to the
// exchange. A replaced generation keeps sampling as the bridge until the
// server settles the new one.
export namespace Dreamsleeve::Game::PhantomPublisher
{
  namespace P       = Dreamsleeve::Client::Phantom;
  namespace Capture = Dreamsleeve::Game::PhantomCapture;
  namespace Trace   = Dreamsleeve::Client::Diagnostics::Trace;
  using Clock       = PhantomLog::Clock;
  using PhantomLog::Micros;

  struct RetainedSource
  {
    P::Generation                    generation;
    P::Sequence                      sequence;
    std::unique_ptr<Capture::Source> source;
  };

  struct State
  {
    std::unique_ptr<Capture::Source> source;
    std::optional<RetainedSource>    previous;
    P::Generation                    generation;
    // Allocation high-watermark never rolls back with the retained native Source.
    std::uint64_t    lastGeneration{};
    P::Sequence      sequence;
    Capture::Cadence cadence;
    std::uint32_t    omittedGeometry{}, hiddenGeometry{};
#ifdef DREAMSLEEVE_DIAGNOSTICS
    std::shared_ptr<const P::ValidatedAsset> diagnosticAsset;
    bool                                     publishing{};
#endif
  };

  State& Get()
  {
    static auto* state = new State;
    return *state;
  }

  // What one capture turn knows.
  struct Turn
  {
    RE::PlayerCharacter&         player;
    P::Exchange&                 exchange;
    const Capture::Engine&       engine;
    Clock::time_point            now;
    bool                         firstPerson{};
    bool                         publishing{};
    std::optional<std::uint64_t> context;
    P::Limits                    limits;
  };

  bool Capturing()
  {
    return Get().source != nullptr;
  }

  // The native source goes; a source the exchange still publishes restarts there.
  void Reset(P::Exchange* exchange)
  {
    auto& state = Get();
    if (exchange && state.source) exchange->RestartCapture();
    state.source.reset();
    state.previous.reset();
#ifdef DREAMSLEEVE_DIAGNOSTICS
    state.diagnosticAsset.reset();
#endif
  }

#ifdef DREAMSLEEVE_DIAGNOSTICS
  // A recording captures from scratch, outside publication.
  void StartRecording()
  {
    auto& state = Get();
    state.source.reset();
    state.previous.reset();
    state.diagnosticAsset.reset();
    state.cadence.Reset();
  }

  void Record(std::shared_ptr<const P::Snapshot> pose, const Turn& turn)
  {
    auto& recorder = Dreamsleeve::Client::Diagnostics::Phantoms();
    if (!recorder.Active()) return;
    const auto position = turn.player.GetPosition(), angle = turn.player.GetAngle();
    recorder.Sample(
      Get().diagnosticAsset,
      std::move(pose),
      {
          0,
          0,
          Micros(turn.now),
          {position.x, position.y, position.z},
          {angle.x,    angle.y,    angle.z   }
    },
      std::chrono::duration<double, std::milli>(Clock::now() - turn.now).count(),
      turn.firstPerson);
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

  void Failed(const P::Error& error, Clock::time_point now)
  {
#ifdef DREAMSLEEVE_DIAGNOSTICS
    Dreamsleeve::Client::Diagnostics::Phantoms().Failed(error);
#endif
    PhantomLog::Warn(error, now);
  }

  // The retained generation's pose at the same instant, so the bridge keeps
  // moving while the new generation is prepared.
  std::shared_ptr<const P::Snapshot> PriorPose(const Turn& turn)
  {
    auto& state = Get();
    if (!turn.publishing || !state.previous) return {};
    auto& old = *state.previous;
    if (old.sequence.value == std::numeric_limits<std::uint64_t>::max()) return {};
    auto pose = old.source->Sample(turn.player, turn.firstPerson, {old.generation, {++old.sequence.value}, 1, Micros(turn.now)}, true);
    return pose ? std::make_shared<const P::Snapshot>(std::move(*pose)) : nullptr;
  }

  // A new generation from the player's current appearance; the old source is
  // retained while the exchange still publishes it.
  bool Replace(const Turn& turn, std::string_view reason)
  {
    auto& state = Get();
    if (state.lastGeneration == std::numeric_limits<std::uint64_t>::max()) return false;
    const P::Generation nextGeneration{state.lastGeneration + 1};
    auto opened = Capture::Open(turn.engine, turn.player, turn.firstPerson, {nextGeneration, {1}, 1, Micros(turn.now)}, turn.limits);
    if (!opened)
    {
      Failed(opened.error(), turn.now);
      return false;
    }

    auto asset = std::move(opened->asset);
    Trace::Event("captured", [&] {
      return std::format(
        "\"generation\":{},\"reason\":\"{}\",\"raw_bytes\":{},\"channels\":{},\"elapsed_ms\":{}",
        nextGeneration.value,
        reason,
        asset.Value().nif.size(),
        asset.Layout().requiredChannels.size(),
        std::chrono::duration<double, std::milli>(Clock::now() - turn.now).count());
    });
    logger::info(
      "Phantom capture: generation {}, {} channels, {} geometry, {:.2f} MiB native NIF, {:.2f} ms, reason={}",
      nextGeneration.value,
      asset.Layout().requiredChannels.size(),
      asset.Layout().bounds.size(),
      asset.Value().nif.size() / 1048576.0,
      std::chrono::duration<double, std::milli>(Clock::now() - turn.now).count(),
      reason);

    if (turn.publishing && !turn.exchange.Submit(*turn.context, nextGeneration, asset)) return false;
    if (turn.publishing && state.source && turn.exchange.Capturing(state.generation))
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
    Record(initial, turn);
#endif
    if (turn.publishing) turn.exchange.Submit(std::move(initial), PriorPose(turn));
    return true;
  }

  // One pose of the current generation; a stale source is rebuilt next turn.
  void Sample(const Turn& turn)
  {
    auto& state = Get();
    if (turn.publishing && !turn.exchange.PosesRequired()) return;
    if (state.sequence.value == std::numeric_limits<std::uint64_t>::max())
    {
      state.source.reset();
      return;
    }
    const auto poseStart = Clock::now();
    auto       pose      = state.source->Sample(
      turn.player,
      turn.firstPerson,
      {state.generation, P::Sequence{++state.sequence.value}, 1, Micros(turn.now)},
      turn.publishing);
    Trace::Observe(Trace::Metric::CapturePose, std::chrono::duration<double, std::milli>(Clock::now() - poseStart).count());
    if (!pose)
    {
      Failed(pose.error(), turn.now);
      if (pose.error().reason == P::Failure::Stale)
        state.source.reset();
      else
        state.cadence.Defer(turn.now);
      return;
    }

    ReportCaptureHealth();
    auto sampled = std::make_shared<const P::Snapshot>(std::move(*pose));
#ifdef DREAMSLEEVE_DIAGNOSTICS
    Record(sampled, turn);
#endif
    if (turn.publishing) turn.exchange.Submit(std::move(sampled), PriorPose(turn));
  }

  // Tick owns capture and playback on the same main-loop thread, after the
  // game's frame update. No actor-update callback touches this state.
  void Tick(RE::PlayerCharacter& player, P::Exchange& exchange, const Capture::Engine& engine)
  {
    auto&      state    = Get();
    const auto now      = Clock::now();
    const auto settings = exchange.Settings();
    const auto observer = World::Observe(&player);
    Turn       turn{player, exchange, engine, now};
    turn.context    = observer ? exchange.CaptureContext(observer->space) : std::nullopt;
    turn.publishing = turn.context.has_value() && settings.publish;
#ifdef DREAMSLEEVE_DIAGNOSTICS
    const bool recording = Dreamsleeve::Client::Diagnostics::Phantoms().Active();
    if (recording)
    {
      turn.publishing = false;
      turn.limits     = Dreamsleeve::Client::Diagnostics::CaptureLimits();
    }
    if (state.source && state.publishing != turn.publishing)
    {
      state.source.reset();
      state.previous.reset();
    }
    state.publishing = turn.publishing;
    if (!turn.publishing && !recording)
#else
    if (!turn.publishing)
#endif
    {
      state.source.reset();
      state.previous.reset();
#ifdef DREAMSLEEVE_DIAGNOSTICS
      state.diagnosticAsset.reset();
#endif
      return;
    }

    // The exchange dropped this generation: fall back to the retained one or start over.
    if (state.source && turn.publishing && !exchange.Capturing(state.generation))
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

    if (!state.source && turn.publishing && exchange.Capturing(state.generation)) exchange.RestartCapture();
    if (!state.cadence.Due(now, settings.sampleRate)) return;

    const auto* camera = RE::PlayerCamera::GetSingleton();
    turn.firstPerson   = camera && camera->IsInFirstPerson();
    if (state.previous && !exchange.Capturing(state.previous->generation)) state.previous.reset();

    if (!state.source || ((!turn.publishing || exchange.CanReplace()) && state.source->RebuildDue(Micros(now))))
    {
      const auto reason = state.source ? state.source->ChangeReason() : std::string_view{"initial"};
      if (state.source) state.source->DeferRebuild(Micros(now));
      if (Replace(turn, reason)) return;
      if (!state.source)
      {
        state.cadence.Defer(now);
        return;
      }
    }
    Sample(turn);
  }

}
