import type { HostEvent } from "./types";
import { isColor } from "../state/settings";
import {
  announcementKinds,
  announcementOrigins,
  authFailures,
  authOperations,
  channelKinds,
  connectionPhases,
  groundMarkKinds,
  guildRemovalReasons,
  guildRoles,
  hidingModes,
  maxError,
  maxSnapshotRows,
  maxText,
  registrationModes,
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
    optional(isColor)(v.color) &&
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
    // The server sends no username, character or color of a pseudonymous player.
    (!v.pseudonymous ||
      (v.username === "" && v.character === undefined && v.color === undefined))
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
function guildMember(v: unknown): boolean {
  return (
    object(v) &&
    id(v.id) &&
    label(v.name) &&
    oneOf(guildRoles)(v.role) &&
    flag(v.online) &&
    time(v.joinedAt) &&
    optional((m) => object(m) && label(m.reason) && optional(time)(m.until))(
      v.mute,
    )
  );
}
function guild(v: unknown): boolean {
  return (
    object(v) &&
    id(v.id) &&
    label(v.name) &&
    id(v.channelId) &&
    time(v.createdAt) &&
    list(v.members, guildMember, 4096)
  );
}
function guildInvite(v: unknown): boolean {
  return (
    object(v) &&
    id(v.guildId) &&
    label(v.guildName) &&
    id(v.invitedBy) &&
    optional(label)(v.inviter) &&
    time(v.expires)
  );
}
const guildLimits = (v: unknown) =>
  object(v) &&
  count(v.perPlayer) &&
  count(v.members) &&
  count(v.nameMin) &&
  count(v.nameMax);
const guildRemoval = (v: unknown) =>
  object(v) &&
  id(v.guildId) &&
  label(v.name) &&
  oneOf(guildRemovalReasons)(v.reason);
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
    oneOf(registrationModes)(v.registration) &&
    flag(v.steam) &&
    flag(v.browserFailed) &&
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
  nameColor: (v) =>
    flag(v.pending) && optional(isColor)(v.changed) && optional(error)(v.error),
  routes: (v) =>
    list(v.routes, label, 8) &&
    (v.routes as unknown[]).length > 1 &&
    (v.routes as unknown[]).includes(v.active) &&
    (v.chosen === "" || (v.routes as unknown[]).includes(v.chosen)) &&
    flag(v.reached),
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
  guilds: (v) =>
    list(v.guilds, guild, 128) &&
    list(v.invites, guildInvite, 1024) &&
    guildLimits(v.limits) &&
    list(v.removed, guildRemoval, 128),
  guildResult: (v) =>
    id(v.requestId) &&
    optional(id)(v.guildId) &&
    optional(error)(v.error) &&
    (v.error === undefined) !== (v.guildId === undefined),
  channels: (v) => list(v.channels, channel, 128),
  show: bare,
  hide: bare,
  activate: bare,
  deactivate: bare,
};

export type HostEventParseResult =
  | { ok: true; event: HostEvent }
  | { ok: false; error: "size" | "json" | "schema" };

// The existing host payload limit is measured in UTF-16 string code units.
const maxPayloadLength = 8 * 1024 * 1024;

function parseJson(
  source: string,
): { ok: true; value: unknown } | { ok: false; error: "json" } {
  // JSON.parse(string), without a reviver, uses SyntaxError for malformed JSON.
  // Only that dependency call is inside the ordinary-failure adapter.
  let value: unknown;
  try {
    value = JSON.parse(source);
  } catch {
    return { ok: false, error: "json" };
  }
  return { ok: true, value };
}

export function parseHostEvent(source: string): HostEventParseResult {
  if (source.length > maxPayloadLength) return { ok: false, error: "size" };
  const parsed = parseJson(source);
  if (!parsed.ok) return parsed;
  const v = parsed.value;
  if (!object(v) || !text(v.type)) return { ok: false, error: "schema" };
  const check = Object.hasOwn(events, v.type)
    ? events[v.type as HostEvent["type"]]
    : undefined;
  if (!check || !check(v)) return { ok: false, error: "schema" };
  return { ok: true, event: v as unknown as HostEvent };
}
