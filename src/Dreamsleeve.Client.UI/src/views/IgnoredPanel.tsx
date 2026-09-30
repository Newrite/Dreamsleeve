import type { Chat, ChatState } from "../state/chat";
import account from "../styles/Account.module.css";
import styles from "../styles/Moderation.module.css";
// The personal ignore list of this server: who is hidden and a way back.
export function IgnoredPanel({
  chat,
  state: s,
}: {
  chat: Chat;
  state: ChatState;
}) {
  const online = new Set(s.players.map((p) => p.id));
  return (
    <div className={account.account}>
      <h3>Игнорируемые игроки</h3>
      {s.ignored.length ? (
        <ul className={styles.list} aria-label="Игнорируемые игроки">
          {s.ignored.map((p) => (
            <li key={p.id}>
              <b>{p.name}</b>
              <small>{online.has(p.id) ? "в сети" : "не в сети"}</small>
              <span className={styles.rowActions}>
                <button onClick={() => chat.open("profile", p.id)}>
                  Профиль
                </button>
                <button onClick={() => chat.unignore(p.id)}>
                  Не игнорировать
                </button>
              </span>
            </li>
          ))}
        </ul>
      ) : (
        <p className={account.muted}>Список пуст.</p>
      )}
      <p className={account.muted}>
        Сообщения этих игроков скрыты только у вас: в чате, над светлячками и в
        метках. Сам светлячок и онлайн остаются. Добавить игрока: правый клик по
        нику в чате или «Игнорировать» в профиле. Список хранится локально для
        этого сервера.
      </p>
    </div>
  );
}
