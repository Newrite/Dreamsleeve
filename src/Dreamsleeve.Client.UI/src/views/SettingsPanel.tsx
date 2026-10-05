import type { Chat } from "../state/chat";
import type {
  ConnectionPhase,
  IdentityState,
  NameColorState,
  Settings,
} from "../bridge/types";
import { isColor } from "../state/settings";
import { nameColorPalette, hueColor, hueOf } from "../state/nameColor";
import { defaults, limits } from "../bridge/settings.generated";
import { identityStatus } from "../state/identity";
import { Select } from "./Select";
import { PhantomPanel } from "./PhantomPanel";
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
// The own name color in chat: a server setting, applied with its own button.
// The server refuses a color too dark to read; the preview shows the chat line.
function NameColorPicker({
  chat,
  current,
  state,
  connected,
  hidden,
  name,
}: {
  chat: Chat;
  current: string | undefined;
  state: NameColorState;
  connected: boolean;
  hidden: boolean;
  name: string;
}) {
  const [draft, setDraft] = useState(current ?? nameColorPalette[0]);
  useEffect(() => {
    if (current) setDraft(current);
  }, [current]);
  const changed = draft.toUpperCase() !== (current ?? "").toUpperCase();
  const status = state.pending
    ? "Ожидание сервера…"
    : state.error
      ? state.error
      : state.changed
        ? "Цвет сохранён"
        : connected
          ? ""
          : "Войдите, чтобы выбрать цвет";
  return (
    <fieldset className={styles.group} data-part="name-color">
      <legend>Цвет вашего имени в чате</legend>
      <p className={styles.preview} aria-label="Как увидят другие">
        <span className={styles.previewChannel}>[Общий]</span>{" "}
        <b style={{ color: draft }}>{name || "Ваше имя"}:</b> Привет, Скайрим!
      </p>
      <div className={styles.palette} role="group" aria-label="Готовые цвета">
        {nameColorPalette.map((color) => (
          <button
            key={color}
            type="button"
            className={styles.paletteColor}
            style={{ background: color }}
            aria-label={color}
            aria-pressed={color === draft.toUpperCase()}
            onClick={() => setDraft(color)}
          />
        ))}
      </div>
      <label className={styles.range}>
        <span>Оттенок</span>
        <input
          type="range"
          aria-label="Оттенок"
          min={0}
          max={359}
          step={1}
          value={hueOf(draft)}
          onChange={(e) => setDraft(hueColor(Number(e.target.value)))}
        />
      </label>
      <ColorField label="Свой цвет" value={draft} onChange={setDraft} />
      <button
        type="button"
        className={`${styles.primary} ${styles.apply}`}
        disabled={!connected || state.pending || !changed}
        onClick={() => chat.setNameColor(draft)}
      >
        Применить цвет
      </button>
      <p className={styles.muted} role="status" aria-label="Смена цвета">
        {status}
      </p>
      <p className={styles.muted}>
        Цвет видят все игроки во всех чатах. Слишком тёмный сервер не примет.
        {hidden && " Пока имя скрыто, другие видят его цветом по умолчанию."}
      </p>
    </fieldset>
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
// Sliders take their bounds from the host's limits; only the step is the panel's.
const ranges = [
  ["delay", "Тишина до затухания, с", 1],
  ["duration", "Длительность fade, с", 0.1],
  ["idleOpacity", "Видимость после fade", 0.05],
  ["scale", "Масштаб", 0.05],
  ["fontSize", "Размер шрифта", 1],
  ["lineHeight", "Межстрочный интервал", 0.05],
  ["background", "Непрозрачность фона", 0.05],
] as const;
export function SettingsPanel({
  chat,
  settings: s,
  identity,
  phase,
  nameColor,
  selfColor,
  selfName,
  connected,
}: {
  chat: Chat;
  settings: Settings;
  identity: IdentityState;
  phase: ConnectionPhase;
  nameColor: NameColorState;
  selfColor: string | undefined;
  selfName: string;
  connected: boolean;
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
      {ranges.map(([key, label, step]) => (
        <label key={key} className={styles.range}>
          <span>
            {label}
            <output>{s[key].toFixed(step < 1 ? 2 : 0)}</output>
          </span>
          <input
            type="range"
            aria-label={label}
            min={limits[key].min}
            max={limits[key].max}
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
      <NameColorPicker
        chat={chat}
        current={selfColor}
        state={nameColor}
        connected={connected}
        hidden={identity.mode !== "off"}
        name={selfName}
      />
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
        <legend>Светлячки и имена над ними</legend>
        {(
          [
            ["fireflyGuildmatesOnly", "Только игроки из ваших гильдий"],
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
            ["fireflyNameFontSize", "Размер шрифта имени"],
            ["fireflyNameOffset", "Высота имени над светлячком"],
            ["fireflyHeightOffset", "Высота светлячка над землёй"],
          ] as const
        ).map(([key, label]) => (
          <label key={key} className={styles.range}>
            <span>
              {label}
              <output>{s[key].toFixed(0)}</output>
            </span>
            <input
              type="range"
              aria-label={label}
              min={limits[key].min}
              max={limits[key].max}
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
            ["bubbleDuration", "Время показа, с", 1],
            ["bubbleFadeDuration", "Длительность исчезновения, с", 0.1],
            ["bubbleFontSize", "Размер шрифта сообщения", 1],
            ["bubbleMaxWidth", "Максимальная ширина", 10],
            ["bubbleBackground", "Непрозрачность фона сообщения", 0.05],
          ] as const
        ).map(([key, label, step]) => (
          <label key={key} className={styles.range}>
            <span>
              {label}
              <output>{s[key].toFixed(step < 1 ? 2 : 0)}</output>
            </span>
            <input
              type="range"
              aria-label={label}
              min={limits[key].min}
              max={limits[key].max}
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
            ["markGuildmatesOnly", "Только от игроков из ваших гильдий"],
            ["groundBorder", "Рамка надписи"],
            ["deathBorder", "Рамка места смерти"],
            ["deathDateHeader", "Дата над местом смерти"],
            ["noteDateHeader", "Дата над надписью"],
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
          Календарь дат
          <Select
            label="Календарь дат"
            value={s.markDateStyle}
            options={[
              {
                value: "tamriel",
                label: "Тамриэльский: Тирдас, Последнего зерна",
              },
              { value: "earth", label: "Привычный: вторник, августа" },
            ]}
            onChange={(markDateStyle) => chat.configure({ markDateStyle })}
          />
        </label>
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
            ["groundDrawDistance", "Дальность прорисовки", 128],
            ["groundNameDistance", "Дальность имени автора", 10],
            ["groundTextDistance", "Дальность текста", 10],
            ["groundNoteOffset", "Высота надписи над полом", 1],
            ["deathMarkOffset", "Высота места смерти над полом", 1],
            ["groundFontSize", "Размер шрифта метки", 1],
            ["groundMaxWidth", "Максимальная ширина текста метки", 10],
            ["groundBackground", "Непрозрачность фона надписи", 0.05],
            ["deathBackground", "Непрозрачность фона места смерти", 0.05],
          ] as const
        ).map(([key, label, step]) => (
          <label key={key} className={styles.range}>
            <span>
              {label}
              <output>{s[key].toFixed(step < 1 ? 2 : 0)}</output>
            </span>
            <input
              type="range"
              aria-label={label}
              min={limits[key].min}
              max={limits[key].max}
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
        <ColorField
          label="Цвет даты"
          value={s.markDateColor}
          onChange={(markDateColor) => chat.configure({ markDateColor })}
        />
        <p className={styles.muted}>
          Дата — игровая, как её видел автор: день недели, число, месяц, эра и
          год, время. Она стоит шапкой над текстом метки и видна вместе с ним;
          тот же календарь используется во вкладке «Метки». У меток, оставленных
          до появления дат, её нет.
        </p>
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
      <PhantomPanel chat={chat} />
      <button className={styles.primary} onClick={chat.save}>
        Сохранить настройки
      </button>
    </div>
  );
}
