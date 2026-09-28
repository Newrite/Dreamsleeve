import type { Settings } from "../bridge/types";
export const defaults: Settings = {
  showFireflyNames: true,
  fireflyNameOcclusion: true,
  fireflyNameFontSize: 18,
  fireflyNameOffset: 35,
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
  locked: true,
  x: 0.025,
  y: 0.42,
  width: 680,
  height: 390,
  activationKey: "Enter",
  theme: "skyrim",
};
const bounds: Partial<Record<keyof Settings, [number, number]>> = {
  fireflyNameFontSize: [8, 48],
  fireflyNameOffset: [0, 512],
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
  for (const key of Object.keys(defaults) as (keyof Settings)[]) {
    const value = input[key];
    if (typeof value !== typeof defaults[key]) continue;
    if (typeof value === "number") {
      const range = bounds[key];
      if (range && Number.isFinite(value))
        Object.assign(result, {
          [key]: Math.min(range[1], Math.max(range[0], value)),
        });
    } else if (typeof value === "boolean")
      Object.assign(result, { [key]: value });
    else if (
      (key === "onlineView" && ["cards", "list"].includes(String(value))) ||
      (key === "font" && ["serif", "sans"].includes(String(value))) ||
      (key === "theme" && ["skyrim", "contrast"].includes(String(value))) ||
      (key === "nameMode" && ["display", "account"].includes(String(value))) ||
      (key === "activationKey" && ["Enter", "F2"].includes(String(value)))
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
