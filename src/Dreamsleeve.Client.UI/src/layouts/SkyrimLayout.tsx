import { controlKey } from "../features/keyboard";
import { connectionLabels } from "../state/connection";
import { useRef, type CSSProperties, type FormEvent } from "react";
import type { Chat } from "../state/chat";
import { frame } from "../state/settings";
import { useChat } from "../features/useChat";
import { useFrame } from "../features/useFrame";
import { Messages } from "../views/Messages";
import { Panels } from "../views/Panels";
import { AuthorMenu } from "../views/AuthorMenu";
import { Select } from "../views/Select";
import styles from "../styles/Chat.module.css";
export function SkyrimLayout({ chat }: { chat: Chat }) {
  const { state: s, input } = useChat(chat);
  const frameRef = useRef<HTMLElement>(null);
  const { viewport, start } = useFrame(chat, frameRef);
  const settings = s.settings;
  const bounds = frame(settings, viewport.width, viewport.height);
  const faded = s.faded && settings.fade && !s.active && s.connected;
  const writable = s.channels.filter((c) => c.writable);
  const style = {
    ...bounds,
    "--chat-font-size": `${settings.fontSize * settings.scale}px`,
    "--chat-line-height": settings.lineHeight,
    "--chat-background": settings.background,
    "--chat-font":
      settings.font === "serif"
        ? "Georgia, Times New Roman, serif"
        : "Segoe UI, Arial, sans-serif",
    "--chrome-opacity": faded ? 0 : 1,
    transitionDuration: `${settings.duration}s`,
  } as CSSProperties;
  function submit(e: FormEvent) {
    e.preventDefault();
    chat.submit();
  }
  return (
    <>
      <section
        ref={frameRef}
        style={style}
        className={styles.window}
        data-part="chat"
        data-active={s.active}
        data-connected={s.connected}
        data-theme={settings.theme}
        data-full-color={settings.fullColor}
        aria-label="Чат Dreamsleeve"
      >
        <header className={styles.header} data-part="header">
          <span className={styles.ornament}>◇</span>
          <div
            className={styles.title}
            onMouseDown={(e) => start(e, false)}
            title={settings.locked ? "Окно закреплено" : "Перетащить окно"}
          >
            <span title={s.serverName}>{s.serverName || "DREAMSLEEVE"}</span>
            <small>DREAMSLEEVE</small>
          </div>
          <span
            className={styles.connection}
            data-connected={s.connected}
            aria-live="polite"
            aria-label="Состояние подключения"
          >
            {connectionLabels[s.connectionPhase]}
          </span>
          {s.active && (
            <>
              <button
                aria-label="Открыть меню Dreamsleeve"
                title="Онлайн, профиль, аккаунт и настройки"
                onClick={() => chat.open(s.connected ? "online" : "account")}
              >
                ☰
              </button>
              <button
                title={settings.locked ? "Открепить окно" : "Закрепить окно"}
                aria-label="Закрепление окна"
                onClick={() => {
                  chat.configure({ locked: !settings.locked });
                  chat.save();
                }}
              >
                {settings.locked ? "◆" : "◇"}
              </button>
            </>
          )}
        </header>
        {s.active && (
          <nav className={styles.tabs} aria-label="Каналы">
            <button
              data-selected={s.filter === "all"}
              onClick={() => chat.select("all")}
            >
              Все
            </button>
            {s.channels.map((c) => (
              <button
                key={c.id}
                data-selected={s.filter === c.id}
                data-channel={c.kind}
                onClick={() => chat.select(c.id)}
              >
                {c.name}
                {s.unread[c.id] > 0 && <sup>{s.unread[c.id]}</sup>}
              </button>
            ))}
          </nav>
        )}
        <Messages chat={chat} state={s} />
        {s.notice && (
          <div className={styles.notice} role="status">
            {s.notice}
          </div>
        )}

        {s.active ? (
          <>
            <form className={styles.composer} onSubmit={submit}>
              <div className={styles.channelPicker}>
                <Select
                  label="Канал отправки"
                  variant="plain"
                  value={s.target}
                  options={
                    writable.length
                      ? writable.map((c) => ({ value: c.id, label: c.name }))
                      : [{ value: "", label: "Нет каналов" }]
                  }
                  onChange={(target) => chat.store.setState({ target })}
                />
              </div>
              <input
                ref={input}
                aria-label="Сообщение"
                placeholder={s.connected ? "Ваше сообщение…" : "Нет соединения"}
                value={s.drafts[s.target] ?? ""}
                maxLength={2000}
                disabled={Object.values(s.pending).some(
                  (p) => p.status === "sending",
                )}
                onChange={(e) => chat.setDraft(e.target.value)}
                onKeyDown={(e) => {
                  if (controlKey(e.nativeEvent) !== "Enter") return;
                  e.preventDefault();
                  if (
                    e.nativeEvent.isComposing ||
                    e.keyCode === 229 ||
                    e.repeat
                  )
                    return;
                  chat.submit();
                }}
              />
              <button
                type="submit"
                aria-label="Отправить"
                disabled={
                  !s.connected ||
                  !s.target ||
                  Object.values(s.pending).some((p) => p.status === "sending")
                }
              >
                ↵
              </button>
            </form>
            <footer className={styles.footer}>
              <button onClick={() => chat.open("online")}>
                Онлайн <span>{s.players.length}</span>
              </button>
              {!s.connected && (
                <button onClick={() => chat.open("account")}>Аккаунт</button>
              )}
              <small>ESC · закрыть</small>
            </footer>
            {!settings.locked && (
              <button
                className={styles.resize}
                aria-label="Изменить размер"
                onMouseDown={(e) => start(e, true)}
              >
                ◢
              </button>
            )}
          </>
        ) : (
          <>
            <div className={styles.hint}>
              Онлайн {s.players.length} · {settings.activationKey} · написать
              сообщение
            </div>
            {!s.connected && !s.auth.savedLogin && (
              <div className={styles.accountHint} role="note">
                Нет сохранённого входа · {settings.activationKey} → ☰ → Аккаунт
              </div>
            )}
          </>
        )}
      </section>
      {s.active && s.authorMenu && <AuthorMenu chat={chat} state={s} />}
      {s.active && s.panel && <Panels chat={chat} state={s} />}
    </>
  );
}
