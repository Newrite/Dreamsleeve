export module Dreamsleeve.Host.Announcements;

import std;
export import Dreamsleeve.Client.Domain;

// Local admission of announcements that other mods request through the plugin
// API. Pure and thread-agnostic: API calls may arrive on any thread, so they are
// checked here against a copy of the session state, then queued for the frame.
// The server repeats every check and is the one that decides.
export namespace Dreamsleeve::Host::Announcements
{

  // Values of DreamsleeveAPI::APIResult; the plugin API maps them one to one.
  enum class Result : std::uint32_t
  {
    Queued        = 0,  // Accepted locally; the final outcome follows asynchronously.
    Published     = 1,  // The server published it (asynchronous outcome only).
    NotConnected  = 2,  // No ready session, or it ended before a reply.
    Rejected      = 3,  // The server refused it: disabled source, word list, invalid request.
    TooLong       = 4,  // Text exceeds the server limit.
    InvalidText   = 5,  // Empty, blank, invalid UTF-8 or control characters.
    InvalidSource = 6,  // Missing, blank, too long or multiline mod label.
    InvalidKind   = 7,  // Not a kind a client may request.
    Busy          = 8,  // A local queue is full.
    RateLimited   = 9,  // Too frequent or repeated; try later.
    Failed        = 10  // Delivery unknown: the session changed or encoding failed.
  };

  // Copied from the caller before the API call returns.
  struct Request
  {
    std::string                      text;
    Domain::AnnouncementKind         kind{Domain::AnnouncementKind::Announcement};
    Domain::ClientAnnouncementSource source{Domain::ClientAnnouncementSource::ThirdParty};
    std::string                      signature;
  };

  // Session facts the check needs; the main thread refreshes the copy. The
  // policy comes with the session welcome and is absent without a session.
  struct Gate
  {
    std::optional<Domain::AnnouncementPolicy> policy;
  };

  // Final result of a queued request, reported to the requesting mods.
  struct Outcome
  {
    std::string signature;
    std::string text;
    Result      result{Result::Failed};
    std::string reason;  // Readable refusal for the log and the UI; empty when published.
  };

  // Bounds before any server limit is known: the largest the protocol allows.
  constexpr std::size_t MaxTextCodePoints      = 2000;
  constexpr std::size_t MaxSignatureCodePoints = 128;

  // Code points of well-formed UTF-8 without control characters, or nullopt.
  // Multiline text may contain tab, LF and CR, like chat; a label may not.
  std::optional<std::size_t> CountCodePoints(std::string_view text, bool multiline)
  {
    std::size_t count = 0;
    for (std::size_t index = 0; index < text.size(); ++count)
    {
      const auto  lead = static_cast<unsigned char>(text[index]);
      std::size_t length{};
      char32_t    value{};
      if (lead < 0x80)
      {
        length = 1;
        value  = lead;
      }
      else if (lead >= 0xC2 && lead <= 0xDF)
      {
        length = 2;
        value  = lead & 0x1F;
      }
      else if (lead >= 0xE0 && lead <= 0xEF)
      {
        length = 3;
        value  = lead & 0x0F;
      }
      else if (lead >= 0xF0 && lead <= 0xF4)
      {
        length = 4;
        value  = lead & 0x07;
      }
      else
        return std::nullopt;
      if (index + length > text.size()) return std::nullopt;
      for (std::size_t next = 1; next < length; ++next)
      {
        const auto byte = static_cast<unsigned char>(text[index + next]);
        if ((byte & 0xC0) != 0x80) return std::nullopt;
        value = (value << 6) | (byte & 0x3F);
      }
      // Overlong forms, surrogates and values beyond Unicode are not text.
      const char32_t minimum = length == 2 ? 0x80 : length == 3 ? 0x800 : length == 4 ? 0x10000 : 0;
      if (value < minimum || (value >= 0xD800 && value <= 0xDFFF) || value > 0x10FFFF) return std::nullopt;
      const bool allowed = multiline && (value == U'\t' || value == U'\n' || value == U'\r');
      if (!allowed && (value < 0x20 || (value >= 0x7F && value <= 0x9F) || value == 0x2028 || value == 0x2029)) return std::nullopt;
      index += length;
    }
    return count;
  }

  bool Blank(std::string_view text)
  {
    return std::ranges::all_of(text, [](char c) { return c == ' ' || c == '\t' || c == '\n' || c == '\r'; });
  }

  // Format first, so a malformed call is reported the same way online and
  // offline; then the session and what its server announced.
  Result Check(const Request& request, const Gate& gate)
  {
    using Kind = Domain::AnnouncementKind;
    if (request.kind != Kind::Announcement && request.kind != Kind::Event) return Result::InvalidKind;

    const auto text = CountCodePoints(request.text, true);
    if (!text || request.text.empty() || Blank(request.text)) return Result::InvalidText;
    if (*text > MaxTextCodePoints) return Result::TooLong;

    const auto label         = CountCodePoints(request.signature, false);
    const bool labelRequired = request.source == Domain::ClientAnnouncementSource::ThirdParty;
    if (
      !label || *label > MaxSignatureCodePoints || (labelRequired && Blank(request.signature)) ||
      (!request.signature.empty() && Blank(request.signature)))
      return Result::InvalidSource;

    if (!gate.policy) return Result::NotConnected;
    if (!gate.policy->Allows(request.source)) return Result::Rejected;
    if (*text > gate.policy->maxTextLength) return Result::TooLong;
    if (*label > gate.policy->maxSignatureLength) return Result::InvalidSource;
    return Result::Queued;
  }

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
      case Result::TooLong:
        return "too long";
      case Result::InvalidText:
        return "invalid text";
      case Result::InvalidSource:
        return "invalid source";
      case Result::InvalidKind:
        return "invalid kind";
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
