# Wavee crash service — deploy runbook

No CI touches this. Every step here is run by hand from a developer machine, the same way
`ops/release/wavee-release.ps1` is — see the repo root `CLAUDE.md`: "There is no CI." This document
covers the Cloudflare Worker in `ops/crash/worker`. The dashboard (`ops/crash/dashboard`, Fluent UI v9
on Cloudflare Pages) is a separate deploy with its own steps; only its Access policy is covered here.

Full design: `docs/plans/wavee/crash-diagnostics-implementation.md` §C (architecture, routes, schema)
and §I (the `.symmap` binary format and the ingest contract other work packages code against). Day-to-day
reference for engineers and support: `docs/guide/crash-diagnostics.md`.

## Prerequisites

- A Cloudflare account with Workers, R2, D1, and Access available (Workers Paid is **not** required for
  this service on paper — see "Free-tier limits" below; verify before committing to it for real).
- `node` + `npm` (this repo already needs both). `wrangler` is **not** a project dependency — every
  command below uses `npx wrangler@latest` so nobody has to keep a global install current. (`npx
  wrangler dev --local` needs `workerd`'s native binary, which currently has **no Windows-on-ARM64
  build**; if your dev box is Windows ARM64, either run `wrangler dev` from an x64/Linux/macOS box, or
  skip local dev and iterate against a deployed dev environment instead — see "Local development".)
- The `wavee-crash-worker` npm project is self-contained: `cd ops/crash/worker && npm install`.

## 1. Log in

```powershell
cd ops/crash/worker
npx wrangler@latest login
```

Opens a browser, authorizes the CLI against your Cloudflare account.

## 2. Create the D1 database and apply the schema

```powershell
npx wrangler@latest d1 create wavee-crash
# → prints a database_id; paste it into wrangler.toml's [[d1_databases]] block.

npx wrangler@latest d1 execute wavee-crash --remote --file schema.sql
```

`schema.sql` is idempotent-unsafe by design (plain `CREATE TABLE`, no `IF NOT EXISTS`) — it is meant to
be run once against a fresh database. If the schema changes later, write a new
`migrations/NNNN_description.sql` and apply it the same way (`wrangler d1 execute wavee-crash --remote
--file migrations/NNNN_description.sql`) rather than re-running `schema.sql`; `wrangler d1 migrations
apply` works too once a `migrations/` folder exists, but for a single from-scratch schema a plain
`execute --file` is simpler and is what this runbook uses.

Sanity check:

```powershell
npx wrangler@latest d1 execute wavee-crash --remote --command "SELECT name FROM sqlite_master WHERE type='table'"
# → reports, issues, symbols
```

## 3. Create the R2 bucket and its lifecycle rule

```powershell
npx wrangler@latest r2 bucket create wavee-crash
```

Then, in the Cloudflare dashboard (R2 → `wavee-crash` → Settings → Object lifecycle rules), add **one**
rule:

- Scope: prefix `reports/`
- Action: delete objects after **90 days** since upload
- **Do not** add a rule for the `symbols/` prefix — symbol maps must outlive individual crash reports
  (a report ingested on day 89 still needs its build's `.symmap` to resolve, and old builds stay on
  users' machines far longer than 90 days on the slow-update tail).

This is the retention promise `PRIVACY.md` makes ("Crash reports (opt-in)" § retention): 90 days, R2
lifecycle rule, `reports/` only.

The lifecycle rule can also be created with `wrangler`, but the dashboard is less error-prone for a
prefix-scoped rule and is the version-controlled source of truth here (screenshot it into your release
notes if you change it).

## 4. Set the ingest secret

```powershell
npx wrangler@latest secret put INGEST_KEY
# paste a long random value when prompted, e.g.:
#   node -e "console.log(require('crypto').randomBytes(32).toString('hex'))"
```

This is the **public-key-shaped** shared secret the desktop app sends as `X-Wavee-Ingest` (plan §B.5 —
stamped into the build via `WaveeCrashIngestKey`, WP-G). It is not secret in the "protects the data"
sense (it ships inside every Wavee install and can be extracted), only in the "stops randos from
spamming the ingest endpoint and burning the free-tier quota" sense. Rotate it by setting a new secret
and re-releasing; there is no dual-key grace period in v1 — accept a short window of 401s from
already-installed builds after a rotation, or stage a release first.

`wrangler.toml`'s `[vars] INGEST_KEY = "dev-only-placeholder…"` value is only ever read by `wrangler dev
--local`; `wrangler secret put` always wins over a `[vars]` entry of the same name once deployed.

## 5. Deploy the Worker

```powershell
npm --prefix ops/crash/worker run deploy
# equivalent to: npx wrangler@latest deploy
```

Confirms the bindings it picked up (`DB`, `BUCKET`, `RATE`) and prints the deployed URL
(`https://wavee-crash.<your-subdomain>.workers.dev`, or your custom domain once one is attached —
`crash.wavee.app` in the plan's examples). That URL is what `WaveeCrashIngestUrl` points the app at
(WP-G, `ops/release/wavee-release.ps1` phase `symbols`).

## 6. Configure Cloudflare Access

Access enforcement happens at the **zone**, not in the Worker's own code — the Worker only checks that
Cloudflare Access already let the request through (it looks for the `Cf-Access-Jwt-Assertion` header;
see the `TODO` in `src/index.ts`'s `requireAccess` for why it stops at "present", not "verified", and
what would need to change if this worker ever ran on a route Access doesn't cover).

In the Cloudflare Zero Trust dashboard:

1. **Access → Applications → Add an application → Self-hosted.**
2. Two applications (or one covering both — your call):
   - The dashboard's Pages domain (all paths).
   - This Worker's `/v1/*` **GET and PATCH** routes only — do **not** gate `POST /v1/report`; that route
     authenticates itself with `X-Wavee-Ingest` and must stay reachable by the desktop app, which cannot
     do an interactive Access login. Path rule: `crash.wavee.app/v1/*` with method excluded for `POST`,
     or simply scope the application's path to exclude `/v1/report` (Access path rules support this).
3. Policy: allow your own identity (and any teammates') by email, via whatever IdP your Cloudflare Zero
   Trust org already uses (GitHub, Google, one-time PIN — anything works for a ≤ 50-user free tier).
4. Save, then verify from a private/incognito window that hitting the dashboard or a `GET /v1/issues`
   URL redirects to an Access login page, and that `POST /v1/report` with a valid `X-Wavee-Ingest` header
   still works unauthenticated (`curl` from a terminal, no browser).

## 7. Uploading symbol maps (what the release script runs)

The release script (`ops/release/wavee-release.ps1`, phase `symbols`, WP-G) uploads one `.symmap` per
quad/arch after building it with `Wavee.ReleaseTool symbol-map`:

```powershell
npx wrangler@latest r2 object put `
  wavee-crash/symbols/<quad>/win-<arch>.symmap `
  --file "Wavee-<quad>-win-<arch>.symmap"
```

This route is **not** exposed through the Worker's HTTP API (there is no `PUT /v1/symbols/:quad/:arch`
handler to keep the ingest surface small) — it only ever happens through this `wrangler` command, run by
whoever cuts the release, same trust level as the rest of `wavee-release.ps1`. The Worker discovers a map
the first time it needs to resolve a report for that quad/arch (`src/symbolicate.ts`, R2 `GET`, cached in
memory per isolate) and opportunistically records it in the `symbols` D1 table so the dashboard's Symbols
tab has something to show — that D1 row is a side effect of the first successful read, not of this
upload step, so a freshly-uploaded map won't appear in the table until the first crash report that needs
it comes in.

## 8. Local development

```powershell
cd ops/crash/worker
npm install
npm run dev     # npx wrangler@latest dev --local
```

Needs `workerd`'s native runtime, which as of this writing has no Windows-on-ARM64 build (`npm install`
of `wrangler` itself succeeds on ARM64; only the `dev`/local-execution path needs `workerd`). If `wrangler
dev --local` won't start on your machine for that reason:

- Run it on an x64 or Linux/macOS box instead, or
- `npx wrangler@latest dev --remote` runs your local code against real Cloudflare infrastructure
  (real D1/R2 in a dev database if you point `wrangler.toml` at one) instead of the local `workerd`
  runtime, or
- Iterate purely against the unit tests (`npm test` — see below) and do the end-to-end check via `wrangler
  dev --remote` or a real deploy once.

Either way, before pointing a real WP-C build at a dev Worker, confirm a bundle from that build round-trips:
`POST /v1/report` with a real multipart body → `201` → the row shows up via `GET /v1/reports/:id` (behind
Access) with resolved `frames_json` if you've uploaded a matching `.symmap`.

## 9. Tests

```powershell
cd ops/crash/worker
npm install
npm test              # vitest run
npm run typecheck     # tsc --noEmit over src/
npm run typecheck:test  # tsc --noEmit over src/ + test/
```

These are plain `vitest` unit/integration tests against hand-written D1/R2 doubles — see
`vitest.config.ts` and `test/fixtures.ts` for why (`@cloudflare/vitest-pool-workers` needs `workerd`,
unavailable on this Windows-ARM64 dev box; the D1 double is real in-memory SQLite via `better-sqlite3`
loaded with the actual `schema.sql`, not a hand-rolled query stub, so `src/store.ts`'s SQL is genuinely
exercised). They do not need `wrangler`, D1, R2, or network access, and run in well under a second.

## Free-tier limits — verify these two before relying on them

Plan §H "Verify-at-implementation items" #3:

1. **R2 binding on the Workers free plan.** A 2026 source the plan cites claims R2 bucket bindings
   require Workers Paid ($5/mo) — unverified as of this writing. Check the current Cloudflare pricing
   page, or just try deploying this Worker on a free-plan account and see whether the `BUCKET` binding
   works; `wrangler deploy` will fail loudly if it doesn't.
2. **10 ms CPU budget per request (Workers free plan).** `POST /v1/report`'s heaviest step is
   `src/symbolicate.ts`'s binary search over the `.symmap`'s `Uint32Array` view — deliberately zero-copy,
   no JSON parsing, so it should comfortably fit, but measure it for real once a production-sized
   `.symmap` (thousands of entries) is available: `wrangler tail` after a real deploy shows per-request
   CPU time.

Other free-tier ceilings this service is sized against (plan §C): Workers 100k requests/day, R2 10 GB +
no egress fees, D1 5 GB / 5M reads per day, Cloudflare Pages free, Cloudflare Access free for ≤ 50 users.
None of these should bind at Wavee's current install base; revisit if `GET /v1/versions` or the
dashboard's polling ever gets chatty.

## Route table (for reference — see `docs/guide/crash-diagnostics.md` for the full contract)

| Route | Auth | Status codes |
|---|---|---|
| `POST /v1/report` | `X-Wavee-Ingest` header == `INGEST_KEY` secret; rate-limited | `201`, `401`, `400`, `409`, `410`, `413`, `429` |
| `DELETE /v1/installs/:installId` | `X-Wavee-Ingest` **or** Cloudflare Access; rate-limited | `200 {deleted:n}`, `401`, `404`, `429` |
| `GET /v1/issues` | Cloudflare Access | `200`, `401` |
| `GET /v1/issues/:fp` | Cloudflare Access | `200`, `401`, `404` |
| `PATCH /v1/issues/:fp` | Cloudflare Access | `200`, `401`, `400`, `404` |
| `GET /v1/reports` (paged list) | Cloudflare Access | `200`, `401` |
| `GET /v1/reports/:id` | Cloudflare Access | `200`, `401`, `404` |
| `GET /v1/reports/:id/:part` (`summary`\|`report`\|`tail`\|`dump`) | Cloudflare Access | `200`, `401`, `404` |
| `GET /v1/versions` | Cloudflare Access | `200`, `401` |
| `GET /v1/stats` | Cloudflare Access | `200`, `401` |
| `GET /v1/symbols` | Cloudflare Access | `200`, `401` |

`wavee-crash/symbols/<quad>/win-<arch>.symmap` is written only via `wrangler r2 object put` (step 7
above) — there is no HTTP route for it.

**`POST /v1/report` now also returns `410 Gone`** when `summary.installId` belongs to an install that
called `DELETE /v1/installs/:installId` (plan §J, "Right to erasure") — the tombstone in the
`deleted_installs` table blocks silent re-admission of data for an id that asked to be forgotten. The
client (`Crash.Uploader`) treats `410` like any other `4xx`: it drops the outbox item rather than
retrying, and rotates its local install id afterward so a future report, if reporting stays on, isn't
linkable to the deleted ones.

**`DELETE /v1/installs/:installId`** is the one route accepted with *either* credential: the app's own
`X-Wavee-Ingest` key (its in-product "Delete my data" button) or a Cloudflare Access session (the
dashboard's Report detail → "Delete this install's data…"). It deletes every `reports` row and R2 object
for that install id, recomputes (or deletes outright, at count 0) every `issues` row those reports
belonged to, and inserts a permanent tombstone. `404` means nothing was ever stored for that id (including
a second call after it's already been erased — there's nothing left the second time either).

`GET /v1/stats`, `GET /v1/reports` (paged, `q`/`kind`/`quad`/`since`/`limit`/`cursor` filters), `GET
/v1/symbols`, and the widened `GET /v1/issues/:fp` (now also returns `frames`, `occurrences`,
`sparkline14d`, `breakdowns`) and `GET /v1/reports/:id` (now also returns `this_install` and `debug_id`)
responses back the dashboard's Overview/Reports/Issue-detail/Report-detail pages — see
`docs/plans/wavee/crash-dashboard-implementation.md` §4 for the shapes and which dashboard page reads
each one.
