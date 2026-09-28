#include "Prelude.hpp"

import Dreamsleeve.Logging;
import Dreamsleeve.SKSEMenu;

void SkseMessageHandle(SKSE::MessagingInterface::Message* message)
{
  switch (message->type)
  {
    case SKSE::MessagingInterface::kPostLoad:
    case SKSE::MessagingInterface::kPostPostLoad:
    case SKSE::MessagingInterface::kInputLoaded:
    case SKSE::MessagingInterface::kDataLoaded:
    case SKSE::MessagingInterface::kNewGame:
    case SKSE::MessagingInterface::kPreLoadGame:
    case SKSE::MessagingInterface::kPostLoadGame:
    case SKSE::MessagingInterface::kSaveGame:
    case SKSE::MessagingInterface::kDeleteGame:
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
