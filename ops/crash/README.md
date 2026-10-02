# Wavee crash service — deploy runbook

No CI touches this. Every step here is run by hand from a developer machine, the same way
`ops/release/wavee-release.ps1` is — see the repo root `CLAUDE.md`: "There is no CI." This document
covers the Cloudflare Worker in `ops/crash/worker` **and** the dashboard (`ops/crash/dashboard`, Fluent
UI v9): the Worker serves the dashboard's built `dist/` as its static assets on the same hostname,
`https://crash.cproducts.dev`, next to `/v1/*` — one deploy (step 5), one Access setup (step 6), no
separate Cloudflare Pages project, no CORS.

Full design: `docs/plans/wavee/crash-diagnostics-implementation.md` §C (architecture, routes, schema)
and §I (the `.symmap` binary format and the ingest contract other work packages code against); hosting
(hostname, DNS, Access, the JWT check): `docs/plans/wavee/crash-hosting-implementation.md`; retention,
alerts, regressions, grouping v2 and report deletion (#165):
`docs/plans/wavee/crash-production-readiness-implementation.md` §W1. Day-to-day reference for engineers
and support: `docs/guide/crash-diagnostics.md`.

## Prerequisites

- A Cloudflare account with Workers, R2, D1, and Access available (Workers Paid is **not** required for
  this service on paper — see "Free-tier limits" below; verify before committing to it for real), and
  `cproducts.dev` as an active zone on it (step 0).
- `node` + `npm` (this repo already needs both). `wrangler` is a devDependency of the Worker (pinned with
  `@cloudflare/workers-types` v5, its peer) — every command below is `npx wrangler …` from
  `ops/crash/worker`. (The `cf` CLI's `cf deploy` delegates to it but cannot spawn it on Windows as of
  `cf` 1.0.0-beta.10 — `spawn EFTYPE` — so deploy with wrangler directly.) (`npx
  wrangler dev --local` needs `workerd`'s native binary, which currently has **no Windows-on-ARM64
  build**; if your dev box is Windows ARM64, either run `wrangler dev` from an x64/Linux/macOS box, or
  skip local dev and iterate against a deployed dev environment instead — see "Local development".)
- Two npm projects: `npm --prefix ops/crash/worker install` and `npm --prefix ops/crash/dashboard
  install` — the Worker's `dev`/`deploy` scripts build the dashboard first (step 5).

## 0. Move cproducts.dev DNS to Cloudflare

Once, before anything else. A Worker Custom Domain (`crash.cproducts.dev`, step 5) needs an **active
Cloudflare zone** for its parent domain, and Cloudflare Access can only gate a hostname on one — a CNAME
from Vercel's DNS could reach a Pages project but never put Access in front of it. Only the nameservers
move; the registration stays at Vercel, exactly like `canijustprint.com` (already on
`lars`/`zara.ns.cloudflare.com`).

1. Cloudflare dashboard → **Add a domain** → `cproducts.dev` → **Free** plan. Make the zone's records
   exactly these — the ones on Vercel DNS as of 2026-10-01 (`vercel dns ls cproducts.dev`) worth keeping;
   delete anything else the import scan brought along:

   | Type | Name | Value | Proxy |
   |---|---|---|---|
   | MX | `@` | `mx1.improvmx.com`, priority 10 | — |
   | MX | `@` | `mx2.improvmx.com`, priority 20 | — |
   | TXT | `@` | `v=spf1 include:spf.improvmx.com ~all` | — |
   | CNAME (flattened at the apex) | `@` | `f93bd6a5b1bd3ebb.vercel-dns-017.com` (or `A 76.76.21.21`) | **DNS only** (grey cloud) |
   | CNAME | `www` | `cname.vercel-dns-017.com` | **DNS only** (grey cloud) |

   The MX pair + SPF are ImprovMX mail forwarding; the two Vercel records stay DNS only so Vercel keeps
   serving and certifying the site itself. **Not** recreated: Vercel's default `*` wildcard — no project
   uses one (`vercel domains inspect cproducts.dev` lists only `cproducts.dev` and `www.cproducts.dev`), it
   would only point every unclaimed subdomain at Vercel — and its default CAA set: with no CAA record any
   CA may issue, so Vercel's certificates and Cloudflare's edge certificate for `crash` both keep working
   with no allow-list to maintain. `crash` itself is not created here either — `wrangler deploy` creates
   it (step 5).
2. Vercel → **Domains** → `cproducts.dev` → **Nameservers** → the pair Cloudflare assigned above (on the
   zone's Overview page).
3. Wait for Cloudflare to mark the zone **Active** (Overview page, plus an e-mail; minutes, at worst a day).
4. Check nothing moved: `https://cproducts.dev` and `https://www.cproducts.dev` still serve the site,
   `Resolve-DnsName cproducts.dev -Type MX` still names the two ImprovMX hosts, and Vercel → Domains shows
   **Valid Configuration** for both names.

## 1. Log in

```powershell
cd ops/crash/worker
npx wrangler login
```

Opens a browser, authorizes the CLI against your Cloudflare account.

## 2. Create the D1 database, apply the schema, then the migrations

```powershell
npx wrangler d1 create wavee-crash
# → prints a database_id; paste it into wrangler.toml's [[d1_databases]] block.

npx wrangler d1 execute wavee-crash --remote --file schema.sql   # once, on a fresh database only
npx wrangler d1 migrations list  wavee-crash --remote            # what is pending
npx wrangler d1 migrations apply wavee-crash --remote            # every pending migrations/NNNN_*.sql, in order
```

`schema.sql` is the **frozen baseline**: idempotent-unsafe by design (plain `CREATE TABLE`, no `IF NOT
EXISTS`), run once against a fresh database and never edited again. Every later change is a new
`migrations/NNNN_description.sql` (`wrangler.toml` `migrations_dir = "migrations"`), applied with
`wrangler d1 migrations apply`, which records each one in the database's `d1_migrations` table and so
never runs it twice. **Never `d1 execute --file` a migration** — that bypasses the record, and the next
`migrations apply` would run it again and fail on the duplicate columns. The Worker tests load exactly this
sequence (`schema.sql`, then `migrations/*.sql` by name — `test/fixtures.ts`).

Write a migration so it is backward compatible with the Worker already deployed (added, defaulted columns;
new indexes), apply it, **then** deploy the code that needs it (step 5).

| Migration | What it adds |
|---|---|
| `0001_retention_alerts_grouping_v2.sql` (#165) | `reports`: `exception_code`, `fault_module`, `fault_offset`, `fp_version`; `issues`: `regressed_at`, `regressions`, `resolved_at`, `resolved_version`, `last_frames_json`, `fp_version`; indexes `reports_received` (the purge key) and `reports_install` |

Sanity check:

```powershell
npx wrangler d1 execute wavee-crash --remote --command "SELECT name FROM sqlite_master WHERE type='table'"
# → reports, issues, symbols, deleted_installs, d1_migrations
npx wrangler d1 migrations list wavee-crash --remote
# → no migrations to apply
```

## 3. Create the R2 bucket and its lifecycle rule

```powershell
npx wrangler r2 bucket create wavee-crash
```

Then, in the Cloudflare dashboard (R2 → `wavee-crash` → Settings → Object lifecycle rules), add **one**
rule:

- Scope: prefix `reports/`
- Action: delete objects after **90 days** since upload
- **Do not** add a rule for the `symbols/` prefix — symbol maps must outlive individual crash reports
  (a report ingested on day 89 still needs its build's `.symmap` to resolve, and old builds stay on
  users' machines far longer than 90 days on the slow-update tail).

The rule is a **backstop** since #165. The retention promise `PRIVACY.md` makes ("Crash reports
(opt-in)" § retention) is kept by the Worker's daily purge (step 5b): it deletes each `reports` row older
than 90 days **together with** its R2 objects, so the D1 row — install id, last route, exception message,
frames — goes too, which an R2 rule alone never did. The rule only catches objects the purge missed (a
failed run, an object whose row is already gone).

The lifecycle rule can also be created with `wrangler`, but the dashboard is less error-prone for a
prefix-scoped rule and is the version-controlled source of truth here (screenshot it into your release
notes if you change it).

## 4. Set the ingest secret

```powershell
npx wrangler secret put INGEST_KEY
# paste a long random value when prompted, e.g.:
#   node -e "console.log(require('crypto').randomBytes(32).toString('hex'))"
```

This is the **public-key-shaped** shared secret the desktop app sends as `X-Wavee-Ingest` (plan §B.5 —
stamped into the build via `WaveeCrashIngestKey`, WP-G). It is not secret in the "protects the data"
sense (it ships inside every Wavee install and can be extracted), only in the "stops randos from
spamming the ingest endpoint and burning the free-tier quota" sense. Rotate it by setting a new secret
and re-releasing; there is no dual-key grace period in v1 — accept a short window of 401s from
already-installed builds after a rotation, or stage a release first.

`INGEST_KEY` is never a `wrangler.toml` `[vars]` entry (a var and a secret share one binding namespace, so a
committed placeholder could become the live key on deploy); `wrangler dev` reads it from the gitignored
`.dev.vars`. Unset, every ingest/erase call answers `401`.

### 4b. The Discord alert webhook (optional)

```powershell
npx wrangler secret put DISCORD_WEBHOOK_URL
# paste the webhook URL: Discord → the alerts channel → Edit Channel → Integrations → Webhooks → New Webhook
```

A **secret only**, never a `[vars]` entry — the URL alone lets anyone post to the channel. Keep a copy in
1Password next to the ingest key. Unset (or anything that is not a `https://discord.com/api/webhooks/<id>/<token>`
URL — `ptb.`/`canary.`/`discordapp.com` are accepted too), alerts are a silent no-op. What an alert says and
when it fires: step 5b.

## 5. Deploy the Worker and the dashboard

```powershell
npm --prefix ops/crash/worker run deploy
# = npm --prefix ../dashboard run build && npx wrangler deploy
```

One command ships both: it builds the dashboard (`ops/crash/dashboard` → `dist/`), then `wrangler
deploy` uploads the Worker plus that `dist/` as its static assets (`[assets]` in `wrangler.toml` — the
Worker runs first only for `/v1/*`; every other path is an asset, unknown ones fall back to `index.html`
for the dashboard's client-side routes). It confirms the bindings it picked up (`DB`, `BUCKET`, `RATE`),
lists the cron trigger `schedule: 17 3 * * *` (`[triggers]` — the daily purge, step 5b), and attaches the
Custom Domain `crash.cproducts.dev` from `routes` in `wrangler.toml`, creating its DNS record and
certificate in the step-0 zone. `workers_dev = false` and `preview_urls = false`: there is no
`*.workers.dev` or preview URL, so no hostname reaches the Worker without Access in front of it (step 6).

Apply pending migrations (step 2) **before** deploying code that reads their columns.

`https://crash.cproducts.dev` is the one URL every release stamps into the build:
`ops/release/wavee-release.ps1 -CrashIngestUrl https://crash.cproducts.dev` (`WaveeCrashIngestUrl`,
WP-G; phase `symbols` uploads the maps this Worker resolves against).

Until step 6 is done the host is not gated: the dashboard's static files are reachable (they carry no
data) and every data route answers `401`, because `wrangler.toml`'s `ACCESS_*` placeholders fail closed.

### 5b. What the deployed Worker does on its own (#165)

**Retention.** `[triggers] crons = ["17 3 * * *"]` runs `scheduled()` daily at 03:17 UTC: `reports` rows
with `received_at` older than **90 days** are deleted together with their four R2 objects
(`reports/<quad>/<id>/{summary.json,report.txt,log-tail.txt,minidump.dmp}`). It is driven from D1, never
from an R2 listing: batches of 200 rows → one R2 delete of ≤ 800 keys → the same rows deleted, at most 10
batches (≤ 30 subrequests) per run; a bigger backlog continues on the next run. The dashboard's Overview
"Run retention now" calls `POST /v1/retention/run` (Access only), which runs the same purge immediately and
answers `{deleted, batches, more}`. Only counts are logged (`wrangler tail` → `retention.purge`).

**Issues keep their stats.** The purge never touches `issues`: count, installs, versions, first/last seen,
status and the newest stack (`last_frames_json`) are **lifetime** values, updated incrementally on each
report. An issue whose reports are all purged stays listed with its counts; its detail page shows the stack
from `last_frames_json`. Erasure (`DELETE /v1/installs/:id`) and `DELETE /v1/reports/:id` still
*recompute* the issue from the reports that remain — so after a purge they can shrink its counts to the
90-day window, and removing an issue's last remaining report deletes the issue (accepted, owner decision
2026-10-02).

**Grouping v2.** `src/grouping.ts`: Managed groups by exception type + the top three **app** frames
(BCL/runtime/ThrowHelper/NuGet frames skipped, compiler ordinals normalized); Native by NTSTATUS +
faulting module + app frames (module + offset when no frame was captured); ExitCode by code; Hang and
UncleanExit are one issue each. Titles name the status code and the first `Wavee_*` frame. Issues grouped
before migration 0001 (`fp_version` 1) stay as they are; new reports open v2 issues.

**Alerts and regressions.** With `DISCORD_WEBHOOK_URL` set (step 4b), the Worker posts to Discord when a
report opens a **new issue** and when it **reopens a resolved one**. Resolving an issue (dashboard, `PATCH
… {status:"resolved"}`) records `resolved_version` = the newest semver it had seen; only a report from a
**newer** semver reopens it (status `open`, `regressed_at`, `regressions + 1`) and alerts — the same or an
older version never does, `ignored` never reopens, and an issue resolved before any version was recorded
never auto-reopens. Alerts fire only for builds whose `.symmap` the Worker holds (step 7), so the public
ingest key cannot spam the channel with invented versions. An alert carries the title, kind,
version/quad/arch, channel, counts, the GitHub issue number and a dashboard link — never an exception
message, install id, report id or log text — and mentions nobody.

## 6. Configure Cloudflare Access

Two **Self-hosted** applications on the one hostname. Access applies the **most specific** path match, so
the ingest application's Bypass wins on its two paths and the dashboard application's Allow covers the rest:

```
crash.cproducts.dev
  app "Wavee crashes"       host, all paths   → Allow  (owner's e-mail)
  app "Wavee crash ingest"  /v1/report        → Bypass (Everyone)
                            /v1/installs/*    → Bypass (Everyone)
```

**Live since 2026-10-02:** Zero Trust team `cproducts` (`https://cproducts.cloudflareaccess.com`, login by
one-time PIN), reusable policies "Wavee crashes - owner" (Allow, owner's e-mail) and "Wavee crash ingest -
everyone" (Bypass), the two applications below; the values are in `wrangler.toml`. The steps stay as the
from-scratch recipe.

In Cloudflare Zero Trust → **Access → Applications → Add an application → Self-hosted**:

1. **"Wavee crashes"** — domain `crash.cproducts.dev`, no path (the dashboard and every `/v1/*` route).
   Policy: **Allow**, Include → Emails → your own address (and any teammates'), via whatever IdP the Zero
   Trust org already uses (GitHub, Google, one-time PIN — anything works for the ≤ 50-user free tier).
2. **"Wavee crash ingest"** — destinations `crash.cproducts.dev/v1/report` and
   `crash.cproducts.dev/v1/installs/*` (the `/*` is what covers `/v1/installs/<id>`). Policy: **Bypass**,
   Include → Everyone. These are the desktop app's two routes (`POST /v1/report`, `DELETE
   /v1/installs/:installId`): the app cannot do an interactive Access login and authenticates itself with
   `X-Wavee-Ingest`. A wrong-key `DELETE` below answering `302` instead of `401` means the bypass misses.
3. Copy two values into `wrangler.toml` `[vars]`, then redeploy (step 5):
   - `ACCESS_AUD` — "Wavee crashes" → Overview → **Application Audience (AUD) Tag**.
   - `ACCESS_TEAM_DOMAIN` — the team domain, `https://<team>.cloudflareaccess.com` (Zero Trust → Settings).

   Both are public values (every Access token carries them), not secrets — `[vars]`, not `wrangler secret
   put`. The `REPLACE_…` placeholders fail closed: every Access-gated route answers `401` until both are real.

Access in front is not the whole check: the **Worker verifies the Access JWT itself** (`src/access.ts` —
RS256 signature against `<team>/cdn-cgi/access/certs`, `aud` = the AUD tag above, `iss` = the team domain,
expiry). It reads the `Cf-Access-Jwt-Assertion` header Access injects on gated paths, else the
`CF_Authorization` cookie — the only carrier on the Bypass paths, where Access injects nothing and nothing
stops a caller forging the header, and where the dashboard's "Delete this install's data…" call lands. A
forged, foreign or expired token gets `401`.

Verify, from a terminal (no browser session) and one private/incognito window:

```powershell
# private window: https://crash.cproducts.dev → the Access login page, then the dashboard

# ingest, wrong key → JSON 401 from the Worker (a 302 to the login page means the Bypass missed)
curl.exe -i -X POST https://crash.cproducts.dev/v1/report -H "X-Wavee-Ingest: wrong"

# erasure, wrong key → JSON 401 (not a 302)
curl.exe -i -X DELETE https://crash.cproducts.dev/v1/installs/x -H "X-Wavee-Ingest: wrong"

# a dashboard route, no session → 302 to the Access login
curl.exe -i https://crash.cproducts.dev/v1/issues

# deleting a report is dashboard-only: no session → 302 to the Access login (never reachable with the ingest key)
curl.exe -i -X DELETE https://crash.cproducts.dev/v1/reports/x

# erasure with a forged Access header → 401 (verified, not merely present)
curl.exe -i -X DELETE https://crash.cproducts.dev/v1/installs/x -H "Cf-Access-Jwt-Assertion: forged"
```

## 7. Uploading symbol maps (what the release script runs)

The release script (`ops/release/wavee-release.ps1`, phase `symbols`, WP-G) uploads one `.symmap` per
quad/arch after building it with `Wavee.ReleaseTool symbol-map`:

```powershell
npx wrangler r2 object put `
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

Frames are resolved **at ingest** and stored in `reports.frames_json`: a report that arrives before its
map is uploaded stays `<unresolved>`; send a new one after the upload. For a verify build outside a
release, `ops/build/publish-wavee-aot.ps1 -CrashService` stamps the exe and uploads its map in one step
(limited to `0.0.x.y` verify quads; see the `releasing` skill, § Verify builds).

## 8. Local development

```powershell
cd ops/crash/worker
npm install
npm run dev     # npm --prefix ../dashboard run build && npx wrangler dev --local
```

Builds the dashboard first, so the local Worker serves it exactly as the deployed one does. There is no
Access in front of a local Worker, though, and its JWT verification fails closed: every Access-gated route
(everything the dashboard calls) answers `401` locally. Exercise the ingest path with the dev
`INGEST_KEY`, and the dashboard with its mock mode (`ops/crash/dashboard/README.md`).

Needs `workerd`'s native runtime, which as of this writing has no Windows-on-ARM64 build (`npm install`
of `wrangler` itself succeeds on ARM64; only the `dev`/local-execution path needs `workerd`). If `wrangler
dev --local` won't start on your machine for that reason:

- Run it on an x64 or Linux/macOS box instead, or
- `npx wrangler dev --remote` runs your local code against real Cloudflare infrastructure
  (real D1/R2 in a dev database if you point `wrangler.toml` at one) instead of the local `workerd`
  runtime, or
- Iterate purely against the unit tests (`npm test` — see below) and do the end-to-end check via `wrangler
  dev --remote` or a real deploy once.

Either way, before pointing a real WP-C build at a dev Worker, confirm a bundle from that build round-trips:
`POST /v1/report` with a real multipart body → `201` → the row shows up via `GET /v1/reports/:id` (behind
Access — so on the deployed host; a local Worker answers it `401`) with resolved `frames_json` if you've
uploaded a matching `.symmap`.

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
loaded with the actual `schema.sql` plus every `migrations/*.sql`, not a hand-rolled query stub, so
`src/store.ts`'s SQL is genuinely exercised). They do not need `wrangler`, D1, R2, or network access.
`test/helpers.ts` holds the shared env/summary/ingest helpers and a recording alert sink; no test reaches
Discord (`DISCORD_WEBHOOK_URL` is unset unless a test stubs `fetch` itself).

`test/contract.test.ts` POSTs `ops/crash/contract/{managed,native}.multipart` — the byte-exact bodies the
C# client produces (`ops/crash/contract/README.md`) — so the Worker is tested against the real client's
bytes, not a re-implementation of them.

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
no egress fees, D1 5 GB / 5M reads per day, Workers static assets free (asset requests are free and don't
count against the Worker's requests — it runs only for `/v1/*`), Cloudflare Access free for ≤ 50 users,
≤ 5 cron triggers per account (this Worker uses one), ≤ 50 subrequests per invocation (the purge uses ≤ 30;
erasure deletes R2 objects 1000 keys per call), ≤ 100 bound parameters per D1 statement.
None of these should bind at Wavee's current install base; revisit if `GET /v1/versions` or the
dashboard's polling ever gets chatty.

## Route table (for reference — see `docs/guide/crash-diagnostics.md` for the full contract)

| Route | Auth | Status codes |
|---|---|---|
| `POST /v1/report` | `X-Wavee-Ingest` header == `INGEST_KEY` secret; rate-limited | `201`, `401`, `400`, `409`, `410`, `413`, `429` |
| `DELETE /v1/installs/:installId` | `X-Wavee-Ingest` **or** verified Access JWT; rate-limited | `200 {deleted:n}`, `401`, `404`, `429` |
| `GET /v1/issues` | verified Access JWT (header or `CF_Authorization` cookie) | `200`, `401` |
| `GET /v1/issues/:fp` | verified Access JWT (header or `CF_Authorization` cookie) | `200`, `401`, `404` |
| `PATCH /v1/issues/:fp` (`status`: `open`\|`resolved`\|`ignored`, `github_issue`) | verified Access JWT (header or `CF_Authorization` cookie) | `200`, `401`, `400` (malformed body or another status), `404` |
| `GET /v1/reports` (paged list) | verified Access JWT (header or `CF_Authorization` cookie) | `200`, `401` |
| `GET /v1/reports/:id` | verified Access JWT (header or `CF_Authorization` cookie) | `200`, `401`, `404` |
| `DELETE /v1/reports/:id` | verified Access JWT (header or `CF_Authorization` cookie) | `200 {deleted:1, fingerprint}`, `401`, `404` |
| `GET /v1/reports/:id/:part` (`summary`\|`report`\|`tail`\|`dump`) | verified Access JWT (header or `CF_Authorization` cookie) | `200`, `401`, `404` |
| `GET /v1/versions` | verified Access JWT (header or `CF_Authorization` cookie) | `200`, `401` |
| `GET /v1/stats` | verified Access JWT (header or `CF_Authorization` cookie) | `200`, `401` |
| `GET /v1/symbols` | verified Access JWT (header or `CF_Authorization` cookie) | `200`, `401` |
| `POST /v1/retention/run` | verified Access JWT (header or `CF_Authorization` cookie) | `200 {deleted, batches, more}`, `401` |

`wavee-crash/symbols/<quad>/win-<arch>.symmap` is written only via `wrangler r2 object put` (step 7
above) — there is no HTTP route for it.

**`DELETE /v1/reports/:id`** (the dashboard's "Delete report", #165) deletes that report's four R2 objects
and its row, then recomputes its issue from the remaining reports (deleting the issue with its last report)
and answers the issue's fingerprint so the dashboard can refresh or leave it. Unlike an install's erasure it
writes no tombstone: the install may keep reporting.

**`PATCH /v1/issues/:fp`** with `status: "resolved"` also records `resolved_at` and `resolved_version` (the
regression baseline, step 5b); reopening by hand clears neither.

**`POST /v1/retention/run`** runs the daily purge now (step 5b); `more: true` means it stopped at its batch
limit with expired rows left — run it again.

**`POST /v1/report` now also returns `410 Gone`** when `summary.installId` belongs to an install that
called `DELETE /v1/installs/:installId` (plan §J, "Right to erasure") — the tombstone in the
`deleted_installs` table blocks silent re-admission of data for an id that asked to be forgotten. The
client (`Crash.Uploader`) treats `410` like any other `4xx`: it drops the outbox item rather than
retrying, and rotates its local install id afterward so a future report, if reporting stays on, isn't
linkable to the deleted ones.

**`DELETE /v1/installs/:installId`** is the one route accepted with *either* credential: the app's own
`X-Wavee-Ingest` key (its in-product "Delete my data" button) or a verified Cloudflare Access JWT (the
dashboard's Report detail → "Delete this install's data…"; the path sits under the ingest Bypass, so that
JWT arrives as the `CF_Authorization` cookie — step 6). It deletes every `reports` row and R2 object
for that install id, recomputes (or deletes outright, at count 0) every `issues` row those reports
belonged to, and inserts a permanent tombstone. `404` means nothing was ever stored for that id (including
a second call after it's already been erased — there's nothing left the second time either).

`GET /v1/stats`, `GET /v1/reports` (paged, `q`/`kind`/`quad`/`since`/`limit`/`cursor` filters), `GET
/v1/symbols`, and the widened `GET /v1/issues/:fp` (now also returns `frames`, `occurrences`,
`sparkline14d`, `breakdowns`) and `GET /v1/reports/:id` (now also returns `this_install` and `debug_id`)
responses back the dashboard's Overview/Reports/Issue-detail/Report-detail pages — see
`docs/plans/wavee/crash-dashboard-implementation.md` §4 for the shapes and which dashboard page reads
each one. Since migration 0001 an issue row also carries `regressed_at`, `regressions`, `resolved_at`,
`resolved_version`, `last_frames_json` and `fp_version`, and a report row `exception_code`, `fault_module`,
`fault_offset` and `fp_version`; `GET /v1/issues/:fp`'s `frames` falls back to the issue's
`last_frames_json` once every report has been purged. `has_dump`/`dump_bytes` are derived from the `dump`
part actually received, never from the summary's own claim.
