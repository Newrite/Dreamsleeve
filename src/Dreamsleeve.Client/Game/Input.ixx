module;

#include "Prelude.hpp"

export module Dreamsleeve.Game.Input;

import std;
import Dreamsleeve.Host.InputCapture;

// Keyboard capture for the chat. Hooks.ixx redirects the call through which
// BSInputDeviceManager::PollInputDevices hands the frame's event chain to the
// dispatcher that walks every input sink, so Dispatch below sees the events
// before any sink: the game's own controls and every SKSE mod alike. While the
// chat owns the keyboard the chain is relinked without the withheld events for
// the duration of the call and restored afterwards; the queue and the events
// stay the game's. No addresses here: docs/InputCaptureHookRu.md has them.
namespace Input
{

  namespace Capture = Dreamsleeve::Host::InputCapture;

  export using Source   = RE::BSTEventSource<RE::InputEvent*>;
  export using Original = void (*)(Source* source, RE::InputEvent** events);

  struct State
  {
    std::mutex      mutex;  // Everything runs on the main thread; the lock is insurance, not a contract.
    Capture::Filter filter;
  };

  State& Get()
  {
    static State state;
    return state;
  }

  export void BeginCapture()
  {
    auto&           state = Get();
    std::lock_guard lock{state.mutex};
    state.filter.Begin();
  }

  export void EndCapture()
  {
    auto&           state = Get();
    std::lock_guard lock{state.mutex};
    state.filter.End();
  }

  Capture::Device DeviceOf(const RE::InputEvent& event)
  {
    switch (event.GetDevice())
    {
      case RE::INPUT_DEVICE::kKeyboard:
        return Capture::Device::Keyboard;
      case RE::INPUT_DEVICE::kMouse:
        return Capture::Device::Mouse;
      case RE::INPUT_DEVICE::kGamepad:
        return Capture::Device::Gamepad;
      default:
        return Capture::Device::Other;
    }
  }

  // Anything but a button passes: characters, mouse motion, thumbsticks, VR wands.
  bool Admit(Capture::Filter& filter, RE::InputEvent& event)
  {
    const auto* button = event.AsButtonEvent();
    if (!button) return true;
    return filter.Admit({DeviceOf(event), button->GetIDCode(), button->IsPressed()});
  }

  struct Link
  {
    RE::InputEvent* event{};
    RE::InputEvent* next{};
  };

  // Body of the dispatch hook. `original` is the displaced call target.
  export void Dispatch(Source* source, RE::InputEvent** events, Original original)
  {
    auto&            state = Get();
    std::unique_lock lock{state.mutex};
    if (!events || !*events || !state.filter.Capturing())
    {
      lock.unlock();
      original(source, events);
      return;
    }

    // Links redirected past withheld events; restored once the sinks return.
    // The game passes the head through a stack slot of its own, so this does
    // the same and never writes the caller's slot or frees anything.
    std::array<Link, 64> links{};
    std::size_t          count = 0;
    RE::InputEvent*      head  = nullptr;
    RE::InputEvent*      last  = nullptr;
    bool                 full  = false;
    for (auto* event = *events; event; event = event->next)
    {
      if (!Admit(state.filter, *event)) continue;
      if (last && last->next != event)
      {
        if (count == links.size())
        {
          full = true;
          break;
        }
        links[count++] = {last, last->next};
        last->next     = event;
      }
      if (!last) head = event;
      last = event;
    }

    if (!full && last && last->next)
    {
      if (count < links.size())
      {
        links[count++] = {last, last->next};
        last->next     = nullptr;
      }
      else
        full = true;
    }

    lock.unlock();
    if (full)
    {
      static bool warned = false;
      if (!warned) logger::warn("Input chain needed more than {} relinks in one frame; its tail was delivered unfiltered", links.size());
      warned = true;
    }

    RE::InputEvent* chain = head;
    original(source, &chain);

    for (auto i = count; i-- > 0;)
      links[i].event->next = links[i].next;
  }

}
