import type { Chat } from "../state/chat";
import type { Settings } from "../bridge/types";
import { defaults } from "../state/settings";
import styles from "../styles/Settings.module.css";
const ranges = [
  ["delay", "Тишина до затухания, с", 0, 60, 1],
  ["duration", "Длительность fade, с", 0, 5, 0.1],
  ["idleOpacity", "Видимость после fade", 0, 1, 0.05],
  ["scale", "Масштаб", 0.7, 1.5, 0.05],
  ["fontSize", "Размер шрифта", 12, 26, 1],
  ["lineHeight", "Межстрочный интервал", 1.1, 2, 0.05],
  ["background", "Непрозрачность фона", 0, 1, 0.05],
] as const;
export function SettingsPanel({
  chat,
  settings: s,
  ignored,
}: {
  chat: Chat;
  settings: Settings;
  ignored: { id: string; name: string }[];
}) {
  return (
    <div className={styles.settings}>
      {(["fade", "timestamps", "fullColor", "locked"] as const).map(
        (key, i) => (
          <label key={key}>
            <span>
              {
                [
                  "Плавно скрывать чат",
                  "Время сообщений",
                  "Окрашивать всю строку",
                  "Закрепить окно",
                ][i]
              }
            </span>
            <input
              type="checkbox"
              checked={s[key]}
              onChange={(e) => chat.configure({ [key]: e.target.checked })}
            />
          </label>
        ),
      )}
      {ranges.map(([key, label, min, max, step]) => (
        <label key={key} className={styles.range}>
          <span>
            {label}
            <output>{s[key].toFixed(step < 1 ? 2 : 0)}</output>
          </span>
          <input
            type="range"
            aria-label={label}
            min={min}
            max={max}
            step={step}
            value={s[key]}
            onChange={(e) => chat.configure({ [key]: Number(e.target.value) })}
          />
        </label>
      ))}
      <label>
        Шрифт
        <select
          value={s.font}
          onChange={(e) =>
            chat.configure({ font: e.target.value as Settings["font"] })
          }
        >
          <option value="serif">Книжный</option>
          <option value="sans">Без засечек</option>
        </select>
      </label>
      <label>
        Тема
        <select
          value={s.theme}
          onChange={(e) =>
            chat.configure({ theme: e.target.value as Settings["theme"] })
          }
        >
          <option value="skyrim">Skyrim</option>
          <option value="contrast">Контрастная</option>
        </select>
      </label>
      <label>
        Открыть чат
        <select
          value={s.activationKey}
          onChange={(e) =>
            chat.configure({
              activationKey: e.target.value as Settings["activationKey"],
            })
          }
        >
          <option>Enter</option>
          <option>F2</option>
        </select>
      </label>
      <p className={styles.muted}>
        Открепите окно, чтобы перемещать его за заголовок и изменять размер за
        нижний угол.
      </p>
      <button
        onClick={() =>
          chat.configure({
            x: defaults.x,
            y: defaults.y,
            width: defaults.width,
            height: defaults.height,
            scale: 1,
          })
        }
      >
        Сбросить расположение
      </button>
      <fieldset className={styles.group}>
        <legend>Отображение имён</legend>
        <label>
          Показывать
          <select
            aria-label="Отображаемое имя"
            value={s.nameMode}
            disabled={s.streamerMode}
            onChange={(e) =>
              chat.configure({
                nameMode: e.target.value as Settings["nameMode"],
              })
            }
          >
            <option value="username">Имя пользователя</option>
            <option value="display">Отображаемое имя</option>
            <option value="character">Имя персонажа</option>
          </select>
        </label>
        <label>
          <span>Режим стримера</span>
          <input
            type="checkbox"
            aria-label="Режим стримера"
            checked={s.streamerMode}
            onChange={(e) => chat.configure({ streamerMode: e.target.checked })}
          />
        </label>
        <p className={styles.muted}>
          Одно имя везде: чат, онлайн, профиль, надписи и сообщения над
          светлячками. Без имени персонажа показывается отображаемое имя. Режим
          стримера заменяет все имена локальными псевдонимами (они не уходят на
          сервер), но не скрывает имена, написанные в тексте сообщений.
          Применяется сразу.
        </p>
      </fieldset>
      <fieldset className={styles.group}>
        <legend>Игнорируемые игроки</legend>
        {ignored.length ? (
          <ul className={styles.ignored} aria-label="Игнорируемые игроки">
            {ignored.map((p) => (
              <li key={p.id}>
                <span>{p.name}</span>
                <button onClick={() => chat.unignore(p.id)}>Убрать</button>
              </li>
            ))}
          </ul>
        ) : (
          <p className={styles.muted}>Список пуст.</p>
        )}
        <p className={styles.muted}>
          Добавить игрока: «Игнорировать» в его профиле. Список хранится
          локально для этого сервера.
        </p>
      </fieldset>
      <fieldset className={styles.group}>
        <legend>Имена над светлячками</legend>
        {(
          [
            ["showFireflyNames", "Показывать имена"],
            ["fireflyNameOcclusion", "Скрывать имена за препятствиями"],
          ] as const
        ).map(([key, label]) => (
          <label key={key}>
            <span>{label}</span>
            <input
              type="checkbox"
              checked={s[key]}
              onChange={(e) => chat.configure({ [key]: e.target.checked })}
            />
          </label>
        ))}
        {(
          [
            ["fireflyNameFontSize", "Размер шрифта имени", 8, 48],
            ["fireflyNameOffset", "Высота имени над светлячком", 0, 512],
          ] as const
        ).map(([key, label, min, max]) => (
          <label key={key} className={styles.range}>
            <span>
              {label}
              <output>{s[key].toFixed(0)}</output>
            </span>
            <input
              type="range"
              aria-label={label}
              min={min}
              max={max}
              step={1}
              value={s[key]}
              onChange={(e) =>
                chat.configure({ [key]: Number(e.target.value) })
              }
            />
          </label>
        ))}
        <p className={styles.muted}>
          Применяются после сохранения, без перезапуска игры. Высота — в игровых
          единицах. Имена пока доступны только в SE/AE.
        </p>
      </fieldset>
      <fieldset className={styles.group}>
        <legend>Сообщения над игроками</legend>
        {(
          [
            ["showBubbles", "Показывать сообщения"],
            ["bubbleFade", "Плавно скрывать сообщение"],
          ] as const
        ).map(([key, label]) => (
          <label key={key}>
            <span>{label}</span>
            <input
              type="checkbox"
              checked={s[key]}
              onChange={(e) => chat.configure({ [key]: e.target.checked })}
            />
          </label>
        ))}
        {(
          [
            ["bubbleDuration", "Время показа, с", 1, 60, 1],
            ["bubbleFadeDuration", "Длительность исчезновения, с", 0.1, 5, 0.1],
            ["bubbleFontSize", "Размер шрифта сообщения", 8, 48, 1],
            ["bubbleMaxWidth", "Максимальная ширина", 120, 800, 10],
            ["bubbleBackground", "Непрозрачность фона сообщения", 0, 1, 0.05],
          ] as const
        ).map(([key, label, min, max, step]) => (
          <label key={key} className={styles.range}>
            <span>
              {label}
              <output>{s[key].toFixed(step < 1 ? 2 : 0)}</output>
            </span>
            <input
              type="range"
              aria-label={label}
              min={min}
              max={max}
              step={step}
              value={s[key]}
              onChange={(e) =>
                chat.configure({ [key]: Number(e.target.value) })
              }
            />
          </label>
        ))}
        <p className={styles.muted}>
          Последнее сообщение общего канала показывается над светлячком автора;
          новое сообщение заменяет предыдущее. Не зависит от показа имён и
          затухания окна чата. Только SE/AE.
        </p>
      </fieldset>
      <button className={styles.primary} onClick={chat.save}>
        Сохранить настройки
      </button>
    </div>
  );
}
