// Hue 0..360, saturation and value 0..1: the picker's own model, so dragging
// through grey or black keeps the hue the player chose.
export interface Hsv {
  h: number;
  s: number;
  v: number;
}
const channel = (value: number) =>
  Math.round(Math.min(1, Math.max(0, value)) * 255)
    .toString(16)
    .padStart(2, "0")
    .toUpperCase();
// "#RRGGBB" of a picker position.
export function hsvToHex({ h, s, v }: Hsv): string {
  const chroma = v * s;
  const sector = (((h % 360) + 360) % 360) / 60;
  const part = chroma * (1 - Math.abs((sector % 2) - 1));
  const [r, g, b] =
    sector < 1
      ? [chroma, part, 0]
      : sector < 2
        ? [part, chroma, 0]
        : sector < 3
          ? [0, chroma, part]
          : sector < 4
            ? [0, part, chroma]
            : sector < 5
              ? [part, 0, chroma]
              : [chroma, 0, part];
  const base = v - chroma;
  return `#${channel(r + base)}${channel(g + base)}${channel(b + base)}`;
}
// The picker position of a "#RRGGBB" color; a grey keeps the given hue.
export function hexToHsv(color: string, hue = 0): Hsv {
  const value = Number.parseInt(color.slice(1), 16);
  const r = ((value >> 16) & 255) / 255;
  const g = ((value >> 8) & 255) / 255;
  const b = (value & 255) / 255;
  const max = Math.max(r, g, b);
  const delta = max - Math.min(r, g, b);
  const s = max === 0 ? 0 : delta / max;
  if (delta === 0) return { h: hue, s, v: max };
  const h =
    max === r
      ? 60 * (((g - b) / delta + 6) % 6)
      : max === g
        ? 60 * ((b - r) / delta + 2)
        : 60 * ((r - g) / delta + 4);
  return { h, s, v: max };
}
