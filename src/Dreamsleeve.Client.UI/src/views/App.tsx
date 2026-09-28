import type { Chat } from "../state/chat";
import { SkyrimLayout } from "../layouts/SkyrimLayout";
export function App({ chat }: { chat: Chat }) {
  return <SkyrimLayout chat={chat} />;
}
