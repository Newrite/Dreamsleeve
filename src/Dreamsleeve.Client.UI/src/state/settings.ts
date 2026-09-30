import type { Settings } from "../bridge/types";
export const defaults: Settings = {
  showFireflyNames: true,
  fireflyNameOcclusion: true,
  fireflyNameFontSize: 18,
  fireflyNameOffset: 35,
  showBubbles: true,
  bubbleDuration: 8,
  bubbleFade: true,
  bubbleFadeDuration: 1,
  bubbleFontSize: 16,
  bubbleMaxWidth: 320,
  bubbleBackground: 0.65,
  combatHideFireflies: false,
  combatHideNames: false,
  combatHideBubbles: false,
  onlineView: "cards",
  fade: true,
  delay: 12,
  duration: 1,
  idleOpacity: 0,
  scale: 1,
  fontSize: 16,
  font: "sans",
  lineHeight: 1.45,
  background: 0.82,
  timestamps: true,
  fullColor: false,
  nameMode: "display",
  streamerMode: false,
  textFilter: "off",
  locked: true,
  x: 0.025,
  y: 0.42,
  width: 680,
  height: 390,
  activationKey: "Enter",
  theme: "skyrim",
  announcementChannels: "all",
  announcementsServer: true,
  announcementsTrustedClient: true,
  announcementsThirdParty: true,
  announcementsEvents: true,
  announcementsPeriodic: true,
  bubbleBorder: true,
  bubbleTextColor: "#EEECE5",
  fireflyNameColor: "#EEECE5",
  fireflyHeightOffset: 110,
  showGroundNotes: true,
  showDeathMarks: true,
  maxVisibleNotes: 16,
  maxVisibleDeaths: 16,
  groundDrawDistance: 4096,
  groundNoteOffset: 5,
  deathMarkOffset: 5,
  groundNameDistance: 600,
  groundTextDistance: 150,
  groundFontSize: 16,
  groundMaxWidth: 320,
  groundBackground: 0.65,
  groundBorder: true,
  groundTextColor: "#EEECE5",
  deathTextColor: "#D9534F",
  deathBackground: 0.65,
  deathBorder: true,
  combatHideGroundMarks: false,
  combatHideGroundText: false,
  markDateStyle: "tamriel",
  deathDateHeader: true,
  noteDateHeader: false,
  markDateColor: "#A9A69B",
  hideIdentity: "off",
};
// "#RRGGBB" only, as the host parses it.
export const isColor = (value: unknown) =>
  typeof value === "string" && /^#[0-9a-fA-F]{6}$/.test(value);
const colors: (keyof Settings)[] = [
  "bubbleTextColor",
  "fireflyNameColor",
  "groundTextColor",
  "deathTextColor",
  "markDateColor",
];
// Whole numbers of marks; the host floors them the same way.
const integers: (keyof Settings)[] = ["maxVisibleNotes", "maxVisibleDeaths"];
const bounds: Partial<Record<keyof Settings, [number, number]>> = {
  fireflyHeightOffset: [0, 512],
  maxVisibleNotes: [1, 64],
  maxVisibleDeaths: [1, 64],
  groundDrawDistance: [0, 16384],
  groundNoteOffset: [-64, 256],
  deathMarkOffset: [-64, 256],
  groundNameDistance: [50, 4096],
  groundTextDistance: [50, 4096],
  groundFontSize: [8, 48],
  groundMaxWidth: [120, 800],
  groundBackground: [0, 1],
  deathBackground: [0, 1],
  fireflyNameFontSize: [8, 48],
  fireflyNameOffset: [0, 512],
  bubbleDuration: [1, 60],
  bubbleFadeDuration: [0.1, 5],
  bubbleFontSize: [8, 48],
  bubbleMaxWidth: [120, 800],
  bubbleBackground: [0, 1],
  delay: [0, 120],
  duration: [0, 5],
  idleOpacity: [0, 1],
  scale: [0.7, 1.5],
  fontSize: [12, 26],
  lineHeight: [1.1, 2],
  background: [0, 1],
  x: [0, 1],
  y: [0, 1],
  width: [320, 1600],
  height: [220, 1200],
};
export function settingsFrom(input: Partial<Settings>): Settings {
  const result = { ...defaults };
  // Files from before the character mode used "account" for the username.
  if ((input.nameMode as string) === "account")
    input = { ...input, nameMode: "username" };
  for (const key of Object.keys(defaults) as (keyof Settings)[]) {
    const value = input[key];
    if (typeof value !== typeof defaults[key]) continue;
    if (typeof value === "number") {
      const range = bounds[key];
      if (range && Number.isFinite(value)) {
        const clamped = Math.min(range[1], Math.max(range[0], value));
        Object.assign(result, {
          [key]: integers.includes(key) ? Math.floor(clamped) : clamped,
        });
      }
    } else if (typeof value === "boolean")
      Object.assign(result, { [key]: value });
    else if (colors.includes(key)) {
      if (isColor(value)) Object.assign(result, { [key]: value });
    } else if (
      (key === "onlineView" && ["cards", "list"].includes(String(value))) ||
      (key === "font" && ["serif", "sans"].includes(String(value))) ||
      (key === "theme" && ["skyrim", "contrast"].includes(String(value))) ||
      (key === "textFilter" &&
        ["off", "mask", "hide"].includes(String(value))) ||
      (key === "nameMode" &&
        ["username", "display", "character"].includes(String(value))) ||
      (key === "activationKey" && ["Enter", "F2"].includes(String(value))) ||
      (key === "announcementChannels" &&
        ["tab", "all", "current"].includes(String(value))) ||
      (key === "hideIdentity" &&
        ["off", "everywhere", "exceptGroundMarks"].includes(String(value))) ||
      (key === "markDateStyle" && ["tamriel", "earth"].includes(String(value)))
    )
      Object.assign(result, { [key]: value });
  }
  return result;
}
export function frame(s: Settings, width: number, height: number) {
  const w = Math.min(s.width * s.scale, width);
  const h = Math.min(s.height * s.scale, height);
  return {
    width: w,
    height: h,
    left: Math.min(s.x * width, width - w),
    top: Math.min(s.y * height, height - h),
  };
}
