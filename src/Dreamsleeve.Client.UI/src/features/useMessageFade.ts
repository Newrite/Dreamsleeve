import { useEffect, useState } from "react";
import type { ChatState } from "../state/chat";
// One timer for the nearest expiry. CSS animates opacity; no per-frame store updates.
export function useMessageFade(state: ChatState) {
  const [clock, setClock] = useState(Date.now);
  const current = Math.max(clock, Date.now());
  const enabled = state.settings.fade && !state.active;
  const delay = state.settings.delay * 1000;
  useEffect(() => {
    if (!enabled) return;
    const now = Date.now();
    const deadlines = Object.values(state.receivedAt)
      .map((time) => time + delay)
      .filter((time) => time > current);
    if (!deadlines.length) return;
    const timer = window.setTimeout(
      () => setClock(Date.now()),
      Math.max(1, Math.min(...deadlines) - now),
    );
    return () => window.clearTimeout(timer);
  }, [enabled, state.receivedAt, delay, clock, current]);
  return (id: string) =>
    enabled &&
    (state.receivedAt[id] === undefined ||
      current >= state.receivedAt[id] + delay);
}
