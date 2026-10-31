import { describe, expect, it } from "vitest";
import { hexToHsv, hsvToHex } from "../src/state/color";

describe("the color picker model", () => {
  it("round-trips codes through hue, saturation and value", () => {
    for (const color of [
      "#FF0000",
      "#A8CE85",
      "#8CCCCC",
      "#123456",
      "#FFFFFF",
      "#000000",
    ])
      expect(hsvToHex(hexToHsv(color))).toBe(color);
    expect(hexToHsv("#FF0000")).toEqual({ h: 0, s: 1, v: 1 });
    expect(hsvToHex({ h: 120, s: 1, v: 0.5 })).toBe("#008000");
  });

  it("keeps the chosen hue through greys and wraps a full turn", () => {
    expect(hexToHsv("#808080", 200)).toEqual({ h: 200, s: 0, v: 128 / 255 });
    expect(hsvToHex({ h: 360, s: 1, v: 1 })).toBe("#FF0000");
  });
});
