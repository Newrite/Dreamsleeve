module;

#include "Prelude.hpp"

export module Dreamsleeve.Game.PhantomScene;

import std;
import Dreamsleeve.Client.Phantom.Types;
import Dreamsleeve.Game.PhantomAsset;

export namespace Dreamsleeve::Game::PhantomScene
{
  namespace P = Dreamsleeve::Client::Phantom;
  namespace A = Dreamsleeve::Game::PhantomAsset;
  using Flag  = RE::NiAVObject::Flag;

  struct Look
  {
    P::Vec3 color{0.55f, 0.8f, 1};
    float   opacity{0.6f};
    // Adapter-only source cutoff, supplied per geometry by Scene.
    std::uint8_t alphaThreshold{};
  };

  struct Context
  {
    std::uint64_t epoch{};
    std::uint32_t cell{}, world{};
    bool          operator==(const Context&) const = default;
  };

  // Borrowed only for the duration of makeMesh. The adapter copies/uploads
  // these bytes into an ordinary BSTriShape, with its own lighting/alpha
  // property and generated white+alpha texture. No shader/material/path from
  // the sender is used. Index count fits the engine's u16 triangle count.
  struct MeshCreate
  {
    std::span<const A::RenderVertex> vertices;
    std::span<const std::uint16_t>   indices;
    const P::AlphaMask*              mask{};
    std::uint8_t                     alphaThreshold{};
    bool                             alphaBlend{}, doubleSided{}, mutableVertices{};
    Look                             look;
  };

  struct Engine
  {
    bool                                     (*mainThread)() noexcept {};
    P::Result<RE::NiPointer<RE::NiNode>>     (*makeNode)(){};
    P::Result<RE::NiPointer<RE::BSTriShape>> (*makeMesh)(const MeshCreate&){};
    // Only vertex buffers are mutable; indices and generated mask resources
    // may be shared immutably by the factory. Per-peer transforms, materials
    // and mutable vertex buffers MUST NOT be shared between scene owners.
    P::Result<void> (*upload)(RE::BSTriShape&, std::span<const A::RenderVertex>){};
    P::Result<void> (*look)(RE::BSTriShape&, Look){};
  };

  // These are mechanical work/resource admission budgets, supplied by the
  // parent from its effective policy, not a second presentation/AOI policy.
  struct Work
  {
    std::uint32_t nodes{128}, vertices{65535}, meshes{8};
  };

  struct Budget
  {
    std::uint64_t memoryBytes{256 * 1024 * 1024};
    std::uint32_t verticesPerFrame{2'000'000};
  };

  // Shared by every Scene in one main-update frame. Parent owns fairness and
  // retains the previously applied frame when these credits are exhausted.
  struct FrameBudget
  {
    std::uint64_t remainingVertices{2'000'000};
  };
  enum class BuildProgress
  {
    Pending,
    Ready
  };

  inline bool Valid(Look look)
  {
    return A::Finite(look.color) && look.color.x >= 0 && look.color.y >= 0 && look.color.z >= 0 && look.color.x <= 1 && look.color.y <= 1 &&
           look.color.z <= 1 && std::isfinite(look.opacity) && look.opacity >= 0 && look.opacity <= 1;
  }

  // Own one Scene per admitted peer, on the main thread. Begin + Advance
  // builds a detached, hidden candidate in bounded steps. Apply installs a
  // complete pose; Attach publishes it only after that succeeds. The parent
  // retains its old ready Scene until the candidate is ready for replacement.
  // Scene does not own scheduling, interpolation, subscriptions or fallback.
  class Scene final
  {
    struct Piece
    {
      std::uint32_t                 geometry{}, firstIndex{}, indexCount{};
      RE::NiPointer<RE::BSTriShape> mesh;
    };

public:

    Scene(const Scene&)            = delete;
    Scene& operator=(const Scene&) = delete;

    ~Scene()
    {
      // Releasing engine resources on a network/worker thread is a programming
      // error. Explicit Clear during quit avoids DLL/static teardown releases.
      if (root_ && (!engine_.mainThread || !engine_.mainThread())) std::terminate();
      ClearUnchecked();
    }

    struct Footprint
    {
      std::uint64_t cpuBytes{}, gpuBytes{}, nativeBytes{}, scratchBytes{};
      std::uint64_t applyCost{}, pieces{};

      std::uint64_t Total() const noexcept
      {
        return cpuBytes + gpuBytes + nativeBytes + scratchBytes;
      }
    };

    // Allocation-free reservation for this Graphics adapter, including its
    // retained immutable Asset. Exchange may already reserve a replacement's
    // smaller descriptor while this scene still owns the previous Asset.
    // Conservative duplicate Asset charges are intentional; Snapshot storage
    // remains in Core. Counts requested bytes, not opaque driver overhead.
    static P::Result<Footprint> Requirements(const P::ValidatedAsset& validated)
    {
      const auto&             asset         = validated.Value();
      const bool              vr            = REL::Module::IsVR();
      const std::uint64_t     nodeBytes     = vr ? 0x150 : 0x128;
      const std::uint64_t     meshBytes     = vr ? 0x1A0 : 0x160;
      const std::uint64_t     lightingBytes = vr ? 0x178 : 0x160;
      constexpr std::uint64_t packedBytes = 32, maximumIndices = 65535 * 3;
      Footprint               result;
      result.cpuBytes               = validated.MemoryBytes() + sizeof(Scene) +
                                      asset.nodes.size() * (sizeof(RE::NiPointer<RE::NiNode>) + sizeof(std::uint32_t)) +
                                      asset.geometry.size() * (sizeof(std::vector<A::RenderVertex>) + sizeof(P::Bound));
      result.nativeBytes            = (asset.nodes.size() + 1) * nodeBytes;
      std::uint64_t largestVertices = 0, largestMask = 1, largestBones = 0;
      for (const auto& geometry : asset.geometry)
      {
        const std::uint64_t count    = geometry.vertices.size();
        const std::uint64_t indices  = geometry.indices.size();
        const std::uint64_t pieces   = (indices + maximumIndices - 1) / maximumIndices;
        const std::uint64_t pixels   = geometry.mask ? geometry.mask->pixels.size() : 1;
        result.pieces               += pieces;
        result.cpuBytes             += count * sizeof(A::RenderVertex) + pieces * sizeof(Piece);
        // Every split owns VB/IB and CPU shadows; the mask and neutral normal
        // are RGBA8, one mip each. There is no second normalized Vertex copy.
        result.cpuBytes += pieces * count * packedBytes + indices * 2;
        result.gpuBytes += pieces * (count * packedBytes + (pixels + 1) * 4) + indices * 2;
        // BSTriShape + lighting + alpha + unique material + emissive color +
        // renderer wrapper + two NiSourceTexture/Texture wrappers; unique
        // material manager entry is 0x18. Sizes are verified for SE/AE/VR.
        result.nativeBytes += pieces * (meshBytes + lightingBytes + 0x38 + 0xA0 + 0xC + 0x30 + 2 * (0x58 + 0x28) + 0x18);
        result.applyCost   += count * (geometry.skin ? 6 : 2);
        if (geometry.skin) result.applyCost += geometry.skin->bones.size() * 16ULL;
        if (geometry.skin || geometry.dynamic) result.applyCost += pieces * count * 3;
        if (geometry.dynamic) result.applyCost += count * 2;  // central pose finite checks
        largestVertices = std::max(largestVertices, count);
        largestMask     = std::max(largestMask, pixels);
        largestBones    = std::max<std::uint64_t>(largestBones, geometry.skin ? geometry.skin->bones.size() : 0);
      }
      // Factory NiNode arrays start empty and grow by one. Each tree edge and
      // mesh attachment owns one NiPointer; reserve an array cookie per node.
      result.nativeBytes +=
        (asset.nodes.size() + result.pieces) * sizeof(RE::NiPointer<RE::NiAVObject>) + (asset.nodes.size() + 1) * sizeof(std::size_t);
      const auto buildScratch  = largestVertices * packedBytes + largestMask * 4 + 0xA0 +
                                 (asset.nodes.size() + result.pieces) * sizeof(RE::NiPointer<RE::NiAVObject>);
      const auto applyScratch  = asset.geometry.size() * (sizeof(const P::Deformation*) + 1) + largestBones * sizeof(RE::NiTransform);
      result.scratchBytes      = std::max(buildScratch, applyScratch);
      result.applyCost        += asset.nodes.size() * 2;  // pose installation and bound traversal
      std::uint64_t total      = 0;
      for (const auto bytes : {result.cpuBytes, result.gpuBytes, result.nativeBytes, result.scratchBytes})
      {
        if (bytes > std::numeric_limits<std::uint64_t>::max() - total) return A::Fail(P::Failure::LimitExceeded, "scene.memory-overflow");
        total += bytes;
      }
      return result;
    }

    static P::Result<std::uint64_t> RequiredBytes(const P::ValidatedAsset& asset)
    {
      auto required = Requirements(asset);
      if (!required) return std::unexpected(required.error());
      return required->Total();
    }

    static P::Result<std::unique_ptr<Scene>> Begin(
      P::ValidatedAsset asset,
      Engine            engine,
      Context           context,
      P::Generation     generation,
      Look              look   = {},
      Budget            budget = {},
      P::Limits         limits = {})
    {
      if (!engine.mainThread || !engine.mainThread()) return A::Fail(P::Failure::Busy, "scene.main-thread");
      if (!engine.makeNode || !engine.makeMesh || !engine.upload || !engine.look)
        return A::Fail(P::Failure::MissingSource, "engine.scene-factories");
      if (!context.cell || !Valid(look)) return A::Fail(P::Failure::InvalidNumber, "scene.context/look");
      auto scene     = std::unique_ptr<Scene>(new Scene(std::move(asset), engine, context, generation, look, budget, limits));
      auto admission = scene->Admit();
      if (!admission) return std::unexpected(admission.error());
      auto root = engine.makeNode();
      if (!root) return std::unexpected(root.error());
      if (!*root) return A::Fail(P::Failure::Storage, "scene.root-factory");
      scene->root_ = std::move(*root);
      Sanitize(*scene->root_);
      scene->root_->GetFlags().set(Flag::kHidden);
      scene->nodes_.resize(scene->asset_->Value().nodes.size());
      scene->vertices_.resize(scene->asset_->Value().geometry.size());
      scene->bounds_.resize(scene->asset_->Value().geometry.size());
      return scene;
    }

    P::Result<BuildProgress> Advance(Context current, Work work = {})
    {
      auto context = Check(current);
      if (!context) return std::unexpected(context.error());
      if (ready_) return BuildProgress::Ready;
      const auto&   asset    = asset_->Value();
      std::uint32_t nodeWork = 0;
      while (createdNodes_ < nodes_.size() && nodeWork < work.nodes)
      {
        auto node = engine_.makeNode();
        if (!node) return AbortBuild(node.error());
        if (!*node) return AbortBuild({P::Failure::Storage, "scene.node-factory"});
        Sanitize(**node);
        (*node)->local          = A::Native(asset.nodes[createdNodes_].local);
        nodes_[createdNodes_++] = std::move(*node);
        ++nodeWork;
      }
      if (createdNodes_ != nodes_.size()) return BuildProgress::Pending;
      while (attachedNodes_ < nodes_.size() && nodeWork < work.nodes)
      {
        const auto parent = asset.nodes[attachedNodes_].parent.value;
        auto*      into   = parent == P::NoNode ? root_.get() : nodes_[parent].get();
        into->AttachChild(nodes_[attachedNodes_].get(), true);
        ++attachedNodes_;
        ++nodeWork;
      }
      if (attachedNodes_ != nodes_.size()) return BuildProgress::Pending;
      std::uint32_t meshWork   = 0;
      std::uint64_t vertexWork = 0;
      while (createdPieces_ < pieces_.size() && meshWork < work.meshes)
      {
        auto&       piece    = pieces_[createdPieces_];
        const auto& geometry = asset.geometry[piece.geometry];
        const auto  count    = geometry.vertices.size();
        if (count > work.vertices - std::min<std::uint64_t>(vertexWork, work.vertices))
        {
          if (!meshWork && count > work.vertices) return A::Fail(P::Failure::LimitExceeded, "scene.step-vertex-budget");
          break;
        }
        auto& vertices = vertices_[piece.geometry];
        if (vertices.empty())
        {
          vertices.reserve(count);
          for (const auto& vertex : geometry.vertices)
            vertices.push_back({vertex.position, vertex.normal, vertex.tangent, vertex.u, vertex.v, vertex.color});
        }
        const MeshCreate request{
            vertices,
            std::span(geometry.indices).subspan(piece.firstIndex, piece.indexCount),
            geometry.mask ? &*geometry.mask : nullptr,
            geometry.alphaThreshold,
            geometry.alphaBlend,
            geometry.doubleSided,
            geometry.dynamic || geometry.skin.has_value(),
            look_
        };
        auto mesh = engine_.makeMesh(request);
        if (!mesh) return AbortBuild(mesh.error());
        if (!*mesh) return AbortBuild({P::Failure::Storage, "scene.mesh-factory"});
        // Factories return ordinary non-skinned shapes. CPU skinning happens
        // above their vertex buffers; no source bone/controller links exist.
        if ((*mesh)->GetGeometryRuntimeData().skinInstance) return AbortBuild({P::Failure::InvalidSkin, "scene.factory-returned-skin"});
        Sanitize(**mesh);
        piece.mesh = std::move(*mesh);
        nodes_[geometry.node.value]->AttachChild(piece.mesh.get(), true);
        ++createdPieces_;
        ++meshWork;
        vertexWork += count;
      }
      ready_ = createdPieces_ == pieces_.size();
      return ready_ ? BuildProgress::Ready : BuildProgress::Pending;
    }

    P::Result<void> Apply(const P::Snapshot& snapshot, Context current, FrameBudget& frame)
    {
      auto context = Check(current);
      if (!context) return context;
      if (!ready_) return A::Fail(P::Failure::Busy, "scene.build-pending");
      if (snapshot.generation != generation_ || snapshot.context != context_.epoch)
        return A::Fail(P::Failure::Stale, "scene.generation/context");
      const auto& asset = asset_->Value();
      auto        shape = P::CheckSnapshot(snapshot, *asset_);
      if (!shape) return shape;
      if (vertexCost_ > frame.remainingVertices) return A::Fail(P::Failure::Busy, "scene.shared-frame-budget");
      frame.remainingVertices -= vertexCost_;
      std::vector<const P::Deformation*> deformations(asset.geometry.size());
      for (const auto& deformation : snapshot.deformations)
        deformations[deformation.geometry] = &deformation;
      // All input and skin computation succeeds before touching the visible
      // tree. Buffers are pre-admitted, reused and never shared with a peer.
      for (std::uint32_t i = 0; i < asset.geometry.size(); ++i)
      {
        auto bound = A::Deform(asset.geometry[i], snapshot, deformations[i], vertices_[i]);
        if (!bound) return std::unexpected(bound.error());
        A::Enclose(*bound, snapshot.bounds[i]);
        if (!A::Finite(*bound)) return A::Fail(P::Failure::InvalidNumber, "scene.bound-union");
        bounds_[i] = *bound;
      }
      root_->GetFlags().set(Flag::kHidden);
      posed_ = false;
      for (auto& piece : pieces_)
      {
        const auto& geometry = asset.geometry[piece.geometry];
        if (geometry.dynamic || geometry.skin)
        {
          auto uploaded = engine_.upload(*piece.mesh, vertices_[piece.geometry]);
          if (!uploaded) return uploaded;  // Keep the whole candidate hidden.
        }
      }
      root_->previousWorld = root_->world;
      root_->world         = RE::NiTransform{};
      root_->local         = parent_ ? parent_->world.Invert() * root_->world : root_->world;
      for (std::uint32_t i = 0; i < nodes_.size(); ++i)
      {
        auto& node         = *nodes_[i];
        node.previousWorld = node.world;
        node.world         = A::Native(snapshot.channels[i].world);
        node.GetFlags().set(snapshot.channels[i].hidden, Flag::kHidden);
        node.GetFadeAmount() = 1;
      }
      // The validated tree is topological. Install every world transform
      // before deriving locals so no parent comes from an older pose.
      for (auto& node : nodes_)
        node->local = node->parent->world.Invert() * node->world;
      for (auto& piece : pieces_)
      {
        const auto& geometry = asset.geometry[piece.geometry];
        auto&       mesh     = *piece.mesh;
        mesh.previousWorld   = mesh.world;
        mesh.world           = nodes_[geometry.node.value]->world;
        mesh.local           = RE::NiTransform{};
        mesh.GetFlags().set(snapshot.channels[geometry.node.value].hidden, Flag::kHidden);
        A::Bounds(mesh, bounds_[piece.geometry]);
        // Update model bounds too: a later engine pass must not replace the
        // recomputed world sphere with the original undeformed body bound.
        const auto     inverse = mesh.world.Invert();
        const P::Bound model{
            A::Value(inverse * A::Native(bounds_[piece.geometry].center)),
            bounds_[piece.geometry].radius / mesh.world.scale
        };
        mesh.GetModelData().modelBound = {A::Native(model.center), model.radius};
        if (auto* box = mesh.GetVRModelBoundBox())
        {
          box->center      = A::Native(model.center);
          box->halfExtents = {model.radius, model.radius, model.radius};
        }
      }
      auto rebuilt = RebuildBounds();
      if (!rebuilt) return rebuilt;
      posed_ = true;
      if (parent_) root_->GetFlags().reset(Flag::kHidden);
      return {};
    }

    P::Result<void> Apply(const P::Snapshot& snapshot, Context current)
    {
      FrameBudget single{budget_.verticesPerFrame};
      return Apply(snapshot, current, single);
    }

    P::Result<void> Attach(RE::NiNode& parent, Context current)
    {
      auto context = Check(current);
      if (!context) return context;
      if (!ready_ || !posed_) return A::Fail(P::Failure::Busy, "scene.not-posed");
      if (parent_.get() == &parent) return {};
      if (parent_)
      {
        ClearUnchecked();
        return A::Fail(P::Failure::Stale, "scene.parent-changed");
      }
      auto transform = A::Value(parent.world);
      if (!transform) return std::unexpected(transform.error());
      parent_      = RE::NiPointer<RE::NiNode>{&parent};
      root_->local = parent.world.Invert() * root_->world;
      parent.AttachChild(root_.get(), true);
      root_->GetFlags().reset(Flag::kHidden);
      return {};
    }

    P::Result<void> SetLook(Look look, Context current)
    {
      auto context = Check(current);
      if (!context) return context;
      if (!Valid(look)) return A::Fail(P::Failure::InvalidNumber, "scene.look");
      root_->GetFlags().set(Flag::kHidden);
      for (auto& piece : pieces_)
        if (piece.mesh)
        {
          auto materialLook           = look;
          materialLook.alphaThreshold = asset_->Value().geometry[piece.geometry].alphaThreshold;
          auto changed                = engine_.look(*piece.mesh, materialLook);
          if (!changed)
          {
            posed_ = false;
            return changed;
          }
        }
      look_ = look;
      if (posed_ && parent_) root_->GetFlags().reset(Flag::kHidden);
      return {};
    }

    P::Result<void> Hide(Context current)
    {
      auto context = Check(current);
      if (!context) return context;
      root_->GetFlags().set(Flag::kHidden);
      return {};
    }

    P::Result<void> Clear()
    {
      if (!engine_.mainThread || !engine_.mainThread()) return A::Fail(P::Failure::Busy, "scene.main-thread");
      ClearUnchecked();
      return {};
    }

    // Use only within this frame; the public result is a value, not a Ni node.
    P::Bound BodyBound() const noexcept
    {
      return bodyBound_;
    }

    bool Ready() const noexcept
    {
      return ready_ && posed_;
    }

    std::uint64_t MemoryBytes() const noexcept
    {
      return memoryBytes_;
    }

    std::uint64_t WorkingBytes() const noexcept
    {
      return MemoryBytes();
    }

    std::uint64_t ApplyCost() const noexcept
    {
      return vertexCost_;
    }

    // Conservative vertex work units: 2 visits for rigid bounds, 6 for skin
    // (up to 4 weighted bone transforms + 2 bound passes), and 3 per uploaded
    // piece (pack, GPU copy, CPU shadow). RenderVertex itself is 48 bytes;
    // the engine's packed vertex is 32. Split pieces count each upload.
    // Matrix setup costs 16/bone, dynamic finite checks 2/vertex, nodes 2/node.
    // This bounds admitted work; it is not elapsed-time measurement.
    std::uint64_t VertexCost() const noexcept
    {
      return vertexCost_;
    }

private:

    Scene(P::ValidatedAsset asset, Engine engine, Context context, P::Generation generation, Look look, Budget budget, P::Limits limits)
        : asset_(std::move(asset)),
          engine_(engine),
          context_(context),
          generation_(generation),
          look_(look),
          budget_(budget),
          limits_(limits)
    {}

    std::optional<P::ValidatedAsset>          asset_;
    Engine                                    engine_;
    Context                                   context_;
    P::Generation                             generation_;
    Look                                      look_;
    Budget                                    budget_;
    P::Limits                                 limits_;
    RE::NiPointer<RE::NiNode>                 root_, parent_;
    std::vector<RE::NiPointer<RE::NiNode>>    nodes_;
    std::vector<Piece>                        pieces_;
    std::vector<std::vector<A::RenderVertex>> vertices_;
    std::vector<P::Bound>                     bounds_;
    std::vector<std::uint32_t>                order_;
    std::size_t                               createdNodes_{}, attachedNodes_{}, createdPieces_{};
    std::uint64_t                             memoryBytes_{};
    std::uint64_t                             vertexCost_{};
    P::Bound                                  bodyBound_;
    bool                                      ready_{}, posed_{};

    static void Sanitize(RE::NiAVObject& object)
    {
      object.controllers.reset();
      object.collisionObject.reset();
      object.SetUserData(nullptr);
      object.GetFlags().set(Flag::kIgnoreFade);
      object.GetFadeAmount() = 1;
    }

    P::Result<void> Admit()
    {
      const auto& asset    = asset_->Value();
      auto        required = Requirements(*asset_);
      if (!required) return std::unexpected(required.error());
      memoryBytes_ = required->Total();
      vertexCost_  = required->applyCost;
      if (vertexCost_ > budget_.verticesPerFrame) return A::Fail(P::Failure::LimitExceeded, "scene.frame-vertex-budget");
      if (memoryBytes_ > budget_.memoryBytes) return A::Fail(P::Failure::LimitExceeded, "scene.memory-budget");
      // Core guarantees one topological tree: reverse node order is already
      // a bottom-up traversal. No duplicate graph policy or adjacency copy.
      order_.resize(asset.nodes.size());
      std::iota(order_.begin(), order_.end(), 0U);
      pieces_.reserve(static_cast<std::size_t>(required->pieces));
      constexpr std::size_t maximumIndices = 65535 * 3;
      for (std::uint32_t i = 0; i < asset.geometry.size(); ++i)
      {
        const auto& geometry = asset.geometry[i];
        for (std::size_t first = 0; first < geometry.indices.size(); first += maximumIndices)
          pieces_.push_back(
            {i,
             static_cast<std::uint32_t>(first),
             static_cast<std::uint32_t>(std::min(maximumIndices, geometry.indices.size() - first)),
             {}});
      }
      return {};
    }

    P::Result<void> Check(Context current)
    {
      if (!engine_.mainThread || !engine_.mainThread()) return A::Fail(P::Failure::Busy, "scene.main-thread");
      if (!root_) return A::Fail(P::Failure::Stale, "scene.cleared");
      if (current != context_)
      {
        ClearUnchecked();
        return A::Fail(P::Failure::Stale, "scene.cell/world/epoch");
      }
      if (parent_)
      {
        if (root_->parent != parent_.get())
        {
          ClearUnchecked();
          return A::Fail(P::Failure::Stale, "scene.detached-by-engine");
        }
        auto transform = A::Value(parent_->world);
        if (!transform)
        {
          ClearUnchecked();
          return std::unexpected(transform.error());
        }
      }
      return {};
    }

    P::Result<BuildProgress> AbortBuild(P::Error error)
    {
      ClearUnchecked();
      return std::unexpected(std::move(error));
    }

    P::Result<void> RebuildBounds()
    {
      for (auto id : order_ | std::views::reverse)
      {
        P::Bound bound;
        for (const auto& child : nodes_[id]->GetChildren())
          if (child && !child->GetFlags().all(Flag::kHidden))
            A::Enclose(bound, {A::Value(child->worldBound.center), child->worldBound.radius});
        if (!A::Finite(bound)) return A::Fail(P::Failure::InvalidNumber, "scene.container-bound");
        A::Bounds(*nodes_[id], bound);
      }
      P::Bound bound;
      for (const auto& child : root_->GetChildren())
        if (child && !child->GetFlags().all(Flag::kHidden))
          A::Enclose(bound, {A::Value(child->worldBound.center), child->worldBound.radius});
      if (!A::Finite(bound)) return A::Fail(P::Failure::InvalidNumber, "scene.body-bound");
      bodyBound_ = bound;
      A::Bounds(*root_, bound);
      return {};
    }

    void ClearUnchecked() noexcept
    {
      if (root_)
      {
        root_->GetFlags().set(Flag::kHidden);
        // The actual parent may have changed/detached during an engine cell
        // transition. Read it now; no raw parent survives this frame.
        if (auto* parent = root_->parent) parent->DetachChild(root_.get());
      }
      decltype(pieces_){}.swap(pieces_);
      decltype(nodes_){}.swap(nodes_);
      root_.reset();
      parent_.reset();
      decltype(vertices_){}.swap(vertices_);
      decltype(bounds_){}.swap(bounds_);
      decltype(order_){}.swap(order_);
      bodyBound_ = {};
      asset_.reset();
      memoryBytes_ = 0;
      vertexCost_  = 0;
      ready_       = false;
      posed_       = false;
    }
  };

}
