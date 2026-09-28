module;

#include "Prelude.hpp"

export module Dreamsleeve.Hooks;

import Dreamsleeve.Runtime;
import Dreamsleeve.Logic;

namespace Hooks
{

  namespace Address
  {

    auto MainUpdate = REL::RelocationID(35551, 36544);

  }

  namespace Offset
  {

    auto MainUpdate = REL::Relocate(0x11F, 0x160);

  }

  struct MainUpdate
  {
    static void Update(RE::Main* this_, float shouldBeDelta)
    {
      UpdateOriginal(this_, shouldBeDelta);
    }

    static inline REL::Relocation<decltype(Update)> UpdateOriginal;
  };

  export void InstallHooks()
  {
    auto& trampoline = SKSE::GetTrampoline();
    trampoline.create(1024);

    MainUpdate::UpdateOriginal = trampoline.write_call<5>(Address::MainUpdate.address() + Offset::MainUpdate, MainUpdate::Update);
  }

}
