module;
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

export module Dreamsleeve.Client.Utils;

import std;

// Reusable helpers of Core and the game client, one namespace per topic.

// UTF-8 text. It is checked once, where it enters the client (ValidUtf8);
// past that point it is assumed well-formed and only measured or cut.
export namespace Dreamsleeve::Utils::Text
{

  // Well-formed UTF-8: no truncated or overlong sequences, surrogates or
  // values beyond Unicode.
  bool ValidUtf8(std::string_view text)
  {
    if (text.empty()) return true;
    if (text.size() > static_cast<std::size_t>(std::numeric_limits<int>::max())) return false;
    return MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0) > 0;
  }

  // Well-formed UTF-8 of any bytes: each invalid sequence becomes U+FFFD, as
  // Windows decodes it. Text of unknown encoding (the game's) passes here once.
  std::string Repair(std::string_view text)
  {
    if (ValidUtf8(text)) return std::string{text};
    if (text.size() > static_cast<std::size_t>(std::numeric_limits<int>::max())) return {};
    const auto   size = static_cast<int>(text.size());
    std::wstring wide(static_cast<std::size_t>(MultiByteToWideChar(CP_UTF8, 0, text.data(), size, nullptr, 0)), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, text.data(), size, wide.data(), static_cast<int>(wide.size()));
    const auto  wideSize = static_cast<int>(wide.size());
    std::string result(
      static_cast<std::size_t>(WideCharToMultiByte(CP_UTF8, 0, wide.data(), wideSize, nullptr, 0, nullptr, nullptr)),
      '\0');
    WideCharToMultiByte(CP_UTF8, 0, wide.data(), wideSize, result.data(), static_cast<int>(result.size()), nullptr, nullptr);
    return result;
  }

  // Text in a Windows code page (CP_ACP = 0 for the system ANSI one) as UTF-8;
  // nullopt when the bytes are not valid in that code page.
  std::optional<std::string> FromCodePage(std::string_view text, std::uint32_t codePage)
  {
    if (text.empty()) return std::string{};
    if (text.size() > static_cast<std::size_t>(std::numeric_limits<int>::max())) return std::nullopt;
    const auto size     = static_cast<int>(text.size());
    const int  wideSize = MultiByteToWideChar(codePage, MB_ERR_INVALID_CHARS, text.data(), size, nullptr, 0);
    if (wideSize <= 0) return std::nullopt;
    std::wstring wide(static_cast<std::size_t>(wideSize), L'\0');
    MultiByteToWideChar(codePage, MB_ERR_INVALID_CHARS, text.data(), size, wide.data(), wideSize);
    const int bytes = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide.data(), wideSize, nullptr, 0, nullptr, nullptr);
    if (bytes <= 0) return std::nullopt;
    std::string result(static_cast<std::size_t>(bytes), '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide.data(), wideSize, result.data(), bytes, nullptr, nullptr);
    return result;
  }

  // C0 and C1 control characters and DEL, line breaks included, in
  // well-formed UTF-8 (C1 is encoded as C2 80..C2 9F).
  bool HasControl(std::string_view text)
  {
    for (std::size_t index = 0; index < text.size(); ++index)
    {
      const auto byte = static_cast<unsigned char>(text[index]);
      if (byte < 0x20 || byte == 0x7F) return true;
      if (byte == 0xC2 && index + 1 < text.size() && static_cast<unsigned char>(text[index + 1]) < 0xA0) return true;
    }
    return false;
  }

  // A UTF-8 continuation byte (10xxxxxx): never the start of a code point.
  constexpr bool Continuation(char byte) noexcept
  {
    return (static_cast<unsigned char>(byte) & 0xC0) == 0x80;
  }

  // Whether a cut at byte `at` keeps well-formed UTF-8 on both sides.
  constexpr bool CodePointBoundary(std::string_view text, std::size_t at) noexcept
  {
    return at == text.size() || (at < text.size() && !Continuation(text[at]));
  }

  // Code points of well-formed UTF-8, the unit the server limits count.
  std::size_t CodePoints(std::string_view text)
  {
    return static_cast<std::size_t>(std::ranges::count_if(text, [](char c) { return !Continuation(c); }));
  }

  // The first `count` code points.
  std::string_view Prefix(std::string_view text, std::size_t count)
  {
    std::size_t end = 0;
    for (std::size_t seen = 0; end < text.size(); ++end)
    {
      if (Continuation(text[end])) continue;
      if (seen == count) break;
      ++seen;
    }
    return text.substr(0, end);
  }

  // The first `count` code points without trailing spaces, then an ellipsis.
  std::string Ellipsize(std::string_view text, std::size_t count)
  {
    auto kept = Prefix(text, count);
    while (kept.ends_with(' '))
      kept.remove_suffix(1);
    return std::string{kept} + "\xE2\x80\xA6";
  }

  // Identifiers fold ASCII case only; other bytes stay as they are.
  constexpr char AsciiLower(char value) noexcept
  {
    return value >= 'A' && value <= 'Z' ? static_cast<char>(value + ('a' - 'A')) : value;
  }

  bool ContainsAsciiInsensitive(std::string_view text, std::string_view needle)
  {
    return !std::ranges::search(text, needle, {}, AsciiLower, AsciiLower).empty();
  }

  // At most `maxBytes` bytes, cut on a code point boundary.
  std::string_view ClipBytes(std::string_view text, std::size_t maxBytes)
  {
    if (text.size() <= maxBytes) return text;
    auto end = maxBytes;
    while (end > 0 && !CodePointBoundary(text, end))
      --end;
    return text.substr(0, end);
  }

}

// Pacing of retries after failures.
export namespace Dreamsleeve::Utils::Timing
{

  // Each attempt doubles the wait before the next one, up to the maximum.
  // Success resets it, and the first attempt after a reset is due at once.
  class Backoff
  {
public:

    using Clock = std::chrono::steady_clock;

    constexpr Backoff(Clock::duration minimum, Clock::duration maximum) noexcept : minimum(minimum), maximum(maximum), delay(minimum) {}

    // True when an attempt may start now; the next one is then scheduled.
    bool Due(Clock::time_point now) noexcept
    {
      if (now < next) return false;
      next  = now + delay;
      delay = std::min(maximum, delay * 2);
      return true;
    }

    void Reset() noexcept
    {
      delay = minimum;
      next  = {};
    }

private:

    Clock::duration   minimum;
    Clock::duration   maximum;
    Clock::duration   delay;
    Clock::time_point next{};
  };

}
