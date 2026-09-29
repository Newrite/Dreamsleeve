import { describe, it, expect, vi } from "vitest";
import { parseHostEvent } from "../src/bridge/parse";
import {
  makeChat,
  visible,
  PENDING_LIMIT,
  PENDING_TIMEOUT,
} from "../src/state/chat";
import { defaults, settingsFrom } from "../src/state/settings";
import type {
  Announcement,
  Channel,
  Command,
  HostEvent,
  Message,
  Player,
  Settings,
} from "../src/bridge/types";
const author: Player = {
  id: "2",
  name: "Мира",
  inCharacter: true,
  displayName: "Мира",
  username: "mira",
};
const announcement = (
  id: string,
  a: Announcement = { origin: "server", kind: "announcement" },
  extra: Partial<Message> = {},
): Message =>
  ({
    id,
    channelId: "9",
    source: "system",
    text: "Объявление",
    time: 0,
    announcement: a,
    ...extra,
  }) as Message;
const chatLine = (id: string, channelId = "1"): Message => ({
  id,
  channelId,
  source: "player",
  author,
  text: "Привет",
  time: 0,
});
// The system channel is found by its kind, whatever its ID.
const channels: Channel[] = [
  { id: "1", name: "Общий", kind: "global", writable: true },
  { id: "9", name: "Объявления", kind: "system", writable: false },
];
const snapshot: HostEvent = {
  type: "snapshot",
  serverName: "Голоса Тамриэля",
  channels: [
    { id: "1", name: "Общий", kind: "global", writable: true },
    { id: "2", name: "Группа", kind: "party", writable: true },
    {
      id: "9",
      name: "Объявления",
      kind: "system",
      writable: false,
    },
  ],
  messages: [],
  players: [],
  selfId: "7",
};
function ready(now = () => Date.now()) {
  const send = vi.fn((_command: Command) => true);
  const chat = makeChat(send, now);
  chat.receive(snapshot);
  return { chat, send };
}
const parse = (value: unknown) => parseHostEvent(JSON.stringify(value));

describe("announcement bridge", () => {
  it("accepts every origin and kind, a signature and a posting author", () => {
    const event = {
      type: "messages",
      messages: [
        announcement("1", { origin: "server", kind: "admin" }),
        announcement(
          "2",
          { origin: "trustedClient", kind: "event" },
          {
            author,
          },
        ),
        announcement(
          "3",
          { origin: "thirdParty", kind: "periodic", signature: "Мод «Кареты»" },
          { author },
        ),
        announcement("4", { origin: "thirdParty", kind: "announcement" }),
      ],
    };
    expect(parse(event)).toEqual(event);
  });
  it("rejects unknown origins or kinds, bad signatures and authors", () => {
    const valid = announcement("1", { origin: "server", kind: "event" });
    for (const broken of [
      { ...valid, announcement: { origin: "admin", kind: "event" } },
      { ...valid, announcement: { origin: "server", kind: "news" } },
      { ...valid, announcement: { origin: "server" } },
      { ...valid, announcement: "server" },
      { ...valid, announcement: undefined },
      {
        ...valid,
        announcement: { origin: "thirdParty", kind: "event", signature: 5 },
      },
      {
        ...valid,
        announcement: {
          origin: "thirdParty",
          kind: "event",
          signature: "x".repeat(129),
        },
      },
      {
        ...valid,
        announcement: {
          origin: "thirdParty",
          kind: "event",
          signature: "Мод\nСервер",
        },
      },
      {
        ...valid,
        announcement: {
          origin: "thirdParty",
          kind: "event",
          signature: "Мод\u0085",
        },
      },
      { ...valid, author: { id: "2", name: "Мира" } },
      { ...chatLine("9"), announcement: { origin: "server", kind: "event" } },
    ])
      expect(() => parse({ type: "messages", messages: [broken] })).toThrow();
    expect(
      parse({
        type: "messages",
        messages: [
          {
            ...valid,
            announcement: {
              origin: "thirdParty",
              kind: "event",
              signature: "x".repeat(128),
            },
          },
        ],
      }),
    ).toMatchObject({ type: "messages" });
  });
  it("validates announcementResult", () => {
    const event = {
      type: "announcementResult",
      channelId: "9",
      source: "Carriage Tours",
      text: "Карета отправляется",
      error: "Слишком часто",
    };
    expect(parse(event)).toEqual(event);
    for (const broken of [
      { ...event, channelId: undefined },
      { ...event, source: undefined },
      { ...event, source: "x".repeat(129) },
      { ...event, source: "a\u0007b" },
      { ...event, text: 1 },
      { ...event, text: "x".repeat(16001) },
      { ...event, error: undefined },
      { ...event, error: "x".repeat(513) },
    ])
      expect(() => parse(broken)).toThrow();
  });
});

describe("announcement settings", () => {
  const keys = {
    announcementChannels: "all",
    announcementsServer: true,
    announcementsTrustedClient: true,
    announcementsThirdParty: true,
    announcementsEvents: true,
    announcementsPeriodic: true,
  };
  it("defaults apply to settings saved before announcements existed", () => {
    expect(defaults).toMatchObject(keys);
    const old = settingsFrom({ fontSize: 20, theme: "contrast" });
    expect(old).toMatchObject({ ...keys, fontSize: 20, theme: "contrast" });
  });
  it("accepts known values only", () => {
    expect(
      settingsFrom({
        announcementChannels: "current",
        announcementsServer: false,
        announcementsPeriodic: false,
      }),
    ).toMatchObject({
      announcementChannels: "current",
      announcementsServer: false,
      announcementsPeriodic: false,
    });
    expect(
      settingsFrom({
        announcementChannels: "everywhere" as "all",
        announcementsThirdParty: "no" as unknown as boolean,
        announcementsEvents: 0 as unknown as boolean,
      }),
    ).toMatchObject(keys);
  });
});

describe("announcement visibility", () => {
  const server = announcement("s", { origin: "server", kind: "announcement" });
  it("places announcements by announcementChannels", () => {
    const matrix: [Settings["announcementChannels"], string, boolean][] = [
      ["tab", "9", true],
      ["tab", "all", false],
      ["tab", "1", false],
      ["all", "9", true],
      ["all", "all", true],
      ["all", "1", false],
      ["current", "9", true],
      ["current", "all", true],
      ["current", "1", true],
    ];
    for (const [announcementChannels, filter, expected] of matrix) {
      const s = { ...defaults, announcementChannels };
      expect(visible(server, filter, s, channels)).toBe(expected);
      // Ordinary lines never move.
      expect(visible(chatLine("p"), filter, s, channels)).toBe(
        filter === "all" || filter === "1",
      );
    }
  });
  it("origin and kind switches hide everywhere, the own tab included", () => {
    const lines = {
      server,
      trusted: announcement("t", {
        origin: "trustedClient",
        kind: "announcement",
      }),
      mod: announcement("m", { origin: "thirdParty", kind: "admin" }),
      event: announcement("e", { origin: "server", kind: "event" }),
      periodic: announcement("p", { origin: "server", kind: "periodic" }),
      modEvent: announcement("me", { origin: "thirdParty", kind: "event" }),
    };
    const cases: [Partial<Settings>, (keyof typeof lines)[]][] = [
      [{}, ["server", "trusted", "mod", "event", "periodic", "modEvent"]],
      [{ announcementsServer: false }, ["trusted", "mod", "modEvent"]],
      [
        { announcementsTrustedClient: false },
        ["server", "mod", "event", "periodic", "modEvent"],
      ],
      [
        { announcementsThirdParty: false },
        ["server", "trusted", "event", "periodic"],
      ],
      [
        { announcementsEvents: false },
        ["server", "trusted", "mod", "periodic"],
      ],
      [
        { announcementsPeriodic: false },
        ["server", "trusted", "mod", "event", "modEvent"],
      ],
    ];
    for (const announcementChannels of ["tab", "all", "current"] as const)
      for (const [patch, shown] of cases) {
        const s = { ...defaults, announcementChannels, ...patch };
        for (const filter of ["9", "all", "1"])
          for (const [name, line] of Object.entries(lines))
            expect(visible(line, filter, s, channels)).toBe(
              shown.includes(name as keyof typeof lines) &&
                (filter === "9" ||
                  announcementChannels === "current" ||
                  (filter === "all" && announcementChannels === "all")),
            );
      }
  });
  it("hidden announcements never count as unread", () => {
    const { chat } = ready();
    chat.configure({ announcementsThirdParty: false });
    chat.receive({
      type: "messages",
      messages: [
        announcement("m", { origin: "thirdParty", kind: "event" }),
        announcement("s", { origin: "server", kind: "event" }),
      ],
    });
    expect(chat.store.getState().unread["9"]).toBe(1);
    chat.configure({ announcementsEvents: false });
    chat.receive({
      type: "messages",
      messages: [announcement("e", { origin: "server", kind: "event" })],
    });
    expect(chat.store.getState().unread["9"]).toBe(1);
  });
  it("unread follows where announcements are shown", () => {
    const { chat } = ready();
    chat.receive({ type: "activate" });
    chat.configure({ announcementChannels: "tab" });
    chat.receive({ type: "messages", messages: [announcement("1")] });
    // Not shown in "Все": the tab badge grows.
    expect(chat.store.getState().unread["9"]).toBe(1);
    chat.read();
    expect(chat.store.getState().unread["9"]).toBe(1);
    chat.configure({ announcementChannels: "current" });
    chat.select("2");
    chat.read();
    expect(chat.store.getState().unread["9"]).toBe(0);
    chat.receive({ type: "messages", messages: [announcement("2")] });
    expect(chat.store.getState().unread["9"] ?? 0).toBe(0);
    chat.configure({ announcementChannels: "all" });
    chat.receive({ type: "messages", messages: [announcement("3")] });
    expect(chat.store.getState().unread["9"]).toBe(1);
    chat.select("all");
    chat.read();
    expect(chat.store.getState().unread["9"]).toBe(0);
  });
});

describe("refused announcements of other mods", () => {
  const refusal = (text = "Карета"): HostEvent => ({
    type: "announcementResult",
    channelId: "9",
    source: "Carriage Tours",
    text,
    error: "Слишком часто",
  });
  it("shows a failed row without retry that expires like any failed row", () => {
    let now = 1000;
    const { chat, send } = ready(() => now);
    chat.receive(refusal());
    const [[id, row]] = Object.entries(chat.store.getState().pending);
    expect(row).toMatchObject({
      channelId: "9",
      status: "failed",
      error: "Слишком часто",
      external: "Carriage Tours",
      since: 1000,
    });
    chat.receive({ type: "activate" });
    chat.retry(id);
    expect(send).not.toHaveBeenCalled();
    expect(chat.store.getState().pending[id]).toBeDefined();
    chat.store.setState({ active: false });
    now = 1000 + PENDING_TIMEOUT - 1;
    chat.expirePending();
    expect(chat.store.getState().pending[id]).toBeDefined();
    now = 1000 + PENDING_TIMEOUT;
    chat.expirePending();
    expect(chat.store.getState().pending).toEqual({});
    chat.receive(refusal());
    chat.dismiss(Object.keys(chat.store.getState().pending)[0]);
    expect(chat.store.getState().pending).toEqual({});
    // Like messages, a refusal of an unknown channel is not shown.
    chat.receive({ ...refusal(), channelId: "404" } as HostEvent);
    expect(chat.store.getState().pending).toEqual({});
  });
  it("keeps at most the limit, drops the oldest refusals first and never blocks sending", () => {
    let now = 0;
    const { chat, send } = ready(() => now);
    for (let i = 0; i < 20; i++) {
      now = i;
      chat.receive(refusal(String(i)));
    }
    const texts = () =>
      Object.values(chat.store.getState().pending).map((p) => p.text);
    expect(texts()).toHaveLength(PENDING_LIMIT);
    expect(texts()).toEqual(
      Array.from({ length: PENDING_LIMIT }, (_, i) => String(i + 4)),
    );
    chat.receive({ type: "activate" });
    chat.setDraft("Моё");
    chat.submit();
    expect(send.mock.calls[0][0]).toMatchObject({ type: "sendChat" });
    expect(texts()).toHaveLength(PENDING_LIMIT);
    expect(texts()).toContain("Моё");
    expect(texts()).not.toContain("4");
  });
});
