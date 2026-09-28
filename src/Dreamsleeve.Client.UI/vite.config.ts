import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
export default defineConfig(({ mode }) => ({
  base: "./",
  plugins: [react()],
  build: {
    target: "es2022",
    cssTarget: "safari15",
    outDir: mode === "demo" ? "dist-demo" : "dist",
    rollupOptions: { input: mode === "demo" ? "demo.html" : "index.html" },
  },
  server: { open: false },
}));
