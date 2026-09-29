import type { Chat, ChatState } from "../state/chat";
import styles from "../styles/Account.module.css";
const kindLabels = { note: "Надпись", death: "Место смерти" } as const;
const timeFormat = new Intl.DateTimeFormat("ru-RU", {
  day: "2-digit",
  month: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
});
// The player's own ground marks the host knows in this session: placed here
// or met nearby. Removal is the only action; the outcome arrives as a notice.
export function MarksPanel({
  chat,
  state: s,
}: {
  chat: Chat;
  state: ChatState;
}) {
  const marks = [...s.groundMarks].sort((a, b) => b.time - a.time);
  return (
    <div className={styles.account}>
      <h3>Мои метки</h3>
      {!s.groundMarksSupported ? (
        <p className={styles.muted}>
          Метки недоступны: нет соединения или сервер их не поддерживает.
        </p>
      ) : marks.length === 0 ? (
        <p className={styles.muted}>
          Пока ни одной метки. Напишите текст в чате и нажмите «Оставить здесь».
        </p>
      ) : (
        <ul className={styles.marks} aria-label="Мои метки">
          {marks.map((m) => (
            <li key={m.id} data-kind={m.kind}>
              <span className={styles.markKind}>{kindLabels[m.kind]}</span>
              <span className={styles.markText}>
                {m.text || (m.kind === "death" ? "Без подписи" : "")}
              </span>
              <time dateTime={new Date(m.time).toISOString()}>
                {timeFormat.format(m.time)}
              </time>
              <button
                aria-label={`Удалить метку ${m.id}`}
                disabled={!s.connected}
                onClick={() => chat.removeMark(m.id)}
              >
                Удалить
              </button>
            </li>
          ))}
        </ul>
      )}
      <p className={styles.muted}>
        Список содержит метки, оставленные в этой сессии или встреченные рядом.
        Сервер хранит не больше нескольких меток каждого вида на игрока: новая
        вытесняет самую старую, у меток есть срок жизни.
      </p>
    </div>
  );
}
