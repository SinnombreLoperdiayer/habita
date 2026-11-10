import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { fileURLToPath, URL } from "node:url";
import { defineConfig } from "vite";

export default defineConfig({
  base: "./",
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
    },
  },
  build: { sourcemap: false },
  server: {
    host: "0.0.0.0",
    proxy: { "/api": `http://127.0.0.1:${process.env.BACKEND_PORT ?? 8100}` }, // API .NET (backend-dotnet)
  },
});
