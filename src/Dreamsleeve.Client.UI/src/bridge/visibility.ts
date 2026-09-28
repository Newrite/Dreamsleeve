import type { Chat } from "../state/chat";

declare global {
  interface Window {
    Hide: () => void;
    Show: () => void;
  }
}

// Prisma Invoke(view, "Hide()") / Invoke(view, "Show()"). No data is discarded.
export function installVisibility(chat: Chat) {
  window.Hide = () => chat.receive({ type: "hide" });
  window.Show = () => chat.receive({ type: "show" });
}
