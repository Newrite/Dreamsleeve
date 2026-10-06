module;

#include <glaze/glaze.hpp>
#include <glaze/toml.hpp>

export module Dreamsleeve.Host.UiSettings;

import std;
import Dreamsleeve.Client.Utils;

// Settings written by the web UI and the SKSE menu. They live in their own TOML
// next to the Core client configuration: the Core file is user-authored and only
// read by LoadClientSettings, this file is rewritten by the plugin.
export namespace Dreamsleeve::Host
{

  // The one description of the UI settings: fields and defaults here, limits in
  // the rule tables below. The web UI's Settings type, defaults and limits are
  // generated from both (src/Dreamsleeve.Client.UI/src/bridge/settings.generated.ts).
  struct UiSettings
  {
    // Fireflies (with their names and bubbles) only of players who share a guild with you.
    bool   fireflyGuildmatesOnly{false};
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
    bool streamerMode{false};
    // Ranges the server flagged: off (show) | mask (stars) | hide (whole message).
    std::string textFilter{"off"};
    bool        locked{true};
    double      x{0.025};
    double      y{0.42};
    double      width{680.0};
    double      height{390.0};
    std::string activationKey{"Enter"};
    std::string theme{"skyrim"};
    // Hidden while the player is in combat, each surface on its own.
    bool combatHideFireflies{false};
    bool combatHideNames{false};
    bool combatHideBubbles{false};
    // System channel: tab (only its own tab) | all (also the "all" view) | current (every tab).
    std::string announcementChannels{"all"};
    // Shown origins and kinds; announcement and admin kinds follow their origin only.
    bool announcementsServer{true};
    bool announcementsTrustedClient{true};
    bool announcementsThirdParty{true};
    bool announcementsEvents{true};
    bool announcementsPeriodic{true};
    // Bubble look above fireflies: the fill opacity is bubbleBackground; a
    // border and the text colour ("#RRGGBB") are separate, so "no box at all"
    // is background 0 with the border off.
    bool        bubbleBorder{true};
    std::string bubbleTextColor{"#EEECE5"};
    std::string fireflyNameColor{"#EEECE5"};
    // Height of the glow above the pose origin, game units.
    double fireflyHeightOffset{110};
    // Ground marks: notes and death places near the player (see docs/GroundMarksRu.md).
    bool        showGroundNotes{true};
    bool        showDeathMarks{true};
    // Notes and death marks only of players who share a guild with you; your own always.
    bool        markGuildmatesOnly{false};
    double      maxVisibleNotes{16};
    double      maxVisibleDeaths{16};
    double      groundDrawDistance{4096};  // Game units; the server delivers within its own radius.
    double      groundNoteOffset{5};       // Above the ground hit, game units.
    double      deathMarkOffset{5};
    double      groundNameDistance{600};   // Author name visible within, with 10% hysteresis.
    double      groundTextDistance{150};   // Text visible within, with 10% hysteresis.
    double      groundFontSize{16};
    double      groundMaxWidth{320};
    double      groundBackground{0.65};
    bool        groundBorder{true};
    std::string groundTextColor{"#EEECE5"};
    std::string deathTextColor{"#D9534F"};
    double      deathBackground{0.65};
    bool        deathBorder{true};
    bool        combatHideGroundMarks{false};  // Statics and labels.
    bool        combatHideGroundText{false};   // Labels only.
    // The in-game date as a header line on top of a mark's bubble. Style
    // "tamriel" (Тирдас, 17 Последнего зерна) or "earth" (Вторник, 17 августа);
    // the era and year are Tamriel's either way. The web UI lists use the same style.
    std::string markDateStyle{"tamriel"};
    bool        deathDateHeader{true};
    bool        noteDateHeader{false};
    std::string markDateColor{"#A9A69B"};

    bool        publishPhantoms{true};
    bool        showPhantoms{true};
    bool        phantomFallback{true};
    bool        combatHidePhantoms{false};
    double      maxVisiblePhantoms{4};
    double      phantomDrawDistance{4096};
    double      phantomOpacity{0.6};
    std::string phantomColor{"#8CCCCC"};
    double      phantomSampleRate{20};
    double      phantomDelayMs{100};
    double      phantomExtrapolationMs{100};
    double      phantomTimeoutMs{1000};
    double      phantomMemoryMiB{512};
    double      phantomCacheMiB{1024};
    double      phantomUploadKiB{5120};
    double      phantomDownloadKiB{5120};

    bool operator==(const UiSettings&) const = default;
  };

  // A number within [min, max]; integer ones are floored.
  struct NumberRule
  {
    std::string_view key;
    double           min{};
    double           max{};
    bool             integer{};
  };

  // A word from a fixed list.
  struct ChoiceRule
  {
    std::string_view              key;
    std::vector<std::string_view> values;
  };

  // Every number of UiSettings: out of range is clamped, non-finite is the default.
  constexpr auto NumberRules = std::to_array<NumberRule>({
      {"fireflyNameFontSize", 8, 48},
      {"fireflyNameOffset", 0, 512},
      {"bubbleDuration", 1, 60},
      {"bubbleFadeDuration", 0.1, 5},
      {"bubbleFontSize", 8, 48},
      {"bubbleMaxWidth", 120, 800},
      {"bubbleBackground", 0, 1},
      {"delay", 0, 120},
      {"duration", 0, 5},
      {"idleOpacity", 0, 1},
      {"scale", 0.7, 1.5},
      {"fontSize", 12, 26},
      {"lineHeight", 1.1, 2},
      {"background", 0, 1},
      {"x", 0, 1},
      {"y", 0, 1},
      {"width", 320, 1600},
      {"height", 220, 1200},
      {"fireflyHeightOffset", 0, 512},
      {"maxVisibleNotes", 1, 64, true},
      {"maxVisibleDeaths", 1, 64, true},
      {"groundDrawDistance", 0, 16384},
      {"groundNoteOffset", -64, 256},
      {"deathMarkOffset", -64, 256},
      {"groundNameDistance", 50, 4096},
      {"groundTextDistance", 50, 4096},
      {"groundFontSize", 8, 48},
      {"groundMaxWidth", 120, 800},
      {"groundBackground", 0, 1},
      {"deathBackground", 0, 1},
      {"maxVisiblePhantoms", 0, 16, true},
      {"phantomDrawDistance", 0, 16384},
      {"phantomOpacity", 0, 1},
      {"phantomSampleRate", 1, 50, true},
      {"phantomDelayMs", 0, 500, true},
      {"phantomExtrapolationMs", 0, 250, true},
      {"phantomTimeoutMs", 500, 5000, true},
      {"phantomMemoryMiB", 64, 2048, true},
      {"phantomCacheMiB", 0, 8192, true},
      {"phantomUploadKiB", 64, 8192, true},
      {"phantomDownloadKiB", 64, 8192, true},
  });

  // Words the UI chooses from; anything else is the default.
  const auto ChoiceRules = std::to_array<ChoiceRule>({
      {"onlineView",           {"cards", "list"}                   },
      {"font",                 {"serif", "sans"}                   },
      {"nameMode",             {"username", "display", "character"}},
      {"textFilter",           {"off", "mask", "hide"}             },
      {"activationKey",        {"Enter", "F2"}                     },
      {"theme",                {"skyrim", "contrast"}              },
      {"announcementChannels", {"tab", "all", "current"}           },
      {"markDateStyle",        {"tamriel", "earth"}                },
  });

  // "#RRGGBB"; anything else is the default.
  constexpr auto ColorKeys =
    std::to_array<std::string_view>({"bubbleTextColor", "fireflyNameColor", "groundTextColor", "deathTextColor", "markDateColor", "phantomColor"});

  // How names, texts, dates and whose marks are projected. These apply to every
  // surface at once, without saving, and a change projects the session again.
  constexpr auto InstantKeys =
    std::to_array<std::string_view>({"nameMode", "streamerMode", "textFilter", "markDateStyle", "markGuildmatesOnly"});

  // The hide-my-name choices in the order of Domain::HiddenIdentity.
  constexpr auto HidingNames = std::to_array<std::string_view>({"off", "everywhere", "exceptGroundMarks"});

  // "#RRGGBB" to 0xRRGGBB; anything else is absent.
  std::optional<std::uint32_t> ParseColor(std::string_view text)
  {
    if (text.size() != 7 || text.front() != '#') return std::nullopt;
    std::uint32_t value{};
    const auto    parsed = std::from_chars(text.data() + 1, text.data() + text.size(), value, 16);
    if (parsed.ec != std::errc{} || parsed.ptr != text.data() + text.size()) return std::nullopt;
    return value;
  }

  // 0xRRGGBB to "#RRGGBB", the form ParseColor reads.
  std::string ColorText(std::uint32_t value)
  {
    return std::format("#{:06X}", value & 0xFFFFFF);
  }

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
    bool hideUi{false};
    // Where others see a server pseudonym instead of this player's names, one of
    // HidingNames. Sent when a session opens; only the host writes it, once the
    // server confirmed a switch, so the web UI never sends it.
    std::string hideIdentity{"off"};
    // The route of client.toml the player chose, by name; empty: automatic.
    std::string route{};
    // The route that answered last in automatic mode; the next start begins there.
    std::string lastRoute{};
    UiSettings  chat{};

    bool operator==(const UiSection&) const = default;
  };

  struct UiFile
  {
    int       version{1};
    UiSection ui{};
    NameBook  names{};

    bool operator==(const UiFile&) const = default;
  };

  // Fields of two settings side by side, with their names.
  template <class First, class Second, class Visit>
  void ForEachSettingPair(First& first, Second& second, Visit&& visit)
  {
    constexpr auto& keys = glz::reflect<UiSettings>::keys;
    [&]<std::size_t... I>(std::index_sequence<I...>) {
      (visit(keys[I], glz::get<I>(glz::to_tie(first)), glz::get<I>(glz::to_tie(second))), ...);
    }(std::make_index_sequence<keys.size()>{});
  }

  // Hand-edited files and UI commands fall back per field, by the rule tables.
  UiSettings Normalize(UiSettings value)
  {
    const UiSettings defaults{};
    ForEachSettingPair(value, defaults, [](std::string_view key, auto& field, const auto& fallback) {
      using Field = std::remove_cvref_t<decltype(field)>;
      if constexpr (std::is_same_v<Field, double>)
      {
        const auto rule = std::ranges::find(NumberRules, key, &NumberRule::key);
        if (!std::isfinite(field) || rule == NumberRules.end())
          field = fallback;
        else
          field = rule->integer ? std::floor(std::clamp(field, rule->min, rule->max)) : std::clamp(field, rule->min, rule->max);
      }
      else if constexpr (std::is_same_v<Field, std::string>)
      {
        if (const auto rule = std::ranges::find(ChoiceRules, key, &ChoiceRule::key); rule != ChoiceRules.end())
        {
          if (!std::ranges::contains(rule->values, field)) field = fallback;
        }
        else if (std::ranges::contains(ColorKeys, key) && !ParseColor(field))
          field = fallback;
      }
    });
    return value;
  }

  // Copies the InstantKeys settings of source into target.
  void ApplyInstant(UiSettings& target, const UiSettings& source)
  {
    ForEachSettingPair(target, source, [](std::string_view key, auto& field, const auto& value) {
      if (std::ranges::contains(InstantKeys, key)) field = value;
    });
  }

  bool InstantChanged(const UiSettings& before, const UiSettings& after)
  {
    bool changed = false;
    ForEachSettingPair(before, after, [&](std::string_view key, const auto& first, const auto& second) {
      changed = changed || (std::ranges::contains(InstantKeys, key) && first != second);
    });
    return changed;
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
  std::expected<UiFile, std::string> LoadUiFile(const std::filesystem::path& path)
  {
    std::error_code probe;
    if (!std::filesystem::exists(path, probe)) return UiFile{};

    std::ifstream input{path, std::ios::binary | std::ios::ate};
    if (!input) return std::unexpected{"Cannot open UI settings"};
    const auto length = input.tellg();
    // Pseudonyms and the ignore list share the file, hence the larger bound.
    if (length < 0 || length > (1 << 20)) return std::unexpected{"UI settings must not exceed 1 MiB"};

    std::string source(static_cast<std::size_t>(length), '\0');
    input.seekg(0);
    if (!input.read(source.data(), static_cast<std::streamsize>(source.size()))) return std::unexpected{"Cannot read UI settings"};

    UiFile file;
    source = Dreamsleeve::Utils::Toml::OneLineArrays(source);
    if (auto error = glz::read<glz::opts{.format = glz::TOML, .error_on_unknown_keys = false}>(file, source); !source.empty() && error)
      return std::unexpected{"Invalid UI TOML: " + glz::format_error(error, source)};
    if (file.version != 1) return std::unexpected{"Unsupported UI settings version"};

    file.ui.chat = Normalize(file.ui.chat);
    if (!std::ranges::contains(HidingNames, file.ui.hideIdentity)) file.ui.hideIdentity = HidingNames.front();
    file.names = Normalize(std::move(file.names));
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
