import type { StoreApi } from "zustand/vanilla";
import type {
  Guild,
  GuildAction,
  GuildInvite,
  GuildLimits,
  GuildRole,
  HostEvent,
  Send,
} from "../bridge/types";
import type { ChatState } from "./chat";
import { term } from "./moderation";

// The guild mute dialog of one member; request is the answer it waits for.
export interface GuildMuteDialog {
  guildId: string;
  playerId: string;
  name: string;
  request?: string;
  error?: string;
}
export interface GuildsState {
  // The player's guilds and invitations as the host last sent them. Known is
  // false until the server sent them in this session: "no guilds" is news
  // only after that.
  guilds: Guild[];
  invites: GuildInvite[];
  guildLimits: GuildLimits;
  guildsKnown: boolean;
  // Guild requests waiting for their answer, by request ID.
  guildRequests: Record<string, GuildAction>;
  // The guild open in the guilds panel.
  selectedGuild: string | null;
  guildMute: GuildMuteDialog | null;
}
export const idleGuilds: GuildsState = {
  guilds: [],
  invites: [],
  guildLimits: { perPlayer: 0, members: 0, nameMin: 0, nameMax: 0 },
  guildsKnown: false,
  guildRequests: {},
  selectedGuild: null,
  guildMute: null,
};
export const roleLabels: Record<GuildRole, string> = {
  member: "участник",
  officer: "офицер",
  master: "глава",
};
const rank = (role: GuildRole) =>
  role === "master" ? 2 : role === "officer" ? 1 : 0;
export const memberOf = (guild: Guild, playerId: string) =>
  guild.members.find((m) => m.id === playerId);
export const roleOf = (guild: Guild, playerId: string) =>
  memberOf(guild, playerId)?.role;
export const guildOfChannel = (guilds: Guild[], channelId: string) =>
  guilds.find((g) => g.channelId === channelId);
// The rules of the server's guild owner, so the page offers only what it
// would accept; the server decides anyway.
export const mayInvite = (role?: GuildRole) =>
  role === "officer" || role === "master";
// Excluding and muting need a rank above the target's.
export const outranks = (own: GuildRole | undefined, target: GuildRole) =>
  own !== undefined && rank(own) > rank(target);
// The master removes any message, an officer one of a member or of someone
// who left; nobody removes their own through the guild.
export function mayRemove(guild: Guild, selfId: string, authorId: string) {
  if (authorId === selfId) return false;
  return outranks(roleOf(guild, selfId), roleOf(guild, authorId) ?? "member");
}
// A guild mute holds until its end; one without an end until lifted.
export function guildMuted(
  guild: Guild | undefined,
  selfId: string,
  now: number,
) {
  const mute = guild && memberOf(guild, selfId)?.mute;
  return !!mute && (mute.until === undefined || mute.until > now);
}
export const guildMuteText = (guild: Guild, selfId: string) => {
  const mute = memberOf(guild, selfId)?.mute;
  return `Мут в гильдии «${guild.name}»${mute ? ` ${term(mute.until)}: ${mute.reason}` : ""}`;
};
// How long a mute is given for, as the officer chose it.
const span = (minutes?: number) =>
  minutes === undefined
    ? "бессрочно"
    : minutes % 1440 === 0
      ? `на ${minutes / 1440} дн.`
      : minutes % 60 === 0
        ? `на ${minutes / 60} ч`
        : `на ${minutes} мин`;
type GuildsEvent = Extract<HostEvent, { type: "guilds" }>;
type GuildResult = Extract<HostEvent, { type: "guildResult" }>;
type ModerationResult = Extract<HostEvent, { type: "moderationResult" }>;
// What changed for the player between two guild events, as local notices.
export function guildNotices(
  before: Guild[],
  beforeInvites: GuildInvite[],
  event: GuildsEvent,
  selfId: string,
) {
  const notices: string[] = [];
  for (const invite of event.invites)
    if (!beforeInvites.some((i) => i.guildId === invite.guildId))
      notices.push(
        `Приглашение в гильдию «${invite.guildName}»${invite.inviter ? ` от ${invite.inviter}` : ""}`,
      );
  for (const removal of event.removed)
    if (removal.reason === "excluded")
      notices.push(`Вас исключили из гильдии «${removal.name}»`);
    else if (removal.reason === "disbanded")
      notices.push(`Гильдия «${removal.name}» распущена`);
  for (const guild of event.guilds) {
    const previous = before.find((g) => g.id === guild.id);
    if (!previous) continue;
    const was = memberOf(previous, selfId);
    const now = memberOf(guild, selfId);
    if (was && now && was.role !== now.role)
      notices.push(
        `Ваша роль в гильдии «${guild.name}»: ${roleLabels[now.role]}`,
      );
    if (was && now && !was.mute && now.mute)
      notices.push(
        `Мут в гильдии «${guild.name}» ${term(now.mute.until)}: ${now.mute.reason}`,
      );
    if (was?.mute && now && !now.mute)
      notices.push(`Мут в гильдии «${guild.name}» снят`);
    const master = guild.members.find((m) => m.role === "master");
    const previousMaster = previous.members.find((m) => m.role === "master");
    if (master && master.id !== previousMaster?.id && master.id !== selfId)
      notices.push(`Новый глава гильдии «${guild.name}»: ${master.name}`);
  }
  return notices;
}

// Guild requests of the page and the notices of the server's changes.
// Requests carry their own IDs ("g1", ...), apart from chat and moderation.
export function makeGuilds(store: StoreApi<ChatState>, send: Send) {
  let sequence = 0;
  // What each request asked and whom it names, for its notice.
  const labels = new Map<string, string>();
  const removals = new Set<string>();
  const notice = (text: string) => store.setState({ notice: text });
  const guildName = (guildId: string) =>
    store.getState().guilds.find((g) => g.id === guildId)?.name ??
    store.getState().invites.find((i) => i.guildId === guildId)?.guildName ??
    "";
  // The command goes out with a fresh ID; undefined when it did not.
  function request(action: GuildAction, label = ""): string | undefined {
    const s = store.getState();
    if (!s.connected) return undefined;
    const requestId = `g${++sequence}`;
    store.setState({
      guildRequests: { ...s.guildRequests, [requestId]: action },
    });
    labels.set(requestId, label);
    if (send({ type: "guild", requestId, ...action })) return requestId;
    settle(requestId);
    notice("Команда не принята приложением");
    return undefined;
  }
  function settle(requestId: string) {
    const requests = { ...store.getState().guildRequests };
    delete requests[requestId];
    store.setState({ guildRequests: requests });
    labels.delete(requestId);
  }
  function done(action: GuildAction, label: string, guildId: string) {
    const name = guildName(guildId) || label;
    switch (action.action) {
      case "create":
        store.setState({ selectedGuild: guildId });
        return `Гильдия «${action.name}» создана`;
      case "invite":
        return `Приглашение отправлено: ${label}`;
      case "answer":
        if (action.accept) store.setState({ selectedGuild: guildId });
        return action.accept
          ? `Вы вступили в гильдию «${name}»`
          : `Приглашение в гильдию «${label}» отклонено`;
      case "leave":
        return `Вы вышли из гильдии «${label}»`;
      case "exclude":
        return `${label} исключён(а) из гильдии`;
      case "setRole":
        return `${label}: ${roleLabels[action.role]}`;
      case "transfer":
        return `Глава гильдии «${name}» теперь ${label}`;
      case "mute":
        return `${label}: мут в гильдии ${span(action.minutes)}`;
      case "unmute":
        return `${label}: мут в гильдии снят`;
      case "disband":
        return `Гильдия «${label}» распущена`;
    }
  }
  function result(event: GuildResult) {
    const s = store.getState();
    const action = s.guildRequests[event.requestId];
    if (!action) return;
    const label = labels.get(event.requestId) ?? "";
    settle(event.requestId);
    const dialog = store.getState().guildMute;
    if (dialog?.request === event.requestId)
      store.setState({
        guildMute:
          event.error === undefined
            ? null
            : { ...dialog, request: undefined, error: event.error },
      });
    if (event.error !== undefined) notice(`Не удалось: ${event.error}`);
    else notice(done(action, label, event.guildId ?? ""));
  }
  function changed(event: GuildsEvent) {
    const s = store.getState();
    const notices = s.guildsKnown
      ? guildNotices(s.guilds, s.invites, event, s.selfId)
      : [];
    const selected =
      s.selectedGuild && event.guilds.some((g) => g.id === s.selectedGuild)
        ? s.selectedGuild
        : (event.guilds[0]?.id ?? null);
    const dialog = s.guildMute;
    store.setState({
      guilds: event.guilds,
      invites: event.invites,
      guildLimits: event.limits,
      guildsKnown: true,
      selectedGuild: selected,
      // A mute dialog of a guild the player left closes.
      guildMute:
        dialog && event.guilds.some((g) => g.id === dialog.guildId)
          ? dialog
          : null,
      ...(notices.length ? { notice: notices.join(" · ") } : {}),
    });
  }
  return {
    receive(event: GuildsEvent | GuildResult | ModerationResult) {
      if (event.type === "guilds") changed(event);
      else if (event.type === "guildResult") result(event);
      else if (removals.delete(event.requestId))
        notice(
          event.error === undefined
            ? "Сообщение удалено"
            : `Не удалось: ${event.error}`,
        );
    },
    // A new session starts without guilds until the server sends them.
    reset() {
      store.setState({ ...idleGuilds });
    },
    select(guildId: string) {
      if (store.getState().guilds.some((g) => g.id === guildId))
        store.setState({ selectedGuild: guildId });
    },
    create(name: string) {
      const value = name.trim();
      if (value) request({ action: "create", name: value });
    },
    invite(guildId: string, playerId: string, name: string) {
      request({ action: "invite", guildId, playerId }, name);
    },
    answer(guildId: string, accept: boolean) {
      request({ action: "answer", guildId, accept }, guildName(guildId));
    },
    leave(guildId: string) {
      request({ action: "leave", guildId }, guildName(guildId));
    },
    exclude(guildId: string, playerId: string, name: string) {
      request({ action: "exclude", guildId, playerId }, name);
    },
    setRole(
      guildId: string,
      playerId: string,
      role: "member" | "officer",
      name: string,
    ) {
      request({ action: "setRole", guildId, playerId, role }, name);
    },
    transfer(guildId: string, playerId: string, name: string) {
      request({ action: "transfer", guildId, playerId }, name);
    },
    unmute(guildId: string, playerId: string, name: string) {
      request({ action: "unmute", guildId, playerId }, name);
    },
    disband(guildId: string) {
      request({ action: "disband", guildId }, guildName(guildId));
    },
    openMute(guildId: string, playerId: string, name: string) {
      store.setState({
        guildMute: { guildId, playerId, name },
        authorMenu: null,
      });
    },
    closeMute() {
      if (store.getState().guildMute) store.setState({ guildMute: null });
    },
    // The dialog stays open until the answer; a refusal stays in it.
    submitMute(minutes: number | undefined, reason: string) {
      const dialog = store.getState().guildMute;
      const text = reason.trim();
      if (!dialog || dialog.request || !text) return;
      const sent = request(
        {
          action: "mute",
          guildId: dialog.guildId,
          playerId: dialog.playerId,
          reason: text,
          ...(minutes === undefined ? {} : { minutes }),
        },
        dialog.name,
      );
      if (sent)
        store.setState({
          guildMute: { ...dialog, request: sent, error: undefined },
        });
    },
    // A message of the guild channel, removed by the guild's own roles.
    deleteMessage(channelId: string, messageId: string) {
      const s = store.getState();
      store.setState({ authorMenu: null });
      if (!s.connected) return;
      const requestId = `gd${++sequence}`;
      removals.add(requestId);
      if (
        !send({ type: "deleteChatMessage", requestId, channelId, messageId })
      ) {
        removals.delete(requestId);
        notice("Команда не принята приложением");
      }
    },
  };
}
export type Guilds = ReturnType<typeof makeGuilds>;
