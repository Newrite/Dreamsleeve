import { useState, type FormEvent } from "react";
import type { Chat, ChatState } from "../state/chat";
import {
  accountActions,
  authStatus,
  canCancelSteam,
  canRegister,
  canResetPassword,
  registrationNote,
} from "../state/auth";
import { connectionLabels } from "../state/connection";
import { identityStatus } from "../state/identity";
import { sessionEndText } from "../state/moderation";
import { Select } from "./Select";
import styles from "../styles/Account.module.css";
export function AccountPanel({
  chat,
  state: s,
}: {
  chat: Chat;
  state: ChatState;
}) {
  const [username, setUsername] = useState(
    s.settings.streamerMode ? "" : s.auth.savedUsername,
  );
  const [password, setPassword] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [remember, setRemember] = useState(true);
  const [newName, setNewName] = useState("");
  const [code, setCode] = useState("");
  const [newPassword, setNewPassword] = useState("");
  const [resetSent, setResetSent] = useState(false);
  const [linkCopied, setLinkCopied] = useState(false);
  const self = s.players.find((p) => p.id === s.selfId);
  // Streamer mode keeps even the own names off the screen, like the saved login.
  const current = s.settings.streamerMode ? "" : (self?.displayName ?? "");
  const canRename =
    s.connected &&
    !s.displayName.pending &&
    newName.trim().length > 0 &&
    newName.trim() !== self?.displayName;
  function rename(e: FormEvent) {
    e.preventDefault();
    if (!canRename) return;
    chat.changeDisplayName(newName);
    setNewName("");
  }
  const nameStatus = s.displayName.pending
    ? "Ожидание сервера…"
    : s.displayName.error
      ? s.displayName.error
      : s.displayName.changed
        ? s.settings.streamerMode
          ? "Имя изменено"
          : `Имя изменено на „${s.displayName.changed}“`
        : current
          ? `Сейчас: „${current}“`
          : "";
  const can = accountActions(s.auth, s.connected, {
    username,
    password,
    displayName,
  });
  const status = authStatus(s.auth);
  const steamWaiting = canCancelSteam(s.auth);
  const registration = canRegister(s.auth);
  const note = registrationNote(s.auth);
  function signIn(register: boolean) {
    chat.signIn(username, password, remember, register ? displayName : "");
    // The secret leaves with the command; the form never keeps it.
    setPassword("");
    setResetSent(false);
  }
  const canReset = canResetPassword(s.auth, s.connected, code, newPassword);
  function resetPassword(e: FormEvent) {
    e.preventDefault();
    if (!canReset) return;
    chat.resetPassword(code, newPassword);
    setNewPassword("");
    setCode("");
    setResetSent(true);
  }
  const resetDone =
    resetSent &&
    !s.auth.authenticating &&
    s.auth.failure === "none" &&
    !s.auth.error;
  function submit(e: FormEvent) {
    e.preventDefault();
    if (can.signIn) signIn(false);
  }
  return (
    <div className={styles.account}>
      <h3>Аккаунт</h3>
      <p className={styles.summary}>
        <span>{connectionLabels[s.connectionPhase]}</span>
        <span>
          {s.auth.savedLogin
            ? `Сохранённый вход: ${(!s.settings.streamerMode && s.auth.savedUsername) || "есть"}`
            : "Нет сохранённого входа"}
        </span>
        {s.identity.mode !== "off" && (
          <span aria-label="Скрытое имя" data-part="account-identity">
            {identityStatus(s.identity, s.connectionPhase)}
          </span>
        )}
        {s.identity.error && (
          <span className={styles.error} role="alert">
            {s.identity.error}
          </span>
        )}
        {s.sessionEnd && (
          <span className={styles.error} role="alert" data-part="session-end">
            {sessionEndText(s.sessionEnd)}
          </span>
        )}
      </p>
      {s.routes && (
        <section className={styles.form} data-part="routes">
          <label className={styles.route}>
            Маршрут к серверу
            <Select
              label="Маршрут к серверу"
              value={s.routes.chosen}
              options={[
                { value: "", label: "Автоматически" },
                ...s.routes.routes.map((route) => ({
                  value: route,
                  label: route,
                })),
              ]}
              onChange={chat.chooseRoute}
            />
          </label>
          <p className={styles.muted} role="status" aria-label="Маршрут">
            {s.routes.reached
              ? `Сейчас через: ${s.routes.active}.`
              : `Подключение через: ${s.routes.active}…`}
            {s.routes.chosen === ""
              ? " Если маршрут не отвечает, игра сама пробует следующий."
              : s.routes.chosen !== s.routes.active
                ? " Выбранный маршрут применится при следующем подключении."
                : ""}
          </p>
        </section>
      )}
      {s.connected && (
        <form
          className={styles.form}
          onSubmit={rename}
          aria-busy={s.displayName.pending}
          data-part="display-name"
        >
          <label>
            Отображаемое имя
            <input
              name="newDisplayName"
              maxLength={128}
              value={newName}
              placeholder={current}
              onChange={(e) => setNewName(e.target.value)}
            />
          </label>
          <div className={styles.actions}>
            <button type="submit" disabled={!canRename}>
              Сменить имя
            </button>
          </div>
          <p
            className={styles.status}
            role="status"
            aria-live="polite"
            aria-label="Смена имени"
            data-failure={Boolean(s.displayName.error)}
          >
            {nameStatus}
          </p>
          <p className={styles.muted}>
            Имя пользователя не меняется. Сервер проверяет имя по словарю и
            ограничивает, как часто его можно менять.
            {s.identity.mode !== "off" &&
              " Пока имя скрыто, другие игроки видят псевдоним."}
          </p>
        </form>
      )}
      <form
        className={styles.form}
        onSubmit={submit}
        aria-busy={s.auth.authenticating}
      >
        <label>
          Имя пользователя
          <input
            name="username"
            autoComplete="username"
            maxLength={128}
            value={username}
            onChange={(e) => setUsername(e.target.value)}
          />
        </label>
        <label>
          Пароль
          <input
            type="password"
            name="password"
            autoComplete="current-password"
            maxLength={512}
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
        </label>
        {registration && (
          <label>
            Отображаемое имя (для регистрации)
            <input
              name="displayName"
              maxLength={128}
              value={displayName}
              onChange={(e) => setDisplayName(e.target.value)}
            />
          </label>
        )}
        <label className={styles.check}>
          <span>Запомнить меня</span>
          <input
            type="checkbox"
            checked={remember}
            onChange={(e) => setRemember(e.target.checked)}
          />
        </label>
        <div className={styles.actions}>
          <button
            type="submit"
            className={styles.primary}
            disabled={!can.signIn}
          >
            Войти
          </button>
          {registration && (
            <button
              type="button"
              disabled={!can.register}
              onClick={() => signIn(true)}
            >
              Зарегистрироваться и войти
            </button>
          )}
          <button
            type="button"
            disabled={!can.resume}
            onClick={chat.signInSaved}
          >
            Войти сохранённой сессией
          </button>
        </div>
        {note && (
          <p className={styles.muted} data-part="registration-note">
            {note}
          </p>
        )}
      </form>
      {s.auth.steam && (
        <div className={styles.form} data-part="steam">
          <div className={styles.actions}>
            {steamWaiting ? (
              <>
                <button type="button" onClick={chat.cancelSteam}>
                  Отменить вход через Steam
                </button>
                <button
                  type="button"
                  onClick={() => setLinkCopied(chat.copySteamLink())}
                >
                  Скопировать ссылку
                </button>
              </>
            ) : (
              <button
                type="button"
                disabled={!can.steam}
                onClick={() => {
                  setLinkCopied(false);
                  chat.signInSteam(remember);
                }}
              >
                Войти через Steam
              </button>
            )}
          </div>
          {steamWaiting && s.auth.browserFailed && (
            <p className={styles.error} role="alert" data-part="steam-browser">
              Браузер не открылся. Скопируйте ссылку и откройте её в браузере
              сами.
            </p>
          )}
          {steamWaiting && linkCopied && !s.auth.error && (
            <p className={styles.status} role="status">
              Ссылка скопирована: вставьте её в адресную строку браузера.
            </p>
          )}
          {steamWaiting && s.auth.error && (
            <p className={styles.error} role="alert">
              {s.auth.error}
            </p>
          )}
          <p className={styles.muted}>
            {steamWaiting
              ? "Страница входа Steam открыта в браузере. Если его не видно, он за игрой: переключитесь на него (Alt+Tab), войдите и вернитесь — игра войдёт сама."
              : "Откроется браузер со страницей Steam; пароль Steam вводится только там. После входа вернитесь в игру. «Запомнить меня» сохраняет вход и для Steam."}
          </p>
        </div>
      )}
      <details data-part="password-code">
        <summary>Пароль по коду</summary>
        <form className={styles.form} onSubmit={resetPassword}>
          <label>
            Код от администратора
            <input
              name="setupCode"
              autoComplete="one-time-code"
              maxLength={128}
              value={code}
              onChange={(e) => {
                setCode(e.target.value);
                setResetSent(false);
              }}
            />
          </label>
          <label>
            Новый пароль
            <input
              type="password"
              name="newPassword"
              autoComplete="new-password"
              maxLength={512}
              value={newPassword}
              onChange={(e) => setNewPassword(e.target.value)}
            />
          </label>
          <div className={styles.actions}>
            <button type="submit" disabled={!canReset}>
              Задать пароль
            </button>
          </div>
          {resetDone && (
            <p className={styles.status} role="status">
              Пароль задан. Войдите с именем пользователя и новым паролем.
            </p>
          )}
          <p className={styles.muted}>
            Код выдаёт администратор сервера — для нового аккаунта или сброса
            пароля; он одноразовый. Пароль — от 12 до 128 байт.
          </p>
        </form>
      </details>
      <div className={styles.actions}>
        {s.connected && (
          <button disabled={!can.disconnect} onClick={chat.disconnect}>
            Отключиться
          </button>
        )}
        <button disabled={!can.signOut} onClick={chat.signOut}>
          Выйти
        </button>
        <button disabled={!can.forget} onClick={chat.forgetLogin}>
          Забыть сохранённый вход
        </button>
      </div>
      <p
        className={styles.status}
        role="status"
        aria-live="polite"
        aria-label="Состояние входа"
        data-failure={s.auth.failure !== "none"}
      >
        {status}
      </p>
      <p className={styles.muted}>
        Пароль передаётся приложению один раз и не хранится в интерфейсе.
        Сохранённый вход — токен в диспетчере учётных данных Windows; «Выйти»
        отзывает его на сервере, «Забыть» удаляет только локально.
      </p>
    </div>
  );
}
