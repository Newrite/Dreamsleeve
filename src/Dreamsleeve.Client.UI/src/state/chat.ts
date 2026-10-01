import { createStore } from "zustand/vanilla";
import type {
  AuthOperation,
  AuthState,
  Command,
  ConnectionPhase,
  Channel,
  GroundMark,
  HideIdentity,
  DisplayNameState,
  HostEvent,
  IdentityState,
  Message,
  MuteState,
  Player,
  Send,
  SessionEndState,
  Settings,
} from "../bridge/types";
import { idleAuth } from "./auth";
import { muted, muteText, sessionEndText } from "./moderation";
import { idleModerator, makeModerator, type ModeratorState } from "./moderator";
import { defaults, instantKeys } from "../bridge/settings.generated";
// Lines kept per channel: a busy channel never pushes another one out.
export const HISTORY_LIMIT = 500;
// Message IDs are sequential within a channel; a line is named by the pair.
const messageKey = (channelId: string, id: string) => `${channelId}:${id}`;
export const keyOf = (m: Message) => messageKey(m.channelId, m.id);
// The newest HISTORY_LIMIT lines of every channel, order kept.
function retain(messages: Message[]) {
  const counts = new Map<string, number>();
  const kept: Message[] = [];
  for (let i = messages.length - 1; i >= 0; i--) {
    const count = counts.get(messages[i].channelId) ?? 0;
    if (count >= HISTORY_LIMIT) continue;
    counts.set(messages[i].channelId, count + 1);
    kept.push(messages[i]);
  }
  return kept.reverse();
}
// A snapshot lists channel after channel; "Все" reads them in time order.
const chronological = (messages: Message[]) =>
  retain([...messages].sort((a, b) => a.time - b.time));
const confirmedKey = (p: PendingMessage) =>
  p.messageId === undefined ? undefined : messageKey(p.channelId, p.messageId);
export type Panel =
  | "online"
  | "profile"
  | "stats"
  | "settings"
  | "account"
  | "marks"
  | "ignored"
  | "moderation"
  | null;
export interface PendingMessage {
  channelId: string;
  text: string;
  time: number;
  status: "sending" | "failed" | "unknown";
  error?: string;
  messageId?: string;
  since?: number; // when the row became failed or unknown
  // Refused announcement of another mod: the mod label; never retried here.
  external?: string;
  // A ground note left where the character stands, not a chat line: it
  // settles by markResult and is shown whatever channel is selected.
  kind?: "note";
}
export const sending = (state: ChatState) =>
  Object.values(state.pending).some((p) => p.status === "sending");
export const PENDING_LIMIT = 16;
// Refusals of other mods give way first: they never block the player's own sending.
function makeRoom(pending: Record<string, PendingMessage>) {
  const result = { ...pending };
  const external = Object.entries(result)
    .filter(([, p]) => p.external !== undefined)
    .sort(([, a], [, b]) => (a.since ?? a.time) - (b.since ?? b.time));
  while (Object.keys(result).length >= PENDING_LIMIT && external.length)
    delete result[external.shift()![0]];
  return result;
}
// A request without a reply becomes "unknown" after this long; a failed or
// unknown row leaves the passive HUD after the same time, while an active chat
// keeps it until the player retries or dismisses.
export const PENDING_TIMEOUT = 15000;
export interface ChatState extends ModeratorState {
  channels: Channel[];
  messages: Message[];
  receivedAt: Record<string, number>;
  players: Player[];
  selfId: string;
  serverName: string;
  connected: boolean;
  connectionPhase: ConnectionPhase;
  auth: AuthState;
  initialized: boolean;
  visible: boolean;
  active: boolean;
  faded: boolean;
  filter: string;
  target: string;
  unread: Record<string, number>;
  drafts: Record<string, string>;
  scrolled: boolean;
  panel: Panel;
  selectedPlayer: string | null;
  settings: Settings;
  notice: string;
  pending: Record<string, PendingMessage>;
  activity: number;
  revision: number;
  savedRevision: number;
  // Personal ignore list of the current server, named by the host.
  ignored: { id: string; name: string }[];
  // Context menu of a message author, at viewport coordinates, with the
  // message it was opened on.
  authorMenu: {
    playerId: string;
    name: string;
    x: number;
    y: number;
    pseudonymous?: boolean;
    message?: { channelId: string; id: string };
  } | null;
  // "Hide my name from other players", as the host reports it.
  identity: IdentityState;
  // A change of the own display name, as the host reports it.
  displayName: DisplayNameState;
  // The player's own mute, as the host reports it.
  mute: MuteState;
  // Why the last session ended or sign-in was refused by a ban; cleared by the next session.
  sessionEnd: SessionEndState | null;
  // Ground marks: whether the session can place them, the server's list of
  // the player's own marks and the marks it shows nearby.
  groundMarksSupported: boolean;
  groundMarks: GroundMark[];
  nearbyMarks: GroundMark[];
}
export const announcementOf = (m: Message) =>
  m.source === "system" ? m.announcement : undefined;
// Origin and kind switches hide a system line everywhere, its own tab included.
export function allowed(message: Message, s: Settings) {
  const a = announcementOf(message);
  if (!a) return true;
  return (
    (a.origin === "server"
      ? s.announcementsServer
      : a.origin === "trustedClient"
        ? s.announcementsTrustedClient
        : s.announcementsThirdParty) &&
    (a.kind === "event"
      ? s.announcementsEvents
      : a.kind === "periodic"
        ? s.announcementsPeriodic
        : true)
  );
}
// Whether the view `filter` includes lines of a channel: the system channel
// joins "Все" or every tab as chosen. Unread counting follows the same rule.
export function shows(
  channelId: string,
  filter: string,
  s: Settings,
  channels: Channel[],
) {
  if (channelId === filter) return true;
  if (channels.some((c) => c.id === channelId && c.kind === "system"))
    return (
      s.announcementChannels === "current" ||
      (filter === "all" && s.announcementChannels === "all")
    );
  return filter === "all";
}
export const visible = (
  message: Message,
  filter: string,
  s: Settings,
  channels: Channel[],
) => shows(message.channelId, filter, s, channels) && allowed(message, s);
export function makeChat(send: Send, now = () => Date.now()) {
  const store = createStore<ChatState>(() => ({
    channels: [],
    messages: [],
    receivedAt: {},
    players: [],
    selfId: "",
    serverName: "",
    connected: false,
    connectionPhase: "disconnected",
    auth: { ...idleAuth },
    initialized: false,
    visible: true,
    active: false,
    faded: false,
    filter: "all",
    target: "",
    unread: {},
    drafts: {},
    scrolled: false,
    panel: null,
    selectedPlayer: null,
    settings: { ...defaults },
    notice: "",
    pending: {},
    activity: now(),
    revision: 0,
    savedRevision: 0,
    ignored: [],
    authorMenu: null,
    identity: { mode: "off", pending: false },
    displayName: { pending: false },
    mute: { muted: false, reason: "" },
    sessionEnd: null,
    groundMarksSupported: false,
    groundMarks: [],
    nearbyMarks: [],
    ...idleModerator,
  }));
  const moderator = makeModerator(store, send);
  let sequence = 0;
  let refusals = 0;
  // Removal requests in flight: request id -> mark id, for the result notice.
  const removals = new Map<string, string>();
  const touch = () => store.setState({ activity: now(), faded: false });
  // The server does not announce a term ending: the page redraws then. A term
  // beyond the timer's range is checked again when that range runs out.
  let muteTimer: ReturnType<typeof setTimeout> | undefined;
  function unmuteAt(until?: number) {
    clearTimeout(muteTimer);
    if (until === undefined) return;
    muteTimer = setTimeout(
      () => {
        const mute = store.getState().mute;
        store.setState({ mute: { ...mute } });
        if (muted(mute, now())) unmuteAt(mute.until);
      },
      Math.min(Math.max(until - now(), 0), 2 ** 31 - 1),
    );
  }
  // Writing while muted is refused by the server anyway; say why at once.
  function silenced() {
    const mute = store.getState().mute;
    if (!muted(mute, now())) return false;
    store.setState({ notice: muteText(mute) });
    return true;
  }
  function receive(event: HostEvent) {
    const state = store.getState();
    switch (event.type) {
      case "snapshot": {
        const channels = event.channels;
        if (event.refresh && state.initialized) {
          // Same session re-projected: keep pending rows, filters and scroll.
          const messages = chronological(event.messages);
          const retained = new Set(messages.map(keyOf));
          const receivedAt = Object.fromEntries(
            Object.entries(state.receivedAt).filter(([id]) => retained.has(id)),
          );
          store.setState({
            channels,
            messages,
            receivedAt,
            players: event.players,
            groundMarksSupported: event.groundMarksSupported ?? false,
            groundMarks: event.groundMarks ?? state.groundMarks,
            nearbyMarks: event.nearbyMarks ?? state.nearbyMarks,
          });
          break;
        }
        const shown = new Set(event.messages.map(keyOf));
        store.setState({
          channels,
          messages: chronological(event.messages),
          receivedAt: {},
          players: event.players,
          selfId: event.selfId,
          serverName: event.serverName,
          initialized: true,
          connected: true,
          connectionPhase: "connected",
          groundMarksSupported: event.groundMarksSupported ?? false,
          groundMarks: event.groundMarks ?? [],
          nearbyMarks: event.nearbyMarks ?? [],
          pending:
            state.selfId === event.selfId &&
            state.serverName === event.serverName
              ? Object.fromEntries(
                  Object.entries(state.pending)
                    .filter(([, p]) => !shown.has(confirmedKey(p) ?? ""))
                    .map(([id, p]) => [
                      id,
                      p.status === "sending"
                        ? { ...p, status: "unknown" as const }
                        : p,
                    ]),
                )
              : {},
          unread: {},
          filter: "all",
          target: channels.find((c) => c.writable)?.id ?? "",
          notice: "",
          sessionEnd: null,
          scrolled: false,
        });
        touch();
        break;
      }
      case "messages": {
        const seen = new Set(state.messages.map(keyOf));
        const channels = new Set(state.channels.map((c) => c.id));
        const added = event.messages.filter((m) => {
          const key = keyOf(m);
          if (seen.has(key) || !channels.has(m.channelId)) return false;
          seen.add(key);
          return true;
        });
        const unread = { ...state.unread };
        // A hidden announcement is not news anywhere.
        for (const m of added)
          if (
            allowed(m, state.settings) &&
            (!state.active ||
              state.scrolled ||
              !shows(m.channelId, state.filter, state.settings, state.channels))
          )
            unread[m.channelId] = Math.min(
              HISTORY_LIMIT,
              (unread[m.channelId] ?? 0) + 1,
            );
        const messages = retain([...state.messages, ...added]);
        const receivedAt = { ...state.receivedAt };
        const receipt = now();
        for (const message of added) receivedAt[keyOf(message)] = receipt;
        const retained = new Set(messages.map(keyOf));
        for (const key of Object.keys(receivedAt))
          if (!retained.has(key)) delete receivedAt[key];
        const pending = Object.fromEntries(
          Object.entries(state.pending).filter(
            ([, p]) => !seen.has(confirmedKey(p) ?? ""),
          ),
        );
        store.setState({ messages, receivedAt, unread, pending });
        break;
      }
      case "messagesRemoved": {
        // A moderator removed them: gone from the history and any pending row.
        const gone = new Set(
          event.messageIds.map((id) => messageKey(event.channelId, id)),
        );
        const receivedAt = { ...state.receivedAt };
        for (const key of gone) delete receivedAt[key];
        store.setState({
          messages: state.messages.filter((m) => !gone.has(keyOf(m))),
          receivedAt,
          pending: Object.fromEntries(
            Object.entries(state.pending).filter(
              ([, p]) => !gone.has(confirmedKey(p) ?? ""),
            ),
          ),
        });
        break;
      }
      case "role":
      case "moderationResult":
        moderator.receive(event);
        break;
      case "players":
        store.setState({ players: event.players });
        break;
      case "groundMarks":
        store.setState({ groundMarks: event.marks });
        break;
      case "nearbyMarks":
        store.setState({ nearbyMarks: event.marks });
        break;
      case "markResult": {
        const removed = removals.get(event.requestId);
        if (removed !== undefined) {
          removals.delete(event.requestId);
          const listed = state.playerMarks;
          store.setState({
            notice:
              event.error !== undefined
                ? `Метка не удалена: ${event.error}`
                : "Метка удалена",
            // A moderator's list of another player's marks loses it too.
            ...(listed && event.error === undefined
              ? {
                  playerMarks: {
                    ...listed,
                    marks: listed.marks.filter((m) => m.id !== removed),
                  },
                }
              : {}),
          });
          break;
        }
        const item = state.pending[event.requestId];
        if (!item || item.kind !== "note") break;
        const pending = { ...state.pending };
        if (event.error !== undefined) {
          pending[event.requestId] = {
            ...item,
            status: "failed",
            error: event.error,
            since: now(),
          };
          store.setState({ pending });
          break;
        }
        delete pending[event.requestId];
        store.setState({
          pending,
          notice: event.evictedId
            ? `Метка оставлена; самая старая (№${event.evictedId}) убрана по квоте`
            : "Метка оставлена",
        });
        break;
      }
      case "identity": {
        const { type: _, ...identity } = event;
        store.setState({ identity });
        break;
      }
      case "displayName": {
        const { type: _, ...displayName } = event;
        store.setState({ displayName });
        break;
      }
      case "mute": {
        const { type: _, ...mute } = event;
        store.setState({ mute });
        unmuteAt(mute.muted ? mute.until : undefined);
        break;
      }
      case "sessionEnded": {
        const { type: _, ...sessionEnd } = event;
        store.setState({ sessionEnd, notice: sessionEndText(sessionEnd) });
        break;
      }
      case "ignored":
        store.setState({ ignored: event.players });
        break;
      case "hide":
        store.setState({
          visible: false,
          active: false,
          panel: null,
          authorMenu: null,
        });
        // Visibility is authoritative even if the native listener is unavailable.
        // Native also releases Prisma focus when reacting to a Skyrim menu.
        if (state.active) send({ type: "close" });
        break;
      case "show":
        store.setState({ visible: true });
        break;
      case "activate":
        if (!state.visible) break;
        store.setState({ active: true });
        touch();
        break;
      case "deactivate":
        store.setState({
          active: false,
          panel: null,
          scrolled: false,
          authorMenu: null,
        });
        touch();
        break;
      case "connection":
        store.setState({
          connected: event.connected,
          connectionPhase:
            event.phase ?? (event.connected ? "connected" : "disconnected"),
          notice:
            !event.connected &&
            state.initialized &&
            (!event.phase || ["disconnected", "faulted"].includes(event.phase))
              ? "Соединение потеряно. Доставка ожидающих сообщений неизвестна."
              : "",
        });
        if (!event.connected) {
          const pending = Object.fromEntries(
            Object.entries(state.pending).map(([id, p]) => [
              id,
              p.status === "sending"
                ? { ...p, status: "unknown" as const, since: now() }
                : p,
            ]),
          );
          store.setState({ pending });
        }
        touch();
        break;
      case "auth":
        // The host mirrors the phase here; pending rows react to "connection".
        store.setState({
          auth: {
            authenticating: event.authenticating,
            operation: event.operation,
            failure: event.failure,
            error: event.error,
            savedLogin: event.savedLogin,
            savedUsername: event.savedUsername,
          },
          connected: event.phase === "connected",
          connectionPhase: event.phase,
        });
        break;
      case "sendResult": {
        const item = state.pending[event.requestId];
        if (!item) break;
        const pending = { ...state.pending };
        if (event.error !== undefined) {
          pending[event.requestId] = {
            ...item,
            status: "failed",
            error: event.error,
            since: now(),
          };
        } else if (
          state.messages.some(
            (m) => keyOf(m) === messageKey(item.channelId, event.messageId),
          )
        ) {
          delete pending[event.requestId];
        } else {
          pending[event.requestId] = { ...item, messageId: event.messageId };
        }
        store.setState({ pending });
        break;
      }
      case "settings":
        store.setState({ settings: event.settings });
        break;
      case "settingsResult":
        if (event.revision !== state.revision) break;
        store.setState({
          savedRevision: event.error ? state.savedRevision : event.revision,
          notice: event.error ?? "Настройки сохранены",
        });
        break;
      case "announcementResult": {
        // Shown like a refused own line, expiring the same way; like messages,
        // only for a channel of the current view.
        if (!state.channels.some((c) => c.id === event.channelId)) break;
        const pending = makeRoom(state.pending);
        if (Object.keys(pending).length >= PENDING_LIMIT) break;
        const at = now();
        pending[`x${++refusals}`] = {
          channelId: event.channelId,
          text: event.text,
          time: at,
          status: "failed",
          error: event.error,
          since: at,
          external: event.source,
        };
        store.setState({ pending });
        break;
      }
      default:
        event satisfies never;
    }
  }
  function close() {
    if (!send({ type: "close" })) {
      store.setState({ notice: "Не удалось вернуть управление игре" });
      return;
    }
    receive({ type: "deactivate" });
  }
  // The current draft goes to the ground where the character stands instead
  // of the chat. Same limits, same pending row, same single request at a time.
  function placeNote() {
    const s = store.getState();
    if (!s.visible || sending(s)) return;
    const text = s.drafts[s.target] ?? "";
    if (!text.trim() || !s.connected || !s.groundMarksSupported) return;
    if (silenced()) return;
    const room = makeRoom(s.pending);
    if (Object.keys(room).length >= PENDING_LIMIT) {
      store.setState({
        notice: "Удалите старые неподтверждённые сообщения перед отправкой.",
      });
      return;
    }
    const requestId = String(++sequence);
    const row: PendingMessage = {
      channelId: s.target,
      text,
      time: now(),
      status: "sending",
      kind: "note",
    };
    store.setState({
      scrolled: false,
      pending: { ...room, [requestId]: row },
      notice: "",
    });
    if (!send({ type: "placeGroundNote", requestId, text })) {
      store.setState({
        pending: {
          ...room,
          [requestId]: {
            ...row,
            status: "failed",
            error: "Команда не принята приложением",
            since: now(),
          },
        },
        drafts: { ...s.drafts, [s.target]: "" },
      });
      return;
    }
    store.setState({ drafts: { ...s.drafts, [s.target]: "" } });
    close();
  }
  function submit() {
    const s = store.getState();
    if (!s.visible) return;
    const text = s.drafts[s.target] ?? "";
    if (sending(s)) return;
    if (!text.trim()) {
      close();
      return;
    }
    if (
      !s.connected ||
      !s.channels.some((c) => c.id === s.target && c.writable)
    )
      return;
    if (silenced()) return;
    const room = makeRoom(s.pending);
    if (Object.keys(room).length >= PENDING_LIMIT) {
      store.setState({
        notice: "Удалите старые неподтверждённые сообщения перед отправкой.",
      });
      return;
    }
    const requestId = String(++sequence);
    store.setState({
      filter: s.filter === "all" ? "all" : s.target,
      scrolled: false,
      pending: {
        ...room,
        [requestId]: {
          channelId: s.target,
          text,
          time: now(),
          status: "sending",
        },
      },
      notice: "",
    });
    if (!send({ type: "sendChat", requestId, channelId: s.target, text })) {
      store.setState({
        pending: {
          ...room,
          [requestId]: {
            channelId: s.target,
            text,
            time: now(),
            status: "failed",
            error: "Команда не принята приложением",
            since: now(),
          },
        },
        drafts: { ...s.drafts, [s.target]: "" },
      });
      return;
    }
    store.setState({ drafts: { ...s.drafts, [s.target]: "" } });
    close();
  }
  // One auth operation at a time. The command carries the secret; the store
  // only ever holds the typed state the host reports back.
  function authenticate(command: Command, operation: AuthOperation) {
    const s = store.getState();
    if (s.auth.authenticating) return;
    store.setState({
      auth: {
        ...s.auth,
        authenticating: true,
        operation,
        failure: "none",
        error: "",
      },
      notice: "",
    });
    if (!send(command))
      store.setState({
        auth: s.auth,
        notice: "Команда не принята приложением",
      });
  }
  function save() {
    const s = store.getState();
    const revision = s.revision + 1;
    store.setState({ revision, notice: "Сохранение…" });
    if (!send({ type: "saveSettings", settings: s.settings, revision }))
      store.setState({
        notice: "Настройки применены, но не сохранены: приложение недоступно.",
      });
  }
  function select(filter: string) {
    if (
      filter !== "all" &&
      !store.getState().channels.some((c) => c.id === filter)
    )
      return;
    store.setState({ filter, scrolled: false });
    touch();
  }
  function read() {
    const s = store.getState();
    const unread = { ...s.unread };
    for (const id of Object.keys(unread))
      if (shows(id, s.filter, s.settings, s.channels)) unread[id] = 0;
    store.setState({ unread, scrolled: false });
  }
  return {
    store,
    expirePending() {
      const s = store.getState();
      const at = now();
      const pending = Object.fromEntries(
        Object.entries(s.pending)
          .map(([id, p]): [string, PendingMessage] => [
            id,
            p.status === "sending" && at - p.time >= PENDING_TIMEOUT
              ? { ...p, status: "unknown", since: at }
              : p,
          ])
          .filter(
            ([, p]) =>
              s.active ||
              p.status === "sending" ||
              at - (p.since ?? p.time) < PENDING_TIMEOUT,
          ),
      );
      store.setState({ pending });
    },
    dismiss(requestId: string) {
      const pending = { ...store.getState().pending };
      if (pending[requestId]?.status === "sending") return;
      delete pending[requestId];
      store.setState({ pending });
    },
    retry(requestId: string) {
      const s = store.getState();
      const item = s.pending[requestId];
      if (
        !s.visible ||
        !item ||
        item.status !== "failed" ||
        item.external !== undefined ||
        !s.connected ||
        sending(s)
      )
        return;
      if (s.drafts[item.channelId]) {
        store.setState({
          notice: "Сначала отправьте или очистите черновик этого канала.",
        });
        return;
      }
      const pending = { ...s.pending };
      delete pending[requestId];
      store.setState({
        pending,
        target: item.channelId,
        drafts: { ...s.drafts, [item.channelId]: item.text },
      });
      if (item.kind === "note") placeNote();
      else submit();
    },
    placeNote,
    moderator,
    // The author's own marks, or any for a moderator; the result is a notice.
    removeMark(markId: string) {
      const s = store.getState();
      if (!s.connected || !s.groundMarksSupported || !markId) return;
      if ([...removals.values()].includes(markId)) return;
      const requestId = String(++sequence);
      removals.set(requestId, markId);
      if (!send({ type: "removeGroundMark", requestId, markId })) {
        removals.delete(requestId);
        store.setState({ notice: "Команда не принята приложением" });
        return;
      }
      store.setState({ notice: "Удаление метки…" });
    },
    receive,
    touch,
    close,
    submit,
    save,
    select,
    read,
    signIn(
      username: string,
      password: string,
      remember: boolean,
      displayName?: string,
    ) {
      const name = username.trim();
      const display = displayName?.trim();
      if (!name || !password) return;
      authenticate(
        {
          type: "signIn",
          username: name,
          password,
          remember,
          ...(display ? { displayName: display } : {}),
        },
        "passwordLogin",
      );
    },
    signInSaved() {
      if (!store.getState().auth.savedLogin) return;
      authenticate({ type: "signInSaved" }, "resume");
    },
    // An administrator's one-time code: a new account or a reset password.
    resetPassword(code: string, password: string) {
      const value = code.trim();
      if (!value || !password) return;
      authenticate(
        { type: "resetPassword", code: value, password },
        "resetPassword",
      );
    },
    signOut() {
      const s = store.getState();
      if (!s.connected && !s.auth.savedLogin) return;
      authenticate({ type: "signOut" }, "signOut");
    },
    forgetLogin() {
      if (!store.getState().auth.savedLogin) return;
      authenticate({ type: "forgetLogin" }, "forgetSavedLogin");
    },
    disconnect() {
      const s = store.getState();
      if (!s.connected || s.auth.authenticating) return;
      if (!send({ type: "disconnect" }))
        store.setState({ notice: "Команда не принята приложением" });
    },
    setDraft(text: string) {
      const s = store.getState();
      store.setState({ drafts: { ...s.drafts, [s.target]: text } });
    },
    configure(patch: Partial<Settings>) {
      const current = store.getState();
      const settings = { ...current.settings, ...patch };
      store.setState({
        settings,
        revision: current.revision + 1,
        notice: "Настройки изменены. Нажмите «Сохранить настройки».",
      });
      // Names, the text filter and dates apply to every surface at once, the game included.
      if (instantKeys.some((key) => settings[key] !== current.settings[key])) {
        if (!send({ type: "displaySettings", settings }))
          store.setState({ notice: "Команда не принята приложением" });
        else store.setState({ notice: "Отображение применено" });
      }
      touch();
    },
    // The server decides in a session; until it answers the UI waits and says so.
    setHideIdentity(hiding: HideIdentity) {
      const s = store.getState();
      if (s.identity.pending || hiding === s.identity.mode) return;
      store.setState({
        identity: {
          mode: hiding,
          pending: s.connected,
          pseudonym: s.identity.pseudonym,
        },
      });
      if (!send({ type: "setIdentityVisibility", hiding }))
        store.setState({
          identity: { ...s.identity, error: "Команда не принята приложением" },
        });
      touch();
    },
    // The server stores the name; the own profile follows through the players list.
    changeDisplayName(name: string) {
      const s = store.getState();
      const displayName = name.trim();
      if (!displayName || !s.connected || s.displayName.pending) return;
      store.setState({ displayName: { pending: true } });
      if (!send({ type: "changeDisplayName", displayName }))
        store.setState({
          displayName: {
            pending: false,
            error: "Команда не принята приложением",
          },
        });
      touch();
    },
    // A personal filter by account ID; the host keeps and saves the list.
    ignore(playerId: string) {
      const s = store.getState();
      if (!playerId || playerId === s.selfId) return;
      if (!send({ type: "ignore", playerId }))
        store.setState({ notice: "Команда не принята приложением" });
    },
    unignore(playerId: string) {
      if (!send({ type: "unignore", playerId }))
        store.setState({ notice: "Команда не принята приложением" });
    },
    // Right click on an author: profile and ignore actions for that account,
    // a moderator's tools for the account and the message.
    openAuthorMenu(
      playerId: string,
      name: string,
      x: number,
      y: number,
      pseudonymous = false,
      message?: { channelId: string; id: string },
    ) {
      const s = store.getState();
      if (!s.visible || !s.active || !playerId) return;
      store.setState({
        authorMenu: { playerId, name, x, y, pseudonymous, message },
      });
    },
    closeAuthorMenu() {
      if (store.getState().authorMenu) store.setState({ authorMenu: null });
    },
    open(panel: Panel, playerId?: string) {
      if (!store.getState().visible) return;
      store.setState({
        panel,
        selectedPlayer: playerId ?? store.getState().selfId,
        authorMenu: null,
      });
      touch();
    },
  };
}
export type Chat = ReturnType<typeof makeChat>;
