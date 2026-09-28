import type { AuthFailure, AuthOperation, AuthState } from "../bridge/types";
export const failureLabels: Record<Exclude<AuthFailure, "none">, string> = {
  invalidCredentials: "Неверное имя или пароль",
  usernameTaken: "Имя занято",
  invalidRequest: "Некорректный запрос",
  registrationDisabled: "Регистрация отключена",
  busy: "Сервер занят, повторите позже",
  unavailable: "Сервер недоступен",
  invalidResponse: "Некорректный ответ сервера",
  credentialStorage: "Ошибка хранилища учётных данных Windows",
  canceled: "Операция отменена",
};
export const operationLabels: Record<Exclude<AuthOperation, "none">, string> = {
  passwordLogin: "Вход по паролю…",
  resume: "Вход сохранённой сессией…",
  signOut: "Выход…",
  forgetSavedLogin: "Удаление сохранённого входа…",
  resetPassword: "Сброс пароля…",
};
export const idleAuth: AuthState = {
  authenticating: false,
  operation: "none",
  failure: "none",
  error: "",
  savedLogin: false,
  savedUsername: "",
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
export interface AccountForm {
  username: string;
  password: string;
  displayName: string;
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
    register: idle && credentials && form.displayName.trim().length > 0,
    resume: idle && auth.savedLogin,
    disconnect: idle && connected,
    signOut: idle && (connected || auth.savedLogin),
    forget: idle && auth.savedLogin,
  };
}
