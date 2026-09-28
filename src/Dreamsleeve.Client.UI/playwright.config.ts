import { defineConfig } from "@playwright/test";
export default defineConfig({
  testDir: "./tests/browser",
  use: {
    channel: process.env.UI_BROWSER_CHANNEL || undefined,
    baseURL: "http://127.0.0.1:5178",
    viewport: { width: 1440, height: 900 },
  },
  webServer: [
    {
      command: "npm run dev -- --port 5178 --strictPort",
      url: "http://127.0.0.1:5178/demo.html",
      reuseExistingServer: false,
    },
    {
      command: "npx vite preview --host 127.0.0.1 --port 5179 --strictPort",
      url: "http://127.0.0.1:5179",
      reuseExistingServer: false,
    },
  ],
});
