import { useEffect } from "react";
import {
  PENDING_TIMEOUT,
  shows,
  type Chat,
  type ChatState,
} from "../state/chat";
import styles from "../styles/Chat.module.css";

export function PendingMessages({
  chat,
  state: s,
}: {
  chat: Chat;
  state: ChatState;
}) {
  // One timer for the nearest change: a request giving up, or a settled row
  // leaving the passive HUD. While the chat is active settled rows stay.
  useEffect(() => {
    const deadlines = Object.values(s.pending)
      .filter((p) => p.status === "sending" || !s.active)
      .map(
        (p) =>
          (p.status === "sending" ? p.time : (p.since ?? p.time)) +
          PENDING_TIMEOUT,
      );
    if (!deadlines.length) return;
    const timer = setTimeout(
      chat.expirePending,
      Math.max(0, Math.min(...deadlines) - Date.now()),
    );
    return () => clearTimeout(timer);
  }, [chat, s.pending, s.active]);

  return Object.entries(s.pending)
    .filter(([, p]) => shows(p.channelId, s.filter, s.settings, s.channels))
    .map(([id, p]) => (
      <div
        key={id}
        className={styles.pendingMessage}
        data-part="pending-message"
        data-status={p.status}
        data-external={p.external !== undefined || undefined}
      >
        <span className={styles.pendingText}>
          [{s.channels.find((c) => c.id === p.channelId)?.name ?? "Канал"}]{" "}
          {p.external === undefined ? "Вы" : p.external || "Мод"}: {p.text}
        </span>
        <small role="status">
          {p.status === "sending"
            ? " · Отправляется…"
            : p.status === "failed"
              ? ` · Не отправлено: ${p.error}`
              : " · Доставка неизвестна"}
        </small>
        {s.active && p.status === "failed" && p.external === undefined && (
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
