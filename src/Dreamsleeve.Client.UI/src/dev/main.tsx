import { installVisibility } from "../bridge/visibility";
import { createRoot } from "react-dom/client";
import { useStore } from "zustand";
import { makeChat } from "../state/chat";
import type {
  AuthFailure,
  AuthState,
  Command,
  ConnectionPhase,
  Message,
  HideIdentity,
  Player,
  Settings,
} from "../bridge/types";
import { defaults } from "../bridge/settings.generated";
import { App } from "../views/App";
import {
  SYSTEM_CHANNEL,
  announcements,
  channels,
  groundMarks,
  messages,
  nearbyMarks,
  players,
} from "./fixture";
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
// Stand-in for the C++ host projection: one resolved name per player, and
// in streamer mode only pseudonyms. The real resolver lives in Host/Names.ixx.
const aliases = new Map<string, string>();
const aliasPool = ["Странник", "Следопыт", "Бард", "Страж"];
const ignored = new Map<string, string>();
function alias(id: string) {
  if (!aliases.has(id))
    aliases.set(
      id,
      aliasPool[aliases.size % aliasPool.length] +
        (aliases.size >= aliasPool.length ? " 2" : ""),
    );
  return aliases.get(id)!;
}
function project(p: Player, s: Settings, character = p.character): Player {
  if (s.streamerMode) {
    const name = alias(p.id);
    return {
      ...p,
      name,
      alias: name,
      displayName: name,
      username: "",
      character: undefined,
    };
  }
  // A hidden player is known by the server pseudonym in every name mode.
  if (p.pseudonymous) return { ...p, name: p.displayName };
  const name =
    s.nameMode === "username"
      ? p.username
      : s.nameMode === "character" && character
        ? character
        : p.displayName;
  return { ...p, name, character };
}
// Stand-in for the server side of "hide my name": a pseudonym per switch,
// a short wait for the answer, a refusal on demand. The choice is kept like
// the host keeps it in ui.toml.
const identity = {
  mode: "off" as HideIdentity,
  pseudonym: undefined as string | undefined,
  allowed: true,
  switches: 0,
};
const hidings: HideIdentity[] = ["off", "everywhere", "exceptGroundMarks"];
try {
  const saved = localStorage.getItem("dreamsleeve.ui.hideIdentity");
  identity.mode = hidings.find((hiding) => hiding === saved) ?? "off";
} catch {
  /* Local preview only. */
}
const pseudonymOf = () => `Страж ${3 + identity.switches}`;
// The last own display name change, for the stand-in interval.
let renamedAt = 0;
if (identity.mode !== "off") identity.pseudonym = pseudonymOf();
function identityEvent(error?: string, pending = false, mode = identity.mode) {
  chat.receive({
    type: "identity",
    mode,
    pending,
    ...(chat.store.getState().connected && identity.pseudonym
      ? { pseudonym: identity.pseudonym }
      : {}),
    ...(error ? { error } : {}),
  });
}
// Stand-in for server flag ranges (UTF-16 here; the host works on UTF-8).
const flags = new Map<string, [number, number][]>();
function filterText(m: Message, s: Settings): Message | undefined {
  const ranges = flags.get(m.id);
  if (!ranges || s.textFilter === "off") return m;
  const own = m.source === "player" && m.author.id === players[0].id;
  if (s.textFilter === "hide")
    return own
      ? { ...m, text: "[скрыто фильтром]", filtered: true }
      : undefined;
  let text = m.text;
  for (const [start, end] of ranges)
    text =
      text.slice(0, start) +
      text.slice(start, end).replace(/\S/gu, "*") +
      text.slice(end);
  return { ...m, text, filtered: true };
}
function projectMessages(list: Message[], s: Settings) {
  return list
    .filter((m) => m.source === "system" || !ignored.has(m.author.id))
    .map((m) => filterText(m, s))
    .filter((m): m is Message => m !== undefined)
    .map((m) => (m.author ? { ...m, author: project(m.author, s) } : m));
}
function ignoredEvent(s = chat.store.getState().settings) {
  chat.receive({
    type: "ignored",
    players: [...ignored].map(([id, name]) => ({
      id,
      name: s.streamerMode ? alias(id) : name,
    })),
  });
}
const history: Message[] = [
  ...[...messages, ...announcements].sort((a, b) => a.time - b.time),
  {
    id: "flagged-1",
    channelId: "1",
    source: "player",
    author: players[2],
    text: "Раздаю скины: t.me/freeskins",
    time: Date.now() - 1000,
  },
  {
    id: "hidden-1",
    channelId: "1",
    source: "player",
    author: players[3],
    text: "Кто-то видел дракона над Ривервудом?",
    time: Date.now() - 500,
  },
];
flags.set("flagged-1", [[14, 28]]);
// Stand-in for the server's own list and the host's nearby projection.
const marks = [...groundMarks];
const nearby = [...nearbyMarks];
let nextMarkId = 400;
function markEvents() {
  chat.receive({ type: "groundMarks", marks: [...marks] });
  chat.receive({ type: "nearbyMarks", marks: [...nearby] });
}
function snapshot(settings = chat.store.getState().settings, refresh = false) {
  chat.receive({
    type: "snapshot",
    serverName: "Голоса Тамриэля",
    channels,
    messages: projectMessages(history, settings),
    players: players.map((p) => project(p, settings)),
    selfId: players[0].id,
    settings,
    refresh,
    groundMarksSupported: true,
    groundMarks: marks,
    nearbyMarks: nearby,
  });
}
function command(c: Command) {
  if (c.type === "close") return true;
  if (c.type === "placeGroundNote") {
    const rejected = rejectNext;
    rejectNext = false;
    setTimeout(() => {
      if (rejected) {
        chat.receive({
          type: "markResult",
          requestId: c.requestId,
          error: "Здесь уже слишком много меток",
        });
        return;
      }
      // Two notes per player on the stand: the oldest gives way.
      const notes = marks.filter((m) => m.kind === "note");
      const evicted = notes.length >= 2 ? notes[0] : undefined;
      if (evicted) marks.splice(marks.indexOf(evicted), 1);
      const markId = String(nextMarkId++);
      const placed = {
        id: markId,
        kind: "note" as const,
        text: c.text,
        time: Date.now(),
        character: "Довакин",
        gameDate: "Турдас, 19 Последнего зерна 4Э 201, 10:30",
        location: "skyrim.esm:01A26F",
        x: 1240,
        y: -880,
        z: 512,
      };
      marks.push(placed);
      if (evicted) {
        const index = nearby.findIndex((m) => m.id === evicted.id);
        if (index >= 0) nearby.splice(index, 1);
      }
      nearby.push({ ...placed, author: players[0].name });
      chat.receive({
        type: "markResult",
        requestId: c.requestId,
        markId,
        ...(evicted ? { evictedId: evicted.id } : {}),
      });
      markEvents();
    }, 300);
    return true;
  }
  if (c.type === "removeGroundMark") {
    setTimeout(() => {
      const index = marks.findIndex((m) => m.id === c.markId);
      if (index < 0) {
        chat.receive({
          type: "markResult",
          requestId: c.requestId,
          error: "Метка не найдена или уже удалена",
        });
        return;
      }
      marks.splice(index, 1);
      const shown = nearby.findIndex((m) => m.id === c.markId);
      if (shown >= 0) nearby.splice(shown, 1);
      chat.receive({
        type: "markResult",
        requestId: c.requestId,
        removed: true,
      });
      markEvents();
    }, 300);
    return true;
  }
  if (c.type === "setIdentityVisibility") {
    if (!chat.store.getState().connected) {
      identity.mode = c.hiding;
      identity.pseudonym = undefined;
      localStorage.setItem("dreamsleeve.ui.hideIdentity", c.hiding);
      setTimeout(() => identityEvent(), 0);
      return true;
    }
    setTimeout(() => identityEvent(undefined, true, c.hiding), 0);
    setTimeout(() => {
      if (c.hiding !== "off" && !identity.allowed) {
        identityEvent("Сервер не разрешает скрывать имя");
        return;
      }
      // A new pseudonym only when the names were shown everywhere before.
      if (identity.mode === "off" && c.hiding !== "off") {
        identity.switches++;
        identity.pseudonym = pseudonymOf();
      }
      if (c.hiding === "off") identity.pseudonym = undefined;
      identity.mode = c.hiding;
      localStorage.setItem("dreamsleeve.ui.hideIdentity", c.hiding);
      identityEvent();
    }, 400);
    return true;
  }
  // Stand-in for the server: the word list and a one-minute change interval.
  if (c.type === "changeDisplayName") {
    setTimeout(() => chat.receive({ type: "displayName", pending: true }), 0);
    setTimeout(() => {
      const name = c.displayName.trim();
      if (/badword/i.test(name)) {
        chat.receive({
          type: "displayName",
          pending: false,
          error: "Имя содержит запрещённые слова",
        });
        return;
      }
      if (renamedAt && Date.now() - renamedAt < 60000) {
        chat.receive({
          type: "displayName",
          pending: false,
          error: "Имя можно сменить снова через 1 мин",
        });
        return;
      }
      renamedAt = Date.now();
      players[0] = { ...players[0], displayName: name };
      snapshot(chat.store.getState().settings, true);
      chat.receive({ type: "displayName", pending: false, changed: name });
    }, 400);
    return true;
  }
  if (c.type === "ignore" || c.type === "unignore") {
    const known = players.find((p) => p.id === c.playerId);
    if (c.type === "ignore") ignored.set(c.playerId, known?.displayName ?? "");
    else ignored.delete(c.playerId);
    setTimeout(() => {
      ignoredEvent();
      snapshot(chat.store.getState().settings, true);
    }, 50);
    return true;
  }
  if (c.type === "displaySettings") {
    setTimeout(() => {
      const settings = c.settings;
      ignoredEvent(settings);
      snapshot(settings, true);
    }, 50);
    return true;
  }
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
    const settings = chat.store.getState().settings;
    chat.receive({
      type: "sendResult",
      requestId: c.requestId,
      ...(rejected
        ? { error: "Сообщение содержит запрещённые слова" }
        : { messageId }),
    });
    if (!rejected) {
      const message: Message = {
        id: messageId,
        channelId: c.channelId,
        source: "player",
        author: players[0],
        text: c.text,
        time: Date.now(),
      };
      history.push(message);
      chat.receive({
        type: "messages",
        messages: projectMessages([message], settings),
      });
    }
  }, 600);
  return true;
}
const chat = makeChat(command);
installVisibility(chat);
// The host normalizes real settings; the preview trusts its own saved copy.
let settings = defaults;
try {
  settings = {
    ...defaults,
    ...JSON.parse(localStorage.getItem("dreamsleeve.ui.settings") ?? "{}"),
  };
} catch {
  /* Local preview only. */
}
snapshot(settings);
emitAuth({}, "connected");
identityEvent();
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
// Stand-in for a mod posting through the API: the server may refuse it.
const MOD = "Carriage Tours";
function publish(system?: "server" | "thirdParty") {
  const message: Message = {
    id: String(nextId++),
    channelId: system ? SYSTEM_CHANNEL : "1",
    text:
      system === "server"
        ? "Объявление сервера: сегодня дороги открыты для всех странников."
        : system
          ? "Карета до Маркарта ждёт у конюшен Вайтрана."
          : "Встретимся у старой башни. Я уже в пути.",
    time: Date.now(),
    ...(system === "server"
      ? {
          source: "system" as const,
          announcement: {
            origin: "server" as const,
            kind: "announcement" as const,
          },
        }
      : system
        ? {
            source: "system" as const,
            announcement: {
              origin: "thirdParty" as const,
              kind: "event" as const,
              signature: MOD,
            },
            author: players[2],
          }
        : { source: "player" as const, author: players[1] }),
  };
  history.push(message);
  chat.receive({
    type: "messages",
    messages: projectMessages([message], chat.store.getState().settings),
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
                name: i === 0 ? players[0].name : `Странник ${i + 1}`,
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
        <button onClick={() => publish("server")}>Системное объявление</button>
        <button onClick={() => publish("thirdParty")}>Объявление мода</button>
        <button
          onClick={() =>
            chat.receive({
              type: "announcementResult",
              channelId: SYSTEM_CHANNEL,
              source: MOD,
              text: "Карета до Рифтена отправляется через минуту.",
              error: "Слишком частые объявления",
            })
          }
        >
          Отказ объявления мода
        </button>
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
            identity.allowed = !identity.allowed;
            chat.receive({ type: "activate" });
          }}
        >
          Сервер разрешает скрытое имя: переключить
        </button>
        <button
          onClick={() => {
            const phase = connected ? "disconnected" : "connected";
            connection(phase);
            emitAuth({}, phase);
            identityEvent();
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
