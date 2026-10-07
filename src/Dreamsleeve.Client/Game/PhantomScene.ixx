module;
#include "Prelude.hpp"
export module Dreamsleeve.Game.PhantomScene;
import std;
import Dreamsleeve.Client.Phantom.Types;
import Dreamsleeve.Game.PhantomNative;
import Dreamsleeve.Game.PhantomMath;
import Dreamsleeve.Game.PhantomCaptureRules;

export namespace Dreamsleeve::Game::PhantomScene
{
  namespace P  = Dreamsleeve::Client::Phantom;
  namespace N  = Dreamsleeve::Game::PhantomNative;
  namespace A  = Dreamsleeve::Game::PhantomMath;
  using Engine = N::Engine;
  using Look   = N::Look;
  using Flag   = RE::NiAVObject::Flag;

  struct Context
  {
    std::uint64_t epoch{};
    PhantomSpace  space;
    bool          operator==(const Context&) const = default;
  };

  struct Budget
  {
    std::uint64_t memoryBytes{256ULL * 1024 * 1024};
  };

  struct FrameBudget
  {
    std::uint64_t remainingChannels{65536};
  };
  enum class BuildProgress
  {
    Pending,
    Ready
  };

  class Scene
  {
    Engine                                     engine;
    P::ValidatedAsset                          asset;
    Context                                    context;
    P::Generation                              generation;
    Look                                       look;
    RE::NiPointer<RE::NiNode>                  root, parent;
    std::vector<RE::NiAVObject*>               nodes;
    std::vector<RE::NiSkinInstance*>           skins;
    std::vector<RE::BSLightingShaderProperty*> surfaces;
    enum class Phase
    {
      Waiting,
      Loaded,
      Posed
    };
    Phase         phase{Phase::Waiting};
    std::uint64_t memory{};

    Scene(P::ValidatedAsset a, Engine e, Context c, P::Generation g, Look l, std::uint64_t bytes)
        : engine(e),
          asset(std::move(a)),
          context(c),
          generation(g),
          look(l),
          memory(bytes)
    {}

    P::Result<void> Check(Context c) const
    {
      if (!engine.mainThread || !engine.mainThread()) return A::Fail(P::Failure::Busy, "scene.thread");
      if (c != context) return A::Fail(P::Failure::Stale, "scene.context");
      return {};
    }

public:

    Scene(const Scene&)            = delete;
    Scene& operator=(const Scene&) = delete;

    ~Scene()
    {
      if (root && !engine.mainThread()) std::terminate();
      if (root && parent && root->parent == parent.get()) parent->DetachChild(root.get());
    }

    static std::uint64_t Reservation(const P::ValidatedAsset& asset)
    {
      return asset.MemoryBytes() + 4ULL * asset.Value().nif.size() + asset.Layout().blocks * 1024ULL;
    }

    static P::Result<std::unique_ptr<Scene>> Begin(
      P::ValidatedAsset asset,
      Engine            engine,
      Context           context,
      P::Generation     generation,
      Look              look,
      Budget            budget = {})
    {
      if (!engine.mainThread || !engine.mainThread() || !engine.load) return A::Fail(P::Failure::Busy, "scene.native-operations");
      // Conservative admission, not a claim to measure native heap or driver
      // residency: NIF + native arrays + native GPU buffers + loader scratch.
      const auto bytes = Reservation(asset);
      if (bytes > budget.memoryBytes) return A::Fail(P::Failure::LimitExceeded, "scene.memory");
      return std::unique_ptr<Scene>(new Scene(std::move(asset), engine, context, generation, look, bytes));
    }

    std::uint64_t MemoryBytes() const
    {
      return memory;
    }

    bool Ready() const
    {
      return phase == Phase::Posed;
    }

    P::Result<BuildProgress> Advance(Context c)
    {
      auto checked = Check(c);
      if (!checked) return std::unexpected(checked.error());
      if (phase != Phase::Waiting) return BuildProgress::Ready;
      try
      {
        nodes.clear();
        skins.clear();
        surfaces.clear();
        root.reset();
#ifdef DREAMSLEEVE_DIAGNOSTICS
        const auto loadStart = std::chrono::steady_clock::now();
#endif
        auto loaded = engine.load(asset);
#ifdef DREAMSLEEVE_DIAGNOSTICS
        const auto loadEnd = std::chrono::steady_clock::now();
#endif
        if (!loaded) return std::unexpected(loaded.error());
        root = std::move(*loaded);
        N::Collect(root.get(), nodes);
        const auto& layout = asset.Layout();
        if (nodes.size() != layout.nodes.size()) return A::Fail(P::Failure::InvalidLink, "scene.tree-size");
        for (std::size_t i = 0; i < nodes.size(); ++i)
        {
          auto*       object   = nodes[i];
          const auto& expected = layout.nodes[i];
          if ((i && object->parent != nodes[expected.parent]) || bool(object->AsGeometry()) != expected.geometry)
            return A::Fail(P::Failure::InvalidLink, "scene.tree-links");
          object->SetUserData(nullptr);
          object->GetFadeAmount() = 1;
          object->GetFlags().set(Flag::kIgnoreFade);
          object->GetFlags().reset(Flag::kHidden);
          if (auto* geometry = object->AsGeometry())
          {
            N::Ghostify(*geometry, engine, look);
            surfaces.push_back(geometry->lightingShaderProp_cast());
            if (auto* skin = geometry->GetGeometryRuntimeData().skinInstance.get())
            {
              if (!skin->skinData || !skin->rootParent || !skin->bones || !skin->boneWorldTransforms)
                return A::Fail(P::Failure::InvalidLink, "scene.skin");
              for (std::uint32_t b = 0; b < skin->skinData->GetBoneCount(); ++b)
              {
                if (!skin->bones[b]) return A::Fail(P::Failure::InvalidLink, "scene.bone");
                skin->boneWorldTransforms[b] = &skin->bones[b]->world;
              }
              if (std::ranges::find(skins, skin) == skins.end()) skins.push_back(skin);
            }
          }
        }
        root->GetFlags().set(Flag::kHidden);
        phase = Phase::Loaded;
#ifdef DREAMSLEEVE_DIAGNOSTICS
        logger::info(
          "[Phantom stages] generation={} nistream_load_ms={:.3f} scene_prepare_ms={:.3f}",
          generation.value,
          std::chrono::duration<double, std::milli>(loadEnd - loadStart).count(),
          std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - loadEnd).count());
#endif
        return BuildProgress::Ready;
      }
      catch (const std::exception& e)
      {
        return A::Fail(P::Failure::InvalidFormat, e.what());
      }
    }

    P::Result<void> Apply(const P::Snapshot& pose, Context c, FrameBudget& frame)
    {
      auto checked = Check(c);
      if (!checked) return checked;
      if (phase == Phase::Waiting) return A::Fail(P::Failure::Busy, "scene.loading");
      if (pose.generation != generation || pose.context != c.epoch) return A::Fail(P::Failure::Stale, "scene.generation");
      const auto& layout = asset.Layout();
      if (pose.channels.size() != layout.requiredChannels.size() || pose.bounds.size() != layout.bounds.size())
        return A::Fail(P::Failure::InvalidFormat, "scene.pose-shape");
      if (frame.remainingChannels < nodes.size()) return A::Fail(P::Failure::Busy, "scene.frame-budget");
      frame.remainingChannels -= nodes.size();
      for (std::size_t i = 0; i < layout.requiredChannels.size(); ++i)
      {
        auto* node          = nodes[layout.requiredChannels[i]];
        node->previousWorld = node->world;
        node->world         = A::Native(pose.channels[i].world);
        node->GetFlags().set(pose.channels[i].hidden, Flag::kHidden);
        node->GetFadeAmount() = 1;
      }
      for (std::size_t i = 0; i < layout.bounds.size(); ++i)
      {
        auto bound    = pose.bounds[i];
        bound.radius += 0.055f;
        A::Bounds(*nodes[layout.bounds[i]], bound);
      }
      for (auto* node : nodes)
        node->local =
          node->parent && std::abs(node->parent->world.scale) > 1e-6f ? node->parent->world.Invert() * node->world : node->world;
      for (auto* object : nodes | std::views::reverse)
        if (auto* node = object->AsNode())
        {
          P::Bound bound{};
          for (const auto& child : node->GetChildren())
            if (child) A::Enclose(bound, {A::Value(child->worldBound.center), child->worldBound.radius});
          A::Bounds(*node, bound);
        }
      for (auto* skin : skins)
        skin->frameID = std::numeric_limits<std::uint32_t>::max();
      for (auto* surface : surfaces)
        N::ApplyLook(*surface, look);
      phase = Phase::Posed;
      return {};
    }

    P::Result<void> Attach(RE::NiNode& target, Context c)
    {
      auto checked = Check(c);
      if (!checked) return checked;
      if (!Ready()) return A::Fail(P::Failure::Busy, "scene.not-posed");
      if (parent.get() != &target)
      {
        if (parent && root->parent == parent.get()) parent->DetachChild(root.get());
        parent.reset(&target);
        parent->AttachChild(root.get(), false);
      }
      root->GetFlags().reset(Flag::kHidden);
      return {};
    }

    P::Result<void> Hide(Context c)
    {
      auto checked = Check(c);
      if (!checked) return checked;
      if (root) root->GetFlags().set(Flag::kHidden);
      return {};
    }

    P::Result<void> SetLook(Look value, Context c)
    {
      auto checked = Check(c);
      if (!checked) return checked;
      look = value;
      for (auto* surface : surfaces)
        N::ApplyLook(*surface, look);
      return {};
    }

    P::Bound BodyBound() const
    {
      return root ? P::Bound{A::Value(root->worldBound.center), root->worldBound.radius} : P::Bound{};
    }
  };

}
