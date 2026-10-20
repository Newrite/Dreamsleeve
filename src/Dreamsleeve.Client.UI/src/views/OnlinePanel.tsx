import { useState } from "react";
import type { Chat, ChatState } from "../state/chat";
import { OnlineList } from "./OnlineList";
import { PlayerDetails } from "./PlayerDetails";
import { nameMatches } from "../state/names";
import { sortPlayers, type OnlineSort } from "../state/onlineSort";
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
  const players = s.players.filter(
    (p) =>
      nameMatches(p, s.settings, normalized) ||
      [p.location, p.zone].some((v) =>
        v?.toLocaleLowerCase("ru").includes(normalized),
      ),
  );
  // The same column again turns the direction; another starts from the top.
  const sortBy = (sort: OnlineSort) => {
    chat.configure({
      onlineSort: sort,
      onlineSortDescending:
        sort === s.settings.onlineSort && !s.settings.onlineSortDescending,
    });
    chat.save();
  };
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
          players={sortPlayers(
            players,
            s.settings.onlineSort,
            s.settings.onlineSortDescending,
            s.settings,
          )}
          selfId={s.selfId}
          connected={s.connected}
          settings={s.settings}
          openProfile={(id) => chat.open("profile", id)}
          onSort={sortBy}
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
