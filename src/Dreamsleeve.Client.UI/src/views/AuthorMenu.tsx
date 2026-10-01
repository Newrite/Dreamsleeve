import { useEffect, useRef } from "react";
import type { Chat, ChatState } from "../state/chat";
import { HIDDEN_BY_PLAYER } from "../state/names";
import { playerActions } from "../state/moderator";
import styles from "../styles/Chat.module.css";
const WIDTH = 220;
// The header and the profile and ignore items; each further item adds a row.
const HEIGHT = 110;
const ROW = 32;
// Context menu of a chat author. A transparent layer closes it on any click
// outside; Escape closes it through useChat.
export function AuthorMenu({
  chat,
  state: s,
}: {
  chat: Chat;
  state: ChatState;
}) {
  const menu = useRef<HTMLDivElement>(null);
  const target = s.authorMenu!;
  useEffect(() => {
    menu.current?.querySelector<HTMLButtonElement>("button")?.focus();
  }, [target.playerId]);
  const own = target.playerId === s.selfId;
  const ignored = s.ignored.some((p) => p.id === target.playerId);
  const message = s.moderator ? target.message : undefined;
  const moderate =
    s.moderator && !own
      ? playerActions(chat.moderator, target.playerId, target.name)
      : [];
  const height = HEIGHT + ROW * (moderate.length + (message ? 1 : 0));
  const left = Math.max(0, Math.min(target.x, window.innerWidth - WIDTH));
  const top = Math.max(0, Math.min(target.y, window.innerHeight - height));
  function act(run: () => void) {
    chat.closeAuthorMenu();
    run();
  }
  return (
    <div
      className={styles.menuLayer}
      data-part="author-menu-layer"
      data-theme={s.settings.theme}
      onMouseDown={(e) => {
        if (e.target === e.currentTarget) chat.closeAuthorMenu();
      }}
      onContextMenu={(e) => {
        e.preventDefault();
        if (e.target === e.currentTarget) chat.closeAuthorMenu();
      }}
    >
      <div
        ref={menu}
        className={styles.authorMenu}
        role="menu"
        aria-label={`Действия: ${target.name}`}
        style={{ left, top }}
      >
        <strong>{target.name}</strong>
        {target.pseudonymous && (
          <span className={styles.menuNote}>{HIDDEN_BY_PLAYER}</span>
        )}
        <button
          role="menuitem"
          onClick={() => act(() => chat.open("profile", target.playerId))}
        >
          Открыть профиль
        </button>
        {!own &&
          (ignored ? (
            <button
              role="menuitem"
              onClick={() => act(() => chat.unignore(target.playerId))}
            >
              Не игнорировать
            </button>
          ) : (
            <button
              role="menuitem"
              onClick={() => act(() => chat.ignore(target.playerId))}
            >
              Игнорировать
            </button>
          ))}
        {message && (
          <button
            role="menuitem"
            className={styles.menuModerator}
            onClick={() =>
              act(() =>
                chat.moderator.deleteMessage(message.channelId, message.id),
              )
            }
          >
            Удалить сообщение
          </button>
        )}
        {moderate.map((item) => (
          <button
            key={item.label}
            role="menuitem"
            className={styles.menuModerator}
            onClick={() => act(item.run)}
          >
            {item.label}
          </button>
        ))}
      </div>
    </div>
  );
}
