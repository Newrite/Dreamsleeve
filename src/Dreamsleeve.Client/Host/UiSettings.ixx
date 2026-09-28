module;

#include <glaze/glaze.hpp>
#include <glaze/toml.hpp>

export module Dreamsleeve.Host.UiSettings;

import std;

// Settings written by the web UI and the SKSE menu. They live in their own TOML
// next to the Core client configuration: the Core file is user-authored and only
// read by LoadClientSettings, this file is rewritten by the plugin.
export namespace Dreamsleeve::Host
{

  // Mirrors bridge/types.ts Settings, including the same defaults and bounds.
  struct UiSettings
  {
    bool   showFireflyNames{true};
    bool   fireflyNameOcclusion{true};
    double fireflyNameFontSize{18};
    double fireflyNameOffset{35};
    // Chat bubbles above fireflies; independent of names and of the chat window fade.
    bool        showBubbles{true};
    double      bubbleDuration{8.0};      // Seconds a message stays fully visible.
    bool        bubbleFade{true};
    double      bubbleFadeDuration{1.0};  // Seconds of the fade-out after the display time.
    double      bubbleFontSize{16};       // HUD units.
    double      bubbleMaxWidth{320};      // HUD units, including padding.
    double      bubbleBackground{0.65};   // Background fill only; the text stays opaque.
    std::string onlineView{"cards"};
    bool        fade{true};
    double      delay{12.0};
    double      duration{1.0};
    double      idleOpacity{0.0};
    double      scale{1.0};
    double      fontSize{16.0};
    std::string font{"sans"};
    double      lineHeight{1.45};
    double      background{0.82};
    bool        timestamps{true};
    bool        fullColor{false};
    // username | display | character. "account" from older files reads as username.
    std::string nameMode{"display"};
    // Local pseudonyms instead of every real name; not sent to the server.
    bool        streamerMode{false};
    bool        locked{true};
    double      x{0.025};
    double      y{0.42};
    double      width{680.0};
    double      height{390.0};
    std::string activationKey{"Enter"};
    std::string theme{"skyrim"};

    bool operator==(const UiSettings&) const = default;
  };

  // Host-owned records, never round-tripped through the web UI. IDs are
  // decimal strings scoped by the server address: an account ID is unique
  // only within the server that issued it.
  struct AliasRecord
  {
    std::string server;
    std::string id;
    std::string name;

    bool operator==(const AliasRecord&) const = default;
  };

  struct IgnoredRecord
  {
    std::string server;
    std::string id;
    // Names known when the player was ignored; shown only outside streamer mode.
    std::string displayName;
    std::string username;

    bool operator==(const IgnoredRecord&) const = default;
  };

  struct NameBook
  {
    std::vector<AliasRecord>   aliases;
    std::vector<IgnoredRecord> ignored;

    bool operator==(const NameBook&) const = default;
  };

  constexpr std::size_t MaxAliasRecords   = 4096;
  constexpr std::size_t MaxIgnoredRecords = 1000;

  struct UiSection
  {
    // Full user opt-out: no view, no focus, no activation key. Network keeps running.
    bool       hideUi{false};
    UiSettings chat{};

    bool operator==(const UiSection&) const = default;
  };

  struct UiFile
  {
    int       version{1};
    UiSection ui{};
    NameBook  names{};

    bool operator==(const UiFile&) const = default;
  };

  namespace UiSettingsDetail
  {

    double Clamp(double value, double low, double high, double fallback)
    {
      if (!std::isfinite(value)) return fallback;
      return std::min(high, std::max(low, value));
    }

    void Choose(std::string& value, std::initializer_list<std::string_view> allowed, std::string_view fallback)
    {
      for (auto option : allowed)
        if (value == option) return;
      value = fallback;
    }

  }

  // Same bounds as state/settings.ts; hand-edited files fall back per field.
  UiSettings Normalize(UiSettings value)
  {
    using UiSettingsDetail::Choose;
    using UiSettingsDetail::Clamp;
    const UiSettings defaults{};

    Choose(value.onlineView, {"cards", "list"}, defaults.onlineView);
    Choose(value.font, {"serif", "sans"}, defaults.font);
    if (value.nameMode == "account") value.nameMode = "username";
    Choose(value.nameMode, {"username", "display", "character"}, defaults.nameMode);
    Choose(value.activationKey, {"Enter", "F2"}, defaults.activationKey);
    Choose(value.theme, {"skyrim", "contrast"}, defaults.theme);

    value.fireflyNameFontSize = Clamp(value.fireflyNameFontSize, 8, 48, defaults.fireflyNameFontSize);
    value.fireflyNameOffset   = Clamp(value.fireflyNameOffset, 0, 512, defaults.fireflyNameOffset);
    value.bubbleDuration      = Clamp(value.bubbleDuration, 1, 60, defaults.bubbleDuration);
    value.bubbleFadeDuration  = Clamp(value.bubbleFadeDuration, 0.1, 5, defaults.bubbleFadeDuration);
    value.bubbleFontSize      = Clamp(value.bubbleFontSize, 8, 48, defaults.bubbleFontSize);
    value.bubbleMaxWidth      = Clamp(value.bubbleMaxWidth, 120, 800, defaults.bubbleMaxWidth);
    value.bubbleBackground    = Clamp(value.bubbleBackground, 0, 1, defaults.bubbleBackground);
    value.delay               = Clamp(value.delay, 0, 120, defaults.delay);
    value.duration            = Clamp(value.duration, 0, 5, defaults.duration);
    value.idleOpacity         = Clamp(value.idleOpacity, 0, 1, defaults.idleOpacity);
    value.scale               = Clamp(value.scale, 0.7, 1.5, defaults.scale);
    value.fontSize            = Clamp(value.fontSize, 12, 26, defaults.fontSize);
    value.lineHeight          = Clamp(value.lineHeight, 1.1, 2, defaults.lineHeight);
    value.background          = Clamp(value.background, 0, 1, defaults.background);
    value.x                   = Clamp(value.x, 0, 1, defaults.x);
    value.y                   = Clamp(value.y, 0, 1, defaults.y);
    value.width               = Clamp(value.width, 320, 1600, defaults.width);
    value.height              = Clamp(value.height, 220, 1200, defaults.height);
    return value;
  }

  // Hand-edited records: drop incomplete or duplicate entries and keep the
  // newest ones within the bounds.
  NameBook Normalize(NameBook book)
  {
    const auto incomplete = [](const auto& record) {
      return record.server.empty() || record.id.empty();
    };
    std::erase_if(book.aliases, [&](const AliasRecord& record) { return incomplete(record) || record.name.empty(); });
    std::erase_if(book.ignored, incomplete);
    const auto unique = [](auto& records) {
      std::set<std::pair<std::string, std::string>> seen;
      std::erase_if(records, [&](const auto& record) { return !seen.emplace(record.server, record.id).second; });
    };
    unique(book.aliases);
    unique(book.ignored);
    if (book.aliases.size() > MaxAliasRecords)
      book.aliases.erase(book.aliases.begin(), book.aliases.end() - static_cast<std::ptrdiff_t>(MaxAliasRecords));
    if (book.ignored.size() > MaxIgnoredRecords)
      book.ignored.erase(book.ignored.begin(), book.ignored.end() - static_cast<std::ptrdiff_t>(MaxIgnoredRecords));
    return book;
  }

  // A missing file is the ordinary first run and yields defaults. A present but
  // unreadable file is an error: the caller keeps its current values and reports it.
  std::expected<UiFile, std::string> LoadUiFile(const std::filesystem::path& path, UiFile defaults = {})
  {
    std::error_code probe;
    if (!std::filesystem::exists(path, probe)) return defaults;

    std::ifstream input{path, std::ios::binary | std::ios::ate};
    if (!input) return std::unexpected{"Cannot open UI settings"};
    const auto length = input.tellg();
    // Pseudonyms and the ignore list share the file, hence the larger bound.
    if (length < 0 || length > (1 << 20)) return std::unexpected{"UI settings must not exceed 1 MiB"};

    std::string source(static_cast<std::size_t>(length), '\0');
    input.seekg(0);
    if (!input.read(source.data(), static_cast<std::streamsize>(source.size()))) return std::unexpected{"Cannot read UI settings"};

    UiFile file = std::move(defaults);
    if (auto error = glz::read<glz::opts{.format = glz::TOML, .error_on_unknown_keys = false}>(file, source); !source.empty() && error)
      return std::unexpected{"Invalid UI TOML: " + glz::format_error(error, source)};
    if (file.version != 1) return std::unexpected{"Unsupported UI settings version"};

    file.ui.chat = Normalize(file.ui.chat);
    file.names   = Normalize(std::move(file.names));
    return file;
  }

  // Temporary file plus rename: a crash mid-write never leaves a truncated file.
  std::expected<void, std::string> SaveUiFile(const std::filesystem::path& path, const UiFile& file)
  {
    auto text = glz::write_toml(file);
    if (!text) return std::unexpected{"Cannot encode UI settings"};

    std::error_code error;
    if (path.has_parent_path()) std::filesystem::create_directories(path.parent_path(), error);
    if (error) return std::unexpected{"Cannot create UI settings directory: " + error.message()};

    auto temporary  = path;
    temporary      += ".tmp";
    {
      std::ofstream output{temporary, std::ios::binary | std::ios::trunc};
      if (!output) return std::unexpected{"Cannot write UI settings"};
      output << *text << '\n';
      if (!output.flush()) return std::unexpected{"Cannot flush UI settings"};
    }

    std::filesystem::rename(temporary, path, error);
    if (error)
    {
      std::filesystem::remove(temporary, error);
      return std::unexpected{"Cannot replace UI settings file"};
    }
    return {};
  }

}
