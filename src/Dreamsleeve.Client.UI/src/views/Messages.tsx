import { PendingMessages } from "./PendingMessages";
import { useMessageFade } from "../features/useMessageFade";
import { memo, useEffect, useRef } from "react";
import {
  announcementOf,
  keyOf,
  shows,
  visible,
  type Chat,
  type ChatState,
} from "../state/chat";
import type { Announcement, Message } from "../bridge/types";
import { playerName } from "../state/names";
import styles from "../styles/Chat.module.css";
const kindLabels: Record<Announcement["kind"], string> = {
  announcement: "Объявление",
  event: "Событие",
  admin: "Администрация",
  periodic: "Напоминание",
};
// Who stands behind an announcement. A mod signature is its own claim, shown
// as received next to the player whose client posted it.
function originLabel(a: Announcement, author: string) {
  if (a.origin === "server") return "Сервер";
  const source =
    a.origin === "trustedClient" ? "Dreamsleeve" : a.signature || "Мод";
  return author ? `${source} · ${author}` : source;
}
// One row per message, memoised on plain props: the window re-renders on
// every store change (a keystroke, a presence update, a drag), and rebuilding
// hundreds of rows with locale time formatting each time was the bulk of the
// CPU cost on Ultralight. A row re-renders only when something it shows moved.
const MessageRow = memo(function MessageRow({
  chat,
  time,
  text,
  source,
  authorId,
  name,
  channelName,
  channelKind,
  filtered,
  origin,
  kind,
  label,
  faded,
  idleOpacity,
  duration,
  active,
  timestamps,
}: {
  chat: Chat;
  time: number;
  text: string;
  source: Message["source"];
  authorId: string;
  name: string;
  channelName: string;
  channelKind: string | undefined;
  filtered: boolean;
  origin: Announcement["origin"] | undefined; // absent for a player line
  kind: Announcement["kind"] | undefined;
  label: string;
  faded: boolean;
  idleOpacity: number;
  duration: number;
  active: boolean;
  timestamps: boolean;
}) {
  return (
    <div
      className={styles.message}
      data-part="message"
      data-faded={faded}
      style={{
        opacity: faded ? idleOpacity : 1,
        transitionDuration: active ? "0s" : `${duration}s`,
      }}
      data-channel={channelKind}
      data-source={source}
      data-filtered={filtered || undefined}
      data-origin={origin}
      data-kind={kind}
    >
      {timestamps && (
        <time>
          {new Date(time).toLocaleTimeString("ru", {
            hour: "2-digit",
            minute: "2-digit",
          })}{" "}
        </time>
      )}
      <span className={styles.channel}>[{channelName}] </span>
      {source === "player" && (
        <button
          className={styles.author}
          disabled={!active}
          aria-haspopup="menu"
          onClick={() => chat.open("profile", authorId)}
          // Ultralight may not raise contextmenu: the right button opens it too.
          onMouseDown={(e) => {
            if (e.button !== 2) return;
            e.preventDefault();
            chat.openAuthorMenu(authorId, name, e.clientX, e.clientY);
          }}
          onContextMenu={(e) => {
            e.preventDefault();
            chat.openAuthorMenu(authorId, name, e.clientX, e.clientY);
          }}
        >
          {name}:
        </button>
      )}
      {origin && kind && (
        <>
          <span className={styles.kind}>{kindLabels[kind]}</span>{" "}
          <span className={styles.origin}>{label}:</span>
        </>
      )}
      <span className={styles.text}> {text}</span>
    </div>
  );
});
export function Messages({ chat, state: s }: { chat: Chat; state: ChatState }) {
  const list = useRef<HTMLDivElement>(null);
  const faded = useMessageFade(s);
  const messages = s.messages.filter((m) =>
    visible(m, s.filter, s.settings, s.channels),
  );
  const newest = messages[messages.length - 1];
  const last = newest && keyOf(newest);
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
    (n, [id, count]) =>
      n + (shows(id, s.filter, s.settings, s.channels) ? count : 0),
    0,
  );
  const channels = new Map(s.channels.map((c) => [c.id, c]));
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
          const channel = channels.get(m.channelId);
          const announcement = announcementOf(m);
          const author = m.author ? playerName(m.author, s.settings) : "";
          const key = keyOf(m);
          return (
            <MessageRow
              key={key}
              chat={chat}
              time={m.time}
              text={m.text}
              source={m.source}
              authorId={m.source === "player" ? m.author.id : ""}
              name={m.source === "player" ? author : ""}
              channelName={channel?.name ?? "Канал"}
              channelKind={channel?.kind}
              filtered={!!m.filtered}
              origin={announcement?.origin}
              kind={announcement?.kind}
              label={announcement ? originLabel(announcement, author) : ""}
              faded={faded(key)}
              idleOpacity={s.settings.idleOpacity}
              duration={s.settings.duration}
              active={s.active}
              timestamps={s.settings.timestamps}
            />
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
