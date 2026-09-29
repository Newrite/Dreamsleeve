import type {
  Announcement,
  Channel,
  GroundMark,
  Message,
  Player,
} from "../bridge/types";
// Demo marks of the signed-in player: a note here, a death place far away.
export const groundMarks: GroundMark[] = [
  {
    id: "301",
    kind: "note",
    text: "Осторожно: за поворотом тролль",
    time: Date.now() - 45 * 60000,
    character: "Довакин",
    location: "skyrim.esm:01A26F",
    x: 1240,
    y: -880,
    z: 512,
  },
  {
    id: "302",
    kind: "death",
    text: "Морозный тролль",
    time: Date.now() - 12 * 60000,
    character: "Довакин",
    location: "skyrim.esm:016BB4",
    x: 20140,
    y: 3300,
    z: 610,
  },
];
// Marks the server shows around the player: others' and the own note.
export const nearbyMarks: GroundMark[] = [
  groundMarks[0],
  {
    id: "310",
    kind: "death",
    text: "Медведь",
    time: Date.now() - 5 * 60000,
    author: "Мира",
    character: "Эйра",
    location: "skyrim.esm:01A26F",
    x: 1300,
    y: -720,
    z: 514,
  },
  {
    id: "311",
    kind: "note",
    text: "Сундук за водопадом",
    time: Date.now() - 90 * 60000,
    author: "Седобородый",
    location: "skyrim.esm:01A26F",
    x: 980,
    y: -1020,
    z: 498,
  },
];
// The system channel, found by kind like in the game; the id is only data.
export const SYSTEM_CHANNEL = "5";
export const channels: Channel[] = [
  { id: "1", kind: "global", name: "Общий", writable: true },
  { id: "2", kind: "party", name: "Группа", writable: true },
  { id: "3", kind: "guild", name: "Гильдия", writable: true },
  { id: "4", kind: "whisper", name: "Личные", writable: true },
  { id: SYSTEM_CHANNEL, kind: "system", name: "Объявления", writable: false },
];
export const players: Player[] = [
  {
    id: "18446744073709551601",
    name: "Северный",
    inCharacter: true,
    displayName: "Северный",
    username: "northern",
    character: "Довакин",
    level: 0,
    location: "Драконий Предел",
    zone: "Вайтран",
    race: "Норд",
    interior: true,
    activity: "Разговор",
    activityTarget: "Балгруф Старший",
    nearbyMarker: "Драконий Предел",
    markerKind: "Дворец",
    actorValues: [
      {
        key: "skyrim:health",
        name: "Здоровье",
        value: { current: 153, maximum: 153 },
      },
      {
        key: "skyrim:magicka",
        name: "Магия",
        value: { current: 72, maximum: 100 },
      },
      {
        key: "skyrim:stamina",
        name: "Запас сил",
        value: { current: 420, maximum: 569 },
      },
    ],
  },
  {
    id: "2",
    name: "Мира",
    inCharacter: true,
    displayName: "Мира",
    username: "mira",
    character: "Эйра",
    level: 24,
    location: "Ривервуд",
    zone: "Скайрим",
    race: "Бретонка",
    interior: false,
    activity: "Исследование",
    nearbyMarker: "Спящий великан",
    actorValues: [
      {
        key: "skyrim:health",
        name: "Здоровье",
        value: { current: 218, maximum: 300 },
      },
      {
        key: "skyrim:magicka",
        name: "Магия",
        value: { current: 340, maximum: 400 },
      },
      {
        key: "skyrim:stamina",
        name: "Запас сил",
        value: { current: 120, maximum: 180 },
      },
    ],
  },
  {
    id: "3",
    name: "Седобородый",
    inCharacter: true,
    displayName: "Седобородый",
    username: "greybeard",
    character: "Хальвар",
    level: 61,
    location: "Высокий Хротгар",
    zone: "Глотка Мира",
    race: "Норд",
    activity: "Обучение",
    activityTarget: "Арнгейр",
    interior: true,
    actorValues: [
      {
        key: "skyrim:health",
        name: "Здоровье",
        value: { current: 640, maximum: 640 },
      },
      { key: "shoutRecovery", name: "Восстановление крика", value: 12.5 },
    ],
  },
  // A player who hides their names: the server sends only its pseudonym.
  {
    id: "4",
    name: "Страж 2",
    inCharacter: false,
    displayName: "Страж 2",
    username: "",
    pseudonymous: true,
    level: 12,
    location: "Ривервуд",
    zone: "Скайрим",
    race: "Имперец",
    activity: "Исследование",
  },
];
const lines = [
  [
    SYSTEM_CHANNEL,
    "Добро пожаловать в Dreamsleeve. Пусть ваши дороги будут тёплыми, даже среди снегов.",
  ],
  ["1", "Кто-нибудь сейчас в Вайтране? Собираемся у ворот."],
  ["1", "Я в Ривервуде. Подойду через несколько минут."],
  ["2", "Давайте через перевал. Оттуда хорошо видно долину."],
  ["3", "Сегодня вечером собираемся у Высокого Хротгара."],
  ["1", "Здесь удивительно тихо без драконов."],
  [
    SYSTEM_CHANNEL,
    "Напоминание: берегите своих спутников и уважайте других странников.",
  ],
];
export const messages: Message[] = lines.map(([channelId, text], i) => ({
  id: String(i + 1),
  channelId,
  text,
  time: Date.now() - (lines.length - i) * 65000,
  ...(channelId === SYSTEM_CHANNEL
    ? {
        source: "system" as const,
        announcement: {
          origin: "server" as const,
          kind: "announcement" as const,
        },
      }
    : { source: "player" as const, author: players[(i + 1) % 3] }),
}));
// Every origin and kind.
const announce = (
  id: string,
  minutesAgo: number,
  announcement: Announcement,
  text: string,
  author?: Player,
): Message => ({
  id,
  channelId: SYSTEM_CHANNEL,
  source: "system",
  announcement,
  ...(author ? { author } : {}),
  text,
  time: Date.now() - minutesAgo * 60000,
});
export const announcements: Message[] = [
  announce(
    "51",
    6.5,
    { origin: "server", kind: "event" },
    "Сегодня в 20:00 — турнир лучников у ворот Вайтрана.",
  ),
  announce(
    "52",
    5.5,
    { origin: "server", kind: "admin" },
    "Сервер перезапустится через 10 минут.",
  ),
  announce(
    "53",
    4.5,
    { origin: "server", kind: "periodic" },
    "Правила сервера — в меню ☰. Не забывайте сохраняться.",
  ),
  announce(
    "54",
    3.5,
    { origin: "trustedClient", kind: "announcement" },
    "Караван до Виндхельма выходит из Ривервуда.",
    players[2],
  ),
  announce(
    "55",
    2.5,
    { origin: "thirdParty", kind: "event", signature: "Carriage Tours" },
    "Карета до Солитьюда отправляется через 5 минут.",
    players[0],
  ),
];
