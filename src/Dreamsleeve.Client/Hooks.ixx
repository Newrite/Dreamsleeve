module;

#include "Prelude.hpp"

export module Dreamsleeve.Hooks;

import std;
import Dreamsleeve.Logic;
import Dreamsleeve.Runtime;
import Dreamsleeve.UI.Nameplates;
import Dreamsleeve.Game.Input;

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

  }

  // Bytes right before the dispatch call on all three runtimes:
  // `mov [rsp+40h], rcx; mov rcx, rsi`. The head pointer goes to the stack
  // slot whose address is the second argument; the manager is the first.
  constexpr std::uint8_t DispatchPrologue[] = {0x48, 0x89, 0x4C, 0x24, 0x40, 0x48, 0x8B, 0xCE};
  constexpr std::uint8_t CallOpcode         = 0xE8;
  constexpr std::size_t  CallSize           = 5;

  struct MainUpdate
  {
    static void Update(RE::Main* self)
    {
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
