import type { HostEvent } from "./types";
type ObjectValue = Record<string, unknown>;
const object = (v: unknown): v is ObjectValue =>
  v !== null && typeof v === "object" && !Array.isArray(v);
const text = (v: unknown): v is string => typeof v === "string";
const finite = (v: unknown) => typeof v === "number" && Number.isFinite(v);
const id = (v: unknown) => text(v) && v.length > 0 && v.length <= 128;
const list = (v: unknown, check: (x: unknown) => boolean, limit: number) =>
  Array.isArray(v) && v.length <= limit && v.every(check);
const label = (v: unknown) => text(v) && v.length <= 512;
const phases = [
  "disconnected",
  "authenticating",
  "connecting",
  "opening",
  "connected",
  "disconnecting",
  "faulted",
];
const operations = [
  "none",
  "passwordLogin",
  "resume",
  "signOut",
  "forgetSavedLogin",
  "resetPassword",
];
const failures = [
  "none",
  "invalidCredentials",
  "usernameTaken",
  "invalidRequest",
  "registrationDisabled",
  "busy",
  "unavailable",
  "invalidResponse",
  "credentialStorage",
  "canceled",
  "nameNotAllowed",
];
const origins = ["server", "trustedClient", "thirdParty"];
const kinds = ["announcement", "event", "admin", "periodic"];
// A mod label as received: short, one line, shown as plain text.
const signature = (v: unknown) =>
  text(v) && v.length <= 128 && !/[\u0000-\u001f\u007f-\u009f]/.test(v);
function announcement(v: unknown): boolean {
  return (
    object(v) &&
    origins.includes(String(v.origin)) &&
    kinds.includes(String(v.kind)) &&
    (v.signature === undefined || signature(v.signature))
  );
}
function actorValue(v: unknown): boolean {
  return (
    object(v) &&
    text(v.key) &&
    text(v.name) &&
    (finite(v.value) ||
      (object(v.value) && finite(v.value.current) && finite(v.value.maximum)))
  );
}
function player(v: unknown): boolean {
  return (
    object(v) &&
    id(v.id) &&
    text(v.name) &&
    (v.alias === undefined || text(v.alias)) &&
    typeof v.inCharacter === "boolean" &&
    text(v.displayName) &&
    text(v.username) &&
    (v.character === undefined || text(v.character)) &&
    (v.location === undefined || text(v.location)) &&
    (v.level === undefined || finite(v.level)) &&
    [
      "zone",
      "race",
      "nearbyMarker",
      "markerKind",
      "activity",
      "activityTarget",
      "lockDifficulty",
      "menu",
    ].every((key) => v[key] === undefined || text(v[key])) &&
    (v.interior === undefined || typeof v.interior === "boolean") &&
    (v.gameStartedAt === undefined ||
      (finite(v.gameStartedAt) &&
        Math.abs(v.gameStartedAt as number) <= 8640000000000000)) &&
    (v.actorValues === undefined || list(v.actorValues, actorValue, 64))
  );
}
function channel(v: unknown): boolean {
  return (
    object(v) &&
    id(v.id) &&
    text(v.name) &&
    typeof v.writable === "boolean" &&
    ["global", "local", "party", "guild", "whisper", "system"].includes(
      String(v.kind),
    ) &&
    (v.kind !== "system" || !v.writable)
  );
}
function message(v: unknown): boolean {
  return (
    object(v) &&
    id(v.id) &&
    id(v.channelId) &&
    text(v.text) &&
    v.text.length <= 16000 &&
    finite(v.time) &&
    Math.abs(v.time as number) <= 8640000000000000 &&
    (v.filtered === undefined || typeof v.filtered === "boolean") &&
    ((v.source === "system" &&
      announcement(v.announcement) &&
      (v.author === undefined || player(v.author))) ||
      (v.source === "player" &&
        v.announcement === undefined &&
        player(v.author)))
  );
}
export function parseHostEvent(source: string): HostEvent {
  if (source.length > 8 * 1024 * 1024)
    throw new Error("UI payload exceeds limit");
  const v: unknown = JSON.parse(source);
  if (!object(v)) throw new Error("Expected UI event");
  let valid = false;
  switch (v.type) {
    case "snapshot":
      valid =
        list(v.channels, channel, 128) &&
        // The host sends up to 500 lines of every channel.
        list(v.messages, message, 500 * (v.channels as unknown[]).length) &&
        list(v.players, player, 4096) &&
        id(v.selfId) &&
        text(v.serverName) &&
        v.serverName.length <= 512 &&
        (v.settings === undefined || object(v.settings)) &&
        (v.refresh === undefined || typeof v.refresh === "boolean");
      break;
    case "ignored":
      valid = list(
        v.players,
        (p) => object(p) && id(p.id) && text(p.name),
        1000,
      );
      break;
    case "messages":
      valid = list(v.messages, message, 500);
      break;
    case "players":
      valid = list(v.players, player, 4096);
      break;
    case "show":
    case "hide":
    case "activate":
    case "deactivate":
      valid = true;
      break;
    case "connection":
      valid =
        typeof v.connected === "boolean" &&
        (v.phase === undefined || phases.includes(String(v.phase))) &&
        (v.phase === undefined || v.connected === (v.phase === "connected"));
      break;
    case "auth":
      valid =
        typeof v.authenticating === "boolean" &&
        operations.includes(String(v.operation)) &&
        failures.includes(String(v.failure)) &&
        label(v.error) &&
        typeof v.savedLogin === "boolean" &&
        label(v.savedUsername) &&
        phases.includes(String(v.phase));
      break;
    case "sendResult":
      valid =
        id(v.requestId) &&
        (v.error === undefined
          ? id(v.messageId)
          : text(v.error) && v.messageId === undefined);
      break;
    case "settingsResult":
      valid =
        Number.isSafeInteger(v.revision) &&
        (v.error === undefined || text(v.error));
      break;
    case "settings":
      valid = object(v.settings);
      break;
    case "announcementResult":
      valid =
        id(v.channelId) &&
        signature(v.source) &&
        text(v.text) &&
        v.text.length <= 16000 &&
        label(v.error);
      break;
  }
  if (!valid) throw new Error("Invalid UI event");
  return v as unknown as HostEvent;
}
