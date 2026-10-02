# Wavee crash dashboard

A Fluent 2 (Fluent UI React v9) web app for `ops/crash/worker`'s crash-reporting data — Vite + React +
TypeScript, served by that Worker itself as static assets on `https://crash.cproducts.dev`, next to its
`/v1/*` API and behind the same Cloudflare Access application as the `GET`/`PATCH` routes (no Cloudflare
Pages — `docs/plans/wavee/crash-hosting-implementation.md`, `ops/crash/README.md` steps 5–6). It is a
from-scratch rebuild per `docs/plans/wavee/crash-dashboard-implementation.md`: every visual control is a
stock `@fluentui/react-components`/`@fluentui/react-charts` component, one `<main>` scroll container, four
states (loading/empty/error/ready) on every scene, screenshot-verified in both themes.

All seven pages are real, routed in `src/app/router.tsx` and backed by `src/api/hooks.ts`: Overview,
Issues → Issue detail, Reports → Report detail, Versions, Symbols.

## Scripts

```powershell
npm install
npm run dev       # vite dev server, port 5173
npm run build     # tsc -b (strict) && vite build
npm test          # vitest run — mapping, format, render tests per page (mock mode), and the Delete report /
                  # Run retention mutations against a stubbed `fetch` (mock mode off)
npm run shots     # Playwright (system Edge) screenshot pass — see below
npm run preview   # serve the production build, port 4173
```

## Deploy

There is no deploy of its own. `npm --prefix ops/crash/worker run deploy` runs this project's `npm run
build` (so `npm install` here first) and then `wrangler deploy`, which uploads `dist/` as the Worker's
static assets (`[assets]` in `ops/crash/worker/wrangler.toml`: the Worker runs first only for `/v1/*`;
every other path is an asset, unknown ones fall back to `index.html` for the client-side routes). One
hostname, one deploy, everything same-origin — no CORS, no API base URL.

## Mock mode

Real data comes from the Worker's same-origin `/v1/*` API (`src/api/client.ts`), which only exists on the
deployed host — see "Real data" below. Without it, set `VITE_MOCK=1` and everything in `src/api/hooks.ts`
routes through `src/api/mock.ts`'s generated data instead: 128 reports over the last 30 days across 24
issues, including the plan's named sample issues (`NullReferenceException · Detail.UI.Hero.Render`
67/19, a 31s hang on the artist route 9/6, a native access violation in `nvwgf2umx.dll` 3/3, an
`0xC0000409` fail-fast 4/4) plus 19 filler issues and one purged issue (below), deterministically generated (seeded PRNG) so the same
data comes back every run. It also seeds four version quads (`0.3.2-stable`, `0.3.2-beta`, `0.3.1-stable`,
`0.3.0-stable`, each ×`x64`/`arm64`) and their symbol maps — including one intentionally-missing map
(`0.3.0-stable`/`arm64`) so the Symbols page's "missing" `Badge` is reviewable without a real gap.

The issue lifecycle and the newer Worker fields each have a case:

| Case | Mock issue | Where it shows |
|---|---|---|
| Regressed (status `open`, `regressedAt` set) | `InvalidOperationException · Queue.Advance` — resolved in ≤ 0.3.1, 2 regressions | Issues status column, Issue detail header |
| Resolved with `resolvedVersion` | `Hang · Shell.Routes.Album` (its newest semver) | Issue detail header note "Resolved in ≤ …" |
| Native fault facts (`exceptionCode`/`faultModule`/`faultOffset`) | the four Native issues, titled the Worker's grouping-v2 way (`ACCESS_VIOLATION in nvwgf2umx.dll+0x2f10c`) | Issue detail header, Report detail "Native fault" fact |
| Every report purged (no reports, `lastFrames` only, last seen 104 days ago) | `System.ObjectDisposedException · Wavee_Wavee_Playback_Engine__Dispose` | Issues → "All time"; Issue detail stack + the "Reports older than 90 days…" state |

The mutations answer with the Worker's shapes and leave the generated data unchanged: Delete report →
`{deleted: 1, fingerprint}` (404 for an unknown id), Run retention → `{deleted: 12, batches: 1, more: false}`.

Three more env vars, all overridable by an equivalent `?query=` param for one page load (what
`shots/take.mjs` uses so one running server can produce every state):

| Env var | Query param | Values | Effect |
|---|---|---|---|
| `VITE_MOCK_STATE` | `?mockState=` | `loading` \| `empty` \| `error` \| `refetching` | Forces every mock endpoint into that state. `loading` never resolves (shows the `Skeleton`, the *initial*-load-only shimmer). `empty` resolves with zero rows. `error` rejects after the delay. `refetching` resolves normally but also makes `hooks.ts` set a short `refetchInterval`, so react-query keeps cycling into a background fetch — the state that demonstrates `PageHeader`'s pinned indeterminate `ProgressBar` + `PageBody`'s dimmed content, as opposed to the full-page `Skeleton` (that's *only* for the very first load with no data yet). |
| `VITE_MOCK_DELAY` | `?mockDelay=` | milliseconds (default `250`) | Added latency before a mock endpoint resolves/rejects, for realism or to give a screenshot script more time. |
| `VITE_MOCK` | — | `1` \| unset | Master switch; `hooks.ts` calls the mock functions instead of `fetchJson` when set. |

`?theme=light|dark` is a similar one-shot override on top of `useTheme()`'s normal
OS-default-plus-persisted-override behaviour (`src/app/theme.ts`) — it does not touch `localStorage`.

## Real data

Real data is only on the deployed host, `https://crash.cproducts.dev`: Cloudflare Access signs you in,
and every same-origin `fetch` carries its `CF_Authorization` cookie, whose JWT the Worker verifies itself
(signature against the team's keys, `aud`, `iss`, expiry — `ops/crash/worker/src/access.ts`). Besides the
`GET`s, the dashboard calls `PATCH /v1/issues/:fp` (status/GitHub #), `DELETE /v1/reports/:id` (Report
detail → Delete report), `DELETE /v1/installs/:id` (Report detail → Delete this install's data) and
`POST /v1/retention/run` (Overview → Run retention now). A detail read that answers 404 renders the page's
"not found" state, not an error. Locally,
use mock mode. `npm run dev` here has no `/v1/*` behind it (no proxy), and a local Worker
(`npm --prefix ops/crash/worker run dev`, which builds and serves this dashboard the same way) has no
Access in front of it, so its verification fails closed and every route the dashboard calls answers `401`.

## Screenshots (`npm run shots`)

`shots/take.mjs` starts a Vite dev server programmatically with `VITE_MOCK=1`, drives it with Playwright
over **system Edge** (`chromium.launch({ channel: "msedge" })` — never `npx playwright install`, its
Chromium download has no Windows-on-ARM64 build), on its own port (`5174`, so the interactive `npm run
dev` server on `5173` can stay up while shots run), and visits every route × mock state × theme at both
required viewports (1280×800, 1920×1080), saving `shots/out/<route>-<state>-<theme>-<width>.png`
(gitignored). The run fails (non-zero exit) on any browser console error or on
`document.documentElement.scrollWidth > window.innerWidth` (horizontal overflow) at either viewport.

## Configuration

| Env var | Read by | Effect |
|---|---|---|
| `VITE_MOCK`, `VITE_MOCK_STATE`, `VITE_MOCK_DELAY` | `src/api/mock.ts` | See "Mock mode" above. |

There is no other configuration — no API base URL: every request is a relative, same-origin `/v1/...`
path, so the browser sends the Cloudflare Access cookie by default; the dashboard itself never handles
credentials.

## The shell contract

- `<main>` (inside `src/app/AppFrame.tsx`) is the **only** scroll container in the app: `html`, `body`,
  and `#root` are 100% height with no margin, `body` has `overflow: hidden` (`src/app/global.ts`), and the
  `NavDrawer` beside `<main>` never scrolls on its own.
- `PageHeader` (`src/scene/PageHeader.tsx`) is sticky at the top of `<main>` — breadcrumb + `Title2` +
  subtitle + a command `Toolbar` that collapses into an overflow `Menu` at narrow widths.
- `PageBody` (`src/scene/PageBody.tsx`) is a 12-column CSS grid; every child sets its own width via
  `gridColumn: "span n"` (the `Panel`'s `span` prop, or an inline wrapper `div` when a state — error,
  empty — has no `Panel` of its own).
- `Panel` (`src/scene/Panel.tsx`) is the one "card" primitive: `Card appearance="filled" size="medium"` +
  `CardHeader` + body + optional `CardFooter`. The canvas behind everything is
  `tokens.colorNeutralBackground2`; cards sit on `colorNeutralBackground1` (`Card`'s own default).

## Loading convention

Every scene follows one rule (plan §7): the `Skeleton` shimmer is reserved for a page's **very first**
load, when there is no data yet (`useXQuery().isLoading`). Any load that happens after that — a filter or
range change, pressing Refresh, pagination, a mutation invalidating a query — keeps the current content
mounted instead of swapping back to a `Skeleton`, and shows two things instead: an indeterminate
`ProgressBar` pinned to the bottom edge of `PageHeader`, and a slight dim (`opacity: 0.6` +
`aria-busy="true"`) on `PageBody`'s content. This is `isFetching && !isLoading` in react-query terms, and
it's implemented exactly once, in `PageHeader`/`PageBody` themselves — a page just passes its own
`isFetching`/`isLoading` down; it never re-implements the distinction. Mock mode has a fourth state,
`refetching`, purely to make this demonstrable without a real, changing backend.

## Fluent 2 conventions (surface → component)

| Surface | Component(s) | Notes |
|---|---|---|
| App shell / left nav | `NavDrawer` (`type="inline" open size="medium"`, `type="overlay"` under 640px) + `NavDrawerHeader`/`Body`/`Footer`, `AppItem`, `NavSectionHeader`, `NavItem`, `Hamburger` | `src/app/AppFrame.tsx`; selection derives from the route, items navigate via `useNavigate` but keep `href` |
| Scroll container | `<main>` only — `flex: 1 1 auto; overflow: auto` | Nothing else scrolls; `body` is `overflow: hidden` |
| Page header | `Breadcrumb`/`BreadcrumbItem`/`BreadcrumbButton`/`BreadcrumbDivider` + `Title2` + `Body1` + `Toolbar`/`ToolbarGroup`/`ToolbarDivider` inside `Overflow`/`OverflowItem`/`useOverflowMenu`, trailing `ToolbarButton`s (e.g. Refresh) outside the overflow group | `src/scene/PageHeader.tsx`; sticky, canvas background; also carries the background-refetch `ProgressBar` |
| Filters | `Dropdown`/`Option`, `SearchBox` | Passed into `PageHeader`'s `filters` slot (see Overview's range/version/channel `Dropdown`s). Issues' range adds "All time" (no `since`) for issues whose reports are all purged; Reports' search strips `-`, so the app's short id `3f9c-2b1a` matches the id prefix |
| Page content grid | `PageBody` — a 12-column CSS grid, children set `gridColumn: span n` | `src/scene/PageBody.tsx` |
| Card / panel | `Panel` = `Card appearance="filled" size="medium"` + `CardHeader` + optional `CardFooter` | `src/scene/Panel.tsx`; clickable panels get `focusMode="tab-exit"` |
| Stat tile | bare `Card` (no `CardHeader`) + `Caption1`/`LargeTitle`/`Caption1` | `src/pages/Overview/parts.tsx` `StatTile` — deliberately lighter than `Panel`, no title row |
| Sortable/multiselect grid | `DataGrid`/`DataGridHeader`/`DataGridBody`/`DataGridRow`/`DataGridCell`/`createTableColumn`, `columnSizingOptions` | Overview "Top issues" |
| Plain table | `Table`/`TableHeader`/`TableRow`/`TableHeaderCell`/`TableBody`/`TableCell`/`TableCellLayout` | Overview "Where it happened", Versions (one row per semver+quad+arch), Symbols (one row per quad+arch) |
| Charts | `VerticalStackedBarChart` (Overview "Reports per day"), `DonutChart` (Overview "By kind"), `HorizontalBarChart` (Overview "By version") — all `@fluentui/react-charts`, colours from `src/lib/colors.ts`'s theme-token palette | No `@fluentui/react-charting` (the deprecated v8-era package) anywhere |
| States: empty | `EmptyState` — icon + `Body1Strong` + `Caption1` + optional `Button` | `src/scene/EmptyState.tsx`; Issue detail's report-backed tabs (Occurrences, Environment, Log tail) show `PurgedReportsState` ("Reports older than 90 days are deleted; this issue's counts are kept.") when an issue has no reports left |
| States: error | `ErrorBar` — `MessageBar intent="error"` + `MessageBarBody`/`MessageBarTitle` + `MessageBarActions` Retry `Button` | `src/scene/ErrorBar.tsx` |
| States: initial load | `Skeleton` + `SkeletonItem`s shaped like the final layout (`StatsSkeleton`/`ChartSkeleton`/`GridSkeleton`/`FactsSkeleton`/`DetailSkeleton`), wrapped in the *same* `Panel`/`Card` the ready state uses | `src/scene/skeletons.tsx` — **only** for the first load with no cached data |
| States: background refetch | `PageHeader`'s pinned indeterminate `ProgressBar` (`thickness="medium"`) + `PageBody`'s `opacity: 0.6` dim + `aria-busy` on the still-mounted content | Never a `Skeleton` swap — see "Loading convention" above |
| Feedback | `useAppToast()` over `useToastController` + `Toast`/`ToastTitle`/`ToastBody`; `Dialog` for destructive confirms | `src/scene/toast.ts`; one `Toaster` in `main.tsx` with a fixed id |
| Destructive commands | `Button`/`ToolbarButton` → confirm `Dialog` → mutation → toast | Report detail "Delete report" (`DELETE /v1/reports/:id`, then back to Reports); Overview "Run retention now" (`POST /v1/retention/run`, toast "{n} reports purged" + "More remain…" when the run hit its batch cap). The retention `Dialog` is rendered by the page, not the button, so it still opens when the command sits in the toolbar's overflow menu |
| Badges | `Badge` (`appearance="tint"`; `color` from `src/lib/colors.ts`'s `kindBadgeColor`/`statusBadgeColor`, or a page-local map for things like a version's channel or a symbol map's uploaded/missing status), `CounterBadge` for the nav's open-issue count; `appearance="filled" color="danger"` "Regressed" | Kind pills, channel pills, symbol upload status, delta pills; "Regressed" replaces "Open" in the Issues status column (`IssueStatusBadge`, `src/pages/Issues/parts.tsx`, the regression facts as its `Tooltip` description) and leads Issue detail's header beside a visible "Resolved in ≤ {version} · reopened … · n regressions" caption |
| Native fault | monospace `Text` / a Facts row: `{NTSTATUS name} in {module}+0x{offset}` (`nativeFaultText`, `src/lib/format.ts` — the Worker's code names, else `0x` + 8 hex) | Issue detail header (newest report), Report detail "Native fault" fact |
| Info affordance | `InfoLabel` | Overview's "By version" panel header, Versions' subtitle ("Counts only — there is no active-user denominator") |
| Truncated grid cell + tooltip | `TableCellLayout truncate` (or a bare `Tooltip`) | `DataGrid`'s Issue column; Symbols' relative "Uploaded" time (`Tooltip` shows the absolute `formatDateTime`) |
| Theme toggle | nav footer `Menu`/`MenuTrigger`/`MenuButton`/`MenuPopover`/`MenuList`/`MenuItemRadio` (Auto/Light/Dark) | `src/app/AppFrame.tsx` |
| Theme provider | `FluentProvider` with `webLightTheme`/`webDarkTheme` from `useTheme()` | `src/main.tsx`, `src/app/theme.ts` |

No hand-drawn controls: `grep -rn "boxShadow\|borderRadius: tokens\|border: \`1px" src` should find nothing.

## Folder layout

```
src/
  main.tsx              FluentProvider (ThemeContext-driven) + QueryClientProvider + RouterProvider + Toaster
  app/                  router.tsx, AppFrame.tsx (shell), theme.ts (useTheme + ThemeContext), global.ts
  api/                  client.ts, types.ts (Wire*/App*), mapping.ts, mock.ts, hooks.ts
  scene/                PageHeader, PageBody, Panel, EmptyState, ErrorBar, skeletons.tsx, toast.ts
  pages/Overview/       index.tsx (data + layout), parts.tsx (stat tiles, charts, grid columns, tables)
  pages/Versions/       index.tsx — one Table row per semver+quad+arch, per-kind counts + a total
  pages/Symbols/        index.tsx — one Table row per quad+arch, upload status/debug id/entries/uploaded
  pages/Issues/         index.tsx — filterable multiselect DataGrid over useIssues, parts.tsx
  pages/IssueDetail/    index.tsx — stack (falls back to the issue's lastFrames), occurrences, environment,
                        the newest report's log tail, 14-day trend, parts.tsx
  pages/Reports/        index.tsx — filterable, keyset-paginated DataGrid over GET /v1/reports, parts.tsx
  pages/ReportDetail/   index.tsx — facts, report.txt/log-tail viewer (LogViewer, shared with Issue detail),
                        memory snapshot, delete report, install erasure, parts.tsx
  lib/                  format.ts, colors.ts
  test/setup.ts         jsdom polyfills (ResizeObserver, canvas, matchMedia, NodeFilter) for vitest
shots/take.mjs           Playwright screenshot script (output in shots/out/, gitignored)
```
