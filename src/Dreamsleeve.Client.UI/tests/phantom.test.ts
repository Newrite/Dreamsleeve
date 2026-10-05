import { describe, expect, it, vi } from "vitest";
import { makeChat } from "../src/state/chat";
import { parseHostEvent } from "../src/bridge/parse";
import type { Command, HostEvent } from "../src/bridge/types";

const status: HostEvent = {
  type: "phantom",
  supported: true,
  recording: false,
  playing: false,
  ready: true,
  rate: 40,
  frames: 600,
  nodes: 120,
  bones: 80,
  seconds: 15,
  appearanceBytes: 4096,
  poseBytes: 5242880,
  buildMs: 12,
  sampleMs: 0.2,
  status: "Запись готова",
  exporting: false,
  exportPath: "",
  exportError: "",
  loading: false,
  replayChannels: 0,
  replayPoseBytes: 0,
  optimizedModelBytes: 0,
  removedGeometry: 0,
  loadedArchive: "",
};

describe("local phantom experiment", () => {
  it("accepts native status and keeps it separate from server state", () => {
    const chat = makeChat(() => true);
    chat.receive(parseHostEvent(JSON.stringify(status)));
    expect(chat.store.getState().phantom.ready).toBe(true);
    expect(chat.store.getState().connected).toBe(false);
    expect(() =>
      parseHostEvent(JSON.stringify({ ...status, rate: 100 })),
    ).toThrow();
    expect(() =>
      parseHostEvent(JSON.stringify({ ...status, poseBytes: -1 })),
    ).toThrow();
  });
  it("starts offline and releases input focus for recording and playback", () => {
    const send = vi.fn((_command: Command) => true);
    const chat = makeChat(send);
    chat.phantom("record", 40);
    expect(send.mock.calls.map(([command]) => command)).toEqual([
      {
        type: "phantom",
        action: "record",
        rate: 40,
        scenario: "mixed",
        poseMode: "full",
        modelMode: "original",
      },
      { type: "close" },
    ]);
    send.mockClear();
    chat.phantom("clear");
    expect(send).toHaveBeenCalledExactlyOnceWith({
      type: "phantom",
      action: "clear",
      rate: 40,
      scenario: "mixed",
      poseMode: "full",
      modelMode: "original",
    });
  });
  it("keeps the selected menu and sample rate across closing and native resets", () => {
    const send = vi.fn((_command: Command) => true);
    const chat = makeChat(send);
    chat.open("settings");
    chat.setPhantomRate(40);
    chat.receive({ type: "deactivate" });
    chat.receive({ type: "activate" });
    chat.openMenu();
    expect(chat.store.getState().panel).toBe("settings");
    chat.receive({ ...status, rate: 20, ready: false });
    expect(chat.store.getState().phantomRate).toBe(40);
    chat.phantom("query");
    chat.phantom("record");
    expect(send).toHaveBeenCalledWith({
      type: "phantom",
      action: "record",
      rate: 40,
      scenario: "mixed",
      poseMode: "full",
      modelMode: "original",
    });
    chat.open(null);
    chat.openMenu();
    expect(chat.store.getState().panel).toBe("settings");
  });
  it("keeps scenario preferences and receives archive progress independently of playback", () => {
    const send = vi.fn((_command: Command) => true);
    const chat = makeChat(send);
    chat.setPhantomScenario("equipment");
    chat.receive({
      ...status,
      exporting: true,
      exportPath: "",
      exportError: "",
      loading: false,
      replayChannels: 0,
      replayPoseBytes: 0,
      optimizedModelBytes: 0,
      removedGeometry: 0,
      loadedArchive: "",
    });
    expect(chat.store.getState().phantom.exporting).toBe(true);
    chat.receive({ ...status, exportPath: "C:/SKSE/DreamsleevePhantoms/test" });
    expect(chat.store.getState().phantom.exportPath).toContain(
      "DreamsleevePhantoms",
    );
    chat.phantom("record");
    expect(send).toHaveBeenCalledWith({
      type: "phantom",
      action: "record",
      rate: 20,
      scenario: "equipment",
      poseMode: "full",
      modelMode: "original",
    });
    expect(chat.store.getState().phantomScenario).toBe("equipment");
    expect(() =>
      parseHostEvent(JSON.stringify({ ...status, exporting: "true" })),
    ).toThrow();
  });
  it("reports a missing native bridge without closing the menu", () => {
    const send = vi.fn(() => false);
    const chat = makeChat(send);
    chat.phantom("record");
    expect(send).toHaveBeenCalledTimes(1);
    expect(chat.store.getState().phantom.status).toContain("не принята");
  });
  it("loads an existing archive without closing the menu and retains comparison choices", () => {
    const send = vi.fn((_command: Command) => true);
    const chat = makeChat(send);
    chat.setPhantomScenario("camera");
    chat.setPhantomPoseMode("quantized");
    chat.setPhantomModelMode("pruned");
    chat.phantom("load");
    expect(send).toHaveBeenCalledExactlyOnceWith({
      type: "phantom",
      action: "load",
      rate: 20,
      scenario: "camera",
      poseMode: "quantized",
      modelMode: "pruned",
    });
    chat.receive({ ...status, loading: true, ready: false });
    chat.receive({
      ...status,
      loadedArchive: "C:/SKSE/DreamsleevePhantoms/camera",
      replayChannels: 327,
    });
    expect(chat.store.getState().phantomPoseMode).toBe("quantized");
    expect(chat.store.getState().phantomModelMode).toBe("pruned");
    send.mockClear();
    chat.phantom("play");
    expect(send.mock.calls.at(-1)?.[0]).toEqual({ type: "close" });
    expect(send.mock.calls[0][0]).toMatchObject({
      action: "play",
      poseMode: "quantized",
      modelMode: "pruned",
    });
    expect(() =>
      parseHostEvent(JSON.stringify({ ...status, loading: "yes" })),
    ).toThrow();
    expect(() =>
      parseHostEvent(JSON.stringify({ ...status, replayChannels: -1 })),
    ).toThrow();
  });
});
