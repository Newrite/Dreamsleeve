module;
#include "Prelude.hpp"
export module Dreamsleeve.Game.Phantoms;
import std;
import Dreamsleeve.Runtime;
import Dreamsleeve.Game.World;
import Dreamsleeve.Game.Phantom.Capture;
import Dreamsleeve.Game.Phantom.Log;
import Dreamsleeve.Game.Phantom.Publisher;
import Dreamsleeve.Game.Phantom.Display;
import Dreamsleeve.UI.Nameplates;
import Dreamsleeve.Host.PhantomSettings;
import Dreamsleeve.Client.Phantom.Exchange;
import Dreamsleeve.Client.Diagnostics.PhantomTrace;
#ifdef DREAMSLEEVE_DIAGNOSTICS
import Dreamsleeve.Client.Diagnostics.PhantomRecorder;
#endif

// The phantoms' entry for the rest of the game: one Tick per frame after the
// game's update keeps the settings, follows the player's space and runs the
// own phantom (Publisher) and the remote ones (Display); fireflies ask here
// whether a phantom stands in for a player.
namespace Phantoms
{
  namespace P         = Dreamsleeve::Client::Phantom;
  namespace Capture   = Dreamsleeve::Game::PhantomCapture;
  namespace Publisher = Dreamsleeve::Game::PhantomPublisher;
  namespace Display   = Dreamsleeve::Game::PhantomDisplay;
  namespace Trace     = Dreamsleeve::Client::Diagnostics::Trace;
  using Clock         = Dreamsleeve::Game::PhantomLog::Clock;
  using Dreamsleeve::Game::PhantomLog::Micros;

  // Diagnostic builds log the exchange and each remote's playback this often.
  constexpr auto NetworkReportInterval = std::chrono::seconds(5);
  // Trace metrics are written this often.
  constexpr auto MetricsInterval = std::chrono::seconds(1);

  struct State
  {
    Capture::Engine                engine;
    std::optional<P::ViewSettings> settings;
    Capture::Context               context;
    bool                           waiting{};
    Clock::time_point              nextNetworkReport{};
  };

  State& Get()
  {
    static auto* state = new State;
    return *state;
  }

  P::Exchange* CurrentExchange()
  {
    auto& runtime = Runtime::Get();
    return runtime.app ? &runtime.app->Exchange().Phantoms() : nullptr;
  }

  export void Install(Capture::Engine engine)
  {
    Get().engine = engine;
  }

  void ResetResources()
  {
    auto* exchange = CurrentExchange();
    Publisher::Reset(exchange);
    Display::Reset(exchange);
  }

  export void Clear(std::string_view reason = "game-context-ended")
  {
    if (Publisher::Capturing() || !Display::Empty()) Trace::Event("clear", [&] { return std::format("\"reason\":\"{}\"", reason); });
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
    auto& runtime = Runtime::Get();
    if (!runtime.app || runtime.context != Runtime::GameContext::Playing) return;
    if (
      Dreamsleeve::Client::Diagnostics::Phantoms().Start(
        runtime.clientPath.parent_path() / "phantom-diagnostics" / "recordings",
        scenario,
        thirtySeconds ? 30 : 15,
        runtime.app->Exchange().Phantoms().Settings().sampleRate))
    {
      Publisher::StartRecording();
      logger::info("Phantom diagnostics recording requested");
    }
  }

  export void StartReplay(std::uint32_t scenario)
  {
    if (Runtime::Get().context != Runtime::GameContext::Playing || Dreamsleeve::Client::Diagnostics::Phantoms().Active()) return;
    const auto directory = Runtime::Get().clientPath.parent_path() / "phantom-diagnostics" / "recordings";
    const auto logs      = SKSE::log::log_directory();
    if (
      Dreamsleeve::Game::PhantomReplay::Start(
        directory,
        scenario,
        Get().engine,
        logs ? *logs / "DreamsleevePhantomDiagnostics" : std::filesystem::path{}))
      ResetResources();
  }
#endif

  export bool UseFirefly(Domain::PlayerId id)
  {
    const auto& settings = Get().settings;
    if (!settings || !settings->receive) return true;
    if (Display::Shown(id)) return false;
    return settings->fallback;
  }

  // How far the phantom of a player shown as a firefly has loaded; absent when
  // it is not loading (shown, refused, out of reach or phantoms switched off).
  export std::optional<Nameplates::Loading> Loading(Domain::PlayerId id)
  {
    return Display::Loading(id);
  }

  export std::size_t Count()
  {
    return Display::Count();
  }

  void TraceFrame(Clock::time_point now)
  {
    static Clock::time_point lastFrame{}, nextTrace{};
    if (lastFrame != Clock::time_point{})
      Trace::Observe(Trace::Metric::Frame, std::chrono::duration<double, std::milli>(now - lastFrame).count());
    lastFrame = now;
    if (now >= nextTrace)
    {
      Trace::FlushMetrics();
      nextTrace = now + MetricsInterval;
    }
  }

  void ReportNetwork(P::Exchange& exchange, const P::Display& display, const P::ViewSettings& settings, Clock::time_point now)
  {
    auto& state = Get();
    if (now < state.nextNetworkReport) return;
    state.nextNetworkReport = now + NetworkReportInterval;
    const auto stats        = exchange.Stats();
    Trace::Event("exchange", [&] {
      return std::format(
        "\"epoch\":{},\"model_bytes_total\":{},\"pose_bytes_total\":{},\"rejected_total\":{},\"dropped_total\":{},\"cache_hits_total\":{},\"queued\":{},\"sample_rate\":{}",
        exchange.Epoch(),
        stats.modelBytes,
        stats.poseBytes,
        stats.rejected,
        stats.dropped,
        stats.cacheHits,
        stats.queued,
        stats.sampleRate);
    });
    for (const auto& remote : display.remotes)
    {
      const auto timing    = remote.playback.Inspect(Micros(now), settings);
      const auto displayed = Display::DisplayedGeneration(remote.player);
      Trace::Event("playback", [&] {
        return std::format(
          "\"player\":{},\"view\":{},\"generation\":{},\"displayed\":{},\"samples\":{},\"sequence\":{},\"age_us\":{},\"ahead_us\":{},\"delay_us\":{}",
          remote.player,
          remote.view,
          remote.descriptor.generation.value,
          displayed,
          timing.samples,
          timing.sequence,
          timing.ageUs,
          timing.aheadUs,
          timing.delayUs);
      });
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

  export void Tick(Clock::time_point now, Nameplates::Frame& names)
  {
    Trace::Span tickSpan(Trace::Metric::GameTick);
    TraceFrame(now);
    auto& runtime = Runtime::Get();
    auto& state   = Get();
    Display::BeginFrame();
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

    // Apply visibility even while waiting for the local player/cell or replay.
    if (!settings.receive && !Display::Empty()) Display::Reset(&exchange);

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
    const auto observed =
      state.context.Observe(ready ? std::optional<Capture::Context::Space>{{space->form->GetFormID(), space->interior}} : std::nullopt);
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
        Display::HideAll(now);
        state.waiting = true;
      }
      return;
    }

    if (state.waiting) logger::info("Phantom capture resumed in {} {:08X}", space->interior ? "CELL" : "WRLD", space->form->GetFormID());
    state.waiting = false;
    if (observed == Capture::Context::Observation::Changed)
    {
      logger::info("Phantom coordinate space changed to {} {:08X}", space->interior ? "CELL" : "WRLD", space->form->GetFormID());
      Trace::Event("space", [&] { return std::format("\"form\":{},\"interior\":{}", space->form->GetFormID(), space->interior); });
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

    Publisher::Tick(*player, exchange, state.engine);
    auto display = exchange.Read();
    if constexpr (Trace::Compiled) ReportNetwork(exchange, display, settings, now);
    if (!display.available)
    {
#ifdef DREAMSLEEVE_DIAGNOSTICS
      if (Dreamsleeve::Client::Diagnostics::Phantoms().Active())
      {
        Display::Reset(&exchange);
        return;
      }
#endif
      ResetResources();
      return;
    }

    if (!settings.receive) return;
    const auto observer = World::Observe(player);
    if (!observer) return;
    // Players who start in one place must not stand inside each other's character
    // creation. Scenes stay loaded and reappear when the menu closes.
    if (runtime.ui.ui.chat.raceMenuHidePlayers && World::CharacterCreationOpen())
    {
      Display::HideAll(now);
      return;
    }

    Display::Pass pass{
        exchange,
        settings,
        state.engine,
        *player,
        *parent,
        {space->form->GetFormID(), space->interior},
        names,
        now
    };
    Display::Show(std::move(display.remotes), *observer, pass);
  }

}
