module;
#include "Prelude.hpp"
export module Dreamsleeve.Game.PhantomCapture;
import std;
#ifdef DREAMSLEEVE_DIAGNOSTICS
import Dreamsleeve.Client.Diagnostics.PhantomTrace;
#endif
import Dreamsleeve.Client.Phantom.Types;
import Dreamsleeve.Game.PhantomNative;
import Dreamsleeve.Game.PhantomMath;
export import Dreamsleeve.Game.PhantomCaptureRules;

export namespace Dreamsleeve::Game::PhantomCapture
{
  namespace P  = Dreamsleeve::Client::Phantom;
  namespace N  = Dreamsleeve::Game::PhantomNative;
  namespace A  = Dreamsleeve::Game::PhantomMath;
  using Engine = N::Engine;
  using Flag   = RE::NiAVObject::Flag;

  struct Stamp
  {
    P::Generation generation;
    P::Sequence   sequence;
    std::uint64_t context{}, sampledAtUs{};
  };

  // Events and completion hooks request an audit; only stable native contents
  // can authorize publication. No game object crosses this atomic boundary.
  std::atomic<bool> auditRequested{};

  inline void RequestAudit() noexcept
  {
    auditRequested.store(true, std::memory_order_relaxed);
  }

  struct Attachment
  {
    bool present{}, hidden{};
  };

  inline Attachment Locate(RE::NiAVObject* root, RE::NiAVObject* object)
  {
    bool hidden = false;
    for (unsigned i = 0; object && i < 4096; ++i, object = object->parent)
    {
      if (object == root) return {true, hidden};
      hidden |= object->GetFlags().all(Flag::kHidden);
    }
    return {};
  }

  // Transforms, visibility, parent links and camera state are intentionally
  // absent. A bounded structural audit detects unannounced mesh replacements.
  inline P::Result<AppearanceProbe> Probe(RE::NiNode& root)
  {
    std::vector<RE::NiAVObject*> nodes;
    if (auto collected = N::Collect(&root, nodes); !collected) return std::unexpected(collected.error());
    AppearanceProbe result;
    // Reparenting/reordering the same attachments is not an appearance change.
    std::ranges::sort(nodes, std::less<RE::NiAVObject*>{});
    for (auto* node : nodes)
      if (auto* g = node->AsGeometry())
      {
        auto auxiliary = N::Auxiliary(*g);
        if (!auxiliary) return std::unexpected(auxiliary.error());
        if (*auxiliary) continue;
        auto& d  = g->GetGeometryRuntimeData();
        auto  v  = std::uint64_t(reinterpret_cast<std::uintptr_t>(g));
        v       ^= std::uint64_t(reinterpret_cast<std::uintptr_t>(d.skinInstance.get())) << 7;
        if (d.skinInstance) v ^= std::uint64_t(reinterpret_cast<std::uintptr_t>(d.skinInstance->skinPartition.get())) << 13;
        if (auto* dynamic = g->AsDynamicTriShape())
        {
          auto&               data = dynamic->GetDynamicTrishapeRuntimeData();
          RE::BSSpinLockGuard guard(data.lock);
          if (data.dynamicData && data.dataSize >= 16 && data.dataSize <= P::Limits{}.assetBytes)
          {
            const auto count  = data.dataSize / 16;
            v                ^= std::uint64_t(count) << 37;
            // Bounded deformation probe, never a per-frame vertex stream.
            // Continuous expressions must settle before a new native asset.
            const auto* positions = static_cast<const float*>(data.dynamicData);
            for (std::uint32_t i = 0; i < std::min(count, 32u); ++i)
              for (unsigned axis = 0; axis < 3; ++axis)
              {
                const float f = positions[(std::uint64_t(i) * count / std::min(count, 32u)) * 4 + axis];
                if (!std::isfinite(f) || std::abs(f) >= 100000) return A::Fail(P::Failure::InvalidNumber, "native.probe-position");
                result.positions.push_back(f);
              }
          }
        }
        if (auto* tri = g->AsTriShape())
        {
          const auto& c  = tri->GetTrishapeRuntimeData();
          v             ^= std::uint64_t(c.vertexCount) << 32;
          v             ^= c.triangleCount;
        }
        result.structure += v * 0x9e3779b185ebca87ULL;
      }
    return result;
  }

  class Source
  {
    Engine                    engine;
    RE::ObjectRefHandle       player;
    RE::NiPointer<RE::NiNode> root;
    std::vector<N::Binding>   bindings;
    P::ValidatedAsset         asset;
    P::Snapshot               last;

    struct Anchor
    {
      RE::NiPointer<RE::NiAVObject> parent;
      RE::NiTransform               local;
    };

    std::vector<Anchor> anchors;
    std::uint32_t       missingGeometry{};
    AppearanceRevision  appearance;
    AppearanceChange    change{};
    std::uint64_t       nextAudit{}, retryAt{};

public:

    Source(Engine e, RE::PlayerCharacter& p, RE::NiNode* r, std::vector<N::Binding> b, P::ValidatedAsset a, AppearanceProbe initial)
        : engine(e),
          player(p.GetHandle()),
          root(r),
          bindings(std::move(b)),
          asset(std::move(a)),
          appearance(std::move(initial))
    {}

    bool RebuildDue(std::uint64_t now)
    {
      if (now < retryAt) return false;
      // Even many actor callbacks cannot trigger more than four probes/sec.
      if (now < nextAudit) return false;
      const auto requested = auditRequested.exchange(false, std::memory_order_relaxed);
      // Quiet scenes retain a bounded one-second fallback for third-party edits.
      nextAudit     = now + (requested ? 250000 : 1000000);
      auto  ref     = player.get();
      auto* actor   = ref ? ref->As<RE::Actor>() : nullptr;
      auto* current = actor ? actor->Get3D(false) : nullptr;
      if (!current) return false;
      if (current != root.get())
      {
        change = AppearanceChange::Structure;
        return true;
      }
      auto probe = Probe(*root);
      // Open reports the concrete failure; retain the last usable scene meanwhile.
      change = probe ? appearance.Observe(*probe, now) : AppearanceChange::Structure;
      if (probe && appearance.Pending()) nextAudit = now + 250000;
      return change != AppearanceChange::None;
    }

    std::string_view ChangeReason() const
    {
      return change == AppearanceChange::Structure ? "structure" : "settled-deformation";
    }

    void DeferRebuild(std::uint64_t now)
    {
      retryAt = now + 1000000;
    }

    struct Health
    {
      std::uint32_t omitted{}, hidden{};
      std::string   detail;
    };

    Health ReadStatus() const
    {
      return {
          missingGeometry,
          static_cast<std::uint32_t>(std::ranges::count_if(last.channels, [](const auto& c) { return c.hidden; })),
          missingGeometry ? "native attachments temporarily unavailable" : ""
      };
    }

    P::Result<P::Snapshot> Sample(RE::PlayerCharacter& p, bool firstPerson, Stamp stamp, bool retainMissing = false)
    {
      if (!engine.mainThread || !engine.mainThread()) return A::Fail(P::Failure::Busy, "native.thread");
      if (p.GetHandle() != player || p.Get3D(false) != root.get()) return A::Fail(P::Failure::Stale, "native.source");
      P::Snapshot out    = last;
      out.generation     = stamp.generation;
      out.sequence       = stamp.sequence;
      out.context        = stamp.context;
      out.sampledAtUs    = stamp.sampledAtUs;
      out.origin         = A::Value(root->world.translate);
      const auto& layout = asset.Layout();
      out.channels.resize(layout.requiredChannels.size());
      anchors.resize(layout.requiredChannels.size());
      out.bounds.resize(layout.bounds.size());
      std::uint32_t missing = 0;
      for (std::size_t i = 0; i < layout.requiredChannels.size(); ++i)
      {
        auto&           binding    = bindings[layout.requiredChannels[i]];
        const auto      attachment = Locate(root.get(), binding.owner.get());
        const auto*     transform  = attachment.present ? N::Resolve(binding) : nullptr;
        const bool      geometry   = layout.nodes[layout.requiredChannels[i]].geometry;
        RE::NiTransform retained;
        auto&           anchor       = anchors[i];
        const bool      hadTransform = transform != nullptr;
        if (geometry && transform && binding.owner->parent && std::abs(binding.owner->parent->world.scale) > 1e-6f)
        {
          anchor.parent.reset(binding.owner->parent);
          anchor.local = anchor.parent->world.Invert() * *transform;
        }
        else if (geometry && retainMissing && anchor.parent && Locate(root.get(), anchor.parent.get()).present)
        {
          retained  = anchor.parent->world * anchor.local;
          transform = &retained;
        }
        if (transform)
        {
          auto value = A::Value(*transform);
          if (!value) return std::unexpected(value.error());
          out.channels[i].world = *value;
        }
        else if (last.channels.empty())
          return A::Fail(P::Failure::MissingSource, "native.initial-channel");
        if (!hadTransform && geometry) ++missing;
        out.channels[i].hidden = geometry && (retainMissing && !hadTransform && transform
                                                ? last.channels[i].hidden
                                                : binding.visibility.Sample(transform != nullptr, attachment.hidden, firstPerson));
      }
      for (std::size_t i = 0; i < layout.bounds.size(); ++i)
      {
        auto& binding = bindings[layout.bounds[i]];
        if (Locate(root.get(), binding.owner.get()).present)
        {
          const auto& b = binding.owner->worldBound;
          out.bounds[i] = {A::Value(b.center), b.radius};
        }
        else if (retainMissing && !last.channels.empty())
        {
          const auto channel = std::ranges::lower_bound(layout.requiredChannels, layout.bounds[i]);
          if (channel != layout.requiredChannels.end() && *channel == layout.bounds[i])
          {
            const auto index = static_cast<std::size_t>(channel - layout.requiredChannels.begin());
            const auto delta = A::Native(out.channels[index].world) * A::Native(last.channels[index].world).Invert();
            out.bounds[i]    = {A::Value(delta * A::Native(last.bounds[i].center)), last.bounds[i].radius * std::abs(delta.scale)};
          }
        }
      }
      auto checked = P::CheckSnapshot(out, asset);
      if (!checked) return std::unexpected(checked.error());
      missingGeometry = missing;
      last            = out;
      return out;
    }
  };

  struct Opened
  {
    std::unique_ptr<Source> source;
    P::ValidatedAsset       asset;
    P::Snapshot             initial;
  };

  inline P::Result<Opened> Open(Engine engine, RE::PlayerCharacter& player, bool firstPerson, Stamp stamp, const P::Limits& limits = {})
  {
    auto* object = player.Get3D(false);
    auto* root   = object ? object->AsNode() : nullptr;
    if (!root || !root->parent) return A::Fail(P::Failure::MissingSource, "native.third-person");
    auto prepared = N::Prepare(root, engine);
    if (!prepared) return std::unexpected(prepared.error());
#ifdef DREAMSLEEVE_DIAGNOSTICS
    const auto parseStart = std::chrono::steady_clock::now();
#endif
    auto asset = [&] {
#ifdef DREAMSLEEVE_DIAGNOSTICS
      Dreamsleeve::Client::Diagnostics::Trace::Span span(Dreamsleeve::Client::Diagnostics::Trace::Metric::ValidateAsset);
#endif
      return P::ValidatedAsset::Parse(std::move(prepared->asset), limits);
    }();
#ifdef DREAMSLEEVE_DIAGNOSTICS
    const auto parseEnd = std::chrono::steady_clock::now();
#endif
    if (!asset) return std::unexpected(asset.error());
    if (asset->Layout().nodes.size() != prepared->bindings.size()) return A::Fail(P::Failure::InvalidLink, "native.serialized-tree");
    auto probe = Probe(*root);
    if (!probe) return std::unexpected(probe.error());
    auto source  = std::make_unique<Source>(engine, player, root, std::move(prepared->bindings), *asset, std::move(*probe));
    auto initial = source->Sample(player, firstPerson, stamp);
    if (!initial) return std::unexpected(initial.error());
#ifdef DREAMSLEEVE_DIAGNOSTICS
    logger::info(
      "[Phantom stages] validate_asset_ms={:.3f} bind_probe_initial_pose_ms={:.3f}",
      std::chrono::duration<double, std::milli>(parseEnd - parseStart).count(),
      std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - parseEnd).count());
#endif
    return Opened{std::move(source), std::move(*asset), std::move(*initial)};
  }

}
