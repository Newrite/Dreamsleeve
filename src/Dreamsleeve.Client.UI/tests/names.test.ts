import { describe, it, expect, vi } from "vitest";
import { makeChat } from "../src/state/chat";
import { defaults } from "../src/bridge/settings.generated";
import {
  characterLine,
  HIDDEN_NAME,
  playerName,
  realNames,
} from "../src/state/names";
import { identityStatus } from "../src/state/identity";
import { hueColor, hueOf, nameColorPalette } from "../src/state/nameColor";
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

  it("sends name settings at once and keeps the local choice over the refresh", () => {
    const { chat, send } = ready();
    chat.configure({ streamerMode: true });
    expect(send).toHaveBeenLastCalledWith({
      type: "displaySettings",
      settings: { ...defaults, streamerMode: true },
    });
    chat.receive({ ...snapshot, refresh: true } as HostEvent);
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
    const receipt = chat.store.getState().receivedAt["1:11"];
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
          registration: "open",
          steam: false,
          browserFailed: false,
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
      pseudonymous: false,
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
      settings: { ...defaults, textFilter: "mask" },
    });
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

describe("hidden identity", () => {
  const hidden: Player = {
    id: "9",
    name: "Страж 2",
    inCharacter: false,
    displayName: "Страж 2",
    username: "",
    pseudonymous: true,
  };
  it("shows a pseudonymous player by the server pseudonym and no real names", () => {
    expect(playerName(hidden, defaults)).toBe("Страж 2");
    expect(realNames(hidden, defaults)).toBeUndefined();
    expect(characterLine(hidden, defaults)).toBe("Имя скрыто игроком");
    expect(realNames(lydia, defaults)?.username).toBe("lydia");
    expect(() =>
      parseHostEvent(
        JSON.stringify({
          type: "players",
          players: [{ ...hidden, username: "leak" }],
        }),
      ),
    ).toThrow();
    expect(() =>
      parseHostEvent(
        JSON.stringify({
          type: "players",
          players: [{ ...hidden, character: "Leak" }],
        }),
      ),
    ).toThrow();
    expect(
      parseHostEvent(JSON.stringify({ type: "players", players: [hidden] })),
    ).toEqual({ type: "players", players: [hidden] });
  });

  it("describes the choice without claiming a hidden name before the server answers", () => {
    expect(
      identityStatus({ mode: "everywhere", pending: true }, "connected"),
    ).toBe("Ожидание сервера…");
    expect(
      identityStatus(
        { mode: "everywhere", pending: false, pseudonym: "Страж 2" },
        "connected",
      ),
    ).toBe("Другие видят вас как „Страж 2“");
    expect(
      identityStatus(
        { mode: "exceptGroundMarks", pending: false, pseudonym: "Страж 2" },
        "connected",
      ),
    ).toBe("Другие видят вас как „Страж 2“; в метках на земле — ваше имя");
    expect(
      identityStatus({ mode: "everywhere", pending: false }, "opening"),
    ).toBe("Ожидание сервера…");
    expect(
      identityStatus(
        { mode: "exceptGroundMarks", pending: false },
        "disconnected",
      ),
    ).toBe("Имя будет скрыто при следующем входе на сервер");
    expect(identityStatus({ mode: "off", pending: false }, "connected")).toBe(
      "Другие игроки видят ваше имя",
    );
  });

  it("sends the choice and keeps the saved one until the server answers", () => {
    const { chat, send } = ready();
    chat.receive({ type: "connection", connected: true, phase: "connected" });
    chat.setHideIdentity("exceptGroundMarks");
    expect(send).toHaveBeenLastCalledWith({
      type: "setIdentityVisibility",
      hiding: "exceptGroundMarks",
    });
    expect(chat.store.getState().identity).toMatchObject({
      mode: "exceptGroundMarks",
      pending: true,
    });
    // One switch at a time.
    chat.setHideIdentity("off");
    expect(send).toHaveBeenCalledTimes(1);
    chat.receive(
      parseHostEvent(
        JSON.stringify({
          type: "identity",
          mode: "exceptGroundMarks",
          pending: false,
          pseudonym: "Страж 2",
        }),
      ),
    );
    expect(chat.store.getState().identity.pseudonym).toBe("Страж 2");
    // Choosing the current mode sends nothing.
    chat.setHideIdentity("exceptGroundMarks");
    expect(send).toHaveBeenCalledTimes(1);
    // A refusal returns the choice to the server's state with the reason.
    chat.setHideIdentity("off");
    chat.receive({
      type: "identity",
      mode: "exceptGroundMarks",
      pending: false,
      pseudonym: "Страж 2",
      error: "Слишком часто",
    });
    expect(chat.store.getState().identity).toEqual({
      mode: "exceptGroundMarks",
      pending: false,
      pseudonym: "Страж 2",
      error: "Слишком часто",
    });
    for (const mode of ["sometimes", true])
      expect(() =>
        parseHostEvent(
          JSON.stringify({ type: "identity", mode, pending: false }),
        ),
      ).toThrow();
  });

  it("without a session only the next choice changes, without waiting", () => {
    const send = vi.fn((_command: Command) => true);
    const chat = makeChat(send);
    chat.setHideIdentity("everywhere");
    expect(chat.store.getState().identity.pending).toBe(false);
    expect(send).toHaveBeenCalledWith({
      type: "setIdentityVisibility",
      hiding: "everywhere",
    });
  });

  it("asks the server for a new display name, one at a time, and shows its answer", () => {
    const { chat, send } = ready();
    // Outside a session nothing is sent.
    chat.receive({
      type: "connection",
      connected: false,
      phase: "disconnected",
    });
    send.mockClear();
    chat.changeDisplayName("Новое Имя");
    expect(send).not.toHaveBeenCalled();
    chat.receive({ type: "connection", connected: true, phase: "connected" });
    send.mockClear();
    chat.changeDisplayName("   ");
    expect(send).not.toHaveBeenCalled();
    chat.changeDisplayName("  Новое Имя ");
    expect(send).toHaveBeenLastCalledWith({
      type: "changeDisplayName",
      displayName: "Новое Имя",
    });
    expect(chat.store.getState().displayName).toEqual({ pending: true });
    chat.changeDisplayName("Другое");
    expect(send).toHaveBeenCalledTimes(1);
    chat.receive(
      parseHostEvent(
        JSON.stringify({
          type: "displayName",
          pending: false,
          changed: "Новое Имя",
        }),
      ),
    );
    expect(chat.store.getState().displayName).toEqual({
      pending: false,
      changed: "Новое Имя",
    });
    chat.receive({
      type: "displayName",
      pending: false,
      error: "Имя можно сменить снова через 90 мин",
    });
    expect(chat.store.getState().displayName.error).toBe(
      "Имя можно сменить снова через 90 мин",
    );
    for (const pending of ["yes", undefined])
      expect(() =>
        parseHostEvent(JSON.stringify({ type: "displayName", pending })),
      ).toThrow();
  });
});

describe("name color", () => {
  it("asks the server for a color, one at a time, and shows its answer", () => {
    const { chat, send } = ready();
    chat.receive({ type: "connection", connected: true, phase: "connected" });
    send.mockClear();
    chat.setNameColor("#e57373");
    expect(send).toHaveBeenLastCalledWith({
      type: "setNameColor",
      color: "#E57373",
    });
    expect(chat.store.getState().nameColor).toEqual({ pending: true });
    chat.setNameColor("#4FC3F7");
    expect(send).toHaveBeenCalledTimes(1);
    chat.receive(
      parseHostEvent(
        JSON.stringify({
          type: "nameColor",
          pending: false,
          changed: "#E57373",
        }),
      ),
    );
    expect(chat.store.getState().nameColor).toEqual({
      pending: false,
      changed: "#E57373",
    });
    for (const changed of ["red", "#E5737", 7])
      expect(() =>
        parseHostEvent(
          JSON.stringify({ type: "nameColor", pending: false, changed }),
        ),
      ).toThrow();
  });

  it("an author carries a #RRGGBB color; a pseudonymous one never does", () => {
    const parsePlayers = (player: object) =>
      parseHostEvent(JSON.stringify({ type: "players", players: [player] }));
    const colored = { ...lydia, color: "#FFD54F" };
    expect(parsePlayers(colored)).toEqual({
      type: "players",
      players: [colored],
    });
    expect(() => parsePlayers({ ...lydia, color: "gold" })).toThrow();
    const hidden = {
      ...lydia,
      username: "",
      character: undefined,
      pseudonymous: true,
    };
    expect(() => parsePlayers({ ...hidden, color: "#FFD54F" })).toThrow();
    expect(parsePlayers(hidden)).toBeTruthy();
  });

  it("every offered color and every hue of the slider reads by the server's rule", () => {
    const luminance = (color: string) => {
      const value = Number.parseInt(color.slice(1), 16);
      const linear = (channel: number) => {
        const share = channel / 255;
        return share <= 0.04045
          ? share / 12.92
          : ((share + 0.055) / 1.055) ** 2.4;
      };
      return (
        0.2126 * linear((value >> 16) & 255) +
        0.7152 * linear((value >> 8) & 255) +
        0.0722 * linear(value & 255)
      );
    };
    for (const color of nameColorPalette)
      expect(luminance(color)).toBeGreaterThanOrEqual(0.15);
    for (let hue = 0; hue < 360; hue++) {
      const color = hueColor(hue);
      expect(color).toMatch(/^#[0-9A-F]{6}$/);
      expect(luminance(color)).toBeGreaterThanOrEqual(0.15);
      expect(Math.abs(hueOf(color) - hue) % 359).toBeLessThanOrEqual(2);
    }
    expect(hueOf("#808080")).toBe(0);
  });
});
