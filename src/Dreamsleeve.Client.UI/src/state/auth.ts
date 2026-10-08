import type { AuthFailure, AuthOperation, AuthState } from "../bridge/types";
export const failureLabels: Record<Exclude<AuthFailure, "none">, string> = {
  invalidCredentials: "Неверное имя или пароль",
  usernameTaken: "Имя занято",
  invalidRequest: "Некорректный запрос",
  registrationClosed:
    "Регистрация закрыта: аккаунт создаёт администратор сервера",
  busy: "Сервер занят, повторите позже",
  unavailable: "Сервер недоступен",
  invalidResponse: "Некорректный ответ сервера",
  credentialStorage: "Ошибка хранилища учётных данных Windows",
  canceled: "Операция отменена",
  nameNotAllowed: "Имя содержит недопустимые слова",
  banned: "Аккаунт заблокирован",
  registrationSteamOnly: "Регистрация только через Steam",
  addressBanned: "IP-адрес заблокирован",
  deviceBanned: "Устройство заблокировано",
  steamExpired: "Вход через Steam не завершён вовремя",
  unreachable: "Сервер не отвечает",
};
export const operationLabels: Record<Exclude<AuthOperation, "none">, string> = {
  passwordLogin: "Вход по паролю…",
  resume: "Вход сохранённой сессией…",
  signOut: "Выход…",
  forgetSavedLogin: "Удаление сохранённого входа…",
  resetPassword: "Сброс пароля…",
  steamLogin: "Вход через Steam: завершите вход в открывшемся браузере…",
};
export const idleAuth: AuthState = {
  authenticating: false,
  operation: "none",
  failure: "none",
  error: "",
  savedLogin: false,
  savedUsername: "",
  registration: "unknown",
  steam: false,
  browserFailed: false,
};
// One human-readable line: the operation in progress, or the failure with the
// raw host text when present. Empty when there is nothing to report.
export function authStatus(auth: AuthState): string {
  if (auth.authenticating)
    return auth.operation === "none"
      ? "Авторизация…"
      : operationLabels[auth.operation];
  const error = auth.error.trim();
  if (auth.failure === "none") return error;
  const label = failureLabels[auth.failure];
  return error ? `${label}: ${error}` : label;
}
// The host keeps the last operation after it ends, so a finished sign-in that
// left a failure is told apart from a failed reset, sign-out or forget.
const signInOperations: ReadonlySet<AuthOperation> = new Set([
  "passwordLogin",
  "resume",
  "steamLogin",
]);
export function signInFailed(auth: AuthState): boolean {
  return (
    !auth.authenticating &&
    signInOperations.has(auth.operation) &&
    (auth.failure !== "none" || auth.error.trim() !== "")
  );
}
export interface AccountForm {
  username: string;
  password: string;
  displayName: string;
}
// Registration in the game is offered unless the server said it is closed.
export function canRegister(auth: AuthState) {
  return auth.registration === "open" || auth.registration === "unknown";
}
// Why there is no registration form, in the server's words.
export function registrationNote(auth: AuthState): string {
  if (auth.registration === "manual")
    return "Регистрация закрыта: аккаунт создаёт администратор сервера и выдаёт код для пароля.";
  if (auth.registration === "steam")
    return "Новые аккаунты создаются только входом через Steam.";
  return "";
}
export function canCancelSteam(auth: AuthState) {
  return auth.authenticating && auth.operation === "steamLogin";
}
// Which account buttons may act right now; the view only mirrors this.
export function accountActions(
  auth: AuthState,
  connected: boolean,
  form: AccountForm,
) {
  const idle = !auth.authenticating;
  const credentials =
    form.username.trim().length > 0 && form.password.length > 0;
  return {
    signIn: idle && credentials,
    register:
      idle &&
      credentials &&
      form.displayName.trim().length > 0 &&
      canRegister(auth),
    resume: idle && auth.savedLogin,
    disconnect: idle && connected,
    signOut: idle && (connected || auth.savedLogin),
    forget: idle && auth.savedLogin,
    steam: idle && !connected && auth.steam,
  };
}
// A password from an administrator's code is set outside a session, before signing in.
export function canResetPassword(
  auth: AuthState,
  connected: boolean,
  code: string,
  password: string,
) {
  return (
    !auth.authenticating &&
    !connected &&
    code.trim().length > 0 &&
    password.length > 0
  );
}
