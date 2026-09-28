import { describe, it, expect, vi } from "vitest";
import { makeChat, HISTORY_LIMIT } from "../src/state/chat";
import { frame, defaults, settingsFrom } from "../src/state/settings";
import type { HostEvent, Message, Command } from "../src/bridge/types";
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
  expect(chat.store.getState().receivedAt).toEqual({ first: 100, second: 200 });
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
