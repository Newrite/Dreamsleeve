import { test, expect } from "@playwright/test";
import type { Page } from "@playwright/test";
import { defaults } from "../../src/bridge/settings.generated";

async function openSettings(page: Page) {
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await page.getByRole("button", { name: "Настройки", exact: true }).click();
}

const sliders = [
  ["phantomDrawDistance", "Дальность фантомов", 2048],
  ["phantomOpacity", "Непрозрачность фантомов", 0.35],
  ["phantomSampleRate", "Частота движения", 15],
  ["phantomDelayMs", "Задержка сглаживания", 125],
  ["phantomExtrapolationMs", "Продолжение движения без обновлений", 75],
  ["phantomTimeoutMs", "Скрывать при отсутствии обновлений", 1500],
  ["phantomMemoryMiB", "Лимит памяти моделей", 512],
  ["phantomCacheMiB", "Лимит кеша на диске", 2048],
  ["phantomUploadKiB", "Отправка моделей", 256],
  ["phantomDownloadKiB", "Загрузка моделей", 1024],
] as const;

test("phantom controls save and restore all sixteen scalar settings", async ({
  page,
}, testInfo) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await page.goto("/demo.html");
  await openSettings(page);
  const group = page.getByRole("group", { name: "Фантомы", exact: true });
  await expect(group).toContainText("ограничивается сервером");
  await expect(group).toContainText("хранит готовые модели внешности");
  const publish = group.getByLabel("Публиковать мой фантом", { exact: true });
  const show = group.getByLabel("Показывать фантомы других игроков");
  await publish.uncheck();
  await expect(show).toBeChecked();
  await show.uncheck();
  await publish.check();
  await expect(show).not.toBeChecked();
  await publish.uncheck();
  await group
    .getByLabel("Показывать светлячок, если фантом недоступен")
    .uncheck();
  await group.getByLabel("Скрывать фантомы в бою").check();
  const count = group.getByRole("combobox", { name: "Фантомов рядом" });
  await count.click();
  await page.getByRole("option", { name: "3", exact: true }).click();
  const color = group.getByLabel("Цвет фантомов", { exact: true });
  await color.fill("invalid");
  await color.blur();
  await expect(color).toHaveValue(defaults.phantomColor);
  await color.fill("#abcdef");
  await color.blur();
  await expect(color).toHaveValue("#ABCDEF");
  for (const [, label, value] of sliders)
    await group
      .getByRole("slider", { name: label, exact: true })
      .fill(String(value));
  await group.scrollIntoViewIfNeeded();
  await page.screenshot({ path: testInfo.outputPath("phantom-settings.png") });
  await page.getByRole("button", { name: "Сохранить настройки" }).click();
  await expect(
    page.getByRole("status", { name: "Результат операции" }),
  ).toContainText("сохранены");
  await page.reload();
  await openSettings(page);
  await expect(publish).not.toBeChecked();
  await expect(show).not.toBeChecked();
  await expect(
    group.getByLabel("Показывать светлячок, если фантом недоступен"),
  ).not.toBeChecked();
  await expect(group.getByLabel("Скрывать фантомы в бою")).toBeChecked();
  await expect(count).toHaveAttribute("data-value", "3");
  await expect(color).toHaveValue("#ABCDEF");
  for (const [, label, value] of sliders)
    await expect(
      group.getByRole("slider", { name: label, exact: true }),
    ).toHaveValue(String(value));
  expect(errors).toEqual([]);
});

test("built phantom settings send one scalar save and keep controls stable on chat updates", async ({
  page,
}, testInfo) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await page.goto("http://127.0.0.1:5179");
  await page.evaluate((settings) => {
    const commands: unknown[] = [];
    window.dreamsleeveCommand = (json) => {
      commands.push(JSON.parse(json));
    };
    Object.assign(window, { phantomCommands: commands });
    window.dreamsleeveReceive!(
      JSON.stringify({
        type: "snapshot",
        serverName: "Dreamsleeve",
        selfId: "1",
        channels: [{ id: "1", name: "Общий", kind: "global", writable: true }],
        players: [],
        messages: [],
      }),
    );
    window.dreamsleeveReceive!(JSON.stringify({ type: "settings", settings }));
    window.dreamsleeveReceive!(JSON.stringify({ type: "activate" }));
  }, defaults);
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await page.getByRole("button", { name: "Настройки", exact: true }).click();
  const group = page.getByRole("group", { name: "Фантомы", exact: true });
  await group.scrollIntoViewIfNeeded();
  await group.getByRole("slider", { name: "Частота движения" }).fill("15");
  await expect(
    group.getByRole("slider", { name: "Частота движения" }),
  ).toHaveValue("15");
  await group.evaluate((element) => {
    const mutations: MutationRecord[] = [];
    const observer = new MutationObserver((records) =>
      mutations.push(...records),
    );
    observer.observe(element, {
      attributes: true,
      attributeOldValue: true,
      childList: true,
      subtree: true,
      characterData: true,
      characterDataOldValue: true,
    });
    Object.assign(window, {
      phantomMutations: mutations,
      phantomObserver: observer,
      phantomBeforeHtml: element.innerHTML,
      phantomInputs: Array.from(element.querySelectorAll("input")),
    });
  });
  await page.evaluate(() =>
    window.dreamsleeveReceive!(
      JSON.stringify({
        type: "messages",
        messages: [
          {
            id: "1",
            channelId: "1",
            source: "system",
            time: 0,
            announcement: { origin: "server", kind: "announcement" },
            text: "Проверка фонового чата",
          },
        ],
      }),
    ),
  );
  await expect(page.locator('[data-part="messages"]')).toContainText(
    "Проверка фонового чата",
  );
  const beforeSave = await page.evaluate(() => {
    const probe = window as unknown as {
      phantomCommands: unknown[];
      phantomMutations: MutationRecord[];
      phantomObserver: MutationObserver;
      phantomBeforeHtml: string;
      phantomInputs: HTMLInputElement[];
    };
    probe.phantomObserver.disconnect();
    // Controlled React inputs can temporarily remove/restore an attribute in a
    // commit. Check the final markup and node ownership, and record raw writes.
    const group = document.querySelector('[data-part="phantoms"]')!;
    const inputs = Array.from(group.querySelectorAll("input"));
    return {
      commands: probe.phantomCommands.length,
      unchangedMarkup: group.innerHTML === probe.phantomBeforeHtml,
      retainedInputs:
        inputs.length === probe.phantomInputs.length &&
        inputs.every((input, i) => input === probe.phantomInputs[i]),
      attributeWrites: probe.phantomMutations.filter(
        (record) => record.type === "attributes",
      ).length,
      changedAttributes: Array.from(
        new Set(
          probe.phantomMutations
            .map((record) => record.attributeName)
            .filter(Boolean),
        ),
      ),
    };
  });
  await testInfo.attach("chat-update-observation", {
    body: JSON.stringify(beforeSave),
    contentType: "application/json",
  });
  expect(beforeSave).toMatchObject({
    commands: 0,
    unchangedMarkup: true,
    retainedInputs: true,
  });
  await page.getByRole("button", { name: "Сохранить настройки" }).click();
  const commands = await page.evaluate(
    () => (window as unknown as { phantomCommands: unknown[] }).phantomCommands,
  );
  expect(commands).toEqual([
    {
      type: "saveSettings",
      settings: { ...defaults, phantomSampleRate: 15 },
      revision: expect.any(Number),
    },
  ]);
  expect(errors).toEqual([]);
});
