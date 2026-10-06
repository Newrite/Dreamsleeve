module;
#include "Prelude.hpp"
export module Dreamsleeve.Game.PhantomCapture;
import std;
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

  std::atomic<std::uint64_t> completedRevision{};

  // Hooks/events only mark a completed model change. All native work is later
  // coalesced in the main-update phase; no scene ownership crosses threads.
  inline void AppearanceChanged() noexcept
  {
    completedRevision.fetch_add(1, std::memory_order_relaxed);
  }

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
  inline std::uint64_t Signature(RE::NiNode& root)
  {
    std::vector<RE::NiAVObject*> nodes;
    N::Collect(&root, nodes);
    std::uint64_t sum = 0;
    for (auto* node : nodes)
      if (auto* g = node->AsGeometry(); g && !N::Auxiliary(*g))
      {
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
            const auto count = data.dataSize / 16;
            // Bounded deformation probe, never a per-frame vertex stream.
            // Continuous expressions must settle before a new native asset.
            const auto* positions = static_cast<const float*>(data.dynamicData);
            for (std::uint32_t i = 0; i < std::min(count, 32u); ++i)
              for (unsigned axis = 0; axis < 3; ++axis)
              {
                const float f = positions[(std::uint64_t(i) * count / std::min(count, 32u)) * 4 + axis];
                if (std::isfinite(f) && std::abs(f) < 100000) v = (v ^ std::uint64_t(std::int64_t(std::round(f * 16)))) * 1099511628211ULL;
              }
          }
        }
        if (auto* tri = g->AsTriShape())
        {
          const auto& c  = tri->GetTrishapeRuntimeData();
          v             ^= std::uint64_t(c.vertexCount) << 32;
          v             ^= c.triangleCount;
        }
        sum += v * 0x9e3779b185ebca87ULL;
      }
    return sum;
  }

  class Source
  {
    Engine                    engine;
    RE::ObjectRefHandle       player;
    RE::NiPointer<RE::NiNode> root;
    std::vector<N::Binding>   bindings;
    P::ValidatedAsset         asset;
    P::Snapshot               last;
    std::uint32_t             missingGeometry{};
    std::uint64_t             signature{}, revision{}, candidate{}, changedAt{}, nextAudit{}, retryAt{};

public:

    Source(Engine e, RE::PlayerCharacter& p, RE::NiNode* r, std::vector<N::Binding> b, P::ValidatedAsset a)
        : engine(e),
          player(p.GetHandle()),
          root(r),
          bindings(std::move(b)),
          asset(std::move(a)),
          signature(Signature(*r)),
          revision(completedRevision.load(std::memory_order_relaxed)),
          candidate(signature)
    {}

    bool RebuildDue(std::uint64_t now)
    {
      if (now < retryAt) return false;
      const auto requested = auditRequested.exchange(false, std::memory_order_relaxed);
      if (!requested && now < nextAudit) return false;
      nextAudit     = now + 250000;
      auto  ref     = player.get();
      auto* actor   = ref ? ref->As<RE::Actor>() : nullptr;
      auto* current = actor ? actor->Get3D(false) : nullptr;
      if (!current) return false;
      if (current != root.get()) return true;
      std::uint64_t observed{};
      try
      {
        observed = Signature(*root);
      }
      catch (const std::exception&)
      {
        return true;
      }  // Open reports the concrete unsupported asset.
      const auto rev = completedRevision.load(std::memory_order_relaxed);
      if (observed == signature && rev == revision)
      {
        changedAt = 0;
        return false;
      }
      if (!changedAt || candidate != observed)
      {
        candidate = observed;
        changedAt = now;
        return false;
      }
      return now - changedAt >= 750000;
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

    P::Result<P::Snapshot> Sample(RE::PlayerCharacter& p, bool firstPerson, Stamp stamp)
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
      out.bounds.resize(layout.bounds.size());
      std::uint32_t missing = 0;
      for (std::size_t i = 0; i < layout.requiredChannels.size(); ++i)
      {
        auto&       binding    = bindings[layout.requiredChannels[i]];
        const auto  attachment = Locate(root.get(), binding.owner.get());
        const auto* transform  = attachment.present ? N::Resolve(binding) : nullptr;
        if (transform)
        {
          auto value = A::Value(*transform);
          if (!value) return std::unexpected(value.error());
          out.channels[i].world = *value;
        }
        else if (last.channels.empty())
          return A::Fail(P::Failure::MissingSource, "native.initial-channel");
        if (!transform && layout.nodes[layout.requiredChannels[i]].geometry) ++missing;
        out.channels[i].hidden = layout.nodes[layout.requiredChannels[i]].geometry &&
                                 binding.visibility.Sample(transform != nullptr, attachment.hidden, firstPerson);
      }
      for (std::size_t i = 0; i < layout.bounds.size(); ++i)
      {
        auto& binding = bindings[layout.bounds[i]];
        if (Locate(root.get(), binding.owner.get()).present)
        {
          const auto& b = binding.owner->worldBound;
          out.bounds[i] = {A::Value(b.center), b.radius};
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
    try
    {
      auto* object = player.Get3D(false);
      auto* root   = object ? object->AsNode() : nullptr;
      if (!root || !root->parent) return A::Fail(P::Failure::MissingSource, "native.third-person");
      auto prepared = N::Prepare(root, engine);
      auto asset    = P::ValidatedAsset::Parse(std::move(prepared.asset), limits);
      if (!asset) return std::unexpected(asset.error());
      if (asset->Layout().nodes.size() != prepared.bindings.size()) return A::Fail(P::Failure::InvalidLink, "native.serialized-tree");
      auto source  = std::make_unique<Source>(engine, player, root, std::move(prepared.bindings), *asset);
      auto initial = source->Sample(player, firstPerson, stamp);
      if (!initial) return std::unexpected(initial.error());
      return Opened{std::move(source), std::move(*asset), std::move(*initial)};
    }
    catch (const std::exception& error)
    {
      return A::Fail(P::Failure::InvalidFormat, error.what());
    }
  }

}
