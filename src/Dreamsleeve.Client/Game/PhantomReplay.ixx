module;
#include "Prelude.hpp"
export module Dreamsleeve.Game.PhantomReplay;
#ifdef DREAMSLEEVE_DIAGNOSTICS
import std;
import Dreamsleeve.Client.Diagnostics.PhantomReplay;
import Dreamsleeve.Client.Phantom.Playback;
import Dreamsleeve.Game.PhantomScene;

export namespace Dreamsleeve::Game::PhantomReplay
{
  namespace D = Dreamsleeve::Client::Diagnostics;
  namespace P = Dreamsleeve::Client::Phantom;
  namespace S = Dreamsleeve::Game::PhantomScene;
  using Clock = std::chrono::steady_clock;

  struct Status
  {
    bool          active{}, loading{};
    std::string   stage{"Не запущено"}, directory, error;
    std::uint64_t rendered{}, decoded{}, models{}, memoryBytes{};
    double        seconds{}, buildMs{}, buildStepMs{}, applyMs{}, decodeMs{};
  };

  struct State
  {
    D::ReplayReader               reader;
    S::Engine                     engine;
    Status                        status;
    std::optional<D::ReplayFrame> current, next;
    std::unique_ptr<S::Scene>     scene, candidate;
    S::Context                    context;
    std::optional<PhantomSpace>   space;
    Clock::time_point             lastTick{}, beginAt{};
    std::uint64_t                 firstUs{}, playheadUs{};
    double                        buildingMs{};
    bool                          finished{};
  };

  State& Get()
  {
    static auto* state = new State;
    return *state;
  }

  void Stop(std::string_view reason = "Остановлено")
  {
    auto& s = Get();
    s.reader.Stop();
    s.scene.reset();
    s.candidate.reset();
    s.current.reset();
    s.next.reset();
    s.space.reset();
    s.lastTick      = {};
    s.status.active = false;
    s.status.stage  = reason;
  }

  bool Start(std::filesystem::path root, std::uint32_t scenario, S::Engine engine)
  {
    auto& s = Get();
    if (s.reader.Read().busy) return false;
    Stop();
    if (!s.reader.Start(std::move(root), scenario)) return false;
    s.engine        = engine;
    s.status        = {};
    s.status.active = s.status.loading = true;
    s.status.stage                     = "Загрузка архива — закройте меню";
    s.finished                         = false;
    s.firstUs = s.playheadUs = 0;
    s.buildingMs             = 0;
    s.beginAt                = {};
    logger::info("Phantom replay requested: scenario={}", scenario);
    return true;
  }

  Status Read()
  {
    auto       out  = Get().status;
    const auto load = Get().reader.Read();
    out.loading     = load.busy;
    out.directory   = load.directory;
    out.decoded     = load.frames;
    out.models      = load.models;
    out.decodeMs    = load.decodeMs;
    if (!load.error.empty()) out.error = load.error;
    return out;
  }

  bool Active()
  {
    return Get().status.active;
  }

  void Pause()
  {
    Get().lastTick = {};
  }

  void Shutdown()
  {
    Stop();
    Get().reader.Shutdown();
  }

  void Fail(std::string_view message)
  {
    auto& s = Get();
    logger::error("Phantom replay failed: {}", message);
    Stop("Ошибка воспроизведения");
    s.status.error = message;
  }

  void Tick(RE::NiNode& parent, PhantomSpace space, S::Look look, Clock::time_point now)
  {
    auto& s = Get();
    if (!s.status.active || s.finished) return;
    if (s.space && *s.space != space)
    {
      Stop("Изменился мир/интерьер");
      return;
    }
    s.space         = space;
    const auto load = s.reader.Read();
    if (!load.error.empty())
    {
      Fail(load.error);
      return;
    }
    if (auto* ui = RE::UI::GetSingleton(); ui && ui->GameIsPaused())
    {
      Pause();
      return;
    }
    const auto elapsed = s.lastTick == Clock::time_point{}
                         ? 0ULL
                         : static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::microseconds>(now - s.lastTick).count());
    s.lastTick         = now;
    if (!s.next) s.next = s.reader.Take();
    if (!s.next)
    {
      if (load.complete && s.current)
      {
        s.finished     = true;
        s.status.stage = "Завершено — фантом оставлен для осмотра";
        logger::info(
          "Phantom replay complete: rendered={}, build max={:.2f} ms, step max={:.2f} ms, apply max={:.2f} ms",
          s.status.rendered,
          s.status.buildMs,
          s.status.buildStepMs,
          s.status.applyMs);
      }
      return;  // Waiting for disk never advances the playback clock.
    }
    if (s.current && !s.candidate && now >= s.beginAt) s.playheadUs += elapsed;
    if (s.current && now < s.beginAt) return;
    // Consume already elapsed samples; timestamps come from the saved capture.
    while (s.current && s.next && s.next->pose->generation == s.current->pose->generation && s.next->pose->sampledAtUs <= s.playheadUs)
    {
      s.current = std::move(s.next);
      s.next    = s.reader.Take();
    }
    const bool replace =
      !s.current || (s.next && s.next->pose->generation != s.current->pose->generation && s.next->pose->sampledAtUs <= s.playheadUs);
    if (replace)
    {
      const auto       start = Clock::now();
      const auto&      frame = *s.next;
      const S::Context context{frame.pose->context, space};
      if (!s.candidate)
      {
        auto scene = S::Scene::Begin(*frame.asset, s.engine, context, frame.pose->generation, look, {S::Scene::Reservation(*frame.asset)});
        if (!scene)
        {
          Fail(scene.error().field);
          return;
        }
        s.candidate  = std::move(*scene);
        s.buildingMs = 0;
        logger::info(
          "Phantom replay building generation {}: nodes={}, geometry={}, reserved={:.2f} MiB",
          frame.pose->generation.value,
          frame.asset->Layout().requiredChannels.size(),
          frame.asset->Layout().bounds.size(),
          S::Scene::Reservation(*frame.asset) / 1048576.0);
      }
      auto       built      = s.candidate->Advance(context);
      const auto ms         = std::chrono::duration<double, std::milli>(Clock::now() - start).count();
      s.buildingMs         += ms;
      s.status.buildStepMs  = std::max(s.status.buildStepMs, ms);
      s.status.stage        = "Создание игровой модели — закройте меню";
      if (!built)
      {
        Fail(built.error().field);
        return;
      }
      if (*built != S::BuildProgress::Ready) return;
      s.status.buildMs = std::max(s.status.buildMs, s.buildingMs);
      s.scene          = std::move(s.candidate);
      s.context        = context;
      s.current        = std::move(s.next);
      s.next.reset();
      s.playheadUs = s.current->pose->sampledAtUs;
      if (!s.firstUs)
      {
        s.firstUs = s.playheadUs;
        s.beginAt = now + std::chrono::seconds(3);
      }
      s.status.memoryBytes = s.scene->MemoryBytes();
      logger::info("Phantom replay model ready: generation={}, build={:.2f} ms", s.current->pose->generation.value, s.buildingMs);
    }
    if (!s.scene || !s.current) return;
    const auto                 start = Clock::now();
    std::optional<P::Snapshot> interpolated;
    if (s.next && s.next->pose->generation == s.current->pose->generation && s.next->pose->context == s.current->pose->context)
    {
      const auto a = s.current->pose->sampledAtUs, b = s.next->pose->sampledAtUs;
      const auto t = std::clamp(double(s.playheadUs - a) / double(b - a), 0.0, 1.0);
      interpolated = P::Motion::Between(*s.current->pose, *s.next->pose, static_cast<float>(t));
    }
    S::FrameBudget budget;
    auto           applied = s.scene->Apply(interpolated ? *interpolated : *s.current->pose, s.context, budget);
    if (!applied)
    {
      Fail(applied.error().field);
      return;
    }
    auto attached = s.scene->Attach(parent, s.context);
    if (!attached)
    {
      Fail(attached.error().field);
      return;
    }
    const auto ms    = std::chrono::duration<double, std::milli>(Clock::now() - start).count();
    s.status.applyMs = std::max(s.status.applyMs, ms);
    s.status.seconds = double(s.current->pose->sampledAtUs - s.firstUs) / 1000000.0;
    ++s.status.rendered;
    s.status.stage = now < s.beginAt ? "Старт через 3 секунды — закройте меню" : "Воспроизведение";
    if (s.status.rendered == 1 || s.status.rendered % 100 == 0)
      logger::info(
        "Phantom replay frame {}: source sequence={}, apply={:.2f} ms, elapsed={:.2f} s",
        s.status.rendered,
        s.current->pose->sequence.value,
        ms,
        s.status.seconds);
  }

}
#endif
