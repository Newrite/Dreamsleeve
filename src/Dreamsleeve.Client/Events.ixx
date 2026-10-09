module;

#include "Prelude.hpp"

export module Dreamsleeve.Events;

import std;
import Dreamsleeve.Runtime;
import Dreamsleeve.Game.PhantomCapture;

// Game event sinks. They only post notices; ScriptEventSourceHolder events can
// arrive from AI or script threads, so no game state is touched here.
namespace Events
{

  using Key = RE::BSKeyboardDevice::Keys;

  // Mirrored by the main thread from ui.toml; read on the input thread.
  std::atomic<std::uint32_t> activationKey{Key::kEnter};

  export void SetActivationKey(std::string_view name)
  {
    activationKey.store(name == "F2" ? Key::kF2 : Key::kEnter, std::memory_order_relaxed);
  }

  bool IsPlayer(const RE::TESObjectREFR* ref)
  {
    return ref && ref == RE::PlayerCharacter::GetSingleton();
  }

  export struct MenuEventHandler final : RE::BSTEventSink<RE::MenuOpenCloseEvent>
  {
    static auto GetSingleton() -> MenuEventHandler*
    {
      static MenuEventHandler singleton;
      return std::addressof(singleton);
    }

    static auto RegisterHandler() -> void
    {
      if (const auto ui = RE::UI::GetSingleton())
      {
        ui->AddEventSink(GetSingleton());
        logger::info("Menu open/close sink registered");
      }
    }

    auto ProcessEvent(const RE::MenuOpenCloseEvent* event, RE::BSTEventSource<RE::MenuOpenCloseEvent>*) -> RE::BSEventNotifyControl override
    {
      if (event) Runtime::Post({Runtime::NoticeKind::MenuChanged});
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
      if (const auto manager = RE::BSInputDeviceManager::GetSingleton())
      {
        manager->AddEventSink(GetSingleton());
        logger::info("Input sink registered");
      }
    }

    auto ProcessEvent(RE::InputEvent* const* events, RE::BSTEventSource<RE::InputEvent*>*) -> RE::BSEventNotifyControl override
    {
      if (!events) return RE::BSEventNotifyControl::kContinue;
      const auto wanted = activationKey.load(std::memory_order_relaxed);
      for (auto event = *events; event; event = event->next)
      {
        const auto button = event->AsButtonEvent();
        if (!button || event->GetDevice() != RE::INPUT_DEVICE::kKeyboard || !button->IsDown()) continue;
        const auto key     = button->GetIDCode();
        const bool matches = key == wanted || (wanted == Key::kEnter && key == Key::kKP_Enter);
        if (matches) Runtime::Post({Runtime::NoticeKind::ActivationKey});
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
      const auto holder = RE::ScriptEventSourceHolder::GetSingleton();
      if (!holder) return;
      if (const auto source = holder->GetEventSource<RE::TESDeathEvent>())
      {
        source->AddEventSink(GetSingleton());
        logger::info("Death sink registered");
      }
    }

    // May fire twice per death (dying, then dead) and from non-main threads:
    // only handles are taken here, the frame resolves the killer.
    auto ProcessEvent(const RE::TESDeathEvent* event, RE::BSTEventSource<RE::TESDeathEvent>*) -> RE::BSEventNotifyControl override
    {
      if (event && IsPlayer(event->actorDying.get()))
      {
        Runtime::Notice notice{Runtime::NoticeKind::PlayerDeath, event->dead};
        if (auto* killer = event->actorKiller.get()) notice.handle = killer->GetHandle();
        Runtime::Post(notice);
      }
      return RE::BSEventNotifyControl::kContinue;
    }
  };

  export struct ActivateEventHandler final : RE::BSTEventSink<RE::TESActivateEvent>
  {
    static auto GetSingleton() noexcept -> ActivateEventHandler*
    {
      static ActivateEventHandler singleton;
      return std::addressof(singleton);
    }

    static auto RegisterHandler() -> void
    {
      const auto holder = RE::ScriptEventSourceHolder::GetSingleton();
      if (!holder) return;
      if (const auto source = holder->GetEventSource<RE::TESActivateEvent>())
      {
        source->AddEventSink(GetSingleton());
        logger::info("Activate sink registered");
      }
    }

    auto ProcessEvent(const RE::TESActivateEvent* event, RE::BSTEventSource<RE::TESActivateEvent>*) -> RE::BSEventNotifyControl override
    {
      if (event && event->objectActivated && IsPlayer(event->actionRef.get()))
        Runtime::Post({Runtime::NoticeKind::PlayerActivated, false, event->objectActivated->GetFormID()});
      return RE::BSEventNotifyControl::kContinue;
    }
  };

  struct PhantomModelEvents final : RE::BSTEventSink<SKSE::NiNodeUpdateEvent>, RE::BSTEventSink<RE::TESEquipEvent>
  {
    RE::BSEventNotifyControl ProcessEvent(const SKSE::NiNodeUpdateEvent* event, RE::BSTEventSource<SKSE::NiNodeUpdateEvent>*) override
    {
      if (event && IsPlayer(event->reference)) Dreamsleeve::Game::PhantomCapture::RequestAudit();
      return RE::BSEventNotifyControl::kContinue;
    }

    RE::BSEventNotifyControl ProcessEvent(const RE::TESEquipEvent* event, RE::BSTEventSource<RE::TESEquipEvent>*) override
    {
      // Equip is an intent notification, not completed geometry. Only request
      // the bounded audit; it will wait for stable composition on the main thread.
      if (event && IsPlayer(event->actor.get())) Dreamsleeve::Game::PhantomCapture::RequestAudit();
      return RE::BSEventNotifyControl::kContinue;
    }

    static void Register()
    {
      static PhantomModelEvents sink;
      if (auto* source = SKSE::GetNiNodeUpdateEventSource()) source->AddEventSink(&sink);
      if (auto* source = RE::ScriptEventSourceHolder::GetSingleton()) source->AddEventSink<RE::TESEquipEvent>(&sink);
    }
  };

  // kDataLoaded, once: every source exists by then.
  export void RegisterEvents()
  {
    static bool registered = false;
    if (registered) return;
    registered = true;
    MenuEventHandler::RegisterHandler();
    InputEventHandler::RegisterHandler();
    DeathEventHandler::RegisterHandler();
    ActivateEventHandler::RegisterHandler();
    PhantomModelEvents::Register();
  }

}
