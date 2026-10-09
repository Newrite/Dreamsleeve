import { describe, expect, it, vi } from "vitest";
import { makeChat } from "../src/state/chat";
import type {
  Command,
  GroundMark,
  HostEvent,
  Message,
} from "../src/bridge/types";

const snapshot: HostEvent = {
  type: "snapshot",
  serverName: "Голоса Тамриэля",
  channels: [{ id: "1", name: "Общий", kind: "global", writable: true }],
  messages: [],
  players: [],
  selfId: "1",
  groundMarksSupported: true,
};
const line = (id: string): Message => ({
  id,
  channelId: "1",
  source: "player",
  author: {
    id: "7",
    name: "Мира",
    displayName: "Мира",
    username: "mira",
    inCharacter: false,
  },
  text: "спам",
  time: 0,
});
const mark = (id: string, kind: GroundMark["kind"]): GroundMark => ({
  id,
  kind,
  text: kind === "note" ? "надпись" : "",
  time: 0,
  author: "Мира",
  location: "Skyrim.esm:00003C",
  x: 0,
  y: 0,
  z: 0,
});
function moderator(role = true) {
  const send = vi.fn((_command: Command) => true);
  const chat = makeChat(send);
  chat.receive(snapshot);
  if (role) chat.receive({ type: "role", moderator: true });
  const sent = () => send.mock.calls.map(([command]) => command);
  return { chat, send, sent };
}

describe("moderator tools", () => {
  it("drops old-session moderator work when the projected role stays the same", () => {
    const { chat, sent } = moderator();
    chat.moderator.listSanctions();
    chat.receive({ type: "moderationResult", requestId: "m1", sanctions: [] });
    chat.moderator.listMarks("7", "Мира");
    chat.receive({
      type: "moderationResult",
      requestId: "m2",
      playerId: "7",
      marks: [mark("3", "note")],
    });
    chat.moderator.openDialog("mute", "7", "Мира");
    chat.moderator.submitDialog({
      reason: "Флуд",
      notes: false,
      deaths: false,
    });
    chat.moderator.listSanctions();
    expect(chat.store.getState().moderation?.request).toBe("m3");

    // Host can coalesce a Moderator -> Player -> Moderator reconnect.
    chat.receive(snapshot);
    expect(chat.store.getState()).toMatchObject({
      moderator: true,
      moderation: null,
      sanctions: null,
      playerMarks: null,
      panel: null,
    });
    chat.receive({
      type: "moderationResult",
      requestId: "m3",
      error: "old refusal",
    });
    chat.receive({ type: "moderationResult", requestId: "m4", sanctions: [] });
    expect(chat.store.getState()).toMatchObject({
      moderation: null,
      sanctions: null,
      notice: "",
    });

    chat.moderator.listSanctions();
    expect(sent().at(-1)).toEqual({ type: "listSanctions", requestId: "m5" });
    chat.receive({ type: "moderationResult", requestId: "m5", sanctions: [] });
    expect(chat.store.getState().sanctions).toEqual([]);
  });
  it("keeps current moderator requests and dialogs across a refresh snapshot", () => {
    const { chat } = moderator();
    chat.moderator.openDialog("kick", "7", "Мира");
    chat.moderator.submitDialog({
      reason: "Флуд",
      notes: false,
      deaths: false,
    });
    chat.moderator.listSanctions();
    chat.receive({ ...snapshot, refresh: true });
    expect(chat.store.getState().moderation?.request).toBe("m1");
    chat.receive({ type: "moderationResult", requestId: "m2", sanctions: [] });
    expect(chat.store.getState().sanctions).toEqual([]);
    chat.receive({
      type: "moderationResult",
      requestId: "m1",
      error: "current refusal",
    });
    expect(chat.store.getState().moderation?.error).toBe("current refusal");
  });
  it("come with the role and go with it, closing what they opened", () => {
    const { chat } = moderator();
    chat.open("moderation");
    chat.moderator.openDialog("mute", "7", "Мира");
    expect(chat.store.getState().moderation?.action).toBe("mute");
    chat.receive({ type: "role", moderator: false });
    const s = chat.store.getState();
    expect(s.moderator).toBe(false);
    expect(s.moderation).toBeNull();
    expect(s.panel).toBeNull();
  });
  it("send nothing without the role, and never act on oneself", () => {
    const { chat, sent } = moderator(false);
    chat.moderator.listSanctions();
    chat.moderator.openDialog("ban", "7", "Мира");
    expect(sent()).toEqual([]);
    expect(chat.store.getState().moderation).toBeNull();
    chat.receive({ type: "role", moderator: true });
    chat.moderator.openDialog("ban", "1", "Я");
    expect(chat.store.getState().moderation).toBeNull();
  });
  it("send a mute with its term and reason, ticked removals apart; a refusal stays in the dialog", () => {
    const { chat, sent } = moderator();
    chat.moderator.openDialog("mute", "7", "Мира");
    chat.moderator.submitDialog({
      minutes: 60,
      reason: " Флуд ",
      notes: false,
      deaths: true,
    });
    expect(sent()).toEqual([
      {
        type: "sanctionPlayer",
        requestId: "m1",
        playerId: "7",
        kind: "mute",
        minutes: 60,
        reason: "Флуд",
      },
      {
        type: "clearPlayerMarks",
        requestId: "m2",
        playerId: "7",
        notes: false,
        deaths: true,
      },
    ]);
    // One answer at a time: a second submit waits.
    chat.moderator.submitDialog({ reason: "ещё", notes: false, deaths: false });
    expect(sent()).toHaveLength(2);
    chat.receive({
      type: "moderationResult",
      requestId: "m1",
      error: "Модератор не может наказать себя или другого модератора",
    });
    expect(chat.store.getState().moderation).toMatchObject({
      error: "Модератор не может наказать себя или другого модератора",
      request: undefined,
    });
    chat.receive({ type: "moderationResult", requestId: "m2", removed: 3 });
    expect(chat.store.getState().notice).toBe("Удалено меток игрока Мира: 3");
    // Until lifted: no minutes on the wire.
    chat.moderator.submitDialog({
      reason: "Флуд",
      notes: false,
      deaths: false,
    });
    expect(sent()[2]).not.toHaveProperty("minutes");
    chat.receive({
      type: "moderationResult",
      requestId: "m3",
      sanction: {
        playerId: "7",
        name: "Мира",
        kind: "mute",
        reason: "Флуд",
        issuedAt: 0,
      },
    });
    expect(chat.store.getState().moderation).toBeNull();
    expect(chat.store.getState().notice).toBe("Мут бессрочно: Мира");
  });
  it("a ban covers the devices only when ticked; a mute never does", () => {
    const { chat, sent } = moderator();
    chat.moderator.openDialog("mute", "7", "Мира");
    chat.moderator.submitDialog({
      reason: "Флуд",
      notes: false,
      deaths: false,
      devices: true,
    });
    expect(sent()[0]).not.toHaveProperty("devices");
    chat.moderator.closeDialog();
    chat.moderator.openDialog("ban", "7", "Мира");
    chat.moderator.submitDialog({
      reason: "Спам",
      notes: false,
      deaths: false,
      devices: true,
    });
    expect(sent()[1]).toMatchObject({
      type: "sanctionPlayer",
      kind: "ban",
      devices: true,
    });
  });
  it("a kick needs a reason; removing marks needs a kind", () => {
    const { chat, sent } = moderator();
    chat.moderator.openDialog("kick", "7", "Мира");
    chat.moderator.submitDialog({ reason: "  ", notes: false, deaths: false });
    expect(sent()).toEqual([]);
    chat.moderator.submitDialog({
      reason: "Остынь",
      notes: true,
      deaths: true,
    });
    expect(sent()).toEqual([
      { type: "kickPlayer", requestId: "m1", playerId: "7", reason: "Остынь" },
    ]);
    chat.moderator.closeDialog();
    chat.moderator.openDialog("clear", "7", "Мира", { notes: true });
    expect(chat.store.getState().moderation).toMatchObject({
      notes: true,
      deaths: false,
    });
    chat.moderator.submitDialog({ reason: "", notes: false, deaths: false });
    expect(sent()).toHaveLength(1);
  });
  it("list sanctions in force and lift one of them", () => {
    const { chat, sent } = moderator();
    chat.moderator.listSanctions();
    expect(chat.store.getState().sanctions).toBeNull();
    chat.receive({
      type: "moderationResult",
      requestId: "m1",
      sanctions: [
        {
          playerId: "7",
          name: "Мира",
          kind: "ban",
          reason: "Читы",
          issuedAt: 0,
        },
        { playerId: "9", kind: "mute", reason: "Флуд", issuedAt: 0, until: 5 },
      ],
    });
    const [ban] = chat.store.getState().sanctions!;
    chat.moderator.lift(ban);
    expect(sent()[1]).toEqual({
      type: "liftSanction",
      requestId: "m2",
      playerId: "7",
      kind: "ban",
    });
    chat.receive({ type: "moderationResult", requestId: "m2", playerId: "7" });
    expect(chat.store.getState().sanctions?.map((s) => s.playerId)).toEqual([
      "9",
    ]);
    expect(chat.store.getState().notice).toBe("Бан снят: Мира");
  });
  it("a removed message leaves the history for everyone; the moderator hears it went", () => {
    const { chat, sent } = moderator();
    chat.receive({ type: "messages", messages: [line("5"), line("6")] });
    chat.moderator.deleteMessage("1", "5");
    expect(sent()).toEqual([
      {
        type: "deleteChatMessage",
        requestId: "m1",
        channelId: "1",
        messageId: "5",
      },
    ]);
    chat.receive({
      type: "messagesRemoved",
      channelId: "1",
      messageIds: ["5"],
    });
    expect(chat.store.getState().messages.map((m) => m.id)).toEqual(["6"]);
    chat.receive({ type: "moderationResult", requestId: "m1" });
    expect(chat.store.getState().notice).toBe("Сообщение удалено");
  });
  it("a player's marks open in the moderation panel and a removed one leaves the list", () => {
    const { chat, sent } = moderator();
    chat.moderator.listMarks("7", "Мира");
    chat.receive({
      type: "moderationResult",
      requestId: "m1",
      playerId: "7",
      marks: [mark("3", "note"), mark("4", "death")],
    });
    expect(chat.store.getState().panel).toBe("moderation");
    chat.removeMark("4");
    const removal = sent()[1] as Extract<Command, { type: "removeGroundMark" }>;
    chat.receive({
      type: "markResult",
      requestId: removal.requestId,
      removed: true,
    });
    expect(chat.store.getState().playerMarks?.marks.map((m) => m.id)).toEqual([
      "3",
    ]);
  });
});
