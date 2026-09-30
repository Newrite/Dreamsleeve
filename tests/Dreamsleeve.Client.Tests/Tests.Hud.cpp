#include <doctest/doctest.h>

import std;
import Dreamsleeve.Host.Hud;

using namespace Dreamsleeve::Host;

TEST_SUITE_BEGIN("Host.Hud");

TEST_CASE("Labels built before any settings use the default colours of those settings")
{
  const UiSettings defaults;
  CHECK(ParseColor(defaults.bubbleTextColor) == Hud::DefaultTextColor);
  CHECK(ParseColor(defaults.fireflyNameColor) == Hud::DefaultTextColor);
  CHECK(ParseColor(defaults.groundTextColor) == Hud::DefaultTextColor);
  CHECK(ParseColor(defaults.markDateColor) == Hud::DefaultHeaderColor);
  CHECK(
    Hud::PlayerBubble(defaults) == Hud::BubbleStyle{
                                       static_cast<float>(defaults.bubbleFontSize),
                                       static_cast<float>(defaults.bubbleMaxWidth),
                                       static_cast<float>(defaults.bubbleBackground),
                                       defaults.bubbleBorder,
                                   });
}

TEST_CASE("A player bubble, a note and a place of death take their own settings")
{
  UiSettings ui;
  ui.bubbleFontSize   = 20;
  ui.bubbleTextColor  = "#112233";
  ui.groundFontSize   = 14;
  ui.groundMaxWidth   = 280;
  ui.groundBackground = 0.4;
  ui.deathBackground  = 0.9;
  ui.groundBorder     = true;
  ui.deathBorder      = false;
  ui.groundTextColor  = "#445566";
  ui.deathTextColor   = "#AA0000";
  ui.markDateColor    = "#010203";
  ui.fireflyNameColor = "#778899";

  const auto player = Hud::PlayerBubble(ui);
  CHECK(player.fontSize == 20);
  CHECK(player.textColor == 0x112233);
  CHECK(player.headerColor == Hud::DefaultHeaderColor);

  const auto note  = Hud::MarkBubble(ui, false);
  const auto death = Hud::MarkBubble(ui, true);
  CHECK(note.fontSize == 14);
  CHECK(death.maxWidth == 280);
  CHECK(note.background == doctest::Approx(0.4f));
  CHECK(death.background == doctest::Approx(0.9f));
  CHECK(note.border);
  CHECK_FALSE(death.border);
  CHECK(note.textColor == 0x445566);
  CHECK(death.textColor == 0xAA0000);
  CHECK(death.headerColor == 0x010203);

  // A death label names its author in the death colour.
  CHECK(Hud::NameColor(ui) == 0x778899);
  CHECK(Hud::NameColor(ui, true) == 0xAA0000);
}

TEST_CASE("A colour that does not parse falls back to its setting's default")
{
  const UiSettings defaults;
  UiSettings       broken;
  broken.deathTextColor = "red";
  CHECK(Hud::NameColor(broken, true) == ParseColor(defaults.deathTextColor));
  CHECK(Hud::ColorOf("#GGGGGG", "#010203") == 0x010203);
}

TEST_SUITE_END();
