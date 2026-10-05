import { describe, expect, it, vi } from "vitest";
import { GUILDS, makeChat, visible } from "../src/state/chat";
import { mayRemove } from "../src/state/guilds";
import { parseHostEvent } from "../src/bridge/parse";
import type {
  Channel,
  Command,
  Guild,
  GuildMember,
  HostEvent,
  Message,
} from "../src/bridge/types";

const SELF = "1";
const global: Channel = {
  id: "1",
  kind: "global",
  name: "Общий",
  writable: true,
};
const system: Channel = {
  id: "2",
  kind: "system",
  name: "Объявления",
  writable: false,
};
const ravens: Channel = {
  id: "4294967300",
  kind: "guild",
  name: "Вороны",
  writable: true,
};
const companions: Channel = {
  id: "4294967301",
  kind: "guild",
  name: "Соратники",
  writable: true,
};
const member = (
  id: string,
  role: GuildMember["role"],
  extra: Partial<GuildMember> = {},
): GuildMember => ({
  id,
  name: `Игрок ${id}`,
  role,
  online: true,
  joinedAt: 0,
  ...extra,
});
const guild = (
  id: string,
  channel: Channel,
  members: GuildMember[],
): Guild => ({
  id,
  name: channel.name,
  channelId: channel.id,
  createdAt: 0,
  members,
});
const guildsEvent = (
  guilds: Guild[],
  extra: Partial<Extract<HostEvent, { type: "guilds" }>> = {},
): HostEvent => ({
  type: "guilds",
  guilds,
  invites: [],
  limits: { perPlayer: 3, members: 64, nameMin: 3, nameMax: 24 },
  removed: [],
  ...extra,
});
const line = (id: string, channelId: string): Message => ({
  id,
  channelId,
  source: "player",
  author: {
    id: "7",
    name: "Мира",
    displayName: "Мира",
    username: "mira",
    inCharacter: false,
  },
  text: "привет",
  time: Number(id),
});
// Sending a line also hands control back to the game: only the chat commands.
const chats = (commands: Command[]) =>
  commands.filter((c) => c.type === "sendChat");
// A session with two guilds: the player leads one and is a member of the other.
function inGuilds() {
  const send = vi.fn((_command: Command) => true);
  const chat = makeChat(send);
  chat.receive({
    type: "snapshot",
    serverName: "Голоса Тамриэля",
    channels: [global, system, ravens, companions],
    messages: [
      line("1", global.id),
      line("2", ravens.id),
      line("3", companions.id),
    ],
    players: [],
    selfId: SELF,
  });
  const guilds = [
    guild("4", ravens, [member(SELF, "master"), member("7", "officer")]),
    guild("5", companions, [member("8", "master"), member(SELF, "member")]),
  ];
  chat.receive(guildsEvent(guilds));
  const sent = () => send.mock.calls.map(([command]) => command);
  return { chat, send, sent, guilds };
}

describe("guilds in the chat", () => {
  it("start as a baseline: the first guilds of a session are no news", () => {
    const { chat } = inGuilds();
    const s = chat.store.getState();
    expect(s.guildsKnown).toBe(true);
    expect(s.guilds.map((g) => g.name)).toEqual(["Вороны", "Соратники"]);
    expect(s.selectedGuild).toBe("4");
    expect(s.notice).toBe("");
    // A new session forgets them until the server sends its own.
    chat.receive({
      type: "snapshot",
      serverName: "Голоса Тамриэля",
      channels: [global],
      messages: [],
      players: [],
      selfId: SELF,
    });
    expect(chat.store.getState().guildsKnown).toBe(false);
    expect(chat.store.getState().guilds).toEqual([]);
  });

  it("turn the server's changes into notices: invitation, role, mute, new master, exclusion", () => {
    const { chat, guilds } = inGuilds();
    const [ravensGuild, companionsGuild] = guilds;
    chat.receive(
      guildsEvent(guilds, {
        invites: [
          {
            guildId: "6",
            guildName: "СеребрянаяРука",
            invitedBy: "7",
            inviter: "Мира",
            expires: 0,
          },
        ],
      }),
    );
    expect(chat.store.getState().notice).toBe(
      "Приглашение в гильдию «СеребрянаяРука» от Мира",
    );
    chat.receive(
      guildsEvent([
        ravensGuild,
        {
          ...companionsGuild,
          members: [
            member("8", "officer"),
            member(SELF, "officer", {
              mute: { reason: "Флуд", until: undefined },
            }),
            member("9", "master"),
          ],
        },
      ]),
    );
    expect(chat.store.getState().notice).toBe(
      [
        "Ваша роль в гильдии «Соратники»: офицер",
        "Мут в гильдии «Соратники» бессрочно: Флуд",
        "Новый глава гильдии «Соратники»: Игрок 9",
      ].join(" · "),
    );
    chat.receive(
      guildsEvent([ravensGuild], {
        removed: [{ guildId: "5", name: "Соратники", reason: "excluded" }],
      }),
    );
    expect(chat.store.getState().notice).toBe(
      "Вас исключили из гильдии «Соратники»",
    );
  });

  it("follow the channel list: a guild's lines, draft and unread go with its channel", () => {
    const { chat } = inGuilds();
    chat.store.setState({ target: companions.id, filter: companions.id });
    chat.setDraft("черновик");
    chat.receive({ type: "channels", channels: [global, system, ravens] });
    const s = chat.store.getState();
    expect(s.messages.map((m) => m.channelId)).toEqual([global.id, ravens.id]);
    expect(s.drafts[companions.id]).toBeUndefined();
    expect(s.target).toBe(global.id);
    expect(s.filter).toBe("all");
    // A new guild's history arrives after its channel.
    chat.receive({
      type: "channels",
      channels: [global, system, ravens, companions],
    });
    chat.receive({ type: "messages", messages: [line("4", companions.id)] });
    expect(chat.store.getState().messages.at(-1)?.channelId).toBe(
      companions.id,
    );
  });

  it("join one view: «Гильдии» shows every guild channel and keeps it after sending", () => {
    const { chat, sent } = inGuilds();
    chat.receive({ type: "activate" });
    chat.select(GUILDS);
    const s = chat.store.getState();
    expect(s.filter).toBe(GUILDS);
    const shown = s.messages.filter((m) =>
      visible(m, s.filter, s.settings, s.channels),
    );
    expect(shown.map((m) => m.channelId)).toEqual([ravens.id, companions.id]);
    chat.store.setState({ target: ravens.id });
    chat.setDraft("в гильдию");
    chat.submit();
    expect(chats(sent()).at(-1)).toMatchObject({
      type: "sendChat",
      channelId: ravens.id,
      text: "в гильдию",
    });
    expect(chat.store.getState().filter).toBe(GUILDS);
  });

  it("turn the send target with the tab: a channel, «Гильдии», but not «Все» or announcements", () => {
    const { chat } = inGuilds();
    chat.receive({ type: "activate" });
    const target = () => chat.store.getState().target;
    expect(target()).toBe(global.id);
    chat.select(companions.id);
    expect(target()).toBe(companions.id);
    chat.select(GUILDS);
    expect(target()).toBe(companions.id);
    chat.select(global.id);
    expect(target()).toBe(global.id);
    chat.select(GUILDS);
    expect(target()).toBe(ravens.id);
    chat.select("all");
    expect(target()).toBe(ravens.id);
    chat.select(system.id);
    expect(target()).toBe(ravens.id);
  });

  it("keep a guild mute to its own channel: the global chat still goes out", () => {
    const { chat, sent, guilds } = inGuilds();
    const [ravensGuild, companionsGuild] = guilds;
    chat.receive(
      guildsEvent([
        ravensGuild,
        {
          ...companionsGuild,
          members: [
            member("8", "master"),
            member(SELF, "member", {
              mute: { reason: "Флуд", until: Date.now() + 60000 },
            }),
          ],
        },
      ]),
    );
    chat.receive({ type: "activate" });
    chat.store.setState({ target: companions.id });
    chat.setDraft("можно?");
    chat.submit();
    expect(sent().some((c) => c.type === "sendChat")).toBe(false);
    expect(chat.store.getState().notice).toMatch(
      /^Мут в гильдии «Соратники» до /,
    );
    chat.store.setState({ target: global.id });
    chat.setDraft("а здесь можно");
    chat.submit();
    expect(chats(sent()).at(-1)).toMatchObject({ channelId: global.id });
  });

  it("send requests with their own IDs and say how they ended", () => {
    const { chat, sent } = inGuilds();
    chat.guilds.create("  Изгнанники ");
    expect(sent().at(-1)).toEqual({
      type: "guild",
      requestId: "g1",
      action: "create",
      name: "Изгнанники",
    });
    chat.receive({ type: "guildResult", requestId: "g1", guildId: "10" });
    expect(chat.store.getState().notice).toBe("Гильдия «Изгнанники» создана");
    expect(chat.store.getState().guildRequests).toEqual({});
    chat.guilds.leave("5");
    chat.receive({
      type: "guildResult",
      requestId: "g2",
      error: "Ваша роль в гильдии этого не позволяет",
    });
    expect(chat.store.getState().notice).toBe(
      "Не удалось: Ваша роль в гильдии этого не позволяет",
    );
    // An answer to nobody's request changes nothing.
    chat.receive({ type: "guildResult", requestId: "g9", guildId: "4" });
    expect(chat.store.getState().notice).toBe(
      "Не удалось: Ваша роль в гильдии этого не позволяет",
    );
  });

  it("mute a member through a dialog that keeps a refusal and closes on success", () => {
    const { chat, sent } = inGuilds();
    chat.guilds.openMute("4", "7", "Игрок 7");
    chat.guilds.submitMute(60, "  Флуд ");
    expect(sent().at(-1)).toEqual({
      type: "guild",
      requestId: "g1",
      action: "mute",
      guildId: "4",
      playerId: "7",
      minutes: 60,
      reason: "Флуд",
    });
    chat.receive({
      type: "guildResult",
      requestId: "g1",
      error: "Игрок не в сети",
    });
    expect(chat.store.getState().guildMute?.error).toBe("Игрок не в сети");
    chat.guilds.submitMute(undefined, "Флуд");
    expect(sent().at(-1)).not.toHaveProperty("minutes");
    chat.receive({ type: "guildResult", requestId: "g2", guildId: "4" });
    expect(chat.store.getState().guildMute).toBeNull();
    expect(chat.store.getState().notice).toBe(
      "Игрок 7: мут в гильдии бессрочно",
    );
  });

  it("remove messages by the guild's ranks, answered like a moderator's request", () => {
    const { chat, sent, guilds } = inGuilds();
    const [ravensGuild, companionsGuild] = guilds;
    // The master removes anyone's; a member nobody's; nobody their own.
    expect(mayRemove(ravensGuild, SELF, "7")).toBe(true);
    expect(mayRemove(ravensGuild, SELF, "99")).toBe(true);
    expect(mayRemove(ravensGuild, SELF, SELF)).toBe(false);
    expect(mayRemove(companionsGuild, SELF, "8")).toBe(false);
    expect(mayRemove(ravensGuild, "7", "99")).toBe(true);
    expect(mayRemove(ravensGuild, "7", SELF)).toBe(false);
    chat.guilds.deleteMessage(ravens.id, "2");
    expect(sent().at(-1)).toEqual({
      type: "deleteChatMessage",
      requestId: "gd1",
      channelId: ravens.id,
      messageId: "2",
    });
    chat.receive({ type: "moderationResult", requestId: "gd1" });
    expect(chat.store.getState().notice).toBe("Сообщение удалено");
  });
});

describe("guild events at the bridge", () => {
  const sample = {
    type: "guilds",
    guilds: [
      {
        id: "4",
        name: "Вороны",
        channelId: "4294967300",
        createdAt: 0,
        members: [
          {
            id: "1",
            name: "Северный",
            role: "master",
            online: true,
            joinedAt: 0,
            mute: { reason: "Флуд" },
          },
        ],
      },
    ],
    invites: [
      { guildId: "5", guildName: "Соратники", invitedBy: "7", expires: 0 },
    ],
    limits: { perPlayer: 3, members: 64, nameMin: 3, nameMax: 24 },
    removed: [{ guildId: "6", name: "Изгнанники", reason: "disbanded" }],
  };
  it("accept what the host sends and refuse what it never would", () => {
    expect(parseHostEvent(JSON.stringify(sample)).type).toBe("guilds");
    const broken = structuredClone(sample);
    broken.guilds[0].members[0].role = "king";
    expect(() => parseHostEvent(JSON.stringify(broken))).toThrow();
    expect(() =>
      parseHostEvent(
        JSON.stringify({
          type: "guildResult",
          requestId: "g1",
          guildId: "4",
          error: "x",
        }),
      ),
    ).toThrow();
    expect(() =>
      parseHostEvent(JSON.stringify({ type: "guildResult", requestId: "g1" })),
    ).toThrow();
    expect(
      parseHostEvent(
        JSON.stringify({ type: "channels", channels: [{ ...ravens }] }),
      ).type,
    ).toBe("channels");
  });
});
