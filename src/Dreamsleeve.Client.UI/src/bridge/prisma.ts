import { installVisibility } from "./visibility";
import { makeChat } from "../state/chat";
import { parseHostEvent } from "./parse";
declare global {
  interface Window {
    dreamsleeveCommand?: (json: string) => void;
    dreamsleeveReceive?: (json: string) => void;
  }
}
// Installed before React mounts. The native adapter supplies the command listener.
export const chat = makeChat((command) => {
  if (!window.dreamsleeveCommand) return false;
  try {
    window.dreamsleeveCommand(JSON.stringify(command));
    return true;
  } catch {
    return false;
  }
});
window.dreamsleeveReceive = (payload) => {
  try {
    chat.receive(parseHostEvent(payload));
  } catch {
    chat.store.setState({
      notice: "Ошибка данных интерфейса. Ожидается новый снимок.",
    });
  }
};

installVisibility(chat);
