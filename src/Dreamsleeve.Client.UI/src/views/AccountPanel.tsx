import { useState, type FormEvent } from "react";
import type { Chat, ChatState } from "../state/chat";
import { accountActions, authStatus } from "../state/auth";
import { connectionLabels } from "../state/connection";
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
  const can = accountActions(s.auth, s.connected, {
    username,
    password,
    displayName,
  });
  const status = authStatus(s.auth);
  function signIn(register: boolean) {
    chat.signIn(username, password, remember, register ? displayName : "");
    // The secret leaves with the command; the form never keeps it.
    setPassword("");
  }
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
      </p>
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
        <label>
          Отображаемое имя (для регистрации)
          <input
            name="displayName"
            maxLength={128}
            value={displayName}
            onChange={(e) => setDisplayName(e.target.value)}
          />
        </label>
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
          <button
            type="button"
            disabled={!can.register}
            onClick={() => signIn(true)}
          >
            Зарегистрироваться и войти
          </button>
          <button
            type="button"
            disabled={!can.resume}
            onClick={chat.signInSaved}
          >
            Войти сохранённой сессией
          </button>
        </div>
      </form>
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
