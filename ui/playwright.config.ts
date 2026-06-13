import { defineConfig } from "@playwright/test";

// Smoke tests run against a live host (built UI served from wwwroot).
// Start the host on :5299 before running: dotnet run --project ../src/EzOdata.Host
export default defineConfig({
  testDir: "./e2e",
  timeout: 30_000,
  use: {
    baseURL: process.env.EZ_BASE_URL ?? "http://localhost:5299",
    headless: true,
  },
  reporter: "list",
});
