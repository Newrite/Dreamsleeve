import { controlKey } from "./keyboard";
import { useEffect, useRef } from "react";
export function useDialog() {
  const ref = useRef<HTMLElement>(null);
  useEffect(() => {
    const prior = document.activeElement as HTMLElement | null;
    ref.current?.querySelector<HTMLButtonElement>("button")?.focus();
    function trap(event: KeyboardEvent) {
      if (controlKey(event) !== "Tab") return;
      const nodes = Array.from(
        ref.current?.querySelectorAll<HTMLElement>(
          'button:not(:disabled), input:not(:disabled), select:not(:disabled), [tabindex="0"]',
        ) ?? [],
      );
      const first = nodes[0],
        last = nodes[nodes.length - 1];
      if (!first) return;
      if (
        event.shiftKey &&
        (document.activeElement === first ||
          !ref.current?.contains(document.activeElement))
      ) {
        event.preventDefault();
        last.focus();
      } else if (
        !event.shiftKey &&
        (document.activeElement === last ||
          !ref.current?.contains(document.activeElement))
      ) {
        event.preventDefault();
        first.focus();
      }
    }
    window.addEventListener("keydown", trap);
    return () => {
      window.removeEventListener("keydown", trap);
      prior?.focus();
    };
  }, []);
  return ref;
}
