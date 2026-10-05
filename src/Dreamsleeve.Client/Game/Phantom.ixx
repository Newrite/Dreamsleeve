module;

#include "Prelude.hpp"

export module Dreamsleeve.Game.Phantom;

import std;
import Dreamsleeve.Host.Bridge;
import Dreamsleeve.Game.PhantomCapture;
import Dreamsleeve.Host.PhantomArchive;
import Dreamsleeve.Host.PhantomReplay;

// Local experiment. No network, actors, physics, or persistent game refs.
export namespace Phantom
{
  namespace Bridge               = Dreamsleeve::Host::Bridge;
  using Clock                    = std::chrono::steady_clock;
  using Flag                     = RE::NiAVObject::Flag;
  constexpr std::size_t MaxNodes = 4096;
  constexpr std::size_t MaxBytes = 128 * 1024 * 1024;
  constexpr double      Duration = 15.0;

  struct Engine
  {
    std::expected<std::vector<char>, std::string>         (*save)(RE::NiNode*){};
    std::expected<RE::NiPointer<RE::NiNode>, std::string> (*load)(std::vector<char>&){};
    RE::NiAlphaProperty*                                  (*alpha)(){};
    std::map<std::string, std::uint64_t>                  (*layout)(RE::NiNode*){};
  };

  struct Pose
  {
    RE::NiTransform world;
    RE::NiBound     bound;
    bool            hidden{};
    std::uint32_t   liveParent{PhantomArchive::NoParent};
  };

  struct Frame
  {
    double            time{};
    std::vector<Pose> pose;
    std::uint8_t      flags{};
    double            sampleMs{};
  };

  // No game-owned references: shared immutably with the file writer after Stop.
  struct Clip
  {
    std::vector<Frame>       frames;
    std::vector<char>        appearance;
    PhantomArchive::Metadata metadata;
  };

  PhantomArchive::Writer& ArchiveWriter()
  {
    static auto* writer = new PhantomArchive::Writer;
    return *writer;
  }

  std::future<PhantomReplay::Prepared>& ArchiveLoader()
  {
    static auto* loader = new std::future<PhantomReplay::Prepared>;
    return *loader;
  }

  PhantomArchive::Writer& ComparisonWriter()
  {
    static auto* writer = new PhantomArchive::Writer;
    return *writer;
  }

  enum class Transform
  {
    World,
    Local,
    BoneWorld,
    BoneLocal
  };

  // Owners stay alive during recording. Bone arrays may move/reorder, so a
  // channel stores its name and index hint, never a cross-frame array pointer.
  struct Binding
  {
    RE::NiPointer<RE::NiAVObject> owner;
    Transform                     kind{};
    RE::BSFixedString             boneName;
    std::uint32_t                 boneIndex{};
    bool                          unavailable{};
    PhantomCapture::Visibility    visibility;
  };

  struct State
  {
    Engine                    engine;
    Bridge::PhantomEvent      status;
    RE::ObjectRefHandle       player;
    PhantomCapture::Space     space;
    RE::NiPointer<RE::NiNode> source;
    RE::NiPointer<RE::NiNode> root;
    RE::NiPointer<RE::NiNode> parent;
    std::vector<Binding>      bindings;
    // Keys are owned by bindings; allocated once at Start, refreshed per sample.
    std::unordered_map<RE::NiAVObject*, PhantomCapture::Attachment> attachments;
    std::vector<RE::NiAVObject*>                                    nodes;     // owned exclusively by root
    std::vector<bool>                                               excluded;
    std::vector<RE::BSLightingShaderProperty*>                      surfaces;  // owned by root
    std::vector<RE::NiSkinInstance*>                                skins;     // owned by root
    RE::NiPointer<RE::NiSourceTexture>                              white;
    std::shared_ptr<Clip>                                           clip{std::make_shared<Clip>()};
    std::unordered_map<RE::NiAVObject*, std::uint32_t>              liveChannels;
    std::string                                                     scenario{"mixed"};
    double                                                          elapsed{};
    double                                                          nextSample{};
    Clock::time_point                                               nextStatus{};
    bool                                                            dirty{true};
    std::string                                                     pending;
    std::optional<bool>                                             weaponDrawn;
    std::optional<bool>                                             firstPerson;
    std::shared_ptr<PhantomReplay::Prepared>                        replay;
    std::vector<RE::NiBound>                                        localBounds;
    std::vector<char>                                               prunedAppearance;
    std::vector<std::uint32_t>                                      retainedChannels;
    std::string                                                     poseMode{"full"};
    std::string                                                     modelMode{"original"};
  };

  State& Get()
  {
    // Explicitly cleared while the engine still exists, never at DLL teardown.
    static auto* state = new State;
    return *state;
  }

  void Configure(Engine engine)
  {
    auto& s            = Get();
    s.engine           = engine;
    s.status.supported = engine.save && engine.load && engine.alpha;
    s.status.status    = s.status.supported ? "Готов к записи" : "Этот runtime не поддерживается локальным стендом";
    s.dirty            = true;
  }

  void Collect(RE::NiAVObject* object, std::vector<RE::NiAVObject*>& nodes)
  {
    if (!object) return;
    if (nodes.size() >= MaxNodes) throw std::runtime_error("Слишком много узлов персонажа");
    nodes.push_back(object);
    if (auto* node = object->AsNode())
      for (const auto& child : node->GetChildren())
        Collect(child.get(), nodes);
  }

  void Detach(State& s)
  {
    if (s.root && s.parent && s.root->parent == s.parent.get()) s.parent->DetachChild(s.root.get());
    s.parent.reset();
  }

  void Reset()
  {
    auto&      s      = Get();
    const auto engine = s.engine;
    const auto rate   = s.status.rate;
    Detach(s);
    s = State{};
    Configure(engine);
    s.status.rate = rate;
  }

  PhantomArchive::Pose ArchivePose(const Pose& pose)
  {
    PhantomArchive::Pose out;
    std::size_t          i = 0;
    for (const auto& row : pose.world.rotate.entry)
      for (const auto value : row)
        out.values[i++] = value;
    for (
      const auto value :
      {pose.world.translate.x,
       pose.world.translate.y,
       pose.world.translate.z,
       pose.world.scale,
       pose.bound.center.x,
       pose.bound.center.y,
       pose.bound.center.z,
       pose.bound.radius})
      out.values[i++] = value;
    out.hidden = pose.hidden;
    out.parent = pose.liveParent;
    return out;
  }

  void Export(State& s, const std::string& reason)
  {
    auto& meta           = s.clip->metadata;
    meta.stopReason      = reason;
    meta.frameCount      = static_cast<std::uint32_t>(s.clip->frames.size());
    meta.seconds         = s.clip->frames.back().time;
    meta.appearanceBytes = s.clip->appearance.size();
    meta.memoryPoseBytes = s.status.poseBytes;
    meta.filePoseBytes =
      PhantomArchive::HeaderBytes + meta.frameCount * (PhantomArchive::FrameHeaderBytes + meta.nodes.size() * PhantomArchive::PoseBytes);
    meta.buildMs = s.status.buildMs;
    // Percentiles and file writes happen on the worker; no full-recording copy.
    const auto logs = SKSE::log::log_directory();
    if (!logs)
    {
      s.status.exportError = "Не найдена папка логов SKSE";
      return;
    }
    const std::shared_ptr<const Clip> clip = s.clip;
    const auto submitted = ArchiveWriter().Submit(*logs / "DreamsleevePhantoms", s.scenario, [clip](const auto& directory) {
      std::ofstream model(directory / "appearance.nif", std::ios::binary);
      model.exceptions(std::ios::failbit | std::ios::badbit);
      PhantomArchive::Bytes(model, clip->appearance);
      model.close();
      std::ofstream poses(directory / "poses.bin", std::ios::binary);
      poses.exceptions(std::ios::failbit | std::ios::badbit);
      PhantomArchive::Header(
        poses,
        static_cast<std::uint32_t>(clip->metadata.nodes.size()),
        clip->metadata.frameCount,
        clip->metadata.rate);
      std::vector<double> samples;
      for (const auto& frame : clip->frames)
      {
        PhantomArchive::Scalar(poses, frame.time);
        PhantomArchive::Scalar(poses, frame.flags);
        for (const auto& pose : frame.pose)
          PhantomArchive::WritePose(poses, ArchivePose(pose));
        samples.push_back(frame.sampleMs);
      }
      poses.close();
      auto metadata = clip->metadata;
      std::ranges::sort(samples);
      metadata.sampleMedianMs = samples[samples.size() / 2];
      metadata.sampleP95Ms    = samples[static_cast<std::size_t>(std::ceil(samples.size() * 0.95)) - 1];
      metadata.sampleMaxMs    = samples.back();
      PhantomArchive::MetadataFile(directory, metadata);
    });
    s.status.exportError = submitted ? "" : submitted.error();
    if (!submitted)
      logger::error("[Phantom] archive could not start: {}", submitted.error());
    else
      logger::info("[Phantom] archive queued: {}; {} frames; {} canonical pose bytes", s.scenario, meta.frameCount, meta.filePoseBytes);
  }

  void Stop(State& s, std::string message)
  {
    const bool wasRecording = s.status.recording;
    if (s.status.recording)
      logger::info(
        "[Phantom] recording stopped: {}; {} frames, {:.2f} seconds, {} pose bytes",
        message,
        s.clip->frames.size(),
        s.elapsed,
        s.status.poseBytes);
    s.status.recording = false;
    s.source.reset();
    s.attachments.clear();
    s.liveChannels.clear();
    s.bindings.clear();
    s.status.ready  = s.clip->frames.size() >= 2 && s.root;
    s.status.status = std::move(message);
    if (wasRecording && s.status.ready) Export(s, s.status.status);
    s.dirty = true;
  }

  void Fail(State& s, std::string message)
  {
    logger::error("[Phantom] {}", message);
    Reset();
    s.status.status = std::move(message);
  }

  // Engine Clone allocates BoneEntry[] with an 8-byte array cookie, whereas
  // BSFlattenedBoneTree's destructor frees the entry pointer itself (SE/AE/VR IDA).
  void Normalize(RE::BSFlattenedBoneTree& tree)
  {
    auto& data = tree.GetRuntimeData();
    if (!data.boneEntries) return;
    if (!data.numBones)
    {
      RE::free(reinterpret_cast<std::byte*>(data.boneEntries) - sizeof(std::uint64_t));
      data.boneEntries = nullptr;
      return;
    }
    const auto bytes  = data.numBones * sizeof(RE::BSFlattenedBoneTree::BoneEntry);
    auto*      native = static_cast<RE::BSFlattenedBoneTree::BoneEntry*>(RE::malloc(bytes));
    if (!native && bytes)
    {
      // Move the allocation down over its cookie so the destructor can still
      // release every name and the correct allocation without another allocation.
      auto* allocation = reinterpret_cast<std::byte*>(data.boneEntries) - sizeof(std::uint64_t);
      std::memmove(allocation, data.boneEntries, bytes);
      data.boneEntries = reinterpret_cast<RE::BSFlattenedBoneTree::BoneEntry*>(allocation);
      return;
    }
    std::memcpy(native, data.boneEntries, bytes);
    RE::free(reinterpret_cast<std::byte*>(data.boneEntries) - sizeof(std::uint64_t));
    data.boneEntries = native;
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

  void ApplyLook(RE::BSLightingShaderProperty& lit)
  {
    auto* material          = static_cast<RE::BSLightingShaderMaterialBase*>(lit.material);
    lit.alpha               = 1.0f;
    material->materialAlpha = 0.6f;
    material->rimLightPower = 3.4f;
    lit.emissiveMult        = 1.5f;
  }

  bool Ghostify(RE::BSGeometry& geometry, State& s)
  {
    auto&                  data = geometry.GetGeometryRuntimeData();
    auto*                  lit  = netimmerse_cast<RE::BSLightingShaderProperty*>(data.shaderProperty.get());
    const std::string_view name = geometry.name.c_str();
    if (!lit || !lit->material || name.find(" [Ovl") != name.npos || name.find(" [SOvl") != name.npos) return false;
    auto* base = static_cast<RE::BSLightingShaderMaterialBase*>(lit->material);
    lit->controllers.reset();
    if (lit->alpha < 0.01f || base->materialAlpha < 0.01f) return false;
    auto* temporary = static_cast<RE::BSLightingShaderMaterialBase*>(base->Create());
    if (!temporary) throw std::bad_alloc{};
    temporary->CopyMembers(base);
    lit->SetMaterial(temporary, true);
    temporary->~BSLightingShaderMaterialBase();
    RE::free(temporary);
    auto* material = static_cast<RE::BSLightingShaderMaterialBase*>(lit->material);
    if (s.white) material->diffuseTexture = s.white;
    using Shader = RE::BSShaderProperty::EShaderPropertyFlag;
    lit->flags.reset(Shader::kCastShadows, Shader::kReceiveShadows, Shader::kSpecular);
    lit->flags.set(Shader::kZBufferTest, Shader::kZBufferWrite, Shader::kNoFade, Shader::kRimLighting);
    if (!lit->flags.all(Shader::kOwnEmit) || !lit->emissiveColor)
    {
      auto* color = RE::malloc<RE::NiColor>();
      if (!color) throw std::bad_alloc{};
      new (color) RE::NiColor(0.55f, 0.8f, 1.0f);
      lit->emissiveColor = color;
    }
    else
      *lit->emissiveColor = RE::NiColor(0.55f, 0.8f, 1.0f);
    lit->flags.set(Shader::kOwnEmit);
    ApplyLook(*lit);
    RE::NiPointer<RE::NiAlphaProperty> alpha{s.engine.alpha()};
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
    s.surfaces.push_back(lit);
    return true;
  }

  const RE::NiTransform* Resolve(Binding& binding)
  {
    if (binding.kind == Transform::World) return &binding.owner->world;
    if (binding.kind == Transform::Local) return &binding.owner->local;
    auto& data = static_cast<RE::BSFlattenedBoneTree*>(binding.owner.get())->GetRuntimeData();
    if (data.numBones > MaxNodes) throw std::runtime_error("Слишком много костей во время записи");
    if (!data.boneEntries) return nullptr;
    const auto found = PhantomCapture::FindBone(
      std::span<const RE::BSFlattenedBoneTree::BoneEntry>{data.boneEntries, data.numBones},
      binding.boneName.c_str(),
      binding.boneIndex,
      [](const auto& bone) { return std::string_view{bone.nodeName.c_str()}; });
    if (!found) return nullptr;
    binding.boneIndex = static_cast<std::uint32_t>(*found);
    const auto& bone  = data.boneEntries[*found];
    return binding.kind == Transform::BoneWorld ? &bone.world : &bone.local;
  }

  void Sample(State& s)
  {
    const auto  start = Clock::now();
    Frame       frame{.time = s.elapsed};
    const auto* camera      = RE::PlayerCamera::GetSingleton();
    const bool  firstPerson = camera && camera->IsInFirstPerson();
    const auto  playerRef   = s.player.get();
    const auto* actorState  = playerRef ? playerRef->As<RE::Actor>() : nullptr;
    frame.flags             = static_cast<std::uint8_t>((firstPerson ? 1 : 0) | (actorState && actorState->IsWeaponDrawn() ? 2 : 0));
    frame.pose.reserve(s.bindings.size());
    for (auto& [owner, attachment] : s.attachments)
      attachment = PhantomCapture::Locate(
        static_cast<RE::NiAVObject*>(s.source.get()),
        owner,
        [](RE::NiAVObject* node) { return static_cast<RE::NiAVObject*>(node->parent); },
        [](RE::NiAVObject* node) { return node->GetFlags().all(Flag::kHidden); });
    std::size_t unavailable = 0;
    std::size_t changed     = 0;
    std::string examples;
    for (std::size_t i = 0; i < s.bindings.size(); ++i)
    {
      auto&       binding     = s.bindings[i];
      const auto  attachment  = s.attachments.at(binding.owner.get());
      const auto* transform   = attachment.present ? Resolve(binding) : nullptr;
      const bool  missing     = !transform;
      unavailable            += missing;
      if (missing != binding.unavailable)
      {
        if (++changed <= 4)
          examples += std::format(
            "{}{} ({})",
            examples.empty() ? "" : ", ",
            binding.kind == Transform::BoneWorld || binding.kind == Transform::BoneLocal ? binding.boneName.c_str()
                                                                                         : binding.owner->name.c_str(),
            missing ? (attachment.present ? "bone missing" : "detached") : "restored");
        binding.unavailable = missing;
      }
      // A removed part keeps its last valid transform but is hidden; a later
      // reattachment resumes the same channel without breaking the recording.
      Pose pose = s.clip->frames.empty() ? Pose{s.nodes[i]->world, s.nodes[i]->worldBound, true} : s.clip->frames.back().pose[i];
      if (transform)
      {
        pose.world = *transform;
        pose.bound = binding.owner->worldBound;
      }
      pose.liveParent = s.clip->metadata.nodes[i].parent;
      if (binding.kind == Transform::World)
        if (const auto parent = s.liveChannels.find(binding.owner->parent); parent != s.liveChannels.end())
          pose.liveParent = parent->second;
      // Containers and synthetic bones must not gate a whole body part.
      // In first person the game hides the live third-person model, not the ghost.
      pose.hidden = s.nodes[i]->AsGeometry() && binding.visibility.Sample(!missing, attachment.hidden, firstPerson);
      frame.pose.push_back(pose);
    }
    if (changed) logger::info("[Phantom] pose sources changed: {} channels; {} unavailable; {}", changed, unavailable, examples);
    const auto player = s.player.get();
    auto*      actor  = player ? player->As<RE::Actor>() : nullptr;
    if (actor)
    {
      const bool drawn = actor->IsWeaponDrawn();
      if (s.weaponDrawn != drawn || s.firstPerson != firstPerson)
      {
        s.weaponDrawn = drawn;
        s.firstPerson = firstPerson;
        logger::info("[Phantom] equipment pose: {:.2f}s; drawn {}; first person {}", s.elapsed, drawn, firstPerson);
        const auto& biped = actor->GetBiped(false);
        if (biped)
          for (std::uint32_t slot = RE::BIPED_OBJECTS::kOneHandSword; slot <= RE::BIPED_OBJECTS::kCrossbow; ++slot)
          {
            const auto& part = biped->objects[slot];
            if (!part.item || !part.partClone) continue;
            const auto* node  = part.partClone.get();
            const auto  found = s.attachments.find(part.partClone.get());
            logger::info(
              "[Phantom] weapon slot {}; item {:08X}; node {}; parent {}; captured {}; hidden {}; scale {:.3f}; bound {:.2f}",
              slot,
              part.item->GetFormID(),
              node->name.c_str(),
              node->parent ? node->parent->name.c_str() : "<detached>",
              found != s.attachments.end(),
              found != s.attachments.end() ? found->second.hidden : node->GetFlags().all(Flag::kHidden),
              node->world.scale,
              node->worldBound.radius);
          }
      }
    }
    s.clip->frames.push_back(std::move(frame));
    s.status.frames                = static_cast<std::uint32_t>(s.clip->frames.size());
    s.status.seconds               = s.elapsed;
    s.status.poseBytes             = s.clip->frames.size() * s.bindings.size() * sizeof(Pose);
    s.status.sampleMs              = std::chrono::duration<double, std::milli>(Clock::now() - start).count();
    s.clip->frames.back().sampleMs = s.status.sampleMs;
  }

  void Describe(State& s)
  {
    auto& meta    = s.clip->metadata;
    meta.runtime  = REL::Module::get().version().string();
    meta.scenario = s.scenario;
    meta.rate     = s.status.rate;
    meta.cell     = s.space.cell;
    meta.world    = s.space.world;
    meta.cppSizes = {
        {"NiStream",       sizeof(RE::NiStream)                      },
        {"NiAVObject",     sizeof(RE::NiAVObject)                    },
        {"NiNode",         sizeof(RE::NiNode)                        },
        {"NiTransform",    sizeof(RE::NiTransform)                   },
        {"NiBound",        sizeof(RE::NiBound)                       },
        {"NiSkinInstance", sizeof(RE::NiSkinInstance)                },
        {"BSGeometry",     sizeof(RE::BSGeometry)                    },
        {"BSTriShape",     sizeof(RE::BSTriShape)                    },
        {"BoneEntry",      sizeof(RE::BSFlattenedBoneTree::BoneEntry)},
        {"MemoryPose",     sizeof(Pose)                              }
    };
    if (s.engine.layout) meta.observedOffsets = s.engine.layout(s.source.get());
    std::unordered_map<RE::NiAVObject*, std::uint32_t> indices;
    for (std::uint32_t i = 0; i < s.nodes.size(); ++i)
    {
      indices.emplace(s.nodes[i], i);
      if (s.bindings[i].kind == Transform::World) s.liveChannels.emplace(s.bindings[i].owner.get(), i);
    }
    constexpr auto kinds = std::to_array<std::string_view>({"world", "local", "boneWorld", "boneLocal"});
    for (std::uint32_t i = 0; i < s.nodes.size(); ++i)
    {
      auto*                node    = s.nodes[i];
      const auto&          binding = s.bindings[i];
      PhantomArchive::Node description;
      description.name       = node->name.c_str();
      description.type       = node->GetRTTI()->GetName();
      description.sourceName = binding.owner->name.c_str();
      description.sourceType = binding.owner->GetRTTI()->GetName();
      description.channel    = kinds[static_cast<std::size_t>(binding.kind)];
      description.boneName   = binding.boneName.c_str();
      description.boneIndex  = binding.boneIndex;
      description.geometry   = node->AsGeometry() != nullptr;
      description.excluded   = s.excluded[i];
      if (indices.contains(node->parent)) description.parent = indices.at(node->parent);
      if (auto* shape = node->AsTriShape())
      {
        const auto& data      = shape->GetTrishapeRuntimeData();
        description.vertices  = data.vertexCount;
        description.triangles = data.triangleCount;
      }
      meta.nodes.push_back(std::move(description));
      if (auto* geometry = node->AsGeometry())
        if (auto* skin = geometry->GetGeometryRuntimeData().skinInstance.get())
        {
          PhantomArchive::Skin links;
          links.geometry = i;
          links.root     = indices.at(skin->rootParent);
          for (std::uint32_t b = 0; b < skin->skinData->GetBoneCount(); ++b)
            links.bones.push_back(indices.at(skin->bones[b]));
          meta.skins.push_back(std::move(links));
        }
    }
    const auto geometryCount = std::ranges::count(meta.nodes, true, &PhantomArchive::Node::geometry);
    logger::info(
      "[Phantom] archive layout: {} channels; {} geometry; {} skins; pose memory {} bytes, file {} bytes",
      meta.nodes.size(),
      geometryCount,
      meta.skins.size(),
      sizeof(Pose),
      PhantomArchive::PoseBytes);
    for (const auto& [name, size] : meta.cppSizes)
      logger::info("[Phantom] C++ sizeof {} = {}", name, size);
    for (const auto& [name, offset] : meta.observedOffsets)
      logger::info("[Phantom] observed offset {} = 0x{:X}", name, offset);
  }

  void Start(State& s, RE::PlayerCharacter& player)
  {
    const auto start  = Clock::now();
    auto*      object = player.Get3D(false);
    auto*      live   = object ? object->AsNode() : nullptr;
    if (!live || !live->parent || !player.GetParentCell()) throw std::runtime_error("Нет загруженной модели персонажа");
    s.player    = player.GetHandle();
    auto* cell  = player.GetParentCell();
    auto* world = cell->IsExteriorCell() ? player.GetWorldspace() : nullptr;
    s.space     = {cell->GetFormID(), world ? world->GetFormID() : 0};
    s.source    = RE::NiPointer<RE::NiNode>{live};
    RE::NiPointer<RE::NiTexture> white;
    RE::BSShaderManager::GetTexture("textures\\effects\\fxwhite.dds", true, white, false);
    if (white) s.white = RE::NiPointer<RE::NiSourceTexture>{static_cast<RE::NiSourceTexture*>(white.get())};
    if (!s.white) logger::warn("[Phantom] fxwhite.dds unavailable; keeping original diffuse textures");
    std::vector<RE::NiAVObject*> topology;
    Collect(live, topology);
    RE::NiPointer<RE::NiObject> holder{live->Clone()};
    auto*                       clone = holder ? holder->AsNode() : nullptr;
    if (!clone) throw std::runtime_error("Не удалось клонировать модель");
    std::vector<RE::NiAVObject*> cloned;
    Collect(clone, cloned);
    const std::unordered_set<RE::NiAVObject*> sourceNodes{topology.begin(), topology.end()};
    for (auto* node : cloned)
      if (sourceNodes.contains(node)) throw std::runtime_error("Копия содержит узлы исходной модели");
    for (auto* node : cloned)
      if (auto* tree = netimmerse_cast<RE::BSFlattenedBoneTree*>(node)) Normalize(*tree);
    std::unordered_map<RE::NiAVObject*, RE::NiAVObject*> pairs;
    std::unordered_map<const RE::NiTransform*, Binding>  transforms;
    Pair(live, clone, pairs, transforms);
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
      if (!geometry || !copy) continue;
      const auto* skin     = geometry->GetGeometryRuntimeData().skinInstance.get();
      auto*       skinCopy = copy->GetGeometryRuntimeData().skinInstance.get();
      if (!skin || !skinCopy) continue;
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
      if (
        node->AsGeometry() &&
        !netimmerse_cast<RE::BSLightingShaderProperty*>(node->AsGeometry()->GetGeometryRuntimeData().shaderProperty.get()))
        node->GetFlags().set(Flag::kHidden);
    }
    logger::info("[Phantom] removed {} clone extra-data entries before NiStream Save", strippedExtras);
    auto streamRoot = StreamTree(clone, channels);
    cloned.clear();
    Collect(streamRoot.get(), cloned);
    auto encoded = s.engine.save(streamRoot.get());
    if (!encoded) throw std::runtime_error(encoded.error());
    s.clip->appearance = std::move(*encoded);
    auto decoded       = s.engine.load(s.clip->appearance);
    if (!decoded) throw std::runtime_error(decoded.error());
    s.root = std::move(*decoded);
    Collect(s.root.get(), s.nodes);
    if (s.nodes.size() != cloned.size()) throw std::runtime_error("NiStream изменил структуру модели");
    for (std::size_t i = 0; i < cloned.size(); ++i)
    {
      auto* target = s.nodes[i];
      if (target->name != cloned[i]->name || target->GetStreamableRTTI() != cloned[i]->GetStreamableRTTI() || !channels.contains(cloned[i]))
        throw std::runtime_error("NiStream не восстановил связи узлов");
      s.bindings.push_back(channels.at(cloned[i]));
      s.attachments.try_emplace(s.bindings.back().owner.get());
      target->SetUserData(nullptr);
      target->collisionObject.reset();
      target->controllers.reset();
      target->GetFadeAmount() = 1.0f;
      target->GetFlags().set(Flag::kIgnoreFade);
      bool excluded = false;
      if (auto* geometry = target->AsGeometry())
      {
        excluded = !Ghostify(*geometry, s);
        if (excluded) logger::debug("[Phantom] excluded geometry: {}", target->name.c_str());
        if (auto* skin = geometry->GetGeometryRuntimeData().skinInstance.get())
        {
          if (!skin->skinData || !skin->rootParent) throw std::runtime_error("NiStream потерял корень скина");
          const auto count = skin->skinData->GetBoneCount();
          if (count > MaxNodes || (count && (!skin->bones || !skin->boneWorldTransforms)))
            throw std::runtime_error("NiStream потерял кости скина");
          for (std::uint32_t b = 0; b < count; ++b)
          {
            if (!skin->bones[b]) throw std::runtime_error("NiStream потерял кость скина");
            skin->boneWorldTransforms[b] = &skin->bones[b]->world;
          }
          if (std::ranges::find(s.skins, skin) == s.skins.end()) s.skins.push_back(skin);
        }
      }
      s.excluded.push_back(excluded);
    }
    s.root->GetFlags().set(Flag::kHidden);
    s.status.nodes           = static_cast<std::uint32_t>(s.nodes.size());
    s.status.bones           = static_cast<std::uint32_t>(bones.size());
    s.status.appearanceBytes = s.clip->appearance.size();
    s.status.buildMs         = std::chrono::duration<double, std::milli>(Clock::now() - start).count();
    s.status.recording       = true;
    s.status.status          = "Запись: 15 секунд движения";
    Describe(s);
    Sample(s);
    s.nextSample = 1.0 / s.status.rate;
    s.dirty      = true;
    logger::info(
      "[Phantom] NiStream roundtrip: {} bytes, {} nodes, {} bones, {:.2f} ms",
      s.clip->appearance.size(),
      s.nodes.size(),
      bones.size(),
      s.status.buildMs);
  }

  Pose NativePose(const PhantomArchive::Pose& source)
  {
    Pose        result;
    std::size_t i = 0;
    for (auto& row : result.world.rotate.entry)
      for (auto& value : row)
        value = source.values[i++];
    result.world.translate = {source.values[9], source.values[10], source.values[11]};
    result.world.scale     = source.values[12];
    result.bound           = {
        {source.values[13], source.values[14], source.values[15]},
        source.values[16]
    };
    result.hidden     = source.hidden;
    result.liveParent = source.parent;
    return result;
  }

  void ValidateModel(const State& s)
  {
    const auto&                                        metadata = s.clip->metadata;
    std::unordered_map<RE::NiAVObject*, std::uint32_t> indices;
    for (std::uint32_t i = 0; i < s.nodes.size(); ++i)
      if (s.nodes[i]) indices.emplace(s.nodes[i], i);
    for (std::uint32_t i = 0; i < s.nodes.size(); ++i)
    {
      auto* object = s.nodes[i];
      if (!object) continue;
      const auto& saved = metadata.nodes[i];
      if (
        saved.name != object->name.c_str() || saved.type != object->GetRTTI()->GetName() ||
        saved.geometry != (object->AsGeometry() != nullptr))
        throw std::runtime_error("Модель изменила каналы архива");
      const auto parent = indices.contains(object->parent) ? indices.at(object->parent) : PhantomArchive::NoParent;
      if (saved.parent != parent) throw std::runtime_error("Модель изменила родителей архива");
      if (auto* shape = object->AsTriShape())
      {
        const auto& data = shape->GetTrishapeRuntimeData();
        if (data.vertexCount != saved.vertices || data.triangleCount != saved.triangles)
          throw std::runtime_error("Модель изменила вершины или треугольники");
      }
    }
    for (const auto& links : metadata.skins)
    {
      if (!s.nodes[links.geometry]) continue;
      auto* geometry = s.nodes[links.geometry]->AsGeometry();
      auto* skin     = geometry ? geometry->GetGeometryRuntimeData().skinInstance.get() : nullptr;
      if (
        !skin || !skin->skinData || skin->skinData->GetBoneCount() != links.bones.size() || links.root == PhantomArchive::NoParent ||
        !s.nodes[links.root] || skin->rootParent != s.nodes[links.root] ||
        (!links.bones.empty() && (!skin->bones || !skin->boneWorldTransforms)))
        throw std::runtime_error("Модель изменила корень или число костей скина");
      for (std::size_t b = 0; b < links.bones.size(); ++b)
        if (!s.nodes[links.bones[b]] || skin->bones[b] != s.nodes[links.bones[b]])
          throw std::runtime_error("Модель изменила связи костей скина");
    }
  }

  void ExportComparison(State& s)
  {
    const auto logs = SKSE::log::log_directory();
    if (!logs || !s.replay) return;
    auto                                                 bytes   = std::make_shared<const std::vector<char>>(s.prunedAppearance);
    const std::shared_ptr<const PhantomReplay::Prepared> replay  = s.replay;
    const auto                                           removed = s.status.removedGeometry;
    auto                                                 submitted =
      ComparisonWriter().Submit(*logs / "DreamsleevePhantomComparisons", s.scenario, [bytes, replay, removed](const auto& directory) {
        std::ofstream model(directory / "appearance.nif", std::ios::binary);
        model.exceptions(std::ios::failbit | std::ios::badbit);
        PhantomArchive::Bytes(model, *bytes);
        model.close();
        for (const auto* clip : {&replay->selected, &replay->quantized})
        {
          std::ofstream out(directory / (clip->quantized ? "quantized.bin" : "selected.bin"), std::ios::binary);
          out.exceptions(std::ios::failbit | std::ios::badbit);
          PhantomArchive::Bytes(out, std::span<const char>{"DSPCMP01", 8});
          PhantomArchive::Scalar(out, static_cast<std::uint32_t>(clip->channels.size()));
          PhantomArchive::Scalar(out, static_cast<std::uint32_t>(clip->frames.size()));
          PhantomArchive::Scalar(out, static_cast<std::uint32_t>(clip->quantized ? 23 : 33));
          for (auto x : clip->origin)
            PhantomArchive::Scalar(out, x);
          for (auto i : clip->channels)
            PhantomArchive::Scalar(out, i);
          for (const auto& frame : clip->frames)
            PhantomArchive::Bytes(out, frame);
          out.close();
        }
        std::ofstream info(directory / "metrics.json", std::ios::binary);
        info.exceptions(std::ios::failbit | std::ios::badbit);
        info << std::format(
          R"({{"appearanceBytes":{},"removedGeometry":{},"channels":{},"selectedBytes":{},"quantizedBytes":{}}})",
          bytes->size(),
          removed,
          replay->selected.channels.size(),
          replay->selected.Bytes(),
          replay->quantized.Bytes());
        info.close();
      });
    if (!submitted) logger::warn("[Phantom] comparison export: {}", submitted.error());
  }

  void InstallModel(State& s, bool pruned)
  {
    Detach(s);
    s.nodes.clear();
    s.skins.clear();
    s.surfaces.clear();
    s.root.reset();
    auto& bytes  = pruned && !s.prunedAppearance.empty() ? s.prunedAppearance : s.clip->appearance;
    auto  loaded = s.engine.load(bytes);
    if (!loaded) throw std::runtime_error(loaded.error());
    s.root = std::move(*loaded);
    std::vector<RE::NiAVObject*> tree;
    Collect(s.root.get(), tree);
    const auto count  = s.clip->metadata.nodes.size();
    const bool cached = pruned && !s.prunedAppearance.empty();
    if (tree.size() != (cached ? s.retainedChannels.size() : count)) throw std::runtime_error("Число узлов модели не совпало с архивом");
    s.nodes.resize(count);
    for (std::uint32_t i = 0; i < tree.size(); ++i)
      s.nodes[cached ? s.retainedChannels[i] : i] = tree[i];
    ValidateModel(s);
    if (pruned && !cached)
    {
      const auto                                 channels = PhantomReplay::Channels(s.clip->metadata);
      const std::unordered_set<std::uint32_t>    selected{channels.begin(), channels.end()};
      std::vector<RE::NiPointer<RE::NiAVObject>> removed;
      s.retainedChannels.clear();
      for (std::uint32_t i = 0; i < count; ++i)
      {
        auto* node = s.nodes[i];
        if (s.clip->metadata.nodes[i].geometry && s.clip->metadata.nodes[i].excluded && !selected.contains(i) && node->parent)
        {
          removed.emplace_back(node);
          node->parent->DetachChild(node);
          s.nodes[i] = nullptr;
        }
        else
          s.retainedChannels.push_back(i);
      }
      auto encoded = s.engine.save(s.root.get());
      if (!encoded) throw std::runtime_error(encoded.error());
      s.prunedAppearance           = std::move(*encoded);
      s.status.removedGeometry     = static_cast<std::uint32_t>(removed.size());
      s.status.optimizedModelBytes = s.prunedAppearance.size();
      logger::info(
        "[Phantom] pruned appearance: {} -> {} bytes; {} excluded geometry removed; vertices and skin links retained",
        s.clip->appearance.size(),
        s.prunedAppearance.size(),
        removed.size());
      // Validate the actual serialized candidate, not just the in-memory edit.
      InstallModel(s, true);
      ExportComparison(s);
      return;
    }
    RE::NiPointer<RE::NiTexture> white;
    RE::BSShaderManager::GetTexture("textures\\effects\\fxwhite.dds", true, white, false);
    if (white) s.white = RE::NiPointer<RE::NiSourceTexture>{static_cast<RE::NiSourceTexture*>(white.get())};
    s.excluded.assign(count, true);
    for (std::uint32_t i = 0; i < count; ++i)
    {
      auto* node = s.nodes[i];
      if (!node) continue;
      node->world      = s.clip->frames.front().pose[i].world;
      node->worldBound = s.clip->frames.front().pose[i].bound;
      node->SetUserData(nullptr);
      node->collisionObject.reset();
      node->controllers.reset();
      node->GetFadeAmount() = 1.0f;
      node->GetFlags().set(Flag::kIgnoreFade);
      s.excluded[i] = s.clip->metadata.nodes[i].excluded;
      if (auto* geometry = node->AsGeometry())
      {
        const bool visible = Ghostify(*geometry, s);
        if (visible == s.excluded[i]) throw std::runtime_error("Сокращение изменило видимость геометрии");
        if (auto* skin = geometry->GetGeometryRuntimeData().skinInstance.get())
        {
          for (std::uint32_t b = 0; b < skin->skinData->GetBoneCount(); ++b)
            skin->boneWorldTransforms[b] = &skin->bones[b]->world;
          if (std::ranges::find(s.skins, skin) == s.skins.end()) s.skins.push_back(skin);
        }
      }
      node->GetFlags().set(s.excluded[i] || s.clip->frames.front().pose[i].hidden, Flag::kHidden);
    }
    s.root->GetFlags().set(Flag::kHidden);
  }

  void LoadReady(State& s, RE::PlayerCharacter& player)
  {
    auto& loader = ArchiveLoader();
    if (!loader.valid() || loader.wait_for(std::chrono::seconds{0}) != std::future_status::ready) return;
    if (!s.status.loading)
    {
      // A cancelled result, including its exception, cannot reset a later game state.
      try
      {
        loader.get();
      }
      catch (const std::exception&)
      {}
      return;
    }
    auto                        prepared = loader.get();
    auto*                       cell     = player.GetParentCell();
    auto*                       world    = cell && cell->IsExteriorCell() ? player.GetWorldspace() : nullptr;
    const PhantomCapture::Space current{cell ? cell->GetFormID() : 0, world ? world->GetFormID() : 0};
    const auto&                 metadata = prepared.capture.metadata;
    s.space                              = {metadata.cell, metadata.world};
    if (!s.space.Contains(current)) throw std::runtime_error("Загрузи тот же интерьер или мир, где записан архив");
    const auto started     = Clock::now();
    s.player               = player.GetHandle();
    s.status.loadedArchive = prepared.capture.path;
    s.clip->metadata       = metadata;
    s.clip->appearance     = std::move(prepared.capture.appearance);
    for (const auto& frame : prepared.capture.frames)
    {
      Frame converted{.time = frame.time, .flags = frame.flags};
      converted.pose.reserve(frame.poses.size());
      for (const auto& pose : frame.poses)
        converted.pose.push_back(NativePose(pose));
      s.clip->frames.push_back(std::move(converted));
    }
    prepared.capture.frames.clear();
    prepared.capture.frames.shrink_to_fit();
    s.replay = std::make_shared<PhantomReplay::Prepared>(std::move(prepared));
    s.localBounds.resize(s.clip->metadata.nodes.size());
    // A clip-wide local envelope lets compact replay omit per-frame bounds.
    // This is diagnostic data derived from the full clip, not a live network solution.
    for (const auto& frame : s.clip->frames)
      for (std::size_t i = 0; i < frame.pose.size(); ++i)
        if (s.clip->metadata.nodes[i].geometry)
        {
          const auto& pose  = frame.pose[i];
          const auto  scale = std::abs(pose.world.scale);
          if (scale < 0.000001f) continue;
          const RE::NiBound local{pose.world.Invert() * pose.bound.center, std::max(0.0f, pose.bound.radius) / scale};
          PhantomCapture::Enclose(s.localBounds[i], local);
        }
    for (auto& bound : s.localBounds)
      bound.radius += 1.0f;
    InstallModel(s, false);
    s.status.nodes = static_cast<std::uint32_t>(s.nodes.size());
    std::set<std::uint32_t> bones;
    for (const auto& skin : s.clip->metadata.skins)
      bones.insert(skin.bones.begin(), skin.bones.end());
    s.status.bones           = static_cast<std::uint32_t>(bones.size());
    s.status.frames          = static_cast<std::uint32_t>(s.clip->frames.size());
    s.status.seconds         = s.clip->frames.back().time;
    s.status.appearanceBytes = s.clip->appearance.size();
    s.status.poseBytes       = s.clip->metadata.memoryPoseBytes;
    s.status.buildMs         = std::chrono::duration<double, std::milli>(Clock::now() - started).count();
    s.status.loading         = false;
    s.status.ready           = true;
    s.status.status          = "Архив загружен — выбери варианты и воспроизведи";
    s.dirty                  = true;
    logger::info(
      "[Phantom] comparison loaded: {}; {} full / {} selected channels; float {} / quantized {} bytes",
      s.status.loadedArchive,
      s.nodes.size(),
      s.replay->selected.channels.size(),
      s.replay->selected.Bytes(),
      s.replay->quantized.Bytes());
  }

  RE::NiTransform Mix(const RE::NiTransform& a, const RE::NiTransform& b, float t)
  {
    RE::NiQuaternion qa{a.rotate}, qb{b.rotate};
    qb.Correct(qa);
    RE::NiQuaternion q{std::lerp(qa.w, qb.w, t), std::lerp(qa.x, qb.x, t), std::lerp(qa.y, qb.y, t), std::lerp(qa.z, qb.z, t)};
    const auto       norm = std::sqrt(q.Dot(q));
    if (norm > 0.00001f)
    {
      q.w /= norm;
      q.x /= norm;
      q.y /= norm;
      q.z /= norm;
    }
    RE::NiTransform result;
    result.rotate    = q.ToRotation();
    result.translate = a.translate + (b.translate - a.translate) * t;
    result.scale     = std::lerp(a.scale, b.scale, t);
    return result;
  }

  void Play(State& s)
  {
    const auto  upper = std::ranges::upper_bound(s.clip->frames, s.elapsed, {}, &Frame::time);
    const auto  i     = upper == s.clip->frames.begin() ? 0 : static_cast<std::size_t>(upper - s.clip->frames.begin() - 1);
    const auto& a     = s.clip->frames[i];
    const auto& b     = s.clip->frames[std::min(i + 1, s.clip->frames.size() - 1)];
    const float t     = b.time > a.time ? static_cast<float>(std::clamp((s.elapsed - a.time) / (b.time - a.time), 0.0, 1.0)) : 0.0f;
    for (std::size_t n = 0; n < s.nodes.size(); ++n)
    {
      auto* node = s.nodes[n];
      if (!node) continue;
      if (s.poseMode != "full") continue;
      node->previousWorld     = node->world;
      node->world             = Mix(a.pose[n].world, b.pose[n].world, t);
      node->worldBound        = a.pose[n].bound;
      node->worldBound.center = a.pose[n].bound.center + (b.pose[n].bound.center - a.pose[n].bound.center) * t;
      node->worldBound.radius = std::max(a.pose[n].bound.radius, b.pose[n].bound.radius);
      node->GetFlags().set(s.excluded[n] || a.pose[n].hidden, Flag::kHidden);
      node->GetFadeAmount() = 1.0f;
    }
    if (s.replay && s.poseMode != "full")
    {
      const auto& clip   = s.poseMode == "quantized" ? s.replay->quantized : s.replay->selected;
      const auto& frames = s.poseMode == "quantized" ? s.replay->quantizedPoses : s.replay->selectedPoses;
      const auto  next   = std::min(i + 1, frames.size() - 1);
      for (std::size_t c = 0; c < clip.channels.size(); ++c)
      {
        const auto n    = clip.channels[c];
        auto*      node = s.nodes[n];
        if (!node) throw std::runtime_error("Модель потеряла выбранный канал");
        const auto first = NativePose(frames[i][c]), second = NativePose(frames[next][c]);
        node->previousWorld = node->world;
        node->world         = Mix(first.world, second.world, t);
        node->worldBound    = {node->world * s.localBounds[n].center, std::abs(node->world.scale) * s.localBounds[n].radius};
        node->GetFlags().set(s.excluded[n] || first.hidden, Flag::kHidden);
        node->GetFadeAmount() = 1.0f;
      }
    }
    for (auto* node : s.nodes)
      if (node)
        node->local =
          node->parent && std::abs(node->parent->world.scale) > 0.000001f ? node->parent->world.Invert() * node->world : node->world;
    // World poses follow live weapon reparenting, but our parents stay fixed.
    // Copied live container bounds may now be empty or surround the sheath,
    // causing the culler to reject geometry which is actually in the hand.
    // Collect is preorder: rebuild from leaves upward using the replay edges.
    for (auto* object : s.nodes | std::views::reverse)
      if (auto* node = object ? object->AsNode() : nullptr)
      {
        RE::NiBound bound{};
        for (const auto& child : node->GetChildren())
          if (child) PhantomCapture::Enclose(bound, child->worldBound);
        node->worldBound = bound;
      }
    for (auto* object : s.nodes)
      if (object)
        if (auto* box = object->GetVROcclusionBox()) PhantomCapture::OcclusionBox(object->worldBound, *box);
    s.root->GetFlags().reset(Flag::kHidden);
    for (auto* skin : s.skins)
      skin->frameID = std::numeric_limits<std::uint32_t>::max();
    for (auto* surface : s.surfaces)
      ApplyLook(*surface);
    s.status.seconds = std::min(s.elapsed, s.clip->frames.back().time);
  }

  Bridge::PhantomEvent Command(const Bridge::Commands::Phantom& command)
  {
    auto& s = Get();
    if (!s.status.supported) return s.status;
    if (command.action == "clear")
      Reset();
    else if (command.action == "stop")
    {
      s.pending.clear();
      s.status.loading = false;
      Stop(s, "Запись остановлена");
      s.status.playing = false;
      Detach(s);
    }
    else if (command.action == "record")
    {
      if (ArchiveWriter().Busy() || ArchiveLoader().valid())
      {
        s.status.status = "Дождись сохранения предыдущей записи";
        s.dirty         = true;
        return s.status;
      }
      Reset();
      s.status.rate   = command.rate;
      s.scenario      = command.scenario;
      s.status.status = "Ожидание обновления персонажа";
      s.pending       = "record";
    }
    else if (command.action == "load")
    {
      if (ArchiveWriter().Busy() || ArchiveLoader().valid() || s.status.recording || s.status.playing)
      {
        s.status.status = "Дождись завершения записи или загрузки";
        s.dirty         = true;
        return s.status;
      }
      const auto logs = SKSE::log::log_directory();
      if (!logs)
      {
        s.status.status = "Не найдена папка логов SKSE";
        s.dirty         = true;
        return s.status;
      }
      Reset();
      s.status.rate    = command.rate;
      s.scenario       = command.scenario;
      s.status.loading = true;
      s.status.status  = "Загрузка архива и подготовка форматов поз…";
      try
      {
        ArchiveLoader() =
          std::async(std::launch::async, [base = *logs / "DreamsleevePhantoms", scenario = command.scenario, rate = command.rate] {
            return PhantomReplay::Prepare(PhantomArchive::Latest(base, scenario, rate));
          });
      }
      catch (const std::exception& error)
      {
        Fail(s, error.what());
      }
    }
    else if (command.action == "play" && s.status.ready && !s.status.recording)
    {
      if (!s.replay && (command.poseMode != "full" || command.modelMode != "original"))
      {
        s.status.status = "Для сравнения сначала загрузи сохранённый архив";
        s.dirty         = true;
        return s.status;
      }
      s.poseMode  = command.poseMode;
      s.modelMode = command.modelMode;
      s.pending   = "play";
    }
    s.dirty = true;
    return s.status;
  }

  void Tick(RE::PlayerCharacter* player, float delta)
  {
    auto& s = Get();
    if (!s.status.supported || !player) return;
    auto* ui = RE::UI::GetSingleton();
    if (!ui || ui->GameIsPaused()) return;
    try
    {
      LoadReady(s, *player);
      if (s.pending == "record")
      {
        s.pending.clear();
        Start(s, *player);
      }
      if (!s.root) return;
      const auto owner = s.player.get();
      auto*      cell  = player->GetParentCell();
      // ParentCell can be temporarily unavailable during an engine transition.
      // Do not erase a valid fragment because one update had no cell.
      if (!cell) return;
      auto*                       world = cell->IsExteriorCell() ? player->GetWorldspace() : nullptr;
      const PhantomCapture::Space currentSpace{cell->GetFormID(), world ? world->GetFormID() : 0};
      if (owner.get() != player || !s.space.Contains(currentSpace))
      {
        logger::info(
          "[Phantom] reset: player match {}; cell {:08X}->{:08X}; world {:08X}->{:08X}; {:.2f} seconds",
          owner.get() == player,
          s.space.cell,
          currentSpace.cell,
          s.space.world,
          currentSpace.world,
          s.elapsed);
        Reset();
        return;
      }
      if (s.pending == "play")
      {
        s.pending.clear();
        if (s.replay) InstallModel(s, s.modelMode == "pruned");
        s.status.replayChannels  = s.poseMode == "full" ? static_cast<std::uint32_t>(s.nodes.size())
                                                        : static_cast<std::uint32_t>(s.replay->selected.channels.size());
        s.status.replayPoseBytes = s.poseMode == "full"
                                   ? s.clip->metadata.filePoseBytes
                                   : (s.poseMode == "quantized" ? s.replay->quantized.Bytes() : s.replay->selected.Bytes());
        logger::info(
          "[Phantom] replay: poses {}; model {}; {} channels; {} pose bytes; {} appearance bytes",
          s.poseMode,
          s.modelMode,
          s.status.replayChannels,
          s.status.replayPoseBytes,
          s.modelMode == "pruned" ? s.prunedAppearance.size() : s.clip->appearance.size());
        const auto* loaded = cell->GetRuntimeData().loadedData;
        auto*       parent = loaded ? loaded->cell3D.get() : nullptr;
        if (!parent) throw std::runtime_error("Нет родителя модели в сцене");
        Detach(s);
        s.parent = RE::NiPointer<RE::NiNode>{parent};
        parent->AttachChild(s.root.get(), true);
        s.elapsed        = 0;
        s.status.playing = true;
        s.status.status  = "Воспроизведение в месте записи";
      }
      const double step = std::clamp(static_cast<double>(delta), 0.0, 0.25);
      if (s.status.recording)
      {
        if (player->Get3D(false) != s.source.get())
        {
          Stop(s, "3D персонажа полностью заменено; фрагмент сохранён");
          return;
        }
        s.elapsed += step;
        if (s.elapsed >= s.nextSample)
        {
          if ((s.clip->frames.size() + 1) * s.bindings.size() * sizeof(Pose) > MaxBytes)
          {
            Stop(s, "Достигнут лимит записи 128 МиБ");
            return;
          }
          Sample(s);
          // No catch-up duplicates after a hitch: each frame is a real pose.
          s.nextSample = (std::floor(s.elapsed * s.status.rate) + 1.0) / s.status.rate;
        }
        if (s.elapsed >= Duration) Stop(s, "Запись готова — можно воспроизвести");
      }
      else if (s.status.playing)
      {
        Play(s);
        s.elapsed += step;
        if (s.elapsed > s.clip->frames.back().time)
        {
          s.status.playing = false;
          s.status.status  = "Воспроизведение завершено";
          Detach(s);
          s.dirty = true;
        }
      }
    }
    catch (const std::exception& error)
    {
      Fail(s, error.what());
    }
  }

  std::optional<Bridge::PhantomEvent> PollStatus()
  {
    auto&      s         = Get();
    const bool exporting = ArchiveWriter().Busy();
    if (s.status.exporting != exporting)
    {
      s.status.exporting = exporting;
      s.dirty            = true;
    }
    if (auto result = ArchiveWriter().Poll())
    {
      s.status.exportPath  = result->path;
      s.status.exportError = result->error;
      s.dirty              = true;
      if (result->error.empty())
        logger::info("[Phantom] archive saved: {}; {:.2f} ms", result->path, result->milliseconds);
      else
        logger::error("[Phantom] archive failed: {}; {}", result->path, result->error);
    }
    if (auto result = ComparisonWriter().Poll())
    {
      if (result->error.empty())
        logger::info("[Phantom] comparison files saved: {}; {:.2f} ms", result->path, result->milliseconds);
      else
        logger::error("[Phantom] comparison files failed: {}; {}", result->path, result->error);
    }
    const auto now = Clock::now();
    if (!s.dirty && (!(s.status.recording || s.status.playing) || now < s.nextStatus)) return std::nullopt;
    s.dirty      = false;
    s.nextStatus = now + std::chrono::milliseconds{250};
    return s.status;
  }

  void Shutdown()
  {
    ArchiveWriter().Shutdown();
    ComparisonWriter().Shutdown();
    if (ArchiveLoader().valid()) ArchiveLoader().wait();
  }

}
