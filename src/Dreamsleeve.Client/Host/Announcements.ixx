export module Dreamsleeve.Host.Announcements;

import std;
export import Dreamsleeve.Client.Domain;

// Announcements that other mods request through the plugin API. The request is
// queued as given; Core checks it against the limits the server announced, and
// the server decides.
export namespace Dreamsleeve::Host::Announcements
{

  // Values of DreamsleeveAPI::APIResult; the plugin API maps them one to one.
  enum class Result : std::uint32_t
  {
    Queued       = 0,  // Accepted locally; the final outcome follows asynchronously.
    Published    = 1,  // The server published it (asynchronous outcome only).
    NotConnected = 2,  // No ready session, or it ended before a reply.
    Rejected     = 3,  // Refused locally or by the server; the reason tells why.
    Busy         = 4,  // A local queue is full.
    RateLimited  = 5,  // Too frequent or repeated; try later.
    Failed       = 6   // Delivery unknown: the session changed or encoding failed.
  };

  // Copied from the caller before the API call returns.
  struct Request
  {
    std::string                      text;
    Domain::AnnouncementKind         kind{Domain::AnnouncementKind::Announcement};
    Domain::ClientAnnouncementSource source{Domain::ClientAnnouncementSource::ThirdParty};
    std::string                      signature;
  };

  // Final result of a queued request, reported to the requesting mods.
  struct Outcome
  {
    std::string signature;
    std::string text;
    Result      result{Result::Failed};
    std::string reason;  // Readable refusal for the log and the UI; empty when published.
  };

  std::string_view ResultName(Result result)
  {
    switch (result)
    {
      case Result::Queued:
        return "queued";
      case Result::Published:
        return "published";
      case Result::NotConnected:
        return "not connected";
      case Result::Rejected:
        return "rejected";
      case Result::Busy:
        return "busy";
      case Result::RateLimited:
        return "rate limited";
      case Result::Failed:
        return "failed";
    }
    return "unknown";
  }

}
