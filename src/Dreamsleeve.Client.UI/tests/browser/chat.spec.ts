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
  await page.getByRole("combobox", { name: "Канал отправки" }).click();
  await expect(
    page.getByRole("listbox", { name: "Канал отправки" }),
  ).not.toContainText("Объявления");
  await page.keyboard.press("Escape");
  await expect(page.getByLabel("Чат Dreamsleeve")).toHaveAttribute(
    "data-active",
    "true",
  );
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await page.getByRole("button", { name: "Настройки", exact: true }).click();
  await page.getByRole("combobox", { name: "Отображаемое имя" }).click();
  await page.getByRole("option", { name: "Имя пользователя" }).click();
  await expect(
    page.getByRole("combobox", { name: "Отображаемое имя" }),
  ).toHaveAttribute("data-value", "username");
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
            announcement: { origin: "server", kind: "announcement" },
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
  // Sending keeps the chat active; an empty Enter returns the passive HUD.
  await page.getByLabel("Сообщение", { exact: true }).press("Enter");
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
            announcement: { origin: "server", kind: "announcement" },
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
  await expect(page.locator('[data-part="chat"]')).toHaveAttribute(
    "data-active",
    "true",
  );
  await expect(input).toHaveValue("");
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
    const select = page.getByRole("combobox", { name: "Канал отправки" });
    await expect(select).toHaveCSS("background-color", "rgba(0, 0, 0, 0)");
    await select.click();
    await page.getByRole("option", { name: "Общий" }).click();
    await expect(select).toHaveAttribute("data-value", "1");
    await expect(select).toHaveText("Общий");
    await page.screenshot({ path: `test-results/channel-${theme}.png` });
  }
});

test("combat preferences save and restore", async ({ page }) => {
  const openSettings = async () => {
    await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
    await page
      .getByRole("button", { name: "Открыть меню Dreamsleeve" })
      .click();
    await page.getByRole("button", { name: "Настройки", exact: true }).click();
  };
  const boxes = [
    "Скрывать светлячки",
    "Скрывать имена",
    "Скрывать сообщения над игроками",
  ];
  await openSettings();
  for (const name of boxes)
    await expect(page.getByLabel(name, { exact: true })).not.toBeChecked();
  for (const name of boxes)
    await page.getByLabel(name, { exact: true }).check();
  await page.getByRole("button", { name: "Сохранить настройки" }).click();
  await page.reload();
  await openSettings();
  for (const name of boxes)
    await expect(page.getByLabel(name, { exact: true })).toBeChecked();
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
    .getByRole("group", { name: "Светлячки и имена над ними" })
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
  await page
    .getByRole("slider", { name: "Максимальная ширина", exact: true })
    .fill("400");
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
    page.getByRole("slider", { name: "Максимальная ширина", exact: true }),
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
  await page.getByLabel("Скрывать чужие имена (только у меня)").check();
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
  await page.getByLabel("Скрывать чужие имена (только у меня)").uncheck();
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
  // The ignore list has its own tab, named like every other surface.
  const ignoredTab = page.getByRole("button", { name: "Игнор", exact: true });
  const settingsTab = page.getByRole("button", {
    name: "Настройки",
    exact: true,
  });
  const streamer = page.getByLabel("Скрывать чужие имена (только у меня)");
  await ignoredTab.click();
  const list = page.getByLabel("Игнорируемые игроки");
  await expect(list).toContainText("Мира");
  await expect(list).toContainText("в сети");
  await settingsTab.click();
  await streamer.check();
  await ignoredTab.click();
  await expect(list).not.toContainText("Мира");
  await settingsTab.click();
  await streamer.uncheck();
  await ignoredTab.click();
  await list.getByRole("button", { name: "Не игнорировать" }).click();
  await expect(list).toHaveCount(0);
  await expect(page.getByText("Список пуст.")).toBeVisible();
  await expect(history).toContainText("Мира:");
});

test("a moderator mutes from the author menu, lifts it in the list, removes a message and a mark", async ({
  page,
}) => {
  await page.goto("/demo.html?moderator");
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  const history = page.locator('[data-part="messages"]');
  const author = page.getByRole("button", { name: "Мира:" }).first();
  const menu = page.getByRole("menu", { name: "Действия: Мира" });
  await author.click({ button: "right" });
  await menu.getByRole("menuitem", { name: "Мут…" }).click();
  const dialog = page.getByRole("dialog", { name: "Мут: Мира" });
  const mute = dialog.getByRole("button", { name: "Замутить" });
  // A reason is required; the removals of marks are off by default.
  await expect(mute).toBeDisabled();
  await expect(dialog.getByLabel("Все надписи")).not.toBeChecked();
  await expect(dialog.getByLabel("Все места смерти")).not.toBeChecked();
  await dialog.getByLabel("Причина").fill("Флуд");
  await mute.click();
  await expect(dialog).toHaveCount(0);
  await expect(page.getByRole("status").first()).toContainText("Мут до");

  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await page.getByRole("button", { name: "Модерация", exact: true }).click();
  const sanctions = page.getByLabel("Наказания в силе");
  await expect(sanctions).toContainText("Мира");
  await expect(sanctions).toContainText("Флуд");
  await sanctions.getByRole("button", { name: "Снять" }).click();
  await expect(page.getByText("Никто не наказан.")).toBeVisible();
  await page.getByRole("button", { name: "Закрыть панель" }).click();

  // Her marks open in the moderation panel; one of them goes.
  await author.click({ button: "right" });
  await menu.getByRole("menuitem", { name: "Метки игрока" }).click();
  const marks = page.getByLabel("Метки игрока");
  await expect(marks).toContainText("Убийца: Медведь");
  await marks.getByRole("button", { name: "Удалить метку 310" }).click();
  await expect(page.getByText("У игрока нет меток.")).toBeVisible();
  await page.getByRole("button", { name: "Закрыть панель" }).click();

  // The message goes for everyone.
  const line = page
    .locator('[data-part="message"]')
    .filter({ has: page.getByRole("button", { name: "Мира:" }) })
    .first();
  const text = (await line.locator("span").last().textContent())!.trim();
  await line.getByRole("button", { name: "Мира:" }).click({ button: "right" });
  await menu.getByRole("menuitem", { name: "Удалить сообщение" }).click();
  await expect(history).not.toContainText(text);
});

test("a player without the role sees no moderator tools", async ({ page }) => {
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await page
    .getByRole("button", { name: "Мира:" })
    .first()
    .click({ button: "right" });
  const menu = page.getByRole("menu", { name: "Действия: Мира" });
  await expect(menu.getByRole("menuitem", { name: "Мут…" })).toHaveCount(0);
  await expect(
    menu.getByRole("menuitem", { name: "Удалить сообщение" }),
  ).toHaveCount(0);
  await page.keyboard.press("Escape");
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await expect(
    page.getByRole("button", { name: "Модерация", exact: true }),
  ).toHaveCount(0);
});

test("right click on an author opens a menu with profile and ignore", async ({
  page,
}) => {
  await page.goto("/demo.html");
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  const history = page.locator('[data-part="messages"]');
  await expect(history).toContainText("Мира:");
  const author = page.getByRole("button", { name: "Мира:" }).first();
  await author.click({ button: "right" });
  const menu = page.getByRole("menu", { name: "Действия: Мира" });
  await expect(menu).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(menu).toHaveCount(0);
  // Escape closed only the menu, the chat stays active.
  await expect(page.locator('[data-part="chat"]')).toHaveAttribute(
    "data-active",
    "true",
  );

  await author.click({ button: "right" });
  await menu.getByRole("menuitem", { name: "Открыть профиль" }).click();
  await expect(page.getByRole("dialog")).toContainText("Мира");
  await page.getByRole("button", { name: "Закрыть панель" }).click();

  await author.click({ button: "right" });
  await menu.getByRole("menuitem", { name: "Игнорировать" }).click();
  await expect(history).not.toContainText("Мира:");

  // Own name: the menu offers the profile only.
  await page
    .getByRole("button", { name: "Северный:" })
    .first()
    .click({ button: "right" });
  const own = page.getByRole("menu", { name: "Действия: Северный" });
  await expect(
    own.getByRole("menuitem", { name: "Открыть профиль" }),
  ).toBeVisible();
  await expect(own.getByRole("menuitem", { name: "Игнорировать" })).toHaveCount(
    0,
  );
  // A click outside closes the menu.
  await page.mouse.click(5, 5);
  await expect(own).toHaveCount(0);
});

test("server-flagged words are shown, masked or hidden by the local filter", async ({
  page,
}) => {
  await page.goto("/demo.html");
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  const history = page.locator('[data-part="messages"]');
  await expect(history).toContainText("t.me/freeskins");
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await page.getByRole("button", { name: "Настройки", exact: true }).click();
  const filter = page.getByRole("combobox", { name: "Помеченные сообщения" });
  await filter.click();
  await page.getByRole("option", { name: "Заменять звёздочками" }).click();
  await expect(history).toContainText("Раздаю скины: **************");
  await expect(history).not.toContainText("t.me/freeskins");
  await filter.click();
  await page
    .getByRole("option", { name: "Скрывать сообщение целиком" })
    .click();
  await expect(history).not.toContainText("Раздаю скины");
  await filter.click();
  await page.getByRole("option", { name: "Показывать как есть" }).click();
  await expect(history).toContainText("t.me/freeskins");
});

test("announcement preferences save and restore", async ({ page }) => {
  const openSettings = async () => {
    await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
    await page
      .getByRole("button", { name: "Открыть меню Dreamsleeve" })
      .click();
    await page.getByRole("button", { name: "Настройки", exact: true }).click();
  };
  const where = page.getByRole("combobox", { name: "Где показывать" });
  const boxes = [
    "От сервера",
    "От клиента Dreamsleeve",
    "От других модов",
    "События",
    "Периодические",
  ];
  await openSettings();
  await expect(where).toHaveAttribute("data-value", "all");
  for (const name of boxes)
    await expect(page.getByLabel(name, { exact: true })).toBeChecked();
  await where.click();
  await page
    .getByRole("option", { name: "Только во вкладке «Объявления»" })
    .click();
  await expect(where).toHaveAttribute("data-value", "tab");
  await page.getByLabel("От других модов", { exact: true }).uncheck();
  await page.getByLabel("Периодические", { exact: true }).uncheck();
  await page.getByRole("button", { name: "Сохранить настройки" }).click();
  await expect(
    page.getByRole("status", { name: "Результат операции" }),
  ).toContainText("сохранены");
  await page
    .getByRole("group", { name: "Объявления" })
    .scrollIntoViewIfNeeded();
  await page.screenshot({ path: "test-results/announcement-settings.png" });
  await page.reload();
  await openSettings();
  await expect(where).toHaveAttribute("data-value", "tab");
  await expect(
    page.getByLabel("От других модов", { exact: true }),
  ).not.toBeChecked();
  await expect(
    page.getByLabel("Периодические", { exact: true }),
  ).not.toBeChecked();
  for (const name of ["От сервера", "От клиента Dreamsleeve", "События"])
    await expect(page.getByLabel(name, { exact: true })).toBeChecked();
});

test("announcement rows show origin and kind, and follow the placement setting", async ({
  page,
}) => {
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  const history = page.locator('[data-part="messages"]');
  const rows = page.locator('[data-part="message"]');
  const tournament = rows.filter({ hasText: "турнир лучников" });
  await expect(tournament).toHaveAttribute("data-origin", "server");
  await expect(tournament).toHaveAttribute("data-kind", "event");
  await expect(tournament).toContainText("Сервер:");
  await expect(tournament).toContainText("Событие");
  await expect(
    rows.filter({ hasText: "Добро пожаловать в Dreamsleeve" }),
  ).toContainText("Сервер:");
  await expect(rows.filter({ hasText: "перезапустится" })).toContainText(
    "Администрация",
  );
  const trusted = rows.filter({ hasText: "Караван до Виндхельма" });
  await expect(trusted).toHaveAttribute("data-origin", "trustedClient");
  await expect(trusted).toContainText("Dreamsleeve · Седобородый:");
  const mod = rows.filter({ hasText: "Карета до Солитьюда" });
  await expect(mod).toHaveAttribute("data-origin", "thirdParty");
  await expect(mod).toContainText("Carriage Tours · Северный:");
  await expect(mod).not.toContainText("Сервер");
  // Announcement authors are labels, not buttons with a context menu.
  await expect(mod.getByRole("button")).toHaveCount(0);
  await page.screenshot({ path: "test-results/announcement-rows.png" });

  const tabs = page.getByRole("navigation", { name: "Каналы" });
  const setPlacement = async (label: string) => {
    await page
      .getByRole("button", { name: "Открыть меню Dreamsleeve" })
      .click();
    await page.getByRole("button", { name: "Настройки", exact: true }).click();
    await page.getByRole("combobox", { name: "Где показывать" }).click();
    await page.getByRole("option", { name: label }).click();
    await page.getByRole("button", { name: "Закрыть панель" }).click();
  };
  await expect(history).toContainText("турнир лучников");
  await setPlacement("Только во вкладке «Объявления»");
  await expect(history).not.toContainText("турнир лучников");
  await expect(history).toContainText("Кто-нибудь сейчас в Вайтране?");
  await tabs.getByRole("button", { name: /^Объявления/ }).click();
  await expect(history).toContainText("турнир лучников");
  await tabs.getByRole("button", { name: /^Общий/ }).click();
  await expect(history).not.toContainText("турнир лучников");
  await setPlacement("Также в текущем канале");
  await expect(history).toContainText("турнир лучников");
  await expect(history).toContainText("Кто-нибудь сейчас в Вайтране?");
  await expect(history).not.toContainText("Давайте через перевал");
});

test("a refused announcement of another mod can be dismissed but not retried", async ({
  page,
}) => {
  await page.getByRole("button", { name: "Отказ объявления мода" }).click();
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  const row = page.locator('[data-part="pending-message"]');
  await expect(row).toHaveAttribute("data-status", "failed");
  await expect(row).toContainText("[Объявления] Carriage Tours: Карета");
  await expect(row).toContainText("Не отправлено: Слишком частые объявления");
  await expect(row).not.toContainText("Вы:");
  await expect(
    row.getByRole("button", { name: "Повторить", exact: true }),
  ).toHaveCount(0);
  await row.getByRole("button", { name: "Убрать статус сообщения" }).click();
  await expect(row).toHaveCount(0);
});

test("bubble style and ground mark preferences save and restore", async ({
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
  await expect(
    page.getByLabel("Рамка сообщения", { exact: true }),
  ).toBeChecked();
  await expect(
    page.getByLabel("Цвет текста сообщения", { exact: true }),
  ).toHaveValue("#EEECE5");
  await page.getByLabel("Рамка сообщения", { exact: true }).uncheck();
  await page
    .getByLabel("Цвет текста сообщения", { exact: true })
    .fill("#ff8800");
  await page
    .getByLabel("Цвет текста места смерти", { exact: true })
    .fill("zzz");
  await page.getByLabel("Показывать надписи", { exact: true }).uncheck();
  await page.getByLabel("Скрывать метки", { exact: true }).check();
  await expect(
    page.getByLabel("Дата над местом смерти", { exact: true }),
  ).toBeChecked();
  await page.getByLabel("Дата над надписью", { exact: true }).check();
  await page.getByRole("combobox", { name: "Календарь дат" }).click();
  await page.getByRole("option", { name: /Привычный/ }).click();
  await page.getByLabel("Цвет даты", { exact: true }).fill("#112233");
  await page.getByRole("combobox", { name: "Надписей рядом" }).click();
  await page.getByRole("option", { name: "32" }).click();
  await page.getByRole("slider", { name: "Дальность текста" }).fill("300");
  await page
    .getByRole("slider", { name: "Высота светлячка над землёй" })
    .fill("90");
  await page.getByRole("button", { name: "Сохранить настройки" }).click();
  await expect(
    page.getByRole("status", { name: "Результат операции" }),
  ).toContainText("сохранены");
  await page
    .getByRole("group", { name: "Метки на земле" })
    .scrollIntoViewIfNeeded();
  await page.screenshot({ path: "test-results/ground-mark-settings.png" });
  await page.reload();
  await openSettings();
  await expect(
    page.getByLabel("Рамка сообщения", { exact: true }),
  ).not.toBeChecked();
  await expect(
    page.getByLabel("Цвет текста сообщения", { exact: true }),
  ).toHaveValue("#FF8800");
  // An invalid colour never replaces the saved one.
  await expect(
    page.getByLabel("Цвет текста места смерти", { exact: true }),
  ).toHaveValue("#D9534F");
  await expect(
    page.getByLabel("Показывать надписи", { exact: true }),
  ).not.toBeChecked();
  await expect(
    page.getByLabel("Скрывать метки", { exact: true }),
  ).toBeChecked();
  await expect(
    page.getByRole("combobox", { name: "Надписей рядом" }),
  ).toHaveAttribute("data-value", "32");
  await expect(
    page.getByRole("slider", { name: "Дальность текста" }),
  ).toHaveValue("300");
  await expect(
    page.getByRole("slider", { name: "Высота светлячка над землёй" }),
  ).toHaveValue("90");
  await expect(
    page.getByLabel("Дата над надписью", { exact: true }),
  ).toBeChecked();
  await expect(
    page.getByRole("combobox", { name: "Календарь дат" }),
  ).toHaveAttribute("data-value", "earth");
  await expect(page.getByLabel("Цвет даты", { exact: true })).toHaveValue(
    "#112233",
  );
});

test("leave here places the draft as a ground note and shows a refusal", async ({
  page,
}) => {
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  const input = page.getByRole("textbox", { name: "Сообщение" });
  const leave = page.getByRole("button", { name: "Оставить здесь" });
  await expect(leave).toBeDisabled();
  await input.fill("Осторожно, тролль");
  await expect(leave).toBeEnabled();
  await leave.click();
  const row = page.locator('[data-part="pending-message"]');
  await expect(row).toHaveAttribute("data-kind", "note");
  await expect(row).toContainText("[Метка] Вы: Осторожно, тролль");
  await expect(row).toHaveCount(0);
  await expect(page.locator('[data-part="messages"]')).not.toContainText(
    "Осторожно, тролль",
  );
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await expect(input).toHaveValue("");
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await page.getByRole("button", { name: "Метки", exact: true }).click();
  await expect(page.getByLabel("Мои метки")).toContainText("Осторожно, тролль");
  await page.getByRole("button", { name: "Закрыть панель" }).click();
  await page.keyboard.press("Escape");

  await page
    .getByRole("button", { name: "Отклонить следующую отправку" })
    .click();
  await input.fill("Ещё одна");
  await leave.click();
  await expect(page.getByRole("status")).toContainText(
    "Не оставлено: Здесь уже слишком много меток",
  );
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await expect(row).toHaveAttribute("data-status", "failed");
  await page.screenshot({ path: "test-results/ground-note-refused.png" });
  await page.getByRole("button", { name: "Повторить", exact: true }).click();
  await expect(row).toHaveCount(0);
});

test("my marks lists own marks and removes one", async ({ page }) => {
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await page.getByRole("button", { name: "Метки", exact: true }).click();
  const list = page.getByLabel("Мои метки");
  await expect(list.getByRole("listitem")).toHaveCount(2);
  await expect(list).toContainText("Место смерти");
  await expect(list).toContainText("Убийца: Морозный тролль");
  // The in-game date as the host formatted it, next to the real time.
  await expect(list).toContainText("Миддас, 18 Последнего зерна 4Э 201, 02:40");
  // The far death mark is listed with its place, without walking up to it.
  await expect(list).toContainText("skyrim.esm:016BB4 · 20140, 3300, 610");
  const nearby = page.getByLabel("Метки рядом");
  await expect(nearby.getByRole("listitem")).toHaveCount(3);
  await expect(nearby).toContainText("Мира");
  await expect(nearby).toContainText("Эйра");
  await expect(nearby).toContainText("Сундук за водопадом");
  await expect(nearby.locator("img")).toHaveCount(0);
  await page.screenshot({ path: "test-results/my-marks.png" });
  await page.getByRole("button", { name: "Удалить метку 302" }).click();
  await expect(
    page.getByRole("status", { name: "Результат операции" }),
  ).toContainText("Метка удалена");
  await expect(list.getByRole("listitem")).toHaveCount(1);
  await expect(list).not.toContainText("Морозный тролль");
});

test("hide my name waits for the server, shows the pseudonym, survives reload and a refusal", async ({
  page,
}) => {
  await page.evaluate(() =>
    localStorage.removeItem("dreamsleeve.ui.hideIdentity"),
  );
  await page.reload();
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await page.getByRole("button", { name: "Настройки", exact: true }).click();
  const hide = page.getByRole("combobox", {
    name: "Скрывать моё имя от других игроков",
  });
  const choose = async (option: string) => {
    await hide.click();
    await page.getByRole("option", { name: option }).click();
  };
  const status = page.getByRole("status", { name: "Скрытое имя" });
  await expect(status).toHaveText("Другие игроки видят ваше имя");
  await expect(hide).toHaveAttribute("data-value", "off");
  // Independent of the local switch, which stays off.
  await expect(
    page.getByLabel("Скрывать чужие имена (только у меня)"),
  ).not.toBeChecked();
  await choose("Везде, включая метки на земле");
  await expect(status).toHaveText("Ожидание сервера…");
  await expect(hide).toBeDisabled();
  await expect(status).toHaveText("Другие видят вас как „Страж 4“");
  await expect(hide).toHaveAttribute("data-value", "everywhere");
  await expect(
    page.getByLabel("Скрывать чужие имена (только у меня)"),
  ).not.toBeChecked();
  // Marks back under the real name: the same pseudonym elsewhere.
  await choose("Везде, кроме меток на земле");
  await expect(status).toHaveText(
    "Другие видят вас как „Страж 4“; в метках на земле — ваше имя",
  );
  await page.getByRole("button", { name: "Аккаунт", exact: true }).click();
  await expect(page.locator('[data-part="account-identity"]')).toHaveText(
    "Другие видят вас как „Страж 4“; в метках на земле — ваше имя",
  );

  // The choice is kept for the next session, which gets a new pseudonym.
  await page.reload();
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await page.getByRole("button", { name: "Настройки", exact: true }).click();
  await expect(hide).toHaveAttribute("data-value", "exceptGroundMarks");
  await expect(status).toHaveText(
    "Другие видят вас как „Страж 3“; в метках на земле — ваше имя",
  );

  // A server that does not allow hidden names refuses; nothing changes.
  await choose("Нет");
  await expect(status).toHaveText("Другие игроки видят ваше имя");
  // The workshop control sits under the open workspace.
  await page
    .getByRole("button", { name: "Сервер разрешает скрытое имя: переключить" })
    .dispatchEvent("click");
  await choose("Везде, включая метки на земле");
  await expect(page.getByRole("alert")).toHaveText(
    "Сервер не разрешает скрывать имя",
  );
  await expect(hide).toHaveAttribute("data-value", "off");
  await expect(status).toHaveText("Другие игроки видят ваше имя");
});

test("a player who hides their name is marked in chat, online and the author menu", async ({
  page,
}) => {
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  const row = page
    .locator('[data-part="message"][data-pseudonymous]')
    .filter({ hasText: "Кто-то видел дракона" });
  await expect(row).toHaveCount(1);
  await expect(row.getByRole("button", { name: "Страж 2:" })).toHaveAttribute(
    "title",
    "Имя скрыто игроком",
  );
  await expect(
    page.locator('[data-part="message"][data-pseudonymous]'),
  ).toHaveCount(1);
  await row
    .getByRole("button", { name: "Страж 2:" })
    .click({ button: "right" });
  const menu = page.getByRole("menu", { name: "Действия: Страж 2" });
  await expect(menu).toContainText("имя скрыто игроком");
  // Ignore works by account ID as for anyone.
  await expect(
    menu.getByRole("menuitem", { name: "Игнорировать" }),
  ).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(page.getByRole("menu", { name: "Действия: Мира" })).toHaveCount(
    0,
  );

  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await page.getByRole("button", { name: "Список", exact: true }).click();
  const hidden = page.getByRole("table").locator("button[data-pseudonymous]");
  await expect(hidden).toHaveCount(1);
  await expect(hidden).toContainText("Страж 2");
  await expect(hidden).toContainText("Имя скрыто игроком");
  await page.getByRole("button", { name: "Карточки", exact: true }).click();
  await expect(page.locator("h3[data-pseudonymous]")).toHaveText("Страж 2");
});

test("the own display name changes in the account panel once the server answers", async ({
  page,
}) => {
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await page.getByRole("button", { name: "Аккаунт", exact: true }).click();
  const status = page.getByRole("status", { name: "Смена имени" });
  await expect(status).toHaveText(/^Сейчас: „.+“$/);
  const field = page.getByLabel("Отображаемое имя", { exact: true });
  const change = page.getByRole("button", { name: "Сменить имя" });
  await expect(change).toBeDisabled();
  // The workshop stands in for the server: its word list refuses, nothing changes.
  await field.fill("Sir Badword");
  await change.click();
  await expect(status).toHaveText("Имя содержит запрещённые слова");
  await field.fill("Новое Имя");
  await change.click();
  await expect(status).toHaveText("Ожидание сервера…");
  await expect(status).toHaveText("Имя изменено на „Новое Имя“");
  await expect(field).toHaveValue("");
  // A second change within the interval is refused with the wait.
  await field.fill("Ещё Одно");
  await change.click();
  await expect(status).toHaveText("Имя можно сменить снова через 1 мин");
});

test("the account tab picks the route to the server and shows the one in use", async ({
  page,
}) => {
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await page.getByRole("button", { name: "Аккаунт", exact: true }).click();
  const status = page.getByRole("status", { name: "Маршрут" });
  await expect(status).toHaveText(
    "Сейчас через: Основной. Если маршрут не отвечает, игра сама пробует следующий.",
  );
  const route = page.getByRole("combobox", { name: "Маршрут к серверу" });
  await expect(route).toHaveAttribute("data-value", "");
  await route.click();
  await page.getByRole("option", { name: "Прокси" }).click();
  // The workshop answers after a moment, like a route that connects.
  await expect(status).toHaveText("Сейчас через: Прокси.");
  await route.click();
  await page.getByRole("option", { name: "Автоматически" }).click();
  await expect(route).toHaveAttribute("data-value", "");
});

test("names are drawn in their color; the own color changes in the settings once the server answers", async ({
  page,
}) => {
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await expect(
    page
      .locator('[data-part="messages"] button', { hasText: "Седобородый:" })
      .first(),
  ).toHaveCSS("color", "rgb(255, 213, 79)");
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await page.getByRole("button", { name: "Настройки", exact: true }).click();
  const picker = page.locator('[data-part="name-color"]');
  const status = page.getByRole("status", { name: "Смена цвета" });
  const apply = page.getByRole("button", { name: "Применить цвет" });
  // The current color is chosen; nothing to apply yet.
  await expect(picker.getByRole("button", { name: "#64B5F6" })).toHaveAttribute(
    "aria-pressed",
    "true",
  );
  await expect(apply).toBeDisabled();
  await picker.getByRole("button", { name: "#FF8A65" }).click();
  await expect(picker.locator("b")).toHaveCSS("color", "rgb(255, 138, 101)");
  await apply.click();
  await expect(status).toHaveText("Ожидание сервера…");
  await expect(status).toHaveText("Цвет сохранён");
  await expect(apply).toBeDisabled();
  // The workshop stands in for the server: a dark color is refused.
  await picker.getByLabel("Свой цвет").fill("#101010");
  await apply.click();
  await expect(status).toHaveText(
    "Цвет слишком тёмный: имя будет плохо видно в чате",
  );
  await picker.getByRole("slider", { name: "Оттенок" }).fill("200");
  await apply.click();
  await expect(status).toHaveText("Цвет можно сменить снова через 10 с");
  await picker.scrollIntoViewIfNeeded();
  await page.screenshot({ path: "test-results/name-color.png" });
});

test("fireflies and marks of guildmates only save and restore", async ({
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
  const fireflies = page.getByLabel("Только игроки из ваших гильдий");
  const marks = page.getByLabel("Только от игроков из ваших гильдий");
  await expect(fireflies).not.toBeChecked();
  await expect(marks).not.toBeChecked();
  await fireflies.check();
  await marks.check();
  await page.getByRole("button", { name: "Сохранить настройки" }).click();
  await page.reload();
  await openSettings();
  await expect(fireflies).toBeChecked();
  await expect(marks).toBeChecked();
});

test("guilds have their tabs and send target, a panel by role, invitations and a guild mute", async ({
  page,
}) => {
  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  const tabs = page.getByRole("navigation", { name: "Каналы" });
  await expect(tabs.getByRole("button", { name: "Гильдии" })).toBeVisible();
  await expect(tabs.getByRole("button", { name: "Соратники" })).toBeVisible();
  await page.getByRole("combobox", { name: "Канал отправки" }).click();
  await expect(
    page.getByRole("listbox", { name: "Канал отправки" }),
  ).toContainText("Гильдия «Вороны»");
  await page.getByRole("option", { name: "Гильдия «Соратники»" }).click();
  const input = page.getByRole("textbox", { name: "Сообщение" });
  await input.fill("Сбор у Йоррваскра");
  await input.press("Enter");
  await expect(
    page.locator('[data-part="message"][data-channel="guild"]').filter({
      hasText: "Сбор у Йоррваскра",
    }),
  ).toContainText("[Соратники]");

  await page.getByRole("button", { name: "Открыть чат · Enter" }).click();
  await page.getByRole("button", { name: "Открыть меню Dreamsleeve" }).click();
  await page
    .getByRole("navigation", { name: "Разделы меню" })
    .getByRole("button", { name: /Гильдии/ })
    .click();
  const invites = page.getByRole("region", { name: "Приглашения" });
  await expect(invites).toContainText("СеребрянаяРука");
  await expect(invites).toContainText("видят ваше настоящее имя");
  const members = page.getByRole("list", {
    name: "Участники гильдии Соратники",
  });
  const mira = members.getByRole("listitem").filter({ hasText: "Мира" });
  await expect(mira).toContainText("офицер");
  await mira.getByRole("button", { name: "Мут…" }).click();
  const dialog = page.getByRole("dialog", { name: "Мут в гильдии: Мира" });
  await dialog.getByLabel("Причина").fill("Флуд");
  await dialog.getByRole("button", { name: "Замутить" }).click();
  await expect(dialog).toHaveCount(0);
  // The first term is fifteen minutes.
  await expect(mira).toContainText(/мут до .+: Флуд/);

  await invites.getByRole("button", { name: "Вступить" }).click();
  await expect(
    page.getByRole("group", { name: "Выбор гильдии" }),
  ).toContainText("СеребрянаяРука");
  await expect(
    page.getByRole("status", { name: "Результат операции" }),
  ).toHaveText("Вы вступили в гильдию «СеребрянаяРука»");
  await expect(invites).toHaveCount(0);
  // A member of another guild may leave; the master is told how to.
  await page
    .getByRole("group", { name: "Выбор гильдии" })
    .getByRole("button", { name: "Соратники" })
    .click();
  await expect(page.getByRole("region", { name: "Мои гильдии" })).toContainText(
    "Чтобы выйти, передайте роль главы",
  );
});
