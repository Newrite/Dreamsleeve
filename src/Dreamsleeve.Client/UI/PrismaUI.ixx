module;

#include "Prelude.hpp"
#include "API/PrismaUI_API.h"

export module Dreamsleeve.PrismaUI;

import std;
import Dreamsleeve.Game.Phantom;
import Dreamsleeve.Runtime;
import Dreamsleeve.Host.Commands;
import Dreamsleeve.Events;
import Dreamsleeve.Game.Input;
import Dreamsleeve.Game.World;
import Dreamsleeve.Client.Utils;

// PrismaUI host of the production web UI. PrismaUI 1.5.1 wraps every callback
// (DOM ready, JS listener, console) in SKSE::GetTaskInterface()->AddTask, so
// all of them run on the game main thread; nothing here needs a lock.
namespace PrismaUI
{

  namespace Bridge = Dreamsleeve::Host::Bridge;
  namespace Dream  = Dreamsleeve::Client;
  using Clock      = std::chrono::steady_clock;

  constexpr auto ViewPath   = "Dreamsleeve/index.html";
  constexpr auto Listener   = "dreamsleeveCommand";
  constexpr auto Receiver   = "dreamsleeveReceive";
  constexpr auto FocusGrace = std::chrono::milliseconds{1500};

  // Menus that hide the chat and block activation; recomputed from the whole
  // open set on every MenuOpenCloseEvent, never from the last event alone.
  constexpr std::string_view HidingMenus[] = {
      RE::LoadingMenu::MENU_NAME,
      RE::MainMenu::MENU_NAME,
      RE::TitleSequenceMenu::MENU_NAME,
      RE::InventoryMenu::MENU_NAME,
      RE::ContainerMenu::MENU_NAME,
      RE::BarterMenu::MENU_NAME,
      RE::CraftingMenu::MENU_NAME,
      RE::MagicMenu::MENU_NAME,
      RE::MapMenu::MENU_NAME,
      RE::JournalMenu::MENU_NAME,
      RE::StatsMenu::MENU_NAME,
      RE::TweenMenu::MENU_NAME,
      RE::SleepWaitMenu::MENU_NAME,
      RE::LockpickingMenu::MENU_NAME,
      RE::RaceSexMenu::MENU_NAME,
      RE::BookMenu::MENU_NAME,
      RE::GiftMenu::MENU_NAME,
      RE::TrainingMenu::MENU_NAME,
      RE::MessageBoxMenu::MENU_NAME,
      RE::LevelUpMenu::MENU_NAME,
      RE::FavoritesMenu::MENU_NAME,
      RE::TutorialMenu::MENU_NAME,
      RE::CreationClubMenu::MENU_NAME,
      RE::ModManagerMenu::MENU_NAME,
      RE::CreditsMenu::MENU_NAME,
      RE::DialogueMenu::MENU_NAME,
      RE::Console::MENU_NAME,
      RE::MistMenu::MENU_NAME,
  };

  struct State
  {
    PRISMA_UI_API::IVPrismaUI1* api{};
    PRISMA_UI_API::IVPrismaUI2* api2{};
    PrismaView                  view{};
    bool                        domReady{};
    bool                        active{};
    bool                        menuBlocked{};
    bool                        jsHidden{};
    bool                        nativeHidden{};
    Clock::time_point           focusGrace{};
  };

  State& Get()
  {
    static State state;
    return state;
  }

  // Chat focus and keyboard capture change together: while the chat is active
  // the input hook withholds keyboard events from the game and other mods.
  void SetActive(bool active)
  {
    Get().active = active;
    if (active)
      Input::BeginCapture();
    else
      Input::EndCapture();
  }

  bool ViewUsable()
  {
    auto& state = Get();
    return state.api && state.view && state.domReady && state.api->IsValid(state.view);
  }

  export bool Available()
  {
    return Get().api != nullptr;
  }

  export bool Active()
  {
    return Get().active;
  }

  export bool Visible()
  {
    auto& state = Get();
    return ViewUsable() && !Runtime::Get().ui.ui.hideUi && !state.menuBlocked;
  }

  void Send(const std::string& json)
  {
    auto& state = Get();
    if (!ViewUsable()) return;
    state.api->InteropCall(state.view, Receiver, json.c_str());
  }

  void Send(const Bridge::HostEvent& event)
  {
    if (auto json = Bridge::Encode(event))
      Send(*json);
    else
      logger::error("{}: {}", Bridge::TypeOf(event), json.error());
  }

  export void Dispatch(const std::vector<Bridge::HostEvent>& events)
  {
    for (const auto& event : events)
      Send(event);
  }

  export void Deactivate()
  {
    auto& state = Get();
    if (!state.active) return;
    SetActive(false);
    if (state.api && state.view) state.api->Unfocus(state.view);
    Send(Bridge::DeactivateEvent{});
  }

  // Full visibility policy: user opt-out, hiding menus, view readiness.
  void ApplyVisibility()
  {
    auto& state = Get();
    if (!ViewUsable()) return;
    if (Visible())
    {
      if (state.nativeHidden) state.api->Show(state.view);
      state.nativeHidden = false;
      if (state.jsHidden) Send(Bridge::ShowEvent{});
      state.jsHidden = false;
      return;
    }

    Deactivate();
    if (!state.jsHidden) Send(Bridge::HideEvent{});
    state.jsHidden = true;
    if (!state.nativeHidden) state.api->Hide(state.view);
    state.nativeHidden = true;
  }

  export void RecomputeMenus()
  {
    auto& state   = Get();
    auto* ui      = RE::UI::GetSingleton();
    bool  blocked = false;
    if (ui)
      for (const auto name : HidingMenus)
        if (ui->IsMenuOpen(name)) blocked = true;
    state.menuBlocked = blocked;
    ApplyVisibility();
  }

  export void ApplyUserSettings()
  {
    ApplyVisibility();
  }

  export void SendSettings()
  {
    Send(Bridge::SettingsEvent{.settings = Runtime::Get().ui.ui.chat});
  }

  bool ActivationAllowed()
  {
    auto* ui = RE::UI::GetSingleton();
    return Visible() && !Get().active && Runtime::Get().dataLoaded && ui && !ui->GameIsPaused();
  }

  export void RequestActivate()
  {
    auto& state = Get();
    RecomputeMenus();
    if (!ActivationAllowed()) return;
    if (!state.api->Focus(state.view, false, false))
    {
      logger::warn("PrismaUI refused focus");
      return;
    }
    SetActive(true);
    state.focusGrace = Clock::now() + FocusGrace;
    Send(Bridge::ActivateEvent{});
  }

  // The game can drop Prisma focus on its own (Escape closes the focus menu):
  // observe it after the asynchronous focus operation had time to settle.
  export void OnFrame(Clock::time_point now)
  {
    auto& state = Get();
    if (state.active)
      if (auto event = Phantom::PollStatus()) Send(*event);
    if (!state.active || !ViewUsable() || now < state.focusGrace) return;
    if (!state.api->HasFocus(state.view))
    {
      SetActive(false);
      Send(Bridge::DeactivateEvent{});
    }
  }

  void OnCommand(const char* json)
  {
    if (!json) return;
    auto command = Bridge::ParseCommand(json);
    if (!command)
    {
      logger::warn("UI command rejected: {}", command.error());
      return;
    }
    auto& runtime = Runtime::Get();
    if (!runtime.app) return;
    Dreamsleeve::Host::CommandContext context{
        .exchange         = runtime.app->Exchange(),
        .session          = runtime.session,
        .ui               = runtime.ui,
        .bubbles          = runtime.bubbles,
        .manualDisconnect = runtime.manualDisconnect,
        .ports            = {
                             .saveUi        = Runtime::SaveUi,
                             .close         = Deactivate,
                             .activationKey = Events::SetActivationKey,
                             .noteSpot      = World::Spot,
                             .copyText      = Dreamsleeve::Utils::Clipboard::Copy,
                             .phantom       = Phantom::Command
        }
    };
    const auto output = Dreamsleeve::Host::Handle(context, std::move(*command));
    for (const auto& note : output.notes)
      logger::warn("{}", note);
    Dispatch(output.events);
  }

  void OnConsole(PrismaView, PRISMA_UI_API::ConsoleMessageLevel level, const char* message)
  {
    const auto* text = message ? message : "";
    if (level == PRISMA_UI_API::ConsoleMessageLevel::Error)
      logger::error("[JS] {}", text);
    else if (level == PRISMA_UI_API::ConsoleMessageLevel::Warning)
      logger::warn("[JS] {}", text);
    else
      logger::debug("[JS] {}", text);
  }

  // Main thread (task interface). A recreated page starts from settings and a
  // fresh status/snapshot; the session drops correlations of the old page.
  void OnDomReady(PrismaView view)
  {
    auto& state = Get();
    if (view != state.view) return;
    state.domReady = true;
    SetActive(false);
    state.jsHidden = false;
    logger::info("Dreamsleeve view ready");
    Send(Bridge::SettingsEvent{.settings = Runtime::Get().ui.ui.chat});
    Send(Runtime::Get().session.IgnoredList(Runtime::Get().ui.ui.chat));
    Send(Phantom::Command({.action = "query"}));
    RecomputeMenus();
    Runtime::Get().session.ResetView();
  }

  // kPostPostLoad: every plugin has finished loading, so the API can be requested.
  export void RequestApi()
  {
    auto& state = Get();
    state.api   = PRISMA_UI_API::RequestPluginAPI<PRISMA_UI_API::IVPrismaUI1>();
    state.api2  = PRISMA_UI_API::RequestPluginAPI<PRISMA_UI_API::IVPrismaUI2>();
    if (!state.api) logger::warn("PrismaUI not installed; chat UI disabled, network keeps running");
  }

  // kDataLoaded: the view is created once; readiness arrives through OnDomReady.
  export void CreateView()
  {
    auto& state = Get();
    if (!state.api || state.view) return;
    state.view = state.api->CreateView(ViewPath, OnDomReady);
    if (!state.view)
    {
      logger::error("PrismaUI could not create {}", ViewPath);
      return;
    }
    state.api->RegisterJSListener(state.view, Listener, OnCommand);
    if (state.api2) state.api2->RegisterConsoleCallback(state.view, OnConsole);
    logger::info("PrismaUI view {} created", ViewPath);
  }

}
