module;
#include "Prelude.hpp"
export module Dreamsleeve.Game.PlayerLabels;
import std;
import Dreamsleeve.Runtime;
import Dreamsleeve.Host.Hud;
import Dreamsleeve.UI.Nameplates;

export namespace PlayerLabels
{

  void Add(
    Nameplates::Frame&                    frame,
    Domain::PlayerId                      id,
    const RE::NiPoint3&                   position,
    const Domain::Player&                 remote,
    std::chrono::steady_clock::time_point now)
  {
    auto&             runtime = Runtime::Get();
    const auto&       ui      = runtime.ui.ui.chat;
    const auto*       player  = RE::PlayerCharacter::GetSingleton();
    const bool        combat  = player && player->IsInCombat();
    Nameplates::Label label{
        .key       = {Nameplates::LabelKind::Player, id},
        .nameSize  = static_cast<float>(ui.fireflyNameFontSize),
        .nameColor = Dreamsleeve::Host::Hud::NameColor(ui),
        .style     = Dreamsleeve::Host::Hud::PlayerBubble(ui)
    };

    if (ui.showFireflyNames && !(combat && ui.combatHideNames))
    {
      auto name  = runtime.session.PlayerNames().NameFor(id, remote.data, remote.characterName, ui);
      label.name = Dreamsleeve::Host::Names::PlateName(std::move(name), remote.data);
    }

    if (ui.showBubbles && !(combat && ui.combatHideBubbles))
      if (const auto active = runtime.bubbles.Find(id, now, ui))
      {
        label.bubble      = std::string(active->text);
        label.bubbleAlpha = active->alpha;
      }

    auto anchor  = position;
    anchor.z    += static_cast<float>(ui.fireflyNameOffset);
    Nameplates::Add(frame, std::move(label), anchor, ui.fireflyNameOcclusion);
  }

}
