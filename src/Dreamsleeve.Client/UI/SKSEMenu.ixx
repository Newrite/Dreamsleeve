module;

#include "Prelude.hpp"
#include "API/SKSEMenuFramework.h"

export module Dreamsleeve.SKSEMenu;

namespace SKSEMenu
{

  export auto RegisterSKSEMenu() -> void
  {
    if (!SKSEMenuFramework::IsInstalled())
    {
      logger::warn("SKSEMenuFramework not installed");
      return;
    }

    static constexpr auto main_title = "Dreamsleeve Client";
    SKSEMenuFramework::SetSection(main_title);
  }

}
