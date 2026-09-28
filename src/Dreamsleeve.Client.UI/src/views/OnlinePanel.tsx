import { useState } from "react";
import type { Chat, ChatState } from "../state/chat";
import { OnlineList } from "./OnlineList";
import { PlayerDetails } from "./PlayerDetails";
import { playerName, realNames } from "../state/names";
import styles from "../styles/Workspace.module.css";
export function OnlinePanel({
  chat,
  state: s,
}: {
  chat: Chat;
  state: ChatState;
}) {
  const [query, setQuery] = useState("");
  const normalized = query.trim().toLocaleLowerCase("ru");
  // Streamer mode searches only what is shown: pseudonyms and places.
  const players = s.players.filter((p) => {
    const real = realNames(p, s.settings);
    return [
      playerName(p, s.settings),
      real?.displayName,
      real?.username,
      real?.character,
      p.location,
      p.zone,
    ].some((v) => v?.toLocaleLowerCase("ru").includes(normalized));
  });
  return (
    <>
      <div className={styles.toolbar}>
        <span>
          {players.length} / {s.players.length} игроков
        </span>
        <div
          className={styles.viewSwitch}
          role="group"
          aria-label="Вид онлайна"
        >
          <button
            aria-pressed={s.settings.onlineView === "cards"}
            onClick={() => {
              chat.configure({ onlineView: "cards" });
              chat.save();
            }}
          >
            Карточки
          </button>
          <button
            aria-pressed={s.settings.onlineView === "list"}
            onClick={() => {
              chat.configure({ onlineView: "list" });
              chat.save();
            }}
          >
            Список
          </button>
        </div>
        <input
          aria-label="Поиск игроков"
          placeholder="Имя, персонаж или место…"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
      </div>
      {s.settings.onlineView === "list" ? (
        <OnlineList
          players={players}
          selfId={s.selfId}
          connected={s.connected}
          settings={s.settings}
          openProfile={(id) => chat.open("profile", id)}
        />
      ) : (
        <div className={styles.players}>
          {players.map((p) => (
            <article className={styles.player} key={p.id}>
              <header>
                <span>
                  {!s.connected
                    ? "ПОСЛЕДНИЕ ДАННЫЕ · НЕ В СЕТИ"
                    : p.id === s.selfId
                      ? "ВАШ ПЕРСОНАЖ"
                      : "В СЕТИ"}
                </span>
                <button onClick={() => chat.open("profile", p.id)}>
                  Профиль →
                </button>
              </header>
              <PlayerDetails player={p} settings={s.settings} />
            </article>
          ))}
        </div>
      )}
      {!players.length && <p className={styles.muted}>Игроки не найдены.</p>}
    </>
  );
}
