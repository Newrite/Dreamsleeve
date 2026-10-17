import { assert } from "vitest";
import { parseHostEvent } from "../src/bridge/parse";

// State tests consume only accepted events; rejection must fail the test first.
export function expectHostEvent(source: string) {
  const result = parseHostEvent(source);
  assert(result.ok, "Expected host event to be accepted");
  return result.event;
}
