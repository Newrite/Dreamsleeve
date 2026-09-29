/*
 * For modders: copy this file into your own SKSE plugin to use the Dreamsleeve API.
 * It expects CommonLibSSE-NG to be included first (SKSE::PluginHandle).
 * Documentation: docs/DreamsleeveModApiRu.md in the Dreamsleeve repository.
 */
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif

#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <Windows.h>
#include <cstdint>
#include <functional>
#include <string>
#include <string_view>

namespace DreamsleeveAPI
{

  constexpr const auto DreamsleevePluginName = "DreamsleeveClient";

  // Available interface versions. Versions are append-only: a released
  // interface keeps its methods, their order and their signatures.
  enum class InterfaceVersion : std::uint8_t
  {
    V1
  };

  // Kinds a mod may request. Administrator and scheduled notices belong to the server.
  enum class AnnouncementKind : std::uint8_t
  {
    Announcement = 0,  // General notice.
    Event        = 1   // Game event: a death, an achievement, a discovery.
  };

  // Synchronous admission (Queued or a refusal) and asynchronous outcomes
  // (Published or a refusal). New values may be appended.
  enum class APIResult : std::uint8_t
  {
    Queued        = 0,  // Accepted locally; the outcome follows in the result callback.
    Published     = 1,  // The server published the announcement.
    NotConnected  = 2,  // No ready session, or it ended before the server replied.
    Rejected      = 3,  // Refused: the server disabled mod announcements, the word list, an invalid request.
    TooLong       = 4,  // Text exceeds the limit announced by the server.
    InvalidText   = 5,  // Empty, blank, not UTF-8, or control characters other than tab and newline.
    InvalidSource = 6,  // Empty, blank, multiline or too long source label.
    InvalidKind   = 7,  // Not an AnnouncementKind value.
    Busy          = 8,  // A local queue is full; try later.
    RateLimited   = 9,  // Too frequent or repeated; the server limit is per player account.
    Failed        = 10  // Delivery unknown: the session changed or the request could not be encoded.
  };

  enum class CallbackResult : std::uint8_t
  {
    OK                = 0,
    AlreadyRegistered = 1,
    NotRegistered     = 2
  };

  // Outcome of an announcement that returned Queued.
  struct AnnouncementResult
  {
    APIResult   result;
    std::string source;  // The source label passed to PostAnnouncement.
    std::string text;
    std::string reason;  // Readable refusal (Russian UI text); empty when published.
  };

  // Called on the game main thread, once per queued announcement of any mod;
  // filter by source.
  using AnnouncementResultCallback = std::function<void(const AnnouncementResult&)>;

  // Dreamsleeve modder interface v1. Every method may be called from any thread.
  class IVDreamsleeve1
  {
protected:

    ~IVDreamsleeve1() = default;

public:

    // Dreamsleeve.Client.dll version packed as major << 24 | minor << 16 | patch << 4 | build.
    virtual std::uint32_t GetPluginVersion() const noexcept = 0;

    // True while a server session is ready.
    virtual bool IsConnected() const noexcept = 0;

    // Asks the server to publish text in the system channel ("Объявления").
    // text: UTF-8, 1..500 characters by default (the server announces its limit),
    // newlines allowed. source: the name of your mod, UTF-8, one line, 1..64
    // characters by default; it is shown next to the text and never raises trust.
    // Both are copied before the call returns. Queued is not publication: the
    // outcome arrives in the result callback.
    virtual APIResult PostAnnouncement(std::string_view text, AnnouncementKind kind, std::string_view source) noexcept = 0;

    // One callback per plugin.
    virtual CallbackResult AddAnnouncementResultCallback(SKSE::PluginHandle plugin, AnnouncementResultCallback callback) noexcept = 0;

    virtual CallbackResult RemoveAnnouncementResultCallback(SKSE::PluginHandle plugin) noexcept = 0;
  };

  typedef void* (*RequestPluginAPIFunc)(InterfaceVersion interfaceVersion);

  // Request the Dreamsleeve API interface. Call at SKSE kPostLoad or later so the
  // DLL is loaded. Returns nullptr when Dreamsleeve is absent or lacks the version.
  [[nodiscard]] inline void* RequestPluginAPI(InterfaceVersion interfaceVersion = InterfaceVersion::V1)
  {
    auto pluginHandle = GetModuleHandleW(L"Dreamsleeve.Client.dll");
    if (!pluginHandle) return nullptr;

    auto requestAPIFunction = reinterpret_cast<RequestPluginAPIFunc>(GetProcAddress(pluginHandle, "RequestPluginAPI"));
    if (requestAPIFunction) return requestAPIFunction(interfaceVersion);

    return nullptr;
  }

}
