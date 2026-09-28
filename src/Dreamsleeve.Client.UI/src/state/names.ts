import type { Player, Settings } from "../bridge/types";
// Shown for a player whose pseudonym has not arrived yet after streamer mode
// was switched on: never fall back to a real name.
export const HIDDEN_NAME = "Скрытое имя";
// The host resolves `name` for every surface, the native nameplates included.
// Right after a local switch to streamer mode the host has not re-projected
// yet, so only a pseudonym may be shown.
export function playerName(player: Player, settings: Settings): string {
  if (settings.streamerMode) return player.alias ?? HIDDEN_NAME;
  return player.name || player.displayName;
}
// Real names for secondary lines (profile, tooltips, search); none in streamer mode.
export function realNames(player: Player, settings: Settings) {
  if (settings.streamerMode || player.alias !== undefined) return undefined;
  return {
    displayName: player.displayName,
    username: player.username,
    character: player.character,
  };
}
export function characterLine(player: Player, settings: Settings): string {
  if (!player.inCharacter) return "Вне персонажа";
  return realNames(player, settings)?.character ?? "В игре";
}
