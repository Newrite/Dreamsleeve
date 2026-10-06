module;

#include "Prelude.hpp"

export module Dreamsleeve.Hooks;

import std;
import Dreamsleeve.Logic;
import Dreamsleeve.Runtime;
import Dreamsleeve.UI.Nameplates;
import Dreamsleeve.Game.Input;
import Dreamsleeve.Game.Phantoms;
import Dreamsleeve.Game.PhantomNative;

// Every patch of the game binary lives here: Address Library IDs, call-site
// offsets, byte checks and the thunks. The modules behind the thunks (Logic,
// Nameplates, Input) know nothing about addresses.
namespace Hooks
{

  namespace Address
  {

    // The window message loop: SE 0x1405AF3D0 (1.5.97), AE WinMain 0x14063E970
    // (1.6.1170), VR 0x1405B6D70 (1.4.15). Its per-iteration call of
    // Main::Update runs while paused, in the main menu and under loading screens.
    auto MainUpdate = REL::RelocationID(35551, 36544);

    // BSInputDeviceManager::PollInputDevices: SE 0x140C150B0 (1.5.97),
    // AE 0x140CD8F40 (1.6.1170), VR 0x140C519E0 (1.4.15). Called from Main::Update.
    auto PollInputDevices = REL::RelocationID(67315, 68617, 67315);

    // The input dispatcher PollInputDevices calls: SE 0x140C15E00, AE 0x140CD9E00.
    // VR (0x140C52BC0) has no entry in its database, so VR is only range-checked.
    auto DispatchInput = REL::RelocationID(67355, 68655);

    constexpr auto StreamCtor   = REL::VariantID(68971, 70324, 0xC9EC40);
    constexpr auto StreamDtor   = REL::VariantID(68972, 70325, 0xC9EEA0);
    constexpr auto StreamLoad   = REL::VariantID(68978, 70331, 0xC9F470);
    constexpr auto StreamSave   = REL::VariantID(68979, 70332, 0xC9F4C0);
    constexpr auto AlphaFactory = REL::VariantID(69311, 70684, 0xCADF10);
    // Actual string -> no-argument loader registry; the adjacent qword is not it.
    constexpr auto StreamLoaders = REL::VariantID(523904, 410484, 0x316AC08);

  }

  namespace Offset
  {

    // Verified in IDA: the `call Main::Update` inside the loop on each runtime;
    // VR shares the SE layout. A new game build needs its own entry here.
    auto MainUpdate = REL::Relocate(0x11F, 0x160);

    // `call DispatchInput` inside PollInputDevices; VR checks each device's
    // enabled flag in the poll loop above it, which shifts the site by 6.
    auto DispatchCall = REL::Relocate(0x7B, 0x7B, 0x81);

    // IMenu::AdvanceMovie in the HUDMenu vtable, SE and AE alike.
    constexpr std::size_t HudAdvanceMovie = 0x05;

    constexpr std::size_t ArrayFreeIndex = 0x14, ArraySize = 0x18;
    constexpr std::size_t LoaderBuckets = 0x08, LoaderTable = 0x10, LoaderCount = 0x18;
    constexpr std::size_t LoaderNext = 0x00, LoaderName = 0x08, LoaderFactory = 0x10;

  }

  // Bytes right before the dispatch call on all three runtimes:
  // `mov [rsp+40h], rcx; mov rcx, rsi`. The head pointer goes to the stack
  // slot whose address is the second argument; the manager is the first.
  constexpr std::uint8_t DispatchPrologue[] = {0x48, 0x89, 0x4C, 0x24, 0x40, 0x48, 0x8B, 0xCE};
  constexpr std::uint8_t CallOpcode         = 0xE8;
  constexpr std::size_t  CallSize           = 5;

  bool InstallPhantomNative();

  struct MainUpdate
  {
    static void Update(RE::Main* self)
    {
      // kDataLoaded runs on the loader's thread. Bind native scene ownership to
      // this verified main-loop entry instead, before any frame work.
      static const bool nativeInstalled = InstallPhantomNative();
      (void)nativeInstalled;
      UpdateOriginal(self);
      Logic::OnFrame();
    }

    static inline REL::Relocation<decltype(Update)> UpdateOriginal;
  };

  // Firefly names are drawn once the HUD has advanced its own movie.
  struct HudAdvance
  {
    static void AdvanceMovie(RE::HUDMenu* menu, float interval, std::uint32_t time)
    {
      Original(menu, interval, time);
      Nameplates::Advance(menu);
    }

    static inline REL::Relocation<decltype(AdvanceMovie)> Original;
  };

  // The frame's input chain before any sink sees it; Input decides what passes.
  struct InputDispatch
  {
    static void Dispatch(Input::Source* source, RE::InputEvent** events)
    {
      Input::Dispatch(source, events, [](Input::Source* manager, RE::InputEvent** chain) { Original(manager, chain); });
    }

    static inline REL::Relocation<decltype(Dispatch)> Original;
  };

  namespace P = Dreamsleeve::Client::Phantom;
  std::thread::id phantomThread;

  bool PhantomThread() noexcept
  {
    return std::this_thread::get_id() == phantomThread;
  }

  struct StreamDeleter
  {
    void operator()(RE::NiStream* stream) const
    {
      if (!stream) return;
      // Non-deleting destructor, followed by its matching engine allocator.
      REL::Relocation<void(RE::NiStream*)>{Address::StreamDtor}(stream);
      RE::free(stream);
    }
  };

  auto CreateStream()
  {
    auto* memory = static_cast<RE::NiStream*>(RE::malloc(0x620));
    if (!memory) throw std::bad_alloc{};
    auto* stream = REL::Relocation<RE::NiStream*(RE::NiStream*)>{Address::StreamCtor}(memory);
    return std::unique_ptr<RE::NiStream, StreamDeleter>{stream};
  }

  void SeedStream(RE::NiStream& stream, RE::NiNode* root)
  {
    auto& top   = stream.topObjects;
    using Array = std::remove_reference_t<decltype(top)>;
    top.~Array();
    new (&top) Array(1);
    new (top.begin()) RE::NiPointer<RE::NiObject>{root};
    const std::uint32_t one = 1;
    std::memcpy(reinterpret_cast<std::byte*>(&top) + Offset::ArrayFreeIndex, &one, sizeof(one));
    std::memcpy(reinterpret_cast<std::byte*>(&top) + Offset::ArraySize, &one, sizeof(one));
  }

  template <class T>
  T StreamField(const void* object, std::size_t offset)
  {
    T value{};
    std::memcpy(&value, static_cast<const std::byte*>(object) + offset, sizeof(value));
    return value;
  }

  std::expected<void, std::string> AuditPhantom(RE::NiNode* root)
  {
    // SaveStream clears objects after saving. Audit a separate registration
    // pass, including skins/properties/data, using the same streamable RTTI
    // (virtual slot 20) that the engine writes into its type catalog.
    auto audit = CreateStream();
    SeedStream(*audit, root);
    audit->RegisterObjects();
    if (!audit->objects.size() || audit->objects.size() > 65536) return std::unexpected{"NiStream preflight: invalid object count"};
    const auto* registry = *REL::Relocation<const void**>{Address::StreamLoaders};
    if (!registry) return std::unexpected{"NiStream preflight: loader registry unavailable"};
    const auto  buckets = StreamField<std::uint32_t>(registry, Offset::LoaderBuckets);
    const auto  count   = StreamField<std::uint32_t>(registry, Offset::LoaderCount);
    const auto* table   = StreamField<const void* const*>(registry, Offset::LoaderTable);
    if (!table || !buckets || buckets > 65536 || count > 65536)
      return std::unexpected{"NiStream preflight: invalid loader registry layout"};
    std::unordered_set<std::string> factories;
    std::size_t                     visited = 0;
    for (std::uint32_t i = 0; i < buckets; ++i)
      for (const void* entry = table[i]; entry; entry = StreamField<const void*>(entry, Offset::LoaderNext))
      {
        if (++visited > count) return std::unexpected{"NiStream preflight: loader registry chain/count mismatch"};
        const auto* name = StreamField<const char*>(entry, Offset::LoaderName);
        if (name && StreamField<std::uintptr_t>(entry, Offset::LoaderFactory)) factories.emplace(name);
      }
    if (visited != count) return std::unexpected{"NiStream preflight: incomplete loader registry"};
    std::map<std::string, std::size_t> types;
    for (const auto& object : audit->objects)
    {
      const auto* type = object ? object->GetStreamableRTTI() : nullptr;
      if (!type || !type->GetName()) return std::unexpected{"NiStream preflight: missing streamable RTTI"};
      ++types[type->GetName()];
    }
    std::string missing;
    logger::info(
      "[Phantom] NiStream preflight: {} objects, {} types, {} loader factories",
      audit->objects.size(),
      types.size(),
      factories.size());
    for (const auto& [name, instances] : types)
    {
      const bool present = factories.contains(name);
      logger::info("[Phantom] NiStream type: {} x{} — {}", name, instances, present ? "loader present" : "MISSING loader");
      if (!present)
      {
        if (!missing.empty()) missing += ", ";
        missing += std::format("{} x{}", name, instances);
      }
    }
    if (!missing.empty()) return std::unexpected{"NiStream missing loaders: " + missing};
    return {};
  }

  P::Result<std::vector<std::uint8_t>> SavePhantom(RE::NiNode* root)
  {
    if (!PhantomThread()) return std::unexpected(P::Error{P::Failure::Busy, "native.thread"});
    if (auto audited = AuditPhantom(root); !audited) return std::unexpected(P::Error{P::Failure::InvalidFormat, audited.error()});
    auto stream = CreateStream();
    SeedStream(*stream, root);
    char*         output = nullptr;
    std::uint32_t length = 0;
    // All three verified runtimes use a uint32 length reference, not CommonLib's
    // uint64 declaration. NiMemStream::releaseBuffer transfers RE::malloc storage.
    const bool saved = REL::Relocation<bool(RE::NiStream*, char*&, std::uint32_t&)>{Address::StreamSave}(stream.get(), output, length);
    std::unique_ptr<char, decltype(&RE::free)> buffer{output, &RE::free};
    if (!saved || !output || !length)
      return std::unexpected(
        P::Error{P::Failure::InvalidFormat, std::format("NiStream Save: {} {}", stream->lastError, stream->lastErrorMessage)});
    if (length > P::Limits{}.assetBytes) return std::unexpected(P::Error{P::Failure::LimitExceeded, "native.raw-bytes"});
    return std::vector<std::uint8_t>{output, output + length};
  }

  P::Result<RE::NiPointer<RE::NiNode>> LoadPhantom(const P::ValidatedAsset& asset)
  {
    if (!PhantomThread()) return std::unexpected(P::Error{P::Failure::Busy, "native.thread"});
    const auto&                                                             bytes  = asset.Value().nif;
    auto                                                                    stream = CreateStream();
    static const REL::Relocation<bool(RE::NiStream*, char*, std::uint32_t)> load{Address::StreamLoad};
    const bool                                                              loaded =
      load(stream.get(), reinterpret_cast<char*>(const_cast<std::uint8_t*>(bytes.data())), static_cast<std::uint32_t>(bytes.size()));
    if (!loaded || stream->topObjects.size() != 1 || !stream->topObjects[0] || !stream->topObjects[0]->AsNode())
      return std::unexpected(
        P::Error{P::Failure::InvalidFormat, std::format("NiStream Load: {} {}", stream->lastError, stream->lastErrorMessage)});
    return RE::NiPointer<RE::NiNode>{stream->topObjects[0]->AsNode()};
  }

  RE::NiAlphaProperty* PhantomAlpha()
  {
    return REL::Relocation<RE::NiAlphaProperty*()>{Address::AlphaFactory}();
  }

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

  bool InstallPhantomNative()
  {
    const auto version = REL::Module::get().version();
    const bool known   = (REL::Module::IsSE() && version == REL::Version{1, 5, 97, 0}) ||
                         (REL::Module::IsAE() && version == REL::Version{1, 6, 1170, 0}) ||
                         (REL::Module::IsVR() && version == REL::Version{1, 4, 15, 0});
    if (!known)
    {
      logger::warn("Native phantom disabled for unaudited runtime {}", version.string());
      return false;
    }
    phantomThread = std::this_thread::get_id();
    const Dreamsleeve::Game::PhantomNative::Engine engine{PhantomThread, SavePhantom, LoadPhantom, PhantomAlpha, Normalize};
    Phantoms::Install(engine);
    logger::info("Native NiStream phantom operations bound to Main::Update, runtime {}", version.string());
    return true;
  }

  void InstallMainUpdate()
  {
    MainUpdate::UpdateOriginal =
      SKSE::GetTrampoline().write_call<5>(Address::MainUpdate.address() + Offset::MainUpdate, MainUpdate::Update);
    logger::info("Main::Update hook installed (runtime {})", REL::Module::get().version().string());
  }

  void InstallHudAdvance()
  {
    if (REL::Module::IsVR())
    {
      logger::info("Firefly names: flat HUD renderer disabled in VR (stereo renderer pending)");
      return;
    }
    REL::Relocation<std::uintptr_t> vtable{RE::HUDMenu::VTABLE[0]};
    HudAdvance::Original = vtable.write_vfunc(Offset::HudAdvanceMovie, HudAdvance::AdvanceMovie);
    logger::info("HUDMenu::AdvanceMovie hook installed (firefly names)");
  }

  // The site is checked byte for byte before the patch: a runtime this table
  // does not know keeps its input untouched and logs why.
  void InstallInputDispatch()
  {
    if (const auto* settings = Runtime::Settings(); settings && !settings->client.captureKeyboard)
    {
      logger::info("Keyboard capture disabled by client.toml (captureKeyboard = false)");
      return;
    }

    const auto  version = REL::Module::get().version().string();
    const auto  site    = Address::PollInputDevices.address() + Offset::DispatchCall;
    const auto* code    = reinterpret_cast<const std::uint8_t*>(site);
    if (!std::equal(std::begin(DispatchPrologue), std::end(DispatchPrologue), code - sizeof(DispatchPrologue)) || code[0] != CallOpcode)
    {
      logger::error("Input dispatch call site {:X} does not match the known bytes; keyboard capture disabled (runtime {})", site, version);
      return;
    }

    std::int32_t displacement = 0;
    std::memcpy(&displacement, code + 1, sizeof(displacement));
    const auto target = site + CallSize + static_cast<std::intptr_t>(displacement);
    const auto text   = REL::Module::get().segment(REL::Segment::textx);
    const bool inText = target >= text.address() && target < text.address() + text.size();
    if (REL::Module::IsVR())
    {
      if (!inText) logger::info("Input dispatch call already redirected to {:X}; chaining behind it", target);
    }
    else if (const auto expected = Address::DispatchInput.address(); target != expected)
    {
      if (inText)
      {
        logger::error(
          "Input dispatch call site {:X} targets {:X}, expected {:X}; keyboard capture disabled (runtime {})",
          site,
          target,
          expected,
          version);
        return;
      }
      logger::info("Input dispatch call already redirected to {:X}; chaining behind it", target);
    }

    InputDispatch::Original = SKSE::GetTrampoline().write_call<5>(site, InputDispatch::Dispatch);
    logger::info("Input dispatch hook installed at {:X} (runtime {})", site, version);
  }

  // kDataLoaded, once. Two 5-byte calls go through the trampoline.
  export void InstallHooks()
  {
    static bool installed = false;
    if (installed) return;
    installed = true;
    InstallHudAdvance();
    InstallInputDispatch();
    InstallMainUpdate();
  }

}
