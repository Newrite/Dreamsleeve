import type { Player, Settings } from "../bridge/types";
// Shown for a player whose local pseudonym has not arrived yet after streamer
// mode was switched on: never fall back to a real name. Not to be confused
// with a player who hides their own names (Player.pseudonymous).
export const HIDDEN_NAME = "Псевдоним…";
// A player who hides their names from everyone: the server sent a pseudonym only.
export const HIDDEN_BY_PLAYER = "имя скрыто игроком";
// The host resolves `name` for every surface, the native nameplates included.
// Right after a local switch to streamer mode the host has not re-projected
// yet, so only a pseudonym may be shown.
export function playerName(player: Player, settings: Settings): string {
  if (settings.streamerMode) return player.alias ?? HIDDEN_NAME;
  return player.name || player.displayName;
}
// Real names for secondary lines (profile, tooltips, search); none in streamer mode.
export function realNames(player: Player, settings: Settings) {
  if (
    settings.streamerMode ||
    player.alias !== undefined ||
    player.pseudonymous
  )
    return undefined;
  return {
    displayName: player.displayName,
    username: player.username,
    character: player.character,
  };
}
// Whether a name the player may see contains the query (lower case, trimmed):
// the shown name and, outside streamer mode, the real ones.
export function nameMatches(player: Player, settings: Settings, query: string) {
  const real = realNames(player, settings);
  return [
    playerName(player, settings),
    real?.displayName,
    real?.username,
    real?.character,
  ].some((v) => v?.toLocaleLowerCase("ru").includes(query));
}
export function characterLine(player: Player, settings: Settings): string {
  if (player.pseudonymous) return "Имя скрыто игроком";
  if (!player.inCharacter) return "Вне персонажа";
  return realNames(player, settings)?.character ?? "В игре";
}
