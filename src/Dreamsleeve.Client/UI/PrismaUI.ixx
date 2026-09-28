module;

#include "Prelude.hpp"
#include "API/PrismaUI_API.h"

export module Dreamsleeve.PrismaUI;

import std;
import Dreamsleeve.Runtime;
import Dreamsleeve.Host.Bridge;
import Dreamsleeve.Events;

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

  // The overload set of Bridge::Encode is closed: only bridge event types encode.
  template <class Event>
  requires requires(const Event& event) { Bridge::Encode(event); }
  void Send(const Event& event)
  {
    if (auto json = Bridge::Encode(event))
      Send(*json);
    else
      logger::error("{}", json.error());
  }

  export void Dispatch(const std::vector<std::string>& events)
  {
    for (const auto& json : events)
      Send(json);
  }

  export void Deactivate()
  {
    auto& state = Get();
    if (!state.active) return;
    state.active = false;
    if (state.api && state.view) state.api->Unfocus(state.view);
    Send(Bridge::SimpleEvent{"deactivate"});
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
      if (state.jsHidden) Send(Bridge::SimpleEvent{"show"});
      state.jsHidden = false;
      return;
    }

    Deactivate();
    if (!state.jsHidden) Send(Bridge::SimpleEvent{"hide"});
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
    state.active     = true;
    state.focusGrace = Clock::now() + FocusGrace;
    Send(Bridge::SimpleEvent{"activate"});
  }

  // The game can drop Prisma focus on its own (Escape closes the focus menu):
  // observe it after the asynchronous focus operation had time to settle.
  export void OnFrame(Clock::time_point now)
  {
    auto& state = Get();
    if (!state.active || !ViewUsable() || now < state.focusGrace) return;
    if (!state.api->HasFocus(state.view))
    {
      state.active = false;
      Send(Bridge::SimpleEvent{"deactivate"});
    }
  }

  void SendAuthError(std::string error)
  {
    auto& runtime = Runtime::Get();
    auto  event   = Bridge::AuthState(runtime.app->Status(), runtime.ui.ui.chat.streamerMode);
    event.error   = std::move(error);
    Send(event);
  }

  void HandleCommand(Bridge::UiCommand command)
  {
    auto& runtime = Runtime::Get();
    if (!runtime.app) return;
    auto& app  = *runtime.app;
    auto& type = command.type;

    if (type == "sendChat")
    {
      if (auto sent = runtime.session.SendChat(app.Exchange(), command); !sent)
        Send(Bridge::SendResultEvent{.requestId = command.requestId, .error = sent.error()});
      return;
    }
    if (type == "close")
    {
      Deactivate();
      return;
    }
    if (type == "ignore" || type == "unignore")
    {
      const auto id = Bridge::ParseId(command.playerId);
      if (!id) return;
      auto& session = runtime.session;
      if (type == "ignore" ? session.Ignore(*id) : session.Unignore(*id))
      {
        // A visible bubble goes at once; history is re-projected without it.
        if (type == "ignore") runtime.bubbles.Erase(*id);
        if (auto saved = Runtime::SaveUi(); !saved) logger::warn("{}", saved.error());
        session.Refresh();
      }
      Send(session.IgnoredList(runtime.ui.ui.chat));
      return;
    }
    if (type == "nameSettings")
    {
      // Applied and saved at once: every surface switches without reconnecting.
      auto& chat        = runtime.ui.ui.chat;
      chat.nameMode     = command.nameMode;
      chat.streamerMode = command.streamerMode;
      if (auto saved = Runtime::SaveUi(); !saved) logger::warn("{}", saved.error());
      runtime.session.Refresh();
      Send(runtime.session.IgnoredList(chat));
      return;
    }
    if (type == "saveSettings")
    {
      const bool names =
        runtime.ui.ui.chat.nameMode != command.settings->nameMode || runtime.ui.ui.chat.streamerMode != command.settings->streamerMode;
      runtime.ui.ui.chat = *command.settings;
      if (names)
      {
        runtime.session.Refresh();
        Send(runtime.session.IgnoredList(runtime.ui.ui.chat));
      }
      Events::SetActivationKey(runtime.ui.ui.chat.activationKey);
      auto                        saved = Runtime::SaveUi();
      Bridge::SettingsResultEvent result{.revision = command.revision};
      if (!saved) result.error = saved.error();
      Send(result);
      return;
    }

    std::expected<void, std::string> admitted;
    if (type == "signIn")
    {
      std::optional<std::string> registerName;
      if (!command.displayName.empty()) registerName = command.displayName;
      admitted = app.Connect({command.username, std::move(command.password)}, std::move(registerName), command.remember);
    }
    else if (type == "signInSaved")
    {
      runtime.manualDisconnect = false;
      admitted                 = app.ConnectSaved();
    }
    else if (type == "signOut")
    {
      runtime.manualDisconnect = true;
      admitted                 = app.SignOut();
    }
    else if (type == "forgetLogin")
    {
      runtime.manualDisconnect = true;
      admitted                 = app.ForgetSavedLogin();
    }
    else if (type == "disconnect")
    {
      runtime.manualDisconnect = true;
      app.Disconnect();
    }
    if (type == "signIn") runtime.manualDisconnect = false;
    std::ranges::fill(command.password, '\0');
    if (!admitted) SendAuthError(admitted.error());
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
    HandleCommand(std::move(*command));
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
    state.active   = false;
    state.jsHidden = false;
    logger::info("Dreamsleeve view ready");
    Send(Bridge::SettingsEvent{.settings = Runtime::Get().ui.ui.chat});
    Send(Runtime::Get().session.IgnoredList(Runtime::Get().ui.ui.chat));
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
