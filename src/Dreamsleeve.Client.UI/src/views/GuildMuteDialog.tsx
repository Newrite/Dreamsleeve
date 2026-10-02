import { useState, type FormEvent } from "react";
import type { Chat, ChatState } from "../state/chat";
import { TERMS } from "../state/moderator";
import { useDialog } from "../features/useDialog";
import { Select } from "./Select";
import workspace from "../styles/Workspace.module.css";
import styles from "../styles/Moderation.module.css";
// A guild mute by the master or an officer: a term and a short reason the
// member sees. The member keeps reading the guild's chat.
export function GuildMuteDialog({
  chat,
  state: s,
}: {
  chat: Chat;
  state: ChatState;
}) {
  const dialog = s.guildMute!;
  const ref = useDialog();
  const [choice, setChoice] = useState("0");
  const [reason, setReason] = useState("");
  const waiting = dialog.request !== undefined;
  const guild = s.guilds.find((g) => g.id === dialog.guildId);
  function submit(e: FormEvent) {
    e.preventDefault();
    if (!reason.trim()) return;
    chat.guilds.submitMute(TERMS[Number(choice)].minutes, reason);
  }
  return (
    <div
      className={workspace.backdrop}
      data-theme={s.settings.theme}
      data-part="guild-mute"
    >
      <section
        ref={ref}
        className={styles.dialog}
        role="dialog"
        aria-modal="true"
        aria-label={`Мут в гильдии: ${dialog.name}`}
      >
        <form className={styles.form} onSubmit={submit}>
          <h3>
            Мут в гильдии {guild ? `«${guild.name}»` : ""}: <b>{dialog.name}</b>
          </h3>
          <label className={styles.field}>
            Срок
            <Select
              label="Срок"
              value={choice}
              options={TERMS.map((t, i) => ({
                value: String(i),
                label: t.label,
              }))}
              onChange={setChoice}
            />
          </label>
          <label className={styles.field}>
            Причина
            <input
              aria-label="Причина"
              placeholder="Коротко, одной строкой — её увидит участник"
              value={reason}
              onChange={(e) => setReason(e.target.value)}
            />
          </label>
          {dialog.error && (
            <p className={styles.error} role="alert">
              {dialog.error}
            </p>
          )}
          <div className={styles.actions}>
            <button
              type="submit"
              disabled={!reason.trim() || waiting || !s.connected}
            >
              {waiting ? "Ожидание сервера…" : "Замутить"}
            </button>
            <button type="button" onClick={() => chat.guilds.closeMute()}>
              Отмена
            </button>
          </div>
        </form>
      </section>
    </div>
  );
}
