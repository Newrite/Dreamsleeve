#include <doctest/doctest.h>

import std;
import Dreamsleeve.Client.Utils;

TEST_SUITE_BEGIN("Client.Utils");

TEST_CASE("TOML arrays are joined onto one line for glaze; strings, tables and positions stay")
{
  using Dreamsleeve::Utils::Toml::OneLineArrays;

  CHECK(
    OneLineArrays("names = [\n  \"a\", # first\r\n  \"b\",\n]\nversion = 1\n") == "names = [   \"a\",            \"b\"  ]\nversion = 1\n");
  CHECK(OneLineArrays("x = [1, 2 , ]") == "x = [1, 2   ]");
  // Brackets, hashes and newlines inside strings are text; table headers are left alone.
  CHECK(
    OneLineArrays("[ui]\nkey = \"[#\"\nlist = ['x]', \"y\\\"]\"\n]\n# end\n") == "[ui]\nkey = \"[#\"\nlist = ['x]', \"y\\\"]\" ]\n# end\n");
  CHECK(OneLineArrays("[[names.aliases]]\nname = \"Бард\"\n") == "[[names.aliases]]\nname = \"Бард\"\n");
  CHECK(OneLineArrays("a = [[1,\n2],\n[3]]\n") == "a = [[1, 2], [3]]\n");
  // An unclosed array or string stays broken for glaze to report.
  CHECK(OneLineArrays("names = [\n").size() == 10);
  CHECK(OneLineArrays("key = \"open\n[\n") == "key = \"open\n[ ");
}

TEST_CASE("UTF-8 is validated once and then measured and cut on code point boundaries")
{
  using namespace Dreamsleeve::Utils::Text;

  CHECK(ValidUtf8(""));
  CHECK(ValidUtf8("Игрок пал в бою 😀"));
  for (std::string_view invalid : {"\xC0\xAF", "\xED\xA0\x80", "\xE2\x80", "\xF4\x90\x80\x80", "\xFF"})
    CHECK_FALSE(ValidUtf8(invalid));

  CHECK_FALSE(HasControl("Мод-смерти"));
  for (std::string_view control : {"two\nlines", "tab\there", "bell\x07", "del\x7F", "c1\xC2\x85"})
    CHECK(HasControl(control));

  CHECK(CodePoints("Ж😀a") == 3);
  CHECK(Prefix("Ж😀a", 2) == "Ж😀");
  CHECK(Prefix("abc", 10) == "abc");
  CHECK(ClipBytes("ЖЖ", 3) == "Ж");
  CHECK(ClipBytes("ЖЖ", 4) == "ЖЖ");
}

TEST_CASE("Text is shortened on code points with an ellipsis and searched with ASCII case folded")
{
  using namespace Dreamsleeve::Utils::Text;

  CHECK(Ellipsize("Привет, мир", 7) == "Привет,\xE2\x80\xA6");
  CHECK(Ellipsize("Привет  мир", 8) == "Привет\xE2\x80\xA6");
  CHECK(Ellipsize("Ж😀a", 2) == "Ж😀\xE2\x80\xA6");

  CHECK(AsciiLower('Q') == 'q');
  CHECK(AsciiLower('1') == '1');
  CHECK(ContainsAsciiInsensitive("Meshes\\PickaxeMiningMarker.nif", "marker"));
  CHECK(ContainsAsciiInsensitive("This Should Not Be Visible", "should not be visible"));
  CHECK_FALSE(ContainsAsciiInsensitive("Кирка", "marker"));
  // Only ASCII folds: Cyrillic case stays apart.
  CHECK_FALSE(ContainsAsciiInsensitive("ЖУК", "жук"));
}

TEST_CASE("Text of unknown encoding is repaired into valid UTF-8, valid text kept as it is")
{
  using namespace Dreamsleeve::Utils::Text;

  CHECK(Repair("Игрок пал в бою 😀") == "Игрок пал в бою 😀");
  CHECK(Repair("") == "");
  // "Волк" in Windows-1251: every byte is invalid UTF-8 and becomes U+FFFD.
  CHECK(Repair("\xC2\xEE\xEB\xEA") == "\xEF\xBF\xBD\xEF\xBF\xBD\xEF\xBF\xBD\xEF\xBF\xBD");
  const auto truncated = Repair("Ж\xE2\x80");
  CHECK(ValidUtf8(truncated));
  CHECK(truncated.starts_with("Ж"));
  for (std::string_view invalid : {"\xC0\xAF", "\xED\xA0\x80", "\xF4\x90\x80\x80", "\xFF"})
    CHECK(ValidUtf8(Repair(invalid)));
}

TEST_CASE("Text in a code page becomes UTF-8, bytes that code page does not define are refused")
{
  using namespace Dreamsleeve::Utils::Text;

  // "Волк" in Windows-1251.
  CHECK(FromCodePage("\xC2\xEE\xEB\xEA", 1251).value_or("<none>") == "Волк");
  CHECK(FromCodePage("Dragonborn", 1252).value_or("<none>") == "Dragonborn");
  CHECK(FromCodePage("", 1251).value_or("<none>") == "");
  // A Shift-JIS lead byte without its trail byte; malformed UTF-8 read as UTF-8.
  CHECK_FALSE(FromCodePage("\x82", 932).has_value());
  CHECK_FALSE(FromCodePage("\xFF", 65001).has_value());
}

TEST_CASE("Backoff doubles the wait after each attempt up to the maximum and starts over after a reset")
{
  using namespace std::chrono_literals;
  using Dreamsleeve::Utils::Timing::Backoff;

  Backoff    backoff{5s, 20s};
  const auto start = Backoff::Clock::time_point{} + 1h;
  CHECK(backoff.Due(start));
  CHECK_FALSE(backoff.Due(start + 4s));
  CHECK(backoff.Due(start + 5s));
  CHECK_FALSE(backoff.Due(start + 14s));
  CHECK(backoff.Due(start + 15s));
  CHECK_FALSE(backoff.Due(start + 34s));
  CHECK(backoff.Due(start + 35s));
  CHECK(backoff.Due(start + 55s));  // The wait stays at the maximum.
  backoff.Reset();
  CHECK(backoff.Due(start + 56s));
  CHECK_FALSE(backoff.Due(start + 60s));
  CHECK(backoff.Due(start + 61s));
}

TEST_SUITE_END();
