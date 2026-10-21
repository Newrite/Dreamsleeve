module;
#include "Prelude.hpp"
export module Dreamsleeve.Game.Phantom.Log;
import std;
import Dreamsleeve.Client.Phantom.Types;
import Dreamsleeve.Client.Diagnostics.PhantomTrace;

// What the game-thread phantom code shares besides its owners: one rate for
// failure warnings and the clock in the units poses carry.
export namespace Dreamsleeve::Game::PhantomLog
{

  using Clock = std::chrono::steady_clock;

  // Failures repeat every frame while they last: one warning in this time.
  constexpr auto WarningInterval = std::chrono::seconds(5);

  inline std::uint64_t Micros(Clock::time_point now)
  {
    return static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::microseconds>(now.time_since_epoch()).count());
  }

  // Game thread only, like every caller.
  void Warn(const Dreamsleeve::Client::Phantom::Error& error, Clock::time_point now)
  {
    static Clock::time_point next{};
    if (now < next) return;
    next = now + WarningInterval;
    Dreamsleeve::Client::Diagnostics::Trace::Event("failure", [&] { return std::format("\"code\":{}", static_cast<int>(error.reason)); });
    logger::warn("Phantom: {} ({})", error.field, static_cast<int>(error.reason));
  }

}
