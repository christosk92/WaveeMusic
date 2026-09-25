import { defineConfig } from "vitest/config";

// Plain vitest, node environment, with hand-written D1/R2 doubles (test/fixtures.ts) instead of
// @cloudflare/vitest-pool-workers. See the handback report for why: that pool needs workerd, whose
// native binary has no win32-arm64 build, and this box is Windows on ARM64. The doubles back D1 with
// real in-memory SQLite (better-sqlite3) against the actual schema.sql, so store.ts's SQL is exercised
// for real; R2 is a small in-memory Map. Anyone with an x64/Linux/macOS box wanting the real
// miniflare/workerd runtime can add @cloudflare/vitest-pool-workers later without changing src/.
export default defineConfig({
  test: {
    environment: "node",
    include: ["test/**/*.test.ts"],
  },
});
