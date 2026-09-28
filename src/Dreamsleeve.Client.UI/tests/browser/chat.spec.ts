import { test, expect } from "@playwright/test";
test.beforeEach(async ({ page }) => {
  await page.goto("/demo.html");
});
test("publication, announcements, settings, and saved configuration", async ({
  page,
}) => {
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  const input = page.getByRole("textbox", { name: "Сообщение" });
  await input.fill("Проверка публикации");
  await input.press("Enter");
  await expect(page.locator('[data-part="pending-message"]')).toContainText(
    "Проверка публикации",
  );
  await expect(
    page
      .locator('[data-part="message"]')
      .filter({ hasText: "Проверка публикации" }),
  ).toHaveCount(1);
  await expect(page.locator('[data-part="pending-message"]')).toHaveCount(0);
  await expect(page.locator('[data-part="messages"]')).toContainText(
    "Проверка публикации",
  );
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await expect(
    page.getByLabel("Канал отправки").locator("option"),
  ).not.toContainText(["Объявления"]);
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await page.getByRole("button", { name: "Настройки", exact: true }).click();
  await page.getByLabel("Отображаемое имя").selectOption("username");
  await page.getByRole("button", { name: "Сохранить настройки" }).click();
  await page.getByRole("button", { name: "Закрыть панель" }).click();
  await expect(page.locator('[data-part="messages"]')).toContainText(
    "greybeard:",
  );
  await page.reload();
  await expect(page.locator('[data-part="messages"]')).toContainText(
    "greybeard:",
  );
});
test("fade wakes without grabbing focus; active chat does not fade", async ({
  page,
}) => {
  await page.evaluate(() =>
    localStorage.setItem(
      "dreamsleeve.ui.settings",
      JSON.stringify({ delay: 0.1, duration: 0 }),
    ),
  );
  await page.reload();
  await expect(page.locator('[data-part="message"]').first()).toHaveCSS(
    "opacity",
    "0",
  );
  await page
    .getByRole("button", { name: "Новое сообщение", exact: true })
    .click();
  await expect(page.locator('[data-part="chat"]')).toHaveAttribute(
    "data-active",
    "false",
  );
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await page.waitForTimeout(250);
  await expect(page.locator('[data-part="chat"]')).toHaveCSS("opacity", "1");
});
test("scroll stays put and unread jumps to latest", async ({ page }) => {
  await page.getByRole("button", { name: "Заполнить историю" }).click();
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  const history = page.getByLabel("История сообщений");
  await history.evaluate((el) => {
    el.scrollTop = 0;
    el.dispatchEvent(new Event("scroll"));
  });
  await page
    .getByRole("button", { name: "Новое сообщение", exact: true })
    .click();
  await expect(
    page.getByRole("button", { name: /Новые сообщения/ }),
  ).toBeVisible();
  expect(await history.evaluate((el) => el.scrollTop)).toBe(0);
  await page.getByRole("button", { name: /Новые сообщения/ }).click();
  await expect(
    page.getByRole("button", { name: /Новые сообщения/ }),
  ).toHaveCount(0);
});
test("drag and resize persist; smaller viewport keeps the frame visible", async ({
  page,
}) => {
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await page.getByRole("button", { name: "Закрепление окна" }).click();
  const title = page
    .locator('[data-part="header"]')
    .getByText("Голоса Тамриэля", { exact: true });
  const box = (await title.boundingBox())!;
  await page.mouse.move(box.x + 30, box.y + 5);
  await page.mouse.down();
  await page.mouse.move(box.x + 200, box.y - 80);
  await page.mouse.up();
  const resize = (await page
    .getByRole("button", { name: "Изменить размер" })
    .boundingBox())!;
  await page.mouse.move(resize.x + 5, resize.y + 5);
  await page.mouse.down();
  await page.mouse.move(resize.x + 90, resize.y + 65);
  await page.mouse.up();
  await page.setViewportSize({ width: 500, height: 320 });
  await expect
    .poll(async () => {
      const frame = (await page.locator('[data-part="chat"]').boundingBox())!;
      return (
        frame.x >= 0 &&
        frame.y >= 0 &&
        frame.x + frame.width <= 501 &&
        frame.y + frame.height <= 321
      );
    })
    .toBe(true);
});
test("screenshots: passive and active settings", async ({ page }) => {
  await page.screenshot({ path: "test-results/chat-passive.png" });
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await page.screenshot({ path: "test-results/chat-active.png" });
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await page.getByRole("button", { name: "Настройки", exact: true }).click();
  await page.screenshot({ path: "test-results/chat-settings.png" });
});

test("production entry receives a snapshot before focus, escapes text, and sends only commands", async ({
  page,
}) => {
  await page.goto("/index.html");
  await page.evaluate(() => {
    window.dreamsleeveReceive!(
      JSON.stringify({
        type: "snapshot",
        serverName: "Голоса Тамриэля",
        selfId: "18446744073709551615",
        channels: [{ id: "1", name: "Общий", kind: "global", writable: true }],
        players: [],
        messages: [
          {
            id: "1",
            channelId: "1",
            time: 0,
            source: "system",
            text: "<img src=x onerror=alert(1)>",
          },
        ],
      }),
    );
    window.dreamsleeveReceive!(JSON.stringify({ type: "activate" }));
  });
  await expect(page.getByLabel("История сообщений")).toContainText(
    "<img src=x onerror=alert(1)>",
  );
  await expect(page.getByLabel("История сообщений").locator("img")).toHaveCount(
    0,
  );
  await page
    .getByLabel("Сообщение", { exact: true })
    .fill("Сохранить черновик");
  await page.getByLabel("Сообщение", { exact: true }).press("Enter");
  await expect(page.locator('[data-part="pending-message"]')).toContainText(
    "Сохранить черновик",
  );
  await expect(page.locator('[data-part="pending-message"]')).toHaveAttribute(
    "data-status",
    "failed",
  );
  await page.evaluate(() =>
    window.dreamsleeveReceive!('{"type":"messages","messages":[{}]}'),
  );
  await expect(
    page.getByRole("status").filter({ hasText: "Ошибка данных" }),
  ).toContainText("Ошибка данных");
  await expect(page.getByLabel("История сообщений")).toContainText("<img");
});

test("rejection allows explicit retry and level zero remains visible", async ({
  page,
}) => {
  await page
    .getByRole("button", { name: "Отклонить следующую отправку" })
    .click();
  await page.getByLabel("Сообщение", { exact: true }).fill("Не потерять");
  await page.getByLabel("Сообщение", { exact: true }).press("Enter");
  await expect(page.getByRole("status")).toContainText("запрещённые слова");
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await expect(page.locator('[data-part="pending-message"]')).toContainText(
    "Не потерять",
  );
  await page.getByRole("button", { name: "Повторить", exact: true }).click();
  await expect(
    page.locator('[data-part="message"]').filter({ hasText: "Не потерять" }),
  ).toHaveCount(1);
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await page.getByRole("button", { name: "Профиль", exact: true }).click();
  await expect(page.getByLabel("Уровень 0")).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(page.locator('[data-part="chat"]')).toHaveAttribute(
    "data-active",
    "true",
  );
});

test("built game assets load and user theme overrides remain available", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (e) => errors.push(e.message));
  await page.goto("http://127.0.0.1:5179");
  await expect(page.getByLabel("История сообщений")).toContainText(
    "Ожидание подключения",
  );
  await expect(page.locator(".workshop-controls")).toHaveCount(0);
  const response = await page.request.get(
    "http://127.0.0.1:5179/theme.user.css",
  );
  expect(response.ok()).toBe(true);
  await page.evaluate(() => {
    window.dreamsleeveReceive!(
      JSON.stringify({
        type: "snapshot",
        serverName: "Голоса Тамриэля",
        selfId: "1",
        channels: [],
        players: [],
        messages: [],
      }),
    );
    const custom = document.createElement("style");
    custom.textContent = '[data-part="chat"] { --ink: rgb(1, 2, 3); }';
    document.head.appendChild(custom);
  });
  await expect(page.locator('[data-part="chat"]')).toHaveCSS(
    "color",
    "rgb(1, 2, 3)",
  );
  expect(errors).toEqual([]);
});

test("large menu stays independent of chat geometry and exposes player metadata", async ({
  page,
}) => {
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  const dialog = page.getByRole("dialog");
  const before = (await dialog.boundingBox())!;
  expect(before.width).toBeGreaterThan(900);
  await expect(dialog).toContainText("Драконий Предел");
  await expect(dialog).toContainText("153 / 153");
  await expect(dialog).toContainText("Балгруф Старший");
  await page.getByLabel("Поиск игроков").fill("Ривервуд");
  await expect(dialog).toContainText("Эйра");
  await expect(dialog.getByText("Хальвар", { exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Настройки", exact: true }).click();
  await page.getByLabel("Масштаб", { exact: true }).fill("1.5");
  await expect
    .poll(async () => (await dialog.boundingBox())!.width)
    .toBe(before.width);
  await expect.poll(async () => (await dialog.boundingBox())!.x).toBe(before.x);
  await page.getByRole("button", { name: "Сохранить настройки" }).click();
  await expect(
    dialog.getByRole("status", { name: "Результат операции" }),
  ).toContainText("сохранены");
  await page.reload();
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await page.screenshot({ path: "test-results/online-large.png" });
  await page.getByRole("button", { name: "Настройки", exact: true }).click();
  await expect(page.getByLabel("Масштаб", { exact: true })).toHaveValue("1.5");
});

test("connection status stays hidden until activation and hides again on Escape", async ({
  page,
}) => {
  await page.goto("http://127.0.0.1:5179");
  await page.evaluate(() => {
    window.dreamsleeveCommand = () => {};
  });
  const hud = page.locator('[data-part="chat"]');
  await expect(hud).toBeHidden();
  for (const phase of ["authenticating", "faulted", "disconnected"] as const) {
    await page.evaluate((phase) => {
      window.dreamsleeveReceive!(
        JSON.stringify({ type: "connection", connected: false, phase }),
      );
    }, phase);
    await expect(hud).toBeHidden();
    await page.evaluate(() =>
      window.dreamsleeveReceive!(JSON.stringify({ type: "activate" })),
    );
    await expect(hud).toBeVisible();
    await expect(page.getByLabel("Состояние подключения")).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(hud).toBeHidden();
  }
});

test("compact online handles dozens of players and remembers the selected view", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1920, height: 1080 });
  await page.getByRole("button", { name: "48 игроков онлайн" }).click();
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await page.getByRole("button", { name: "Список", exact: true }).click();
  const table = page.getByRole("table");
  await expect(table.locator("tbody tr")).toHaveCount(48);
  expect(
    await page
      .getByLabel("Компактный онлайн")
      .evaluate((el) => el.scrollWidth <= el.clientWidth),
  ).toBe(true);
  expect(
    (await table.locator("tbody tr").first().boundingBox())!.height,
  ).toBeLessThanOrEqual(54);
  await page.screenshot({ path: "test-results/online-compact.png" });
  await page.getByLabel("Поиск игроков").fill("Странник 48");
  await expect(table.locator("tbody tr")).toHaveCount(1);
  await table.getByRole("button").click();
  await expect(page.getByRole("dialog")).toContainText("Странник 48");
  await page.reload();
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await expect(
    page.getByRole("button", { name: "Список", exact: true }),
  ).toHaveAttribute("aria-pressed", "true");
  await expect(page.getByRole("table")).toBeVisible();
  await page.setViewportSize({ width: 600, height: 800 });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
  await expect(page.getByRole("table")).toBeVisible();
  await page.getByRole("button", { name: "Карточки", exact: true }).click();
  await expect(page.getByRole("table")).toHaveCount(0);
});

test("passive chat highlights only fresh publications, while Enter restores history", async ({
  page,
}) => {
  await page.evaluate(() =>
    localStorage.setItem(
      "dreamsleeve.ui.settings",
      JSON.stringify({ delay: 0.3, duration: 0 }),
    ),
  );
  await page.reload();
  const old = page.locator('[data-part="message"]').first();
  await expect(old).toHaveCSS("opacity", "0");
  await expect(page.locator('[data-part="header"]')).toHaveCSS(
    "visibility",
    "hidden",
  );
  await page.waitForTimeout(350);
  await page
    .getByRole("button", { name: "Новое сообщение", exact: true })
    .click();
  const newest = page.locator('[data-part="message"]').last();
  await expect(newest).toHaveCSS("opacity", "1");
  await expect(page.locator('[data-part="header"]')).toHaveCSS(
    "visibility",
    "hidden",
  );
  await page.screenshot({ path: "test-results/passive-new-message.png" });
  await expect(old).toHaveCSS("opacity", "0");
  await expect(page.locator('[data-part="header"]')).toHaveCSS("opacity", "0");
  await expect(page.locator('[data-part="chat"]')).toHaveCSS(
    "background-color",
    "rgba(0, 0, 0, 0)",
  );
  await expect(newest).toHaveCSS("opacity", "0");
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await expect(old).toHaveCSS("opacity", "1");
  await expect(
    page.getByRole("button", { name: "Закрыть чат", exact: true }),
  ).toHaveCount(0);
  await page.keyboard.press("Escape");
  await expect(old).toHaveCSS("opacity", "0");
  await expect(page.locator('[data-part="chat"]')).toHaveAttribute(
    "data-active",
    "false",
  );
});

test("server label and dim pending row survive delayed acknowledgement without waking chrome", async ({
  page,
}) => {
  await page.goto("/index.html");
  await page.clock.install();
  await page.evaluate(() => {
    window.dreamsleeveCommand = () => {};
    window.dreamsleeveReceive!(
      JSON.stringify({
        type: "snapshot",
        serverName: "Голоса Тамриэля",
        selfId: "7",
        channels: [{ id: "1", name: "Общий", kind: "global", writable: true }],
        players: [],
        messages: [],
      }),
    );
    window.dreamsleeveReceive!(JSON.stringify({ type: "activate" }));
  });
  await expect(page.locator('[data-part="header"]')).toContainText(
    "Голоса Тамриэля",
  );
  await page
    .getByLabel("Сообщение", { exact: true })
    .fill("Встречаемся в Ривервуде");
  await page.getByLabel("Сообщение", { exact: true }).press("Enter");
  const pending = page.locator('[data-part="pending-message"]');
  await expect(pending).toHaveAttribute("data-status", "sending");
  await expect(pending.locator("span")).toHaveCSS("opacity", "0.5");
  await expect(page.locator('[data-part="header"]')).toHaveCSS(
    "visibility",
    "hidden",
  );
  await page.addStyleTag({
    content: "body { background: #20282d !important; }",
  });
  await page.screenshot({ path: "test-results/chat-pending.png" });
  await page.clock.fastForward(15001);
  await expect(pending).toHaveAttribute("data-status", "unknown");
  await page.evaluate(() => {
    window.dreamsleeveReceive!(
      JSON.stringify({ type: "sendResult", requestId: "1", messageId: "42" }),
    );
    window.dreamsleeveReceive!(
      JSON.stringify({
        type: "messages",
        messages: [
          {
            id: "42",
            channelId: "1",
            source: "player",
            author: {
              id: "7",
              name: "Северный",
              inCharacter: false,
              displayName: "Северный",
              username: "north",
            },
            text: "Встречаемся в Ривервуде",
            time: Date.now(),
          },
        ],
      }),
    );
  });
  await expect(pending).toHaveCount(0);
  await expect(page.locator('[data-part="message"]')).toHaveCount(1);
});

test("native Hide and Show preserve chat but release focus and hide the workspace", async ({
  page,
}) => {
  await page.goto("/index.html");
  await page.evaluate(() => {
    (window as unknown as Window & { commands: string[] }).commands = [];
    window.dreamsleeveCommand = (json) =>
      (window as unknown as Window & { commands: string[] }).commands.push(
        json,
      );
    window.Hide();
    window.dreamsleeveReceive!(
      JSON.stringify({
        type: "snapshot",
        serverName: "Test",
        selfId: "7",
        channels: [{ id: "1", kind: "global", name: "Общий", writable: true }],
        messages: [],
        players: [],
      }),
    );
    window.dreamsleeveReceive!(JSON.stringify({ type: "activate" }));
  });
  await expect(page.locator('[data-part="chat"]')).toHaveCount(0);
  await page.evaluate(() => window.Show());
  await expect(page.locator('[data-part="chat"]')).toHaveAttribute(
    "data-active",
    "false",
  );
  await page.evaluate(() =>
    window.dreamsleeveReceive!(JSON.stringify({ type: "activate" })),
  );
  await page
    .getByLabel("Сообщение", { exact: true })
    .fill("Не потерять черновик");
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await page.evaluate(() => {
    window.Hide();
    window.Hide();
  });
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(page.locator('[data-part="chat"]')).toHaveCount(0);
  expect(
    await page.evaluate(() => document.activeElement === document.body),
  ).toBe(true);
  await page.evaluate(() => {
    window.dreamsleeveReceive!(
      JSON.stringify({
        type: "messages",
        messages: [
          {
            id: "1",
            channelId: "1",
            source: "system",
            text: "Пришло пока скрыто",
            time: Date.now(),
          },
        ],
      }),
    );
    window.dreamsleeveReceive!(
      JSON.stringify({ type: "connection", connected: false }),
    );
    window.dreamsleeveReceive!(JSON.stringify({ type: "activate" }));
  });
  await expect(page.locator('[data-part="chat"]')).toHaveCount(0);
  await page.evaluate(() => window.Show());
  await expect(page.locator('[data-part="chat"]')).toHaveAttribute(
    "data-active",
    "false",
  );
  await expect(page.getByLabel("История сообщений")).toContainText(
    "Пришло пока скрыто",
  );
  await page.evaluate(() =>
    window.dreamsleeveReceive!(JSON.stringify({ type: "activate" })),
  );
  await expect(page.getByLabel("Сообщение", { exact: true })).toHaveValue(
    "Не потерять черновик",
  );
  expect(
    await page.evaluate(() =>
      (window as unknown as Window & { commands: string[] }).commands.map(
        (s) => JSON.parse(s).type,
      ),
    ),
  ).toEqual(["close"]);
});

test("Ultralight key codes submit once and Escape closes panel then chat", async ({
  page,
}) => {
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  const input = page.getByRole("textbox", { name: "Сообщение" });
  await input.fill("Ultralight keyboard regression");
  await input.dispatchEvent("keydown", {
    key: "Unidentified",
    keyCode: 13,
    isComposing: true,
  });
  await expect(input).toHaveValue("Ultralight keyboard regression");
  await input.dispatchEvent("keydown", { key: "Unidentified", keyCode: 13 });
  await expect(
    page
      .locator('[data-part="message"]')
      .filter({ hasText: "Ultralight keyboard regression" }),
  ).toHaveCount(1);
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  const escape = () =>
    page.evaluate(() =>
      window.dispatchEvent(
        new KeyboardEvent("keydown", {
          key: "Unidentified",
          keyCode: 27,
          bubbles: true,
          cancelable: true,
        }),
      ),
    );
  await escape();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(page.locator('[data-part="chat"]')).toHaveAttribute(
    "data-active",
    "true",
  );
  await escape();
  await expect(page.locator('[data-part="chat"]')).toHaveAttribute(
    "data-active",
    "false",
  );
});

test("Skyrim actor value keys retain colors and values in cards and list", async ({
  page,
}) => {
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  const dialog = page.getByRole("dialog");
  await expect(
    dialog.locator('[data-resource="health"]').first(),
  ).toContainText("153 / 153");
  const colors = await dialog
    .locator("[data-resource] i")
    .evaluateAll((nodes) =>
      nodes.slice(0, 3).map((node) => getComputedStyle(node).backgroundColor),
    );
  expect(new Set(colors).size).toBe(3);
  await page.getByRole("button", { name: "Список", exact: true }).click();
  await expect(
    dialog.locator('[data-resource="health"]').first(),
  ).toContainText("153 / 153");
  await expect(
    dialog.locator('[data-resource="magicka"]').first(),
  ).toContainText("72 / 100");
  await expect(
    dialog.locator('[data-resource="stamina"]').first(),
  ).toContainText("420 / 569");
  await expect(dialog.locator('[data-resource="health"] b').first()).toHaveCSS(
    "font-size",
    "13px",
  );
  await page.screenshot({ path: "test-results/online-readable-resources.png" });
});

test("settings notice fades after Escape with the passive HUD", async ({
  page,
}) => {
  await page.evaluate(() =>
    localStorage.setItem(
      "dreamsleeve.ui.settings",
      JSON.stringify({ delay: 0.1, duration: 0 }),
    ),
  );
  await page.reload();
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await page.getByRole("button", { name: "Настройки", exact: true }).click();
  await page.getByRole("button", { name: "Сохранить настройки" }).click();
  await page.keyboard.press("Escape");
  const notice = page.locator('[data-part="chat"] [role="status"]');
  await expect(notice).toContainText("Настройки сохранены");
  await expect(notice).toHaveCSS("opacity", "1");
  await page.keyboard.press("Escape");
  await expect(notice).toHaveCSS("opacity", "0");
  await page
    .getByRole("button", { name: "Новое сообщение", exact: true })
    .click();
  await expect(notice).toHaveCSS("opacity", "0");
});

test("channel selector uses the active theme instead of native appearance", async ({
  page,
}) => {
  for (const theme of ["skyrim", "contrast"]) {
    await page.evaluate(
      (theme) =>
        localStorage.setItem(
          "dreamsleeve.ui.settings",
          JSON.stringify({ theme }),
        ),
      theme,
    );
    await page.reload();
    await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
    const select = page.getByLabel("Канал отправки");
    await expect(select).toHaveCSS("appearance", "none");
    await expect(select).toHaveCSS("background-color", "rgba(0, 0, 0, 0)");
    await select.selectOption({ label: "Общий" });
    await expect(select).toHaveValue("1");
    await page.screenshot({ path: `test-results/channel-${theme}.png` });
  }
});

test("firefly name preferences save and restore", async ({ page }) => {
  const openSettings = async () => {
    await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
    await page
      .getByRole("button", { name: "Открыть меню Dreamsleeve" })
      .click();
    await page.getByRole("button", { name: "Настройки", exact: true }).click();
  };
  await openSettings();
  await page.getByLabel("Показывать имена", { exact: true }).uncheck();
  await page.getByLabel("Скрывать имена за препятствиями").uncheck();
  await page.getByRole("slider", { name: "Размер шрифта имени" }).fill("26");
  await page
    .getByRole("slider", { name: "Высота имени над светлячком" })
    .fill("70");
  await page.getByRole("button", { name: "Сохранить настройки" }).click();
  await page.reload();
  await openSettings();
  await expect(
    page.getByLabel("Показывать имена", { exact: true }),
  ).not.toBeChecked();
  await expect(
    page.getByLabel("Скрывать имена за препятствиями"),
  ).not.toBeChecked();
  await expect(
    page.getByRole("slider", { name: "Размер шрифта имени" }),
  ).toHaveValue("26");
  await expect(
    page.getByRole("slider", { name: "Высота имени над светлячком" }),
  ).toHaveValue("70");
  await page
    .getByRole("group", { name: "Имена над светлячками" })
    .scrollIntoViewIfNeeded();
  await page.screenshot({ path: "test-results/firefly-settings.png" });
});

test("chat bubble preferences save and restore independently of names", async ({
  page,
}) => {
  const openSettings = async () => {
    await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
    await page
      .getByRole("button", { name: "Открыть меню Dreamsleeve" })
      .click();
    await page.getByRole("button", { name: "Настройки", exact: true }).click();
  };
  await openSettings();
  await page.getByLabel("Показывать сообщения", { exact: true }).uncheck();
  await page.getByLabel("Плавно скрывать сообщение", { exact: true }).uncheck();
  await page.getByRole("slider", { name: "Время показа, с" }).fill("15");
  await page
    .getByRole("slider", { name: "Размер шрифта сообщения" })
    .fill("20");
  await page.getByRole("slider", { name: "Максимальная ширина" }).fill("400");
  await page
    .getByRole("slider", { name: "Непрозрачность фона сообщения" })
    .fill("0.4");
  await page.getByRole("button", { name: "Сохранить настройки" }).click();
  await page.reload();
  await openSettings();
  await expect(
    page.getByLabel("Показывать сообщения", { exact: true }),
  ).not.toBeChecked();
  await expect(
    page.getByLabel("Плавно скрывать сообщение", { exact: true }),
  ).not.toBeChecked();
  await expect(
    page.getByLabel("Показывать имена", { exact: true }),
  ).toBeChecked();
  await expect(
    page.getByRole("slider", { name: "Время показа, с" }),
  ).toHaveValue("15");
  await expect(
    page.getByRole("slider", { name: "Размер шрифта сообщения" }),
  ).toHaveValue("20");
  await expect(
    page.getByRole("slider", { name: "Максимальная ширина" }),
  ).toHaveValue("400");
  await expect(
    page.getByRole("slider", { name: "Непрозрачность фона сообщения" }),
  ).toHaveValue("0.4");
  await page
    .getByRole("group", { name: "Сообщения над игроками" })
    .scrollIntoViewIfNeeded();
  await page.screenshot({ path: "test-results/bubble-settings.png" });
});

test("streamer mode hides real names everywhere and ignore hides history without hiding presence", async ({
  page,
}) => {
  await page.goto("/demo.html");
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await page.getByRole("button", { name: "Настройки", exact: true }).click();
  await page.getByLabel("Режим стримера").check();
  await page.getByRole("button", { name: "Онлайн", exact: true }).click();
  const workspace = page.locator('[data-part="workspace"]');
  const history = page.locator('[data-part="messages"]');
  // "Мира" alone also occurs in the place name "Глотка Мира".
  for (const real of [
    "mira",
    "Седобородый",
    "greybeard",
    "Эйра",
    "Северный",
    "Довакин",
    "Хальвар",
  ]) {
    await expect(workspace).not.toContainText(real);
    await expect(history).not.toContainText(real);
  }
  await page.getByPlaceholder("Имя, персонаж или место…").fill("greybeard");
  await expect(page.getByText("Игроки не найдены.")).toBeVisible();
  await page.getByPlaceholder("Имя, персонаж или место…").fill("");

  await page.getByRole("button", { name: "Настройки", exact: true }).click();
  await page.getByLabel("Режим стримера").uncheck();
  await expect(history).toContainText("Мира:");
  await page.getByRole("button", { name: "Закрыть панель" }).click();

  // Ignore Mira from her profile: her lines leave the chat, presence stays.
  await page.getByRole("button", { name: "Мира:" }).first().click();
  await page.getByRole("button", { name: "Игнорировать", exact: true }).click();
  await expect(history).not.toContainText("Мира:");
  await expect(
    page.getByRole("button", { name: "Не игнорировать" }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Онлайн", exact: true }).click();
  await expect(workspace).toContainText("Мира");
  await page.getByRole("button", { name: "Настройки", exact: true }).click();
  const list = page.getByLabel("Игнорируемые игроки");
  await expect(list).toContainText("Мира");
  await page.getByLabel("Режим стримера").check();
  await expect(list).not.toContainText("Мира");
  await page.getByLabel("Режим стримера").uncheck();
  await list.getByRole("button", { name: "Убрать" }).click();
  await expect(list).toHaveCount(0);
  await expect(history).toContainText("Мира:");
});
