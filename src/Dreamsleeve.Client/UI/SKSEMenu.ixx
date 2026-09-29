module;

#include "Prelude.hpp"
#include "API/SKSEMenuFramework.h"

export module Dreamsleeve.SKSEMenu;

import std;
import Dreamsleeve.Runtime;

// SKSE Menu Framework page. Render callbacks run inside the framework's own
// draw pass, not on the game thread: they read a mirrored snapshot and post
// notices; the frame hook applies and saves the changes.
namespace SKSEMenu
{

  namespace ImGui = ImGuiMCP;

  constexpr auto Section = "Dreamsleeve";

  void __stdcall RenderStatus()
  {
    const auto snapshot = Runtime::ReadMenuSnapshot();
    if (!snapshot.available)
    {
      ImGui::TextWrapped("Клиент ещё не запущен. Проверьте Data/SKSE/Plugins/Dreamsleeve/client.toml и лог DreamsleeveClient.");
      return;
    }

    ImGui::Text("Соединение: %s", snapshot.phase.c_str());
    ImGui::Text("Сервер: %s", snapshot.serverName.empty() ? "-" : snapshot.serverName.c_str());
    ImGui::Text("Игроков онлайн: %zu", snapshot.online);
    ImGui::Text("Светлячков рядом: %zu", snapshot.fireflies);
    ImGui::Text("Меток рядом: %zu", snapshot.groundMarks);
    if (snapshot.savedLogin)
      ImGui::Text("Сохранённый вход: %s", snapshot.savedUsername.c_str());
    else
      ImGui::TextUnformatted("Сохранённого входа нет: войдите через окно чата.");
    if (snapshot.authenticating) ImGui::TextUnformatted("Выполняется вход...");
    if (!snapshot.error.empty()) ImGui::TextWrapped("Ошибка: %s", snapshot.error.c_str());

    ImGui::Separator();
    if (snapshot.savedLogin && !snapshot.authenticating && snapshot.phase != "connected")
      if (ImGui::Button("Войти сохранённой сессией")) Runtime::Post({Runtime::NoticeKind::ResumeLogin});
    if (snapshot.phase != "disconnected" && snapshot.phase != "faulted")
      if (ImGui::Button("Отключиться")) Runtime::Post({Runtime::NoticeKind::Disconnect});
  }

  void __stdcall RenderSettings()
  {
    const auto snapshot = Runtime::ReadMenuSnapshot();
    bool       hidden   = snapshot.hideUi;
    if (ImGui::Checkbox("Отключить чат и интерфейс", &hidden)) Runtime::Post({Runtime::NoticeKind::UiHidden, hidden});
    ImGui::TextWrapped("Скрывает окно PrismaUI и отключает клавишу активации. Сеть, онлайн и светлячки продолжают работать.");

    ImGui::Separator();
    const char* keys[] = {"Enter", "F2"};
    const bool  f2     = snapshot.activationKey == "F2";
    if (ImGui::BeginCombo("Клавиша чата", f2 ? keys[1] : keys[0]))
    {
      for (int index = 0; index < 2; ++index)
      {
        const bool selected = (index == 1) == f2;
        if (ImGui::Selectable(keys[index], selected) && !selected) Runtime::Post({Runtime::NoticeKind::ActivationKeyF2, index == 1});
        if (selected) ImGui::SetItemDefaultFocus();
      }
      ImGui::EndCombo();
    }

    ImGui::Separator();
    ImGui::TextWrapped(
      "Положение, размер и оформление чата настраиваются в самом окне чата (ESC/☰) и хранятся в ui.toml. " "Адрес сервера, радиус видимости и светлячки задаются в client.toml и читаются при запуске игры.");
  }

  export auto RegisterSKSEMenu() -> void
  {
    if (!SKSEMenuFramework::IsInstalled())
    {
      logger::warn("SKSEMenuFramework not installed; settings page unavailable");
      return;
    }

    SKSEMenuFramework::SetSection(Section);
    SKSEMenuFramework::AddSectionItem("Состояние", RenderStatus);
    SKSEMenuFramework::AddSectionItem("Настройки", RenderSettings);
    logger::info("SKSE Menu Framework page registered");
  }

}
