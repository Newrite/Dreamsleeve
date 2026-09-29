/*
 * Dreamsleeve client API for other SKSE plugins. Copy this file into your own
 * project. It needs only SKSE::MessagingInterface from CommonLibSSE-NG
 * (include it after <SKSE/SKSE.h>); no Dreamsleeve module or header is required.
 *
 * Documentation: docs/DreamsleeveModApiRu.md in the Dreamsleeve repository.
 */
#pragma once

#include <cstdint>

namespace DreamsleeveAPI
{

  // SKSE plugin name of the Dreamsleeve client (Dreamsleeve.Client.dll).
  constexpr const char* PluginName = "DreamsleeveClient";

  // Interface versions are append-only: V1 keeps its methods and layout forever.
  enum class InterfaceVersion : std::uint8_t
  {
    V1
  };

  // Kinds a mod may request. Administrator and scheduled notices belong to the server.
  enum class AnnouncementKind : std::uint32_t
  {
    Announcement = 0,  // General notice.
    Event        = 1   // Game event: a death, an achievement, a discovery.
  };

  // Synchronous admission (Queued or a refusal) and asynchronous outcomes
  // (Published or a refusal) share one set of values. New values may be added.
  enum class APIResult : std::uint32_t
  {
    Queued        = 0,   // Accepted locally; the outcome follows in kMessage_AnnouncementResult.
    Published     = 1,   // The server published the announcement.
    NotConnected  = 2,   // No ready session, or it ended before the server replied.
    Rejected      = 3,   // Refused: the server disabled this source, the word list, an invalid request.
    TooLong       = 4,   // Text exceeds the limit announced by the server.
    InvalidText   = 5,   // Null, empty, blank, not UTF-8, or control characters other than tab/newline.
    InvalidSource = 6,   // Null, empty, blank, multiline or too long source label.
    InvalidKind   = 7,   // Not an AnnouncementKind value.
    Busy          = 8,   // A local queue is full; try later.
    Unsupported   = 9,   // The server predates client announcements.
    RateLimited   = 10,  // Too frequent or repeated; the server limit is per player account.
    Failed        = 11   // Delivery unknown: the session changed or the request could not be encoded.
  };

  // SKSE messaging types; arbitrary constants to avoid collisions with other plugins.
  enum : std::uint32_t
  {
    // Consumer -> Dreamsleeve, answered synchronously inside Dispatch.
    kMessage_RequestInterface = 0x44534C01,
    // Dreamsleeve -> every plugin listening to PluginName, on the game main thread.
    kMessage_AnnouncementResult = 0x44534C02
  };

  // Filled by Dreamsleeve during Dispatch. The function stays valid for the process lifetime.
  struct RequestInterfaceMessage
  {
    void* (*RequestPluginAPI)(InterfaceVersion version) = nullptr;
  };

  // Outcome of an announcement that returned Queued. Strings are UTF-8 and
  // valid only during the listener call; copy what you keep.
  struct AnnouncementResultMessage
  {
    APIResult   result;
    const char* source;  // The source label passed to PostAnnouncement.
    const char* text;
    const char* reason;  // Readable refusal (Russian UI text); empty when published.
  };

  // Dreamsleeve modder interface v1. Every method may be called from any thread.
  class IVDreamsleeve1
  {
protected:

    ~IVDreamsleeve1() = default;

public:

    virtual InterfaceVersion GetInterfaceVersion() const noexcept = 0;

    // Dreamsleeve.Client.dll version packed as major << 24 | minor << 16 | patch << 4 | build.
    virtual std::uint32_t GetPluginVersion() const noexcept = 0;

    // True while a server session is ready.
    virtual bool IsConnected() const noexcept = 0;

    // Asks the server to publish text in the system stream (tab "Объявления").
    // text: UTF-8, 1..500 characters by default (the server announces its limit),
    // newlines allowed. source: the name of your mod, UTF-8, one line, 1..64
    // characters by default; it is shown next to the text and never raises trust.
    // Both strings are copied before the call returns. Queued is not publication:
    // the outcome arrives as kMessage_AnnouncementResult.
    virtual APIResult PostAnnouncement(const char* text, AnnouncementKind kind, const char* source) noexcept = 0;
  };

  // Call at SKSE kPostLoad or later, e.g. on kPostPostLoad. Returns nullptr when
  // Dreamsleeve is not installed or does not provide the version.
  [[nodiscard]] inline IVDreamsleeve1* RequestPluginAPI(InterfaceVersion version = InterfaceVersion::V1)
  {
    auto* messaging = SKSE::GetMessagingInterface();
    if (!messaging) return nullptr;
    RequestInterfaceMessage message{};
    if (
      !messaging->Dispatch(kMessage_RequestInterface, &message, static_cast<std::uint32_t>(sizeof(message)), PluginName) ||
      !message.RequestPluginAPI)
      return nullptr;
    return static_cast<IVDreamsleeve1*>(message.RequestPluginAPI(version));
  }

}
