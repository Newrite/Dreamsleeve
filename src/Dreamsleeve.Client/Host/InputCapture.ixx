module;

export module Dreamsleeve.Host.InputCapture;

import std;

// Which button events reach the game while the chat owns the keyboard. Game
// independent: the input hook maps engine events onto Button and asks Admit.
export namespace Dreamsleeve::Host::InputCapture
{

  enum class Device : std::uint8_t
  {
    Keyboard,
    Mouse,
    Gamepad,
    Other
  };

  struct Button
  {
    Device        device{};
    std::uint32_t code{};     // DirectInput scan code on the keyboard
    bool          pressed{};  // value > 0: press or held repeat; false: release
  };

  class Filter
  {
public:

    static constexpr std::size_t Keys = 256;

    // Keys the game already saw pressed keep their release; nothing else passes.
    void Begin() noexcept
    {
      if (capturing) return;
      capturing = true;
      releasing = down;
    }

    void End() noexcept
    {
      capturing = false;
      releasing.reset();
    }

    [[nodiscard]] bool Capturing() const noexcept
    {
      return capturing;
    }

    // True when the game may see the event. Only keyboard buttons are ever
    // withheld: the mouse feeds PrismaUI through this same path, and a gamepad
    // cannot type. Tracks the pressed state exactly as the game saw it.
    [[nodiscard]] bool Admit(Button button) noexcept
    {
      if (button.device != Device::Keyboard) return true;
      if (button.code >= Keys) return !capturing;
      if (!capturing)
      {
        down[button.code] = button.pressed;
        return true;
      }
      if (!releasing[button.code]) return false;
      if (button.pressed) return false;  // Held repeats of the opening key stay silent.
      releasing[button.code] = false;
      down[button.code]      = false;
      return true;
    }

private:

    bool              capturing{};
    std::bitset<Keys> down;       // pressed as far as the game knows
    std::bitset<Keys> releasing;  // pressed before capture; the release still counts
  };

}
