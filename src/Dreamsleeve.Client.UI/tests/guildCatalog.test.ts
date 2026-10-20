import { describe, expect, it, vi } from "vitest";
import { makeChat } from "../src/state/chat";
import type { Command, GuildCatalogGuild } from "../src/bridge/types";

const guild = (id: string, members = 3): GuildCatalogGuild => ({
  id,
  name: `Гильдия ${id}`,
  info: "",
  members,
  createdAt: 0,
});
// A connected session; the catalog asks only once the page opens it.
function connected() {
  const send = vi.fn((_command: Command) => true);
  const chat = makeChat(send);
  chat.receive({
    type: "snapshot",
    serverName: "Голоса Тамриэля",
    channels: [],
    messages: [],
    players: [],
    selfId: "1",
  });
  const catalogs = () =>
    send.mock.calls
      .map(([command]) => command)
      .filter((c) => c.type === "guildCatalog");
  return { chat, catalogs };
}

describe("the guild catalog", () => {
  it("asks for the first page once and the next one after the last entry", () => {
    const { chat, catalogs } = connected();
    chat.catalog.open();
    chat.catalog.open();
    expect(catalogs()).toEqual([
      {
        type: "guildCatalog",
        requestId: "gc1",
        search: "",
        minMembers: 0,
        openOnly: false,
        sort: "name",
      },
    ]);
    expect(chat.store.getState().catalog.loading).toBe("gc1");
    chat.receive({
      type: "guildCatalog",
      requestId: "gc1",
      guilds: [guild("4"), guild("5", 12)],
      more: true,
      total: 3,
    });
    const first = chat.store.getState().catalog;
    expect(first.guilds.map((g) => g.id)).toEqual(["4", "5"]);
    expect([first.loaded, first.more, first.total, first.loading]).toEqual([
      true,
      true,
      3,
      null,
    ]);
    chat.catalog.more();
    expect(catalogs().at(-1)).toMatchObject({
      requestId: "gc2",
      after: { guildId: "5", name: "Гильдия 5", members: 12 },
    });
    // Nothing more while a page is on its way.
    chat.catalog.more();
    expect(catalogs()).toHaveLength(2);
    chat.receive({
      type: "guildCatalog",
      requestId: "gc2",
      guilds: [guild("6")],
      more: false,
      total: 3,
    });
    expect(chat.store.getState().catalog.guilds.map((g) => g.id)).toEqual([
      "4",
      "5",
      "6",
    ]);
    chat.catalog.more();
    expect(catalogs()).toHaveLength(2);
  });

  it("starts over when the filters change and ignores the answer to the old ones", () => {
    const { chat, catalogs } = connected();
    chat.catalog.open();
    chat.catalog.filter({ search: " вор ", openOnly: true, maxMembers: 10 });
    expect(catalogs().at(-1)).toEqual({
      type: "guildCatalog",
      requestId: "gc2",
      search: "вор",
      minMembers: 0,
      maxMembers: 10,
      openOnly: true,
      sort: "name",
    });
    chat.receive({
      type: "guildCatalog",
      requestId: "gc1",
      guilds: [guild("4")],
      more: false,
      total: 1,
    });
    expect(chat.store.getState().catalog.loaded).toBe(false);
    chat.receive({
      type: "guildCatalog",
      requestId: "gc2",
      error: "Сервер занят. Попробуйте позже",
    });
    expect(chat.store.getState().catalog.error).toBe(
      "Сервер занят. Попробуйте позже",
    );
    chat.catalog.refresh();
    chat.receive({
      type: "guildCatalog",
      requestId: "gc3",
      guilds: [guild("7")],
      more: false,
      total: 1,
    });
    const catalog = chat.store.getState().catalog;
    expect([catalog.error, catalog.guilds.map((g) => g.id)]).toEqual([
      "",
      ["7"],
    ]);
    expect(catalog.filters.search).toBe(" вор ");
  });

  it("sends nothing without a connection", () => {
    const send = vi.fn((_command: Command) => true);
    const chat = makeChat(send);
    chat.catalog.open();
    expect(send).not.toHaveBeenCalled();
  });
});
