import {
  useEffect,
  useRef,
  useState,
  type MouseEvent as ReactMouseEvent,
} from "react";
import { frame } from "../state/settings";
import type { Chat } from "../state/chat";
export function useFrame(chat: Chat) {
  const cleanup = useRef<(() => void) | null>(null);
  useEffect(() => () => cleanup.current?.(), []);
  const [viewport, setViewport] = useState({
    width: window.innerWidth,
    height: window.innerHeight,
  });
  useEffect(() => {
    const resize = () =>
      setViewport({ width: window.innerWidth, height: window.innerHeight });
    window.addEventListener("resize", resize);
    return () => window.removeEventListener("resize", resize);
  }, []);
  function start(event: ReactMouseEvent, resize: boolean) {
    const s = chat.store.getState();
    if (s.settings.locked || !s.active || event.button !== 0) return;
    cleanup.current?.();
    event.preventDefault();
    const initial = frame(s.settings, viewport.width, viewport.height);
    const x = event.clientX,
      y = event.clientY;
    function move(e: MouseEvent) {
      const dx = e.clientX - x,
        dy = e.clientY - y;
      if (resize)
        chat.configure({
          width:
            Math.min(viewport.width - initial.left, initial.width + dx) /
            s.settings.scale,
          height:
            Math.min(viewport.height - initial.top, initial.height + dy) /
            s.settings.scale,
        });
      else
        chat.configure({
          x:
            Math.max(
              0,
              Math.min(viewport.width - initial.width, initial.left + dx),
            ) / viewport.width,
          y:
            Math.max(
              0,
              Math.min(viewport.height - initial.height, initial.top + dy),
            ) / viewport.height,
        });
    }
    function end() {
      cleanup.current?.();
      chat.save();
    }
    cleanup.current = () => {
      window.removeEventListener("mousemove", move);
      window.removeEventListener("mouseup", end);
      window.removeEventListener("blur", end);
      cleanup.current = null;
    };
    window.addEventListener("mousemove", move);
    window.addEventListener("mouseup", end);
    window.addEventListener("blur", end);
  }
  return { viewport, start };
}
