module;

#include "Prelude.hpp"

export module Dreamsleeve.Plugin;

import std;
import Dreamsleeve.Logging;
import Dreamsleeve.Runtime;
import Dreamsleeve.SKSEMenu;
import Dreamsleeve.Events;
import Dreamsleeve.Hooks;
import Dreamsleeve.PrismaUI;
import Dreamsleeve.Game.Fireflies;

// SKSE lifecycle. PluginManager::Dispatch_Message calls this on the sender's
// thread with borrowed payloads, so handlers only record notices; the frame
// hook does the actual work on the game thread. See docs/SkseClientRu.md.
namespace Plugin
{

  void OnPostPostLoad()
  {
    PrismaUI::RequestApi();
    if (!Runtime::Initialize()) return;
    Events::SetActivationKey(Runtime::Get().ui.ui.chat.activationKey);
  }

  void OnDataLoaded()
  {
    auto& runtime      = Runtime::Get();
    runtime.dataLoaded = true;
    Hooks::InstallHooks();
    Events::RegisterEvents();
    Fireflies::ResolveForms();
    PrismaUI::CreateView();
  }

  void SkseMessageHandle(SKSE::MessagingInterface::Message* message)
  {
    if (!message) return;
    switch (message->type)
    {
      case SKSE::MessagingInterface::kPostLoad:
        break;
      case SKSE::MessagingInterface::kPostPostLoad:
        OnPostPostLoad();
        break;
      case SKSE::MessagingInterface::kInputLoaded:
        break;
      case SKSE::MessagingInterface::kDataLoaded:
        OnDataLoaded();
        break;
      case SKSE::MessagingInterface::kNewGame:
        // data is the CharGen TESQuest*; the world is not ready yet.
        Runtime::Post({Runtime::NoticeKind::NewGame});
        break;
      case SKSE::MessagingInterface::kPreLoadGame:
        // data is a borrowed save name; the previous world becomes stale now.
        Runtime::Post({Runtime::NoticeKind::PreLoadGame});
        break;
      case SKSE::MessagingInterface::kPostLoadGame:
        // The result is encoded in the pointer itself: non-null means loaded.
        Runtime::Post({Runtime::NoticeKind::PostLoadGame, message->data != nullptr});
        break;
      case SKSE::MessagingInterface::kSaveGame:
        // Sent before the file is written; not a success confirmation.
        Runtime::Post({Runtime::NoticeKind::SaveGame});
        break;
      case SKSE::MessagingInterface::kDeleteGame:
        // A save file was deleted; the current game and the session are unaffected.
        break;
      default:
        break;
    }
  }

  export bool Load(const SKSE::LoadInterface* skse)
  {
    Logging::SetupLog();

    const auto plugin = SKSE::PluginDeclaration::GetSingleton();
    logger::info("{} v{} is loading on runtime {}", plugin->GetName(), plugin->GetVersion(), REL::Module::get().version().string());

    // The logger above is ours; one 5-byte call needs 14 trampoline bytes.
    SKSE::Init(skse, {.log = false, .trampoline = true, .trampolineSize = 64});

    const auto messaging = SKSE::GetMessagingInterface();
    if (!messaging || !messaging->RegisterListener(SkseMessageHandle))
    {
      logger::critical("Cannot register the SKSE messaging listener");
      return false;
    }

    SKSEMenu::RegisterSKSEMenu();

    logger::info("{} has finished loading.", plugin->GetName());
    return true;
  }

}
