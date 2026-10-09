module;

#include "Prelude.hpp"
#include "API/DreamsleeveAPI.h"

export module Dreamsleeve.ModApi;

import std;
import Dreamsleeve.Client.Utils;
import Dreamsleeve.Runtime;

// Plugin API for other mods: IVDreamsleeve1, handed out by the exported
// RequestPluginAPI below, and the Papyrus script DreamsleeveClient. Calls may
// arrive on any thread; they are queued in Runtime, and the frame posts them to
// Core. Outcomes return on the main thread. See docs/DreamsleeveModApiRu.md.
export namespace ModApi
{

  namespace Api = Dreamsleeve::Host::Announcements;

  constexpr std::string_view PapyrusClass   = "DreamsleeveClient";
  constexpr std::string_view ResultModEvent = "Dreamsleeve_AnnouncementResult";

  static_assert(static_cast<std::uint32_t>(DreamsleeveAPI::APIResult::Queued) == static_cast<std::uint32_t>(Api::Result::Queued));
  static_assert(static_cast<std::uint32_t>(DreamsleeveAPI::APIResult::Rejected) == static_cast<std::uint32_t>(Api::Result::Rejected));
  static_assert(static_cast<std::uint32_t>(DreamsleeveAPI::APIResult::Failed) == static_cast<std::uint32_t>(Api::Result::Failed));

  namespace Detail
  {

    // Longest string taken from another mod, far above any server limit; a C++
    // string is read no further, so a missing terminator cannot run on.
    constexpr std::size_t MaxArgumentBytes = 64 * 1024;

    std::optional<Domain::AnnouncementKind> Kind(std::int32_t value)
    {
      if (value == static_cast<std::int32_t>(DreamsleeveAPI::AnnouncementKind::Announcement)) return Domain::AnnouncementKind::Announcement;
      if (value == static_cast<std::int32_t>(DreamsleeveAPI::AnnouncementKind::Event)) return Domain::AnnouncementKind::Event;
      return std::nullopt;
    }

    // A null-terminated C++ string, read up to one byte past MaxArgumentBytes;
    // nullopt for null.
    std::optional<std::string_view> Terminated(const char* value) noexcept
    {
      if (!value) return std::nullopt;
      std::size_t size = 0;
      while (size <= MaxArgumentBytes && value[size] != '\0')
        ++size;
      return std::string_view{value, size};
    }

    // A string from another mod as UTF-8. Well-formed UTF-8 is kept as it is;
    // other bytes are read in the system ANSI code page, which narrow literals
    // compiled without /utf-8 and the "A" Windows functions produce. Missing,
    // overlong and unreadable strings are refused.
    std::optional<std::string> Utf8Argument(std::optional<std::string_view> value, std::string_view field)
    {
      using namespace Dreamsleeve::Utils::Text;
      if (!value || value->size() > MaxArgumentBytes) return std::nullopt;
      if (ValidUtf8(*value)) return std::string{*value};
      auto converted = FromCodePage(*value, CP_ACP);
      if (converted) logger::warn("Announcement {} is not UTF-8; read in ANSI code page {}", field, GetACP());
      return converted;
    }

    // What a mod hands over becomes text only as UTF-8, refused at once so the
    // mod learns it synchronously. Lengths and sources are Core's check against
    // the server policy; words, blank text and one-line labels the server's.
    Api::Result Post(std::optional<std::string_view> text, std::int32_t kind, std::optional<std::string_view> source)
    {
      const auto mapped     = Kind(kind);
      auto       utf8Text   = Utf8Argument(text, "text");
      auto       utf8Source = Utf8Argument(source, "source label");
      if (!mapped || !utf8Text || !utf8Source)
      {
        logger::warn("Announcement refused locally: invalid kind, text or source label");
        return Api::Result::Rejected;
      }

      const auto label = Dreamsleeve::Host::Bridge::ModLabel(*utf8Source);
      const auto result =
        Runtime::RequestAnnouncement({std::move(*utf8Text), *mapped, Domain::ClientAnnouncementSource::ThirdParty, std::move(*utf8Source)});
      if (result == Api::Result::Queued)
        logger::info("Announcement from {} queued", label);
      else
        logger::warn("Announcement from {} refused locally: {}", label, Api::ResultName(result));
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
        const char*                      utf8Text,
        DreamsleeveAPI::AnnouncementKind kind,
        const char*                      utf8Source) noexcept override
      {
        return static_cast<DreamsleeveAPI::APIResult>(Post(Terminated(utf8Text), static_cast<std::int32_t>(kind), Terminated(utf8Source)));
      }

      DreamsleeveAPI::CallbackResult AddAnnouncementResultCallback(
        SKSE::PluginHandle                         plugin,
        DreamsleeveAPI::AnnouncementResultCallback callback) noexcept override
      {
        if (!callback) return DreamsleeveAPI::CallbackResult::InvalidCallback;
        auto&           callbacks = ResultCallbacks();
        std::lock_guard lock{callbacks.mutex};
        return callbacks.byPlugin.try_emplace(plugin, callback).second ? DreamsleeveAPI::CallbackResult::OK
                                                                       : DreamsleeveAPI::CallbackResult::AlreadyRegistered;
      }

      DreamsleeveAPI::CallbackResult RemoveAnnouncementResultCallback(SKSE::PluginHandle plugin) noexcept override
      {
        auto&           callbacks = ResultCallbacks();
        std::lock_guard lock{callbacks.mutex};
        return callbacks.byPlugin.erase(plugin) ? DreamsleeveAPI::CallbackResult::OK : DreamsleeveAPI::CallbackResult::NotRegistered;
      }
    };

    Interface& Instance()
    {
      static Interface instance;
      return instance;
    }

    // Papyrus strings follow the same rule as C++ ones: UTF-8, else the ANSI code page.
    bool PapyrusPostAnnouncement(RE::StaticFunctionTag*, std::string text, std::int32_t kind, std::string source)
    {
      return Post(std::string_view{text}, kind, std::string_view{source}) == Api::Result::Queued;
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
    const auto source = Dreamsleeve::Host::Bridge::ModLabel(outcome.signature);
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

    // The strings live in outcome for the whole loop.
    const DreamsleeveAPI::AnnouncementResult result{
        static_cast<DreamsleeveAPI::APIResult>(outcome.result),
        outcome.signature.c_str(),
        outcome.text.c_str(),
        outcome.reason.c_str()
    };
    for (const auto callback : targets)
      callback(&result);

    if (const auto events = SKSE::GetModCallbackEventSource())
    {
      SKSE::ModCallbackEvent
        event{RE::BSFixedString{ResultModEvent}, RE::BSFixedString{outcome.signature}, static_cast<float>(outcome.result), nullptr};
      events->SendEvent(&event);
    }
  }

}

// Found by other plugins with GetProcAddress (DreamsleeveAPI::RequestPluginAPI).
extern "C" __declspec(dllexport) void* RequestPluginAPI(const DreamsleeveAPI::InterfaceVersion interfaceVersion)
{
  if (interfaceVersion == DreamsleeveAPI::InterfaceVersion::V1)
    return static_cast<DreamsleeveAPI::IVDreamsleeve1*>(&ModApi::Detail::Instance());
  return nullptr;
}
