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

    std::optional<Domain::AnnouncementKind> Kind(std::int32_t value)
    {
      if (value == static_cast<std::int32_t>(DreamsleeveAPI::AnnouncementKind::Announcement)) return Domain::AnnouncementKind::Announcement;
      if (value == static_cast<std::int32_t>(DreamsleeveAPI::AnnouncementKind::Event)) return Domain::AnnouncementKind::Event;
      return std::nullopt;
    }

    // What a mod hands over becomes text only as well-formed UTF-8, refused at
    // once so the mod learns it synchronously. Lengths and sources are Core's
    // check against the server policy; words, blank text and one-line labels the server's.
    Api::Result Post(std::string text, std::int32_t kind, std::string source)
    {
      using Dreamsleeve::Utils::Text::ValidUtf8;
      const auto mapped = Kind(kind);
      if (!mapped || !ValidUtf8(text) || !ValidUtf8(source))
      {
        logger::warn("Announcement refused locally: invalid kind, text or source label");
        return Api::Result::Rejected;
      }
      const auto label = Dreamsleeve::Host::Bridge::ModLabel(source);
      const auto result =
        Runtime::RequestAnnouncement({std::move(text), *mapped, Domain::ClientAnnouncementSource::ThirdParty, std::move(source)});
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

    Interface& Instance()
    {
      static Interface instance;
      return instance;
    }

    // Papyrus strings are taken as UTF-8, like every game string the client reads.
    bool PapyrusPostAnnouncement(RE::StaticFunctionTag*, std::string text, std::int32_t kind, std::string source)
    {
      return Post(std::move(text), kind, std::move(source)) == Api::Result::Queued;
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

// Found by other plugins with GetProcAddress (DreamsleeveAPI::RequestPluginAPI).
extern "C" __declspec(dllexport) void* RequestPluginAPI(const DreamsleeveAPI::InterfaceVersion interfaceVersion)
{
  if (interfaceVersion == DreamsleeveAPI::InterfaceVersion::V1)
    return static_cast<DreamsleeveAPI::IVDreamsleeve1*>(&ModApi::Detail::Instance());
  return nullptr;
}
