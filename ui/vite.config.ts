import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Built into the Host's wwwroot; dev server proxies /system, /api, /mcp to the backend.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: "../src/EzOdata.Host/wwwroot",
    emptyOutDir: true,
  },
  server: {
    proxy: {
      "/system": "http://localhost:5299",
      "/api": "http://localhost:5299",
      "/mcp": "http://localhost:5299",
    },
  },
});
