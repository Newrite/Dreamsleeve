#include <doctest/doctest.h>

import std;
import Dreamsleeve.Client.Utils;

TEST_SUITE_BEGIN("Client.Utils");

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
