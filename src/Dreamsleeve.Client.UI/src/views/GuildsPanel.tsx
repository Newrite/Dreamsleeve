import { useState } from "react";
import type { Chat, ChatState } from "../state/chat";
import type { Guild, GuildMember, GuildRole } from "../bridge/types";
import {
  mayInvite,
  memberOf,
  outranks,
  roleLabels,
  roleOf,
} from "../state/guilds";
import { term } from "../state/moderation";
import { playerName } from "../state/names";
import { Select } from "./Select";
import styles from "../styles/Guilds.module.css";
const createdFormat = new Intl.DateTimeFormat("ru-RU", {
  day: "numeric",
  month: "long",
  year: "numeric",
});
const order: Record<GuildRole, number> = { master: 0, officer: 1, member: 2 };
// The master first, then officers, the online before the offline, by name.
const ordered = (members: GuildMember[]) =>
  [...members].sort(
    (a, b) =>
      order[a.role] - order[b.role] ||
      Number(b.online) - Number(a.online) ||
      a.name.localeCompare(b.name, "ru"),
  );
// Letters and digits in any script; the server checks the name and its
// dictionary anyway, the page only spares a doomed request.
const LETTERS_AND_DIGITS = /^[\p{L}\p{N}]+$/u;
function nameProblem(name: string, minimum: number, maximum: number) {
  const value = name.trim();
  const length = [...value].length;
  if (!value) return "";
  if (!LETTERS_AND_DIGITS.test(value))
    return "Только буквы и цифры, без пробелов и знаков";
  if (minimum && length < minimum) return `Не короче ${minimum} символов`;
  if (maximum && length > maximum) return `Не длиннее ${maximum} символов`;
  return "";
}
// A button that asks once more before an action that cannot be taken back.
function Confirm({
  label,
  confirm,
  disabled,
  onConfirm,
}: {
  label: string;
  confirm: string;
  disabled: boolean;
  onConfirm: () => void;
}) {
  const [asking, setAsking] = useState(false);
  if (!asking)
    return (
      <button disabled={disabled} onClick={() => setAsking(true)}>
        {label}
      </button>
    );
  return (
    <>
      <button
        className={styles.danger}
        disabled={disabled}
        onClick={() => {
          setAsking(false);
          onConfirm();
        }}
      >
        {confirm}
      </button>
      <button onClick={() => setAsking(false)}>Отмена</button>
    </>
  );
}
// What the player's role may do to one member: a rank above theirs mutes and
// excludes, the master also appoints officers and hands the guild over.
function MemberActions({
  chat,
  guild,
  member,
  own,
  busy,
}: {
  chat: Chat;
  guild: Guild;
  member: GuildMember;
  own: GuildRole | undefined;
  busy: boolean;
}) {
  const guilds = chat.guilds;
  const disciplines = outranks(own, member.role);
  const masters = own === "master" && member.role !== "master";
  if (!disciplines && !masters) return null;
  return (
    <span className={styles.rowActions}>
      {masters &&
        (member.role === "member" ? (
          <button
            disabled={busy}
            onClick={() =>
              guilds.setRole(guild.id, member.id, "officer", member.name)
            }
          >
            Сделать офицером
          </button>
        ) : (
          <button
            disabled={busy}
            onClick={() =>
              guilds.setRole(guild.id, member.id, "member", member.name)
            }
          >
            Снять офицера
          </button>
        ))}
      {disciplines &&
        (member.mute ? (
          <button
            disabled={busy}
            onClick={() => guilds.unmute(guild.id, member.id, member.name)}
          >
            Снять мут
          </button>
        ) : (
          <button
            disabled={busy}
            onClick={() => guilds.openMute(guild.id, member.id, member.name)}
          >
            Мут…
          </button>
        ))}
      {disciplines && (
        <Confirm
          label="Исключить"
          confirm="Исключить из гильдии"
          disabled={busy}
          onConfirm={() => guilds.exclude(guild.id, member.id, member.name)}
        />
      )}
      {masters && (
        <Confirm
          label="Передать главу"
          confirm="Передать роль главы"
          disabled={busy}
          onConfirm={() => guilds.transfer(guild.id, member.id, member.name)}
        />
      )}
    </span>
  );
}
function GuildCard({
  chat,
  state: s,
  guild,
  busy,
}: {
  chat: Chat;
  state: ChatState;
  guild: Guild;
  busy: boolean;
}) {
  const guilds = chat.guilds;
  const own = memberOf(guild, s.selfId)?.role;
  const limit = s.guildLimits.members;
  const count = guild.members.length;
  // An online player outside the guild; the server checks they are still online.
  const candidates = s.players.filter(
    (p) => p.id !== s.selfId && !memberOf(guild, p.id),
  );
  const [invitee, setInvitee] = useState("");
  const chosen = candidates.find((p) => p.id === invitee) ?? candidates[0];
  return (
    <div className={styles.section}>
      <p className={styles.facts}>
        <span>
          Ваша роль: <b>{own ? roleLabels[own] : "—"}</b>
        </span>
        <span>
          Участников:{" "}
          <b>
            {count} / {limit}
          </b>
        </span>
        <span>
          Создана: <b>{createdFormat.format(guild.createdAt)}</b>
        </span>
      </p>
      <ul
        className={styles.members}
        aria-label={`Участники гильдии ${guild.name}`}
      >
        {ordered(guild.members).map((m) => (
          <li key={m.id} data-online={m.online} data-role={m.role}>
            <button onClick={() => chat.open("profile", m.id)}>
              {m.name}
              {m.id === s.selfId ? " (вы)" : ""}
            </button>
            <span className={styles.role}>{roleLabels[m.role]}</span>
            <small className={styles.muted}>
              {m.online ? "в сети" : "не в сети"}
            </small>
            {m.mute && (
              <span className={styles.mute}>
                мут {term(m.mute.until)}: {m.mute.reason}
              </span>
            )}
            {m.id !== s.selfId && (
              <MemberActions
                chat={chat}
                guild={guild}
                member={m}
                own={own}
                busy={busy}
              />
            )}
          </li>
        ))}
      </ul>
      {mayInvite(own) &&
        (limit > 0 && count >= limit ? (
          <p className={styles.muted}>
            {count}/{limit} — приглашения недоступны
          </p>
        ) : !chosen ? (
          <p className={styles.muted}>
            Пригласить некого: все игроки онлайн уже в гильдии.
          </p>
        ) : (
          <div className={styles.invite}>
            <Select
              label="Кого пригласить"
              value={chosen.id}
              options={candidates.map((p) => ({
                value: p.id,
                label: playerName(p, s.settings),
              }))}
              onChange={setInvitee}
            />
            <button
              disabled={busy}
              onClick={() =>
                guilds.invite(
                  guild.id,
                  chosen.id,
                  playerName(chosen, s.settings),
                )
              }
            >
              Пригласить
            </button>
          </div>
        ))}
      <div className={styles.actions}>
        {own === "master" ? (
          <>
            <p className={styles.muted}>
              Чтобы выйти, передайте роль главы другому участнику или распустите
              гильдию.
            </p>
            <Confirm
              label="Распустить гильдию"
              confirm="Распустить навсегда"
              disabled={busy}
              onConfirm={() => guilds.disband(guild.id)}
            />
          </>
        ) : (
          <Confirm
            label="Выйти из гильдии"
            confirm="Точно выйти"
            disabled={busy}
            onConfirm={() => guilds.leave(guild.id)}
          />
        )}
      </div>
    </div>
  );
}
// In a profile: the guilds the player shares with this one, and an
// invitation to each guild where the player may invite and there is room.
export function ProfileGuilds({
  chat,
  state: s,
  playerId,
  name,
}: {
  chat: Chat;
  state: ChatState;
  playerId: string;
  name: string;
}) {
  const online = s.players.some((p) => p.id === playerId);
  const limit = s.guildLimits.members;
  const shared = s.guilds.filter((g) => memberOf(g, playerId));
  const invitable = online
    ? s.guilds.filter(
        (g) =>
          mayInvite(roleOf(g, s.selfId)) &&
          !memberOf(g, playerId) &&
          !(limit > 0 && g.members.length >= limit),
      )
    : [];
  if (!shared.length && !invitable.length) return null;
  const busy = !s.connected || Object.keys(s.guildRequests).length > 0;
  return (
    <div className={styles.invite} role="group" aria-label="Гильдии игрока">
      {shared.length > 0 && (
        <small className={styles.muted}>
          В ваших гильдиях:{" "}
          {shared
            .map((g) => `«${g.name}» (${roleLabels[roleOf(g, playerId)!]})`)
            .join(", ")}
        </small>
      )}
      {invitable.map((g) => (
        <button
          key={g.id}
          disabled={busy}
          onClick={() => chat.guilds.invite(g.id, playerId, name)}
        >
          Пригласить в «{g.name}»
        </button>
      ))}
    </div>
  );
}
// The guilds tab: invitations, the player's guilds and a new one. Buttons
// follow the player's role; the server decides every request.
export function GuildsPanel({
  chat,
  state: s,
}: {
  chat: Chat;
  state: ChatState;
}) {
  const guilds = chat.guilds;
  const [name, setName] = useState("");
  if (!s.guildsKnown)
    return (
      <p className={styles.muted}>
        {s.connected
          ? "Загрузка гильдий…"
          : "Гильдии доступны после подключения к серверу."}
      </p>
    );
  const busy = !s.connected || Object.keys(s.guildRequests).length > 0;
  const limits = s.guildLimits;
  const full = limits.perPlayer > 0 && s.guilds.length >= limits.perPlayer;
  const problem = nameProblem(name, limits.nameMin, limits.nameMax);
  const selected = s.guilds.find((g) => g.id === s.selectedGuild);
  return (
    <div className={styles.guilds}>
      {s.invites.length > 0 && (
        <section className={styles.section} aria-label="Приглашения">
          <h3>Приглашения</h3>
          <p className={styles.warning}>
            Участники гильдии видят ваше настоящее имя, даже если вы скрываете
            его от остальных игроков.
          </p>
          <ul className={styles.members}>
            {s.invites.map((invite) => (
              <li key={invite.guildId}>
                <b>«{invite.guildName}»</b>
                <small className={styles.muted}>
                  {invite.inviter ? `от ${invite.inviter} · ` : ""}действует{" "}
                  {term(invite.expires)}
                </small>
                <span className={styles.rowActions}>
                  <button
                    disabled={busy || full}
                    title={full ? "Достигнут предел гильдий" : undefined}
                    onClick={() => guilds.answer(invite.guildId, true)}
                  >
                    Вступить
                  </button>
                  <button
                    disabled={busy}
                    onClick={() => guilds.answer(invite.guildId, false)}
                  >
                    Отклонить
                  </button>
                </span>
              </li>
            ))}
          </ul>
        </section>
      )}
      <section className={styles.section} aria-label="Мои гильдии">
        <h3>
          Мои гильдии{" "}
          <small className={styles.muted}>
            {s.guilds.length} / {limits.perPlayer}
          </small>
        </h3>
        {s.guilds.length === 0 ? (
          <p className={styles.muted}>Вы пока не состоите в гильдиях.</p>
        ) : (
          <div className={styles.tabs} role="group" aria-label="Выбор гильдии">
            {s.guilds.map((g) => (
              <button
                key={g.id}
                aria-pressed={g.id === s.selectedGuild}
                onClick={() => guilds.select(g.id)}
              >
                {g.name}
              </button>
            ))}
          </div>
        )}
        {selected && (
          <GuildCard
            key={selected.id}
            chat={chat}
            state={s}
            guild={selected}
            busy={busy}
          />
        )}
      </section>
      <section className={styles.section} aria-label="Новая гильдия">
        <h3>Новая гильдия</h3>
        <form
          className={styles.create}
          onSubmit={(e) => {
            e.preventDefault();
            if (busy || full || problem || !name.trim()) return;
            guilds.create(name);
            setName("");
          }}
        >
          <input
            aria-label="Название гильдии"
            placeholder="Название"
            value={name}
            maxLength={limits.nameMax || 64}
            onChange={(e) => setName(e.target.value)}
          />
          <button
            type="submit"
            disabled={busy || full || !!problem || !name.trim()}
          >
            Создать
          </button>
        </form>
        {problem && <p className={styles.error}>{problem}</p>}
        <p className={styles.muted}>
          {full
            ? `Вы уже в ${s.guilds.length} гильдиях — это предел сервера, свои гильдии тоже считаются. Чтобы создать новую или вступить в другую, выйдите из одной.`
            : `Буквы и цифры, ${limits.nameMin}–${limits.nameMax} символов. Название не должно совпадать с чужим без учёта регистра и проходит словарь сервера; переименовать гильдию нельзя. Вы станете её главой.`}
        </p>
      </section>
    </div>
  );
}
