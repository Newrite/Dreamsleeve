import type { GroundMark } from "../bridge/types";
import type { Chat, ChatState } from "../state/chat";
import styles from "../styles/Account.module.css";
const kindLabels = { note: "Надпись", death: "Место смерти" } as const;
const timeFormat = new Intl.DateTimeFormat("ru-RU", {
  day: "2-digit",
  month: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
});
const coordinate = (value: number) => Math.round(value).toString();
// One row per mark: kind, text, author (nearby only), character snapshot,
// the in-game date, the real time and the place. Text is a React text node,
// never HTML.
export function MarkRow({
  mark,
  action,
}: {
  mark: GroundMark;
  action?: { label: string; disabled: boolean; onClick: () => void };
}) {
  return (
    <li data-kind={mark.kind} data-mark={mark.id}>
      <span className={styles.markKind}>{kindLabels[mark.kind]}</span>
      <span className={styles.markText}>
        {mark.text || (mark.kind === "death" ? "Без подписи" : "")}
      </span>
      <span className={styles.markMeta}>
        {mark.author !== undefined && <b>{mark.author}</b>}
        {mark.character !== undefined && <i>{mark.character}</i>}
        {mark.gameDate !== undefined && (
          <span title="Игровая дата у автора">{mark.gameDate}</span>
        )}
        <time
          title="Реальное время"
          dateTime={new Date(mark.time).toISOString()}
        >
          {timeFormat.format(mark.time)}
        </time>
        <small title="Пространство (WRLD/CELL) и координаты в игровых единицах">
          {mark.location} · {coordinate(mark.x)}, {coordinate(mark.y)},{" "}
          {coordinate(mark.z)}
        </small>
      </span>
      {action && (
        <button
          aria-label={`${action.label} ${mark.id}`}
          disabled={action.disabled}
          onClick={action.onClick}
        >
          {action.label}
        </button>
      )}
    </li>
  );
}
// Own marks: the server's complete list, wherever they stand. Nearby: what
// the server shows here right now, with details visible without walking up.
export function MarksPanel({
  chat,
  state: s,
}: {
  chat: Chat;
  state: ChatState;
}) {
  const own = [...s.groundMarks].sort((a, b) => b.time - a.time);
  const nearby = [...s.nearbyMarks].sort((a, b) => b.time - a.time);
  return (
    <div className={styles.account}>
      <h3>Мои метки</h3>
      {!s.groundMarksSupported ? (
        <p className={styles.muted}>
          Метки недоступны: нет соединения или сервер их не поддерживает.
        </p>
      ) : own.length === 0 ? (
        <p className={styles.muted}>
          Пока ни одной метки. Напишите текст в чате и нажмите «Оставить здесь».
        </p>
      ) : (
        <ul className={styles.marks} aria-label="Мои метки">
          {own.map((m) => (
            <MarkRow
              key={m.id}
              mark={m}
              action={{
                label: "Удалить метку",
                disabled: !s.connected,
                onClick: () => chat.removeMark(m.id),
              }}
            />
          ))}
        </ul>
      )}
      <p className={styles.muted}>
        Полный список ваших меток на сервере, где бы они ни стояли. Сервер
        хранит не больше нескольких меток каждого вида на игрока: новая
        вытесняет самую старую, у меток есть срок жизни.
      </p>
      <h3>Метки рядом</h3>
      {nearby.length === 0 ? (
        <p className={styles.muted}>
          Рядом с вами сейчас нет меток. Список обновляется, когда сервер
          показывает или убирает метки вокруг персонажа.
        </p>
      ) : (
        <ul className={styles.marks} aria-label="Метки рядом">
          {nearby.map((m) => (
            <MarkRow key={m.id} mark={m} />
          ))}
        </ul>
      )}
      <p className={styles.muted}>
        Метки, которые сервер показывает в этом пространстве в радиусе
        видимости: автор, персонаж, текст и место видны здесь, не подходя к ним.
        Метки игнорируемых игроков не показываются.
      </p>
    </div>
  );
}
