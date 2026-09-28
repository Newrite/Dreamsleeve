import { connectionLabels } from "../state/connection";
import type { Chat, ChatState, Panel } from "../state/chat";
import { SettingsPanel } from "./SettingsPanel";
import { OnlinePanel } from "./OnlinePanel";
import { PlayerDetails } from "./PlayerDetails";
import { AccountPanel } from "./AccountPanel";
import { useDialog } from "../features/useDialog";
import styles from "../styles/Workspace.module.css";
const tabs: { id: Exclude<Panel, null>; label: string }[] = [
  { id: "online", label: "Онлайн" },
  { id: "profile", label: "Профиль" },
  { id: "stats", label: "Статистика" },
  { id: "settings", label: "Настройки" },
  { id: "account", label: "Аккаунт" },
];
export function Panels({ chat, state: s }: { chat: Chat; state: ChatState }) {
  const dialog = useDialog();
  const player = s.players.find((p) => p.id === s.selectedPlayer);
  return (
    <div
      className={styles.backdrop}
      data-theme={s.settings.theme}
      data-part="workspace"
    >
      <aside
        ref={dialog}
        className={styles.workspace}
        role="dialog"
        aria-modal="true"
        aria-label="Меню Dreamsleeve"
      >
        <header className={styles.header}>
          <div>
            <small>DREAMSLEEVE</small>
            <h2>{s.serverName || "Странники Тамриэля"}</h2>
          </div>
          <span className={styles.status}>
            {connectionLabels[s.connectionPhase]} · Онлайн {s.players.length}
          </span>
          <button aria-label="Закрыть панель" onClick={() => chat.open(null)}>
            ×
          </button>
        </header>
        <nav aria-label="Разделы меню" className={styles.nav}>
          {tabs.map((t) => (
            <button
              key={t.id}
              aria-current={s.panel === t.id ? "page" : undefined}
              onClick={() => chat.open(t.id)}
            >
              {t.label}
            </button>
          ))}
        </nav>
        <div
          className={`${styles.body} ${s.panel === "online" ? styles.onlineBody : ""}`}
        >
          {s.panel === "online" && <OnlinePanel chat={chat} state={s} />}
          {s.panel === "settings" && (
            <>
              <h3>Настройки чата</h3>
              <p className={styles.muted}>
                Изменения применяются к чату. Это окно сохраняет своё положение
                и размер.
              </p>
              <SettingsPanel chat={chat} settings={s.settings} />
            </>
          )}
          {s.panel === "profile" &&
            (player ? (
              <PlayerDetails player={player} />
            ) : (
              <p>Игрок сейчас не в сети. Данные профиля недоступны.</p>
            ))}
          {s.panel === "account" && <AccountPanel chat={chat} state={s} />}
          {s.panel === "stats" && (
            <>
              <h3>Состояние клиента</h3>
              <dl>
                <dt>Соединение</dt>
                <dd>{s.connected ? "Установлено" : "Отсутствует"}</dd>
                <dt>Игроков онлайн</dt>
                <dd>{s.players.length}</dd>
                <dt>Сообщений в памяти UI</dt>
                <dd>{s.messages.length} / 500</dd>
                <dt>Ожидает отправки</dt>
                <dd>{Object.keys(s.pending).length}</dd>
              </dl>
            </>
          )}
        </div>
        <footer className={styles.footer}>
          <span role="status" aria-label="Результат операции">
            {s.notice ||
              "Настройки применяются сразу; сохраните их перед закрытием."}
          </span>
          <small>ESC · вернуться в чат</small>
        </footer>
      </aside>
    </div>
  );
}
