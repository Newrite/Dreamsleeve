// Ultralight can expose a Windows virtual key code without KeyboardEvent.key.
export function controlKey(
  event: Pick<KeyboardEvent, "key" | "code" | "keyCode">,
) {
  if (event.key && event.key !== "Unidentified") return event.key;
  if (event.code === "NumpadEnter") return "Enter";
  if (["Enter", "Escape", "Tab"].includes(event.code)) return event.code;
  return ({ 13: "Enter", 27: "Escape", 9: "Tab" } as Record<number, string>)[
    event.keyCode
  ];
}
