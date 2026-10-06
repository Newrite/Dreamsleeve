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

// TOML files the player edits, before glaze reads them.
export namespace Dreamsleeve::Utils::Toml
{

  // glaze 7 reads an array only on one line and without a comma after the
  // last element, while TOML allows newlines, comments and that comma. Inside
  // brackets this turns newlines into spaces and blanks comments and the last
  // comma; strings stay as they are. The length is kept, so glaze reports
  // errors at the original line and column. Multi-line strings inside an
  // array are not supported.
  std::string OneLineArrays(std::string_view source)
  {
    std::string result{source};
    std::size_t depth{};
    char        quote{};
    for (std::size_t at = 0; at < result.size(); ++at)
    {
      const char c = result[at];
      if (quote)
      {
        if (quote == '"' && c == '\\')
          ++at;
        else if (c == quote || c == '\n')
          quote = 0;
      }
      else if (c == '"' || c == '\'')
        quote = c;
      else if (c == '#')
      {
        for (; at < result.size() && result[at] != '\n' && result[at] != '\r'; ++at)
          if (depth != 0) result[at] = ' ';
        --at;
      }
      else if (c == '[')
        ++depth;
      else if (c == ']' && depth != 0)
      {
        // Newlines and comments before it are blanks by now.
        const auto last = result.find_last_not_of(" \t", at - 1);
        if (last != std::string::npos && result[last] == ',') result[last] = ' ';
        --depth;
      }
      else if (depth != 0 && (c == '\n' || c == '\r'))
        result[at] = ' ';
    }
    return result;
  }

}

// The Windows clipboard.
export namespace Dreamsleeve::Utils::Clipboard
{

  // Puts UTF-8 text on the clipboard as Unicode text; false when Windows refuses.
  bool Copy(std::string_view text)
  {
    const int size  = static_cast<int>(text.size());
    const int count = text.empty() ? 0 : MultiByteToWideChar(CP_UTF8, 0, text.data(), size, nullptr, 0);
    if (count == 0 && !text.empty()) return false;
    HGLOBAL memory = GlobalAlloc(GMEM_MOVEABLE, (static_cast<std::size_t>(count) + 1) * sizeof(wchar_t));
    if (!memory) return false;
    auto* target = static_cast<wchar_t*>(GlobalLock(memory));
    if (!target)
    {
      GlobalFree(memory);
      return false;
    }
    if (count != 0) MultiByteToWideChar(CP_UTF8, 0, text.data(), size, target, count);
    target[count] = L'\0';
    GlobalUnlock(memory);
    if (!OpenClipboard(nullptr))
    {
      GlobalFree(memory);
      return false;
    }
    EmptyClipboard();
    // The clipboard owns the memory once it takes it.
    const bool placed = SetClipboardData(CF_UNICODETEXT, memory) != nullptr;
    CloseClipboard();
    if (!placed) GlobalFree(memory);
    return placed;
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

export namespace Dreamsleeve::Utils::Time
{

  // Caller has consumed one due sample (next <= now), interval is positive.
  // Advance to the first future slot, without drift or duplicate catch-up samples.
  template <class Rep, class Period>
  void AdvanceSample(
    std::chrono::steady_clock::time_point& next,
    std::chrono::steady_clock::time_point  now,
    std::chrono::duration<Rep, Period>     interval)
  {
    if (next == std::chrono::steady_clock::time_point{}) next = now;
    next += interval * ((now - next) / interval + 1);
  }

}
