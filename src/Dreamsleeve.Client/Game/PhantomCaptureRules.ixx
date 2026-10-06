export module Dreamsleeve.Game.PhantomCaptureRules;

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
      const auto change = observed.structure != accepted.structure ? AppearanceChange::Structure
                        : !Near(accepted, observed, 0.25f)         ? AppearanceChange::Deformation
                                                                   : AppearanceChange::None;
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
    bool             effectMaterial{}, decalMaterial{}, skinned{}, dedicatedDecal{};
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
      // Engine decal geometry/weapon blood is auxiliary even when skinned.
      // A skin-bearing lighting mesh with a decal flag can still be the body.
      return dedicatedDecal || (!skinned && (effectMaterial || decalMaterial));
    }
  };

  struct Visibility
  {
    bool hidden{};

    static constexpr bool Capture(bool culled, bool partitionsHidden, bool firstPerson) noexcept
    {
      // Camera suppression must not remove the third-person character.
      // Dismember partitions have their own authored visibility.
      return !partitionsHidden && (firstPerson || !culled);
    }

    constexpr bool Sample(bool present, bool culled, bool firstPerson) noexcept
    {
      if (present && !firstPerson) hidden = culled;
      return !present || hidden;
    }
  };

}
