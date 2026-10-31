import { controlKey } from "../features/keyboard";
import { hexToHsv, hsvToHex, type Hsv } from "../state/color";
import { isColor } from "../state/settings";
import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
} from "react";
import type {
  CSSProperties,
  KeyboardEvent,
  MouseEvent as ReactMouseEvent,
} from "react";
import styles from "../styles/ColorField.module.css";
// A keyboard step on the saturation/value square, and a large one with Shift.
const STEP = 0.02;
const LARGE_STEP = 0.1;
const clamp = (value: number) => Math.min(1, Math.max(0, value));
// The picker: a saturation/value square in the chosen hue, a hue strip and
// ready colors. Ultralight has no native colour dialog, so the layer is ours,
// fixed to the screen like the Select list and closed by Escape or a click
// elsewhere. Every move applies the colour, as typing a valid code does.
function Picker({
  label,
  value,
  presets,
  anchor,
  onChange,
  onClose,
}: {
  label: string;
  value: string;
  presets: readonly string[];
  anchor: HTMLElement;
  onChange: (value: string) => void;
  onClose: () => void;
}) {
  const layer = useRef<HTMLDivElement>(null);
  const area = useRef<HTMLDivElement>(null);
  const strip = useRef<HTMLDivElement>(null);
  const [hsv, setHsv] = useState<Hsv>(() => hexToHsv(value));
  const [place, setPlace] = useState<CSSProperties>({ visibility: "hidden" });
  // A colour set from outside (a typed code, a preset) moves the marker; the
  // picker's own colour keeps its hue and position through greys.
  useEffect(() => {
    setHsv((current) =>
      hsvToHex(current) === value.toUpperCase()
        ? current
        : hexToHsv(value, current.h),
    );
  }, [value]);
  // The layer follows its swatch when a panel scrolls; a swatch scrolled off
  // the screen takes the layer with it.
  useLayoutEffect(() => {
    const position = () => {
      const box = anchor.getBoundingClientRect();
      if (box.bottom < 0 || box.top > window.innerHeight) {
        onClose();
        return;
      }
      const height = layer.current?.offsetHeight ?? 0;
      const width = layer.current?.offsetWidth ?? 0;
      const below = box.bottom + 4 + height <= window.innerHeight;
      setPlace({
        left: Math.max(0, Math.min(box.left, window.innerWidth - width)),
        top: below ? box.bottom + 4 : Math.max(0, box.top - 4 - height),
      });
    };
    position();
    const follow = (event: Event) => {
      const target = event.target as Node | null;
      if (event.type === "resize" || target?.contains(anchor)) position();
    };
    const outside = (event: MouseEvent) => {
      const target = event.target as Node;
      if (!layer.current?.contains(target) && !anchor.contains(target))
        onClose();
    };
    window.addEventListener("resize", follow);
    document.addEventListener("scroll", follow, true);
    document.addEventListener("mousedown", outside);
    return () => {
      window.removeEventListener("resize", follow);
      document.removeEventListener("scroll", follow, true);
      document.removeEventListener("mousedown", outside);
    };
  }, [anchor, onClose]);
  function apply(next: Hsv) {
    setHsv(next);
    const color = hsvToHex(next);
    if (color !== value.toUpperCase()) onChange(color);
  }
  // The point's share of the element's width and of its height from the
  // bottom, clamped: a drag past the edge stays on it.
  function share(element: HTMLElement | null, x: number, y: number) {
    const box = element?.getBoundingClientRect();
    if (!box || box.width === 0 || box.height === 0) return undefined;
    return {
      across: clamp((x - box.left) / box.width),
      up: clamp(1 - (y - box.top) / box.height),
    };
  }
  const pickArea = (x: number, y: number) => {
    const point = share(area.current, x, y);
    if (point) apply({ h: hsv.h, s: point.across, v: point.up });
  };
  const pickHue = (x: number, y: number) => {
    const point = share(strip.current, x, y);
    if (point) apply({ ...hsv, h: Math.min(359, point.across * 360) });
  };
  // A drag keeps following the mouse outside the element until the button is up.
  function drag(event: ReactMouseEvent, pick: (x: number, y: number) => void) {
    event.preventDefault();
    (event.currentTarget as HTMLElement).focus();
    pick(event.clientX, event.clientY);
    const move = (e: MouseEvent) => pick(e.clientX, e.clientY);
    const up = () => {
      window.removeEventListener("mousemove", move);
      window.removeEventListener("mouseup", up);
    };
    window.addEventListener("mousemove", move);
    window.addEventListener("mouseup", up);
  }
  function key(event: KeyboardEvent) {
    const name = controlKey(event.nativeEvent);
    if (name === "Escape") {
      event.preventDefault();
      event.stopPropagation();
      onClose();
      anchor.focus();
      return;
    }
    const step = event.shiftKey ? LARGE_STEP : STEP;
    if (event.target === strip.current) {
      const turn = { ArrowLeft: -1, ArrowDown: -1, ArrowRight: 1, ArrowUp: 1 }[
        name
      ];
      if (turn === undefined) return;
      event.preventDefault();
      apply({ ...hsv, h: (hsv.h + turn * step * 360 + 360) % 360 });
      return;
    }
    if (event.target !== area.current) return;
    const moves: Record<string, [number, number]> = {
      ArrowLeft: [-step, 0],
      ArrowRight: [step, 0],
      ArrowUp: [0, step],
      ArrowDown: [0, -step],
    };
    const move = moves[name];
    if (!move) return;
    event.preventDefault();
    apply({ h: hsv.h, s: clamp(hsv.s + move[0]), v: clamp(hsv.v + move[1]) });
  }
  return (
    <div
      ref={layer}
      className={styles.picker}
      style={place}
      role="dialog"
      aria-label={`Выбор цвета: ${label}`}
      onKeyDown={key}
    >
      <div
        ref={area}
        className={styles.area}
        style={{ backgroundColor: hsvToHex({ h: hsv.h, s: 1, v: 1 }) }}
        role="slider"
        tabIndex={0}
        aria-label="Насыщенность и яркость"
        aria-valuetext={`насыщенность ${Math.round(hsv.s * 100)}%, яркость ${Math.round(hsv.v * 100)}%`}
        onMouseDown={(e) => drag(e, pickArea)}
      >
        <span
          className={styles.marker}
          style={{
            left: `${hsv.s * 100}%`,
            top: `${(1 - hsv.v) * 100}%`,
            background: hsvToHex(hsv),
          }}
        />
      </div>
      <div
        ref={strip}
        className={styles.hue}
        role="slider"
        tabIndex={0}
        aria-label="Оттенок"
        aria-valuemin={0}
        aria-valuemax={359}
        aria-valuenow={Math.round(hsv.h) % 360}
        onMouseDown={(e) => drag(e, pickHue)}
      >
        <span
          className={styles.hueMarker}
          style={{ left: `${(hsv.h / 360) * 100}%` }}
        />
      </div>
      <div className={styles.presets} role="group" aria-label="Готовые цвета">
        {presets.map((color) => (
          <button
            key={color}
            type="button"
            style={{ background: color }}
            aria-label={color}
            aria-pressed={color === value.toUpperCase()}
            onClick={() => apply(hexToHsv(color, hsv.h))}
          />
        ))}
      </div>
    </div>
  );
}
// Light colors for text and fireflies, a few darker accents, white and grey.
export const colorPresets = [
  "#EEECE5",
  "#B0BEC5",
  "#E57373",
  "#D9534F",
  "#FFB74D",
  "#FFF176",
  "#A8CE85",
  "#81C784",
  "#8CCCCC",
  "#64B5F6",
  "#9575CD",
  "#F06292",
];
// A "#RRGGBB" field: the swatch opens the picker, the code can be typed. An
// incomplete code is kept while typing and applied only once it is valid.
export function ColorField({
  label,
  value,
  onChange,
  presets = colorPresets,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  presets?: readonly string[];
}) {
  const swatch = useRef<HTMLButtonElement>(null);
  const [text, setText] = useState(value);
  const [open, setOpen] = useState(false);
  const close = useCallback(() => setOpen(false), []);
  useEffect(() => setText(value), [value]);
  return (
    <div className={styles.color}>
      <span>{label}</span>
      <button
        ref={swatch}
        type="button"
        className={styles.swatch}
        style={{ background: isColor(text) ? text : value }}
        aria-label={`Выбрать цвет: ${label}`}
        aria-haspopup="dialog"
        aria-expanded={open}
        onClick={() => setOpen(!open)}
      />
      <input
        type="text"
        aria-label={label}
        maxLength={7}
        spellCheck={false}
        value={text}
        data-valid={isColor(text)}
        onChange={(e) => {
          const next = e.target.value.trim();
          setText(next);
          if (isColor(next) && next.toUpperCase() !== value.toUpperCase())
            onChange(next.toUpperCase());
        }}
        onBlur={() => {
          if (!isColor(text)) setText(value);
        }}
      />
      {open && swatch.current && (
        <Picker
          label={label}
          value={value}
          presets={presets}
          anchor={swatch.current}
          onChange={onChange}
          onClose={close}
        />
      )}
    </div>
  );
}
