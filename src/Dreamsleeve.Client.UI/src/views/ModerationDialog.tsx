import { useState, type FormEvent } from "react";
import type { Chat, ChatState } from "../state/chat";
import { TERMS, type ModerationAction } from "../state/moderator";
import { useDialog } from "../features/useDialog";
import { Select } from "./Select";
import workspace from "../styles/Workspace.module.css";
import styles from "../styles/Moderation.module.css";
const titles: Record<ModerationAction, string> = {
  mute: "Мут",
  ban: "Бан",
  kick: "Закрыть сессию",
  clear: "Удалить метки",
};
const confirms: Record<ModerationAction, string> = {
  mute: "Замутить",
  ban: "Забанить",
  kick: "Закрыть сессию",
  clear: "Удалить",
};
// "Другой срок": a number of minutes, hours or days.
const CUSTOM = String(TERMS.length);
const units = [
  { value: "1", label: "минут" },
  { value: "60", label: "часов" },
  { value: "1440", label: "дней" },
];
// A moderator's action on one player. A mute or a ban needs a term and a
// short reason and may remove the player's marks too (off by default); a
// kick needs a reason; removing marks asks which kinds.
export function ModerationDialog({
  chat,
  state: s,
}: {
  chat: Chat;
  state: ChatState;
}) {
  const dialog = s.moderation!;
  const ref = useDialog();
  const [choice, setChoice] = useState("0");
  const [amount, setAmount] = useState("");
  const [unit, setUnit] = useState("60");
  const [reason, setReason] = useState("");
  const [notes, setNotes] = useState(dialog.notes);
  const [deaths, setDeaths] = useState(dialog.deaths);
  const [devices, setDevices] = useState(false);
  const sanction = dialog.action === "mute" || dialog.action === "ban";
  const waiting = dialog.request !== undefined;
  const custom = choice === CUSTOM;
  const count = Number(amount);
  const minutes = custom ? count * Number(unit) : TERMS[Number(choice)].minutes;
  const ready =
    dialog.action === "clear"
      ? notes || deaths
      : reason.trim() !== "" &&
        (!custom || (Number.isInteger(count) && count > 0));
  function submit(e: FormEvent) {
    e.preventDefault();
    if (!ready) return;
    chat.moderator.submitDialog({ minutes, reason, notes, deaths, devices });
  }
  return (
    <div
      className={workspace.backdrop}
      data-theme={s.settings.theme}
      data-part="moderation"
    >
      <section
        ref={ref}
        className={styles.dialog}
        role="dialog"
        aria-modal="true"
        aria-label={`${titles[dialog.action]}: ${dialog.name}`}
      >
        <form className={styles.form} onSubmit={submit}>
          <h3>
            {titles[dialog.action]}: <b>{dialog.name}</b>
          </h3>
          {sanction && (
            <label className={styles.field}>
              Срок
              <Select
                label="Срок"
                value={choice}
                options={[
                  ...TERMS.map((t, i) => ({
                    value: String(i),
                    label: t.label,
                  })),
                  { value: CUSTOM, label: "Другой срок…" },
                ]}
                onChange={setChoice}
              />
            </label>
          )}
          {sanction && custom && (
            <div className={styles.custom}>
              <input
                aria-label="Длительность"
                inputMode="numeric"
                value={amount}
                onChange={(e) => setAmount(e.target.value.replace(/\D/g, ""))}
              />
              <Select
                label="Единица срока"
                value={unit}
                options={units}
                onChange={setUnit}
              />
            </div>
          )}
          {dialog.action !== "clear" && (
            <label className={styles.field}>
              Причина
              <input
                aria-label="Причина"
                placeholder="Коротко, одной строкой — её увидит игрок"
                value={reason}
                onChange={(e) => setReason(e.target.value)}
              />
            </label>
          )}
          {dialog.action === "ban" && (
            <label className={styles.field}>
              <span>
                <input
                  type="checkbox"
                  checked={devices}
                  onChange={(e) => setDevices(e.target.checked)}
                />{" "}
                Заблокировать и устройство игрока
              </span>
              <small>
                С его компьютера не войти и не завести новый аккаунт, пока
                действует бан.
              </small>
            </label>
          )}
          {dialog.action !== "kick" && (
            <fieldset className={styles.removals}>
              <legend>
                {sanction
                  ? "Заодно удалить метки игрока"
                  : "Какие метки удалить"}
              </legend>
              <label>
                <input
                  type="checkbox"
                  checked={notes}
                  onChange={(e) => setNotes(e.target.checked)}
                />
                Все надписи
              </label>
              <label>
                <input
                  type="checkbox"
                  checked={deaths}
                  onChange={(e) => setDeaths(e.target.checked)}
                />
                Все места смерти
              </label>
            </fieldset>
          )}
          {dialog.error && (
            <p className={styles.error} role="alert">
              {dialog.error}
            </p>
          )}
          <div className={styles.actions}>
            <button type="submit" disabled={!ready || waiting || !s.connected}>
              {waiting ? "Ожидание сервера…" : confirms[dialog.action]}
            </button>
            <button type="button" onClick={() => chat.moderator.closeDialog()}>
              Отмена
            </button>
          </div>
        </form>
      </section>
    </div>
  );
}
