// Name colors the settings offer: the palette the server gives new accounts
// at random (NameColor.palette), then white and a light grey. Every one reads
// on the dark chat background; the server judges any other color.
export const nameColorPalette = [
  "#E57373",
  "#F06292",
  "#BA68C8",
  "#9575CD",
  "#7986CB",
  "#64B5F6",
  "#4FC3F7",
  "#4DD0E1",
  "#4DB6AC",
  "#81C784",
  "#AED581",
  "#DCE775",
  "#FFF176",
  "#FFD54F",
  "#FFB74D",
  "#FF8A65",
  "#FFFFFF",
  "#B0BEC5",
];
const hex = (value: number) =>
  Math.round(value * 255)
    .toString(16)
    .padStart(2, "0")
    .toUpperCase();
// A light, saturated color of the hue (0..359), always readable in chat.
export function hueColor(hue: number): string {
  const saturation = 0.7;
  const lightness = 0.65;
  const chroma = (1 - Math.abs(2 * lightness - 1)) * saturation;
  const part = chroma * (1 - Math.abs(((hue / 60) % 2) - 1));
  const [r, g, b] =
    hue < 60
      ? [chroma, part, 0]
      : hue < 120
        ? [part, chroma, 0]
        : hue < 180
          ? [0, chroma, part]
          : hue < 240
            ? [0, part, chroma]
            : hue < 300
              ? [part, 0, chroma]
              : [chroma, 0, part];
  const base = lightness - chroma / 2;
  return `#${hex(r + base)}${hex(g + base)}${hex(b + base)}`;
}
// The hue of a "#RRGGBB" color, 0 for greys.
export function hueOf(color: string): number {
  const value = Number.parseInt(color.slice(1), 16);
  const r = ((value >> 16) & 255) / 255;
  const g = ((value >> 8) & 255) / 255;
  const b = (value & 255) / 255;
  const max = Math.max(r, g, b);
  const delta = max - Math.min(r, g, b);
  if (delta === 0) return 0;
  const hue =
    max === r
      ? ((g - b) / delta) % 6
      : max === g
        ? (b - r) / delta + 2
        : (r - g) / delta + 4;
  return Math.round((hue * 60 + 360) % 360);
}
