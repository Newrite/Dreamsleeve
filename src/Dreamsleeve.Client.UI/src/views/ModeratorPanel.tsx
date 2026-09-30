import { useEffect } from "react";
import type { Chat, ChatState } from "../state/chat";
import { term } from "../state/moderation";
import { sanctionLabels, sanctionName } from "../state/moderator";
import { MarkRow } from "./MarksPanel";
import account from "../styles/Account.module.css";
import styles from "../styles/Moderation.module.css";
const issuedFormat = new Intl.DateTimeFormat("ru-RU", {
  day: "2-digit",
  month: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
});
// Moderators only: sanctions in force, offline players included, and the
// marks of the player last asked for.
export function ModeratorPanel({
  chat,
  state: s,
}: {
  chat: Chat;
  state: ChatState;
}) {
  const moderator = chat.moderator;
  const listed = s.sanctions !== null;
  useEffect(() => {
    if (!listed) moderator.listSanctions();
  }, [moderator, listed, s.connected]);
  const marks = s.playerMarks;
  return (
    <div className={account.account}>
      <h3>Наказания в силе</h3>
      <div className={account.actions}>
        <button
          disabled={!s.connected}
          onClick={() => moderator.listSanctions()}
        >
          Обновить
        </button>
      </div>
      {!listed ? (
        <p className={account.muted}>Загрузка…</p>
      ) : !s.sanctions!.length ? (
        <p className={account.muted}>Никто не наказан.</p>
      ) : (
        <ul className={styles.list} aria-label="Наказания в силе">
          {s.sanctions!.map((x) => (
            <li key={`${x.kind}:${x.playerId}`} data-kind={x.kind}>
              <span className={styles.kind}>{sanctionLabels[x.kind]}</span>
              <b>{sanctionName(x)}</b>
              <span>{term(x.until)}</span>
              <span className={styles.reason}>{x.reason}</span>
              <small>выдан {issuedFormat.format(x.issuedAt)}</small>
              <span className={styles.rowActions}>
                <button
                  disabled={!s.connected}
                  onClick={() => moderator.lift(x)}
                >
                  Снять
                </button>
              </span>
            </li>
          ))}
        </ul>
      )}
      <p className={account.muted}>
        Снять наказание может любой модератор. Модератор не может наказать себя
        или другого модератора. Игрок, которого клиент не встречал, показан
        номером.
      </p>
      {marks && (
        <>
          <h3>Метки игрока {marks.name}</h3>
          <div className={account.actions}>
            <button
              disabled={!s.connected}
              onClick={() => moderator.listMarks(marks.playerId, marks.name)}
            >
              Обновить
            </button>
            <button
              disabled={!s.connected}
              onClick={() =>
                moderator.openDialog("clear", marks.playerId, marks.name, {
                  notes: true,
                  deaths: true,
                })
              }
            >
              Удалить метки…
            </button>
          </div>
          {marks.marks.length === 0 ? (
            <p className={account.muted}>У игрока нет меток.</p>
          ) : (
            <ul className={account.marks} aria-label="Метки игрока">
              {marks.marks.map((m) => (
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
          <p className={account.muted}>
            Все метки игрока на сервере, где бы они ни стояли, новые сверху.
            Удаление попадает в журнал аудита.
          </p>
        </>
      )}
    </div>
  );
}
