import { installVisibility } from "../bridge/visibility";
import { createRoot } from "react-dom/client";
import { useState } from "react";
import { makeChat } from "../state/chat";
import type { Command } from "../bridge/types";
import { defaults, settingsFrom } from "../state/settings";
import { App } from "../views/App";
import { channels, messages, players } from "./fixture";
import "../styles/base.css";
import "../themes/skyrim.css";
import "./workshop.css";
let nextId = 100;
let rejectNext = false;
function command(c: Command) {
  if (c.type === "close") return true;
  if (c.type === "saveSettings") {
    try {
      localStorage.setItem(
        "dreamsleeve.ui.settings",
        JSON.stringify(c.settings),
      );
      setTimeout(
        () => chat.receive({ type: "settingsResult", revision: c.revision }),
        50,
      );
    } catch {
      setTimeout(
        () =>
          chat.receive({
            type: "settingsResult",
            revision: c.revision,
            error: "Не удалось сохранить настройки",
          }),
        50,
      );
    }
    return true;
  }
  const rejected = rejectNext;
  rejectNext = false;
  setTimeout(() => {
    const messageId = String(nextId++);
    chat.receive({
      type: "sendResult",
      requestId: c.requestId,
      ...(rejected
        ? { error: "Сообщение отклонено сервером." }
        : { messageId }),
    });
    if (!rejected)
      chat.receive({
        type: "messages",
        messages: [
          {
            id: messageId,
            channelId: c.channelId,
            source: "player",
            author: players[0],
            text: c.text,
            time: Date.now(),
          },
        ],
      });
  }, 600);
  return true;
}
const chat = makeChat(command);
installVisibility(chat);
let settings = defaults;
try {
  settings = settingsFrom(
    JSON.parse(localStorage.getItem("dreamsleeve.ui.settings") ?? "{}"),
  );
} catch {
  /* Local preview only. */
}
chat.receive({
  type: "snapshot",
  serverName: "Голоса Тамриэля",
  channels,
  messages,
  players,
  selfId: players[0].id,
  settings,
});
window.addEventListener("keydown", (e) => {
  const target = e.target as HTMLElement;
  if (
    chat.store.getState().visible &&
    !e.repeat &&
    !chat.store.getState().active &&
    e.key === chat.store.getState().settings.activationKey &&
    !["INPUT", "SELECT", "TEXTAREA", "BUTTON"].includes(target.tagName)
  ) {
    e.preventDefault();
    chat.receive({ type: "activate" });
  }
});
function publish(system = false) {
  chat.receive({
    type: "messages",
    messages: [
      {
        id: String(nextId++),
        channelId: system ? "announcements" : "1",
        text: system
          ? "Объявление сервера: сегодня дороги открыты для всех странников."
          : "Встретимся у старой башни. Я уже в пути.",
        time: Date.now(),
        ...(system
          ? { source: "system" as const }
          : { source: "player" as const, author: players[1] }),
      },
    ],
  });
}
function Workshop() {
  const [connected, setConnected] = useState(true);
  return (
    <>
      <div className="landscape" aria-hidden="true">
        <div className="moon" />
        <div className="ridge far" />
        <div className="ridge near" />
      </div>
      <div className="workshop-title">
        <span>UI WORKSHOP / 01</span>
        <h1>
          Голоса
          <br />
          Тамриэля
        </h1>
        <p>Один мир. Тысячи историй.</p>
      </div>
      <aside className="workshop-controls">
        <span>БРАУЗЕРНЫЙ СТЕНД · ТЕСТОВЫЕ ДАННЫЕ</span>
        <button onClick={() => chat.receive({ type: "activate" })}>
          Открыть чат · Enter
        </button>
        <button
          onClick={() =>
            chat.receive({
              type: "players",
              players: Array.from({ length: 48 }, (_, i) => ({
                ...players[i % players.length],
                id: i === 0 ? players[0].id : `demo-player-${i}`,
                displayName:
                  i === 0 ? players[0].displayName : `Странник ${i + 1}`,
                username: `traveler${i + 1}`,
              })),
            })
          }
        >
          48 игроков онлайн
        </button>
        <button onClick={() => publish()}>Новое сообщение</button>
        <button onClick={() => publish(true)}>Системное объявление</button>
        <button
          onClick={() => {
            for (let i = 0; i < 80; i++) publish();
          }}
        >
          Заполнить историю
        </button>
        <button
          onClick={() => {
            rejectNext = true;
            chat.receive({ type: "activate" });
          }}
        >
          Отклонить следующую отправку
        </button>
        <button
          onClick={() => {
            chat.receive({ type: "connection", connected: !connected });
            setConnected(!connected);
          }}
        >
          {connected ? "Отключить" : "Подключить"}
        </button>
      </aside>
      <App chat={chat} />
    </>
  );
}
createRoot(document.getElementById("root")!).render(<Workshop />);
