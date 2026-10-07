module;
#include "Prelude.hpp"
export module Dreamsleeve.Game.PhantomNative;
import std;
import Dreamsleeve.Client.Phantom.Types;
import Dreamsleeve.Game.PhantomMath;
import Dreamsleeve.Game.PhantomCaptureRules;

export namespace Dreamsleeve::Game::PhantomNative
{
  namespace P                    = Dreamsleeve::Client::Phantom;
  namespace A                    = Dreamsleeve::Game::PhantomMath;
  using Flag                     = RE::NiAVObject::Flag;
  constexpr std::size_t MaxNodes = P::Limits{}.nodes;

  struct Engine
  {
    bool                                 (*mainThread)() noexcept {};
    P::Result<std::vector<std::uint8_t>> (*save)(RE::NiNode*){};
    P::Result<RE::NiPointer<RE::NiNode>> (*load)(const P::ValidatedAsset&){};
    RE::NiAlphaProperty*                 (*alpha)(){};
    RE::BSLightingShaderProperty*        (*lighting)(){};
    void                                 (*normalizeBones)(RE::BSFlattenedBoneTree&){};
  };
  enum class Transform
  {
    World,
    Local,
    BoneWorld,
    BoneLocal
  };

  struct Binding
  {
    RE::NiPointer<RE::NiAVObject>                 owner;
    Transform                                     kind{};
    RE::BSFixedString                             boneName;
    std::uint32_t                                 boneIndex{};
    bool                                          unavailable{};
    Dreamsleeve::Game::PhantomCapture::Visibility visibility;
  };

  inline void Collect(RE::NiAVObject* object, std::vector<RE::NiAVObject*>& nodes)
  {
    std::unordered_set<RE::NiAVObject*> seen;
    std::vector<RE::NiAVObject*>        pending;
    if (object) pending.push_back(object);
    while (!pending.empty())
    {
      auto* current = pending.back();
      pending.pop_back();
      if (nodes.size() >= MaxNodes || !seen.insert(current).second) throw std::runtime_error("native tree limit/cycle");
      nodes.push_back(current);
      if (auto* node = current->AsNode())
        for (const auto& child : node->GetChildren() | std::views::reverse)
          if (child) pending.push_back(child.get());
    }
  }

  void Pair(
    RE::NiAVObject*                                       source,
    RE::NiAVObject*                                       clone,
    std::unordered_map<RE::NiAVObject*, RE::NiAVObject*>& pairs,
    std::unordered_map<const RE::NiTransform*, Binding>&  transforms)
  {
    pairs.emplace(source, clone);
    transforms.emplace(&source->world, Binding{RE::NiPointer<RE::NiAVObject>{source}, Transform::World});
    transforms.emplace(&source->local, Binding{RE::NiPointer<RE::NiAVObject>{source}, Transform::Local});
    if (auto* tree = netimmerse_cast<RE::BSFlattenedBoneTree*>(source))
    {
      const auto& data = tree->GetRuntimeData();
      if (data.numBones > MaxNodes) throw std::runtime_error("Слишком много костей");
      for (std::uint32_t i = 0; data.boneEntries && i < data.numBones; ++i)
      {
        const auto& bone = data.boneEntries[i];
        transforms.emplace(&bone.world, Binding{RE::NiPointer<RE::NiAVObject>{source}, Transform::BoneWorld, bone.nodeName, i});
        transforms.emplace(&bone.local, Binding{RE::NiPointer<RE::NiAVObject>{source}, Transform::BoneLocal, bone.nodeName, i});
      }
    }
    auto* a = source->AsNode();
    auto* b = clone->AsNode();
    if (!a || !b) return;
    auto&       children = b->GetChildren();
    std::size_t next     = 0;
    for (const auto& child : a->GetChildren())
    {
      if (!child) continue;
      for (auto i = next; i < children.capacity(); ++i)
        if (
          children[static_cast<std::uint16_t>(i)] && children[static_cast<std::uint16_t>(i)]->name == child->name &&
          children[static_cast<std::uint16_t>(i)]->GetRTTI() == child->GetRTTI())
        {
          Pair(child.get(), children[static_cast<std::uint16_t>(i)].get(), pairs, transforms);
          next = i + 1;
          break;
        }
    }
  }

  RE::NiPointer<RE::NiNode> StreamTree(RE::NiNode* root, std::unordered_map<RE::NiAVObject*, Binding>& channels)
  {
    // Actor-only container classes have Clone constructors but no NiStream
    // loaders. Keep the cloned geometry and rebuild its container hierarchy
    // with ordinary NiNodes; preserve pose channels and every skin link.
    std::vector<RE::NiAVObject*> old;
    Collect(root, old);
    std::unordered_map<RE::NiAVObject*, RE::NiPointer<RE::NiAVObject>> mapped;
    std::unordered_map<RE::NiAVObject*, Binding>                       nextChannels;
    std::vector<std::pair<RE::NiNode*, RE::NiAVObject*>>               edges;
    std::map<std::string, std::size_t>                                 containers;
    std::size_t                                                        geometryCount = 0;
    for (auto* object : old)
    {
      if (!channels.contains(object) || mapped.contains(object)) throw std::runtime_error("Неполное дерево поз персонажа");
      RE::NiPointer<RE::NiAVObject> replacement{object};
      if (auto* node = object->AsNode())
      {
        replacement.reset(RE::NiNode::Create(0));
        if (!replacement) throw std::bad_alloc{};
        replacement->name            = object->name;
        replacement->local           = object->local;
        replacement->world           = object->world;
        replacement->previousWorld   = object->previousWorld;
        replacement->worldBound      = object->worldBound;
        replacement->GetFlags()      = object->GetFlags();
        replacement->GetFadeAmount() = object->GetFadeAmount();
        ++containers[object->GetRTTI()->GetName()];
        // Snapshot edges before moving geometry mutates the old child arrays.
        for (const auto& child : node->GetChildren())
          if (child) edges.emplace_back(node, child.get());
      }
      if (object->AsGeometry()) ++geometryCount;
      nextChannels.emplace(replacement.get(), channels.at(object));
      mapped.emplace(object, std::move(replacement));
    }
    std::unordered_set<RE::NiSkinInstance*> remappedSkins;
    for (auto* object : old)
      if (auto* geometry = object->AsGeometry())
        if (auto* skin = geometry->GetGeometryRuntimeData().skinInstance.get())
        {
          if (!remappedSkins.emplace(skin).second) continue;
          if (!skin->skinData || !mapped.contains(skin->rootParent)) throw std::runtime_error("Корень скина вне дерева фантома");
          const auto count = skin->skinData->GetBoneCount();
          if (count > MaxNodes || (count && (!skin->bones || !skin->boneWorldTransforms)))
            throw std::runtime_error("Некорректные связи скина фантома");
          for (std::uint32_t b = 0; b < count; ++b)
            if (!mapped.contains(skin->bones[b])) throw std::runtime_error("Кость скина вне дерева фантома");
          skin->rootParent = mapped.at(skin->rootParent).get();
          for (std::uint32_t b = 0; b < count; ++b)
          {
            skin->bones[b]               = mapped.at(skin->bones[b]).get();
            skin->boneWorldTransforms[b] = &skin->bones[b]->world;
          }
        }
    for (const auto& [parent, child] : edges)
    {
      if (child->parent != parent) throw std::runtime_error("Некорректный родитель узла фантома");
      auto* replacement = mapped.at(child).get();
      if (replacement == child) parent->DetachChild(child);
      mapped.at(parent)->AsNode()->AttachChild(replacement, false);
    }
    for (const auto& [name, count] : containers)
      logger::info("[Phantom] stream containers: {} x{} -> NiNode", name, count);
    logger::info("[Phantom] stream tree retains {} geometry objects and {} pose channels", geometryCount, nextChannels.size());
    channels = std::move(nextChannels);
    return RE::NiPointer<RE::NiNode>{mapped.at(root)->AsNode()};
  }

  struct Look
  {
    P::Vec3 color{0.55f, 0.8f, 1};
    float   opacity{0.6f};
  };

  inline void ApplyLook(RE::BSLightingShaderProperty& lit, Look look)
  {
    auto* material          = static_cast<RE::BSLightingShaderMaterialBase*>(lit.material);
    lit.alpha               = 1;
    material->materialAlpha = look.opacity;
    material->rimLightPower = 3.4f;
    lit.emissiveMult        = 1.5f;
    if (lit.emissiveColor) *lit.emissiveColor = {look.color.x, look.color.y, look.color.z};
  }

  inline bool Auxiliary(RE::NiAVObject& object)
  {
    auto* geometry = object.AsGeometry();
    if (!geometry)
    {
      if (object.AsNode()) return false;
      if (netimmerse_cast<RE::NiLight*>(&object) || netimmerse_cast<RE::NiParticleSystem*>(&object)) return true;
      // Unknown visible legacy/mod geometry must not silently disappear.
      throw std::runtime_error("unsupported native scene object");
    }
    const auto&                                data = geometry->GetGeometryRuntimeData();
    auto*                                      lit  = netimmerse_cast<RE::BSLightingShaderProperty*>(data.shaderProperty.get());
    const std::string_view                     name = object.name.c_str() ? object.name.c_str() : "";
    Dreamsleeve::Game::PhantomCapture::Surface surface;
    surface.name           = name;
    surface.hasShader      = data.shaderProperty != nullptr;
    surface.skinned        = data.skinInstance != nullptr;
    auto* effect           = netimmerse_cast<RE::BSEffectShaderProperty*>(data.shaderProperty.get());
    surface.effectMaterial = effect != nullptr;
    if (lit && lit->material)
    {
      surface.shaderAlpha    = lit->alpha;
      surface.materialAlpha  = static_cast<RE::BSLightingShaderMaterialBase*>(lit->material)->materialAlpha;
      surface.dedicatedDecal = lit->flags.all(RE::BSShaderProperty::EShaderPropertyFlag::kWeaponBlood);
      surface.decalMaterial  = lit->flags.all(RE::BSShaderProperty::EShaderPropertyFlag::kDecal);
    }
    if (effect && effect->GetMaterial()) surface.materialAlpha = effect->GetMaterial()->baseColor.alpha;
    if (surface.Auxiliary()) return true;
    // Skinned effect geometry can be part of clothing/body accessories. Keep
    // its native mesh/skin and replace only the detached clone's shader.
    if ((!lit || !lit->material) && (!effect || !effect->GetMaterial()))
      throw std::runtime_error(
        std::format(
          "unsupported visible native material: {} (shader={}, skinned={})",
          name,
          data.shaderProperty ? data.shaderProperty->GetRTTI()->GetName() : "none",
          surface.skinned));
    return false;
  }

  // Called only on the detached clone or a freshly loaded validated scene.
  // Keep native material subclasses/skin geometry; remove all author texture
  // references before Save and install the prototype's local white ghost look.
  inline void Ghostify(RE::BSGeometry& geometry, const Engine& engine, Look look)
  {
    auto& data = geometry.GetGeometryRuntimeData();
    auto* lit  = netimmerse_cast<RE::BSLightingShaderProperty*>(data.shaderProperty.get());
    if (!lit || !lit->material) throw std::runtime_error("native lighting material missing");
    lit->RemoveAllExtraData();
    lit->controllers.reset();
    auto* base      = static_cast<RE::BSLightingShaderMaterialBase*>(lit->material);
    auto* temporary = static_cast<RE::BSLightingShaderMaterialBase*>(base->Create());
    if (!temporary) throw std::bad_alloc{};
    temporary->CopyMembers(base);
    temporary->ClearTextures();
    RE::NiPointer<RE::BSTextureSet> textures{RE::BSShaderTextureSet::Create()};
    if (!textures)
    {
      temporary->~BSLightingShaderMaterialBase();
      RE::free(temporary);
      throw std::bad_alloc{};
    }
    temporary->SetTextureSet(textures);
    lit->SetMaterial(temporary, true);
    temporary->~BSLightingShaderMaterialBase();
    RE::free(temporary);
    auto*                        material = static_cast<RE::BSLightingShaderMaterialBase*>(lit->material);
    RE::NiPointer<RE::NiTexture> white;
    RE::BSShaderManager::GetTexture("textures\\effects\\fxwhite.dds", true, white, false);
    if (!white) throw std::runtime_error("required local ghost texture fxwhite.dds missing");
    material->diffuseTexture = RE::NiPointer<RE::NiSourceTexture>{static_cast<RE::NiSourceTexture*>(white.get())};
    using Shader             = RE::BSShaderProperty::EShaderPropertyFlag;
    lit->flags.reset(Shader::kCastShadows, Shader::kReceiveShadows, Shader::kSpecular);
    lit->flags.set(Shader::kZBufferTest, Shader::kZBufferWrite, Shader::kNoFade, Shader::kRimLighting);
    if (!lit->flags.all(Shader::kOwnEmit) || !lit->emissiveColor)
    {
      auto* color = RE::malloc<RE::NiColor>();
      if (!color) throw std::bad_alloc{};
      lit->emissiveColor = new (color) RE::NiColor(look.color.x, look.color.y, look.color.z);
    }
    lit->flags.set(Shader::kOwnEmit);
    ApplyLook(*lit, look);
    RE::NiPointer<RE::NiAlphaProperty> alpha{engine.alpha()};
    if (!alpha) throw std::bad_alloc{};
    if (data.alphaProperty)
    {
      alpha->alphaFlags     = data.alphaProperty->alphaFlags;
      alpha->alphaThreshold = data.alphaProperty->alphaThreshold;
    }
    else
      alpha->SetAlphaTesting(false);
    alpha->SetAlphaBlending(true);
    alpha->SetSrcBlendMode(RE::NiAlphaProperty::AlphaFunction::kSrcAlpha);
    alpha->SetDestBlendMode(RE::NiAlphaProperty::AlphaFunction::kOne);
    data.alphaProperty = alpha;
    lit->SetupGeometry(&geometry);
    lit->FinishSetupGeometry(&geometry);
  }

  struct Prepared
  {
    P::Asset             asset;
    std::vector<Binding> bindings;
  };

  inline Prepared Prepare(RE::NiNode* live, const Engine& engine)
  {
    if (!engine.mainThread || !engine.mainThread() || !engine.save || !engine.normalizeBones)
      throw std::runtime_error("native capture outside game thread");
    std::vector<RE::NiAVObject*> topology;
    Collect(live, topology);
    std::vector<RE::NiAVObject*> required;
    for (auto* source : topology)
    {
      const bool auxiliary = Auxiliary(*source);
      if (source->AsGeometry() && !auxiliary) required.push_back(source);
    }
    RE::NiPointer<RE::NiObject> holder{live->Clone()};
    auto*                       clone = holder ? holder->AsNode() : nullptr;
    if (!clone) throw std::runtime_error("Не удалось клонировать модель");
    std::vector<RE::NiAVObject*> cloned;
    Collect(clone, cloned);
    const std::unordered_set<RE::NiAVObject*> sourceNodes{topology.begin(), topology.end()};
    for (auto* node : cloned)
      if (sourceNodes.contains(node)) throw std::runtime_error("Копия содержит узлы исходной модели");
    for (auto* node : cloned)
      if (auto* tree = netimmerse_cast<RE::BSFlattenedBoneTree*>(node)) engine.normalizeBones(*tree);
    std::unordered_map<RE::NiAVObject*, RE::NiAVObject*> pairs;
    std::unordered_map<const RE::NiTransform*, Binding>  transforms;
    Pair(live, clone, pairs, transforms);
    for (auto* source : required)
      if (!pairs.contains(source)) throw std::runtime_error("clone omitted required native geometry");
    for (const auto& [source, target] : pairs)
    {
      auto* geometry = source->AsGeometry();
      auto* copy     = target->AsGeometry();
      if (!geometry || !copy || Auxiliary(*source)) continue;
      const auto& original = geometry->GetGeometryRuntimeData();
      auto&       detached = copy->GetGeometryRuntimeData();
      if (const auto* effect = netimmerse_cast<RE::BSEffectShaderProperty*>(original.shaderProperty.get()))
      {
        // Effect properties can be shared or omitted by the engine clone.
        // Replace from source facts before filtering the cloned tree; never
        // mutate the live property or silently lose this required surface.
        if (!engine.lighting) throw std::runtime_error("native lighting factory missing");
        RE::NiPointer<RE::BSLightingShaderProperty> replacement{engine.lighting()};
        if (!replacement || !replacement->material) throw std::bad_alloc{};
        using Shader = RE::BSShaderProperty::EShaderPropertyFlag;
        if (original.skinInstance) replacement->flags.set(Shader::kSkinned);
        if (effect->flags.all(Shader::kTwoSided)) replacement->flags.set(Shader::kTwoSided);
        detached.shaderProperty = replacement;
        logger::info(
          "[Phantom] converted native effect surface '{}' to ghost lighting (skinned={})",
          source->name.c_str(),
          original.skinInstance != nullptr);
      }
      if (original.shaderProperty && original.shaderProperty == detached.shaderProperty)
        throw std::runtime_error("clone shares source shader property");
    }
    // Clone may retain runtime-only metadata (FaceGen model/morph handles,
    // actor animation data, mod extras) which NiStream cannot reconstruct.
    // This is a controller-free visual snapshot, not another live actor.
    // Never remove extras from the source or a shared extra-pointer array.
    for (const auto& [source, target] : pairs)
      if (source->extra && source->extra == target->extra) throw std::runtime_error("Копия разделяет метаданные с исходной моделью");
    std::unordered_map<RE::NiAVObject*, Binding> channels;
    for (const auto& [source, target] : pairs)
    {
      channels.emplace(target, Binding{RE::NiPointer<RE::NiAVObject>{source}, Transform::World});
      target->world      = source->world;
      target->worldBound = source->worldBound;
    }
    // Materialize optimized bone transforms as ordinary owned NiNodes. NIF
    // links can now survive Save/Load; never retain a pointer to the live skin.
    std::unordered_map<const RE::NiTransform*, RE::NiNode*> bones;
    for (const auto& [source, target] : pairs)
    {
      auto* geometry = source->AsGeometry();
      auto* copy     = target->AsGeometry();
      if (!geometry || !copy || Auxiliary(*source)) continue;
      const auto* skin     = geometry->GetGeometryRuntimeData().skinInstance.get();
      auto*       skinCopy = copy->GetGeometryRuntimeData().skinInstance.get();
      if (bool(skin) != bool(skinCopy)) throw std::runtime_error("clone changed native skin presence");
      if (!skin) continue;
      if (
        skin == skinCopy || !skin->skinData || !skinCopy->skinData || !skinCopy->bones || !skinCopy->boneWorldTransforms ||
        skin->bones == skinCopy->bones || skin->boneWorldTransforms == skinCopy->boneWorldTransforms)
        throw std::runtime_error("Скин не имеет независимой копии");
      const auto count = skin->skinData->GetBoneCount();
      if (count > MaxNodes || count != skinCopy->skinData->GetBoneCount()) throw std::runtime_error("Некорректное число костей скина");
      skinCopy->rootParent = pairs.contains(skin->rootParent) ? pairs.at(skin->rootParent) : clone;
      for (std::uint32_t b = 0; b < count; ++b)
      {
        const auto* transform = skin->boneWorldTransforms ? skin->boneWorldTransforms[b] : nullptr;
        if (!transform && skin->bones && skin->bones[b]) transform = &skin->bones[b]->world;
        const auto found = transforms.find(transform);
        if (found == transforms.end()) throw std::runtime_error("Кость ссылается за пределы модели персонажа");
        auto& bone = bones[transform];
        if (!bone)
        {
          if (bones.size() + cloned.size() > MaxNodes) throw std::runtime_error("Слишком много костей скинов");
          RE::NiPointer<RE::NiNode> owned{RE::NiNode::Create(0)};
          if (!owned) throw std::bad_alloc{};
          bone        = owned.get();
          bone->name  = std::format("DreamsleevePose{}", bones.size()).c_str();
          bone->world = *transform;
          bone->local = clone->world.Invert() * bone->world;
          clone->AttachChild(bone, true);
          channels.emplace(bone, found->second);
        }
        skinCopy->bones[b]               = bone;
        skinCopy->boneWorldTransforms[b] = &bone->world;
      }
    }
    cloned.clear();
    Collect(clone, cloned);
    std::size_t strippedExtras = 0;
    for (auto* node : cloned)
    {
      strippedExtras += node->GetExtraDataSize();
      node->RemoveAllExtraData();
      node->SetUserData(nullptr);
      node->collisionObject.reset();
      node->controllers.reset();
    }
    logger::info("[Phantom] removed {} clone extra-data entries before NiStream Save", strippedExtras);
    // A leaf excluded by the prototype must not retain controllers, lights
    // or shader dependencies in the network asset, even while hidden.
    for (auto* object : cloned | std::views::reverse)
      if (object != clone && Auxiliary(*object) && object->parent)
      {
        object->parent->DetachChild(object);
        channels.erase(object);
      }
    auto streamRoot = StreamTree(clone, channels);
    cloned.clear();
    Collect(streamRoot.get(), cloned);

    for (auto* object : cloned)
      if (auto* geometry = object->AsGeometry()) Ghostify(*geometry, engine, {});
    auto encoded = engine.save(streamRoot.get());
    if (!encoded) throw std::runtime_error(encoded.error().field);
    Prepared out{P::Asset{std::move(*encoded)}, {}};
    out.bindings.reserve(cloned.size());
    for (auto* node : cloned)
      out.bindings.push_back(channels.at(node));
    return out;
  }

  inline const RE::NiTransform* Resolve(Binding& binding)
  {
    if (binding.kind == Transform::World) return &binding.owner->world;
    if (binding.kind == Transform::Local) return &binding.owner->local;
    auto& data = static_cast<RE::BSFlattenedBoneTree*>(binding.owner.get())->GetRuntimeData();
    if (!data.boneEntries || data.numBones > MaxNodes) return nullptr;
    // Runtime name is only a source lookup hint. The wire identity is the
    // validated preorder index, and an ambiguous source lookup is rejected.
    std::optional<std::uint32_t> found;
    for (std::uint32_t i = 0; i < data.numBones; ++i)
      if (data.boneEntries[i].nodeName == binding.boneName)
      {
        if (found) return nullptr;
        found = i;
      }
    if (!found) return nullptr;
    binding.boneIndex = *found;
    return binding.kind == Transform::BoneWorld ? &data.boneEntries[*found].world : &data.boneEntries[*found].local;
  }

}
