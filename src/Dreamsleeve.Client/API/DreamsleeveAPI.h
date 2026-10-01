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

namespace DreamsleeveAPI
{

  // Available interface versions. Versions are append-only: a released
  // interface keeps its methods, their order and their signatures. Only plain
  // types cross the DLL boundary (null-terminated UTF-8 strings, function
  // pointers), so a plugin built with another toolset, STL or debug settings works.
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
    Queued       = 0,  // Accepted locally; the outcome follows in the result callback.
    Published    = 1,  // The server published the announcement.
    NotConnected = 2,  // No ready session; the request was not sent.
    Rejected     = 3,  // Refused; the callback reason tells why (synchronously: a null, overlong or unreadable string or an unknown kind).
    Busy         = 4,  // A local queue is full; try later.
    RateLimited  = 5,  // Too frequent or repeated; the server limit is per player account.
    Failed       = 6   // Delivery unknown: the session changed or the request could not be encoded.
  };

  enum class CallbackResult : std::uint8_t
  {
    OK                = 0,
    AlreadyRegistered = 1,
    NotRegistered     = 2,
    InvalidCallback   = 3  // The callback is null.
  };

  // Outcome of an announcement that returned Queued. The strings are
  // null-terminated UTF-8, never null, and valid only during the callback:
  // copy what you keep.
  struct AnnouncementResult
  {
    APIResult   result;
    const char* source;  // The source label as accepted by PostAnnouncement (converted to UTF-8 if it was not).
    const char* text;
    const char* reason;  // Readable refusal (Russian UI text); empty when published.
  };

  // Called on the game main thread, once per queued announcement of any mod;
  // filter by source. A capture-less lambda converts to it.
  using AnnouncementResultCallback = void (*)(const AnnouncementResult* result);

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
    // utf8Text: null-terminated UTF-8, up to 500 characters by default (the
    // server announces its limit), newlines allowed. utf8Source: the name of your
    // mod, null-terminated UTF-8, one line, up to 64 characters by default; it is
    // shown next to the text and never raises trust. A string that is not UTF-8
    // is read in the system ANSI code page; a null or unreadable one is Rejected.
    // Both are copied before the call returns. Queued is not publication: the
    // outcome arrives in the result callback.
    virtual APIResult PostAnnouncement(const char* utf8Text, AnnouncementKind kind, const char* utf8Source) noexcept = 0;

    // One callback per plugin; InvalidCallback for null.
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
