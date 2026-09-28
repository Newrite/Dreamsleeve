import { installVisibility } from "../bridge/visibility";
import { createRoot } from "react-dom/client";
import { useStore } from "zustand";
import { makeChat } from "../state/chat";
import type {
  AuthFailure,
  AuthState,
  Command,
  ConnectionPhase,
} from "../bridge/types";
import { defaults, settingsFrom } from "../state/settings";
import { App } from "../views/App";
import { channels, messages, players } from "./fixture";
import "../styles/base.css";
import "../themes/skyrim.css";
import "./workshop.css";
let nextId = 100;
let rejectNext = false;
// Fake account: the stand starts signed in through a remembered login. Only
// typed state is kept; the password from a signIn command is never read.
const auth: AuthState = {
  authenticating: false,
  operation: "none",
  failure: "none",
  error: "",
  savedLogin: true,
  savedUsername: players[0].username,
};
function connection(phase: ConnectionPhase) {
  chat.receive({ type: "connection", connected: phase === "connected", phase });
}
function emitAuth(next: Partial<AuthState>, phase: ConnectionPhase) {
  Object.assign(auth, next);
  chat.receive({ type: "auth", ...auth, phase });
}
function snapshot(settings = chat.store.getState().settings) {
  chat.receive({
    type: "snapshot",
    serverName: "Голоса Тамриэля",
    channels,
    messages,
    players,
    selfId: players[0].id,
    settings,
  });
}
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
  if (c.type === "signIn" || c.type === "signInSaved") {
    const resume = c.type === "signInSaved";
    const username = resume ? auth.savedUsername : c.username;
    const failure: AuthFailure =
      resume && !auth.savedLogin
        ? "credentialStorage"
        : !resume && c.username === "bad"
          ? "invalidCredentials"
          : !resume && c.displayName && c.username === "taken"
            ? "usernameTaken"
            : "none";
    connection("authenticating");
    emitAuth(
      {
        authenticating: true,
        operation: resume ? "resume" : "passwordLogin",
        failure: "none",
        error: "",
      },
      "authenticating",
    );
    setTimeout(() => {
      if (failure !== "none") {
        connection("disconnected");
        emitAuth({ authenticating: false, failure }, "disconnected");
        return;
      }
      const remember = resume || c.remember;
      connection("connected");
      emitAuth(
        {
          authenticating: false,
          operation: "none",
          savedLogin: remember,
          savedUsername: remember ? username : "",
        },
        "connected",
      );
      snapshot();
    }, 400);
    return true;
  }
  if (c.type === "signOut") {
    connection("disconnected");
    emitAuth(
      {
        operation: "none",
        failure: "none",
        error: "",
        savedLogin: false,
        savedUsername: "",
      },
      "disconnected",
    );
    return true;
  }
  if (c.type === "forgetLogin") {
    emitAuth(
      { savedLogin: false, savedUsername: "" },
      chat.store.getState().connectionPhase,
    );
    return true;
  }
  if (c.type === "disconnect") {
    connection("disconnected");
    emitAuth({}, "disconnected");
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
snapshot(settings);
emitAuth({}, "connected");
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
  const connected = useStore(chat.store, (s) => s.connected);
  const savedLogin = useStore(chat.store, (s) => s.auth.savedLogin);
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
            const phase = connected ? "disconnected" : "connected";
            connection(phase);
            emitAuth({}, phase);
          }}
        >
          {connected ? "Отключить" : "Подключить"}
        </button>
        <button
          onClick={() =>
            emitAuth(
              {
                savedLogin: !savedLogin,
                savedUsername: savedLogin ? "" : players[0].username,
              },
              chat.store.getState().connectionPhase,
            )
          }
        >
          Сохранённый вход: {savedLogin ? "есть" : "нет"}
        </button>
      </aside>
      <App chat={chat} />
    </>
  );
}
createRoot(document.getElementById("root")!).render(<Workshop />);
