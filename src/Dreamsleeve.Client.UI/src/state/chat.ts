import { createStore } from "zustand/vanilla";
import type {
  AuthOperation,
  AuthState,
  Command,
  ConnectionPhase,
  Channel,
  HostEvent,
  Message,
  Player,
  Send,
  Settings,
} from "../bridge/types";
import { idleAuth } from "./auth";
import { defaults, settingsFrom } from "./settings";
export const HISTORY_LIMIT = 500;
export type Panel =
  "online" | "profile" | "stats" | "settings" | "account" | null;
export interface PendingMessage {
  channelId: string;
  text: string;
  time: number;
  status: "sending" | "failed" | "unknown";
  error?: string;
  messageId?: string;
}
export const sending = (state: ChatState) =>
  Object.values(state.pending).some((p) => p.status === "sending");
export interface ChatState {
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
  // Context menu of a message author, at viewport coordinates.
  authorMenu: { playerId: string; name: string; x: number; y: number } | null;
}
export const visible = (message: Message, filter: string) =>
  filter === "all" || message.channelId === filter;
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
  }));
  let sequence = 0;
  const touch = () => store.setState({ activity: now(), faded: false });
  function receive(event: HostEvent) {
    const state = store.getState();
    switch (event.type) {
      case "snapshot": {
        const channels = event.channels;
        if (event.refresh && state.initialized) {
          // Same session re-projected: keep pending rows, filters and scroll.
          const messages = event.messages.slice(-HISTORY_LIMIT);
          const retained = new Set(messages.map((m) => m.id));
          const receivedAt = Object.fromEntries(
            Object.entries(state.receivedAt).filter(([id]) => retained.has(id)),
          );
          store.setState({
            channels,
            messages,
            receivedAt,
            players: event.players,
            settings: event.settings
              ? settingsFrom({
                  ...event.settings,
                  // A local switch may be newer than the host copy.
                  nameMode: state.settings.nameMode,
                  streamerMode: state.settings.streamerMode,
                  textFilter: state.settings.textFilter,
                })
              : state.settings,
          });
          break;
        }
        store.setState({
          channels,
          messages: event.messages.slice(-HISTORY_LIMIT),
          receivedAt: {},
          players: event.players,
          selfId: event.selfId,
          serverName: event.serverName,
          initialized: true,
          connected: true,
          connectionPhase: "connected",
          pending:
            state.selfId === event.selfId &&
            state.serverName === event.serverName
              ? Object.fromEntries(
                  Object.entries(state.pending)
                    .filter(
                      ([, p]) =>
                        !event.messages.some((m) => m.id === p.messageId),
                    )
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
          settings: event.settings
            ? settingsFrom(event.settings)
            : state.settings,
          notice: "",
          scrolled: false,
        });
        touch();
        break;
      }
      case "messages": {
        const seen = new Set(state.messages.map((m) => m.id));
        const channels = new Set(state.channels.map((c) => c.id));
        const added = event.messages.filter((m) => {
          if (seen.has(m.id) || !channels.has(m.channelId)) return false;
          seen.add(m.id);
          return true;
        });
        const unread = { ...state.unread };
        for (const m of added)
          if (!state.active || state.scrolled || !visible(m, state.filter))
            unread[m.channelId] = Math.min(
              HISTORY_LIMIT,
              (unread[m.channelId] ?? 0) + 1,
            );
        const messages = [...state.messages, ...added].slice(-HISTORY_LIMIT);
        const receivedAt = { ...state.receivedAt };
        const receipt = now();
        for (const message of added) receivedAt[message.id] = receipt;
        const retained = new Set(messages.map((message) => message.id));
        for (const id of Object.keys(receivedAt))
          if (!retained.has(id)) delete receivedAt[id];
        const pending = Object.fromEntries(
          Object.entries(state.pending).filter(
            ([, p]) => !seen.has(p.messageId ?? ""),
          ),
        );
        store.setState({ messages, receivedAt, unread, pending });
        break;
      }
      case "players":
        store.setState({ players: event.players });
        break;
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
              p.status === "sending" ? { ...p, status: "unknown" as const } : p,
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
          };
        } else if (state.messages.some((m) => m.id === event.messageId)) {
          delete pending[event.requestId];
        } else {
          pending[event.requestId] = { ...item, messageId: event.messageId };
        }
        store.setState({ pending });
        break;
      }
      case "settings":
        store.setState({ settings: settingsFrom(event.settings) });
        break;
      case "settingsResult":
        if (event.revision !== state.revision) break;
        store.setState({
          savedRevision: event.error ? state.savedRevision : event.revision,
          notice: event.error ?? "Настройки сохранены",
        });
        break;
    }
  }
  function close() {
    if (!send({ type: "close" })) {
      store.setState({ notice: "Не удалось вернуть управление игре" });
      return;
    }
    receive({ type: "deactivate" });
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
    if (Object.keys(s.pending).length >= 16) {
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
        ...s.pending,
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
          ...s.pending,
          [requestId]: {
            channelId: s.target,
            text,
            time: now(),
            status: "failed",
            error: "Команда не принята приложением",
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
      if (s.filter === "all" || id === s.filter) unread[id] = 0;
    store.setState({ unread, scrolled: false });
  }
  return {
    store,
    expirePending() {
      const s = store.getState();
      const pending = Object.fromEntries(
        Object.entries(s.pending).map(([id, p]) => [
          id,
          p.status === "sending" && now() - p.time >= 15000
            ? { ...p, status: "unknown" as const }
            : p,
        ]),
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
      submit();
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
      const settings = settingsFrom({ ...current.settings, ...patch });
      store.setState({
        settings,
        revision: current.revision + 1,
        notice: "Настройки изменены. Нажмите «Сохранить настройки».",
      });
      // Names and the text filter apply to every surface at once, the game included.
      if (
        settings.nameMode !== current.settings.nameMode ||
        settings.streamerMode !== current.settings.streamerMode ||
        settings.textFilter !== current.settings.textFilter
      ) {
        if (
          !send({
            type: "displaySettings",
            nameMode: settings.nameMode,
            streamerMode: settings.streamerMode,
            textFilter: settings.textFilter,
          })
        )
          store.setState({ notice: "Команда не принята приложением" });
        else store.setState({ notice: "Отображение применено" });
      }
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
    // Right click on an author: profile and ignore actions for that account.
    openAuthorMenu(playerId: string, name: string, x: number, y: number) {
      const s = store.getState();
      if (!s.visible || !s.active || !playerId) return;
      store.setState({ authorMenu: { playerId, name, x, y } });
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
