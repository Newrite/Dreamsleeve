import { describe, it, expect, vi } from "vitest";
import { makeChat, HISTORY_LIMIT } from "../src/state/chat";
import { frame, defaults, settingsFrom } from "../src/state/settings";
import { accountActions, authStatus, idleAuth } from "../src/state/auth";
import type {
  AuthEvent,
  HostEvent,
  Message,
  Command,
} from "../src/bridge/types";
const snapshot: HostEvent = {
  type: "snapshot",
  serverName: "Голоса Тамриэля",
  channels: [
    { id: "1", name: "Общий", kind: "global", writable: true },
    { id: "system", name: "Объявления", kind: "system", writable: false },
  ],
  messages: [],
  players: [],
  selfId: "18446744073709551615",
};
const message = (id: string, channelId = "1"): Message => ({
  id,
  channelId,
  source: "system",
  announcement: { origin: "server", kind: "announcement" },
  text: "<script>alert(1)</script>",
  time: 0,
});
function ready() {
  const send = vi.fn((_command: Command) => true);
  const chat = makeChat(send);
  chat.receive(snapshot);
  return { chat, send };
}
describe("chat boundary", () => {
  it("waits for authoritative publication, and preserves uint64 identifiers", () => {
    const { chat, send } = ready();
    chat.setDraft("Привет");
    chat.submit();
    expect(chat.store.getState().messages).toHaveLength(0);
    expect(send.mock.calls[0][0]).toMatchObject({
      type: "sendChat",
      channelId: "1",
      text: "Привет",
    });
    chat.receive({
      type: "sendResult",
      requestId: "1",
      messageId: "18446744073709551615",
    });
    expect(chat.store.getState().messages).toHaveLength(0);
    chat.receive({
      type: "messages",
      messages: [message("18446744073709551615")],
    });
    expect(chat.store.getState().messages[0].id).toBe("18446744073709551615");
  });
  it("restores rejected drafts and prevents overlapping requests", () => {
    const { chat, send } = ready();
    chat.setDraft("Исходный текст");
    chat.submit();
    chat.submit();
    expect(send.mock.calls.filter(([c]) => c.type === "sendChat")).toHaveLength(
      1,
    );
    chat.receive({ type: "sendResult", requestId: "1", error: "Отклонено" });
    expect(chat.store.getState().pending["1"].status).toBe("failed");
    chat.retry("1");
    expect(send.mock.calls.filter(([c]) => c.type === "sendChat")).toHaveLength(
      2,
    );
  });
  it("does not discard a command if the host listener is missing", () => {
    const chat = makeChat(() => false);
    chat.receive(snapshot);
    chat.setDraft("Повторить");
    chat.submit();
    expect(chat.store.getState().pending["1"]).toMatchObject({
      text: "Повторить",
      status: "failed",
    });
  });
  it("never sends to the read-only announcement channel", () => {
    const { chat, send } = ready();
    chat.store.setState({ target: "system" });
    chat.setDraft("Подделка");
    chat.submit();
    expect(send).not.toHaveBeenCalled();
  });
  it("deduplicates a batch and bounds retained history", () => {
    const { chat } = ready();
    chat.receive({ type: "messages", messages: [message("x"), message("x")] });
    expect(chat.store.getState().messages).toHaveLength(1);
    chat.receive({
      type: "messages",
      messages: Array.from({ length: 700 }, (_, i) => message(String(i))),
    });
    expect(chat.store.getState().messages).toHaveLength(HISTORY_LIMIT);
  });
  it("keeps history per channel: equal IDs coexist and a busy channel evicts only itself", () => {
    const { chat } = ready();
    chat.receive({
      type: "messages",
      messages: [message("1", "system"), message("1")],
    });
    expect(chat.store.getState().messages).toHaveLength(2);
    chat.receive({
      type: "messages",
      messages: Array.from({ length: 700 }, (_, i) => message(String(i + 2))),
    });
    const messages = chat.store.getState().messages;
    expect(messages).toHaveLength(HISTORY_LIMIT + 1);
    expect(messages.filter((m) => m.channelId === "system")).toHaveLength(1);
  });
  it("a snapshot of several full channels is kept whole, in time order", () => {
    const chat = makeChat(() => true);
    const line = (id: string, channelId: string, time: number) => ({
      ...message(id, channelId),
      time,
    });
    chat.receive({
      ...snapshot,
      messages: [
        ...Array.from({ length: HISTORY_LIMIT }, (_, i) =>
          line(String(i), "1", i * 2),
        ),
        ...Array.from({ length: HISTORY_LIMIT }, (_, i) =>
          line(String(i), "system", i * 2 + 1),
        ),
      ],
    } as HostEvent);
    const messages = chat.store.getState().messages;
    expect(messages).toHaveLength(HISTORY_LIMIT * 2);
    expect(messages.slice(0, 2).map((m) => m.channelId)).toEqual([
      "1",
      "system",
    ]);
  });
  it("settles an own line by channel and ID, not by an equal ID of another channel", () => {
    const { chat } = ready();
    chat.receive({ type: "messages", messages: [message("5", "system")] });
    chat.setDraft("Привет");
    chat.submit();
    chat.receive({ type: "sendResult", requestId: "1", messageId: "5" });
    expect(chat.store.getState().pending["1"]).toMatchObject({
      messageId: "5",
    });
    chat.receive({ type: "messages", messages: [message("5")] });
    expect(chat.store.getState().pending["1"]).toBeUndefined();
  });
  it("filtered messages do not wake the chat or change send target", () => {
    const { chat } = ready();
    chat.select("1");
    chat.store.setState({ faded: true });
    chat.receive({ type: "messages", messages: [message("notice", "system")] });
    expect(chat.store.getState().faded).toBe(true);
    expect(chat.store.getState().unread.system).toBe(1);
    chat.select("system");
    expect(chat.store.getState().target).toBe("1");
  });
  it("restores pending text on disconnect", () => {
    const { chat } = ready();
    chat.setDraft("Остаться");
    chat.submit();
    chat.receive({ type: "connection", connected: false });
    expect(chat.store.getState().pending["1"]).toMatchObject({
      text: "Остаться",
      status: "unknown",
    });
  });
  it("ignores stale settings acknowledgements", () => {
    const { chat } = ready();
    chat.save();
    chat.save();
    chat.receive({ type: "settingsResult", revision: 1 });
    expect(chat.store.getState().savedRevision).toBe(0);
    chat.receive({ type: "settingsResult", revision: 2 });
    expect(chat.store.getState().savedRevision).toBe(2);
  });
});
describe("settings", () => {
  it("validates persisted values and clamps geometry to a small viewport", () => {
    const s = settingsFrom({
      scale: Infinity,
      x: 1,
      y: 1,
      width: 1600,
      height: 1200,
      font: "invalid" as "serif",
    });
    expect(s.scale).toBe(1);
    expect(s.font).toBe("sans");
    expect(frame(s, 300, 180)).toEqual({
      left: 0,
      top: 0,
      width: 300,
      height: 180,
    });
    expect(settingsFrom({})).toEqual(defaults);
  });
  it("combat hiding is off by default and accepts booleans only", () => {
    expect(settingsFrom({ fontSize: 20 })).toMatchObject({
      combatHideFireflies: false,
      combatHideNames: false,
      combatHideBubbles: false,
    });
    expect(
      settingsFrom({
        combatHideFireflies: true,
        combatHideNames: "yes" as unknown as boolean,
        combatHideBubbles: true,
      }),
    ).toMatchObject({
      combatHideFireflies: true,
      combatHideNames: false,
      combatHideBubbles: true,
    });
  });
  it("keeps bubble defaults for settings saved before bubbles existed and clamps new values", () => {
    const old = settingsFrom({ fontSize: 20, showFireflyNames: false });
    expect(old.showBubbles).toBe(true);
    expect(old.bubbleDuration).toBe(8);
    expect(old.bubbleFade).toBe(true);
    expect(old.bubbleMaxWidth).toBe(320);
    const edited = settingsFrom({
      showBubbles: false,
      bubbleDuration: 500,
      bubbleFadeDuration: 0,
      bubbleFontSize: 4,
      bubbleMaxWidth: 5000,
      bubbleBackground: -1,
    });
    expect(edited.showBubbles).toBe(false);
    expect(edited.bubbleDuration).toBe(60);
    expect(edited.bubbleFadeDuration).toBe(0.1);
    expect(edited.bubbleFontSize).toBe(8);
    expect(edited.bubbleMaxWidth).toBe(800);
    expect(edited.bubbleBackground).toBe(0);
  });
});

it("editing settings during a save does not show a stale success", () => {
  const { chat } = ready();
  chat.save();
  const revision = chat.store.getState().revision;
  chat.configure({ fontSize: 22 });
  chat.receive({ type: "settingsResult", revision });
  expect(chat.store.getState().savedRevision).toBe(0);
  expect(chat.store.getState().notice).toContain("изменены");
});

it("receipt ages are independent, duplicates do not refresh them, and snapshots do not flash history", () => {
  let now = 100;
  const chat = makeChat(
    () => true,
    () => now,
  );
  chat.receive(snapshot);
  chat.receive({ type: "messages", messages: [message("first")] });
  now = 200;
  chat.receive({
    type: "messages",
    messages: [message("first"), message("second")],
  });
  expect(chat.store.getState().receivedAt).toEqual({
    "1:first": 100,
    "1:second": 200,
  });
  chat.receive({
    type: "messages",
    messages: Array.from({ length: 700 }, (_, i) => message(String(i))),
  });
  expect(Object.keys(chat.store.getState().receivedAt)).toHaveLength(500);
  chat.receive(snapshot);
  expect(chat.store.getState().receivedAt).toEqual({});
});

it("settles either reply order by ID and never by matching text", () => {
  for (const ackFirst of [true, false]) {
    const { chat } = ready();
    chat.setDraft("same");
    chat.submit();
    const publication = { ...message("42"), text: "same" };
    chat.receive({
      type: "messages",
      messages: [{ ...publication, id: "other" }],
    });
    expect(chat.store.getState().pending["1"]).toBeDefined();
    const ack: HostEvent = {
      type: "sendResult",
      requestId: "1",
      messageId: "42",
    };
    const event: HostEvent = { type: "messages", messages: [publication] };
    chat.receive(ackFirst ? ack : event);
    chat.receive(ackFirst ? event : ack);
    expect(chat.store.getState().pending).toEqual({});
    expect(chat.store.getState().messages).toHaveLength(2);
  }
});
it("keeps unknown delivery after timeout, accepts a late reply, and never retries automatically", () => {
  let now = 0;
  const send = vi.fn(() => true);
  const chat = makeChat(send, () => now);
  chat.receive(snapshot);
  chat.setDraft("late");
  chat.submit();
  now = 15000;
  chat.expirePending();
  expect(chat.store.getState().pending["1"].status).toBe("unknown");
  chat.retry("1");
  expect(send).toHaveBeenCalledTimes(2); // send + release focus
  chat.receive({ type: "sendResult", requestId: "1", messageId: "late" });
  chat.receive({ type: "messages", messages: [message("late")] });
  expect(chat.store.getState().pending).toEqual({});
});
it("settled rows leave the passive HUD after the timeout but stay while the chat is active", () => {
  let now = 0;
  const send = vi.fn(() => true);
  const chat = makeChat(send, () => now);
  chat.receive(snapshot);
  chat.setDraft("часто");
  chat.submit();
  now = 1000;
  chat.receive({ type: "sendResult", requestId: "1", error: "Слишком часто" });
  expect(chat.store.getState().pending["1"]).toMatchObject({
    status: "failed",
    since: 1000,
  });
  now = 15999;
  chat.expirePending();
  expect(chat.store.getState().pending["1"]).toBeDefined();
  chat.store.setState({ active: true });
  now = 40000;
  chat.expirePending();
  expect(chat.store.getState().pending["1"]).toBeDefined();
  chat.store.setState({ active: false });
  chat.expirePending();
  expect(chat.store.getState().pending).toEqual({});
});
it("a new identity cannot inherit pending rows, and snapshot reconciliation uses IDs", () => {
  const { chat } = ready();
  expect(chat.store.getState().serverName).toBe("Голоса Тамриэля");
  chat.setDraft("one");
  chat.submit();
  chat.receive({ type: "sendResult", requestId: "1", messageId: "42" });
  chat.receive({ ...snapshot, messages: [message("42")] });
  expect(chat.store.getState().pending).toEqual({});
  chat.setDraft("two");
  chat.submit();
  chat.receive({ ...snapshot, selfId: "different" });
  expect(chat.store.getState().pending).toEqual({});
});

it("hide preserves data, ignores activation and snapshots, and show stays passive", () => {
  const { chat, send } = ready();
  chat.setDraft("Черновик");
  chat.receive({ type: "activate" });
  chat.open("settings");
  chat.receive({ type: "hide" });
  chat.receive({ type: "hide" });
  expect(send.mock.calls.filter(([c]) => c.type === "close")).toHaveLength(1);
  chat.receive({ type: "activate" });
  chat.open("online");
  chat.submit();
  chat.receive({ ...snapshot, messages: [message("hidden")] });
  expect(chat.store.getState()).toMatchObject({
    visible: false,
    active: false,
    panel: null,
    drafts: { "1": "Черновик" },
  });
  chat.receive({ type: "messages", messages: [message("new")] });
  chat.receive({ type: "show" });
  expect(chat.store.getState()).toMatchObject({
    visible: true,
    active: false,
    panel: null,
  });
  expect(chat.store.getState().messages).toHaveLength(2);
  expect(send.mock.calls.filter(([c]) => c.type === "sendChat")).toHaveLength(
    0,
  );
});
it("hide is effective even without a native command listener", () => {
  const chat = makeChat(() => false);
  chat.receive(snapshot);
  chat.receive({ type: "activate" });
  chat.receive({ type: "hide" });
  expect(chat.store.getState()).toMatchObject({
    visible: false,
    active: false,
  });
});

const authEvent = (patch: Partial<AuthEvent> = {}): AuthEvent => ({
  type: "auth",
  authenticating: false,
  operation: "none",
  failure: "none",
  error: "",
  savedLogin: false,
  savedUsername: "",
  phase: "disconnected",
  ...patch,
});
describe("account", () => {
  it("sends the password once with the command and never keeps it in state", () => {
    const send = vi.fn((_command: Command) => true);
    const chat = makeChat(send);
    chat.signIn("  northern ", "s3cret", true);
    chat.signIn("northern", "s3cret", true);
    expect(send).toHaveBeenCalledTimes(1);
    expect(send.mock.calls[0][0]).toEqual({
      type: "signIn",
      username: "northern",
      password: "s3cret",
      remember: true,
    });
    expect(JSON.stringify(chat.store.getState())).not.toContain("s3cret");
    expect(chat.store.getState().auth).toMatchObject({
      authenticating: true,
      operation: "passwordLogin",
    });
    chat.receive(authEvent({ failure: "invalidCredentials" }));
    expect(chat.store.getState().auth.authenticating).toBe(false);
    chat.signIn("northern", "again", false, " Довакин ");
    expect(send.mock.calls[1][0]).toEqual({
      type: "signIn",
      username: "northern",
      password: "again",
      remember: false,
      displayName: "Довакин",
    });
    expect(JSON.stringify(chat.store.getState())).not.toContain("again");
  });
  it("refuses empty credentials and stays idle when the host listener is missing", () => {
    const chat = makeChat(() => false);
    chat.signIn("", "x", true);
    chat.signIn("x", "", true);
    expect(chat.store.getState().auth).toEqual(idleAuth);
    chat.receive(authEvent({ savedLogin: true, savedUsername: "northern" }));
    chat.signInSaved();
    expect(chat.store.getState().auth.authenticating).toBe(false);
    expect(chat.store.getState().notice).toContain("не принята");
  });
  it("maps failure codes to Russian text and appends the raw host error", () => {
    const labels = {
      invalidCredentials: "Неверное имя или пароль",
      usernameTaken: "Имя занято",
      invalidRequest: "Некорректный запрос",
      registrationDisabled: "Регистрация отключена",
      busy: "Сервер занят, повторите позже",
      unavailable: "Сервер недоступен",
      invalidResponse: "Некорректный ответ сервера",
      credentialStorage: "Ошибка хранилища учётных данных Windows",
      canceled: "Операция отменена",
    } as const;
    for (const [failure, label] of Object.entries(labels))
      expect(
        authStatus({ ...idleAuth, failure: failure as keyof typeof labels }),
      ).toBe(label);
    expect(
      authStatus({ ...idleAuth, failure: "unavailable", error: "timeout" }),
    ).toBe("Сервер недоступен: timeout");
    expect(authStatus({ ...idleAuth, error: "Только текст" })).toBe(
      "Только текст",
    );
    expect(
      authStatus({ ...idleAuth, authenticating: true, operation: "resume" }),
    ).toBe("Вход сохранённой сессией…");
    expect(authStatus(idleAuth)).toBe("");
  });
  it("disables every account button while authenticating and gates the rest", () => {
    const form = { username: "northern", password: "x", displayName: "Дов" };
    const busy = accountActions(
      { ...idleAuth, authenticating: true, savedLogin: true },
      true,
      form,
    );
    expect(Object.values(busy)).toEqual([
      false,
      false,
      false,
      false,
      false,
      false,
    ]);
    expect(accountActions(idleAuth, false, form)).toEqual({
      signIn: true,
      register: true,
      resume: false,
      disconnect: false,
      signOut: false,
      forget: false,
    });
    expect(
      accountActions({ ...idleAuth, savedLogin: true }, true, {
        ...form,
        password: "",
        displayName: "",
      }),
    ).toEqual({
      signIn: false,
      register: false,
      resume: true,
      disconnect: true,
      signOut: true,
      forget: true,
    });
  });
  it("mirrors the phase from auth events and guards the saved-login commands", () => {
    const { chat, send } = ready();
    chat.receive(
      authEvent({
        phase: "authenticating",
        authenticating: true,
        operation: "resume",
      }),
    );
    expect(chat.store.getState()).toMatchObject({
      connected: false,
      connectionPhase: "authenticating",
    });
    chat.signOut();
    chat.disconnect();
    expect(send).not.toHaveBeenCalled();
    chat.receive(
      authEvent({ phase: "connected", savedLogin: true, savedUsername: "n" }),
    );
    expect(chat.store.getState().connected).toBe(true);
    chat.disconnect();
    chat.forgetLogin();
    expect(chat.store.getState().auth.operation).toBe("forgetSavedLogin");
    chat.signInSaved();
    chat.receive(authEvent({ phase: "disconnected" }));
    chat.forgetLogin();
    chat.signInSaved();
    chat.signOut();
    expect(send.mock.calls.map(([c]) => c.type)).toEqual([
      "disconnect",
      "forgetLogin",
    ]);
    chat.receive(authEvent({ phase: "connected" }));
    chat.signOut();
    expect(send.mock.calls.at(-1)?.[0]).toEqual({ type: "signOut" });
    expect(chat.store.getState().auth.authenticating).toBe(true);
  });
  it("opens the account panel without a snapshot", () => {
    const chat = makeChat(() => true);
    chat.receive({ type: "activate" });
    chat.open("account");
    expect(chat.store.getState()).toMatchObject({
      initialized: false,
      active: true,
      panel: "account",
    });
  });
});
