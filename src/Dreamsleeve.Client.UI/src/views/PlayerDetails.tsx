import { resourceKey } from "./resources";
import type { ActorValue, Player, Settings } from "../bridge/types";
import { characterLine, playerName, realNames } from "../state/names";
import styles from "../styles/Workspace.module.css";
const number = (n: number) =>
  n.toLocaleString("ru", { maximumFractionDigits: 1 });
export function ActorValueView({ entry }: { entry: ActorValue }) {
  if (typeof entry.value === "number")
    return (
      <div className={styles.scalar}>
        <span>{entry.name || entry.key}</span>
        <strong>{number(entry.value)}</strong>
      </div>
    );
  const { current, maximum } = entry.value;
  const fraction =
    maximum > 0 ? Math.max(0, Math.min(1, current / maximum)) : 0;
  return (
    <div className={styles.resource} data-resource={resourceKey(entry.key)}>
      <div>
        <span>{entry.name || entry.key}</span>
        <strong>
          {number(current)} / {number(maximum)}
        </strong>
      </div>
      <div className={styles.track}>
        <i style={{ width: `${fraction * 100}%` }} />
      </div>
    </div>
  );
}
export function PlayerDetails({
  player: p,
  settings,
}: {
  player: Player;
  settings: Settings;
}) {
  const real = realNames(p, settings);
  return (
    <>
      <div className={styles.identity}>
        <span
          className={styles.level}
          aria-label={`Уровень ${p.level ?? "неизвестен"}`}
        >
          {p.level ?? "—"}
        </span>
        <div>
          <h3 data-pseudonymous={p.pseudonymous || undefined}>
            {playerName(p, settings)}
          </h3>
          <span>
            {real ? `${real.displayName} · @${real.username} · ` : ""}
            {characterLine(p, settings)}
            {p.race ? ` · ${p.race}` : ""}
          </span>
        </div>
      </div>
      <div className={styles.metadata}>
        <div>
          <small>ЗОНА</small>
          <strong>{p.zone ?? "Неизвестно"}</strong>
        </div>
        <div>
          <small>ЛОКАЦИЯ</small>
          <strong>{p.location ?? "Неизвестно"}</strong>
          {p.interior !== undefined && (
            <span>{p.interior ? "В помещении" : "Под открытым небом"}</span>
          )}
        </div>
        <div>
          <small>ЗАНЯТИЕ</small>
          <strong>{p.activity ?? "Неизвестно"}</strong>
          {p.activityTarget && <span>{p.activityTarget}</span>}
        </div>
        {p.nearbyMarker && (
          <div>
            <small>РЯДОМ</small>
            <strong>{p.nearbyMarker}</strong>
            {p.markerKind && <span>{p.markerKind}</span>}
          </div>
        )}
        {p.lockDifficulty && (
          <div>
            <small>СЛОЖНОСТЬ ЗАМКА</small>
            <strong>{p.lockDifficulty}</strong>
          </div>
        )}
        {p.menu && (
          <div>
            <small>МЕНЮ</small>
            <strong>{p.menu}</strong>
          </div>
        )}
        {p.gameStartedAt !== undefined && (
          <div>
            <small>НАЧАЛО ИГРЫ</small>
            <strong>{new Date(p.gameStartedAt).toLocaleString("ru")}</strong>
          </div>
        )}
      </div>
      <div className={styles.values}>
        {p.actorValues?.length ? (
          p.actorValues.map((entry) => (
            <ActorValueView key={entry.key} entry={entry} />
          ))
        ) : (
          <p className={styles.muted}>Показатели персонажа пока не получены.</p>
        )}
      </div>
    </>
  );
}
