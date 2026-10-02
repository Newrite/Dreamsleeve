import { describe, expect, it } from "vitest";
import { readFileSync, writeFileSync } from "node:fs";
import { parseHostEvent } from "../src/bridge/parse";
import { commandTypes, eventTypes } from "../src/bridge/bridge.generated";
import { defaults } from "../src/bridge/settings.generated";
import type { Command } from "../src/bridge/types";

// Both sides of the bridge check each other through tests/contract: the native
// test "Every host event has a sample for the web UI parser" writes events.json,
// this file writes commands.json for "Every web UI command sample is accepted".
const contract = (name: string) =>
  new URL(`./contract/${name}`, import.meta.url);
const read = (name: string) =>
  readFileSync(contract(name), "utf8").replaceAll("\r", "");

// One typed sample per command: a new command does not compile without one.
const commands: { [K in Command["type"]]: Extract<Command, { type: K }> } = {
  sendChat: {
    type: "sendChat",
    requestId: "u1",
    channelId: "1",
    text: "Привет, Вайтран",
  },
  close: { type: "close" },
  saveSettings: { type: "saveSettings", settings: defaults, revision: 3 },
  signIn: {
    type: "signIn",
    username: "seven",
    password: "correct horse battery",
    remember: true,
    displayName: "Seven",
  },
  ignore: { type: "ignore", playerId: "7" },
  unignore: { type: "unignore", playerId: "7" },
  displaySettings: {
    type: "displaySettings",
    settings: { ...defaults, streamerMode: true },
  },
  signInSaved: { type: "signInSaved" },
  signOut: { type: "signOut" },
  forgetLogin: { type: "forgetLogin" },
  disconnect: { type: "disconnect" },
  placeGroundNote: {
    type: "placeGroundNote",
    requestId: "n1",
    text: "Осторожно, тролль",
  },
  removeGroundMark: {
    type: "removeGroundMark",
    requestId: "r1",
    markId: "18446744073709551615",
  },
  setIdentityVisibility: {
    type: "setIdentityVisibility",
    hiding: "exceptGroundMarks",
  },
  changeDisplayName: { type: "changeDisplayName", displayName: "Новое имя" },
  sanctionPlayer: {
    type: "sanctionPlayer",
    requestId: "m1",
    playerId: "7",
    kind: "mute",
    minutes: 60,
    reason: "Флуд",
  },
  liftSanction: {
    type: "liftSanction",
    requestId: "m2",
    playerId: "7",
    kind: "ban",
  },
  kickPlayer: {
    type: "kickPlayer",
    requestId: "m3",
    playerId: "7",
    reason: "Остынь",
  },
  listSanctions: { type: "listSanctions", requestId: "m4" },
  listPlayerMarks: { type: "listPlayerMarks", requestId: "m5", playerId: "7" },
  clearPlayerMarks: {
    type: "clearPlayerMarks",
    requestId: "m6",
    playerId: "7",
    notes: false,
    deaths: true,
  },
  deleteChatMessage: {
    type: "deleteChatMessage",
    requestId: "m7",
    channelId: "1",
    messageId: "11",
  },
  resetPassword: {
    type: "resetPassword",
    code: "one-time-code",
    password: "correct horse battery",
  },
  signInSteam: { type: "signInSteam", remember: true },
  copySteamLink: { type: "copySteamLink" },
};

describe("bridge contract with the host", () => {
  it("parses every event sample the host writes", () => {
    const samples: unknown[] = JSON.parse(read("events.json"));
    const types = samples.map(
      (sample) => parseHostEvent(JSON.stringify(sample)).type,
    );
    expect(types).toEqual([...eventTypes]);
  });
  it("writes one sample of every command for the host", () => {
    expect(Object.keys(commands)).toEqual([...commandTypes]);
    const text = JSON.stringify(Object.values(commands), null, 2) + "\n";
    if (process.env.DREAMSLEEVE_WRITE_GENERATED)
      writeFileSync(contract("commands.json"), text);
    expect(read("commands.json")).toBe(text);
  });
});
