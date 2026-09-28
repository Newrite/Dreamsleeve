import { useEffect } from "react";
import type { Chat, ChatState } from "../state/chat";
import styles from "../styles/Chat.module.css";

export function PendingMessages({
  chat,
  state: s,
}: {
  chat: Chat;
  state: ChatState;
}) {
  useEffect(() => {
    const deadlines = Object.values(s.pending)
      .filter((p) => p.status === "sending")
      .map((p) => p.time + 15000);
    if (!deadlines.length) return;
    const timer = setTimeout(
      chat.expirePending,
      Math.max(0, Math.min(...deadlines) - Date.now()),
    );
    return () => clearTimeout(timer);
  }, [chat, s.pending]);

  return Object.entries(s.pending)
    .filter(([, p]) => s.filter === "all" || p.channelId === s.filter)
    .map(([id, p]) => (
      <div
        key={id}
        className={styles.pendingMessage}
        data-part="pending-message"
        data-status={p.status}
      >
        <span className={styles.pendingText}>
          [{s.channels.find((c) => c.id === p.channelId)?.name ?? "Канал"}] Вы:{" "}
          {p.text}
        </span>
        <small role="status">
          {p.status === "sending"
            ? " · Отправляется…"
            : p.status === "failed"
              ? ` · Не отправлено: ${p.error}`
              : " · Доставка неизвестна"}
        </small>
        {s.active && p.status === "failed" && (
          <button disabled={!s.connected} onClick={() => chat.retry(id)}>
            Повторить
          </button>
        )}
        {s.active && p.status !== "sending" && (
          <button
            onClick={() => chat.dismiss(id)}
            aria-label="Убрать статус сообщения"
          >
            ×
          </button>
        )}
      </div>
    ));
}
