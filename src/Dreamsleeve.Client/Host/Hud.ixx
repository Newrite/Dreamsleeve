export module Dreamsleeve.Host.Hud;

import std;
export import Dreamsleeve.Host.UiSettings;

// How the HUD labels above fireflies and ground marks look, from the UI
// settings. The Scaleform layer draws what these return; the rules themselves
// have no game headers and are tested with the host.
export namespace Dreamsleeve::Host::Hud
{

  // 0xRRGGBB of a colour setting. Normalized settings always parse; a broken
  // value falls back to the setting's default.
  std::uint32_t ColorOf(std::string_view value, std::string_view fallback)
  {
    return ParseColor(value).value_or(ParseColor(fallback).value_or(0xFFFFFF));
  }

  // The text and date colours of labels built before any settings; the
  // defaults of those settings (Tests.Hud compares them).
  constexpr std::uint32_t DefaultTextColor   = 0xEEECE5;
  constexpr std::uint32_t DefaultHeaderColor = 0xA9A69B;

  // Bubble look of one label; a change rebuilds that bubble.
  struct BubbleStyle
  {
    float         fontSize{16};
    float         maxWidth{320};  // HUD units, including padding.
    float         background{0.65f};
    bool          border{true};
    std::uint32_t textColor{DefaultTextColor};
    std::uint32_t headerColor{DefaultHeaderColor};  // The smaller line on top, when a label has one.

    bool operator==(const BubbleStyle&) const = default;
  };

  // The chat bubble above a player's firefly.
  BubbleStyle PlayerBubble(const UiSettings& ui)
  {
    const UiSettings defaults;
    return {
        static_cast<float>(ui.bubbleFontSize),
        static_cast<float>(ui.bubbleMaxWidth),
        static_cast<float>(ui.bubbleBackground),
        ui.bubbleBorder,
        ColorOf(ui.bubbleTextColor, defaults.bubbleTextColor),
    };
  }

  // The label of a ground note, or of a place of death.
  BubbleStyle MarkBubble(const UiSettings& ui, bool death)
  {
    const UiSettings defaults;
    return {
        static_cast<float>(ui.groundFontSize),
        static_cast<float>(ui.groundMaxWidth),
        static_cast<float>(death ? ui.deathBackground : ui.groundBackground),
        death ? ui.deathBorder : ui.groundBorder,
        death ? ColorOf(ui.deathTextColor, defaults.deathTextColor) : ColorOf(ui.groundTextColor, defaults.groundTextColor),
        ColorOf(ui.markDateColor, defaults.markDateColor),
    };
  }

  // A player's name above a firefly or a note; a death label names its author
  // in the death colour.
  std::uint32_t NameColor(const UiSettings& ui, bool death = false)
  {
    const UiSettings defaults;
    return death ? ColorOf(ui.deathTextColor, defaults.deathTextColor) : ColorOf(ui.fireflyNameColor, defaults.fireflyNameColor);
  }

}
