import type { StoreApi } from "zustand/vanilla";
import type {
  Command,
  GroundMark,
  GroundMarkKind,
  HostEvent,
  Sanction,
  SanctionKind,
  Send,
} from "../bridge/types";
import type { ChatState } from "./chat";
import { term } from "./moderation";

// Terms a moderator picks from, in minutes; without minutes until lifted.
export const TERMS: { label: string; minutes?: number }[] = [
  { label: "15 минут", minutes: 15 },
  { label: "1 час", minutes: 60 },
  { label: "1 день", minutes: 24 * 60 },
  { label: "7 дней", minutes: 7 * 24 * 60 },
  { label: "30 дней", minutes: 30 * 24 * 60 },
  { label: "Бессрочно" },
];
export const sanctionLabels: Record<SanctionKind, string> = {
  mute: "Мут",
  ban: "Бан",
};
// A player the page knows only by ID: an offline one never met here.
export const sanctionName = (s: Sanction) => s.name ?? `#${s.playerId}`;

export type ModerationAction = SanctionKind | "kick" | "clear";
// The dialog of one action on one player. request is the answer it waits
// for; notes/deaths are the removals ticked (default off, except when the
// dialog opens to remove marks).
export interface ModerationDialog {
  action: ModerationAction;
  playerId: string;
  name: string;
  notes: boolean;
  deaths: boolean;
  request?: string;
  error?: string;
}
export interface ModeratorState {
  moderator: boolean;
  // Sanctions in force as last listed; null until the moderator asks.
  sanctions: Sanction[] | null;
  // Every mark of one player, as last listed.
  playerMarks: { playerId: string; name: string; marks: GroundMark[] } | null;
  moderation: ModerationDialog | null;
}
export const idleModerator: ModeratorState = {
  moderator: false,
  sanctions: null,
  playerMarks: null,
  moderation: null,
};
// What the form of the dialog holds when it is sent.
export interface ModerationForm {
  minutes?: number;
  reason: string;
  notes: boolean;
  deaths: boolean;
}
type Request =
  | { action: "sanction"; kind: SanctionKind; name: string }
  | { action: "lift"; kind: SanctionKind; name: string }
  | { action: "kick"; name: string }
  | { action: "list" }
  | { action: "marks"; name: string }
  | { action: "clear"; kinds: GroundMarkKind[]; name: string }
  | { action: "delete" };
type Result = Extract<HostEvent, { type: "moderationResult" }>;

// Moderator tools of the chat page. The server checks the role and whom a
// moderator may act on; the page shows what it answers. Requests carry
// their own IDs ("m1", ...), apart from chat and marks.
export function makeModerator(store: StoreApi<ChatState>, send: Send) {
  let sequence = 0;
  const requests = new Map<string, Request>();
  const notice = (text: string) => store.setState({ notice: text });
  // The command goes out with a fresh ID; false when the host did not take it.
  function request(
    make: (requestId: string) => Command,
    what: Request,
  ): string | undefined {
    const s = store.getState();
    if (!s.moderator || !s.connected) return undefined;
    const requestId = `m${++sequence}`;
    requests.set(requestId, what);
    if (send(make(requestId))) return requestId;
    requests.delete(requestId);
    notice("Команда не принята приложением");
    return undefined;
  }
  function upsert(sanction: Sanction) {
    const listed = store.getState().sanctions;
    if (!listed) return;
    const others = listed.filter(
      (s) => s.playerId !== sanction.playerId || s.kind !== sanction.kind,
    );
    store.setState({ sanctions: [sanction, ...others] });
  }
  // A dialog closes on its answer; a refusal stays in it with the reason.
  function settleDialog(requestId: string, error?: string) {
    const dialog = store.getState().moderation;
    if (dialog?.request !== requestId) return false;
    store.setState({
      moderation:
        error === undefined
          ? null
          : { ...dialog, request: undefined, error: error },
    });
    return true;
  }
  function result(event: Result) {
    const what = requests.get(event.requestId);
    if (!what) return;
    requests.delete(event.requestId);
    if (event.error !== undefined) {
      if (!settleDialog(event.requestId, event.error))
        notice(`Не удалось: ${event.error}`);
      return;
    }
    settleDialog(event.requestId);
    const s = store.getState();
    switch (what.action) {
      case "sanction":
        if (event.sanction) upsert(event.sanction);
        notice(
          `${sanctionLabels[what.kind]} ${term(event.sanction?.until)}: ${what.name}`,
        );
        break;
      case "lift":
        store.setState({
          sanctions:
            s.sanctions?.filter(
              (x) => x.playerId !== event.playerId || x.kind !== what.kind,
            ) ?? null,
        });
        notice(`${sanctionLabels[what.kind]} снят: ${what.name}`);
        break;
      case "kick":
        notice(`Сессия закрыта: ${what.name}`);
        break;
      case "list":
        store.setState({ sanctions: event.sanctions ?? [] });
        break;
      case "marks":
        store.setState({
          playerMarks: {
            playerId: event.playerId ?? "",
            name: what.name,
            marks: event.marks ?? [],
          },
          panel: "moderation",
        });
        break;
      case "clear": {
        const listed = s.playerMarks;
        if (listed && listed.playerId === event.playerId)
          store.setState({
            playerMarks: {
              ...listed,
              marks: listed.marks.filter((m) => !what.kinds.includes(m.kind)),
            },
          });
        notice(`Удалено меток игрока ${what.name}: ${event.removed ?? 0}`);
        break;
      }
      case "delete":
        notice("Сообщение удалено");
        break;
    }
  }
  function clear(
    playerId: string,
    name: string,
    notes: boolean,
    deaths: boolean,
  ) {
    const kinds: GroundMarkKind[] = [
      ...(notes ? (["note"] as const) : []),
      ...(deaths ? (["death"] as const) : []),
    ];
    if (!kinds.length) return undefined;
    return request(
      (requestId) => ({
        type: "clearPlayerMarks",
        requestId,
        playerId,
        notes,
        deaths,
      }),
      { action: "clear", kinds, name },
    );
  }
  return {
    receive(event: Extract<HostEvent, { type: "role" | "moderationResult" }>) {
      if (event.type === "moderationResult") {
        result(event);
        return;
      }
      if (event.moderator) {
        store.setState({ moderator: true });
        return;
      }
      // The tools go with the role; open answers are no longer shown.
      requests.clear();
      const panel = store.getState().panel;
      store.setState({
        ...idleModerator,
        panel: panel === "moderation" ? null : panel,
      });
    },
    // Opens the dialog of an action; removing marks asks which kinds.
    openDialog(
      action: ModerationAction,
      playerId: string,
      name: string,
      marks: { notes?: boolean; deaths?: boolean } = {},
    ) {
      const s = store.getState();
      if (!s.moderator || !playerId || playerId === s.selfId) return;
      store.setState({
        moderation: {
          action,
          playerId,
          name,
          notes: marks.notes ?? false,
          deaths: marks.deaths ?? false,
        },
        authorMenu: null,
      });
    },
    closeDialog() {
      if (store.getState().moderation) store.setState({ moderation: null });
    },
    // A mute or a ban with the ticked removals, a kick, or the removals alone.
    submitDialog(form: ModerationForm) {
      const dialog = store.getState().moderation;
      if (!dialog || dialog.request) return;
      const reason = form.reason.trim();
      const { action, playerId, name } = dialog;
      if (action === "clear") {
        const sent = clear(playerId, name, form.notes, form.deaths);
        if (sent) store.setState({ moderation: { ...dialog, request: sent } });
        return;
      }
      if (!reason) return;
      const sent =
        action === "kick"
          ? request(
              (requestId) => ({
                type: "kickPlayer",
                requestId,
                playerId,
                reason,
              }),
              { action: "kick", name },
            )
          : request(
              (requestId) => ({
                type: "sanctionPlayer",
                requestId,
                playerId,
                kind: action,
                reason,
                ...(form.minutes === undefined
                  ? {}
                  : { minutes: form.minutes }),
              }),
              { action: "sanction", kind: action, name },
            );
      if (!sent) return;
      store.setState({
        moderation: { ...dialog, request: sent, error: undefined },
      });
      if (action !== "kick") clear(playerId, name, form.notes, form.deaths);
    },
    lift(sanction: Sanction) {
      request(
        (requestId) => ({
          type: "liftSanction",
          requestId,
          playerId: sanction.playerId,
          kind: sanction.kind,
        }),
        { action: "lift", kind: sanction.kind, name: sanctionName(sanction) },
      );
    },
    listSanctions() {
      request((requestId) => ({ type: "listSanctions", requestId }), {
        action: "list",
      });
    },
    // Every mark of the player, far ones included; shown in the moderation panel.
    listMarks(playerId: string, name: string) {
      store.setState({ authorMenu: null });
      request(
        (requestId) => ({ type: "listPlayerMarks", requestId, playerId }),
        {
          action: "marks",
          name,
        },
      );
    },
    deleteMessage(channelId: string, messageId: string) {
      store.setState({ authorMenu: null });
      request(
        (requestId) => ({
          type: "deleteChatMessage",
          requestId,
          channelId,
          messageId,
        }),
        { action: "delete" },
      );
    },
  };
}
export type Moderator = ReturnType<typeof makeModerator>;
// The moderator's actions on another player, for the author menu and the profile.
export const playerActions = (
  moderator: Moderator,
  playerId: string,
  name: string,
) => [
  {
    label: "Мут…",
    run: () => moderator.openDialog("mute", playerId, name),
  },
  { label: "Бан…", run: () => moderator.openDialog("ban", playerId, name) },
  { label: "Кик…", run: () => moderator.openDialog("kick", playerId, name) },
  { label: "Метки игрока", run: () => moderator.listMarks(playerId, name) },
  {
    label: "Удалить все надписи…",
    run: () => moderator.openDialog("clear", playerId, name, { notes: true }),
  },
  {
    label: "Удалить все места смерти…",
    run: () => moderator.openDialog("clear", playerId, name, { deaths: true }),
  },
];
