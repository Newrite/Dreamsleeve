import type { HostEvent } from "./types";
import {
  announcementKinds,
  announcementOrigins,
  authFailures,
  authOperations,
  channelKinds,
  connectionPhases,
  groundMarkKinds,
  hidingModes,
  maxError,
  maxSnapshotRows,
  maxText,
  sanctionKinds,
  sessionEndReasons,
} from "./bridge.generated";
type ObjectValue = Record<string, unknown>;
const object = (v: unknown): v is ObjectValue =>
  v !== null && typeof v === "object" && !Array.isArray(v);
const text = (v: unknown): v is string => typeof v === "string";
const flag = (v: unknown) => typeof v === "boolean";
const finite = (v: unknown) => typeof v === "number" && Number.isFinite(v);
const time = (v: unknown) =>
  finite(v) && Math.abs(v as number) <= 8640000000000000;
const id = (v: unknown) => text(v) && v.length > 0 && v.length <= 128;
const list = (v: unknown, check: (x: unknown) => boolean, limit: number) =>
  Array.isArray(v) && v.length <= limit && v.every(check);
const label = (v: unknown) => text(v) && v.length <= 512;
const error = (v: unknown) => text(v) && v.length <= maxError;
const body = (v: unknown) => text(v) && v.length <= maxText;
const oneOf = (values: readonly string[]) => (v: unknown) =>
  text(v) && values.includes(v);
const optional = (check: (x: unknown) => boolean) => (v: unknown) =>
  v === undefined || check(v);
// A mod label as received: short, one line, shown as plain text.
const signature = (v: unknown) =>
  text(v) && v.length <= 128 && !/[\u0000-\u001f\u007f-\u009f]/.test(v);
function announcement(v: unknown): boolean {
  return (
    object(v) &&
    oneOf(announcementOrigins)(v.origin) &&
    oneOf(announcementKinds)(v.kind) &&
    optional(signature)(v.signature)
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
    optional(text)(v.alias) &&
    flag(v.inCharacter) &&
    text(v.displayName) &&
    text(v.username) &&
    optional(text)(v.character) &&
    optional(text)(v.location) &&
    optional(finite)(v.level) &&
    [
      "zone",
      "race",
      "nearbyMarker",
      "markerKind",
      "activity",
      "activityTarget",
      "lockDifficulty",
      "menu",
    ].every((key) => optional(text)(v[key])) &&
    optional(flag)(v.interior) &&
    optional(time)(v.gameStartedAt) &&
    optional((x) => list(x, actorValue, 64))(v.actorValues) &&
    optional(flag)(v.pseudonymous) &&
    // The server sends no username or character of a pseudonymous player.
    (!v.pseudonymous || (v.username === "" && v.character === undefined))
  );
}
function groundMark(v: unknown): boolean {
  return (
    object(v) &&
    id(v.id) &&
    oneOf(groundMarkKinds)(v.kind) &&
    body(v.text) &&
    time(v.time) &&
    optional(label)(v.author) &&
    optional(label)(v.character) &&
    label(v.location) &&
    finite(v.x) &&
    finite(v.y) &&
    finite(v.z) &&
    optional(label)(v.gameDate)
  );
}
function channel(v: unknown): boolean {
  return (
    object(v) &&
    id(v.id) &&
    text(v.name) &&
    flag(v.writable) &&
    oneOf(channelKinds)(v.kind) &&
    (v.kind !== "system" || !v.writable)
  );
}
function message(v: unknown): boolean {
  return (
    object(v) &&
    id(v.id) &&
    id(v.channelId) &&
    body(v.text) &&
    time(v.time) &&
    optional(flag)(v.filtered) &&
    ((v.source === "system" &&
      announcement(v.announcement) &&
      optional(player)(v.author)) ||
      (v.source === "player" &&
        v.announcement === undefined &&
        player(v.author)))
  );
}
const marks = (limit: number) => (v: unknown) => list(v, groundMark, limit);
function sanction(v: unknown): boolean {
  return (
    object(v) &&
    id(v.playerId) &&
    optional(label)(v.name) &&
    oneOf(sanctionKinds)(v.kind) &&
    label(v.reason) &&
    time(v.issuedAt) &&
    optional(time)(v.until)
  );
}
const count = (v: unknown) =>
  typeof v === "number" && Number.isSafeInteger(v) && v >= 0;
const bare = () => true;
// One check per host event: a new event type does not compile until it has one.
const events: { [K in HostEvent["type"]]: (v: ObjectValue) => boolean } = {
  snapshot: (v) =>
    list(v.channels, channel, 128) &&
    list(
      v.messages,
      message,
      maxSnapshotRows * (v.channels as unknown[]).length,
    ) &&
    list(v.players, player, 4096) &&
    id(v.selfId) &&
    label(v.serverName) &&
    optional(object)(v.settings) &&
    optional(flag)(v.refresh) &&
    optional(flag)(v.groundMarksSupported) &&
    optional(marks(256))(v.groundMarks) &&
    optional(marks(4096))(v.nearbyMarks),
  messages: (v) => list(v.messages, message, maxSnapshotRows),
  players: (v) => list(v.players, player, 4096),
  connection: (v) =>
    flag(v.connected) &&
    optional(oneOf(connectionPhases))(v.phase) &&
    (v.phase === undefined || v.connected === (v.phase === "connected")),
  ignored: (v) =>
    list(v.players, (p) => object(p) && id(p.id) && text(p.name), 1000),
  sendResult: (v) =>
    id(v.requestId) &&
    (v.error === undefined
      ? id(v.messageId)
      : text(v.error) && v.messageId === undefined),
  announcementResult: (v) =>
    id(v.channelId) && signature(v.source) && body(v.text) && error(v.error),
  settingsResult: (v) =>
    Number.isSafeInteger(v.revision) && optional(text)(v.error),
  auth: (v) =>
    flag(v.authenticating) &&
    oneOf(authOperations)(v.operation) &&
    oneOf(authFailures)(v.failure) &&
    error(v.error) &&
    flag(v.savedLogin) &&
    label(v.savedUsername) &&
    oneOf(connectionPhases)(v.phase),
  settings: (v) => object(v.settings),
  groundMarks: (v) => marks(256)(v.marks),
  markResult: (v) =>
    id(v.requestId) &&
    optional(id)(v.markId) &&
    optional(id)(v.evictedId) &&
    optional(flag)(v.removed) &&
    optional(error)(v.error) &&
    (v.error === undefined) !== (v.markId === undefined && !v.removed),
  nearbyMarks: (v) => marks(4096)(v.marks),
  identity: (v) =>
    oneOf(hidingModes)(v.mode) &&
    flag(v.pending) &&
    optional(label)(v.pseudonym) &&
    optional(error)(v.error),
  displayName: (v) =>
    flag(v.pending) && optional(label)(v.changed) && optional(error)(v.error),
  mute: (v) => flag(v.muted) && label(v.reason) && optional(time)(v.until),
  sessionEnded: (v) =>
    oneOf(sessionEndReasons)(v.reason) &&
    label(v.text) &&
    optional(time)(v.until),
  role: (v) => flag(v.moderator),
  messagesRemoved: (v) =>
    id(v.channelId) && list(v.messageIds, id, maxSnapshotRows),
  moderationResult: (v) =>
    id(v.requestId) &&
    optional(error)(v.error) &&
    optional(sanction)(v.sanction) &&
    optional((x) => list(x, sanction, 4096))(v.sanctions) &&
    optional(id)(v.playerId) &&
    optional(marks(4096))(v.marks) &&
    optional(count)(v.removed),
  show: bare,
  hide: bare,
  activate: bare,
  deactivate: bare,
};
export function parseHostEvent(source: string): HostEvent {
  if (source.length > 8 * 1024 * 1024)
    throw new Error("UI payload exceeds limit");
  const v: unknown = JSON.parse(source);
  if (!object(v)) throw new Error("Expected UI event");
  const type = String(v.type);
  const check = Object.hasOwn(events, type)
    ? events[type as HostEvent["type"]]
    : undefined;
  if (!check || !check(v)) throw new Error("Invalid UI event");
  return v as unknown as HostEvent;
}
