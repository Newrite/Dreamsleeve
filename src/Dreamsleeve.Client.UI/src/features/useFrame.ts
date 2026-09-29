import {
  useEffect,
  useRef,
  useState,
  type MouseEvent as ReactMouseEvent,
  type RefObject,
} from "react";
import { frame } from "../state/settings";
import type { Chat } from "../state/chat";
// Dragging and resizing write to the element directly, once per animation
// frame: a store update per mouse move re-rendered the whole window and moved
// it through layout properties, which stuttered on Ultralight's CPU renderer
// and cost game frames. A drag moves the window with a transform; the store
// learns the final geometry once, on release.
export function useFrame(chat: Chat, target: RefObject<HTMLElement | null>) {
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
    const element = target.current;
    const initial = frame(s.settings, viewport.width, viewport.height);
    const x = event.clientX,
      y = event.clientY;
    let dx = 0,
      dy = 0,
      pending = 0;
    // Screen-pixel geometry the release would commit, clamped like frame().
    function geometry() {
      if (resize)
        return {
          ...initial,
          width: Math.min(viewport.width - initial.left, initial.width + dx),
          height: Math.min(viewport.height - initial.top, initial.height + dy),
        };
      return {
        ...initial,
        left: Math.max(
          0,
          Math.min(viewport.width - initial.width, initial.left + dx),
        ),
        top: Math.max(
          0,
          Math.min(viewport.height - initial.height, initial.top + dy),
        ),
      };
    }
    function paint() {
      pending = 0;
      if (!element) return;
      const g = geometry();
      if (resize) {
        element.style.width = `${g.width}px`;
        element.style.height = `${g.height}px`;
      } else
        element.style.transform = `translate(${g.left - initial.left}px, ${g.top - initial.top}px)`;
    }
    function move(e: MouseEvent) {
      dx = e.clientX - x;
      dy = e.clientY - y;
      if (!pending) pending = window.requestAnimationFrame(paint);
    }
    function end() {
      cleanup.current?.();
      if (pending) window.cancelAnimationFrame(pending);
      pending = 0;
      const g = geometry();
      if (element) {
        // The stored position lands in the same frame as the transform reset,
        // so the window does not jump back while React catches up.
        element.style.transform = "";
        element.style.left = `${g.left}px`;
        element.style.top = `${g.top}px`;
        element.style.width = `${g.width}px`;
        element.style.height = `${g.height}px`;
      }
      if (resize)
        chat.configure({
          width: g.width / s.settings.scale,
          height: g.height / s.settings.scale,
        });
      else
        chat.configure({
          x: g.left / viewport.width,
          y: g.top / viewport.height,
        });
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
