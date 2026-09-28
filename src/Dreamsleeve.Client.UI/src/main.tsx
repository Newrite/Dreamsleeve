import { createRoot } from "react-dom/client";
import { chat } from "./bridge/prisma";
import { App } from "./views/App";
import "./styles/base.css";
import "./themes/skyrim.css";
const theme = document.querySelector('link[href$="theme.user.css"]');
if (theme) document.head.appendChild(theme);
createRoot(document.getElementById("root")!).render(<App chat={chat} />);
