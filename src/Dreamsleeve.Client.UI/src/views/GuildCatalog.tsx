import { useEffect, useRef, useState } from "react";
import type { Chat, ChatState } from "../state/chat";
import type { GuildCatalogGuild, GuildCatalogSort } from "../bridge/types";
import { Select } from "./Select";
import styles from "../styles/Guilds.module.css";
// The next page is asked for while the end of the list is this close to sight.
const NEAR_END_PX = 120;
// Typing settles before the search goes out.
const SEARCH_DELAY_MS = 300;
const sorts: { value: GuildCatalogSort; label: string }[] = [
  { value: "name", label: "По названию" },
  { value: "members", label: "По числу участников" },
];
// A bound the player typed: empty is none, anything else a whole count.
const bound = (text: string) =>
  text.trim() === "" ? undefined : Math.max(0, Math.floor(Number(text)) || 0);
// The box the list scrolls in: the panel around it, or the page.
function scroller(node: HTMLElement) {
  for (let box = node.parentElement; box; box = box.parentElement)
    if (/(auto|scroll)/.test(getComputedStyle(box).overflowY)) return box;
  return null;
}
// What the player can do about one catalog guild, from their own book.
function Entry({
  chat,
  state: s,
  guild,
  busy,
}: {
  chat: Chat;
  state: ChatState;
  guild: GuildCatalogGuild;
  busy: boolean;
}) {
  const limits = s.guildLimits;
  const applied = s.applications.some((a) => a.guildId === guild.id);
  const member = s.guilds.some((g) => g.id === guild.id);
  const invited = s.invites.some((i) => i.guildId === guild.id);
  const full = limits.members > 0 && guild.members >= limits.members;
  const atLimit = limits.perPlayer > 0 && s.guilds.length >= limits.perPlayer;
  const requestsFull =
    limits.applications > 0 && s.applications.length >= limits.applications;
  let action;
  if (member) action = <small className={styles.muted}>Вы в гильдии</small>;
  else if (applied)
    action = (
      <>
        <small className={styles.muted}>Заявка отправлена</small>
        <button
          disabled={busy}
          onClick={() => chat.guilds.withdraw(guild.id, guild.name)}
        >
          Отозвать
        </button>
      </>
    );
  else if (invited)
    action = (
      <small className={styles.muted}>Вас пригласили — ответьте выше</small>
    );
  else if (full) action = <small className={styles.muted}>Мест нет</small>;
  else
    action = (
      <button
        disabled={busy || atLimit || requestsFull}
        title={
          atLimit
            ? "Достигнут предел гильдий"
            : requestsFull
              ? "Достигнут предел заявок"
              : undefined
        }
        onClick={() => chat.guilds.apply(guild.id, guild.name)}
      >
        Подать заявку
      </button>
    );
  return (
    <li className={styles.catalogEntry}>
      <div className={styles.catalogHead}>
        <b>{guild.name}</b>
        <small className={styles.muted}>
          {guild.members} / {limits.members}
        </small>
        <span className={styles.rowActions}>{action}</span>
      </div>
      {guild.info && <p className={styles.about}>{guild.info}</p>}
    </li>
  );
}
// The public catalog: filters, then the pages the server sends, the next one
// asked for while the end of the list comes into sight in the panel's own
// scroll.
export function GuildCatalog({
  chat,
  state: s,
}: {
  chat: Chat;
  state: ChatState;
}) {
  const catalog = s.catalog;
  const filters = catalog.filters;
  const [search, setSearch] = useState(filters.search);
  const [minimum, setMinimum] = useState(
    filters.minMembers ? String(filters.minMembers) : "",
  );
  const [maximum, setMaximum] = useState(
    filters.maxMembers === undefined ? "" : String(filters.maxMembers),
  );
  const end = useRef<HTMLDivElement>(null);
  const busy = !s.connected || Object.keys(s.guildRequests).length > 0;
  useEffect(() => chat.catalog.open(), [chat, s.connected]);
  // Search and the member bounds go out once the player stops typing.
  useEffect(() => {
    const minMembers = bound(minimum) ?? 0;
    const maxMembers = bound(maximum);
    if (
      search === filters.search &&
      minMembers === filters.minMembers &&
      maxMembers === filters.maxMembers
    )
      return;
    const timer = setTimeout(
      () => chat.catalog.filter({ search, minMembers, maxMembers }),
      SEARCH_DELAY_MS,
    );
    return () => clearTimeout(timer);
  }, [chat, search, minimum, maximum, filters]);
  // The next page while the end is near; observed anew after every page, so a
  // page that does not fill the panel asks for one more.
  useEffect(() => {
    const node = end.current;
    if (!node) return;
    const observer = new IntersectionObserver(
      (seen) => {
        if (seen.some((e) => e.isIntersecting)) chat.catalog.more();
      },
      { root: scroller(node), rootMargin: `0px 0px ${NEAR_END_PX}px 0px` },
    );
    observer.observe(node);
    return () => observer.disconnect();
  }, [chat, catalog.guilds.length, catalog.loading]);
  return (
    <section className={styles.section} aria-label="Каталог гильдий">
      <div className={styles.catalogFilters}>
        <input
          aria-label="Поиск гильдии"
          placeholder="Название…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        <label className={styles.bound}>
          Участников от
          <input
            aria-label="Участников от"
            inputMode="numeric"
            value={minimum}
            onChange={(e) => setMinimum(e.target.value.replace(/\D/g, ""))}
          />
          до
          <input
            aria-label="Участников до"
            inputMode="numeric"
            value={maximum}
            onChange={(e) => setMaximum(e.target.value.replace(/\D/g, ""))}
          />
        </label>
        <label className={styles.check}>
          <input
            type="checkbox"
            checked={filters.openOnly}
            onChange={(e) =>
              chat.catalog.filter({ openOnly: e.target.checked })
            }
          />
          Только со свободными местами
        </label>
        <Select
          label="Сортировка каталога"
          value={filters.sort}
          options={sorts}
          onChange={(sort) => chat.catalog.filter({ sort })}
        />
        <button
          disabled={!s.connected || catalog.loading !== null}
          onClick={() => chat.catalog.refresh()}
        >
          Обновить
        </button>
      </div>
      <p className={styles.muted}>
        {catalog.loaded ? `Найдено: ${catalog.total}. ` : ""}
        Глава и офицеры гильдии увидят ваше настоящее имя в заявке.
      </p>
      <div aria-busy={catalog.loading !== null}>
        {catalog.loaded && catalog.guilds.length === 0 && (
          <p className={styles.muted}>Гильдий по этим условиям нет.</p>
        )}
        <ul className={styles.members} aria-label="Гильдии каталога">
          {catalog.guilds.map((g) => (
            <Entry key={g.id} chat={chat} state={s} guild={g} busy={busy} />
          ))}
        </ul>
        {catalog.loading !== null && (
          <p className={styles.muted} role="status">
            Загрузка…
          </p>
        )}
        {catalog.error && (
          <p className={styles.error}>
            Не удалось: {catalog.error}{" "}
            <button onClick={() => chat.catalog.refresh()}>Повторить</button>
          </p>
        )}
        <div ref={end} aria-hidden="true" />
      </div>
    </section>
  );
}
