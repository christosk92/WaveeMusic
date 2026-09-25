# Crash dashboard — a real Fluent 2 app, from scratch

Companion to `crash-diagnostics-implementation.md` §C. The first build is discarded entirely (`ops/crash/dashboard`
is deleted and recreated). Verdict on it, 2026-09-24: the nav, cards, charts, breadcrumbs, titles, command rows,
loading/error/empty states were hand-rolled `<div>`s; nothing had hover/pressed/focus states; the browser's default
8 px body margin and white background framed a dark app; the whole document scrolled because there was no app frame.
It looked like a picture of an app. This plan specifies a Microsoft-grade Fluent 2 web app: shell first, then scenes.

## 0. Non-negotiables (the definition of done)
1. **Every control is a stock `@fluentui/react-components` v9 (or `@fluentui/react-charts`) component.** A `<div>` may
   only lay things out (`makeStyles` grid/flex with `tokens`); it never draws a control, a card, a chart, a badge, a
   title or a state. `grep -rn "boxShadow\|borderRadius: tokens\|border: \`1px" src` finds nothing outside the log body.
2. **Shell fundamentals:** no body margin; `html, body, #root` are 100 % height; `body` has `overflow: hidden`; the
   app frame is the viewport; the nav never scrolls; `<main>` is the ONLY scroll container; the page header is sticky
   inside it; no horizontal scroll at 1280 px; the canvas colour is `colorNeutralBackground2`, cards `Background1`.
3. **Everything reacts:** hover, pressed, focus-visible ring, selected, disabled come from the stock components
   (`NavItem`, `Button`, `ToolbarButton`, `DataGridRow`, `Card` with `focusMode`, `Tab`, `Link`). Keyboard-only use of
   every page works (Tab order, Enter/Space, arrow keys in grids and menus, Esc closes overlays).
4. **Every scene has four states** — loading (`Skeleton` shaped like the final layout), empty (`EmptyState`
   composition), error (`MessageBar intent="error"` with Retry), ready — and mock mode can show each on demand.
5. **Light and dark** both correct, following the OS by default with a persisted override; icons and charts use tokens.
6. **Verified by screenshots**, not by claims: `npm run shots` renders every page × state × theme at 1280 and 1920 px
   with Playwright (Edge channel) into `shots/`; the orchestrator reviews them before calling it done.

## 1. Stack
- Vite 5 · React 18 · TypeScript strict · `@fluentui/react-components` ^9.74 · `@fluentui/react-icons` ·
  `@fluentui/react-charts` ^9.3 (v9-native charts; never `@fluentui/react-charting`) · `react-router-dom` 6 (data
  router, `createBrowserRouter`, lazy routes) · `@tanstack/react-query` 5 (loading/error/refetch/caching for every
  fetch; no hand-rolled `useEffect` fetching) · vitest + `@testing-library/react` · Playwright (`channel: "msedge"`,
  devDependency, screenshots only).
- Folder layout:
  ```
  src/
    main.tsx            providers: FluentProvider (theme) · QueryClientProvider · RouterProvider · Toaster
    app/                router.tsx (routes), AppFrame.tsx (shell), theme.ts (useTheme: os + override), global.ts (static styles)
    api/                client.ts (fetch + Access cookie), types.ts (Wire* + App*), mapping.ts, mock.ts, hooks.ts (useStats, useIssues, …)
    scene/              PageHeader.tsx, PageBody.tsx, Panel.tsx (Card + CardHeader wrapper), EmptyState.tsx, ErrorBar.tsx,
                        skeletons.tsx (StatsSkeleton, ChartSkeleton, GridSkeleton, FactsSkeleton, DetailSkeleton), toast.ts
    pages/              Overview/, Issues/, IssueDetail/, Reports/, ReportDetail/, Versions/, Symbols/  (index.tsx + parts)
    lib/                format.ts (ids, bytes, relative time), colors.ts (kind → Badge color / chart color tokens)
  shots/                Playwright script + output (gitignored output)
  ```

## 2. Shell

### 2.1 Global styles (`app/global.ts`, `makeStaticStyles`)
```ts
makeStaticStyles({
  "html, body, #root": { margin: 0, padding: 0, height: "100%" },
  body: { overflow: "hidden", fontFamily: tokens.fontFamilyBase, WebkitFontSmoothing: "antialiased" },
  "*, *::before, *::after": { boxSizing: "border-box" },
  "::selection": { backgroundColor: tokens.colorBrandBackground2 },
});
```
`index.html`: `<meta name="color-scheme" content="light dark">`, `<meta name="viewport" …>`, title "Wavee crashes".
`FluentProvider` gets `style={{ height: "100%", backgroundColor: tokens.colorNeutralBackground2, color: tokens.colorNeutralForeground1 }}`
and `applyStylesToPortals`.

### 2.2 Theme (`app/theme.ts`)
`useTheme()` → `webLightTheme` | `webDarkTheme` from `prefers-color-scheme` unless `localStorage["theme"]` is
`light|dark`; exposes `mode`, `setMode(auto|light|dark)`. The toggle lives in the nav footer as a `Menu` (Auto /
Light / Dark) on a `MenuButton` with a `Tooltip` "Theme". Charts receive the theme through the provider.

### 2.3 App frame (`app/AppFrame.tsx`)
```
┌ FluentProvider (100vh) ──────────────────────────────────────────────────────────────┐
│ ┌ NavDrawer type="inline" open size="medium" (260 px, full height, never scrolls*) ┐ ┌ main (flex:1, min-width:0, overflow:auto, scrollbar-gutter:stable) ┐ │
│ │ NavDrawerHeader: <Hamburger/> (Tooltip "Collapse navigation")                    │ │ PageHeader (position:sticky; top:0; z:1; canvas background; padding XXL/XL)        │ │
│ │ NavDrawerBody:                                                                    │ │   Breadcrumb (nested pages) · Title2 · Body1 subtitle · Toolbar (command bar)        │ │
│ │   AppItem icon=<Pulse24Regular/> secondary "crash.wavee.app" → "Wavee crashes"    │ │ PageBody (padding 0 XXL XXL; grid 12 cols; gap L; max-width 1600; margin auto)      │ │
│ │   NavSectionHeader "Monitor"  NavItem Overview · Issues (+CounterBadge) · Reports │ │   … Panels …                                                                        │ │
│ │   NavSectionHeader "Manage"   NavItem Versions · Symbols                          │ └───────────────────────────────────────────────────────────────────────────────────┘ │
│ │ NavDrawerFooter: Divider · theme MenuButton · Persona "Wavee" "Cloudflare Access" │                                                                                         │
│ └──────────────────────────────────────────────────────────────────────────────────┘                                                                                         │
└──────────────────────────────────────────────────────────────────────────────────────┘
```
(*) `NavDrawerBody` scrolls on its own only if items overflow; the body never does. Below 640 px the drawer becomes
`type="overlay"` with the `Hamburger` moved into the PageHeader. Nav selection is derived from the route (`useLocation`
→ `selectedValue`), items navigate via `useNavigate`; `NavItem`s carry `href` so middle-click works.

### 2.4 Scene grammar (`scene/*`)
- `PageHeader { crumbs?, title, subtitle?, commands?: ToolbarProps children, filters?: children }` → `Breadcrumb size="small"`
  (`BreadcrumbItem` + `BreadcrumbButton` + `BreadcrumbDivider`), `Title2`, `Body1` in `colorNeutralForeground3`, one
  `Toolbar aria-label="…" size="medium"` with `ToolbarGroup`s: primary commands (icon + text `ToolbarButton`s) ·
  `ToolbarDivider` · filters (`Dropdown`, `SearchBox`) · `ToolbarDivider` · right group (`ToolbarButton` Refresh with
  `Tooltip`, overflow `Menu`). Narrow widths use `Overflow` + `useOverflowMenu`.
- `PageBody` = the 12-column grid; children set `gridColumn: span n`.
- `Panel { title, description?, action?, footer?, span, children }` = `Card` (`appearance="filled"`, `size="medium"`)
  + `CardHeader header={<Body1Strong/>} description={<Caption1/>} action={…}` + body + optional `CardFooter`.
  Clickable panels use `Card focusMode="tab-exit"` + `onClick`.
- `EmptyState { icon, title, hint?, action? }` = 48 px icon · `Body1Strong` · `Caption1` · `Button`.
- `ErrorBar { error, onRetry }` = `MessageBar intent="error"` · `MessageBarBody` (`MessageBarTitle` + message) ·
  `MessageBarActions` (Retry `Button`).
- Skeletons: one `<Skeleton animation="wave">` per page wrapping `SkeletonItem`s shaped like the final layout.
- Toast: `useAppToast()` over `useToastController(APP_TOASTER_ID)`; `Toast` + `ToastTitle`/`ToastBody`; intents success/error.
- Typography: page title `Title2`; panel title `Body1Strong`; meta `Caption1`; numbers `LargeTitle`; code `Text font="monospace"`.

## 3. Scenes
| Page | Header | Body (12-col) |
|---|---|---|
| Overview | Title "Overview" · subtitle "{range} · {version filter}" · Toolbar: [Refresh] ‖ Dropdown range (7/30/90 d) · Dropdown version · Dropdown channel | 5 stat `Panel`s (span 12/5 → `gridTemplateColumns: repeat(5,1fr)` sub-grid): `Caption1` label, `LargeTitle` value, `Caption1` delta (+`Badge` tint when it matters) · `Panel` "Reports per day" span 8 with `VerticalStackedBarChart` (crash/hang/closed, `Legends`) · `Panel` "By kind" span 4 `DonutChart` · `Panel` "Top issues" span 8 `DataGrid` (sortable, row click) + `CardFooter` Link "See all issues" · `Panel` "By version" span 4 `HorizontalBarChart`, header `InfoLabel` "Counts only — no active-user denominator" · `Panel` "Where it happened" span 6 `Table` · `Panel` "Symbols" span 6 `Badge`s per arch + `CardFooter` Link |
| Issues | Title "Issues" · Toolbar: [Refresh] ‖ SearchBox · Dropdown kind · Dropdown status · Dropdown version · Dropdown range ‖ (when selection) [Mark resolved] [Ignore] | `Panel` span 12 with `DataGrid` (`selectionMode="multiselect"`, `DataGridSelectionCell`, sortable, resizable columns, `Overflow`-aware), row click → detail; GridSkeleton(8 rows) |
| Issue detail | Breadcrumb Issues › {id} · Title2 monospace exception · function · `Badge` kind · Body1 meta · Toolbar: [Open GitHub issue] [Mark resolved] ‖ Dropdown status ‖ overflow Menu (Copy fingerprint · Delete issue) | `MessageBar intent="error"` exception message (span 12) · `TabList` Stack / Occurrences / Environment / Log tail (span 12) · main span 8: frames `Table` (size small, monospace names, RVA, module) or occurrences `DataGrid` (→ report) or environment `Table` · aside span 4: `Panel` "Last 14 days" `Sparkline` · `Panel` per breakdown `HorizontalBarChart` · `Panel` "GitHub" (linked `Link` or the existing `Dialog`) |
| Reports | Title "Reports" · Toolbar: [Refresh] ‖ SearchBox (report / install id) · Dropdown kind · Dropdown version · Dropdown range | `Panel` span 12 `DataGrid` over `GET /v1/reports` (received · id · kind · version · arch · gpu · install · dump) → detail |
| Report detail | Breadcrumb Issues › issue › {id} · Title2 "Report {id}" · `Badge` kind · Body1 received · Toolbar: [Download bundle] ‖ [Delete report] (danger, own group) ‖ overflow | span 8: facts `Table` (2 key/value pairs per row) · `TabList` report.txt / log-tail · log viewer `Panel` with its own `Toolbar` (`SearchBox` find, Copy, wrap `ToolbarToggleButton`) and the monospace body (the one custom surface) · aside span 4: `Panel` "Memory snapshot" (`CardFooter` Download, `<pre>` cdb line) · `Panel` "This install" (`Text font="monospace"` id, stats; `CardFooter` **"Delete this install's data…"** → `Dialog` confirm → `DELETE /v1/installs/:id` → Toast) · `Panel` "Privacy check" (3 compact `MessageBar intent="success"`) |
| Versions / Symbols | Title · Toolbar [Refresh] | `Panel` span 12 `Table` (`Badge` uploaded/missing on Symbols) |

## 4. Data
- `api/client.ts`: `fetchJson(path)` with `credentials: "include"` (Access cookie), `VITE_API_BASE`; errors typed.
- `api/hooks.ts`: `useStats(range)`, `useIssues(filters)`, `useIssue(fp)`, `useReports(filters)`, `useReport(id)`,
  `useVersions()`, `useSymbols()`, mutations `usePatchIssue`, `useDeleteInstall`, `useDeleteReport` — all react-query.
- `api/mock.ts`: the prototype data; `VITE_MOCK=1`; `VITE_MOCK_STATE=loading|empty|error` forces a state (loading =
  never resolves; empty = zero rows; error = rejects) so every state is reviewable; `VITE_MOCK_DELAY` ms for realism.
- Worker routes (in `ops/crash/worker`): `/v1/stats`, `/v1/issues`, `/v1/issues/:fp` (+frames, occurrences,
  sparkline14d, breakdowns), `/v1/reports` (list), `/v1/reports/:id` (+this_install, debug_id), `/v1/reports/:id/:part`,
  `/v1/versions`, `/v1/symbols`, `PATCH /v1/issues/:fp`, `DELETE /v1/installs/:id`, `DELETE /v1/reports/:id`.

## 5. Verification
- `npm run build` (tsc strict + vite) clean; `npm test` (mapping, format, one render smoke per page in mock mode).
- `npm run shots`: Playwright (system Edge channel; `playwright install` cannot download Chromium on the ARM64 box)
  starts its own Vite server in mock mode on port **5174** (so the interactive `npm run dev` on 5173 stays up), visits
  every route × state × theme at 1280×800 and 1920×1080 plus every route once at 900 and 600 px wide, saves
  `shots/out/<page>-<state>-<theme>-<w>.png`, and fails on any console error or horizontal overflow
  (`document.documentElement.scrollWidth > innerWidth`). States are one-shot URL overrides (`?mockState=`, `?theme=`);
  detail routes get ready/loading/error plus a `*-missing` not-found shot, since their hooks never poll (no
  `refetching`) and their empty state *is* not-found. The script waits on the real state signal, not a timer:
  `.fui-Skeleton` detached → `.fui-ProgressBar` present for `refetching`, the Retry button for `error` (react-query
  retries once with a 1 s delay first), and it renders with `reducedMotion: "reduce"` so no drawer/enter motion is
  mid-flight. Ignored on purpose: "Keyborg instance … disposed incorrectly", tabster's dev-only StrictMode complaint.
- The orchestrator reviews the PNGs against §0 before reporting done, and keeps `npm run dev` (mock) running for the user.

## 6. Work packages (one agent, sequential, plan-driven; the orchestrator screenshots between steps)
1. Scaffold + shell (§1, §2) with an empty Overview showing all four states via mock — screenshot gate.
2. Overview + charts. 3. Issues + Issue detail. 4. Reports + Report detail (+ erasure). 5. Versions, Symbols,
responsive, a11y pass, README ("Fluent 2 conventions": surface → component table).

### 6.1 Status (2026-09-25)
All five packages landed: shell + scene layer + data layer (WP1), Overview (WP2), Issues + Issue detail (WP3),
Reports + Report detail with the GDPR install-erasure dialog (WP4), Versions + Symbols + README (WP5). Gates:
`tsc -b` clean, `vitest` 9 files / 49 tests, `npm run build` clean (Vite's advisory >500 kB main-chunk note only),
`npm run shots` 150 screenshots with no console errors and no horizontal overflow at 1920/1280/900/600 px, PNGs
reviewed against §0. Deviations from §3, all deliberate: the Issues grid uses a local selection set with a stock
`Checkbox` column instead of `DataGridSelectionCell` (the stock cell toggles selection on *every* row click, which
fights row-click-to-detail); Issue detail's "Log tail" tab is always its empty state (no log-tail field on the issue
wire shape); the Report detail's "Download bundle" is a `Menu` over the Worker's real `report`/`tail`/`dump` parts;
"Delete report" calls the documented client stub until the Worker grows `DELETE /v1/reports/:id`. Below 900 px every
`Panel` and both detail-page columns collapse to full width; the stat row reflows with `auto-fit`; under 640 px the
drawer is an overlay, closed by default, opened from a hamburger bar above `<main>`.

## 7. Loading convention (user rule, 2026-09-25)
Skeleton shimmer only on a page's initial load (no data yet). Every later refetch (filter or range change, Refresh,
pagination, mutation invalidation) keeps the current content in place and shows an indeterminate `ProgressBar` pinned
at the top of the page body, Microsoft-style; stale content dims slightly with `aria-busy`. Implemented once in the
scene layer from react-query's `isLoading` (initial) vs `isFetching && !isLoading` (refetch). Mock state `refetching`.
