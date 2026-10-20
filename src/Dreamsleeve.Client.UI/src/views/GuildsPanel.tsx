import { useState } from "react";
import type { Chat, ChatState } from "../state/chat";
import type {
  Guild,
  GuildLimits,
  GuildMember,
  GuildRole,
  GuildTitle,
  Player,
} from "../bridge/types";
import {
  mayInvite,
  memberOf,
  outranks,
  roleLabels,
  roleOf,
  titleName,
} from "../state/guilds";
import { term } from "../state/moderation";
import { nameMatches, playerName } from "../state/names";
import { Select } from "./Select";
import { GuildCatalog } from "./GuildCatalog";
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
// Letters and digits in any script, spaces between words; the server checks
// the name and its dictionary anyway, the page only spares a doomed request.
// The server turns runs of spaces into one, so they count once.
const LETTERS_DIGITS_AND_SPACES = /^[\p{L}\p{N} ]+$/u;
function nameProblem(name: string, minimum: number, maximum: number) {
  const value = name.trim().replace(/ {2,}/g, " ");
  const length = [...value].length;
  if (!value) return "";
  if (!LETTERS_DIGITS_AND_SPACES.test(value))
    return "Только буквы, цифры и пробелы, без знаков";
  if (minimum && length < minimum) return `Не короче ${minimum} символов`;
  if (maximum && length > maximum) return `Не длиннее ${maximum} символов`;
  return "";
}
// Players a search shows at once: the online list may hold hundreds.
const INVITE_RESULTS = 8;
// An invitation by name: the player types, the page shows a few online
// players outside the guild whose names match, names starting with the query
// first. The server checks the rest.
function InviteSearch({
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
  const [query, setQuery] = useState("");
  const normalized = query.trim().toLocaleLowerCase("ru");
  const name = (p: Player) => playerName(p, s.settings);
  const found = normalized
    ? s.players
        .filter(
          (p) =>
            p.id !== s.selfId &&
            !memberOf(guild, p.id) &&
            nameMatches(p, s.settings, normalized),
        )
        .map((p) => ({
          player: p,
          label: name(p),
          prefix: name(p).toLocaleLowerCase("ru").startsWith(normalized),
        }))
        .sort(
          (a, b) =>
            Number(b.prefix) - Number(a.prefix) ||
            a.label.localeCompare(b.label, "ru"),
        )
    : [];
  const shown = found.slice(0, INVITE_RESULTS);
  return (
    <div className={styles.inviteSearch}>
      <input
        aria-label="Кого пригласить"
        placeholder="Имя игрока онлайн…"
        value={query}
        onChange={(e) => setQuery(e.target.value)}
      />
      {normalized && !found.length && (
        <p className={styles.muted}>
          Среди игроков онлайн вне гильдии такого имени нет.
        </p>
      )}
      {shown.length > 0 && (
        <ul className={styles.members} aria-label="Найденные игроки">
          {shown.map(({ player, label }) => (
            <li key={player.id}>
              <span>{label}</span>
              <span className={styles.rowActions}>
                <button
                  disabled={busy}
                  onClick={() => {
                    chat.guilds.invite(guild.id, player.id, label);
                    setQuery("");
                  }}
                >
                  Пригласить
                </button>
              </span>
            </li>
          ))}
        </ul>
      )}
      {found.length > shown.length && (
        <p className={styles.muted}>
          И ещё {found.length - shown.length} — уточните имя.
        </p>
      )}
    </div>
  );
}
// What the master tells about the guild: everyone in it reads, the master
// edits in place. The server counts characters and checks the word list.
function GuildAbout({
  chat,
  guild,
  limits,
  master,
  busy,
}: {
  chat: Chat;
  guild: Guild;
  limits: GuildLimits;
  master: boolean;
  busy: boolean;
}) {
  const [draft, setDraft] = useState<string | null>(null);
  if (draft === null)
    return (
      <section className={styles.section} aria-label="О гильдии">
        <h4>О гильдии</h4>
        {guild.info ? (
          <p className={styles.about}>{guild.info}</p>
        ) : (
          <p className={styles.muted}>
            {master
              ? "Расскажите, чем живёт гильдия: это увидят участники и, позже, игроки в каталоге."
              : "Глава пока ничего не рассказал о гильдии."}
          </p>
        )}
        {master && (
          <div className={styles.actions}>
            <button disabled={busy} onClick={() => setDraft(guild.info)}>
              Изменить
            </button>
          </div>
        )}
      </section>
    );
  const length = [...draft].length;
  const over = limits.infoMax > 0 && length > limits.infoMax;
  return (
    <section className={styles.section} aria-label="О гильдии">
      <h4>О гильдии</h4>
      <form
        className={styles.aboutForm}
        onSubmit={(e) => {
          e.preventDefault();
          if (busy || over) return;
          chat.guilds.setInfo(guild.id, draft);
          setDraft(null);
        }}
      >
        <textarea
          aria-label="Информация о гильдии"
          rows={5}
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
        />
        <small className={over ? styles.error : styles.muted}>
          {length} / {limits.infoMax}
        </small>
        <div className={styles.actions}>
          <button type="submit" disabled={busy || over}>
            Сохранить
          </button>
          <button type="button" onClick={() => setDraft(null)}>
            Отмена
          </button>
        </div>
      </form>
    </section>
  );
}
const NO_TITLE = "";
// The master's choice of a member's custom role; one role at most.
function TitleChoice({
  chat,
  guild,
  member,
  busy,
}: {
  chat: Chat;
  guild: Guild;
  member: GuildMember;
  busy: boolean;
}) {
  return (
    <Select
      label={`Роль: ${member.name}`}
      variant="plain"
      value={member.title ?? NO_TITLE}
      disabled={busy}
      options={[
        { value: NO_TITLE, label: "без роли" },
        ...guild.titles.map((t) => ({ value: t.id, label: t.name })),
      ]}
      onChange={(value) =>
        chat.guilds.setTitle(
          guild.id,
          member.id,
          value === NO_TITLE ? undefined : value,
          member.name,
        )
      }
    />
  );
}
// One custom role in the master's list: rename in place or delete.
function TitleRow({
  chat,
  guild,
  title,
  limits,
  busy,
}: {
  chat: Chat;
  guild: Guild;
  title: GuildTitle;
  limits: GuildLimits;
  busy: boolean;
}) {
  const [name, setName] = useState<string | null>(null);
  const holders = guild.members.filter((m) => m.title === title.id).length;
  if (name === null)
    return (
      <li>
        <span className={styles.title}>{title.name}</span>
        <small className={styles.muted}>
          {holders ? `у ${holders}` : "ни у кого"}
        </small>
        <span className={styles.rowActions}>
          <button disabled={busy} onClick={() => setName(title.name)}>
            Переименовать
          </button>
          <Confirm
            label="Удалить"
            confirm="Удалить роль"
            disabled={busy}
            onConfirm={() =>
              chat.guilds.deleteTitle(guild.id, title.id, title.name)
            }
          />
        </span>
      </li>
    );
  const problem = nameProblem(name, limits.nameMin, limits.nameMax);
  return (
    <li>
      <form
        className={styles.create}
        onSubmit={(e) => {
          e.preventDefault();
          if (busy || problem || !name.trim()) return;
          chat.guilds.renameTitle(guild.id, title.id, name, title.name);
          setName(null);
        }}
      >
        <input
          aria-label={`Новое название роли ${title.name}`}
          value={name}
          maxLength={limits.nameMax || 64}
          onChange={(e) => setName(e.target.value)}
        />
        <button type="submit" disabled={busy || !!problem || !name.trim()}>
          Сохранить
        </button>
        <button type="button" onClick={() => setName(null)}>
          Отмена
        </button>
      </form>
      {problem && <p className={styles.error}>{problem}</p>}
    </li>
  );
}
// The master's custom roles: cosmetic names such as «Казначей», no rights.
function GuildTitles({
  chat,
  guild,
  limits,
  busy,
}: {
  chat: Chat;
  guild: Guild;
  limits: GuildLimits;
  busy: boolean;
}) {
  const [name, setName] = useState("");
  const full = guild.titles.length >= limits.titles;
  const problem = nameProblem(name, limits.nameMin, limits.nameMax);
  return (
    <section className={styles.section} aria-label="Роли гильдии">
      <h4>
        Роли гильдии{" "}
        <small className={styles.muted}>
          {guild.titles.length} / {limits.titles}
        </small>
      </h4>
      {guild.titles.length > 0 && (
        <ul className={styles.members}>
          {guild.titles.map((t) => (
            <TitleRow
              key={t.id}
              chat={chat}
              guild={guild}
              title={t}
              limits={limits}
              busy={busy}
            />
          ))}
        </ul>
      )}
      {full ? (
        <p className={styles.muted}>
          {guild.titles.length}/{limits.titles} — новых ролей не создать
        </p>
      ) : (
        <form
          className={styles.create}
          onSubmit={(e) => {
            e.preventDefault();
            if (busy || problem || !name.trim()) return;
            chat.guilds.createTitle(guild.id, name);
            setName("");
          }}
        >
          <input
            aria-label="Название новой роли"
            placeholder="Например, Казначей"
            value={name}
            maxLength={limits.nameMax || 64}
            onChange={(e) => setName(e.target.value)}
          />
          <button type="submit" disabled={busy || !!problem || !name.trim()}>
            Создать роль
          </button>
        </form>
      )}
      {problem && <p className={styles.error}>{problem}</p>}
      <p className={styles.muted}>
        Роль — только название: прав она не даёт. Буквы, цифры и пробелы,{" "}
        {limits.nameMin}–{limits.nameMax} символов, без повторов в гильдии. У
        участника одна роль; удалённая роль снимается со всех.
      </p>
    </section>
  );
}
// Requests to join from the catalog, for the master and officers to answer;
// the master also decides whether the catalog shows the guild.
function JoinRequests({
  chat,
  guild,
  master,
  busy,
}: {
  chat: Chat;
  guild: Guild;
  master: boolean;
  busy: boolean;
}) {
  return (
    <section className={styles.section} aria-label="Заявки на вступление">
      <h4>
        Заявки на вступление{" "}
        <small className={styles.muted}>{guild.joinRequests.length}</small>
      </h4>
      {master && (
        <label className={styles.check}>
          <input
            type="checkbox"
            checked={guild.listed}
            disabled={busy}
            onChange={(e) => chat.guilds.setListed(guild.id, e.target.checked)}
          />
          Показывать гильдию в каталоге
        </label>
      )}
      {guild.joinRequests.length === 0 ? (
        <p className={styles.muted}>
          {guild.listed
            ? "Заявок нет. Игроки подают их из каталога гильдий."
            : "Гильдия скрыта из каталога: новых заявок не будет."}
        </p>
      ) : (
        <ul className={styles.members}>
          {guild.joinRequests.map((r) => (
            <li key={r.id}>
              <button onClick={() => chat.open("profile", r.id)}>
                {r.name}
              </button>
              <small className={styles.muted}>ждёт {term(r.expires)}</small>
              <span className={styles.rowActions}>
                <button
                  disabled={busy}
                  onClick={() =>
                    chat.guilds.answerRequest(guild.id, r.id, true, r.name)
                  }
                >
                  Принять
                </button>
                <button
                  disabled={busy}
                  onClick={() =>
                    chat.guilds.answerRequest(guild.id, r.id, false, r.name)
                  }
                >
                  Отклонить
                </button>
              </span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
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
  const invitable = s.players.some(
    (p) => p.id !== s.selfId && !memberOf(guild, p.id),
  );
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
      <GuildAbout
        chat={chat}
        guild={guild}
        limits={s.guildLimits}
        master={own === "master"}
        busy={busy}
      />
      {mayInvite(own) && (
        <JoinRequests
          chat={chat}
          guild={guild}
          master={own === "master"}
          busy={busy}
        />
      )}
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
            {own === "master" && guild.titles.length > 0 ? (
              <TitleChoice chat={chat} guild={guild} member={m} busy={busy} />
            ) : (
              titleName(guild, m.title) && (
                <span className={styles.title}>
                  {titleName(guild, m.title)}
                </span>
              )
            )}
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
      {own === "master" && (
        <GuildTitles
          chat={chat}
          guild={guild}
          limits={s.guildLimits}
          busy={busy}
        />
      )}
      {mayInvite(own) &&
        (limit > 0 && count >= limit ? (
          <p className={styles.muted}>
            {count}/{limit} — приглашения недоступны
          </p>
        ) : !invitable ? (
          <p className={styles.muted}>
            Пригласить некого: все игроки онлайн уже в гильдии.
          </p>
        ) : (
          <InviteSearch chat={chat} state={s} guild={guild} busy={busy} />
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
  const [view, setView] = useState<"mine" | "catalog">("mine");
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
  const views = (
    <div className={styles.tabs} role="group" aria-label="Раздел гильдий">
      <button aria-pressed={view === "mine"} onClick={() => setView("mine")}>
        Мои гильдии
      </button>
      <button
        aria-pressed={view === "catalog"}
        onClick={() => setView("catalog")}
      >
        Каталог
      </button>
    </div>
  );
  if (view === "catalog")
    return (
      <div className={styles.guilds}>
        {views}
        <GuildCatalog chat={chat} state={s} />
      </div>
    );
  return (
    <div className={styles.guilds}>
      {views}
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
      {s.applications.length > 0 && (
        <section className={styles.section} aria-label="Мои заявки">
          <h3>
            Мои заявки{" "}
            <small className={styles.muted}>
              {s.applications.length} / {limits.applications}
            </small>
          </h3>
          <ul className={styles.members}>
            {s.applications.map((a) => (
              <li key={a.guildId}>
                <b>«{a.guildName}»</b>
                <small className={styles.muted}>
                  ждёт ответа {term(a.expires)}
                </small>
                <span className={styles.rowActions}>
                  <button
                    disabled={busy}
                    onClick={() => guilds.withdraw(a.guildId, a.guildName)}
                  >
                    Отозвать
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
            : `Буквы, цифры и пробелы, ${limits.nameMin}–${limits.nameMax} символов. Название не должно совпадать с чужим без учёта регистра и проходит словарь сервера; переименовать гильдию нельзя. Вы станете её главой.`}
        </p>
      </section>
    </div>
  );
}
