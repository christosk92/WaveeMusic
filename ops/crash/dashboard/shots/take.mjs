// Playwright (system Edge — never `npx playwright install`, its Chromium download fails on this
// Windows-ARM64 box) screenshot pass over every route x mock state x theme x viewport (plan §0.6, §5,
// §9). One Vite dev server, started here with VITE_MOCK=1, serves every combination; the mock state and
// theme are one-shot overrides via `?mockState=` / `?theme=` (see src/api/mock.ts, src/app/theme.ts) so
// we never have to restart the server between shots. It listens on its own port (5174) so the
// interactive `npm run dev` server on 5173 can stay up while the shots run.
import { chromium } from "playwright";
import { createServer } from "vite";
import { mkdir, rm } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(__dirname, "..");
const outDir = path.join(__dirname, "out");
const SHOTS_PORT = 5174;

// Every route × every state it can show. Detail routes have no background poll (their hooks set no
// refetchInterval) so `refetching` has nothing to show there, and `empty` is their not-found state,
// which the `*-missing` routes cover explicitly. Ids are the deterministic mock ones (src/api/mock.ts:
// `fp-<index>-<slug>` and `<fp>-r<hex>`).
const FP = "fp-00-nullreferenceexception-detail-ui-hero-render";
const RID = `${FP}-r000`;
const ALL_STATES = ["ready", "loading", "empty", "error", "refetching"];
const DETAIL_STATES = ["ready", "loading", "error"];
const ROUTES = [
  { path: "/", slug: "overview", states: ALL_STATES },
  { path: "/issues", slug: "issues", states: ALL_STATES },
  { path: `/issues/${FP}`, slug: "issue", states: DETAIL_STATES },
  { path: "/issues/missing-fp", slug: "issue-missing", states: ["ready"] },
  { path: "/reports", slug: "reports", states: ALL_STATES },
  { path: `/reports/${RID}`, slug: "report", states: DETAIL_STATES },
  { path: "/reports/missing-id", slug: "report-missing", states: ["ready"] },
  { path: "/versions", slug: "versions", states: ALL_STATES },
  { path: "/symbols", slug: "symbols", states: ALL_STATES },
];
const THEMES = ["light", "dark"];
const VIEWPORTS = [
  { width: 1280, height: 800 },
  { width: 1920, height: 1080 },
];
// Responsive check (plan §0.2 "no horizontal scroll", AppFrame's ≤640px overlay drawer): every route once,
// ready state, light theme only.
const NARROW_VIEWPORTS = [
  { width: 900, height: 900 },
  { width: 600, height: 900 },
];

const consoleErrors = [];
const overflowFailures = [];

function urlFor(routePath, state, theme) {
  const params = new URLSearchParams();
  if (state !== "ready") params.set("mockState", state);
  params.set("theme", theme);
  const qs = params.toString();
  return `${routePath}${qs ? `?${qs}` : ""}`;
}

async function shootOne(page, { route, state, theme, viewport }) {
  const routePath = route.path;
  const name = `${route.slug}-${state}-${theme}-${viewport.width}`;
  await page.setViewportSize(viewport);

  const pageErrors = [];
  const onConsole = (msg) => {
    if (msg.type() !== "error") return;
    // Dev-only: React StrictMode mounts twice, and Fluent's focus manager (tabster/keyborg) logs the
    // second dispose. Absent in production builds; not a page defect.
    if (msg.text().includes("Keyborg instance") && msg.text().includes("disposed incorrectly")) return;
    pageErrors.push(msg.text());
  };
  const onPageError = (err) => pageErrors.push(String(err));
  page.on("console", onConsole);
  page.on("pageerror", onPageError);

  // NOT "networkidle": Vite's dev client keeps an HMR WebSocket open for the page's whole lifetime, so
  // "no network activity for 500ms" never becomes true against a dev server and this would hang forever.
  await page.goto(`http://localhost:${SHOTS_PORT}${urlFor(routePath, state, theme)}`, { waitUntil: "load" });

  if (state === "loading") {
    // Deliberately never resolves (plan §4) — give the Skeleton a moment to paint, then shoot.
    await page.waitForTimeout(500);
  } else if (state === "refetching") {
    // First let the initial load land (the Skeleton root also carries role="progressbar", so a bare
    // role wait would fire on the loading state), then wait for the header's pinned ProgressBar from
    // the mock refetch interval (bounded, not a fixed-time race — see MOCK_REFETCH_INTERVAL_MS).
    await page.waitForSelector(".fui-Skeleton", { state: "detached", timeout: 8000 }).catch(() => {
      pageErrors.push(`refetching: initial load never finished for ${routePath}`);
    });
    await page.waitForSelector(".fui-ProgressBar", { timeout: 5000 }).catch(() => {
      pageErrors.push(`refetching: ProgressBar never appeared for ${routePath}`);
    });
  } else if (state === "error") {
    // react-query retries once (retryDelay 1 s) before it surfaces the error — wait for the
    // MessageBar's Retry button rather than racing that delay.
    await page.waitForSelector("text=Retry", { timeout: 8000 }).catch(() => {
      pageErrors.push(`error: ErrorBar never appeared for ${routePath}`);
    });
  } else {
    // The mock layer's own delay (default 250ms, src/api/mock.ts) — pad past it before shooting.
    await page.waitForTimeout(900);
  }

  const overflow = await page.evaluate(() => ({
    scrollWidth: document.documentElement.scrollWidth,
    innerWidth: window.innerWidth,
  }));
  if (overflow.scrollWidth > overflow.innerWidth) {
    overflowFailures.push(`${name}: scrollWidth ${overflow.scrollWidth} > innerWidth ${overflow.innerWidth}`);
  }

  await page.screenshot({ path: path.join(outDir, `${name}.png`), fullPage: false });
  console.log(`  ${name}.png`);

  page.off("console", onConsole);
  page.off("pageerror", onPageError);
  if (pageErrors.length > 0) {
    consoleErrors.push(...pageErrors.map((e) => `${name}: ${e}`));
  }
}

async function main() {
  await rm(outDir, { recursive: true, force: true });
  await mkdir(outDir, { recursive: true });

  process.env.VITE_MOCK = "1";
  const server = await createServer({
    root,
    server: { port: SHOTS_PORT, strictPort: true },
  });
  await server.listen();
  console.log(`Vite dev server (mock mode) listening on http://localhost:${SHOTS_PORT}`);

  const browser = await chromium.launch({ channel: "msedge", headless: true });
  // Fluent's motion respects prefers-reduced-motion; without this the NavDrawer's open transition
  // and every enter animation are mid-flight in the earliest shots.
  const context = await browser.newContext({ reducedMotion: "reduce" });
  context.setDefaultTimeout(15000);
  context.setDefaultNavigationTimeout(15000);
  const page = await context.newPage();

  try {
    for (const viewport of VIEWPORTS) {
      for (const theme of THEMES) {
        for (const route of ROUTES) {
          for (const state of route.states) {
            await shootOne(page, { route, state, theme, viewport });
          }
        }
      }
    }
    for (const viewport of NARROW_VIEWPORTS) {
      for (const route of ROUTES) {
        await shootOne(page, { route, state: "ready", theme: "light", viewport });
      }
    }
  } finally {
    await browser.close();
    await server.close();
  }

  const perTheme = ROUTES.reduce((n, r) => n + r.states.length, 0);
  const total = VIEWPORTS.length * THEMES.length * perTheme + NARROW_VIEWPORTS.length * ROUTES.length;
  console.log(`\nWrote ${total} screenshots to ${outDir}`);

  let failed = false;
  if (consoleErrors.length > 0) {
    failed = true;
    console.error("\nConsole errors:");
    for (const e of consoleErrors) console.error(`  ${e}`);
  }
  if (overflowFailures.length > 0) {
    failed = true;
    console.error("\nHorizontal overflow:");
    for (const e of overflowFailures) console.error(`  ${e}`);
  }
  if (failed) {
    process.exitCode = 1;
  } else {
    console.log("No console errors, no horizontal overflow.");
  }
}

main().catch((err) => {
  console.error(err);
  process.exitCode = 1;
});
