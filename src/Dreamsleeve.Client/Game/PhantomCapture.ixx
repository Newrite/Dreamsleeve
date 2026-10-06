module;

#include "Prelude.hpp"

export module Dreamsleeve.Game.PhantomCapture;

import std;
import Dreamsleeve.Client.Phantom.Types;
import Dreamsleeve.Client.Phantom.Masks;
import Dreamsleeve.Game.PhantomAsset;
import Dreamsleeve.Game.PhantomCaptureRules;
import Dreamsleeve.Game.PhantomRecovery;
import Dreamsleeve.Game.PhantomMesh;

export namespace Dreamsleeve::Game::PhantomCapture
{
  namespace P = Dreamsleeve::Client::Phantom;
  namespace A = Dreamsleeve::Game::PhantomAsset;
  using Flag  = RE::NiAVObject::Flag;

  struct Engine
  {
    bool (*mainThread)() noexcept {};
    // Returns CURRENT pre-skin vertices, including loaded body/face morphs.
    // Ordinary adapters can call PhantomAsset::CopyCpuMesh. A mod which owns
    // asynchronous per-vertex deformation needs its synchronized copy here.
    P::Result<A::RawMesh> (*mesh)(RE::BSTriShape&, const P::Limits&){};
    // Copy only the live diffuse texture's alpha, including BC-compressed GPU
    // resources. No path lookup. Reject unknown formats, oversized textures
    // and unavailable resources; never substitute an opaque card.
    P::Result<std::shared_ptr<const P::AlphaMask>> (*mask)(RE::NiSourceTexture&, const P::Limits&){};
    // Pose-only copy. No static attributes, material or indices are reread.
    P::Result<P::Deformation> (*deformation)(RE::BSTriShape&, std::uint32_t, const P::Limits&){};
    // Begin a fresh audit once. Busy retries reuse its staging job.
    P::Result<void> (*refreshMesh)(RE::BSTriShape&){};
  };

  struct Stamp
  {
    P::Generation generation;
    P::Sequence   sequence;
    std::uint64_t context{}, sampledAtUs{};
  };

  // Camera culling does not belong to appearance. Follow the prototype's
  // authored visibility rule while first person hides the third-person tree.
  // Root-camera hiding is ignored; removed/reparented equipment is still
  // discovered from attachment to the actual third-person root every frame.
  struct Attachment
  {
    bool present{}, hidden{};
  };

  inline Attachment Locate(RE::NiAVObject* root, RE::NiAVObject* object, std::uint32_t maximum)
  {
    bool hidden = false;
    for (std::uint32_t depth = 0; object && depth < maximum; ++depth, object = object->parent)
    {
      if (object == root) return {true, hidden};
      hidden |= object->GetFlags().all(Flag::kHidden);
    }
    return {};
  }

  inline P::Result<std::vector<RE::NiAVObject*>> Walk(RE::NiAVObject& root, const P::Limits& limits)
  {
    std::vector<RE::NiAVObject*>        objects, stack{&root};
    std::unordered_set<RE::NiAVObject*> seen;
    std::uint64_t                       slots = 0;
    while (!stack.empty())
    {
      auto* object = stack.back();
      stack.pop_back();
      if (!seen.emplace(object).second) return A::Fail(P::Failure::InvalidLink, "source.cycle/shared-child");
      if (objects.size() >= limits.nodes) return A::Fail(P::Failure::LimitExceeded, "source.nodes");
      objects.push_back(object);
      if (auto* node = object->AsNode())
      {
        auto& children  = node->GetChildren();
        slots          += children.capacity();
        if (slots > std::uint64_t(limits.nodes) * 4) return A::Fail(P::Failure::LimitExceeded, "source.child-slots");
        for (std::size_t i = children.capacity(); i > 0; --i)
          if (auto* child = children[static_cast<std::uint16_t>(i - 1)].get())
          {
            if (child->parent != node) return A::Fail(P::Failure::InvalidLink, "source.parent");
            if (stack.size() + objects.size() >= limits.nodes) return A::Fail(P::Failure::LimitExceeded, "source.pending-nodes");
            stack.push_back(child);
          }
      }
    }
    return objects;
  }

  inline bool AuxiliaryGeometry(RE::BSGeometry& geometry)
  {
    const auto& data   = geometry.GetGeometryRuntimeData();
    auto*       shader = data.shaderProperty.get();
    if (!shader) return Surface{.hasShader = false}.Auxiliary();
    using Shader   = RE::BSShaderProperty::EShaderPropertyFlag;
    auto* lighting = geometry.lightingShaderProp_cast();
    auto* material = lighting && lighting->material && lighting->material->GetType() == RE::BSShaderMaterial::Type::kLighting
                     ? static_cast<RE::BSLightingShaderMaterialBase*>(lighting->material)
                     : nullptr;
    return Surface{
        A::Kind(*shader, "BSEffectShaderProperty"),
        shader->flags.any(Shader::kDecal, Shader::kDynamicDecal),
        bool(data.skinInstance) || shader->flags.all(Shader::kSkinned),
        shader->flags.all(Shader::kWeaponBlood) || A::Kind(geometry, "BSSkinnedDecalTriShape"),
        geometry.name.c_str() ? geometry.name.c_str() : "",
        true,
        lighting ? lighting->alpha : 1.f,
        material ? material->materialAlpha : 1.f
    }
      .Auxiliary();
  }

  inline std::unexpected<P::Error> GeometryError(P::Error error, RE::BSGeometry& geometry)
  {
    const auto* shader  = geometry.GetGeometryRuntimeData().shaderProperty.get();
    const auto* name    = geometry.name.c_str();
    const auto* rtti    = shader ? shader->GetRTTI() : nullptr;
    const auto* type    = rtti ? rtti->GetName() : "none";
    error.field        += std::format(
      " [mesh={}, shader={}, flags={:X}]",
      std::string_view(name ? name : "").substr(0, 96),
      std::string_view(type ? type : "").substr(0, 96),
      shader ? shader->flags.underlying() : 0);
    return std::unexpected(std::move(error));
  }

  inline bool AllPartitionsHidden(RE::BSTriShape& shape, const P::Limits& limits)
  {
    auto* skin = shape.GetGeometryRuntimeData().skinInstance.get();
    if (!skin || !A::Kind(*skin, "BSDismemberSkinInstance")) return false;
    const auto& data = static_cast<RE::BSDismemberSkinInstance*>(skin)->GetRuntimeData();
    if (data.numPartitions <= 0 || std::uint32_t(data.numPartitions) > limits.geometry || !data.partitions) return false;
    for (std::int32_t i = 0; i < data.numPartitions; ++i)
      if (data.partitions[i].visible) return false;
    return true;
  }

  inline bool CaptureCandidate(RE::NiAVObject& root, RE::NiAVObject& object, bool firstPerson, const P::Limits& limits)
  {
    const auto attachment = Locate(&root, &object, limits.nodes);
    if (!attachment.present || !Visibility::Capture(attachment.hidden, false, firstPerson)) return false;
    if (auto* geometry = object.AsGeometry())
    {
      if (AuxiliaryGeometry(*geometry)) return false;
      if (auto* shape = geometry->AsTriShape(); shape && AllPartitionsHidden(*shape, limits)) return false;
    }
    return true;
  }

  inline void ReportMaterials(RE::NiAVObject& root, std::span<RE::NiAVObject* const> live, bool firstPerson, const P::Limits& limits)
  {
    // Capture can retry while GPU readback is pending. Bound both log cadence
    // and diagnostic names; never build a per-frame scene dump.
    using Clock = std::chrono::steady_clock;
    static Clock::time_point last;
    const auto               now = Clock::now();
    if (now - last < std::chrono::seconds(5)) return;
    last                  = now;
    std::uint32_t effects = 0, hidden = 0, projected = 0;
    std::string   names;
    using Shader = RE::BSShaderProperty::EShaderPropertyFlag;
    for (auto* object : live)
      if (auto* geometry = object->AsGeometry())
      {
        if (AuxiliaryGeometry(*geometry))
        {
          if (++effects <= 4)
            names += std::format(" [{}]", std::string_view(geometry->name.c_str() ? geometry->name.c_str() : "").substr(0, 96));
        }
        else if (!CaptureCandidate(root, *geometry, firstPerson, limits))
          ++hidden;
        else if (
          auto* property = geometry->lightingShaderProp_cast();
          property && property->flags.any(Shader::kDecal, Shader::kDynamicDecal, Shader::kProjectedUV))
          ++projected;
      }
    logger::info(
      "Phantom selection: {} auxiliary/decal meshes omitted{}; {} hidden meshes skipped before readback; {} projected/decal lighting meshes retained",
      effects,
      names,
      hidden,
      projected);
  }

  inline P::Result<void> Material(RE::BSTriShape& source, P::Geometry& geometry, const Engine& engine, const P::Limits& limits)
  {
    auto& data     = source.GetGeometryRuntimeData();
    auto* property = source.lightingShaderProp_cast();
    if (!property || !property->material || property->material->GetType() != RE::BSShaderMaterial::Type::kLighting)
      return A::Fail(P::Failure::UnsupportedGeometry, "material.lighting");
    auto&      material = *static_cast<RE::BSLightingShaderMaterialBase*>(property->material);
    const auto offset = material.texCoordOffset[0], scale = material.texCoordScale[0];
    if (
      !std::isfinite(offset.x) || !std::isfinite(offset.y) || !std::isfinite(scale.x) || !std::isfinite(scale.y) ||
      !std::isfinite(material.materialAlpha) || material.materialAlpha < 0 || material.materialAlpha > 1)
      return A::Fail(P::Failure::InvalidNumber, "material.uv/alpha");
    using Shader           = RE::BSShaderProperty::EShaderPropertyFlag;
    geometry.doubleSided   = property->flags.all(Shader::kTwoSided);
    const bool vertexAlpha = property->flags.all(Shader::kVertexAlpha);
    for (auto& vertex : geometry.vertices)
    {
      vertex.u        = vertex.u * scale.x + offset.x;
      vertex.v        = vertex.v * scale.y + offset.y;
      vertex.color[3] = static_cast<std::uint8_t>(std::lround((vertexAlpha ? vertex.color[3] : 255) * material.materialAlpha));
      if (!std::isfinite(vertex.u) || !std::isfinite(vertex.v)) return A::Fail(P::Failure::InvalidNumber, "material.baked-uv");
    }
    bool testing = false;
    if (data.alphaProperty)
    {
      const auto flags    = data.alphaProperty->alphaFlags;
      geometry.alphaBlend = (flags & 1) != 0;
      testing             = (flags & (1 << 9)) != 0;
      if (testing)
      {
        const auto function  = (flags >> 10) & 7;
        const auto threshold = data.alphaProperty->alphaThreshold;
        if (function != 4 && function != 6) return A::Fail(P::Failure::UnsupportedGeometry, "material.alpha-test-function");
        geometry.alphaThreshold = function == 6 && threshold ? std::uint8_t(threshold - 1) : threshold;
      }
    }
    if (!testing && !geometry.alphaBlend) return {};
    // Contract v1 has repeat sampling; independent clamp modes require a Core
    // field. UV transform has already been baked, so no material path survives.
    if (material.textureClampMode != 3) return A::Fail(P::Failure::UnsupportedGeometry, "material.alpha-clamp-mode");
    if (!material.diffuseTexture) return A::Fail(P::Failure::MissingSource, "material.alpha-texture");
    if (!engine.mask) return A::Fail(P::Failure::MissingSource, "engine.alpha-readback");
    auto mask = engine.mask(*material.diffuseTexture, limits);
    if (!mask) return std::unexpected(mask.error());
    const auto& value = *mask;
    if (
      !value || !value->width || !value->height || value->width > limits.maskDimension || value->height > limits.maskDimension ||
      std::uint64_t(value->width) * value->height != value->pixels.size() || value->pixels.size() > limits.maskBytes)
      return A::Fail(P::Failure::InvalidMask, "material.mask-shape");
    geometry.mask = std::move(*mask);
    return {};
  }

  struct BoneBinding
  {
    std::string   name;
    std::uint32_t hint{};
  };

  struct Binding
  {
    RE::NiPointer<RE::NiAVObject> owner, attachment;
    std::optional<BoneBinding>    bone;
    Visibility                    visibility;
    P::Channel                    last;
    std::string                   name, type;
    bool                          root{};
  };

  // Array positions/pointers are deliberately not retained: a flattened tree
  // can rebuild its entries while the NiPointer owner remains the same.
  inline P::Result<std::uint32_t> FindBone(RE::BSFlattenedBoneTree& tree, BoneBinding& key, const P::Limits& limits)
  {
    const auto& data = tree.GetRuntimeData();
    if (data.numBones > limits.nodes || (data.numBones && !data.boneEntries)) return A::Fail(P::Failure::InvalidSkin, "bone.entries");
    std::optional<std::uint32_t> found;
    for (std::uint32_t i = 0; i < data.numBones; ++i)
      if (data.boneEntries[i].nodeName.c_str() && key.name == data.boneEntries[i].nodeName.c_str())
      {
        if (found) return A::Fail(P::Failure::InvalidSkin, "bone.ambiguous-name");
        found = i;
      }
    if (!found) return A::Fail(P::Failure::Stale, "bone.missing-name");
    key.hint = *found;
    return *found;
  }

  // Small local change detector, not the wire/content hash. Dynamic positions
  // and normals are pose; every other vertex attribute and the topology are
  // appearance. No address, name or padding enters the signature.
  struct Fingerprint
  {
    std::uint64_t value{14695981039346656037ULL};

    template <class T>
    void Scalar(T scalar)
    {
      for (auto byte : std::bit_cast<std::array<std::byte, sizeof(T)>>(scalar))
        value = (value ^ std::to_integer<std::uint8_t>(byte)) * 1099511628211ULL;
    }

    void Vector(P::Vec3 v)
    {
      Scalar(v.x);
      Scalar(v.y);
      Scalar(v.z);
    }

    void Transform(const P::Transform& t)
    {
      Vector(t.position);
      Scalar(t.rotation.x);
      Scalar(t.rotation.y);
      Scalar(t.rotation.z);
      Scalar(t.rotation.w);
      Scalar(t.scale);
    }
  };

  inline std::uint64_t Signature(const P::Geometry& geometry)
  {
    Fingerprint hash;
    hash.Scalar(geometry.dynamic);
    hash.Scalar(geometry.vertices.size());
    hash.Scalar(geometry.indices.size());
    for (const auto& v : geometry.vertices)
    {
      if (!geometry.dynamic)
      {
        hash.Vector(v.position);
        hash.Vector(v.normal);
        hash.Vector(v.tangent);
      }
      hash.Scalar(v.u);
      hash.Scalar(v.v);
      for (auto c : v.color)
        hash.Scalar(c);
      for (auto w : v.weights)
        hash.Scalar(w);
      for (auto b : v.bones)
        hash.Scalar(b);
    }
    for (auto index : geometry.indices)
      hash.Scalar(index);
    hash.Scalar(geometry.alphaBlend);
    hash.Scalar(geometry.doubleSided);
    hash.Scalar(geometry.alphaThreshold);
    // Immutable alpha pixels are compared separately, by shared identity first.
    // Hashing a multi-megabyte shared mask once per mesh audit stalls the frame.
    return hash.value;
  }

  struct MeshBinding
  {
    RE::NiPointer<RE::BSTriShape>            owner;
    P::NodeId                                node;
    std::uint64_t                            signature{};
    std::uint32_t                            vertexCount{}, boneCount{};
    std::optional<P::Skin>                   skin;
    bool                                     dynamic{};
    Dreamsleeve::Game::PhantomRecovery::Mesh recovery;
    float                                    minimumWeight{4}, maximumWeight{};
    P::Geometry                              cached;
    P::Bound                                 localBound;
    std::uint64_t                            stamp{};
    std::string                              name;
    std::vector<P::Bound>                    boneBounds;
    std::uint64_t                            auditedAtUs{};
    bool                                     auditPending{};
  };

  // Cheap local stamps can contain addresses as integers. They never enter
  // the neutral asset, and no borrowed resource is dereferenced next frame.
  inline std::uint64_t CheapStamp(RE::BSTriShape& shape)
  {
    Fingerprint h;
    const auto& data   = shape.GetGeometryRuntimeData();
    const auto& counts = shape.GetTrishapeRuntimeData();
    h.Scalar(counts.vertexCount);
    h.Scalar(counts.triangleCount);
    h.Scalar(std::bit_cast<std::uint64_t>(data.vertexDesc));
    h.Scalar(reinterpret_cast<std::uintptr_t>(data.rendererData));
    auto* skin = data.skinInstance.get();
    h.Scalar(reinterpret_cast<std::uintptr_t>(skin));
    if (skin)
    {
      h.Scalar(reinterpret_cast<std::uintptr_t>(skin->skinData.get()));
      auto* parts = skin->skinPartition.get();
      h.Scalar(reinterpret_cast<std::uintptr_t>(parts));
      if (parts) h.Scalar(parts->numPartitions);
      if (A::Kind(*skin, "BSDismemberSkinInstance"))
      {
        const auto& dismember = static_cast<RE::BSDismemberSkinInstance*>(skin)->GetRuntimeData();
        h.Scalar(dismember.numPartitions);
        if (dismember.numPartitions > 0 && dismember.numPartitions <= 256 && dismember.partitions)
          for (std::int32_t i = 0; i < dismember.numPartitions; ++i)
            h.Scalar(dismember.partitions[i].visible);
      }
    }
    auto* lit = shape.lightingShaderProp_cast();
    h.Scalar(reinterpret_cast<std::uintptr_t>(lit));
    if (lit && lit->material)
    {
      // Projection/decal changes only affect omitted RGB layers. They must
      // not trigger a new neutral appearance or a GPU readback audit.
      using Shader             = RE::BSShaderProperty::EShaderPropertyFlag;
      constexpr auto rgbLayers = static_cast<std::uint64_t>(Shader::kDecal) | static_cast<std::uint64_t>(Shader::kDynamicDecal) |
                                 static_cast<std::uint64_t>(Shader::kProjectedUV);
      h.Scalar(lit->flags.underlying() & ~rgbLayers);
      h.Scalar(reinterpret_cast<std::uintptr_t>(lit->material));
      if (lit->material->GetType() == RE::BSShaderMaterial::Type::kLighting)
      {
        auto& m = *static_cast<RE::BSLightingShaderMaterialBase*>(lit->material);
        h.Scalar(m.materialAlpha);
        h.Scalar(m.textureClampMode);
        h.Scalar(m.texCoordOffset[0].x);
        h.Scalar(m.texCoordOffset[0].y);
        h.Scalar(m.texCoordScale[0].x);
        h.Scalar(m.texCoordScale[0].y);
        h.Scalar(reinterpret_cast<std::uintptr_t>(m.diffuseTexture.get()));
        const auto* texture = m.diffuseTexture ? m.diffuseTexture->rendererTexture : nullptr;
        h.Scalar(reinterpret_cast<std::uintptr_t>(texture ? texture->resourceView : nullptr));
      }
    }
    if (data.alphaProperty)
    {
      h.Scalar(data.alphaProperty->alphaFlags);
      h.Scalar(data.alphaProperty->alphaThreshold);
    }
    return h.value;
  }

  class Source;

  struct Captured
  {
    P::Asset                asset;
    std::unique_ptr<Source> source;
    P::Snapshot             initial;
  };

  P::Result<Captured> Open(Engine engine, RE::PlayerCharacter& player, bool firstPerson, Stamp stamp, const P::Limits& limits = {});

  // Own this object on the game thread. Only Captured.asset / Snapshot values
  // cross to Core/workers. Clear it on cell/world, save/load, disconnect and
  // quit while the engine is alive, before unloading Hooks' callbacks.
  class Source final
  {
public:

    Source(const Source&)            = delete;
    Source& operator=(const Source&) = delete;

    ~Source()
    {
      if (root_ && (!engine_.mainThread || !engine_.mainThread())) std::terminate();
    }

    P::Result<void> Clear()
    {
      if (!engine_.mainThread || !engine_.mainThread()) return A::Fail(P::Failure::Busy, "capture.main-thread");
      meshes_.clear();
      omitted_.clear();
      bindings_.clear();
      root_.reset();
      player_ = RE::ObjectRefHandle{};
      return {};
    }

    P::Result<void> InvalidateAppearance()
    {
      if (!engine_.mainThread || !engine_.mainThread()) return A::Fail(P::Failure::Busy, "capture.main-thread");
      appearanceDirty_ = true;
      return {};
    }

    P::Result<void> InvalidateBindings()
    {
      if (!engine_.mainThread || !engine_.mainThread()) return A::Fail(P::Failure::Busy, "capture.main-thread");
      bindingsDirty_ = true;
      return {};
    }

    struct Status
    {
      std::uint32_t omitted{}, hidden{};
      std::string   detail;
    };

    Status ReadStatus() const
    {
      Status status{static_cast<std::uint32_t>(omitted_.size())};
      if (!omitted_.empty()) status.detail = omitted_.front().error.field;
      for (const auto& mesh : meshes_)
        if (mesh.recovery.Fault())
        {
          ++status.hidden;
          if (status.detail.empty()) status.detail = mesh.recovery.Fault()->field;
        }
      return status;
    }

    bool PendingReadbacks() const
    {
      return std::ranges::any_of(omitted_, [](const auto& item) { return item.error.reason == P::Failure::Busy; });
    }

    bool SameAppearance(const Source& other) const
    {
      if (bindings_.size() != other.bindings_.size() || meshes_.size() != other.meshes_.size()) return false;
      for (std::size_t i = 0; i < bindings_.size(); ++i)
      {
        const auto& a = bindings_[i];
        const auto& b = other.bindings_[i];
        if (a.name != b.name || a.type != b.type || a.bone.has_value() != b.bone.has_value() || (a.bone && a.bone->name != b.bone->name))
          return false;
      }
      for (std::size_t i = 0; i < meshes_.size(); ++i)
      {
        const auto& a = meshes_[i];
        const auto& b = other.meshes_[i];
        if (
          a.node != b.node || a.signature != b.signature || !SameSkin(a.skin, b.skin) ||
          !PhantomRecovery::SameMask(a.cached.mask, b.cached.mask))
          return false;
      }
      return true;
    }

    bool RebuildDue(std::uint64_t now)
    {
      if (now < nextRebuildUs_) return false;
      DeferRebuild(now);
      // Permanent omissions do not become valid just because five seconds
      // elapsed. Rebuild only after their source changes or a pending read is
      // actually ready, keeping the working model throughout the wait.
      for (auto& item : omitted_)
      {
        auto* geometry = item.owner->AsGeometry();
        auto* shape    = geometry ? geometry->AsTriShape() : nullptr;
        if (!shape || !Locate(root_.get(), shape, limits_.nodes).present) continue;
        if (CheapStamp(*shape) != item.stamp) appearanceDirty_ = true;
        if (item.error.reason != P::Failure::Busy) continue;
        auto raw = engine_.mesh(*shape, limits_);
        if (!raw)
        {
          item.error = raw.error();
          continue;
        }
        P::Geometry material;
        auto        ready = Material(*shape, material, engine_, limits_);
        if (!ready)
        {
          item.error = ready.error();
          continue;
        }
        appearanceDirty_ = true;
      }
      return appearanceDirty_;
    }

    void DeferRebuild(std::uint64_t now)
    {
      nextRebuildUs_ = now + 5000000;
    }

    std::uint64_t AuditCount() const noexcept
    {
      return audits_;
    }

    P::Result<P::Snapshot> Sample(RE::PlayerCharacter& player, bool firstPerson, Stamp stamp)
    {
      if (!engine_.mainThread || !engine_.mainThread()) return A::Fail(P::Failure::Busy, "capture.main-thread");

      if (!root_ || player_.get().get() != &player) return A::Fail(P::Failure::Stale, "capture.third-person-root");
      if (player.Get3D(false) != root_.get())
      {
        auto* next = player.Get3D(false);
        auto* node = next ? next->AsNode() : nullptr;
        if (!node) return A::Fail(P::Failure::Busy, "capture.third-person-rebuilding");
        root_          = RE::NiPointer<RE::NiNode>{node};
        bindingsDirty_ = true;
      }
      auto live = Walk(*root_, limits_);
      if (!live) return std::unexpected(live.error());
      std::unordered_set<RE::NiAVObject*> present(live->begin(), live->end());
      std::unordered_set<RE::NiAVObject*> meshes;
      for (const auto& mesh : meshes_)
        meshes.insert(mesh.owner.get());
      for (const auto& omitted : omitted_)
        meshes.insert(omitted.owner.get());
      for (auto* object : *live)
      {

        if (auto* geometry = object->AsGeometry())
        {
          if (AuxiliaryGeometry(*geometry))
          {
            if (std::ranges::any_of(meshes_, [&](const auto& mesh) { return mesh.owner.get() == object; })) appearanceDirty_ = true;
            continue;
          }
          if (!meshes.contains(object) && CaptureCandidate(*root_, *geometry, firstPerson, limits_)) bindingsDirty_ = true;
        }
      }
      for (const auto& binding : bindings_)
        if (!present.contains(binding.owner.get())) bindingsDirty_ = true;
      if (bindingsDirty_)
      {
        auto rebound = Rebind(*live, firstPerson);
        if (!rebound) return std::unexpected(rebound.error());
        bindingsDirty_ = false;
      }
      P::Snapshot snapshot{stamp.generation, stamp.sequence, stamp.context, stamp.sampledAtUs, A::Value(root_->world.translate)};
      snapshot.channels.reserve(bindings_.size());
      snapshot.bounds.reserve(meshes_.size());
      std::vector<bool>                                     unavailable(bindings_.size());
      std::unordered_map<const RE::NiTransform*, P::NodeId> transforms;
      // One name table per live flattened tree, not a full scan per bone.
      std::unordered_map<RE::BSFlattenedBoneTree*, std::unordered_map<std::string_view, std::uint32_t>> boneNames;
      for (std::uint32_t i = 0; i < bindings_.size(); ++i)
      {
        auto&                  binding   = bindings_[i];
        const RE::NiTransform* transform = nullptr;
        bool                   attached  = present.contains(binding.owner.get());
        if (binding.bone && attached)
        {
          auto& tree  = *static_cast<RE::BSFlattenedBoneTree*>(binding.owner.get());
          auto  table = boneNames.find(&tree);
          if (table == boneNames.end())
          {
            const auto& data = tree.GetRuntimeData();
            if (data.numBones > limits_.nodes || (data.numBones && !data.boneEntries))
              return A::Fail(P::Failure::InvalidSkin, "bone.entries");
            std::unordered_map<std::string_view, std::uint32_t> names;
            for (std::uint32_t b = 0; b < data.numBones; ++b)
            {
              const auto* name = data.boneEntries[b].nodeName.c_str();
              if (!name || !*name || !names.emplace(name, b).second) return A::Fail(P::Failure::InvalidSkin, "bone.ambiguous-name");
            }
            table = boneNames.emplace(&tree, std::move(names)).first;
          }
          const auto found = table->second.find(binding.bone->name);
          if (found == table->second.end()) return Dirty("bone.missing-name");
          binding.bone->hint = found->second;
          const auto& bone   = tree.GetRuntimeData().boneEntries[found->second];
          transform          = &bone.world;
          if (bone.node) transforms.emplace(&bone.node->world, P::NodeId{i});
          binding.attachment = RE::NiPointer<RE::NiAVObject>{bone.node && present.contains(bone.node) ? bone.node : &tree};
        }
        else if (!binding.bone && attached)
          transform = &binding.owner->world;
        auto channel = binding.last;
        if (transform)
        {
          auto value = A::Value(*transform);
          transforms.insert_or_assign(transform, P::NodeId{i});
          if (!value)
          {
            if (binding.root) return std::unexpected(value.error());
            unavailable[i] = true;
            channel.hidden = true;
          }
          else
          {
            channel.world         = *value;
            const auto attachment = Locate(root_.get(), binding.attachment.get(), limits_.nodes);
            channel.hidden        = binding.visibility.Sample(attached, attachment.hidden, firstPerson);
          }
        }
        else
        {
          channel.hidden = true;
          unavailable[i] = true;
        }
        if (unavailable[i]) channel.world.position = snapshot.origin;
        binding.last = channel;
        snapshot.channels.push_back(channel);
      }
      std::uint64_t bytes = 64 + snapshot.channels.size() * 41 + meshes_.size() * 16;
      if (bytes > limits_.poseBytes) return A::Fail(P::Failure::LimitExceeded, "capture.pose-bytes");
      // Account the complete dynamic shape before decoding a single mesh.
      // Hidden/detached channels still have a full independent pose payload.
      for (const auto& binding : meshes_)
        if (binding.dynamic) bytes += 12 + std::uint64_t(binding.vertexCount) * 24;
      if (bytes > limits_.poseBytes) return A::Fail(P::Failure::LimitExceeded, "capture.dynamic-pose-bytes");
      for (std::uint32_t i = 0; i < meshes_.size(); ++i)
      {
        auto& binding = meshes_[i];
        if (AllPartitionsHidden(*binding.owner, limits_) || AuxiliaryGeometry(*binding.owner))
          snapshot.channels[binding.node.value].hidden = true;
        const bool missingTransform =
          unavailable[binding.node.value] ||
          (binding.skin && (unavailable[binding.skin->root.value] ||
                            std::ranges::any_of(binding.skin->bones, [&](const auto& bone) { return unavailable[bone.node.value]; })));
        if (missingTransform) binding.recovery.Failed({P::Failure::MissingSource, "capture.mesh-transform-unavailable"}, stamp.sampledAtUs);
        const bool visible = present.contains(binding.owner.get()) && !snapshot.channels[binding.node.value].hidden;
        if (visible && binding.recovery.Ready(stamp.sampledAtUs))
        {
          auto sample = SampleMesh(binding, transforms, snapshot, i, stamp);
          if (sample)
          {
            binding.recovery.bound = *sample;
            binding.recovery.Recovered();
          }
          else
          {
            binding.recovery.Failed(sample.error(), stamp.sampledAtUs);
          }
        }
        binding.recovery.Append(snapshot, binding.node, i, binding.dynamic);
      }
      // Round-robin audit, at most one mesh per sample; stagger initial deadlines.
      // Normal poses never copy static streams. Dirty events request Open;
      // this audit catches otherwise unannounced in-place source mutations.
      std::uint32_t audited = 0;
      for (std::size_t visited = 0; visited < meshes_.size() && audited < 1; ++visited)
      {
        auto& binding = meshes_[auditCursor_++ % meshes_.size()];
        if (!present.contains(binding.owner.get()) || snapshot.channels[binding.node.value].hidden) continue;
        if (stamp.sampledAtUs < binding.auditedAtUs || stamp.sampledAtUs - binding.auditedAtUs < 5000000) continue;
        auto checked = Audit(binding, transforms);
        if (!checked)
        {
          if (checked.error().reason == P::Failure::Busy)
          {
            --auditCursor_;  // finish before refreshing the next mesh
            break;
          }
          binding.recovery.Failed(checked.error(), stamp.sampledAtUs);
          snapshot.channels[binding.node.value].hidden = true;
        }
        binding.auditedAtUs = stamp.sampledAtUs;
        ++audited;
        ++audits_;
      }
      return snapshot;
    }

private:

    friend P::Result<Captured> Open(Engine, RE::PlayerCharacter&, bool, Stamp, const P::Limits&);

    Source(Engine engine, P::Limits limits) : engine_(engine), limits_(limits) {}

    Engine                    engine_;
    P::Limits                 limits_;
    RE::ObjectRefHandle       player_;
    RE::NiPointer<RE::NiNode> root_;
    std::vector<Binding>      bindings_;
    std::vector<MeshBinding>  meshes_;

    struct Omitted
    {
      RE::NiPointer<RE::NiAVObject> owner;
      P::Error                      error;
      std::uint64_t                 stamp{};
    };

    std::vector<Omitted> omitted_;
    std::uint64_t        nextRebuildUs_{};

    bool                                  appearanceDirty_{}, bindingsDirty_{};
    std::size_t                           auditCursor_{};
    std::uint64_t                         audits_{};
    std::chrono::steady_clock::time_point nextSlowAuditLog_{};

    std::unexpected<P::Error> Dirty(std::string field)
    {
      appearanceDirty_ = true;
      return A::Fail(P::Failure::Stale, std::move(field));
    }

    P::Result<P::Bound> SampleMesh(
      MeshBinding&                                                 binding,
      const std::unordered_map<const RE::NiTransform*, P::NodeId>& transforms,
      const P::Snapshot&                                           snapshot,
      std::uint32_t                                                i,
      Stamp                                                        stamp)
    {
      if (binding.recovery.Fault() || CheapStamp(*binding.owner) != binding.stamp)
      {
        auto checked = Audit(binding, transforms);
        if (!checked) return std::unexpected(checked.error());
        binding.stamp       = CheapStamp(*binding.owner);
        binding.auditedAtUs = stamp.sampledAtUs;
        ++audits_;
      }
      // Check live skin links, but do not reconstruct bind arrays per tick.
      if (binding.skin)
      {
        auto* instance = binding.owner->GetGeometryRuntimeData().skinInstance.get();
        if (
          !instance || !instance->skinData || !instance->rootParent || !instance->boneWorldTransforms ||
          instance->skinData->GetBoneCount() != binding.boneCount || !transforms.contains(&instance->rootParent->world) ||
          transforms.at(&instance->rootParent->world) != binding.skin->root)
          return Dirty("capture.skin-root-changed");
        for (std::uint32_t b = 0; b < binding.boneCount; ++b)
          if (
            !transforms.contains(instance->boneWorldTransforms[b]) ||
            transforms.at(instance->boneWorldTransforms[b]) != binding.skin->bones[b].node)
            return Dirty("capture.skin-link-changed");
      }
      if (binding.dynamic)
      {
        if (!engine_.deformation) return A::Fail(P::Failure::MissingSource, "engine.pose-deformation-copy");
        auto deformation = engine_.deformation(*binding.owner, binding.vertexCount, limits_);
        if (!deformation) return std::unexpected(deformation.error());
        if (
          deformation->positions.size() != binding.vertexCount ||
          (!deformation->normals.empty() && deformation->normals.size() != binding.vertexCount))
          return A::Fail(P::Failure::InvalidGeometry, "capture.dynamic-count");
        if (deformation->normals.empty())
        {
          deformation->normals.resize(binding.vertexCount);
          for (std::size_t triangle = 0; triangle < binding.cached.indices.size(); triangle += 3)
          {
            const auto a = binding.cached.indices[triangle], b = binding.cached.indices[triangle + 1],
                       c      = binding.cached.indices[triangle + 2];
            const auto normal = A::Cross(
              A::Sub(deformation->positions[b], deformation->positions[a]),
              A::Sub(deformation->positions[c], deformation->positions[a]));
            for (auto vertex : {a, b, c})
              deformation->normals[vertex] = A::Add(deformation->normals[vertex], normal);
          }
          for (auto& normal : deformation->normals)
            normal = A::Unit(normal);
        }
        for (std::uint32_t v = 0; v < binding.vertexCount; ++v)
          if (!A::Finite(deformation->positions[v]) || !A::Finite(deformation->normals[v]))
            return A::Fail(P::Failure::InvalidNumber, "capture.dynamic-values");
        deformation->geometry        = i;
        binding.recovery.deformation = *deformation;
      }
      return ConservativeBound(binding, snapshot);
    }

    P::Result<void> Audit(MeshBinding& binding, const std::unordered_map<const RE::NiTransform*, P::NodeId>& transforms)
    {
      if (!binding.auditPending && engine_.refreshMesh)
      {
        auto fresh = engine_.refreshMesh(*binding.owner);
        if (!fresh) return fresh;
        binding.auditPending = true;
      }
      const auto auditStart = std::chrono::steady_clock::now();
      auto       skin       = Skin(*binding.owner, transforms);
      if (!skin) return std::unexpected(skin.error());
      if (!SameSkin(*skin, binding.skin)) return Dirty("capture.audited-skin-changed");
      auto raw = engine_.mesh(*binding.owner, limits_);
      if (!raw) return std::unexpected(raw.error());
      auto geometry = A::Decode(*raw, binding.node, binding.boneCount, limits_);
      if (!geometry) return std::unexpected(geometry.error());
      auto material = Material(*binding.owner, *geometry, engine_, limits_);
      if (!material) return GeometryError(material.error(), *binding.owner);
      if (Signature(*geometry) != binding.signature || !PhantomRecovery::SameMask(geometry->mask, binding.cached.mask))
        return Dirty("capture.audited-appearance-changed");
      binding.auditPending = false;
      const auto auditMs   = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - auditStart).count();
      if (auditMs >= 8 && auditStart >= nextSlowAuditLog_)
      {
        nextSlowAuditLog_ = auditStart + std::chrono::seconds(5);
        logger::info("Phantom slow mesh audit: {} vertices={}, {:.2f} ms", binding.name, binding.vertexCount, auditMs);
      }
      return {};
    }

    P::Result<void> Rebind(std::span<RE::NiAVObject* const> live, bool firstPerson)
    {
      // Match independently of parent attachment and child-array order.
      // Duplicate semantic keys are rejected, never arbitrarily rebound.
      std::vector<RE::NiPointer<RE::NiAVObject>> next(bindings_.size());
      std::unordered_set<RE::NiAVObject*>        used;
      for (std::uint32_t i = 0; i < bindings_.size(); ++i)
      {
        const auto& binding = bindings_[i];
        if (binding.root)
        {
          next[i] = root_;
          continue;
        }
        for (auto* object : live)
          if (binding.name == (object->name.c_str() ? object->name.c_str() : "") && A::Kind(*object, binding.type))
          {
            if (object->AsGeometry()) continue;  // matched by content below
            if (next[i] && next[i].get() != object) return Dirty("capture.ambiguous-semantic-node");
            next[i] = RE::NiPointer<RE::NiAVObject>{object};
          }
      }
      for (auto& binding : meshes_)
      {
        RE::NiPointer<RE::BSTriShape> found;
        double                        nearest = std::numeric_limits<double>::max();
        // A moved existing object needs no readback. Full signature matching
        // is reserved for replacement clones, not ordinary draw/sheath.
        const bool originalPresent = std::ranges::find(live, binding.owner.get()) != live.end();
        // Prefer a visible equivalent over a hidden attachment clone. Otherwise
        // draw/sheath would retain the hidden original and misclassify its
        // visible twin as a new appearance.
        const bool reusable =
          originalPresent && !used.contains(binding.owner.get()) && CaptureCandidate(*root_, *binding.owner, firstPerson, limits_);
        if (reusable) found = binding.owner;
        for (auto* object : live)
        {
          if (reusable) break;
          auto* geometry = object->AsGeometry();
          auto* shape    = geometry ? geometry->AsTriShape() : nullptr;
          if (
            !shape || !CaptureCandidate(*root_, *geometry, firstPerson, limits_) || used.contains(shape) ||
            binding.name != (shape->name.c_str() ? shape->name.c_str() : ""))
            continue;
          auto raw = engine_.mesh(*shape, limits_);
          if (!raw) continue;
          auto decoded = A::Decode(*raw, binding.node, binding.boneCount, limits_);
          if (!decoded) continue;
          auto material = Material(*shape, *decoded, engine_, limits_);
          if (!material) continue;
          if (Signature(*decoded) != binding.signature || !PhantomRecovery::SameMask(decoded->mask, binding.cached.mask)) continue;
          // Identical dual-wield meshes are interchangeable appearance
          // slots. Keep their pose continuity by nearest last world position;
          // unlike a parent path, this cannot alter the published asset.
          const auto   delta    = A::Sub(A::Value(shape->world.translate), bindings_[binding.node.value].last.world.position);
          const double distance = double(delta.x) * delta.x + double(delta.y) * delta.y + double(delta.z) * delta.z;
          if (distance < nearest)
          {
            nearest = distance;
            found   = RE::NiPointer<RE::BSTriShape>{shape};
          }
        }
        // No visible replacement: keep the valid cached hidden mesh. It must
        // not require decoding an inactive buffer just to preserve its slot.
        if (!found && originalPresent && !used.contains(binding.owner.get()) && !AuxiliaryGeometry(*binding.owner)) found = binding.owner;
        if (!found)
        {
          appearanceDirty_ = true;
          binding.recovery.Failed({P::Failure::Stale, "capture.equipment/schema-changed"}, 0);
          next[binding.node.value] = binding.owner;
          continue;
        }
        next[binding.node.value] = found;
        used.insert(found.get());
        const bool replaced = found.get() != binding.owner.get();
        binding.owner       = std::move(found);
        // An original hidden mesh has not been reread. Preserve its old stamp
        // so the next visible sample audits any changes made while hidden.
        if (replaced) binding.stamp = CheapStamp(*binding.owner);
      }
      for (auto* object : live)
        if (
          auto* geometry = object->AsGeometry();
          geometry && CaptureCandidate(*root_, *geometry, firstPerson, limits_) && !used.contains(object))
          if (!std::ranges::any_of(omitted_, [&](const auto& item) { return item.owner.get() == object; })) appearanceDirty_ = true;
      for (std::uint32_t i = 0; i < bindings_.size(); ++i)
      {
        if (!next[i]) return Dirty("capture.missing-semantic-node");
        bindings_[i].owner      = next[i];
        bindings_[i].attachment = next[i];
      }
      return {};
    }

    static P::Result<P::Bound> ConservativeBound(const MeshBinding& binding, const P::Snapshot& snapshot)
    {
      const auto world        = A::Native(snapshot.channels[binding.node.value].world);
      float      displacement = 0;
      if (binding.dynamic)
        for (std::size_t i = 0; i < binding.cached.vertices.size(); ++i)
        {
          const auto delta = A::Sub(binding.recovery.deformation.positions[i], binding.cached.vertices[i].position);
          displacement     = std::max(displacement, std::sqrt(A::Dot(delta, delta)));
        }
      P::Bound out;
      if (!binding.skin)
        out = {A::Value(world * A::Native(binding.localBound.center)), (binding.localBound.radius + displacement) * world.scale};
      else
      {
        const auto& skin     = *binding.skin;
        const auto  intoSkin = A::Native(skin.worldToSkin) * A::Native(snapshot.channels[skin.root.value].world).Invert();
        for (std::size_t b = 0; b < skin.bones.size(); ++b)
        {
          const auto  transform = intoSkin * A::Native(snapshot.channels[skin.bones[b].node.value].world);
          const auto& bound     = binding.boneBounds[b];
          A::Enclose(
            out,
            {A::Value(transform * A::Native(bound.center)), (bound.radius + displacement * skin.bones[b].bind.scale) * transform.scale});
        }
      }
      if (binding.skin)
      {
        out = Dreamsleeve::Game::PhantomMesh::WeightedBound(out, binding.minimumWeight, binding.maximumWeight);
        out = {A::Value(world * A::Native(out.center)), out.radius * world.scale};
      }
      if (!A::Finite(out)) return A::Fail(P::Failure::InvalidNumber, "capture.conservative-bound");
      return out;
    }

    P::Result<void> Canonicalize(P::Asset& asset)
    {
      std::vector<bool> needed(bindings_.size());
      needed[0] = true;
      for (const auto& mesh : meshes_)
      {
        needed[mesh.node.value] = true;
        if (mesh.skin)
        {
          needed[mesh.skin->root.value] = true;
          for (const auto& bone : mesh.skin->bones)
            needed[bone.node.value] = true;
        }
      }
      std::vector<std::pair<std::string, std::uint32_t>> keys;
      for (std::uint32_t i = 1; i < bindings_.size(); ++i)
        if (needed[i])
        {
          const auto& binding = bindings_[i];
          std::string key = binding.bone ? "bone:" + binding.name + ":" + binding.bone->name : "node:" + binding.type + ":" + binding.name;
          for (const auto& mesh : meshes_)
            if (mesh.node.value == i)
            {
              key = "mesh:" + mesh.name + ":" + std::to_string(mesh.signature);
              if (mesh.skin)
              {
                Fingerprint skinHash;
                const auto  semantic = [&](P::NodeId id) {
                  const auto& bone = bindings_[id.value];
                  for (auto c : bone.name)
                    skinHash.Scalar(c);
                  skinHash.Scalar(std::uint8_t(0));
                  if (bone.bone)
                    for (auto c : bone.bone->name)
                      skinHash.Scalar(c);
                  skinHash.Scalar(std::uint8_t(0));
                };
                semantic(mesh.skin->root);
                skinHash.Transform(mesh.skin->worldToSkin);
                for (const auto& bone : mesh.skin->bones)
                {
                  semantic(bone.node);
                  skinHash.Transform(bone.bind);
                  skinHash.Vector(bone.bound.center);
                  skinHash.Scalar(bone.bound.radius);
                }
                key += ":" + std::to_string(skinHash.value);
              }
            }
          keys.emplace_back(std::move(key), i);
        }
      std::ranges::sort(keys);
      for (std::size_t i = 1; i < keys.size(); ++i)
        if (keys[i - 1].first == keys[i].first && !keys[i].first.starts_with("mesh:"))
          return A::Fail(P::Failure::UnsupportedGeometry, "capture.ambiguous-channel-key");
      std::vector<std::uint32_t> remap(bindings_.size(), P::NoNode);
      std::vector<Binding>       sorted;
      sorted.reserve(keys.size() + 1);
      sorted.push_back(std::move(bindings_[0]));
      remap[0]       = 0;
      sorted[0].root = true;
      for (const auto& [key, old] : keys)
      {
        remap[old] = static_cast<std::uint32_t>(sorted.size());
        sorted.push_back(std::move(bindings_[old]));
      }
      bindings_ = std::move(sorted);
      asset.nodes.assign(bindings_.size(), P::Node{P::NodeId{0}, {}});
      asset.nodes[0].parent = {P::NoNode};
      for (auto& mesh : meshes_)
      {
        mesh.node        = {remap[mesh.node.value]};
        mesh.cached.node = mesh.node;
        if (mesh.skin)
        {
          mesh.skin->root = {remap[mesh.skin->root.value]};
          for (auto& bone : mesh.skin->bones)
            bone.node = {remap[bone.node.value]};
          mesh.cached.skin = mesh.skin;
        }
      }
      std::ranges::sort(meshes_, [](const auto& a, const auto& b) { return a.node.value < b.node.value; });
      asset.geometry.clear();
      asset.geometry.reserve(meshes_.size());
      for (std::uint32_t i = 0; i < meshes_.size(); ++i)
      {
        auto& mesh                         = meshes_[i];
        mesh.recovery.deformation.geometry = i;
        asset.geometry.push_back(mesh.cached);
        // Dynamic positions/normals are completely supplied by each pose.
        // Expression/SMP motion must never become appearance content.
        if (mesh.dynamic)
          for (auto& vertex : asset.geometry.back().vertices)
          {
            vertex.position = {};
            vertex.normal   = {0, 0, 1};
            vertex.tangent  = {1, 0, 0};
          }
      }
      return {};
    }

    P::Result<std::optional<P::Skin>> Skin(
      RE::BSTriShape&                                              geometry,
      const std::unordered_map<const RE::NiTransform*, P::NodeId>& transforms)
    {
      auto* instance = geometry.GetGeometryRuntimeData().skinInstance.get();
      if (!instance) return std::optional<P::Skin>{};
      auto* data = instance->skinData.get();
      if (!data || !instance->rootParent || !transforms.contains(&instance->rootParent->world))
        return A::Fail(P::Failure::InvalidSkin, "skin.root");
      const auto count = data->GetBoneCount();
      if (!count || count > limits_.bonesPerSkin || !instance->boneWorldTransforms || !data->GetBoneDataAddress(0))
        return A::Fail(P::Failure::InvalidSkin, "skin.bone-data");
      auto bind = A::Value(data->rootParentToSkin);
      if (!bind) return std::unexpected(bind.error());
      P::Skin skin{transforms.at(&instance->rootParent->world), *bind, {}};
      skin.bones.reserve(count);
      for (std::uint32_t b = 0; b < count; ++b)
      {
        // The authoritative link can be a flattened entry, with no NiAVObject
        // in instance->bones[b]. Never dereference that array as a fallback.
        const auto* world = instance->boneWorldTransforms[b];
        if (!world || !transforms.contains(world)) return A::Fail(P::Failure::InvalidSkin, "skin.unresolved-bone-world");
        auto boneBind = A::Value(data->GetBoneDataSkinToBone(b));
        if (!boneBind) return std::unexpected(boneBind.error());
        const auto& bound = data->GetBoneDataBound(b);
        if (!A::Finite(A::Value(bound.center)) || !std::isfinite(bound.radius) || bound.radius < 0)
          return A::Fail(P::Failure::InvalidSkin, "skin.bone-bound");
        skin.bones.push_back({
            transforms.at(world),
            *boneBind,
            {A::Value(bound.center), bound.radius}
        });
      }
      return std::optional<P::Skin>{std::move(skin)};
    }

    static bool SameSkin(const std::optional<P::Skin>& a, const std::optional<P::Skin>& b)
    {
      if (a.has_value() != b.has_value()) return false;
      if (!a) return true;
      if (a->root != b->root || a->worldToSkin != b->worldToSkin || a->bones.size() != b->bones.size()) return false;
      for (std::size_t i = 0; i < a->bones.size(); ++i)
        if (a->bones[i].node != b->bones[i].node || a->bones[i].bind != b->bones[i].bind || a->bones[i].bound != b->bones[i].bound)
          return false;
      return true;
    }
  };

  inline P::Result<Captured> Open(Engine engine, RE::PlayerCharacter& player, bool firstPerson, Stamp stamp, const P::Limits& limits)
  {
    if (!engine.mainThread || !engine.mainThread()) return A::Fail(P::Failure::Busy, "capture.main-thread");
    if (!engine.mesh) return A::Fail(P::Failure::MissingSource, "engine.current-mesh-copy");
    auto* playerModel = player.Get3D(false);
    auto* root        = playerModel ? playerModel->AsNode() : nullptr;
    if (!root) return A::Fail(P::Failure::MissingSource, "capture.third-person-root");
    auto live = Walk(*root, limits);
    if (!live) return std::unexpected(live.error());
    ReportMaterials(*root, *live, firstPerson, limits);
    Captured out;
    out.source     = std::unique_ptr<Source>(new Source(engine, limits));
    auto& source   = *out.source;
    source.player_ = player.GetHandle();
    source.root_   = RE::NiPointer<RE::NiNode>{root};
    source.DeferRebuild(stamp.sampledAtUs);
    std::unordered_map<RE::NiAVObject*, P::NodeId>        ids;
    std::unordered_map<const RE::NiTransform*, P::NodeId> transforms;
    source.bindings_.reserve(live->size());
    out.asset.nodes.reserve(live->size());
    for (auto* node : *live)
    {
      const P::NodeId id{static_cast<std::uint32_t>(out.asset.nodes.size())};
      ids.emplace(node, id);
      transforms.emplace(&node->world, id);
      // World channels completely describe the live pose. Identity locals
      // prevent idle animations/weapon positions entering appearance content.
      out.asset.nodes.push_back({node == root ? P::NodeId{P::NoNode} : ids.at(node->parent), {}});
      Binding binding;
      binding.owner      = RE::NiPointer<RE::NiAVObject>{node};
      binding.attachment = binding.owner;
      binding.name       = node->name.c_str() ? node->name.c_str() : "";
      binding.type       = node->GetRTTI()->GetName();
      binding.root       = node == root;
      if (binding.name.size() > 1024 || binding.type.size() > 1024) return A::Fail(P::Failure::LimitExceeded, "capture.semantic-name");
      source.bindings_.push_back(std::move(binding));
    }
    for (auto* object : *live)
      if (A::Kind(*object, "BSFlattenedBoneTree"))
      {
        auto& tree = *static_cast<RE::BSFlattenedBoneTree*>(object);
        auto& data = tree.GetRuntimeData();
        if (data.numBones > limits.nodes || (data.numBones && !data.boneEntries)) return A::Fail(P::Failure::InvalidSkin, "bone.entries");
        std::vector<P::NodeId>          boneIds(data.numBones);
        std::unordered_set<std::string> names;
        std::vector<bool>               synthetic(data.numBones);
        for (std::uint32_t b = 0; b < data.numBones; ++b)
        {
          const auto&       bone = data.boneEntries[b];
          const std::string name = bone.nodeName.c_str() ? bone.nodeName.c_str() : "";
          if (name.empty() || name.size() > 1024 || !names.emplace(name).second)
            return A::Fail(P::Failure::InvalidSkin, "bone.empty/duplicate-name");
          P::NodeId id;
          if (bone.node && ids.contains(bone.node))
            id = ids.at(bone.node);
          else
          {
            if (out.asset.nodes.size() >= limits.nodes) return A::Fail(P::Failure::LimitExceeded, "bone.channels");
            id           = {static_cast<std::uint32_t>(out.asset.nodes.size())};
            synthetic[b] = true;
            out.asset.nodes.push_back({ids.at(object), {}});
            source.bindings_.push_back({});
          }
          auto& binding = source.bindings_[id.value];
          if (binding.bone) return A::Fail(P::Failure::InvalidSkin, "bone.duplicate-node");
          binding.owner      = RE::NiPointer<RE::NiAVObject>{object};
          binding.name       = object->name.c_str() ? object->name.c_str() : "";
          binding.type       = "BSFlattenedBoneTree";
          binding.attachment = RE::NiPointer<RE::NiAVObject>{bone.node && ids.contains(bone.node) ? bone.node : object};
          binding.bone       = BoneBinding{name, b};
          boneIds[b]         = id;
          transforms.insert_or_assign(&bone.world, id);
          if (bone.node) transforms.insert_or_assign(&bone.node->world, id);
        }
        for (std::uint32_t b = 0; b < data.numBones; ++b)
        {
          const auto parent = data.boneEntries[b].parentIndex;
          if (parent < -1 || parent >= static_cast<std::int64_t>(data.numBones) || parent == static_cast<std::int64_t>(b))
            return A::Fail(P::Failure::InvalidLink, "bone.parent-index");
          if (synthetic[b] && parent >= 0) out.asset.nodes[boneIds[b].value].parent = boneIds[static_cast<std::uint32_t>(parent)];
          // Fail cycles before returning an asset, independently of its parser.
          auto next = parent;
          for (std::uint32_t depth = 0; next >= 0; ++depth)
          {
            if (depth >= data.numBones || next >= static_cast<std::int64_t>(data.numBones))
              return A::Fail(P::Failure::InvalidLink, "bone.parent-cycle");
            next = data.boneEntries[static_cast<std::uint32_t>(next)].parentIndex;
          }
        }
      }
    std::uint64_t    bytes = out.asset.nodes.size() * 48, vertices = 0, poseBytes = 64 + out.asset.nodes.size() * 41;
    P::AlphaMaskPool masks(limits);
    for (auto* object : *live)
    {
      if (object->AsNiTriShape() && CaptureCandidate(*root, *object, firstPerson, limits))
      {
        source.omitted_.push_back({
            RE::NiPointer<RE::NiAVObject>{object},
            {P::Failure::UnsupportedGeometry, "source.legacy-NiTriShape"}
        });
        continue;
      }
      auto* geometry = object->AsGeometry();
      if (!geometry) continue;
      if (!CaptureCandidate(*root, *geometry, firstPerson, limits)) continue;
      auto captured = [&]() -> P::Result<MeshBinding> {
        auto* shape = geometry->AsTriShape();
        if (!shape) return GeometryError({P::Failure::UnsupportedGeometry, "source.nontriangle-geometry"}, *geometry);
        if (out.asset.geometry.size() >= limits.geometry) return A::Fail(P::Failure::LimitExceeded, "capture.geometry");
        auto skin = source.Skin(*shape, transforms);
        if (!skin) return GeometryError(skin.error(), *geometry);
        const auto bones = *skin ? static_cast<std::uint32_t>((*skin)->bones.size()) : 0U;
        auto       raw   = engine.mesh(*shape, limits);
        if (!raw) return GeometryError(raw.error(), *geometry);
        auto normalized = A::Decode(*raw, ids.at(object), bones, limits);
        if (!normalized) return GeometryError(normalized.error(), *geometry);
        normalized->skin = std::move(*skin);
        auto material    = Material(*shape, *normalized, engine, limits);
        if (!material) return GeometryError(material.error(), *geometry);
        auto valid = P::ValidateGeometry(*normalized, out.asset.nodes.size(), limits);
        if (!valid) return GeometryError(valid.error(), *geometry);
        const auto signature     = Signature(*normalized);
        const auto nextVertices  = vertices + normalized->vertices.size();
        const auto nextBytes     = bytes + P::GeometryBytes(*normalized);
        const auto nextPoseBytes = poseBytes + 16 + (normalized->dynamic ? 12 + normalized->vertices.size() * 24 : 0);
        if (nextPoseBytes > limits.poseBytes) return A::Fail(P::Failure::LimitExceeded, "capture.mesh-pose-budget");
        if (nextVertices > limits.vertices || nextBytes > limits.assetBytes)
          return A::Fail(P::Failure::LimitExceeded, "capture.asset-budget");
        if (normalized->mask)
        {
          auto shared = masks.Intern(normalized->mask);
          if (!shared) return GeometryError(shared.error(), *geometry);
          normalized->mask = *shared;
        }
        vertices  = nextVertices;
        bytes     = nextBytes;
        poseBytes = nextPoseBytes;
        MeshBinding binding;
        binding.owner                         = RE::NiPointer<RE::BSTriShape>{shape};
        binding.node                          = ids.at(object);
        binding.signature                     = signature;
        binding.vertexCount                   = raw->vertexCount;
        binding.boneCount                     = bones;
        binding.skin                          = normalized->skin;
        binding.dynamic                       = normalized->dynamic;
        binding.cached                        = *normalized;
        binding.name                          = shape->name.c_str() ? shape->name.c_str() : "";
        binding.stamp                         = CheapStamp(*shape);
        binding.auditedAtUs                   = stamp.sampledAtUs + source.meshes_.size() * 50000;
        binding.recovery.deformation.geometry = static_cast<std::uint32_t>(source.meshes_.size());
        for (const auto& vertex : normalized->vertices)
        {
          // Tiny positive-radius points ensure coincident/empty engine bone
          // bounds do not disappear from the conservative union.
          A::Enclose(binding.localBound, {vertex.position, 0.01f});
          const auto sum        = std::accumulate(vertex.weights.begin(), vertex.weights.end(), 0.f);
          binding.minimumWeight = std::min(binding.minimumWeight, sum);
          binding.maximumWeight = std::max(binding.maximumWeight, sum);
          if (binding.dynamic)
          {
            binding.recovery.deformation.positions.push_back(vertex.position);
            binding.recovery.deformation.normals.push_back(vertex.normal);
          }
        }
        if (binding.skin)
        {
          binding.boneBounds.reserve(bones);
          for (const auto& bone : binding.skin->bones)
            binding.boneBounds.push_back(bone.bound);
          for (const auto& vertex : normalized->vertices)
            for (unsigned b = 0; b < 4; ++b)
              if (vertex.weights[b] > 0)
              {
                const auto bone = vertex.bones[b];
                const auto bind = A::Native(binding.skin->bones[bone].bind);
                A::Enclose(binding.boneBounds[bone], {A::Value(bind * A::Native(vertex.position)), 0.01f});
              }
        }
        return binding;
      }();
      if (!captured)
      {
        source.omitted_.push_back(
          {RE::NiPointer<RE::NiAVObject>{object}, captured.error(), geometry->AsTriShape() ? CheapStamp(*geometry->AsTriShape()) : 0});
        continue;
      }
      out.asset.geometry.push_back(captured->cached);
      source.meshes_.push_back(std::move(*captured));
    }
    if (out.asset.geometry.empty())
      return A::Fail(
        P::Failure::MissingSource,
        "capture.no-model-geometry" + (source.omitted_.empty() ? std::string{} : ": " + source.omitted_.front().error.field));
    auto canonical = source.Canonicalize(out.asset);
    if (!canonical) return std::unexpected(canonical.error());
    auto snapshot = source.Sample(player, firstPerson, stamp);
    if (!snapshot) return std::unexpected(snapshot.error());
    // Core validates the asset and this atomic pose at the native boundary;
    // capture only guards live-engine reads/allocation before producing them.
    out.initial = std::move(*snapshot);
    return out;
  }

}
