import type { ConnectionPhase } from "../bridge/types";
export const connectionLabels: Record<ConnectionPhase, string> = {
  disconnected: "Нет соединения",
  authenticating: "Авторизация…",
  connecting: "Подключение…",
  opening: "Вход на сервер…",
  connected: "Подключено",
  disconnecting: "Отключение…",
  faulted: "Ошибка подключения",
};
