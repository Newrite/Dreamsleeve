#include <doctest/doctest.h>
import std;
import Dreamsleeve.Host.InputCapture;

namespace
{

  using namespace Dreamsleeve::Host::InputCapture;

  constexpr std::uint32_t KeyW     = 0x11;
  constexpr std::uint32_t KeyE     = 0x12;
  constexpr std::uint32_t KeyEnter = 0x1C;

  Button Key(std::uint32_t code, bool pressed)
  {
    return {Device::Keyboard, code, pressed};
  }

}

TEST_CASE("Input filter is transparent while the chat is inactive")
{
  Filter filter;
  CHECK_FALSE(filter.Capturing());
  CHECK(filter.Admit(Key(KeyW, true)));
  CHECK(filter.Admit(Key(KeyW, true)));
  CHECK(filter.Admit(Key(KeyW, false)));
  CHECK(filter.Admit({Device::Mouse, 0, true}));
  CHECK(filter.Admit(Key(300, true)));
}

TEST_CASE("Input filter withholds keys pressed and released during capture")
{
  Filter filter;
  filter.Begin();
  CHECK(filter.Capturing());
  CHECK_FALSE(filter.Admit(Key(KeyE, true)));
  CHECK_FALSE(filter.Admit(Key(KeyE, true)));
  CHECK_FALSE(filter.Admit(Key(KeyE, false)));
  CHECK_FALSE(filter.Admit(Key(300, true)));
}

TEST_CASE("Input filter delivers only the release of a key held before capture")
{
  Filter filter;
  REQUIRE(filter.Admit(Key(KeyW, true)));

  filter.Begin();
  CHECK_FALSE(filter.Admit(Key(KeyW, true)));  // held repeat
  CHECK(filter.Admit(Key(KeyW, false)));       // the game must see the release
  CHECK_FALSE(filter.Admit(Key(KeyW, true)));  // pressed again while typing
  CHECK_FALSE(filter.Admit(Key(KeyW, false)));
}

TEST_CASE("Input filter lets the opening key release without repeating it")
{
  Filter filter;
  REQUIRE(filter.Admit(Key(KeyEnter, true)));  // the press that opened the chat
  filter.Begin();
  CHECK_FALSE(filter.Admit(Key(KeyEnter, true)));
  CHECK_FALSE(filter.Admit(Key(KeyEnter, true)));
  CHECK(filter.Admit(Key(KeyEnter, false)));
  CHECK_FALSE(filter.Admit(Key(KeyEnter, true)));
}

TEST_CASE("Input filter never touches other devices")
{
  Filter filter;
  filter.Begin();
  CHECK(filter.Admit({Device::Mouse, 0, true}));
  CHECK(filter.Admit({Device::Mouse, 0, false}));
  CHECK(filter.Admit({Device::Gamepad, 0x1000, true}));
  CHECK(filter.Admit({Device::Other, 7, true}));
}

TEST_CASE("Input filter ends capture without leaving keys stuck")
{
  Filter filter;
  REQUIRE(filter.Admit(Key(KeyW, true)));
  filter.Begin();
  CHECK_FALSE(filter.Admit(Key(KeyE, true)));

  filter.End();
  CHECK_FALSE(filter.Capturing());
  CHECK(filter.Admit(Key(KeyW, false)));  // still held through the whole chat
  CHECK(filter.Admit(Key(KeyE, true)));   // held repeat after closing: the game may act on it
  CHECK(filter.Admit(Key(KeyE, false)));

  // A key released while typing is not resurrected by the next capture.
  filter.Begin();
  CHECK_FALSE(filter.Admit(Key(KeyW, false)));
  CHECK_FALSE(filter.Admit(Key(KeyE, false)));
}

TEST_CASE("Input filter capture is idempotent")
{
  Filter filter;
  REQUIRE(filter.Admit(Key(KeyW, true)));
  filter.Begin();
  CHECK_FALSE(filter.Admit(Key(KeyE, true)));

  filter.Begin();
  CHECK(filter.Capturing());
  CHECK_FALSE(filter.Admit(Key(KeyE, false)));  // the second Begin did not admit it
  CHECK(filter.Admit(Key(KeyW, false)));
  filter.End();
  filter.End();
  CHECK_FALSE(filter.Capturing());
}
