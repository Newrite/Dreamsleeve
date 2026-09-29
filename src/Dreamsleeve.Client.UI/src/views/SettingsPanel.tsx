import type { Chat } from "../state/chat";
import type { ConnectionPhase, IdentityState, Settings } from "../bridge/types";
import { defaults, isColor } from "../state/settings";
import { identityStatus } from "../state/identity";
import { Select } from "./Select";
import { useEffect, useState } from "react";
import styles from "../styles/Settings.module.css";
// A "#RRGGBB" text field with a swatch: an incomplete value is kept while
// typing and applied only once it is a valid colour.
function ColorField({
  label,
  value,
  onChange,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
}) {
  const [text, setText] = useState(value);
  useEffect(() => setText(value), [value]);
  return (
    <label className={styles.color}>
      <span>{label}</span>
      <span
        className={styles.swatch}
        aria-hidden="true"
        style={{ background: isColor(text) ? text : value }}
      />
      <input
        type="text"
        aria-label={label}
        maxLength={7}
        spellCheck={false}
        value={text}
        data-valid={isColor(text)}
        onChange={(e) => {
          const next = e.target.value.trim();
          setText(next);
          if (isColor(next) && next.toUpperCase() !== value.toUpperCase())
            onChange(next.toUpperCase());
        }}
        onBlur={() => {
          if (!isColor(text)) setText(value);
        }}
      />
    </label>
  );
}
// Counts of nearby marks: a fixed ladder plus the saved value if it is not on it.
function countOptions(current: number) {
  const ladder = [4, 8, 16, 32, 64];
  const values = ladder.includes(current)
    ? ladder
    : [...ladder, current].sort((a, b) => a - b);
  return values.map((n) => ({ value: String(n), label: String(n) }));
}
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
  identity,
  phase,
}: {
  chat: Chat;
  settings: Settings;
  ignored: { id: string; name: string }[];
  identity: IdentityState;
  phase: ConnectionPhase;
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
      <label className={styles.choice}>
        Шрифт
        <Select
          label="Шрифт"
          value={s.font}
          options={[
            { value: "serif", label: "Книжный" },
            { value: "sans", label: "Без засечек" },
          ]}
          onChange={(font) => chat.configure({ font })}
        />
      </label>
      <label className={styles.choice}>
        Тема
        <Select
          label="Тема"
          value={s.theme}
          options={[
            { value: "skyrim", label: "Skyrim" },
            { value: "contrast", label: "Контрастная" },
          ]}
          onChange={(theme) => chat.configure({ theme })}
        />
      </label>
      <label className={styles.choice}>
        Открыть чат
        <Select
          label="Открыть чат"
          value={s.activationKey}
          options={[
            { value: "Enter", label: "Enter" },
            { value: "F2", label: "F2" },
          ]}
          onChange={(activationKey) => chat.configure({ activationKey })}
        />
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
        <label className={styles.choice}>
          Показывать
          <Select
            label="Отображаемое имя"
            value={s.nameMode}
            disabled={s.streamerMode}
            options={[
              { value: "username", label: "Имя пользователя" },
              { value: "display", label: "Отображаемое имя" },
              { value: "character", label: "Имя персонажа" },
            ]}
            onChange={(nameMode) => chat.configure({ nameMode })}
          />
        </label>
        <p className={styles.muted}>
          Одно имя везде: чат, онлайн, профиль, надписи и сообщения над
          светлячками. Без имени персонажа показывается отображаемое имя.
          Применяется сразу.
        </p>
      </fieldset>
      <fieldset className={styles.group}>
        <legend>Режим стримера</legend>
        <label>
          <span>Скрывать чужие имена (только у меня)</span>
          <input
            type="checkbox"
            aria-label="Скрывать чужие имена (только у меня)"
            checked={s.streamerMode}
            onChange={(e) => chat.configure({ streamerMode: e.target.checked })}
          />
        </label>
        <label className={styles.choice}>
          Скрывать моё имя от других игроков
          <Select
            label="Скрывать моё имя от других игроков"
            value={identity.mode}
            disabled={identity.pending}
            options={[
              { value: "off", label: "Нет" },
              { value: "everywhere", label: "Везде, включая метки на земле" },
              {
                value: "exceptGroundMarks",
                label: "Везде, кроме меток на земле",
              },
            ]}
            onChange={(hiding) => chat.setHideIdentity(hiding)}
          />
        </label>
        <p
          className={styles.identity}
          role="status"
          aria-label="Скрытое имя"
          data-pending={identity.pending}
        >
          {identityStatus(identity, phase)}
        </p>
        {identity.error && (
          <p className={styles.error} role="alert">
            {identity.error}
          </p>
        )}
        <p className={styles.muted}>
          «Скрывать чужие имена» заменяет на вашем экране все имена локальными
          псевдонимами; на сервер это не уходит и не скрывает имена, написанные
          в тексте сообщений. «Скрывать моё имя» просит сервер показывать другим
          игрокам вместо вашего имени пользователя, отображаемого имени и имени
          персонажа псевдоним сервера — в онлайне, над светлячком и в чате, а по
          выбору и в метках на земле; вы по-прежнему видите своё имя. Сообщения,
          объявления и метки, оставленные под псевдонимом, навсегда остаются под
          ним, оставленные раньше — под настоящим именем. ID аккаунта виден
          всегда: псевдонимы одного игрока можно сопоставить между входами, а
          если метки остаются под вашим именем, по ним можно узнать, кто стоит
          за псевдонимом.
        </p>
      </fieldset>
      <fieldset className={styles.group}>
        <legend>Помеченные сообщения</legend>
        <label className={styles.choice}>
          Слова, помеченные сервером
          <Select
            label="Помеченные сообщения"
            value={s.textFilter}
            options={[
              { value: "off", label: "Показывать как есть" },
              { value: "mask", label: "Заменять звёздочками" },
              { value: "hide", label: "Скрывать сообщение целиком" },
            ]}
            onChange={(textFilter) => chat.configure({ textFilter })}
          />
        </label>
        <p className={styles.muted}>
          Сервер отмечает, но не запрещает слова из своего списка (например,
          опасные для трансляций). Фильтр работает только у вас: в чате и в
          сообщениях над светлячками. Ваше скрытое сообщение показывается
          заглушкой. Применяется сразу.
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
          Добавить игрока: правый клик по нику в чате или «Игнорировать» в
          профиле. Список хранится локально для этого сервера.
        </p>
      </fieldset>
      <fieldset className={styles.group}>
        <legend>Объявления</legend>
        <label className={styles.choice}>
          Где показывать
          <Select
            label="Где показывать"
            value={s.announcementChannels}
            options={[
              { value: "tab", label: "Только во вкладке «Объявления»" },
              { value: "all", label: "Также во «Все»" },
              { value: "current", label: "Также в текущем канале" },
            ]}
            onChange={(announcementChannels) =>
              chat.configure({ announcementChannels })
            }
          />
        </label>
        {(
          [
            ["announcementsServer", "От сервера"],
            ["announcementsTrustedClient", "От клиента Dreamsleeve"],
            ["announcementsThirdParty", "От других модов"],
            ["announcementsEvents", "События"],
            ["announcementsPeriodic", "Периодические"],
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
        <p className={styles.muted}>
          Выключенные источники и виды скрываются везде, включая вкладку
          «Объявления». Подпись другого мода указывает сам мод, сервер её не
          проверяет. Применяется сразу.
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
        <ColorField
          label="Цвет имени"
          value={s.fireflyNameColor}
          onChange={(fireflyNameColor) => chat.configure({ fireflyNameColor })}
        />
        {(
          [
            ["fireflyNameFontSize", "Размер шрифта имени", 8, 48],
            ["fireflyNameOffset", "Высота имени над светлячком", 0, 512],
            ["fireflyHeightOffset", "Высота светлячка над землёй", 0, 512],
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
            ["bubbleBorder", "Рамка сообщения"],
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
        <ColorField
          label="Цвет текста сообщения"
          value={s.bubbleTextColor}
          onChange={(bubbleTextColor) => chat.configure({ bubbleTextColor })}
        />
        <p className={styles.muted}>
          Последнее сообщение общего канала показывается над светлячком автора;
          новое сообщение заменяет предыдущее. Не зависит от показа имён и
          затухания окна чата. Без фона и рамки — непрозрачность 0 и рамка
          выключена. Только SE/AE.
        </p>
      </fieldset>
      <fieldset className={styles.group}>
        <legend>Метки на земле</legend>
        {(
          [
            ["showGroundNotes", "Показывать надписи"],
            ["showDeathMarks", "Показывать места смерти"],
            ["groundBorder", "Рамка надписи"],
            ["deathBorder", "Рамка места смерти"],
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
        <label className={styles.choice}>
          Надписей рядом
          <Select
            label="Надписей рядом"
            value={String(s.maxVisibleNotes)}
            options={countOptions(s.maxVisibleNotes)}
            onChange={(value) =>
              chat.configure({ maxVisibleNotes: Number(value) })
            }
          />
        </label>
        <label className={styles.choice}>
          Мест смерти рядом
          <Select
            label="Мест смерти рядом"
            value={String(s.maxVisibleDeaths)}
            options={countOptions(s.maxVisibleDeaths)}
            onChange={(value) =>
              chat.configure({ maxVisibleDeaths: Number(value) })
            }
          />
        </label>
        {(
          [
            ["groundDrawDistance", "Дальность прорисовки", 0, 16384, 128],
            ["groundNameDistance", "Дальность имени автора", 50, 4096, 10],
            ["groundTextDistance", "Дальность текста", 50, 4096, 10],
            ["groundNoteOffset", "Высота надписи над полом", -64, 256, 1],
            ["deathMarkOffset", "Высота места смерти над полом", -64, 256, 1],
            ["groundFontSize", "Размер шрифта метки", 8, 48, 1],
            [
              "groundMaxWidth",
              "Максимальная ширина текста метки",
              120,
              800,
              10,
            ],
            ["groundBackground", "Непрозрачность фона надписи", 0, 1, 0.05],
            ["deathBackground", "Непрозрачность фона места смерти", 0, 1, 0.05],
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
        <ColorField
          label="Цвет текста надписи"
          value={s.groundTextColor}
          onChange={(groundTextColor) => chat.configure({ groundTextColor })}
        />
        <ColorField
          label="Цвет текста места смерти"
          value={s.deathTextColor}
          onChange={(deathTextColor) => chat.configure({ deathTextColor })}
        />
        <p className={styles.muted}>
          Надписи других игроков и места смерти рядом с вами: статик на земле,
          имя автора и текст над ним. Дальности — в игровых единицах; имя и
          текст скрываются чуть дальше, чем появляются, чтобы не мигать на
          границе. Метки игнорируемых игроков не показываются. Применяется после
          сохранения. Только SE/AE.
        </p>
      </fieldset>
      <fieldset className={styles.group}>
        <legend>В бою</legend>
        {(
          [
            ["combatHideFireflies", "Скрывать светлячки"],
            ["combatHideNames", "Скрывать имена"],
            ["combatHideBubbles", "Скрывать сообщения над игроками"],
            ["combatHideGroundMarks", "Скрывать метки"],
            ["combatHideGroundText", "Скрывать текст меток"],
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
        <p className={styles.muted}>
          Пока ваш персонаж в бою. Без светлячка нет и имени с сообщением над
          ним. Сообщения не теряются: они появятся после боя, если время показа
          ещё не вышло. Применяется после сохранения.
        </p>
      </fieldset>
      <button className={styles.primary} onClick={chat.save}>
        Сохранить настройки
      </button>
    </div>
  );
}
