module;

#include "Prelude.hpp"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>

#include "API/DreamsleeveAPI.h"

export module Dreamsleeve.ModApi;

import std;
import Dreamsleeve.Runtime;

// Plugin API for other mods: the C++ interface handed out through SKSE
// messaging and the Papyrus script DreamsleeveClient. Calls may arrive on any
// thread; they are checked and queued in Runtime, and the frame posts them to
// Core. Outcomes return on the main thread. See docs/DreamsleeveModApiRu.md.
export namespace ModApi
{

  namespace Api = Dreamsleeve::Host::Announcements;

  constexpr std::string_view PapyrusClass    = "DreamsleeveClient";
  constexpr std::string_view ResultModEvent  = "Dreamsleeve_AnnouncementResult";
  constexpr std::size_t      MaxLoggedSource = 64;

  static_assert(static_cast<std::uint32_t>(DreamsleeveAPI::APIResult::Failed) == static_cast<std::uint32_t>(Api::Result::Failed));
  static_assert(static_cast<std::uint32_t>(DreamsleeveAPI::APIResult::RateLimited) == static_cast<std::uint32_t>(Api::Result::RateLimited));
  static_assert(static_cast<std::uint32_t>(DreamsleeveAPI::APIResult::Queued) == static_cast<std::uint32_t>(Api::Result::Queued));

  namespace Detail
  {

    // Log lines carry mod text as a value, cut to a bounded length.
    std::string Loggable(std::string_view text)
    {
      return Dreamsleeve::Host::Bridge::SafeLabel(text, MaxLoggedSource);
    }

    std::optional<Domain::AnnouncementKind> Kind(std::uint32_t value)
    {
      switch (static_cast<DreamsleeveAPI::AnnouncementKind>(value))
      {
        case DreamsleeveAPI::AnnouncementKind::Announcement:
          return Domain::AnnouncementKind::Announcement;
        case DreamsleeveAPI::AnnouncementKind::Event:
          return Domain::AnnouncementKind::Event;
      }
      return std::nullopt;
    }

    Api::Result Post(std::string text, std::uint32_t kind, std::string source)
    {
      const auto mapped = Kind(kind);
      if (!mapped) return Api::Result::InvalidKind;
      const auto result =
        Runtime::RequestAnnouncement({std::move(text), *mapped, Domain::ClientAnnouncementSource::ThirdParty, std::move(source)});
      return result;
    }

    // Papyrus strings are bytes of the game's text encoding, which on a
    // localized install is the ANSI code page rather than UTF-8.
    std::string FromPapyrus(std::string value)
    {
      if (Api::CountCodePoints(value, true)) return value;
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

    void LogAdmission(std::string_view source, Api::Result result)
    {
      if (result == Api::Result::Queued)
        logger::info("Announcement from {} queued", Loggable(source));
      else
        logger::warn("Announcement from {} refused locally: {}", Loggable(source), Api::ResultName(result));
    }

    class Interface final : public DreamsleeveAPI::IVDreamsleeve1
    {
  public:

      DreamsleeveAPI::InterfaceVersion GetInterfaceVersion() const noexcept override
      {
        return DreamsleeveAPI::InterfaceVersion::V1;
      }

      std::uint32_t GetPluginVersion() const noexcept override
      {
        return SKSE::PluginDeclaration::GetSingleton()->GetVersion().pack();
      }

      bool IsConnected() const noexcept override
      {
        return Runtime::AnnouncementsConnected();
      }

      DreamsleeveAPI::APIResult PostAnnouncement(const char* text, DreamsleeveAPI::AnnouncementKind kind, const char* source) noexcept
        override
      {
        try
        {
          if (!text) return DreamsleeveAPI::APIResult::InvalidText;
          if (!source) return DreamsleeveAPI::APIResult::InvalidSource;
          const auto result = Post(text, static_cast<std::uint32_t>(kind), source);
          LogAdmission(source, result);
          return static_cast<DreamsleeveAPI::APIResult>(result);
        }
        catch (...)
        {
          return DreamsleeveAPI::APIResult::Busy;
        }
      }
    };

    Interface& Instance()
    {
      static Interface instance;
      return instance;
    }

    void* Request(DreamsleeveAPI::InterfaceVersion version)
    {
      return version == DreamsleeveAPI::InterfaceVersion::V1 ? static_cast<DreamsleeveAPI::IVDreamsleeve1*>(&Instance()) : nullptr;
    }

    // Any sender: the exchange fills a function pointer in the caller's struct.
    void OnPluginMessage(SKSE::MessagingInterface::Message* message)
    {
      if (!message || message->type != DreamsleeveAPI::kMessage_RequestInterface || !message->data) return;
      if (message->dataLen < sizeof(DreamsleeveAPI::RequestInterfaceMessage)) return;
      static_cast<DreamsleeveAPI::RequestInterfaceMessage*>(message->data)->RequestPluginAPI = Request;
      logger::info("Plugin API handed to {}", message->sender ? message->sender : "?");
    }

    bool PapyrusPostAnnouncement(RE::StaticFunctionTag*, std::string text, std::int32_t kind, std::string source)
    {
      if (kind < 0) return false;
      source            = FromPapyrus(std::move(source));
      const auto result = Post(FromPapyrus(std::move(text)), static_cast<std::uint32_t>(kind), source);
      LogAdmission(source, result);
      return result == Api::Result::Queued;
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
  bool Register()
  {
    const auto messaging = SKSE::GetMessagingInterface();
    if (!messaging || !messaging->RegisterListener(nullptr, Detail::OnPluginMessage))
    {
      logger::error("Cannot register the plugin API listener");
      return false;
    }
    if (const auto papyrus = SKSE::GetPapyrusInterface(); !papyrus || !papyrus->Register(Detail::RegisterPapyrus))
    {
      logger::error("Cannot register Papyrus functions of {}", PapyrusClass);
      return false;
    }
    return true;
  }

  // Main thread. Tells every listening plugin and every script registered for
  // the mod event; the log keeps a line per outcome.
  void Report(const Api::Outcome& outcome)
  {
    const auto source = Detail::Loggable(outcome.signature);
    if (outcome.result == Api::Result::Published)
      logger::info("Announcement from {} published", source);
    else
      logger::warn("Announcement from {} not published: {} ({})", source, Api::ResultName(outcome.result), outcome.reason);

    DreamsleeveAPI::AnnouncementResultMessage message{
        static_cast<DreamsleeveAPI::APIResult>(outcome.result),
        outcome.signature.c_str(),
        outcome.text.c_str(),
        outcome.reason.c_str()
    };
    if (const auto messaging = SKSE::GetMessagingInterface())
      messaging->Dispatch(DreamsleeveAPI::kMessage_AnnouncementResult, &message, static_cast<std::uint32_t>(sizeof(message)), nullptr);

    if (const auto events = SKSE::GetModCallbackEventSource())
    {
      SKSE::ModCallbackEvent
        event{RE::BSFixedString{ResultModEvent}, RE::BSFixedString{outcome.signature}, static_cast<float>(outcome.result), nullptr};
      events->SendEvent(&event);
    }
  }

}
