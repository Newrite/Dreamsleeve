import { describe, it, expect, vi } from "vitest";
import { makeChat } from "../src/state/chat";
import { defaults, settingsFrom } from "../src/state/settings";
import { characterLine, HIDDEN_NAME, playerName } from "../src/state/names";
import { parseHostEvent } from "../src/bridge/parse";
import type { Command, HostEvent, Player } from "../src/bridge/types";

const lydia: Player = {
  id: "7",
  name: "Лидия",
  inCharacter: true,
  displayName: "Лидия",
  username: "lydia",
  character: "Хускарл",
};
const snapshot: HostEvent = {
  type: "snapshot",
  serverName: "Тамриэль",
  channels: [{ id: "1", name: "Общий", kind: "global", writable: true }],
  messages: [
    {
      id: "10",
      channelId: "1",
      source: "player",
      author: lydia,
      text: "Привет",
      time: 0,
    },
  ],
  players: [lydia],
  selfId: "1",
};
function ready() {
  const send = vi.fn((_command: Command) => true);
  const chat = makeChat(send);
  chat.receive(snapshot);
  return { chat, send };
}

describe("names", () => {
  it("uses the host-resolved name and never a real name in streamer mode", () => {
    const streamer = { ...defaults, streamerMode: true };
    expect(playerName(lydia, defaults)).toBe("Лидия");
    // The host has not re-projected yet: no real name may appear.
    expect(playerName(lydia, streamer)).toBe(HIDDEN_NAME);
    expect(characterLine(lydia, streamer)).toBe("В игре");
    const aliased = { ...lydia, name: "Страж", alias: "Страж", username: "" };
    expect(playerName(aliased, streamer)).toBe("Страж");
    expect(characterLine({ ...lydia, inCharacter: false }, defaults)).toBe(
      "Вне персонажа",
    );
    // A withheld character name: in a character, but no game name to show.
    expect(characterLine({ ...lydia, character: undefined }, defaults)).toBe(
      "В игре",
    );
  });

  it("reads the legacy account mode as username and keeps streamer mode boolean", () => {
    expect(settingsFrom({ nameMode: "account" as never }).nameMode).toBe(
      "username",
    );
    expect(settingsFrom({ nameMode: "character" }).nameMode).toBe("character");
    expect(settingsFrom({ streamerMode: "yes" as never }).streamerMode).toBe(
      false,
    );
  });

  it("sends name settings at once and keeps the local choice over a stale refresh", () => {
    const { chat, send } = ready();
    chat.configure({ streamerMode: true });
    expect(send).toHaveBeenLastCalledWith({
      type: "displaySettings",
      nameMode: "display",
      streamerMode: true,
      textFilter: "off",
    });
    chat.receive({
      ...snapshot,
      refresh: true,
      settings: { ...defaults, streamerMode: false },
    } as HostEvent);
    expect(chat.store.getState().settings.streamerMode).toBe(true);
    send.mockClear();
    chat.configure({ fontSize: 18 });
    expect(send).not.toHaveBeenCalled();
  });
});

describe("unsent messages and refresh", () => {
  it("a server refusal marks the optimistic row failed with the reason", () => {
    const { chat } = ready();
    chat.receive({ type: "activate" });
    chat.setDraft("плохие слова");
    chat.submit();
    const [id] = Object.keys(chat.store.getState().pending);
    chat.receive({
      type: "sendResult",
      requestId: id,
      error: "Сообщение содержит запрещённые слова",
    });
    const row = chat.store.getState().pending[id];
    expect(row.status).toBe("failed");
    expect(row.error).toBe("Сообщение содержит запрещённые слова");
    expect(chat.store.getState().messages).toHaveLength(1);
  });

  it("a refresh snapshot keeps pending rows, filter and receipts but replaces content", () => {
    const { chat } = ready();
    chat.receive({ type: "activate" });
    chat.setDraft("в пути");
    chat.submit();
    chat.select("1");
    chat.receive({
      type: "messages",
      messages: [{ ...snapshot.messages[0], id: "11" }],
    });
    const receipt = chat.store.getState().receivedAt["11"];
    chat.receive({ ...snapshot, messages: [], refresh: true } as HostEvent);
    const state = chat.store.getState();
    expect(state.messages).toHaveLength(0);
    expect(Object.values(state.pending)[0].status).toBe("sending");
    expect(state.filter).toBe("1");
    expect(receipt).toBeDefined();
    // A full (non-refresh) snapshot still settles "sending" as unknown.
    chat.receive(snapshot);
    expect(Object.values(chat.store.getState().pending)[0].status).toBe(
      "unknown",
    );
  });

  it("ignore commands go to the host; self is never ignored; the list comes back named", () => {
    const { chat, send } = ready();
    chat.ignore("1");
    expect(send).not.toHaveBeenCalled();
    chat.ignore("7");
    expect(send).toHaveBeenLastCalledWith({ type: "ignore", playerId: "7" });
    chat.receive(
      parseHostEvent(
        JSON.stringify({
          type: "ignored",
          players: [{ id: "7", name: "Страж" }],
        }),
      ),
    );
    expect(chat.store.getState().ignored).toEqual([{ id: "7", name: "Страж" }]);
    chat.unignore("7");
    expect(send).toHaveBeenLastCalledWith({ type: "unignore", playerId: "7" });
  });
});

describe("bridge contract", () => {
  it("requires the resolved name fields and validates the ignore list", () => {
    const players = (player: object) =>
      JSON.stringify({ type: "players", players: [player] });
    expect(() => parseHostEvent(players(lydia))).not.toThrow();
    expect(() =>
      parseHostEvent(players({ ...lydia, name: undefined })),
    ).toThrow();
    expect(() =>
      parseHostEvent(players({ ...lydia, inCharacter: "yes" })),
    ).toThrow();
    expect(() =>
      parseHostEvent(JSON.stringify({ type: "ignored", players: [{ id: 7 }] })),
    ).toThrow();
    expect(() =>
      parseHostEvent(
        JSON.stringify({
          type: "auth",
          authenticating: false,
          operation: "none",
          failure: "nameNotAllowed",
          error: "",
          savedLogin: false,
          savedUsername: "",
          phase: "disconnected",
        }),
      ),
    ).not.toThrow();
  });
});

describe("author menu and text filter", () => {
  it("opens only in the active chat, closes on deactivate and before a panel", () => {
    const { chat } = ready();
    chat.openAuthorMenu("7", "Лидия", 10, 20);
    expect(chat.store.getState().authorMenu).toBeNull();
    chat.receive({ type: "activate" });
    chat.openAuthorMenu("7", "Лидия", 10, 20);
    expect(chat.store.getState().authorMenu).toEqual({
      playerId: "7",
      name: "Лидия",
      x: 10,
      y: 20,
    });
    chat.open("profile", "7");
    expect(chat.store.getState().authorMenu).toBeNull();
    expect(chat.store.getState().selectedPlayer).toBe("7");
    chat.openAuthorMenu("7", "Лидия", 10, 20);
    chat.receive({ type: "deactivate" });
    expect(chat.store.getState().authorMenu).toBeNull();
  });

  it("sends the text filter with the display settings and accepts filtered rows", () => {
    const { chat, send } = ready();
    chat.configure({ textFilter: "mask" });
    expect(send).toHaveBeenLastCalledWith({
      type: "displaySettings",
      nameMode: "display",
      streamerMode: false,
      textFilter: "mask",
    });
    expect(settingsFrom({ textFilter: "stars" as never }).textFilter).toBe(
      "off",
    );
    const event = parseHostEvent(
      JSON.stringify({
        type: "messages",
        messages: [
          { ...snapshot.messages[0], id: "12", text: "***", filtered: true },
        ],
      }),
    );
    chat.receive(event);
    expect(chat.store.getState().messages.at(-1)?.filtered).toBe(true);
    expect(() =>
      parseHostEvent(
        JSON.stringify({
          type: "messages",
          messages: [{ ...snapshot.messages[0], filtered: "yes" }],
        }),
      ),
    ).toThrow();
  });
});
