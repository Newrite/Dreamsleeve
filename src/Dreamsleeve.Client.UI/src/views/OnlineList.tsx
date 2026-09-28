import { resourceKey } from "./resources";
import type { Player, Settings } from "../bridge/types";
import { characterLine, playerName, realNames } from "../state/names";
import styles from "../styles/OnlineList.module.css";
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
}: {
  players: Player[];
  selfId: string;
  connected: boolean;
  settings: Settings;
  openProfile: (id: string) => void;
}) {
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
            <th scope="col">Игрок / персонаж</th>
            <th scope="col">Ур.</th>
            <th scope="col">Зона / локация</th>
            <th scope="col">Занятие</th>
            <th scope="col">Ресурсы</th>
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
                    onClick={() => openProfile(p.id)}
                    title={
                      real
                        ? `${real.displayName} @${real.username} · ${characterLine(p, settings)}`
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
