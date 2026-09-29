import type { ConnectionPhase, IdentityState } from "../bridge/types";
// One line under the switch and in the account panel. Until the server
// answers nothing claims the name is hidden already.
export function identityStatus(
  identity: IdentityState,
  phase: ConnectionPhase,
): string {
  if (identity.pending) return "Ожидание сервера…";
  if (identity.mode === "off") return "Другие игроки видят ваше имя";
  if (identity.pseudonym)
    return identity.mode === "exceptGroundMarks"
      ? `Другие видят вас как „${identity.pseudonym}“; в метках на земле — ваше имя`
      : `Другие видят вас как „${identity.pseudonym}“`;
  if (phase === "disconnected" || phase === "faulted")
    return "Имя будет скрыто при следующем входе на сервер";
  return "Ожидание сервера…";
}
