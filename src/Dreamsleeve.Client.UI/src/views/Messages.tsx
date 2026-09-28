import { PendingMessages } from "./PendingMessages";
import { useMessageFade } from "../features/useMessageFade";
import { useEffect, useRef } from "react";
import { visible, type Chat, type ChatState } from "../state/chat";
import styles from "../styles/Chat.module.css";
export function Messages({ chat, state: s }: { chat: Chat; state: ChatState }) {
  const list = useRef<HTMLDivElement>(null);
  const faded = useMessageFade(s);
  const messages = s.messages.filter((m) => visible(m, s.filter));
  const last = messages[messages.length - 1]?.id;
  useEffect(() => {
    if (!s.scrolled && list.current) {
      list.current.scrollTop = list.current.scrollHeight;
      if (s.active && Object.values(s.unread).some(Boolean)) chat.read();
    }
  }, [chat, last, s.filter, s.active, s.scrolled, s.pending]);
  function scroll() {
    const el = list.current!;
    const scrolled = el.scrollHeight - el.scrollTop - el.clientHeight > 12;
    if (scrolled !== s.scrolled) {
      if (scrolled) chat.store.setState({ scrolled: true });
      else chat.read();
    }
  }
  const unread = Object.entries(s.unread).reduce(
    (n, [id, count]) => n + (s.filter === "all" || s.filter === id ? count : 0),
    0,
  );
  return (
    <div className={styles.history}>
      <div
        ref={list}
        className={styles.messages}
        data-part="messages"
        onScroll={scroll}
        aria-label="История сообщений"
      >
        {!s.initialized && (
          <p className={styles.empty}>Ожидание подключения…</p>
        )}
        {s.initialized &&
          !messages.length &&
          !Object.keys(s.pending).length && (
            <p className={styles.empty}>Здесь пока тихо.</p>
          )}
        {messages.map((m) => {
          const channel = s.channels.find((c) => c.id === m.channelId);
          return (
            <div
              key={m.id}
              className={styles.message}
              data-part="message"
              data-faded={faded(m.id)}
              style={{
                opacity: faded(m.id) ? s.settings.idleOpacity : 1,
                transitionDuration: s.active ? "0s" : `${s.settings.duration}s`,
              }}
              data-channel={channel?.kind}
              data-source={m.source}
            >
              {s.settings.timestamps && (
                <time>
                  {new Date(m.time).toLocaleTimeString("ru", {
                    hour: "2-digit",
                    minute: "2-digit",
                  })}{" "}
                </time>
              )}
              <span className={styles.channel}>
                [{channel?.name ?? "Канал"}]{" "}
              </span>
              {m.source === "player" && (
                <button
                  className={styles.author}
                  disabled={!s.active}
                  onClick={() => chat.open("profile", m.author.id)}
                >
                  {m.author.displayName}
                  {s.settings.nameMode === "account" && `@${m.author.username}`}
                  :
                </button>
              )}
              <span className={styles.text}> {m.text}</span>
            </div>
          );
        })}
        <PendingMessages chat={chat} state={s} />
      </div>
      {s.scrolled && unread > 0 && (
        <button className={styles.newMessages} onClick={chat.read}>
          Новые сообщения · {unread} ↓
        </button>
      )}
    </div>
  );
}
