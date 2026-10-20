import type {
  Channel,
  Command,
  Guild,
  GuildApplication,
  GuildCatalogGuild,
  GuildInvite,
  GuildLimits,
  GuildMember,
  HostEvent,
  Message,
  Player,
} from "../bridge/types";
// A guild channel's ID follows from the guild, above every server channel.
const GUILD_BASE = 4294967296;
const LIMITS: GuildLimits = {
  perPlayer: 3,
  members: 64,
  nameMin: 3,
  nameMax: 24,
  titles: 10,
  infoMax: 500,
  joinRequests: 128,
  applications: 10,
};
// Guilds in one catalog page, like the server's [Guilds] CatalogPageSize.
const CATALOG_PAGE = 20;
type GuildsEvent = Extract<HostEvent, { type: "guilds" }>;
type GuildRemoval = GuildsEvent["removed"][number];
type ApplicationEnd = GuildsEvent["applicationsEnded"][number];
type GuildCommand = Extract<Command, { type: "guild" }>;
type CatalogCommand = Extract<Command, { type: "guildCatalog" }>;
// Other players' guilds the catalog shows: enough for several pages.
const CATALOG_NAMES = [
  "Волки",
  "Совы",
  "Лисы",
  "Медведи",
  "Драконоборцы",
  "Стражи Рифта",
  "Северный ветер",
  "Пепельные",
  "Морозные клинки",
  "Тени",
];
function catalogGuilds(now: number): GuildCatalogGuild[] {
  return Array.from({ length: 45 }, (_, index) => ({
    id: String(100 + index),
    name: `${CATALOG_NAMES[index % CATALOG_NAMES.length]} ${Math.floor(index / CATALOG_NAMES.length) + 1}`,
    info:
      index % 3 === 0
        ? ""
        : "Охотимся на драконов по выходным.\nНовичкам рады, пишите главе.",
    members: (index * 7) % 65,
    createdAt: now - (index + 1) * 86400000,
  }));
}
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
        { ...member(players[1], "officer"), title: "1" },
        member(players[2], "member", false),
      ],
      titles: [
        { id: "1", name: "Казначей" },
        { id: "2", name: "Новобранец" },
      ],
      info: "Соратники Йоррваскра.\nСбор по пятницам у Скайфорджа.",
      listed: true,
      joinRequests: [
        {
          id: "21",
          name: "Ингрид",
          createdAt: now - 3600000,
          expires: now + 6 * 86400000,
        },
        {
          id: "22",
          name: "Бьорн",
          createdAt: now - 600000,
          expires: now + 7 * 86400000,
        },
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
      titles: [],
      info: "",
      listed: true,
      joinRequests: [],
    },
  ];
  const directory = catalogGuilds(now);
  const applications: GuildApplication[] = [];
  let ended: ApplicationEnd[] = [];
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
  let nextTitle = 10;
  const titleTaken = (guild: Guild, name: string, except?: string) =>
    guild.titles.some(
      (t) => t.id !== except && t.name.toLowerCase() === name.toLowerCase(),
    );
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
      applications: [...applications],
      applicationsEnded: ended,
    });
    ended = [];
  }
  // The catalog as the server builds it: listed guilds, the player's own too.
  function listed(): GuildCatalogGuild[] {
    return [
      ...directory,
      ...guilds
        .filter((g) => g.listed)
        .map((g) => ({
          id: g.id,
          name: g.name,
          info: g.info,
          members: g.members.length,
          createdAt: g.createdAt,
        })),
    ];
  }
  function page(c: CatalogCommand) {
    const search = c.search.toLowerCase();
    const key = (g: { name: string; members: number; id: string }) =>
      [
        c.sort === "members" ? String(999999 - g.members).padStart(6, "0") : "",
        g.name.toLowerCase(),
        g.id.padStart(12, "0"),
      ].join("\u0000");
    const found = listed()
      .filter(
        (g) =>
          g.name.toLowerCase().includes(search) &&
          g.members >= c.minMembers &&
          (c.maxMembers === undefined || g.members <= c.maxMembers) &&
          (!c.openOnly || g.members < LIMITS.members),
      )
      .sort((a, b) => (key(a) < key(b) ? -1 : key(a) > key(b) ? 1 : 0));
    const after = c.after
      ? key({ ...c.after, id: c.after.guildId })
      : undefined;
    const rest = after ? found.filter((g) => key(g) > after) : found;
    return {
      guilds: rest.slice(0, CATALOG_PAGE),
      more: rest.length > CATALOG_PAGE,
      total: found.length,
    };
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
          titles: [],
          info: "",
          listed: true,
          joinRequests: [],
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
            titles: [],
            info: "",
            listed: true,
            joinRequests: [],
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
      case "createTitle":
        if (!guild) return "Гильдия не найдена";
        if (guild.titles.length >= LIMITS.titles)
          return "В гильдии уже предельное число ролей";
        if (titleTaken(guild, c.name))
          return "В гильдии уже есть роль с таким названием";
        guild.titles.push({ id: String(nextTitle++), name: c.name });
        changed(false);
        return undefined;
      case "renameTitle": {
        const title = guild?.titles.find((t) => t.id === c.titleId);
        if (!guild || !title) return "Такой роли в гильдии уже нет";
        if (titleTaken(guild, c.name, c.titleId))
          return "В гильдии уже есть роль с таким названием";
        title.name = c.name;
        changed(false);
        return undefined;
      }
      case "setInfo":
        if (!guild) return "Гильдия не найдена";
        if ([...c.info].length > LIMITS.infoMax)
          return `Не длиннее ${LIMITS.infoMax} символов`;
        guild.info = c.info;
        changed(false);
        return undefined;
      case "setListed":
        if (!guild) return "Гильдия не найдена";
        guild.listed = c.listed;
        changed(false);
        return undefined;
      case "apply": {
        const entry = listed().find((g) => g.id === c.guildId);
        if (!entry || guild) return "Гильдия не найдена";
        if (applications.some((a) => a.guildId === c.guildId))
          return "Заявка в эту гильдию уже отправлена";
        if (applications.length >= LIMITS.applications)
          return "У вас уже предельное число заявок";
        applications.push({
          guildId: entry.id,
          guildName: entry.name,
          expires: Date.now() + 7 * 86400000,
        });
        changed(false);
        return undefined;
      }
      case "withdraw": {
        const index = applications.findIndex((a) => a.guildId === c.guildId);
        if (index < 0) return "Игрока, приглашения или заявки уже нет";
        const [gone] = applications.splice(index, 1);
        ended.push({
          guildId: gone.guildId,
          name: gone.guildName,
          reason: "withdrawn",
        });
        changed(false);
        return undefined;
      }
      case "answerRequest": {
        const request = guild?.joinRequests.find((r) => r.id === c.playerId);
        if (!guild || !request) return "Игрока, приглашения или заявки уже нет";
        guild.joinRequests = guild.joinRequests.filter((r) => r !== request);
        if (c.accept)
          guild.members.push({
            id: request.id,
            name: request.name,
            role: "member",
            online: false,
            joinedAt: Date.now(),
          });
        changed(false);
        return undefined;
      }
      case "deleteTitle":
        if (!guild?.titles.some((t) => t.id === c.titleId))
          return "Такой роли в гильдии уже нет";
        guild.titles = guild.titles.filter((t) => t.id !== c.titleId);
        for (const m of guild.members)
          if (m.title === c.titleId) delete m.title;
        changed(false);
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
        else if (c.action === "setTitle") {
          if (c.titleId === undefined) delete target.title;
          else target.title = c.titleId;
        } else delete target.mute;
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
    // A catalog page after a network-like wait.
    catalog(c: CatalogCommand) {
      setTimeout(
        () =>
          receive({ type: "guildCatalog", requestId: c.requestId, ...page(c) }),
        400,
      );
      return true;
    },
    // Workshop buttons: an invitation arrives, an officer excludes the
    // player, the master mutes them, a guild answers the oldest request.
    answerApplication(accept: boolean) {
      const application = applications.shift();
      if (!application) return;
      ended.push({
        guildId: application.guildId,
        name: application.guildName,
        reason: accept ? "accepted" : "rejected",
      });
      if (accept)
        guilds.push({
          id: application.guildId,
          name: application.guildName,
          channelId: String(GUILD_BASE + Number(application.guildId)),
          createdAt: Date.now() - 100 * 86400000,
          members: [member(players[2], "master"), member(self, "member")],
          titles: [],
          info: "",
          listed: true,
          joinRequests: [],
        });
      changed(accept);
    },
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
    // The first guild's chat history: its newest line is the message of the day.
    history(): Message[] {
      const guild = guilds[0];
      return [
        [self, "Завтра идём в Ветреный пик, кто с нами?"],
        [
          players[2],
          "Сбор в пятницу у Скайфорджа, не опаздывайте!\nВозьмите зелья.",
        ],
      ].map(([author, text], i) => ({
        id: String(i + 1),
        channelId: guild.channelId,
        source: "player",
        author: author as Player,
        text: text as string,
        time: now - (2 - i) * 3600000,
      }));
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
