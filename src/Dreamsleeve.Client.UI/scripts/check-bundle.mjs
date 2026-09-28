import { readdir, readFile } from "node:fs/promises";
async function inspect(path) {
  for (const item of await readdir(path, { withFileTypes: true })) {
    const file = `${path}/${item.name}`;
    if (item.isDirectory()) {
      await inspect(file);
      continue;
    }
    const text = await readFile(file, "utf8");
    for (const forbidden of [
      "UI WORKSHOP",
      "БРАУЗЕРНЫЙ СТЕНД",
      "dreamsleeve.ui.settings",
      "Отклонить следующую отправку",
      "localStorage",
      "workshop-controls",
    ])
      if (text.includes(forbidden))
        throw new Error(`Development content in ${file}: ${forbidden}`);
  }
}
await inspect("dist");
console.log(
  "Game bundle contains no development fixtures, storage or workshop.",
);
