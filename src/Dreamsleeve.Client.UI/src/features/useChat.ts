import { useEffect, useRef } from "react";
import { useStore } from "zustand";
import type { Chat } from "../state/chat";
export function useChat(chat: Chat) {
  const state = useStore(chat.store);
  const input = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if (state.active && !state.panel) input.current?.focus();
  }, [state.active, state.panel, Object.keys(state.pending).length]);
  useEffect(() => {
    if (!state.settings.fade || state.active || state.scrolled || state.panel)
      return;
    const remaining = Math.max(
      0,
      state.activity + state.settings.delay * 1000 - Date.now(),
    );
    const timer = window.setTimeout(
      () => chat.store.setState({ faded: true }),
      remaining,
    );
    return () => window.clearTimeout(timer);
  }, [
    chat,
    state.activity,
    state.settings.fade,
    state.settings.delay,
    state.active,
    state.scrolled,
    state.panel,
  ]);
  useEffect(() => {
    function key(event: KeyboardEvent) {
      if (!chat.store.getState().active || event.isComposing) return;
      if (event.key === "Escape") {
        event.preventDefault();
        if (chat.store.getState().panel) chat.open(null);
        else chat.close();
      }
    }
    window.addEventListener("keydown", key);
    return () => window.removeEventListener("keydown", key);
  }, [chat]);
  return { state, input };
}
