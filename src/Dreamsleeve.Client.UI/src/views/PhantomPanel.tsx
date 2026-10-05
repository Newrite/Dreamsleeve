import { useEffect } from "react";
import { useStore } from "zustand";
import type { Chat } from "../state/chat";
import { Select } from "./Select";
import styles from "../styles/Settings.module.css";

export function PhantomPanel({ chat }: { chat: Chat }) {
  const state = useStore(chat.store, (s) => s.phantom);
  const rate = useStore(chat.store, (s) => s.phantomRate);
  const scenario = useStore(chat.store, (s) => s.phantomScenario);
  const poseMode = useStore(chat.store, (s) => s.phantomPoseMode);
  const modelMode = useStore(chat.store, (s) => s.phantomModelMode);
  useEffect(() => {
    chat.phantom("query");
  }, [chat]);
  const busy = state.recording || state.playing || state.loading;
  return (
    <fieldset className={styles.group}>
      <legend>Фантом — локальный тест SE / AE / VR</legend>
      <label className={styles.choice}>
        Сценарий записи
        <Select
          label="Сценарий записи фантома"
          value={scenario}
          options={[
            { value: "idle", label: "Покой" },
            { value: "movement", label: "Движение" },
            { value: "combat", label: "Бой" },
            { value: "camera", label: "Смена камеры" },
            { value: "equipment", label: "Оружие" },
            { value: "mixed", label: "Смешанный" },
          ]}
          onChange={(value) => chat.setPhantomScenario(value)}
          disabled={busy || state.exporting}
        />
      </label>
      <label className={styles.choice}>
        Частота записи
        <Select
          label="Частота записи фантома"
          value={String(rate) as "20" | "40"}
          options={[
            { value: "20", label: "20 Гц" },
            { value: "40", label: "40 Гц" },
          ]}
          onChange={(value) => chat.setPhantomRate(value === "20" ? 20 : 40)}
          disabled={busy}
        />
      </label>
      <label className={styles.choice}>
        Позы воспроизведения
        <Select
          label="Позы воспроизведения фантома"
          value={poseMode}
          options={[
            { value: "full", label: "Исходные — все узлы" },
            { value: "selected", label: "Выбранные — float" },
            { value: "quantized", label: "Выбранные — квантованные" },
          ]}
          onChange={(value) => chat.setPhantomPoseMode(value)}
          disabled={busy}
        />
      </label>
      <label className={styles.choice}>
        Модель воспроизведения
        <Select
          label="Модель воспроизведения фантома"
          value={modelMode}
          options={[
            { value: "original", label: "Исходная модель" },
            { value: "pruned", label: "Без скрытой геометрии" },
          ]}
          onChange={(value) => chat.setPhantomModelMode(value)}
          disabled={busy}
        />
      </label>
      <div className={styles.phantomControls}>
        <button
          className={styles.primary}
          disabled={!state.supported || busy || state.exporting}
          onClick={() => chat.phantom("load")}
        >
          Загрузить последний архив
        </button>
        <button
          className={styles.primary}
          disabled={!state.supported || busy || state.exporting}
          onClick={() => chat.phantom("record")}
        >
          Записать 15 секунд
        </button>
        <button
          className={styles.primary}
          disabled={!state.supported || !state.ready || busy}
          onClick={() => chat.phantom("play")}
        >
          Воспроизвести фантома
        </button>
        <button
          className={styles.primary}
          disabled={!state.supported}
          onClick={() => chat.phantom("stop")}
        >
          Остановить
        </button>
        <button
          className={styles.primary}
          disabled={!state.supported}
          onClick={() => chat.phantom("clear")}
        >
          Очистить запись
        </button>
      </div>
      <p role="status">{state.status}</p>
      {state.loadedArchive && (
        <p className={styles.muted}>Загружен: {state.loadedArchive}</p>
      )}
      {state.exporting && <p role="status">Сохранение файлов записи…</p>}
      {state.exportError && (
        <p role="alert">Не удалось сохранить: {state.exportError}</p>
      )}
      {state.exportPath && !state.exportError && !state.exporting && (
        <p className={styles.muted}>Файлы сохранены: {state.exportPath}</p>
      )}
      <p className={styles.muted}>
        {state.seconds.toFixed(1)} с · {state.frames} кадров · {state.nodes}{" "}
        узлов · {state.bones} костей
        <br />
        Модель: {(state.appearanceBytes / 1048576).toFixed(2)} МиБ · Позы:{" "}
        {(state.poseBytes / 1048576).toFixed(2)} МиБ
        <br />
        Подготовка: {state.buildMs.toFixed(1)} мс · Последний снимок:{" "}
        {state.sampleMs.toFixed(2)} мс
        {state.replayChannels > 0 && (
          <>
            <br />
            Воспроизведение: {state.replayChannels} каналов ·{" "}
            {(state.replayPoseBytes / 1048576).toFixed(2)} МиБ данных поз
          </>
        )}
        {state.optimizedModelBytes > 0 && (
          <>
            <br />
            Сокращённая модель:{" "}
            {(state.optimizedModelBytes / 1048576).toFixed(2)} МиБ · Удалено
            скрытых геометрий: {state.removedGeometry}
          </>
        )}
      </p>
      <p className={styles.muted}>
        Запись и воспроизведение отпускают управление чатом. После записи отойди
        в сторону: фантом повторит движения в исходном месте. Запись хранится в
        памяти и очищается при загрузке сохранения или смене интерьера/мира.
        Подключение к серверу не требуется. Завершённые и остановленные записи
        автоматически сохраняются в папку DreamsleevePhantoms рядом с логом
        SKSE. Файлы остаются после очистки записи и выхода из игры. Первое лицо
        поддерживается. Для сравнения выбери сценарий и частоту существующей
        записи, загрузи архив в том же интерьере или мире, затем воспроизведи
        разные варианты поз и модели. Архив выбирается по сценарию и частоте;
        менять их для уже загруженного фрагмента не нужно. Новая запись для
        сравнения не требуется.
      </p>
    </fieldset>
  );
}
