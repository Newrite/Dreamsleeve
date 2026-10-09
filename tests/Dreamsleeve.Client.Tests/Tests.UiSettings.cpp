#include <doctest/doctest.h>
#include <glaze/glaze.hpp>

import std;
import Dreamsleeve.Host.UiSettings;

#include "Repository.h"

using namespace Dreamsleeve::Host;

namespace
{

  template <class Field>
  std::string TypeOf(std::string_view key)
  {
    if constexpr (std::is_same_v<Field, bool>)
      return "boolean";
    else if constexpr (std::is_same_v<Field, double>)
      return "number";
    else
    {
      const auto rule = std::ranges::find(ChoiceRules, key, &ChoiceRule::key);
      if (rule == ChoiceRules.end()) return "string";
      std::string alternatives;
      for (const auto value : rule->values)
        alternatives += (alternatives.empty() ? "\"" : " | \"") + std::string{value} + "\"";
      return alternatives;
    }
  }

  template <class Field>
  std::string LiteralOf(const Field& value)
  {
    if constexpr (std::is_same_v<Field, bool>)
      return value ? "true" : "false";
    else if constexpr (std::is_same_v<Field, double>)
      return std::format("{}", value);
    else
      return glz::write_json(value).value_or("\"\"");
  }

  // The web UI's view of UiSettings.
  std::string SettingsModule()
  {
    const UiSettings defaults{};
    std::string      fields;
    std::string      values;
    ForEachSettingPair(defaults, defaults, [&](std::string_view key, const auto& value, const auto&) {
      using Field = std::remove_cvref_t<decltype(value)>;
      fields += std::format("  {}: {};\n", key, TypeOf<Field>(key));
      values += std::format("  {}: {},\n", key, LiteralOf(value));
    });
    std::string limits;
    for (const auto& rule : NumberRules)
      limits += std::format("  {}: {{ min: {}, max: {} }},\n", rule.key, rule.min, rule.max);
    std::string instant;
    for (const auto key : InstantKeys)
      instant += std::format("  \"{}\",\n", key);

    return "// Generated from src/Dreamsleeve.Client/Host/UiSettings.ixx by the native test\n"
           "// \"The web UI settings module is generated from UiSettings\"; do not edit.\n"
           "// Regenerate: set DREAMSLEEVE_WRITE_GENERATED=1 and run Dreamsleeve.Client.Tests.\n"
           "export interface Settings {\n" +
           fields + "}\n\nexport const defaults: Settings = {\n" + values +
           "};\n\n// Accepted ranges of the numeric settings; the host clamps to them.\nexport const limits = {\n" + limits +
           "} as const;\n\n// Sent with displaySettings: the host applies and saves them at once, on every surface.\nexport const instantKeys = [\n" + instant +
           "] as const;\n";
  }

}

TEST_SUITE_BEGIN("Host.UiSettings");

TEST_CASE("Every UI setting has a rule of its own kind and every rule names a setting")
{
  const UiSettings                             defaults{};
  std::set<std::string_view>                   keys;
  std::set<std::string_view>                   numbers;
  std::map<std::string_view, std::string_view> words;
  ForEachSettingPair(defaults, defaults, [&](std::string_view key, const auto& value, const auto&) {
    using Field = std::remove_cvref_t<decltype(value)>;
    keys.insert(key);
    if constexpr (std::is_same_v<Field, double>)
    {
      numbers.insert(key);
      const auto rule = std::ranges::find(NumberRules, key, &NumberRule::key);
      REQUIRE_MESSAGE(rule != NumberRules.end(), std::string{key});
      CHECK_MESSAGE((rule->min <= value && value <= rule->max), std::string{key});
    }
    else if constexpr (std::is_same_v<Field, std::string>)
      words.emplace(key, value);
  });
  for (const auto& rule : NumberRules)
    CHECK_MESSAGE(numbers.contains(rule.key), std::string{rule.key});
  for (const auto& rule : ChoiceRules)
  {
    REQUIRE_MESSAGE(words.contains(rule.key), std::string{rule.key});
    CHECK_MESSAGE(std::ranges::contains(rule.values, words.at(rule.key)), std::string{rule.key});
  }
  for (const auto key : ColorKeys)
    CHECK_MESSAGE((words.contains(key) && ParseColor(words.at(key))), std::string{key});
  for (const auto key : InstantKeys)
    CHECK_MESSAGE(keys.contains(key), std::string{key});
}

TEST_CASE("Normalize clamps numbers, floors counts and falls back per field")
{
  UiSettings value;
  value.delay           = 500;
  value.scale           = std::numeric_limits<double>::quiet_NaN();
  value.maxVisibleNotes = 7.9;
  value.theme           = "neon";
  value.markDateColor   = "red";
  const auto normalized = Normalize(value);
  CHECK(normalized.delay == 120);
  CHECK(normalized.scale == UiSettings{}.scale);
  CHECK(normalized.maxVisibleNotes == 7);
  CHECK(normalized.theme == UiSettings{}.theme);
  CHECK(normalized.markDateColor == UiSettings{}.markDateColor);
}

TEST_CASE("Instant settings are copied and compared alone")
{
  UiSettings saved;
  UiSettings edited;
  edited.fontSize   = 20;
  edited.textFilter = "mask";
  edited.showPhantoms = false;
  edited.publishPhantoms = false;
  CHECK(InstantChanged(saved, edited));
  ApplyInstant(saved, edited);
  CHECK(saved.textFilter == "mask");
  CHECK_FALSE(saved.showPhantoms);
  CHECK_FALSE(saved.publishPhantoms);
  CHECK(saved.fontSize == UiSettings{}.fontSize);
  CHECK_FALSE(InstantChanged(saved, edited));
}

TEST_CASE("The web UI settings module is generated from UiSettings")
{
  const auto path     = RepositoryRoot() / "src" / "Dreamsleeve.Client.UI" / "src" / "bridge" / "settings.generated.ts";
  const auto expected = SettingsModule();
#pragma warning(suppress : 4996)  // Read once; no other thread touches the environment.
  if (std::getenv("DREAMSLEEVE_WRITE_GENERATED"))
  {
    std::ofstream output{path, std::ios::binary | std::ios::trunc};
    output << expected;
  }
  CHECK_MESSAGE(ReadText(path) == expected, "settings.generated.ts is stale; set DREAMSLEEVE_WRITE_GENERATED=1 and run the tests");
}

TEST_CASE("The bundled ui.example.toml names every setting with its default value")
{
  const auto path = RepositoryRoot() / "src" / "Dreamsleeve.Client.UI" / "ui.example.toml";
  const auto file = LoadUiFile(path);
  REQUIRE(file);
  CHECK(file->ui == UiSection{});
  const auto text = ReadText(path);
  for (const auto key : {"hideUi", "hideIdentity", "route", "lastRoute"})
    CHECK_MESSAGE(text.contains(std::format("\n{} = ", key)), key);
  const UiSettings defaults{};
  ForEachSettingPair(defaults, defaults, [&](std::string_view key, const auto&, const auto&) {
    CHECK_MESSAGE(text.contains(std::format("\n{} = ", key)), std::string{key});
  });
}

TEST_CASE("Filesystem probe failure is distinct from a legitimately missing UI file")
{
  const std::filesystem::path path{"ui-probe.toml"};
  unsigned                    probes{};
  const auto                  unavailable = std::make_error_code(std::errc::permission_denied);
  const auto rejected = Testing::LoadUiFileWithProbe(path, [&](const std::filesystem::path& value, std::error_code& error) {
    CHECK(value == path);
    ++probes;
    error = unavailable;
    return false;
  });
  REQUIRE_FALSE(rejected);
  CHECK(rejected.error() == "Cannot inspect UI settings: " + unavailable.message());

  const auto missing = Testing::LoadUiFileWithProbe(path, [&](const std::filesystem::path& value, std::error_code& error) {
    CHECK(value == path);
    ++probes;
    error.clear();
    return false;
  });
  REQUIRE(missing);
  CHECK(*missing == UiFile{});
  CHECK(probes == 2);
}

TEST_CASE("UI saves preserve foreign temporary paths and clean only their own rejected file")
{
  struct Fixture
  {
    std::filesystem::path root =
      std::filesystem::temp_directory_path() /
      (L"dreamsleeve-ui-owned-\U0001F984-" + std::to_wstring(std::chrono::steady_clock::now().time_since_epoch().count()));

    ~Fixture()
    {
      std::error_code error;
      std::filesystem::remove_all(root, error);
    }
  } fixture;

  std::filesystem::create_directories(fixture.root);
  const auto path       = fixture.root / L"\u4E2D.toml";
  auto       temporary  = path;
  temporary            += ".tmp";
  {
    std::ofstream destination{path, std::ios::binary};
    destination << "preserve destination";
    std::ofstream foreign{temporary, std::ios::binary};
    foreign << "preserve foreign temporary";
  }
  CHECK_FALSE(SaveUiFile(path, {}));
  CHECK(ReadText(path) == "preserve destination");
  CHECK(ReadText(temporary) == "preserve foreign temporary");
  std::filesystem::remove(temporary);
  std::filesystem::create_directory(temporary);
  {
    std::ofstream foreign{temporary / "foreign"};
    foreign << "keep";
  }
  CHECK_FALSE(SaveUiFile(path, {}));
  CHECK(ReadText(path) == "preserve destination");
  CHECK(ReadText(temporary / "foreign") == "keep");

  // This temporary file is created by SaveUiFile, but a directory blocks rename.
  const auto blocked = fixture.root / "blocked.toml";
  std::filesystem::create_directory(blocked);
  {
    std::ofstream original{blocked / "foreign"};
    original << "keep";
  }
  const auto rejected = SaveUiFile(blocked, {});
  CHECK_FALSE(rejected);
  if (!rejected) CHECK_FALSE(rejected.error().empty());
  auto partial  = blocked;
  partial      += ".tmp";
  CHECK_FALSE(std::filesystem::exists(partial));
  CHECK(ReadText(blocked / "foreign") == "keep");

  std::filesystem::remove(temporary / "foreign");
  std::filesystem::remove(temporary);
  UiFile edited;
  edited.ui.hideUi = true;
  REQUIRE(SaveUiFile(path, edited));
  const auto loaded = LoadUiFile(path);
  REQUIRE(loaded);
  CHECK(*loaded == edited);
  CHECK_FALSE(std::filesystem::exists(temporary));
}

TEST_SUITE_END();
