import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    globals: false,
    include: ["test/**/*.test.ts"],
    testTimeout: 30_000,
    // Conformance suites spin up containers which can take several minutes
    hookTimeout: 300_000,
    // Run test files sequentially to prevent concurrent container startup
    // from causing resource contention and test timeouts
    poolOptions: {
      threads: {
        singleThread: true,
      },
    },
  },
});
