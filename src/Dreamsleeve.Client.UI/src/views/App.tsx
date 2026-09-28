import { useStore } from "zustand";
import type { Chat } from "../state/chat";
import { SkyrimLayout } from "../layouts/SkyrimLayout";
export function App({ chat }: { chat: Chat }) {
  const visible = useStore(chat.store, (state) => state.visible);
  return visible ? <SkyrimLayout chat={chat} /> : null;
}
