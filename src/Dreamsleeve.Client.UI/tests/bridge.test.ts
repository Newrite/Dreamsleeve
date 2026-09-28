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
});
