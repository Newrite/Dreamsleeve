import type {
  Channel,
  Command,
  Guild,
  GuildInvite,
  GuildLimits,
  GuildMember,
  HostEvent,
  Player,
} from "../bridge/types";
// A guild channel's ID follows from the guild, above every server channel.
const GUILD_BASE = 4294967296;
const LIMITS: GuildLimits = {
  perPlayer: 3,
  members: 64,
  nameMin: 3,
  nameMax: 24,
};
type GuildRemoval = Extract<HostEvent, { type: "guilds" }>["removed"][number];
type GuildCommand = Extract<Command, { type: "guild" }>;
// Stand-in for the server's guild owner: the player leads one guild, is a
// member of another and has an invitation to a third. Answers come after a
// short wait, like over the network, in the host's order: channels, guilds,
// then the request's result.
export function makeGuildStand(
  receive: (event: HostEvent) => void,
  players: Player[],
  name: (player: Player) => string,
) {
  const self = players[0];
  const now = Date.now();
  const member = (
    player: Player,
    role: GuildMember["role"],
    online = true,
  ): GuildMember => ({
    id: player.id,
    name: name(player),
    role,
    online,
    joinedAt: now - 86400000,
  });
  const guilds: Guild[] = [
    {
      id: "1",
      name: "Соратники",
      channelId: String(GUILD_BASE + 1),
      createdAt: now - 30 * 86400000,
      members: [
        member(self, "master"),
        member(players[1], "officer"),
        member(players[2], "member", false),
      ],
    },
    {
      id: "2",
      name: "Вороны",
      channelId: String(GUILD_BASE + 2),
      createdAt: now - 7 * 86400000,
      members: [
        member(players[2], "master"),
        { ...member(self, "member"), mute: undefined },
      ],
    },
  ];
  const invites: GuildInvite[] = [
    {
      guildId: "3",
      guildName: "СеребрянаяРука",
      invitedBy: players[1].id,
      inviter: name(players[1]),
      expires: now + 7 * 86400000,
    },
  ];
  let nextGuild = 10;
  const channels = (): Channel[] =>
    guilds.map((g) => ({
      id: g.channelId,
      kind: "guild",
      name: g.name,
      writable: true,
    }));
  const later = (run: () => void) => setTimeout(run, 300);
  function changed(channelsChanged: boolean, removed: GuildRemoval[] = []) {
    if (channelsChanged) receive({ type: "channels", channels: all() });
    receive({
      type: "guilds",
      guilds: structuredClone(guilds),
      invites: [...invites],
      limits: LIMITS,
      removed,
    });
  }
  // Set by the stand's owner: the server channels before the guilds.
  let serverChannels: Channel[] = [];
  const all = () => [...serverChannels, ...channels()];
  const find = (id: string) => guilds.find((g) => g.id === id);
  function remove(guildId: string, reason: GuildRemoval["reason"]) {
    const index = guilds.findIndex((g) => g.id === guildId);
    if (index < 0) return;
    const [gone] = guilds.splice(index, 1);
    changed(true, [{ guildId, name: gone.name, reason }]);
  }
  function answer(c: GuildCommand): string | undefined {
    const guild = c.action === "create" ? undefined : find(c.guildId);
    const target =
      "playerId" in c
        ? guild?.members.find((m) => m.id === c.playerId)
        : undefined;
    switch (c.action) {
      case "create": {
        if (guilds.length >= LIMITS.perPlayer)
          return "Достигнут предел гильдий на игрока";
        if (guilds.some((g) => g.name.toLowerCase() === c.name.toLowerCase()))
          return "Гильдия с таким названием уже есть";
        const id = String(nextGuild++);
        guilds.push({
          id,
          name: c.name,
          channelId: String(GUILD_BASE + Number(id)),
          createdAt: Date.now(),
          members: [member(self, "master")],
        });
        changed(true);
        return undefined;
      }
      case "answer": {
        const index = invites.findIndex((i) => i.guildId === c.guildId);
        if (index < 0) return "Игрок не в гильдии или приглашения уже нет";
        const [invite] = invites.splice(index, 1);
        if (c.accept)
          guilds.push({
            id: invite.guildId,
            name: invite.guildName,
            channelId: String(GUILD_BASE + Number(invite.guildId)),
            createdAt: Date.now() - 365 * 86400000,
            members: [member(players[1], "master"), member(self, "member")],
          });
        changed(c.accept);
        return undefined;
      }
      case "invite":
        return players.some((p) => p.id === c.playerId)
          ? undefined
          : "Игрок не в сети";
      case "leave":
        if (!guild) return "Гильдия не найдена";
        if (guild.members.find((m) => m.id === self.id)?.role === "master")
          return "Глава не может выйти: сначала передайте роль или распустите гильдию";
        remove(c.guildId, "left");
        return undefined;
      case "disband":
        remove(c.guildId, "disbanded");
        return undefined;
      default:
        if (!guild || !target)
          return "Игрок не в гильдии или приглашения уже нет";
        if (c.action === "exclude")
          guild.members = guild.members.filter((m) => m !== target);
        else if (c.action === "setRole") target.role = c.role;
        else if (c.action === "transfer") {
          for (const m of guild.members)
            if (m.role === "master") m.role = "officer";
          target.role = "master";
        } else if (c.action === "mute")
          target.mute = {
            reason: c.reason,
            ...(c.minutes === undefined
              ? {}
              : { until: Date.now() + c.minutes * 60000 }),
          };
        else delete target.mute;
        changed(false);
        return undefined;
    }
  }
  return {
    setServerChannels(list: Channel[]) {
      serverChannels = list;
    },
    channels,
    publish: () => changed(false),
    command(c: GuildCommand) {
      later(() => {
        const error = answer(c);
        const guildId =
          c.action === "create"
            ? String(nextGuild - 1)
            : "guildId" in c
              ? c.guildId
              : "";
        receive({
          type: "guildResult",
          requestId: c.requestId,
          ...(error ? { error } : { guildId }),
        });
      });
      return true;
    },
    // Workshop buttons: an invitation arrives, an officer excludes the
    // player, the master mutes them.
    invite() {
      const id = String(nextGuild++);
      invites.push({
        guildId: id,
        guildName: `Гильдия${id}`,
        invitedBy: players[2].id,
        inviter: name(players[2]),
        expires: Date.now() + 7 * 86400000,
      });
      changed(false);
    },
    exclude() {
      const guild = guilds.find(
        (g) => g.members.find((m) => m.id === self.id)?.role !== "master",
      );
      if (guild) remove(guild.id, "excluded");
    },
    mute() {
      const own = guilds
        .flatMap((g) => g.members)
        .find((m) => m.id === self.id && m.role === "member");
      if (!own) return;
      own.mute = { reason: "Флуд в гильдии", until: Date.now() + 15 * 60000 };
      changed(false);
    },
    // A guild a message from another member can go to, with its author.
    speaker() {
      const guild = guilds[0];
      const author = guild?.members.find((m) => m.id !== self.id);
      const player = author && players.find((p) => p.id === author.id);
      return guild && player
        ? { channelId: guild.channelId, player }
        : undefined;
    },
  };
}
