module;

#include "Prelude.hpp"

export module Dreamsleeve.Logic;

import std;
import Dreamsleeve.Runtime;
import Dreamsleeve.PrismaUI;
import Dreamsleeve.Game.Telemetry;
import Dreamsleeve.Game.Fireflies;
import Dreamsleeve.UI.Nameplates;
import Dreamsleeve.Events;
import Dreamsleeve.Host.Bridge;

// Per-frame orchestration on the game main thread: notices, Core drain and UI
// dispatch, session policy, telemetry, fireflies and shutdown.
namespace Logic
{

  namespace Dream = Dreamsleeve::Client;
  using Clock     = std::chrono::steady_clock;

  constexpr auto ReconnectMinimum = std::chrono::seconds{5};
  constexpr auto ReconnectMaximum = std::chrono::seconds{60};
  constexpr auto ReadyProbeWindow = std::chrono::milliseconds{500};

  struct State
  {
    std::vector<Runtime::Notice> notices;
    Dream::ClientOutput          output;
    bool                         resumeTried{};
    bool                         wasReady{};
    Clock::time_point            nextReconnect{};
    std::chrono::seconds         reconnectDelay{ReconnectMinimum};
    Clock::time_point            readySince{};
  };

  State& Get()
  {
    static State state;
    return state;
  }

  void BeginPlaying()
  {
    auto& runtime   = Runtime::Get();
    runtime.context = Runtime::GameContext::Playing;
    Telemetry::BeginContext();
    logger::info("Character context started");
  }

  void LeavePlaying(Runtime::GameContext next)
  {
    auto& runtime = Runtime::Get();
    if (runtime.context == Runtime::GameContext::Playing)
    {
      Telemetry::EndContext();
      logger::info("Character context ended");
    }
    Fireflies::ClearAll();
    Nameplates::Release();
    runtime.context = next;
  }

  void Handle(const Runtime::Notice& notice)
  {
    auto& runtime = Runtime::Get();
    switch (notice.kind)
    {
      case Runtime::NoticeKind::NewGame:
      case Runtime::NoticeKind::PreLoadGame:
        LeavePlaying(Runtime::GameContext::Loading);
        break;
      case Runtime::NoticeKind::PostLoadGame:
        // Success or failure, the world is probed per frame before sampling resumes.
        if (runtime.context == Runtime::GameContext::Playing) LeavePlaying(Runtime::GameContext::Loading);
        break;
      case Runtime::NoticeKind::SaveGame:
        // Fireflies are marked temporary when created. A queued notification
        // is too late to clean up before serialization; keep the live visuals.
        break;
      case Runtime::NoticeKind::MenuChanged:
        PrismaUI::RecomputeMenus();
        if (auto* ui = RE::UI::GetSingleton(); ui && ui->IsMenuOpen(RE::MainMenu::MENU_NAME)) LeavePlaying(Runtime::GameContext::MainMenu);
        break;
      case Runtime::NoticeKind::ActivationKey:
        PrismaUI::RequestActivate();
        break;
      case Runtime::NoticeKind::PlayerDeath:
        Telemetry::NoteDeath();
        break;
      case Runtime::NoticeKind::PlayerActivated:
        Telemetry::NoteActivated(notice.formId);
        break;
      case Runtime::NoticeKind::UiHidden:
        runtime.ui.ui.hideUi = notice.flag;
        if (auto saved = Runtime::SaveUi(); !saved) logger::warn("{}", saved.error());
        PrismaUI::ApplyUserSettings();
        break;
      case Runtime::NoticeKind::ActivationKeyF2:
        runtime.ui.ui.chat.activationKey = notice.flag ? "F2" : "Enter";
        Events::SetActivationKey(runtime.ui.ui.chat.activationKey);
        if (auto saved = Runtime::SaveUi(); !saved) logger::warn("{}", saved.error());
        PrismaUI::SendSettings();
        break;
      case Runtime::NoticeKind::ResumeLogin:
        runtime.manualDisconnect = false;
        if (auto started = runtime.app->ConnectSaved(); !started) logger::warn("{}", started.error());
        break;
      case Runtime::NoticeKind::Disconnect:
        runtime.manualDisconnect = true;
        runtime.app->Disconnect();
        break;
    }
  }

  void HandleNotices()
  {
    auto& state = Get();
    if (Runtime::Take(state.notices)) PrismaUI::RecomputeMenus();
    for (const auto& notice : state.notices)
      Handle(notice);
  }

  // A character is "playing" once the world stayed usable for a short window;
  // Main::Update alone proves nothing about the player, cell or 3D.
  void ProbeContext(Clock::time_point now)
  {
    auto& runtime = Runtime::Get();
    auto& state   = Get();
    if (runtime.context == Runtime::GameContext::Playing || !runtime.dataLoaded) return;
    if (!Telemetry::PlayerReady())
    {
      state.readySince = {};
      return;
    }
    if (state.readySince == Clock::time_point{}) state.readySince = now;
    if (now - state.readySince >= ReadyProbeWindow) BeginPlaying();
  }

  // Saved login resumes once; an unexpected loss retries with backoff until the
  // user disconnects or signs out. No automatic password retry exists. Only an
  // explicit sign-in lifts a manual disconnect: the frame that requests the
  // disconnect still drains a Ready status, so Ready itself proves nothing.
  void SessionPolicy(Clock::time_point now)
  {
    auto&       runtime = Runtime::Get();
    auto&       state   = Get();
    const auto& status  = state.output.status;
    const bool  idle =
      !status.authenticating && (status.phase == Dream::SessionPhase::Disconnected || status.phase == Dream::SessionPhase::Faulted);

    if (status.phase == Dream::SessionPhase::Ready)
    {
      state.wasReady       = true;
      state.reconnectDelay = ReconnectMinimum;
      return;
    }
    if (!idle || !status.savedLogin || runtime.manualDisconnect) return;

    if (!state.resumeTried)
    {
      state.resumeTried   = true;
      state.nextReconnect = now + state.reconnectDelay;
      if (auto started = runtime.app->ConnectSaved()) logger::info("Resuming saved login");
      return;
    }
    // Credential and request errors need the user; transport errors retry.
    using Failure = Dream::Auth::FailureCode;
    if (
      status.authFailure == Failure::InvalidCredentials || status.authFailure == Failure::CredentialStorage ||
      status.authFailure == Failure::InvalidRequest || status.authFailure == Failure::RegistrationDisabled)
      return;
    if (now < state.nextReconnect) return;
    state.nextReconnect  = now + state.reconnectDelay;
    state.reconnectDelay = std::min(ReconnectMaximum, state.reconnectDelay * 2);
    if (runtime.app->ConnectSaved()) logger::info("Reconnecting with saved login");
  }

  void Drain()
  {
    auto& runtime = Runtime::Get();
    auto& state   = Get();
    runtime.app->Exchange().Drain(state.output);
    runtime.movement->Apply(state.output.state);

    Dreamsleeve::Host::Session::Frame frame;
    runtime.session.Process(runtime.app->Exchange(), state.output, runtime.ui.ui.chat, frame);
    for (const auto& note : frame.notes)
      logger::warn("{}", note);
    PrismaUI::Dispatch(frame.events);
  }

  void PublishMenuSnapshot()
  {
    auto&                 runtime = Runtime::Get();
    auto&                 status  = Get().output.status;
    Runtime::MenuSnapshot snapshot;
    snapshot.phase          = std::string{Dreamsleeve::Host::Bridge::PhaseName(status)};
    snapshot.serverName     = status.serverName;
    snapshot.savedUsername  = status.savedUsername;
    snapshot.error          = status.error;
    snapshot.activationKey  = runtime.ui.ui.chat.activationKey;
    snapshot.online         = runtime.session.OnlinePlayers().size();
    snapshot.fireflies      = Fireflies::Count();
    snapshot.savedLogin     = status.savedLogin;
    snapshot.authenticating = status.authenticating;
    snapshot.hideUi         = runtime.ui.ui.hideUi;
    snapshot.available      = true;
    Runtime::WriteMenuSnapshot(std::move(snapshot));
  }

  export void OnFrame()
  {
    auto& runtime = Runtime::Get();
    if (!runtime.app || runtime.shutdown) return;

    if (auto* main = RE::Main::GetSingleton(); main && main->GetRuntimeData().quitGame)
    {
      LeavePlaying(Runtime::GameContext::MainMenu);
      Nameplates::Shutdown();  // GFx objects go before the engine tears Scaleform down.
      Runtime::Shutdown();
      return;
    }

    const auto now = Clock::now();
    HandleNotices();
    Drain();
    SessionPolicy(now);
    ProbeContext(now);
    Telemetry::Tick(now);
    Fireflies::Tick(now);
    PrismaUI::OnFrame(now);
    PublishMenuSnapshot();
  }

}
