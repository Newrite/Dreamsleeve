import { resourceKey } from "./resources";
import type { Player, Settings } from "../bridge/types";
import { characterLine, playerName, realNames } from "../state/names";
import type { OnlineSort } from "../state/onlineSort";
import styles from "../styles/OnlineList.module.css";
// The columns in their order, each with what it orders by.
const columns: { sort: OnlineSort; label: string; title: string }[] = [
  { sort: "name", label: "Игрок / персонаж", title: "по имени" },
  { sort: "level", label: "Ур.", title: "по уровню" },
  { sort: "place", label: "Зона / локация", title: "по зоне и локации" },
  { sort: "activity", label: "Занятие", title: "по занятию" },
  { sort: "health", label: "Ресурсы", title: "по доле здоровья" },
];
const resources = [
  ["health", "HP"],
  ["magicka", "MP"],
  ["stamina", "SP"],
] as const;
function Resources({ player }: { player: Player }) {
  return (
    <div className={styles.resources}>
      {resources.map(([key, label]) => {
        const value = player.actorValues?.find(
          (v) => resourceKey(v.key) === key,
        )?.value;
        const resource = typeof value === "object" ? value : undefined;
        const text = resource
          ? `${resource.current.toLocaleString("ru", { maximumFractionDigits: 1 })} / ${resource.maximum.toLocaleString("ru", { maximumFractionDigits: 1 })}`
          : typeof value === "number"
            ? String(value)
            : "—";
        const percent =
          resource && resource.maximum > 0
            ? Math.max(
                0,
                Math.min(100, (resource.current / resource.maximum) * 100),
              )
            : 0;
        return (
          <span
            key={key}
            className={styles.resource}
            data-resource={key}
            title={`${label}: ${text}`}
          >
            <span>
              {label} <b>{text}</b>
            </span>
            <i>
              <em style={{ width: `${percent}%` }} />
            </i>
          </span>
        );
      })}
    </div>
  );
}
export function OnlineList({
  players,
  selfId,
  connected,
  settings,
  openProfile,
  onSort,
}: {
  players: Player[];
  selfId: string;
  connected: boolean;
  settings: Settings;
  openProfile: (id: string) => void;
  onSort: (sort: OnlineSort) => void;
}) {
  const sort = settings.onlineSort;
  const descending = settings.onlineSortDescending;
  return (
    <div className={styles.scroll} tabIndex={0} aria-label="Компактный онлайн">
      <table className={styles.table}>
        <colgroup>
          <col className={styles.nameColumn} />
          <col className={styles.levelColumn} />
          <col className={styles.placeColumn} />
          <col className={styles.activityColumn} />
          <col className={styles.resourcesColumn} />
        </colgroup>
        <thead>
          <tr>
            {columns.map((c) => {
              const current = c.sort === sort;
              return (
                <th
                  key={c.sort}
                  scope="col"
                  aria-sort={
                    current ? (descending ? "descending" : "ascending") : "none"
                  }
                >
                  <button
                    className={styles.sort}
                    data-current={current || undefined}
                    title={`Сортировать ${c.title}`}
                    onClick={() => onSort(c.sort)}
                  >
                    {c.label}
                    <span aria-hidden="true">
                      {current ? (descending ? " ▼" : " ▲") : ""}
                    </span>
                  </button>
                </th>
              );
            })}
          </tr>
        </thead>
        <tbody>
          {players.map((p) => {
            const real = realNames(p, settings);
            return (
              <tr key={p.id} data-self={p.id === selfId}>
                <td>
                  <button
                    className={styles.name}
                    data-pseudonymous={p.pseudonymous || undefined}
                    onClick={() => openProfile(p.id)}
                    title={
                      real
                        ? `${real.displayName} @${real.username} · ${characterLine(p, settings)}`
                        : p.pseudonymous
                          ? `${playerName(p, settings)} · имя скрыто игроком`
                          : playerName(p, settings)
                    }
                  >
                    <strong>
                      {playerName(p, settings)}
                      {p.id === selfId ? " · вы" : ""}
                    </strong>
                    <small>
                      {characterLine(p, settings)}
                      {p.race ? ` · ${p.race}` : ""}
                    </small>
                  </button>
                </td>
                <td className={styles.level}>{p.level ?? "—"}</td>
                <td>
                  <span className={styles.ellipsis} title={p.zone}>
                    {p.zone ?? "—"}
                  </span>
                  <small className={styles.ellipsis} title={p.location}>
                    {p.location ?? "Неизвестно"}
                  </small>
                </td>
                <td>
                  <span className={styles.ellipsis} title={p.activity}>
                    {connected ? (p.activity ?? "—") : "Нет соединения"}
                  </span>
                  <small className={styles.ellipsis} title={p.activityTarget}>
                    {connected ? (p.activityTarget ?? "") : "Последние данные"}
                  </small>
                </td>
                <td>
                  <Resources player={p} />
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
