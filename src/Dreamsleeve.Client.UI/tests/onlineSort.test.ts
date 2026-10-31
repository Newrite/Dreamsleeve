import { describe, expect, it } from "vitest";
import { defaults } from "../src/bridge/settings.generated";
import type { Player } from "../src/bridge/types";
import { sortPlayers } from "../src/state/onlineSort";

const player = (id: string, name: string, extra: Partial<Player> = {}) =>
  ({
    id,
    name,
    displayName: name,
    username: name.toLowerCase(),
    inCharacter: false,
    ...extra,
  }) satisfies Player;
const health = (current: number, maximum: number) => ({
  actorValues: [
    { key: "skyrim:health", name: "Здоровье", value: { current, maximum } },
  ],
});
const players = [
  player("1", "Мира", {
    level: 12,
    zone: "Вайтран",
    location: "Йоррваскр",
    activity: "Бой",
    ...health(50, 100),
  }),
  player("2", "аська", {
    level: 40,
    zone: "Вайтран",
    location: "Драконий предел",
  }),
  player("3", "Бьорн", {
    zone: "Рифтен",
    activity: "Торговля",
    ...health(90, 100),
  }),
  player("4", "Эйра", { level: 9 }),
];
const order = (
  sort: Parameters<typeof sortPlayers>[1],
  descending = false,
  settings = defaults,
) => sortPlayers(players, sort, descending, settings).map((p) => p.id);

describe("the online list order", () => {
  it("sorts names alphabetically without regard to case", () => {
    expect(order("name")).toEqual(["2", "3", "1", "4"]);
    expect(order("name", true)).toEqual(["4", "1", "3", "2"]);
  });

  it("keeps players without the value last in either direction", () => {
    expect(order("level")).toEqual(["4", "1", "2", "3"]);
    expect(order("level", true)).toEqual(["2", "1", "4", "3"]);
    expect(order("activity")).toEqual(["1", "3", "2", "4"]);
    expect(order("health", true)).toEqual(["3", "1", "2", "4"]);
  });

  it("orders a place by the zone, then by the location in it", () => {
    expect(order("place")).toEqual(["2", "1", "3", "4"]);
    expect(order("place", true)).toEqual(["3", "1", "2", "4"]);
  });

  it("sorts by the shown name: aliases in streamer mode", () => {
    const aliased = players.map((p, i) => ({ ...p, alias: `Тень ${4 - i}` }));
    expect(
      sortPlayers(aliased, "name", false, {
        ...defaults,
        streamerMode: true,
      }).map((p) => p.id),
    ).toEqual(["4", "3", "2", "1"]);
  });
});
