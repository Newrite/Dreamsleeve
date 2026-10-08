import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { readFileSync } from "node:fs";
import { makeChat } from "../src/state/chat";
import type { HostEvent } from "../src/bridge/types";

const badInputNotice = "Ошибка данных интерфейса. Ожидается новый снимок.";

beforeEach(() => {
  vi.resetModules();
  vi.stubGlobal("window", {});
  vi.useFakeTimers();
  vi.setSystemTime(1000);
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe("Prisma host receive boundary", () => {
  it.each([
    ["malformed JSON", "{"],
    ["invalid schema", '{"type":"connection","connected":"yes"}'],
    ["unknown event", '{"type":"new"}'],
    ["oversized payload", " ".repeat(8 * 1024 * 1024 + 1)],
  ])(
    "rejects %s with the existing notice and preserves state",
    async (_name, payload) => {
      const { chat } = await import("../src/bridge/prisma");
      chat.store.setState({
        notice: "Предыдущее уведомление",
        drafts: { "1": "Черновик" },
      });
      const before = chat.store.getState();
      const receive = vi.spyOn(chat, "receive");

      window.dreamsleeveReceive!(payload);

      expect(receive).not.toHaveBeenCalled();
      expect(chat.store.getState()).toEqual({
        ...before,
        notice: badInputNotice,
      });
    },
  );

  it("applies every native event sample with unchanged state behavior", async () => {
    const { chat } = await import("../src/bridge/prisma");
    const reference = makeChat(() => false);
    const samples: HostEvent[] = JSON.parse(
      readFileSync(new URL("./contract/events.json", import.meta.url), "utf8"),
    );

    for (const event of samples) {
      reference.receive(event);
      window.dreamsleeveReceive!(JSON.stringify(event));
      expect(chat.store.getState(), event.type).toEqual(
        reference.store.getState(),
      );
    }
  });

  it("lets a downstream state failure surface without relabeling host input", async () => {
    const { chat } = await import("../src/bridge/prisma");
    const before = chat.store.getState();
    const failure = new Error("State handler failed");
    vi.spyOn(chat, "receive").mockImplementation(() => {
      throw failure;
    });

    expect(() => window.dreamsleeveReceive!('{"type":"show"}')).toThrow(
      failure,
    );
    expect(chat.store.getState()).toBe(before);
  });
});
