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
    ImGui::Text("Фантомов рядом: %zu, захват: %u Гц", snapshot.phantoms, snapshot.phantomSampleRate);
    ImGui::Text(
      "Модели: %.2f МиБ, позы: %.2f МиБ, попаданий в кеш: %llu",
      snapshot.phantomModels / 1048576.0,
      snapshot.phantomPoses / 1048576.0,
      snapshot.phantomCacheHits);
    ImGui::Text("Отклонений: %llu, устаревших поз: %llu", snapshot.phantomRejected, snapshot.phantomDropped);
    if (!snapshot.phantomError.empty()) ImGui::TextWrapped("Фантомы: %s", snapshot.phantomError.c_str());
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

#ifdef DREAMSLEEVE_DIAGNOSTICS
  void __stdcall RenderPhantomRecording()
  {
    namespace D                         = Dreamsleeve::Client::Diagnostics;
    const auto            s             = Runtime::ReadMenuSnapshot().recording;
    static int            scenario      = 0;
    static bool           thirtySeconds = false;
    constexpr const char* labels[] = {"Покой", "Ходьба и повороты", "Спринт", "Бой и оружие", "Первое/третье лицо", "Снаряжение и SMP"};
    const bool            busy     = s.phase == D::Phase::Recording || s.phase == D::Phase::Saving;
    ImGui::TextWrapped("Локальная запись исходных поз и байтов кодека. Сервер не требуется. Файлы остаются рядом с логом SKSE.");
    if (!busy)
    {
      if (ImGui::BeginCombo("Сценарий", labels[scenario]))
      {
        for (int i = 0; i < 6; ++i)
          if (ImGui::Selectable(labels[i], scenario == i)) scenario = i;
        ImGui::EndCombo();
      }
      ImGui::Checkbox("30 секунд (иначе 15)", &thirtySeconds);
      if (ImGui::Button("Начать запись"))
        Runtime::Post({Runtime::NoticeKind::PhantomRecordingStart, thirtySeconds, static_cast<std::uint32_t>(scenario)});
    }
    ImGui::TextWrapped("Во время локальной записи публикация вашего фантома приостановлена. Записывается полная поза для анализа сжатия.");
    if (s.phase == D::Phase::Recording && ImGui::Button("Остановить запись")) Runtime::Post({Runtime::NoticeKind::PhantomRecordingStop});
    constexpr const char* phases[] = {"Не записывается", "Запись (закройте меню)", "Сохранение", "Сохранено", "Ошибка записи"};
    ImGui::Text("%s: %.1f с, %llu кадров, %.1f Гц", phases[static_cast<int>(s.phase)], s.seconds, s.samples, s.sampleHz);
    ImGui::Text("Кодировано: %llu, отправлено в ENet: %llu, movement: %llu", s.encoded, s.sent, s.movements);
    ImGui::Text(
      "Файл: %.2f МиБ, очередь: %.2f МиБ, пропусков: %llu, ошибок: %llu",
      s.bytes / 1048576.0,
      s.queuedBytes / 1048576.0,
      s.dropped,
      s.errors);
    if (!s.reason.empty())
      ImGui::TextWrapped(
        "Причина завершения: %s",
        s.reason == "disk-space-low" ? "Недостаточно свободного места на диске записи" : s.reason.c_str());
    if (s.omittedGeometry || s.hiddenGeometry)
      ImGui::TextWrapped(
        "Частичный захват: пропущено деталей %u, временно скрыто %u. %s",
        s.omittedGeometry,
        s.hiddenGeometry,
        s.partialDetail.c_str());
    if (!s.lastCaptureError.empty()) ImGui::TextWrapped("Последняя ошибка захвата: %s", s.lastCaptureError.c_str());
    if (!s.directory.empty()) ImGui::TextWrapped("Папка: %s", s.directory.c_str());
  }
#endif

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
#ifdef DREAMSLEEVE_DIAGNOSTICS
    SKSEMenuFramework::AddSectionItem("Запись фантомов", RenderPhantomRecording);
#endif
    logger::info("SKSE Menu Framework page registered");
  }

}
