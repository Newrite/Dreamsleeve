import { controlKey } from "../features/keyboard";
import { useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import type { CSSProperties, KeyboardEvent } from "react";
import styles from "../styles/Select.module.css";
export type SelectOption<T extends string> = { value: T; label: string };
// Drop-down in the chat's own style. Ultralight draws the native <select>
// popup with system colours and clips long labels, so the list is ours: the
// button keeps focus and the keyboard, the options open in a fixed layer that
// escapes scrolling panels and flips upwards near the bottom of the screen.
export function Select<T extends string>({
  label,
  value,
  options,
  onChange,
  disabled,
  variant,
}: {
  label: string;
  value: T;
  options: SelectOption<T>[];
  onChange: (value: T) => void;
  disabled?: boolean;
  variant?: "plain";
}) {
  const id = useId();
  const button = useRef<HTMLButtonElement>(null);
  const list = useRef<HTMLUListElement>(null);
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const [place, setPlace] = useState<CSSProperties>({});
  const current = options.find((o) => o.value === value);
  function show() {
    if (disabled) return;
    setActive(
      Math.max(
        0,
        options.findIndex((o) => o.value === value),
      ),
    );
    setOpen(true);
  }
  function choose(next: T) {
    setOpen(false);
    if (next !== value) onChange(next);
  }
  // The list follows its button: a panel that scrolls (the browser itself
  // scrolls a freshly focused button into view) moves the list rather than
  // closing it; a button scrolled off the screen takes the list with it.
  useLayoutEffect(() => {
    if (!open) return;
    const position = () => {
      if (!button.current || !list.current) return;
      const anchor = button.current.getBoundingClientRect();
      if (anchor.bottom < 0 || anchor.top > window.innerHeight) {
        setOpen(false);
        return;
      }
      const height = list.current.offsetHeight;
      const width = Math.max(anchor.width, list.current.offsetWidth);
      const below = anchor.bottom + 2 + height <= window.innerHeight;
      setPlace({
        left: Math.max(0, Math.min(anchor.left, window.innerWidth - width)),
        top: below ? anchor.bottom + 2 : Math.max(0, anchor.top - 2 - height),
        minWidth: anchor.width,
      });
    };
    position();
    const follow = (event: Event) => {
      const target = event.target as Node | null;
      if (
        event.type === "resize" ||
        (button.current && target?.contains(button.current))
      )
        position();
    };
    window.addEventListener("resize", follow);
    document.addEventListener("scroll", follow, true);
    return () => {
      window.removeEventListener("resize", follow);
      document.removeEventListener("scroll", follow, true);
    };
  }, [open]);
  // Keep the active option visible by scrolling the list alone: scrollIntoView
  // would also scroll the panel and move the anchor.
  useEffect(() => {
    const box = list.current;
    const item = box?.children[active] as HTMLElement | undefined;
    if (!open || !box || !item) return;
    const top = item.offsetTop;
    const bottom = top + item.offsetHeight;
    if (top < box.scrollTop) box.scrollTop = top;
    else if (bottom > box.scrollTop + box.clientHeight)
      box.scrollTop = bottom - box.clientHeight;
  }, [open, active]);
  function key(event: KeyboardEvent<HTMLButtonElement>) {
    const name = controlKey(event.nativeEvent);
    if (!open) {
      if (["ArrowDown", "ArrowUp", "Enter", " "].includes(name)) {
        event.preventDefault();
        show();
      }
      return;
    }
    if (name === "Tab") {
      setOpen(false);
      return;
    }
    event.preventDefault();
    if (name === "Escape") {
      event.stopPropagation();
      setOpen(false);
    } else if (name === "ArrowDown")
      setActive(Math.min(options.length - 1, active + 1));
    else if (name === "ArrowUp") setActive(Math.max(0, active - 1));
    else if (name === "Home") setActive(0);
    else if (name === "End") setActive(options.length - 1);
    else if (name === "Enter" || name === " ") {
      const option = options[active];
      if (option) choose(option.value);
    }
  }
  return (
    <div
      className={`${styles.select} ${variant === "plain" ? styles.plain : ""}`}
    >
      <button
        ref={button}
        type="button"
        role="combobox"
        aria-label={label}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={open ? id : undefined}
        aria-activedescendant={open ? `${id}-${active}` : undefined}
        data-value={value}
        disabled={disabled}
        onClick={() => (open ? setOpen(false) : show())}
        onKeyDown={key}
        onBlur={() => setOpen(false)}
      >
        <span className={styles.value}>{current?.label ?? ""}</span>
        <span className={styles.chevron} aria-hidden="true" />
      </button>
      {/* A <label> around the control forwards clicks on the list to the
          button and would reopen it; preventDefault on the list stops that. */}
      {open && (
        <ul
          ref={list}
          id={id}
          role="listbox"
          aria-label={label}
          className={styles.list}
          style={place}
          onMouseDown={(e) => e.preventDefault()}
          onClick={(e) => e.preventDefault()}
        >
          {options.map((o, i) => (
            <li
              key={o.value}
              id={`${id}-${i}`}
              role="option"
              aria-selected={o.value === value}
              data-active={i === active || undefined}
              onMouseEnter={() => setActive(i)}
              onClick={() => choose(o.value)}
            >
              {o.label}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
