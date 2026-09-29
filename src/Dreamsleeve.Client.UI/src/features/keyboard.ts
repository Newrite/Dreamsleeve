// Ultralight can expose a Windows virtual key code without KeyboardEvent.key.
export function controlKey(
  event: Pick<KeyboardEvent, "key" | "code" | "keyCode">,
) {
  if (event.key && event.key !== "Unidentified") return event.key;
  if (event.code === "NumpadEnter") return "Enter";
  if (event.code === "Space") return " ";
  if (
    ["Enter", "Escape", "Tab", "ArrowUp", "ArrowDown", "Home", "End"].includes(
      event.code,
    )
  )
    return event.code;
  return (
    {
      9: "Tab",
      13: "Enter",
      27: "Escape",
      32: " ",
      35: "End",
      36: "Home",
      38: "ArrowUp",
      40: "ArrowDown",
    } as Record<number, string>
  )[event.keyCode];
}
