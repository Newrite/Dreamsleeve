import { expect, test } from "@playwright/test";

test("phantom controls work offline and release chat focus", async ({
  page,
}) => {
  await page.goto("/index.html");
  await page.evaluate(() => {
    const commands: unknown[] = [];
    (window as unknown as { phantomCommands: unknown[] }).phantomCommands =
      commands;
    window.dreamsleeveCommand = (json) => {
      const command = JSON.parse(json);
      commands.push(command);
      if (command.type === "close")
        window.dreamsleeveReceive!(JSON.stringify({ type: "deactivate" }));
    };
    window.dreamsleeveReceive!(JSON.stringify({ type: "activate" }));
    window.dreamsleeveReceive!(
      JSON.stringify({
        type: "phantom",
        supported: true,
        recording: false,
        playing: false,
        ready: false,
        rate: 20,
        frames: 0,
        nodes: 0,
        bones: 0,
        seconds: 0,
        appearanceBytes: 0,
        poseBytes: 0,
        buildMs: 0,
        sampleMs: 0,
        status: "Готов к записи",
        exporting: false,
        exportPath: "",
        exportError: "",
        loading: false,
        replayChannels: 0,
        replayPoseBytes: 0,
        optimizedModelBytes: 0,
        removedGeometry: 0,
        loadedArchive: "",
      }),
    );
  });
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await page.getByRole("button", { name: "Настройки", exact: true }).click();
  const panel = page.getByRole("group", {
    name: "Фантом — локальный тест SE / AE / VR",
  });
  await expect(
    panel.getByRole("button", { name: "Воспроизвести фантома" }),
  ).toBeDisabled();
  await panel.getByRole("combobox", { name: "Частота записи фантома" }).click();
  await page.getByRole("option", { name: "40 Гц" }).click();
  await panel
    .getByRole("combobox", { name: "Сценарий записи фантома" })
    .click();
  await page.getByRole("option", { name: "Оружие", exact: true }).click();
  await panel.getByRole("button", { name: "Записать 15 секунд" }).click();
  await expect(page.locator('[data-part="chat"]')).toHaveAttribute(
    "data-active",
    "false",
  );
  const commands = await page.evaluate(
    () => (window as unknown as { phantomCommands: unknown[] }).phantomCommands,
  );
  expect(commands).toContainEqual({
    type: "phantom",
    action: "record",
    rate: 40,
    scenario: "equipment",
    poseMode: "full",
    modelMode: "original",
  });
  expect(commands.at(-1)).toEqual({ type: "close" });
  await page.evaluate(() =>
    window.dreamsleeveReceive!(JSON.stringify({ type: "activate" })),
  );
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await expect(panel).toBeVisible();
  await expect(
    panel.getByRole("combobox", { name: "Сценарий записи фантома" }),
  ).toHaveAttribute("data-value", "equipment");
  await expect(
    panel.getByRole("combobox", { name: "Частота записи фантома" }),
  ).toHaveAttribute("data-value", "40");
  await panel
    .getByRole("combobox", { name: "Позы воспроизведения фантома" })
    .click();
  await page
    .getByRole("option", { name: "Выбранные — квантованные", exact: true })
    .click();
  await panel
    .getByRole("combobox", { name: "Модель воспроизведения фантома" })
    .click();
  await page
    .getByRole("option", { name: "Без скрытой геометрии", exact: true })
    .click();
  await panel
    .getByRole("button", { name: "Загрузить последний архив" })
    .click();
  await expect(page.locator('[data-part="chat"]')).toHaveAttribute(
    "data-active",
    "true",
  );
  const loaded = await page.evaluate(
    () => (window as unknown as { phantomCommands: unknown[] }).phantomCommands,
  );
  expect(loaded.at(-1)).toEqual({
    type: "phantom",
    action: "load",
    rate: 40,
    scenario: "equipment",
    poseMode: "quantized",
    modelMode: "pruned",
  });
  await page
    .getByRole("dialog", { name: "Меню Dreamsleeve" })
    .getByRole("button", { name: "Аккаунт", exact: true })
    .click();
  await page.getByRole("button", { name: "Настройки", exact: true }).click();
  await expect(
    panel.getByRole("combobox", { name: "Частота записи фантома" }),
  ).toHaveAttribute("data-value", "40");
});
