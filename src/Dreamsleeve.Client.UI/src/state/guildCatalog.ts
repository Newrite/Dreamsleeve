import type { StoreApi } from "zustand/vanilla";
import type {
  GuildCatalogAfter,
  GuildCatalogGuild,
  GuildCatalogSort,
  HostEvent,
  Send,
} from "../bridge/types";
import type { ChatState } from "./chat";

// What the player looks for in the public catalog; no maxMembers: no upper bound.
export interface GuildCatalogFilters {
  search: string;
  minMembers: number;
  maxMembers?: number;
  openOnly: boolean;
  sort: GuildCatalogSort;
}
// The pages loaded so far for the current filters. The server decides the
// page size; the list asks for the next page while its end is in sight.
export interface GuildCatalog {
  filters: GuildCatalogFilters;
  guilds: GuildCatalogGuild[];
  total: number;
  more: boolean;
  // The request in flight: an answer to any other one is stale.
  loading: string | null;
  error: string;
  // The first page for these filters arrived.
  loaded: boolean;
}
export interface GuildCatalogState {
  catalog: GuildCatalog;
}
export const idleCatalog: GuildCatalog = {
  filters: { search: "", minMembers: 0, openOnly: false, sort: "name" },
  guilds: [],
  total: 0,
  more: false,
  loading: null,
  error: "",
  loaded: false,
};
type CatalogEvent = Extract<HostEvent, { type: "guildCatalog" }>;

// Pages of the catalog with their own request IDs ("gc1", ...): a page
// answers only the request that is still current, so changed filters or a
// refresh never mix two lists.
export function makeGuildCatalog(store: StoreApi<ChatState>, send: Send) {
  let sequence = 0;
  // The request that asks for the first page of the current filters.
  let first: string | null = null;
  const update = (patch: Partial<GuildCatalog>) =>
    store.setState({ catalog: { ...store.getState().catalog, ...patch } });
  function load(after?: GuildCatalogAfter) {
    const s = store.getState();
    if (!s.connected) return;
    const filters = s.catalog.filters;
    const requestId = `gc${++sequence}`;
    if (!after) first = requestId;
    update({ loading: requestId, error: "" });
    const sent = send({
      type: "guildCatalog",
      requestId,
      search: filters.search.trim(),
      minMembers: filters.minMembers,
      ...(filters.maxMembers === undefined
        ? {}
        : { maxMembers: filters.maxMembers }),
      openOnly: filters.openOnly,
      sort: filters.sort,
      ...(after ? { after } : {}),
    });
    if (!sent)
      update({ loading: null, error: "Команда не принята приложением" });
  }
  // A new list: what was loaded belongs to other filters.
  function restart(filters: GuildCatalogFilters) {
    update({ ...idleCatalog, filters });
    load();
  }
  return {
    receive(event: CatalogEvent) {
      const catalog = store.getState().catalog;
      if (event.requestId !== catalog.loading) return;
      if (event.error !== undefined) {
        update({ loading: null, error: event.error });
        return;
      }
      const page = event.guilds ?? [];
      update({
        guilds: event.requestId === first ? page : [...catalog.guilds, ...page],
        total: event.total ?? 0,
        more: event.more ?? false,
        loading: null,
        loaded: true,
      });
    },
    // The first page, once per filters: opening the tab again keeps the list.
    open() {
      const catalog = store.getState().catalog;
      if (!catalog.loaded && !catalog.loading) load();
    },
    filter(patch: Partial<GuildCatalogFilters>) {
      restart({ ...store.getState().catalog.filters, ...patch });
    },
    // The next page after the last entry, when there is one and nothing waits.
    more() {
      const catalog = store.getState().catalog;
      const last = catalog.guilds.at(-1);
      if (catalog.loading || !catalog.more || !last) return;
      load({ guildId: last.id, name: last.name, members: last.members });
    },
    refresh() {
      restart(store.getState().catalog.filters);
    },
    // A new session starts the catalog over; the filters stay.
    reset() {
      first = null;
      update({ ...idleCatalog, filters: store.getState().catalog.filters });
    },
  };
}
export type Catalog = ReturnType<typeof makeGuildCatalog>;
