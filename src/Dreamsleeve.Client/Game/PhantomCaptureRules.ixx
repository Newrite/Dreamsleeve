export module Dreamsleeve.Game.PhantomCaptureRules;

import std;

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
