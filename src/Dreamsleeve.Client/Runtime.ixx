module;

#include "Prelude.hpp"

export module Dreamsleeve.Runtime;

import std;
import Dreamsleeve.Client.Utils;
export import Dreamsleeve.Client.Application;
export import Dreamsleeve.Client.MovementView;
export import Dreamsleeve.Host.Session;
export import Dreamsleeve.Host.UiSettings;
export import Dreamsleeve.Host.Bubbles;

// Single owner of the Core application, the interpolation view, the UI session
// projection and the settings files. Every accessor below is main-thread only,
// except Post/Take of notices and the announcement queue of the plugin API,
// the cross-thread boundaries.
export namespace Runtime
{

  namespace Dream = Dreamsleeve::Client;
  namespace Host  = Dreamsleeve::Host;

  // Relative to the game root; SKSE plugins run with that working directory.
  constexpr std::string_view ConfigDirectory = "Data/SKSE/Plugins/Dreamsleeve"sv;
  constexpr std::string_view ClientConfig    = "client.toml"sv;
  constexpr std::string_view UiConfig        = "ui.toml"sv;
  constexpr std::string_view AliasConfig     = "aliases.toml"sv;

  enum class NoticeKind
  {
    NewGame,          // TESQuest::NewGame finished; the world is not ready yet.
    PreLoadGame,      // A load attempt started under g_loadGameLock.
    PostLoadGame,     // flag = the load succeeded.
    SaveGame,         // Sent before the save is written.
    MenuChanged,      // Recompute visibility from the current menu set.
    ActivationKey,    // The chat activation key went down.
    PlayerDeath,      // flag = dead (TESDeathEvent.dead).
    PlayerActivated,  // formId = the activated object.
    UiHidden,         // flag = user opt-out from the SKSE menu.
    ActivationKeyF2,  // flag = F2 instead of Enter, from the SKSE menu.
    ResumeLogin,      // SKSE menu: sign in with the saved login.
    Disconnect        // SKSE menu: close the session and stop reconnecting.
  };

  // A handle is a value: the sink never resolves it, the frame does.
  struct Notice
  {
    NoticeKind          kind{};
    bool                flag{};
    std::uint32_t       formId{};
    RE::ObjectRefHandle handle{};  // PlayerDeath: the killer, if any.
  };

  enum class GameContext
  {
    MainMenu,  // No character: main menu, title sequence, or before any game.
    Loading,   // Transition in progress; stale pointers must not be used.
    Playing    // Character context began; per-frame readiness is still checked.
  };

  // Copy for the SKSE menu renderer, which runs outside the game thread.
  struct MenuSnapshot
  {
    std::string phase{"disconnected"};
    std::string serverName;
    std::string savedUsername;
    std::string error;
    std::string activationKey{"Enter"};
    std::size_t online{};
    std::size_t fireflies{};
    std::size_t groundMarks{};
    bool        savedLogin{};
    bool        authenticating{};
    bool        hideUi{};
    bool        available{};
    std::size_t   phantoms{};
    std::uint64_t phantomModels{}, phantomPoses{}, phantomRejected{}, phantomDropped{}, phantomCacheHits{};
    std::uint32_t phantomSampleRate{};
    std::string   phantomError;
  };

  struct State
  {
    Dream::ClientApplication::Ptr app;
    Dream::MovementView::Ptr      movement;
    Host::Session                 session;
    Host::Bubbles                 bubbles;  // Active chat texts above fireflies; main thread only.
    Host::UiFile                  ui;
    std::filesystem::path         clientPath{std::filesystem::path{ConfigDirectory} / ClientConfig};
    std::filesystem::path         uiPath{std::filesystem::path{ConfigDirectory} / UiConfig};
    std::filesystem::path         aliasPath{std::filesystem::path{ConfigDirectory} / AliasConfig};
    GameContext                   context{GameContext::MainMenu};
    bool                          dataLoaded{};
    bool                          shutdown{};
    bool                          manualDisconnect{};  // Stops automatic reconnects until an explicit sign-in.
  };

  State& Get()
  {
    static State state;
    return state;
  }

  namespace Detail
  {

    constexpr std::size_t MaxNotices = 64;

    struct NoticeQueue
    {
      std::mutex          mutex;
      std::vector<Notice> notices;
      bool                overflow{};
    };

    NoticeQueue& Queue()
    {
      static NoticeQueue queue;
      return queue;
    }

  }

  // Any thread. Overflow drops the notice and is reported to the frame, which
  // then recomputes everything that notices would only have accelerated.
  void Post(Notice notice)
  {
    auto&           queue = Detail::Queue();
    std::lock_guard lock{queue.mutex};
    if (queue.notices.size() >= Detail::MaxNotices)
    {
      queue.overflow = true;
      return;
    }
    queue.notices.push_back(notice);
  }

  // Main thread, once per frame. Returns whether notices were lost since the last take.
  bool Take(std::vector<Notice>& output)
  {
    output.clear();
    auto&           queue = Detail::Queue();
    std::lock_guard lock{queue.mutex};
    output.swap(queue.notices);
    return std::exchange(queue.overflow, false);
  }

  namespace Detail
  {

    struct MenuMirror
    {
      std::mutex   mutex;
      MenuSnapshot snapshot;
    };

    MenuMirror& Mirror()
    {
      static MenuMirror mirror;
      return mirror;
    }

  }

  namespace Detail
  {

    constexpr std::size_t MaxQueuedAnnouncements = 32;

    // Plugin API calls arrive on any thread and are queued while the session
    // the frame last saw is ready; only the frame touches the session.
    struct AnnouncementQueue
    {
      std::mutex                                mutex;
      std::vector<Host::Announcements::Request> requests;
      bool                                      connected{};
    };

    AnnouncementQueue& Announcements()
    {
      static AnnouncementQueue queue;
      return queue;
    }

  }

  // Any thread. Queued means the frame will hand the request to the session.
  Host::Announcements::Result RequestAnnouncement(Host::Announcements::Request request)
  {
    auto&           queue = Detail::Announcements();
    std::lock_guard lock{queue.mutex};
    if (!queue.connected) return Host::Announcements::Result::NotConnected;
    if (queue.requests.size() >= Detail::MaxQueuedAnnouncements) return Host::Announcements::Result::Busy;
    queue.requests.push_back(std::move(request));
    return Host::Announcements::Result::Queued;
  }

  // Any thread.
  bool AnnouncementsConnected()
  {
    auto&           queue = Detail::Announcements();
    std::lock_guard lock{queue.mutex};
    return queue.connected;
  }

  // Main thread, once per frame after the drain.
  void PublishAnnouncementsConnected(bool connected)
  {
    auto&           queue = Detail::Announcements();
    std::lock_guard lock{queue.mutex};
    queue.connected = connected;
  }

  // Main thread.
  void TakeAnnouncements(std::vector<Host::Announcements::Request>& output)
  {
    output.clear();
    auto&           queue = Detail::Announcements();
    std::lock_guard lock{queue.mutex};
    output.swap(queue.requests);
  }

  void WriteMenuSnapshot(MenuSnapshot snapshot)
  {
    auto&           mirror = Detail::Mirror();
    std::lock_guard lock{mirror.mutex};
    mirror.snapshot = std::move(snapshot);
  }

  MenuSnapshot ReadMenuSnapshot()
  {
    auto&           mirror = Detail::Mirror();
    std::lock_guard lock{mirror.mutex};
    return mirror.snapshot;
  }

  const Dream::ClientSettings* Settings()
  {
    auto& state = Get();
    return state.app ? &state.app->Settings() : nullptr;
  }

  // The name book lives in the session's Names; the file gets its current copy.
  std::expected<void, std::string> SaveUi()
  {
    auto& state    = Get();
    state.ui.names = state.session.PlayerNames().Book();
    return Host::SaveUiFile(state.uiPath, state.ui);
  }

  // Creates Core objects. Skyrim data is not needed; the network thread starts here.
  bool Initialize()
  {
    auto& state = Get();
    if (state.app) return true;

    if (auto created = Dream::EnsureClientSettings(state.clientPath); !created) logger::warn("{}", created.error());

    auto settings = Dream::LoadClientSettings(state.clientPath);
    if (!settings)
    {
      logger::error("Cannot load {}: {}", state.clientPath.string(), settings.error());
      return false;
    }

    if (!settings->client.phantomCacheDirectory.empty())
    {
      auto cache = std::filesystem::u8path(settings->client.phantomCacheDirectory);
      if (cache.is_relative()) cache = state.clientPath.parent_path() / cache;
      const auto utf8 = cache.u8string();
      settings->client.phantomCacheDirectory.assign(utf8.begin(), utf8.end());
    }

    // Before the network starts: the first connection goes by the remembered route.
    if (auto ui = Host::LoadUiFile(state.uiPath))
      state.ui = *ui;
    else
      logger::warn("UI settings ignored: {}", ui.error());
    const auto routes = Dream::RoutesOf(*settings);
    const auto chosen = state.ui.ui.route.empty() ? std::nullopt : Dream::RouteIndex(routes, state.ui.ui.route);
    if (!state.ui.ui.route.empty() && !chosen)
    {
      logger::warn("The chosen route \"{}\" is not in {}; choosing automatically", state.ui.ui.route, state.clientPath.string());
      state.ui.ui.route.clear();
    }
    const Dream::RoutePreference preference{chosen, Dream::RouteIndex(routes, state.ui.ui.lastRoute).value_or(0)};

    // Validates every setting; the movement view below trusts them.
    auto app = Dream::ClientApplication::TryCreate(*settings, preference);
    if (!app)
    {
      logger::error("Cannot create client application: {}", app.error());
      return false;
    }
    state.session.ConfigureRoutes(routes | std::views::transform(&Dream::ConnectionRoute::name) | std::ranges::to<std::vector>(),
                                  state.ui.ui.route);

    // Account IDs are unique per server: "host:port" as configured scopes
    // pseudonyms and ignores (DNS names fold ASCII case).
    auto dictionary = Host::LoadAliasDictionary(state.aliasPath);
    if (!dictionary.warning.empty()) logger::warn("{}", dictionary.warning);
    auto& names = state.session.PlayerNames();
    names.Configure(
      std::format(
        "{}:{}",
        settings->client.serverHost | std::views::transform(Dreamsleeve::Utils::Text::AsciiLower) | std::ranges::to<std::string>(),
        settings->client.serverPort),
      std::move(dictionary.names));
    names.Load(std::move(state.ui.names));

    state.movement = Dream::MovementView::Create(settings->client.movement);
    state.app      = std::move(*app);
    // The first session already opens with the saved "hide my name" choice.
    state.app->Exchange().SetHideIdentity(Host::Bridge::HidingOf(state.ui.ui.hideIdentity));
    logger::info("Client application started; server {}:{}, {} route(s)", settings->client.serverHost, settings->client.serverPort, routes.size());
    return true;
  }

  // Main thread, idempotent. Joins the network thread; called when the game quits.
  void Shutdown()
  {
    auto& state = Get();
    if (state.shutdown) return;
    state.shutdown = true;
    // Pseudonyms are saved in batches; keep the last ones assigned this session.
    if (state.session.PlayerNames().TakeDirty())
      if (auto saved = SaveUi(); !saved) logger::warn("{}", saved.error());
    if (state.app) state.app->Stop();
    logger::info("Client application stopped");
  }

}
