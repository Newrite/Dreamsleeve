import { describe, it, expect } from "vitest";
import { parseHostEvent } from "../src/bridge/parse";
describe("native bridge", () => {
  it("rejects unknown or malformed events without entering the store", () => {
    for (const value of [
      null,
      { type: "new" },
      { type: "messages", messages: [{}] },
      {
        type: "snapshot",
        selfId: 123,
        channels: [],
        players: [],
        messages: [],
      },
      { type: "connection", connected: "yes" },
    ])
      expect(() => parseHostEvent(JSON.stringify(value))).toThrow();
  });
  it("allows system messages without a player and never coerces uint64 identifiers", () => {
    const event = {
      type: "messages",
      messages: [
        {
          id: "18446744073709551615",
          channelId: "system",
          source: "system",
          announcement: { origin: "server", kind: "announcement" },
          text: "Объявление",
          time: 0,
        },
      ],
    };
    expect(parseHostEvent(JSON.stringify(event))).toEqual(event);
    expect(() =>
      parseHostEvent(
        JSON.stringify({
          ...event,
          messages: [{ ...event.messages[0], id: 1 }],
        }),
      ),
    ).toThrow();
  });
  it("accepts the typed auth event and rejects unknown codes or oversized text", () => {
    const event = {
      type: "auth",
      authenticating: false,
      operation: "passwordLogin",
      failure: "invalidCredentials",
      error: "Пароль не подошёл",
      savedLogin: true,
      savedUsername: "northern",
      phase: "disconnected",
    };
    expect(parseHostEvent(JSON.stringify(event))).toEqual(event);
    expect(
      parseHostEvent(
        JSON.stringify({
          ...event,
          error: "x".repeat(512),
          savedUsername: "",
          phase: "connected",
        }),
      ),
    ).toMatchObject({ type: "auth", phase: "connected" });
    for (const broken of [
      { ...event, operation: "register" },
      { ...event, failure: "unknown" },
      { ...event, phase: "online" },
      { ...event, authenticating: "yes" },
      { ...event, savedLogin: 1 },
      { ...event, error: "x".repeat(513) },
      { ...event, savedUsername: "x".repeat(513) },
      { ...event, error: undefined },
      { ...event, savedUsername: null },
      { ...event, phase: undefined },
    ])
      expect(() => parseHostEvent(JSON.stringify(broken))).toThrow();
  });
  it("rejects a writable system channel", () => {
    expect(() =>
      parseHostEvent(
        JSON.stringify({
          type: "snapshot",
          channels: [
            { id: "sys", kind: "system", name: "System", writable: true },
          ],
          selfId: "1",
          players: [],
          messages: [],
        }),
      ),
    ).toThrow();
  });
  it("a snapshot carries up to 500 lines of every channel", () => {
    const line = (channelId: string) => ({
      id: "1",
      channelId,
      source: "system",
      announcement: { origin: "server", kind: "announcement" },
      text: "Объявление",
      time: 0,
    });
    const snapshot = (count: number) =>
      JSON.stringify({
        type: "snapshot",
        channels: [
          { id: "1", kind: "global", name: "Общий", writable: true },
          { id: "2", kind: "system", name: "Объявления", writable: false },
        ],
        selfId: "1",
        serverName: "",
        players: [],
        messages: Array.from({ length: count }, () => line("2")),
      });
    expect(parseHostEvent(snapshot(1000))).toMatchObject({ type: "snapshot" });
    expect(() => parseHostEvent(snapshot(1001))).toThrow();
  });
});
describe("ground mark events", () => {
  it("accepts own marks and results, rejects wrong kinds and ambiguous results", () => {
    const place = { location: "skyrim.esm:01A26F", x: 1.5, y: -2, z: 0 };
    const marks = {
      type: "groundMarks",
      marks: [
        {
          id: "18446744073709551615",
          kind: "death",
          text: "",
          time: 0,
          character: "Довакин",
          ...place,
        },
      ],
    };
    expect(parseHostEvent(JSON.stringify(marks))).toEqual(marks);
    const nearby = {
      type: "nearbyMarks",
      marks: [
        { id: "2", kind: "note", text: "x", time: 1, author: "Мира", ...place },
      ],
    };
    expect(parseHostEvent(JSON.stringify(nearby))).toEqual(nearby);
    for (const broken of [
      { id: "1", kind: "sign", text: "", time: 0, ...place },
      { id: "1", kind: "note", text: "", time: 0 },
      { id: "1", kind: "note", text: "", time: 0, ...place, x: "1" },
      { id: "1", kind: "note", text: "", time: 0, ...place, author: 5 },
    ])
      expect(() =>
        parseHostEvent(
          JSON.stringify({ type: "groundMarks", marks: [broken] }),
        ),
      ).toThrow();
    for (const result of [
      { type: "markResult", requestId: "1", markId: "5" },
      { type: "markResult", requestId: "1", markId: "5", evictedId: "4" },
      { type: "markResult", requestId: "1", removed: true },
      { type: "markResult", requestId: "1", error: "Нет" },
    ])
      expect(parseHostEvent(JSON.stringify(result))).toEqual(result);
    for (const broken of [
      { type: "markResult", requestId: "1" },
      { type: "markResult", requestId: "1", markId: "5", error: "Нет" },
      { type: "markResult", requestId: "1", removed: true, error: "Нет" },
      { type: "markResult", requestId: "1", markId: 5 },
    ])
      expect(() => parseHostEvent(JSON.stringify(broken))).toThrow();
    const snapshot = {
      type: "snapshot",
      channels: [],
      selfId: "1",
      serverName: "",
      players: [],
      messages: [],
      groundMarksSupported: true,
      groundMarks: [{ id: "2", kind: "note", text: "x", time: 1, ...place }],
      nearbyMarks: [
        {
          id: "3",
          kind: "note",
          text: "y",
          time: 1,
          gameDate: "Тирдас, 17 Последнего зерна 4Э 201, 14:05",
          ...place,
        },
      ],
    };
    expect(parseHostEvent(JSON.stringify(snapshot))).toEqual(snapshot);
    expect(() =>
      parseHostEvent(JSON.stringify({ ...snapshot, groundMarksSupported: 1 })),
    ).toThrow();
  });
});
