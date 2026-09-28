#include "Prelude.hpp"

import Dreamsleeve.Logging;
import Dreamsleeve.SKSEMenu;
import Dreamsleeve.Events;
import Dreamsleeve.Hooks;

void SkseMessageHandle(SKSE::MessagingInterface::Message* message)
{
  switch (message->type)
  {
    case SKSE::MessagingInterface::kPostLoad: {
      break;
    }
    case SKSE::MessagingInterface::kPostPostLoad: {
      break;
    }
    case SKSE::MessagingInterface::kInputLoaded: {
      break;
    }
    case SKSE::MessagingInterface::kDataLoaded: {
      Hooks::InstallHooks();
      Events::RegisterEvents();
      break;
    }
    case SKSE::MessagingInterface::kNewGame: {
      break;
    }
    case SKSE::MessagingInterface::kPreLoadGame: {
      break;
    }
    case SKSE::MessagingInterface::kPostLoadGame: {
      break;
    }
    case SKSE::MessagingInterface::kSaveGame: {
      break;
    }
    case SKSE::MessagingInterface::kDeleteGame: {
      break;
    }
    default:
      break;
  }
}

SKSEPluginLoad(const SKSE::LoadInterface* skse)
{
  Logging::SetupLog();

  const auto plugin = SKSE::PluginDeclaration::GetSingleton();
  logger::info("{} v{} is loading...", plugin->GetName(), plugin->GetVersion());

  SKSE::Init(skse);

  SKSE::GetMessagingInterface()->RegisterListener(SkseMessageHandle);

  SKSEMenu::RegisterSKSEMenu();

  logger::info("{} has finished loading.", plugin->GetName());

  return true;
}
