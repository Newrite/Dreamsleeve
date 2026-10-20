import type { Player, Settings } from "../bridge/types";
import { resourceKey } from "../views/resources";
import { playerName } from "./names";

export type OnlineSort = Settings["onlineSort"];
const collator = new Intl.Collator("ru", {
  sensitivity: "base",
  numeric: true,
});
// The share of health left, absent without a maximum.
function health(player: Player) {
  const value = player.actorValues?.find(
    (v) => resourceKey(v.key) === "health",
  )?.value;
  return typeof value === "object" && value.maximum > 0
    ? value.current / value.maximum
    : undefined;
}
// What a column orders by; absent sorts last in either direction.
function key(
  player: Player,
  sort: OnlineSort,
  settings: Settings,
): string | number | undefined {
  switch (sort) {
    case "name":
      return playerName(player, settings);
    case "level":
      return player.level;
    case "place":
      return player.zone ?? player.location;
    case "activity":
      return player.activity;
    case "health":
      return health(player);
  }
}
function compare(a: string | number, b: string | number) {
  return typeof a === "number" && typeof b === "number"
    ? a - b
    : collator.compare(String(a), String(b));
}
// The online list as the player ordered it; equal keys fall back to the
// shown name, a place to its location within the zone.
export function sortPlayers(
  players: Player[],
  sort: OnlineSort,
  descending: boolean,
  settings: Settings,
) {
  const sign = descending ? -1 : 1;
  const name = (p: Player) => playerName(p, settings);
  return players
    .map((player) => ({ player, key: key(player, sort, settings) }))
    .sort((a, b) => {
      if (a.key === undefined || b.key === undefined) {
        if (a.key !== b.key) return a.key === undefined ? 1 : -1;
      } else {
        const order = compare(a.key, b.key);
        if (order) return sign * order;
      }
      if (sort === "place") {
        const within = collator.compare(
          a.player.location ?? "",
          b.player.location ?? "",
        );
        if (within) return sign * within;
      }
      return (
        collator.compare(name(a.player), name(b.player)) ||
        a.player.id.localeCompare(b.player.id)
      );
    })
    .map(({ player }) => player);
}
