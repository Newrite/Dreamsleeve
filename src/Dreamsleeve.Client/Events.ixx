module;

#include "Prelude.hpp"

export module Dreamsleeve.Events;

import Dreamsleeve.Runtime;
import Dreamsleeve.Logic;

namespace Events
{

  export struct MenuEventHandler final : RE::BSTEventSink<RE::MenuOpenCloseEvent>
  {
    static auto get_singleton() -> MenuEventHandler*
    {
      static MenuEventHandler singleton;
      return std::addressof(singleton);
    }

    static auto RegisterHandler() -> void
    {
      logger::info("Start register menu open close handler"sv);
      if (const auto ui = RE::UI::GetSingleton())
      {
        ui->AddEventSink(get_singleton());
        logger::info("Finish register menu open close handler"sv);
      }
    }

    auto ProcessEvent(const RE::MenuOpenCloseEvent* menu_event, RE::BSTEventSource<RE::MenuOpenCloseEvent>* event_source)
      -> RE::BSEventNotifyControl override
    {
      if (!menu_event || !event_source)
      {
        return RE::BSEventNotifyControl::kContinue;
      }

      return RE::BSEventNotifyControl::kContinue;
    }
  };

  export struct InputEventHandler final : RE::BSTEventSink<RE::InputEvent*>
  {

    static auto GetSingleton() -> InputEventHandler*
    {
      static InputEventHandler singleton;
      return std::addressof(singleton);
    }

    static auto RegisterHandler() -> void
    {
      const auto device_manager = RE::BSInputDeviceManager::GetSingleton();
      logger::info("Start register input event handler"sv);
      if (device_manager)
      {
        device_manager->AddEventSink(GetSingleton());
        logger::info("Finish register input event handler"sv);
      }
    }

    auto ProcessEvent(RE::InputEvent* const* event, RE::BSTEventSource<RE::InputEvent*>* event_source) -> RE::BSEventNotifyControl override
    {
      for (auto input_event = *event; input_event; input_event = input_event->next)
      {
        if (const auto button = input_event->AsButtonEvent(); button)
        {
          const auto device = input_event->GetDevice();

          auto key = button->GetIDCode();

          switch (device)
          {
            case RE::INPUT_DEVICE::kMouse:
              key += SKSE::InputMap::kMacro_MouseButtonOffset;
              break;
            case RE::INPUT_DEVICE::kGamepad:
              key = SKSE::InputMap::GamepadMaskToKeycode(key);
              break;
            default:
              break;
          }

          // some call there
        }
      }

      return RE::BSEventNotifyControl::kContinue;
    }
  };

  export struct DeathEventHandler final : RE::BSTEventSink<RE::TESDeathEvent>
  {
    static auto GetSingleton() noexcept -> DeathEventHandler*
    {
      static DeathEventHandler singleton;
      return std::addressof(singleton);
    }

    static auto RegisterHandler() -> void
    {
      const auto ScriptEventSourceHandler = RE::ScriptEventSourceHolder::GetSingleton();
      logger::info("Start register death handler"sv);

      if (ScriptEventSourceHandler)
      {
        const auto ScriptEventSource = ScriptEventSourceHandler->GetEventSource<RE::TESDeathEvent>();

        if (ScriptEventSource)
        {
          ScriptEventSource->AddEventSink(GetSingleton());
          logger::info("Finish register death handler"sv);
        }
      }
    }

    auto ProcessEvent(const RE::TESDeathEvent* event, RE::BSTEventSource<RE::TESDeathEvent>*) -> RE::BSEventNotifyControl
    {
      if (!event)
      {
        return RE::BSEventNotifyControl::kContinue;
      }

      // some call here
      return RE::BSEventNotifyControl::kContinue;
    }
  };

  export void RegisterEvents()
  {
    MenuEventHandler::RegisterHandler();
    InputEventHandler::RegisterHandler();
    DeathEventHandler::RegisterHandler();
  }

}
