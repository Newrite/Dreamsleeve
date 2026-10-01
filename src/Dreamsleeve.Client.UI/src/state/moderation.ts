import type { MuteState, SessionEndState } from "../bridge/types";

const endFormat = new Intl.DateTimeFormat("ru-RU", {
  day: "numeric",
  month: "long",
  hour: "2-digit",
  minute: "2-digit",
});
export const term = (until?: number) =>
  until === undefined ? "бессрочно" : `до ${endFormat.format(until)}`;
// A mute holds until its end; one without an end until it is lifted.
export const muted = (mute: MuteState, now: number) =>
  mute.muted && (mute.until === undefined || mute.until > now);
export const muteText = (mute: MuteState) =>
  `Мут ${term(mute.until)}: ${mute.reason}`;
export function sessionEndText(end: SessionEndState): string {
  switch (end.reason) {
    case "kicked":
      return `Модератор закрыл сессию: ${end.text}`;
    case "banned":
      return `Аккаунт заблокирован ${term(end.until)}: ${end.text}`;
    case "revoked":
      return "Администратор отозвал доступ; войдите заново";
    case "addressBanned":
      return `IP-адрес заблокирован ${term(end.until)}: ${end.text}`;
  }
}
