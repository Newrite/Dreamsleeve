export module Dreamsleeve.Game.Phantom.CaptureRules;

import std;
import Dreamsleeve.Client.Utils;

// Shared by capture lifecycle and remote scene lifetime.
export namespace Dreamsleeve::Game
{

  struct PhantomSpace
  {
    std::uint32_t id{};
    bool          interior{};
    bool          operator==(const PhantomSpace&) const = default;
  };

}

// Engine facts are mapped once by the native adapter. No game objects or
// shader flag numbers belong to this policy, so it can run in native tests.
export namespace Dreamsleeve::Game::PhantomCapture
{

  // Retry pacing for a capture that failed. A failure which repeats identically cannot be
  // resolved by retrying it: the same source model produces it again, and the build it
  // re-runs costs tens of milliseconds of the main thread. The interval therefore doubles up
  // to a ceiling, while any different outcome and any structural change restore the
  // immediate retry, so a genuinely new model is never delayed by an old verdict.
  class RetryPolicy
  {
public:

    static constexpr std::uint64_t Immediate = 1'000'000;   // 1 s
    static constexpr std::uint64_t Ceiling   = 60'000'000;  // 60 s

    void Failed(std::string_view outcome)
    {
      const bool same = outcome == failedOutcome;
      failedOutcome.assign(outcome);
      delay = same ? std::min(delay * 2, Ceiling) : Immediate;
    }

    void Reset()
    {
      delay = Immediate;
      failedOutcome.clear();
    }

    [[nodiscard]] std::uint64_t Delay() const noexcept { return delay; }

private:

    std::uint64_t delay{Immediate};
    std::string   failedOutcome;
  };

  struct AppearanceProbe
  {
    std::uint64_t      structure{};
    std::vector<float> positions;
  };

  enum class AppearanceChange
  {
    None,
    Structure,
    Deformation
  };

  // Compare with the accepted asset, not rounded bins or the preceding tick.
  // Small face animation must not publish an entire character when it crosses
  // a quantization boundary. Slow accumulated morphs still exceed this limit.
  class AppearanceRevision
  {
    AppearanceProbe                accepted;
    std::optional<AppearanceProbe> candidate;
    std::uint64_t                  since{};

    static bool Near(const AppearanceProbe& a, const AppearanceProbe& b, float tolerance)
    {
      if (a.structure != b.structure || a.positions.size() != b.positions.size()) return false;
      for (std::size_t i = 0; i + 2 < a.positions.size(); i += 3)
      {
        float distance = 0;
        for (unsigned axis = 0; axis < 3; ++axis)
        {
          const auto delta  = a.positions[i + axis] - b.positions[i + axis];
          distance         += delta * delta;
        }
        if (!std::isfinite(distance) || distance > tolerance * tolerance) return false;
      }
      return true;
    }

public:

    explicit AppearanceRevision(AppearanceProbe initial) : accepted(std::move(initial)) {}

    bool Pending() const noexcept
    {
      return candidate.has_value();
    }

    AppearanceChange Observe(AppearanceProbe observed, std::uint64_t now)
    {
      auto change = AppearanceChange::None;
      if (observed.structure != accepted.structure)
      {
        change = AppearanceChange::Structure;
      }
      else if (!Near(accepted, observed, 0.25f))
      {
        change = AppearanceChange::Deformation;
      }

      if (change == AppearanceChange::None)
      {
        candidate.reset();
        return change;
      }

      // Structural updates settle promptly. In-place deformations must settle
      // for longer, with a tighter tolerance than the publication threshold.
      if (
        !candidate || candidate->structure != observed.structure ||
        (change == AppearanceChange::Deformation && !Near(*candidate, observed, 0.0625f)))
      {
        candidate = std::move(observed);
        since     = now;
        return AppearanceChange::None;
      }

      const auto settle = change == AppearanceChange::Structure ? 250000ULL : 1000000ULL;
      return now - since >= settle ? change : AppearanceChange::None;
    }
  };

  // Preserve the sampling grid when a game frame arrives late. Take at most
  // one fresh sample per tick; missed slots never become duplicate poses.
  class Cadence
  {
public:

    using Clock = std::chrono::steady_clock;

    bool Due(Clock::time_point now, std::uint32_t rate)
    {
      const auto period = std::chrono::microseconds(1000000 / std::max(rate, 1u));
      if (period != period_ || next_ == Clock::time_point{})
      {
        next_   = now;
        period_ = period;
      }
      if (now < next_) return false;
      Dreamsleeve::Utils::Time::AdvanceSample(next_, now, period_);
      return true;
    }

    void Defer(Clock::time_point now)
    {
      next_ = now + std::chrono::seconds(1);
    }

    void Reset()
    {
      next_   = {};
      period_ = {};
    }

private:

    Clock::time_point         next_{};
    std::chrono::microseconds period_{};
  };

  // Local identity of a coordinate space: CELL indoors, WRLD outdoors.
  // Missing readiness does not establish a different space.
  class Context
  {
public:

    using Space = Dreamsleeve::Game::PhantomSpace;

    enum class Observation
    {
      Waiting,
      Ready,
      Changed
    };

    Observation Observe(std::optional<Space> space)
    {
      if (!space) return Observation::Waiting;
      const bool changed = current_ && *current_ != *space;
      current_           = space;
      return changed ? Observation::Changed : Observation::Ready;
    }

    void Reset()
    {
      current_.reset();
    }

private:

    std::optional<Space> current_;
  };

  struct Surface
  {
    bool             effectMaterial{}, decalMaterial{}, skinned{}, dedicatedDecal{}, refraction{};
    std::string_view name;
    bool             hasShader{true};
    float            shaderAlpha{1}, materialAlpha{1};

    constexpr bool Auxiliary() const noexcept
    {
      // The validated local prototype omitted RaceMenu's duplicate tattoo
      // surfaces, shaderless collision helpers and fully faded material.
      // Those layers do not belong to a neutral-colour ghost's silhouette.
      if (!hasShader || name.contains(" [Ovl") || name.contains(" [SOvl")) return true;
      if ((shaderAlpha >= 0 && shaderAlpha < 0.01f) || (materialAlpha >= 0 && materialAlpha < 0.01f)) return true;
      // A refraction surface bends the screen, not the silhouette. Vanilla heat haze and
      // similar effects carry it on a mirrored quad whose world transform no quaternion can
      // express, so keeping it would fail the whole capture for an effect a ghost cannot
      // show anyway. Both the authored and the engine's temporary flag count: the temporary
      // one is what heat haze carries while it plays.
      if (refraction) return true;
      // Engine decal geometry/weapon blood is auxiliary even when skinned.
      // A skin-bearing lighting mesh with a decal flag can still be the body.
      return dedicatedDecal || (!skinned && (effectMaterial || decalMaterial));
    }
  };

  // `culled` already excludes camera-owned flags. A body-in-first-person camera
  // may still hide gear for the local view, so first person keeps the last
  // third-person state. The first observation is authored state either way:
  // SMP collision meshes stay hidden in a capture opened in first person.
  struct Visibility
  {
    bool hidden{};
    bool observed{};

    constexpr bool Sample(bool present, bool culled, bool firstPerson) noexcept
    {
      if (present && (!firstPerson || !observed))
      {
        hidden   = culled;
        observed = true;
      }
      return !present || hidden;
    }
  };

}
