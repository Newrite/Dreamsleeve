module;

#include "Prelude.hpp"

export module Dreamsleeve.Logic;

import std;
import Dreamsleeve.Runtime;
import Dreamsleeve.PrismaUI;
import Dreamsleeve.Game.Telemetry;
import Dreamsleeve.Game.Fireflies;
import Dreamsleeve.Game.GroundMarks;
import Dreamsleeve.UI.Nameplates;
import Dreamsleeve.Events;
import Dreamsleeve.Host.Bridge;
import Dreamsleeve.ModApi;

// Per-frame orchestration on the game main thread: notices, Core drain and UI
// dispatch, session policy, telemetry, fireflies and shutdown.
namespace Logic
{

  namespace Dream = Dreamsleeve::Client;
  using Clock     = std::chrono::steady_clock;

  constexpr auto ReconnectMinimum  = std::chrono::seconds{5};
  constexpr auto ReconnectMaximum  = std::chrono::seconds{60};
  constexpr auto ReadyProbeWindow  = std::chrono::milliseconds{500};
  constexpr auto NamesSaveInterval = std::chrono::seconds{5};

  struct State
  {
    std::vector<Runtime::Notice>                           notices;
    Dream::ClientOutput                                    output;
    bool                                                   resumeTried{};
    bool                                                   wasReady{};
    Clock::time_point                                      nextReconnect{};
    std::chrono::seconds                                   reconnectDelay{ReconnectMinimum};
    Clock::time_point                                      readySince{};
    std::uint64_t                                          bubbleGeneration{};
    Clock::time_point                                      nextNamesSave{};
    std::vector<Dreamsleeve::Host::Announcements::Request> announcements;
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
    GroundMarks::EndContext();
    Nameplates::Publish({});
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
        // The killer handle is resolved here, in the frame; the sink only copied it.
        GroundMarks::NoteDeath(notice.handle, notice.flag);
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

  void Drain(Clock::time_point now)
  {
    auto& runtime = Runtime::Get();
    auto& state   = Get();
    runtime.app->Exchange().Drain(state.output);
    runtime.movement->Apply(state.output.state);

    Dreamsleeve::Host::Session::Frame frame;
    runtime.session.Process(runtime.app->Exchange(), state.output, runtime.ui.ui.chat, frame);
    for (const auto& note : frame.notes)
      logger::warn("{}", note);
    // The server confirmed a switch of "hide my name": the next session opens so too.
    if (frame.hideIdentity && *frame.hideIdentity != Dreamsleeve::Host::Bridge::HidingOf(runtime.ui.ui.chat.hideIdentity))
    {
      runtime.ui.ui.chat.hideIdentity = std::string{Dreamsleeve::Host::Bridge::HidingName(*frame.hideIdentity)};
      runtime.app->Exchange().SetHideIdentity(*frame.hideIdentity);
      if (auto saved = Runtime::SaveUi(); !saved) logger::warn("{}", saved.error());
    }
    // Reconnecting would be refused again until the player turns the choice off.
    if (frame.identityRefused)
    {
      runtime.manualDisconnect = true;
      logger::warn("The server does not allow hidden names; automatic reconnect stopped");
    }
    PrismaUI::Dispatch(frame.events);
    // New pseudonyms are batched: at most one ui.toml write per interval.
    if (now >= state.nextNamesSave && runtime.session.PlayerNames().TakeDirty())
    {
      state.nextNamesSave = now + NamesSaveInterval;
      if (auto saved = Runtime::SaveUi(); !saved) logger::warn("{}", saved.error());
    }

    // Plugin API: outcomes of this drain, then the requests queued since the
    // last frame, handed to the session they now meet.
    for (const auto& outcome : frame.announcementResults)
      ModApi::Report(outcome);
    Runtime::PublishAnnouncementsConnected(runtime.session.Ready());
    Runtime::TakeAnnouncements(state.announcements);
    for (auto& request : state.announcements)
    {
      Dreamsleeve::Host::Announcements::Outcome refused{request.signature, request.text};
      refused.result = runtime.session.PostAnnouncement(runtime.app->Exchange(), std::move(request));
      if (refused.result != Dreamsleeve::Host::Announcements::Result::Queued) ModApi::Report(refused);
    }

    // Bubbles belong to one session: a disconnect or a new generation drops
    // them. Only live confirmed publications from this drain are admitted.
    const auto generation = runtime.session.Generation();
    if (!runtime.session.Ready() || generation != state.bubbleGeneration) runtime.bubbles.Clear();
    state.bubbleGeneration = generation;
    if (runtime.ui.ui.chat.showBubbles)
      for (const auto& message : frame.freshMessages)
        runtime.bubbles.Post(message.author->playerId, message.messageText, now);
  }

  // Fireflies and ground marks share one HUD frame: labels of both go into it
  // and a pause or hidden HUD drops them together.
  void PublishNameplates(Clock::time_point now)
  {
    Nameplates::Frame names;
    Fireflies::Tick(now, names);
    GroundMarks::Tick(now, names);
    auto* menus = RE::UI::GetSingleton();
    if (!menus || menus->GameIsPaused() || !menus->menuSystemVisible) names.labels.clear();
    Nameplates::Publish(std::move(names));
  }

  void PublishMenuSnapshot()
  {
    auto&                 runtime = Runtime::Get();
    auto&                 status  = Get().output.status;
    Runtime::MenuSnapshot snapshot;
    snapshot.phase          = std::string{Dreamsleeve::Host::Bridge::PhaseName(status)};
    snapshot.serverName     = status.serverName;
    snapshot.savedUsername  = runtime.ui.ui.chat.streamerMode ? std::string{} : status.savedUsername;
    snapshot.error          = status.error;
    snapshot.activationKey  = runtime.ui.ui.chat.activationKey;
    snapshot.online         = runtime.session.OnlinePlayers().size();
    snapshot.fireflies      = Fireflies::Count();
    snapshot.groundMarks    = GroundMarks::Count();
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
    Drain(now);
    SessionPolicy(now);
    ProbeContext(now);
    Telemetry::Tick(now);
    PublishNameplates(now);
    PrismaUI::OnFrame(now);
    PublishMenuSnapshot();
  }

}
