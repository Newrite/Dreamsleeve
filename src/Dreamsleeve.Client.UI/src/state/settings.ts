import type { Settings } from "../bridge/types";
// "#RRGGBB" only, as the host parses it.
export const isColor = (value: unknown) =>
  typeof value === "string" && /^#[0-9a-fA-F]{6}$/.test(value);
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
