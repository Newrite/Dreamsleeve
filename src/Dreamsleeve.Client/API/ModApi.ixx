module;

#include "Prelude.hpp"
#include "API/DreamsleeveAPI.h"

export module Dreamsleeve.ModApi;

import std;
import Dreamsleeve.Runtime;

// Plugin API for other mods: IVDreamsleeve1, handed out by the exported
// RequestPluginAPI (Main.cpp), and the Papyrus script DreamsleeveClient. Calls
// may arrive on any thread; they are checked and queued in Runtime, and the
// frame posts them to Core. Outcomes return on the main thread.
// See docs/DreamsleeveModApiRu.md.
export namespace ModApi
{

  namespace Api = Dreamsleeve::Host::Announcements;

  constexpr std::string_view PapyrusClass    = "DreamsleeveClient";
  constexpr std::string_view ResultModEvent  = "Dreamsleeve_AnnouncementResult";
  constexpr std::size_t      MaxLoggedSource = 64;

  static_assert(static_cast<std::uint32_t>(DreamsleeveAPI::APIResult::Queued) == static_cast<std::uint32_t>(Api::Result::Queued));
  static_assert(static_cast<std::uint32_t>(DreamsleeveAPI::APIResult::Busy) == static_cast<std::uint32_t>(Api::Result::Busy));
  static_assert(static_cast<std::uint32_t>(DreamsleeveAPI::APIResult::Failed) == static_cast<std::uint32_t>(Api::Result::Failed));

  namespace Detail
  {

    // Mod text reaches the log as a value, cleaned and bounded.
    std::string Loggable(std::string_view text)
    {
      return Dreamsleeve::Host::Bridge::SafeLabel(text, MaxLoggedSource);
    }

    std::optional<Domain::AnnouncementKind> Kind(std::int32_t value)
    {
      if (value == static_cast<std::int32_t>(DreamsleeveAPI::AnnouncementKind::Announcement)) return Domain::AnnouncementKind::Announcement;
      if (value == static_cast<std::int32_t>(DreamsleeveAPI::AnnouncementKind::Event)) return Domain::AnnouncementKind::Event;
      return std::nullopt;
    }

    Api::Result Post(std::string text, std::int32_t kind, std::string source)
    {
      const auto mapped = Kind(kind);
      const auto result = mapped
                          ? Runtime::RequestAnnouncement({std::move(text), *mapped, Domain::ClientAnnouncementSource::ThirdParty, source})
                          : Api::Result::InvalidKind;
      if (result == Api::Result::Queued)
        logger::info("Announcement from {} queued", Loggable(source));
      else
        logger::warn("Announcement from {} refused locally: {}", Loggable(source), Api::ResultName(result));
      return result;
    }

    struct Callbacks
    {
      std::mutex                                                                         mutex;
      std::unordered_map<SKSE::PluginHandle, DreamsleeveAPI::AnnouncementResultCallback> byPlugin;
    };

    Callbacks& ResultCallbacks()
    {
      static Callbacks callbacks;
      return callbacks;
    }

    class Interface final : public DreamsleeveAPI::IVDreamsleeve1
    {
  public:

      std::uint32_t GetPluginVersion() const noexcept override
      {
        return SKSE::PluginDeclaration::GetSingleton()->GetVersion().pack();
      }

      bool IsConnected() const noexcept override
      {
        return Runtime::AnnouncementsConnected();
      }

      DreamsleeveAPI::APIResult PostAnnouncement(
        std::string_view                 text,
        DreamsleeveAPI::AnnouncementKind kind,
        std::string_view                 source) noexcept override
      {
        return static_cast<DreamsleeveAPI::APIResult>(Post(std::string{text}, static_cast<std::int32_t>(kind), std::string{source}));
      }

      DreamsleeveAPI::CallbackResult AddAnnouncementResultCallback(
        SKSE::PluginHandle                         plugin,
        DreamsleeveAPI::AnnouncementResultCallback callback) noexcept override
      {
        auto&           callbacks = ResultCallbacks();
        std::lock_guard lock{callbacks.mutex};
        return callbacks.byPlugin.try_emplace(plugin, std::move(callback)).second ? DreamsleeveAPI::CallbackResult::OK
                                                                                  : DreamsleeveAPI::CallbackResult::AlreadyRegistered;
      }

      DreamsleeveAPI::CallbackResult RemoveAnnouncementResultCallback(SKSE::PluginHandle plugin) noexcept override
      {
        auto&           callbacks = ResultCallbacks();
        std::lock_guard lock{callbacks.mutex};
        return callbacks.byPlugin.erase(plugin) ? DreamsleeveAPI::CallbackResult::OK : DreamsleeveAPI::CallbackResult::NotRegistered;
      }
    };

    // Papyrus strings are bytes of the game's text encoding, which on a
    // localized install is the ANSI code page rather than UTF-8.
    std::string FromPapyrus(std::string value)
    {
      if (value.empty() || Api::CountCodePoints(value, true)) return value;
      const auto size = static_cast<int>(std::min<std::size_t>(value.size(), 1 << 16));
      const int  wide = MultiByteToWideChar(CP_ACP, 0, value.data(), size, nullptr, 0);
      if (wide <= 0) return value;
      std::wstring utf16(static_cast<std::size_t>(wide), L'\0');
      MultiByteToWideChar(CP_ACP, 0, value.data(), size, utf16.data(), wide);
      const int bytes = WideCharToMultiByte(CP_UTF8, 0, utf16.data(), wide, nullptr, 0, nullptr, nullptr);
      if (bytes <= 0) return value;
      std::string utf8(static_cast<std::size_t>(bytes), '\0');
      WideCharToMultiByte(CP_UTF8, 0, utf16.data(), wide, utf8.data(), bytes, nullptr, nullptr);
      return utf8;
    }

    bool PapyrusPostAnnouncement(RE::StaticFunctionTag*, std::string text, std::int32_t kind, std::string source)
    {
      return Post(FromPapyrus(std::move(text)), kind, FromPapyrus(std::move(source))) == Api::Result::Queued;
    }

    bool PapyrusIsConnected(RE::StaticFunctionTag*)
    {
      return Runtime::AnnouncementsConnected();
    }

    std::int32_t PapyrusApiVersion(RE::StaticFunctionTag*)
    {
      return 1;
    }

    bool RegisterPapyrus(RE::BSScript::IVirtualMachine* vm)
    {
      vm->RegisterFunction("PostAnnouncement", PapyrusClass, PapyrusPostAnnouncement);
      vm->RegisterFunction("IsConnected", PapyrusClass, PapyrusIsConnected);
      vm->RegisterFunction("GetApiVersion", PapyrusClass, PapyrusApiVersion);
      return true;
    }

  }

  // Exported by Main.cpp as RequestPluginAPI; the version is DreamsleeveAPI::InterfaceVersion.
  void* Request(std::uint8_t version)
  {
    static Detail::Interface instance;
    if (version == static_cast<std::uint8_t>(DreamsleeveAPI::InterfaceVersion::V1))
      return static_cast<DreamsleeveAPI::IVDreamsleeve1*>(&instance);
    return nullptr;
  }

  // During SKSEPlugin_Load, after SKSE::Init.
  void Register()
  {
    if (const auto papyrus = SKSE::GetPapyrusInterface(); !papyrus || !papyrus->Register(Detail::RegisterPapyrus))
      logger::error("Cannot register Papyrus functions of {}", PapyrusClass);
  }

  // Main thread. The log keeps a line per outcome; the registered callbacks and
  // every script registered for the mod event are told. Callbacks run outside
  // the lock, so a callback may register or remove callbacks.
  void Report(const Api::Outcome& outcome)
  {
    const auto source = Detail::Loggable(outcome.signature);
    if (outcome.result == Api::Result::Published)
      logger::info("Announcement from {} published", source);
    else
      logger::warn("Announcement from {} not published: {} ({})", source, Api::ResultName(outcome.result), outcome.reason);

    std::vector<DreamsleeveAPI::AnnouncementResultCallback> targets;
    {
      auto&           callbacks = Detail::ResultCallbacks();
      std::lock_guard lock{callbacks.mutex};
      for (const auto& [plugin, callback] : callbacks.byPlugin)
        targets.push_back(callback);
    }
    const DreamsleeveAPI::AnnouncementResult
      result{static_cast<DreamsleeveAPI::APIResult>(outcome.result), outcome.signature, outcome.text, outcome.reason};
    for (const auto& callback : targets)
      callback(result);

    if (const auto events = SKSE::GetModCallbackEventSource())
    {
      SKSE::ModCallbackEvent
        event{RE::BSFixedString{ResultModEvent}, RE::BSFixedString{outcome.signature}, static_cast<float>(outcome.result), nullptr};
      events->SendEvent(&event);
    }
  }

}
