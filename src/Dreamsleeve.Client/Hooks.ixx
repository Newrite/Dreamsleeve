module;

#include "Prelude.hpp"

export module Dreamsleeve.Hooks;

import std;
import Dreamsleeve.Logic;
import Dreamsleeve.UI.Nameplates;

namespace Hooks
{

  namespace Address
  {

    // The window message loop: SE 0x1405AF3D0 (1.5.97), AE WinMain 0x14063E970
    // (1.6.1170), VR 0x1405B6D70 (1.4.15). Its per-iteration call of
    // Main::Update runs while paused, in the main menu and under loading screens.
    auto MainUpdate = REL::RelocationID(35551, 36544);

  }

  namespace Offset
  {

    // Verified in IDA: the `call Main::Update` inside the loop on each runtime;
    // VR shares the SE layout. A new game build needs its own entry here.
    auto MainUpdate = REL::Relocate(0x11F, 0x160);

  }

  struct MainUpdate
  {
    static void Update(RE::Main* self)
    {
      UpdateOriginal(self);
      Logic::OnFrame();
    }

    static inline REL::Relocation<decltype(Update)> UpdateOriginal;
  };

  export void InstallHooks()
  {
    static bool installed = false;
    if (installed) return;
    installed = true;
    Nameplates::Install();

    auto& trampoline           = SKSE::GetTrampoline();
    MainUpdate::UpdateOriginal = trampoline.write_call<5>(Address::MainUpdate.address() + Offset::MainUpdate, MainUpdate::Update);
    logger::info("Main::Update hook installed (runtime {})", REL::Module::get().version().string());
  }

}
