import { Children, createElement, isValidElement } from "react";
import type { ChangeEvent, ReactElement, ReactNode } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import type { Command, Settings } from "../src/bridge/types";
import { defaults, limits } from "../src/bridge/settings.generated";
import { makeChat } from "../src/state/chat";
import type { Chat } from "../src/state/chat";
import { SettingsPanel } from "../src/views/SettingsPanel";

function setup() {
  const send = vi.fn((_command: Command) => true);
  const chat = makeChat(send);
  chat.receive({
    type: "snapshot",
    serverName: "Dreamsleeve",
    channels: [{ id: "1", name: "Общий", kind: "global", writable: true }],
    messages: [],
    players: [],
    selfId: "1",
  });
  return { chat, send };
}

function props(chat: Chat) {
  const state = chat.store.getState();
  return {
    chat,
    settings: state.settings,
    identity: state.identity,
    phase: state.connectionPhase,
    nameColor: state.nameColor,
    selfColor: undefined,
    selfName: "Довакин",
    connected: state.connected,
  };
}

// Exercise the panel's actual callbacks without adding a browser/DOM dependency.
// ReactDOM below renders its nested Select and ColorField as well.
type ControlProps = {
  children?: ReactNode;
  label?: string;
  "aria-label"?: string;
  "data-part"?: string;
  type?: string;
  onChange?: unknown;
  onClick?: unknown;
  options?: { value: string; label: string }[];
};
function elements(node: ReactNode): ReactElement<ControlProps>[] {
  return Children.toArray(node).flatMap((child) =>
    isValidElement<ControlProps>(child)
      ? [child, ...elements(child.props.children)]
      : [],
  );
}
function group(chat: Chat) {
  const found = elements(SettingsPanel(props(chat))).find(
    (element) => element.props["data-part"] === "phantoms",
  );
  expect(found).toBeDefined();
  return found!;
}
function change(chat: Chat, label: string, value: string | number | boolean) {
  const control = elements(group(chat)).find(
    (element) =>
      element.props["aria-label"] === label || element.props.label === label,
  );
  expect(control).toBeDefined();
  const onChange = control!.props.onChange;
  if (control!.props.type === "checkbox") {
    (onChange as (event: ChangeEvent<HTMLInputElement>) => void)({
      target: { checked: value },
    } as ChangeEvent<HTMLInputElement>);
  } else if (control!.props.type === "range") {
    (onChange as (event: ChangeEvent<HTMLInputElement>) => void)({
      target: { value: String(value) },
    } as ChangeEvent<HTMLInputElement>);
  } else {
    (onChange as (value: string) => void)(String(value));
  }
}
function markup(chat: Chat) {
  const html = renderToStaticMarkup(createElement(SettingsPanel, props(chat)));
  const found = html.match(
    /<fieldset\b[^>]*data-part="phantoms"[^>]*>(.*?)<\/fieldset>/s,
  );
  expect(found).not.toBeNull();
  return found![1];
}
function input(html: string, label: string) {
  const found = html.match(
    new RegExp(`<input\\b[^>]*aria-label="${label}"[^>]*>`),
  );
  expect(found).not.toBeNull();
  return found![0];
}

describe("phantom settings", () => {
  it("keeps publication, viewing, fallback and combat switches independent", () => {
    const { chat, send } = setup();
    change(chat, "Публиковать мой фантом", false);
    expect(chat.store.getState().settings).toMatchObject({
      publishPhantoms: false,
      showPhantoms: true,
      phantomFallback: true,
      combatHidePhantoms: false,
    });
    change(chat, "Показывать фантомы других игроков", false);
    change(chat, "Публиковать мой фантом", true);
    change(chat, "Показывать светлячок, если фантом недоступен", false);
    change(chat, "Скрывать фантомы в бою", true);
    const html = markup(chat);
    expect(input(html, "Публиковать мой фантом")).toContain("checked");
    expect(input(html, "Показывать фантомы других игроков")).not.toContain(
      "checked",
    );
    expect(
      input(html, "Показывать светлячок, если фантом недоступен"),
    ).not.toContain("checked");
    expect(input(html, "Скрывать фантомы в бою")).toContain("checked");
    expect(chat.store.getState().settings.combatHideFireflies).toBe(
      defaults.combatHideFireflies,
    );
    expect(send).not.toHaveBeenCalled();
  });

  it("saves all fourteen fields only through the existing save action", () => {
    const { chat, send } = setup();
    const patch = {
      publishPhantoms: false,
      showPhantoms: false,
      phantomFallback: false,
      combatHidePhantoms: true,
      maxVisiblePhantoms: 3,
      phantomDrawDistance: 2048,
      phantomOpacity: 0.35,
      phantomColor: "#ABCDEF",
      phantomSampleRate: 15,
      phantomDelayMs: 125,
      phantomExtrapolationMs: 75,
      phantomTimeoutMs: 1500,
      phantomMemoryMiB: 512,
      phantomCacheMiB: 2048,
    } satisfies Partial<Settings>;
    change(chat, "Публиковать мой фантом", patch.publishPhantoms);
    change(chat, "Показывать фантомы других игроков", patch.showPhantoms);
    change(
      chat,
      "Показывать светлячок, если фантом недоступен",
      patch.phantomFallback,
    );
    change(chat, "Скрывать фантомы в бою", patch.combatHidePhantoms);
    change(chat, "Фантомов рядом", patch.maxVisiblePhantoms);
    change(chat, "Дальность фантомов", patch.phantomDrawDistance);
    change(chat, "Непрозрачность фантомов", patch.phantomOpacity);
    change(chat, "Цвет фантомов", patch.phantomColor);
    change(chat, "Частота движения", patch.phantomSampleRate);
    change(chat, "Минимальная задержка сглаживания", patch.phantomDelayMs);
    change(
      chat,
      "Продолжение движения без обновлений",
      patch.phantomExtrapolationMs,
    );
    change(chat, "Скрывать при отсутствии обновлений", patch.phantomTimeoutMs);
    change(chat, "Лимит памяти моделей", patch.phantomMemoryMiB);
    change(chat, "Лимит кеша на диске", patch.phantomCacheMiB);
    expect(chat.store.getState().settings).toEqual({ ...defaults, ...patch });
    expect(send).not.toHaveBeenCalled();

    const save = elements(SettingsPanel(props(chat))).find(
      (element) =>
        element.type === "button" &&
        element.props.children === "Сохранить настройки",
    );
    expect(save).toBeDefined();
    (save!.props.onClick as () => void)();
    const revision = chat.store.getState().revision;
    expect(send).toHaveBeenCalledExactlyOnceWith({
      type: "saveSettings",
      settings: { ...defaults, ...patch },
      revision,
    });
    chat.receive({ type: "settingsResult", revision });
    expect(chat.store.getState().savedRevision).toBe(revision);
  });

  it("renders host bounds, saved counts, color and read-only values with units", () => {
    const { chat, send } = setup();
    chat.receive({
      type: "settings",
      settings: { ...defaults, maxVisiblePhantoms: 3, phantomColor: "#ABCDEF" },
    });
    const html = markup(chat);
    expect(html).toContain("<legend>Фантомы</legend>");
    expect(html).toContain('aria-label="Фантомов рядом"');
    expect(html).toContain('data-value="3"');
    const count = elements(group(chat)).find(
      (element) => element.props.label === "Фантомов рядом",
    );
    expect(count!.props.options!.map((option) => Number(option.value))).toEqual(
      Array.from(
        {
          length:
            limits.maxVisiblePhantoms.max - limits.maxVisiblePhantoms.min + 1,
        },
        (_, i) => limits.maxVisiblePhantoms.min + i,
      ),
    );
    for (const [key, label] of [
      ["phantomDrawDistance", "Дальность фантомов"],
      ["phantomOpacity", "Непрозрачность фантомов"],
      ["phantomSampleRate", "Частота движения"],
      ["phantomDelayMs", "Минимальная задержка сглаживания"],
      ["phantomExtrapolationMs", "Продолжение движения без обновлений"],
      ["phantomTimeoutMs", "Скрывать при отсутствии обновлений"],
      ["phantomMemoryMiB", "Лимит памяти моделей"],
      ["phantomCacheMiB", "Лимит кеша на диске"],
    ] as const) {
      const range = input(html, label);
      expect(range).toContain('type="range"');
      expect(range).toContain(`min="${limits[key].min}"`);
      expect(range).toContain(`max="${limits[key].max}"`);
      expect(range).toContain(`value="${defaults[key]}"`);
    }
    expect(input(html, "Цвет фантомов")).toContain('value="#ABCDEF"');
    expect(input(html, "Цвет фантомов")).toContain('data-valid="true"');
    expect(html).toContain("<output>4096 игр. ед.</output>");
    expect(html).toContain("<output>10 Гц</output>");
    expect(html).toContain("<output>100 мс</output>");
    expect(html).toContain("<output>512 МиБ</output>");
    expect(html).toContain("<output>1024 МиБ</output>");
    expect(html).toContain("ограничивается сервером");
    expect(html).toContain("хранит готовые модели внешности");
    expect(html).not.toContain("contenteditable");
    expect(send).not.toHaveBeenCalled();
  });

  it("renders unchanged controls without commands or store updates on chat activity", () => {
    const { chat, send } = setup();
    const before = chat.store.getState();
    const html = markup(chat);
    expect(chat.store.getState()).toBe(before);
    chat.receive({
      type: "messages",
      messages: [
        {
          id: "1",
          channelId: "1",
          source: "system",
          announcement: { origin: "server", kind: "announcement" },
          text: "Привет",
          time: 0,
        },
      ],
    });
    const afterMessage = chat.store.getState();
    expect(afterMessage.messages).toHaveLength(1);
    expect(markup(chat)).toBe(html);
    expect(chat.store.getState()).toBe(afterMessage);
    expect(afterMessage.settings).toBe(before.settings);
    expect(html).not.toMatch(/<canvas|<svg|role="listbox"/);
    expect(send).not.toHaveBeenCalled();
  });
});
