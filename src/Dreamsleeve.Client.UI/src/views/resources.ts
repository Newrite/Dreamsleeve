// Native actor values use the Skyrim namespace; older UI fixtures used bare keys.
export function resourceKey(key: string) {
  const value = key.toLowerCase();
  return value.startsWith("skyrim:") ? value.slice("skyrim:".length) : value;
}
