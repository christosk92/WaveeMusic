# Crash service hosting on `crash.cproducts.dev`

Follows `crash-diagnostics-implementation.md` (§C service) and `crash-dashboard-implementation.md`. Decided with the
owner 2026-10-01; nothing was deployed before this, so there is no migration — the old shape is replaced outright.

## Decisions (2026-10-01)

- **Hostname `crash.cproducts.dev`** — the one URL every release bakes in (`-CrashIngestUrl`), ours for good.
- **cproducts.dev's DNS moves from Vercel to Cloudflare** (registration stays at Vercel, exactly like
  `canijustprint.com`, already on `lars/zara.ns.cloudflare.com`). Why: a Worker Custom Domain needs an active
  Cloudflare zone, and Access can only gate a hostname on one; a CNAME from Vercel DNS can reach Pages but never Access.
- **One hostname**: the Worker serves the dashboard's built `dist/` as static assets next to `/v1/*`. The Pages
  project, `DASHBOARD_ORIGIN`, CORS (`cors.ts`) and `VITE_API_BASE` are deleted — everything is same-origin.
- **The Worker verifies the Access JWT** (RS256 against the team's certs, `aud`, `iss`, expiry) instead of checking
  that a `Cf-Access-Jwt-Assertion` header merely exists. Required now, not a nicety: on one hostname the app's two
  routes (`POST /v1/report`, `DELETE /v1/installs/:id`) sit under an Access **Bypass**, where nothing stops a caller
  forging that header — and the dashboard's own erasure call arrives there carrying only the `CF_Authorization` cookie.
- `workers_dev = false`, `preview_urls = false`: no hostname reaches the Worker without Access in front of it.

```
                    crash.cproducts.dev  (zone on Cloudflare, Worker Custom Domain)
                                 │
            ┌────────────────────┴───────────────────────────┐
            │ Cloudflare Access                               │
            │  app "Wavee crashes"       host, all paths  → Allow (owner's e-mail)
            │  app "Wavee crash ingest"  /v1/report       → Bypass (Everyone)
            │                            /v1/installs     → Bypass (Everyone)
            └────────────────────┬───────────────────────────┘
                                 │
        /v1/*  → Worker (run_worker_first)            everything else → static assets (dashboard dist/, SPA fallback)
          POST   /v1/report              X-Wavee-Ingest
          DELETE /v1/installs/:id        X-Wavee-Ingest  OR  verified Access JWT (header or CF_Authorization cookie)
          GET/PATCH every other /v1/*    verified Access JWT (header or cookie)
```

## Worker (`ops/crash/worker`)

### `wrangler.toml` (top-level keys must precede the first table)

```toml
name = "wavee-crash"
main = "src/index.ts"
compatibility_date = "2025-09-01"
compatibility_flags = ["nodejs_compat"]

# Served ONLY on crash.cproducts.dev (zone on Cloudflare — README step 0). workers.dev and preview URLs are off so
# no hostname reaches this Worker without Cloudflare Access in front of it.
workers_dev = false
preview_urls = false
routes = [{ pattern = "crash.cproducts.dev", custom_domain = true }]

# The dashboard (ops/crash/dashboard — `npm run deploy` builds it first) on the same hostname. Cloudflare serves
# assets directly (behind Access); the Worker runs only for /v1/*. Unknown paths fall back to index.html (SPA routes).
[assets]
directory = "../dashboard/dist"
not_found_handling = "single-page-application"
run_worker_first = ["/v1/*"]

# … [[d1_databases]], [[r2_buckets]], [[ratelimits]] unchanged …

[vars]
INGEST_KEY = "dev-only-placeholder-set-a-real-secret-before-deploy"
# Cloudflare Access (README step 6). Public values, not secrets. Placeholders fail closed: every Access-gated route
# answers 401 until both are real.
ACCESS_TEAM_DOMAIN = "https://REPLACE_WITH_TEAM.cloudflareaccess.com"
ACCESS_AUD = "REPLACE_WITH_APPLICATION_AUD_TAG"
```

### `src/access.ts` (new)

```ts
import { createRemoteJWKSet, jwtVerify, type JWTVerifyGetKey } from "jose";
import type { Env } from "./types.js";

/** One remote key set per team domain for the isolate's lifetime. jose caches the fetched keys and refetches on an
 *  unknown `kid` (Access rotates its signing keys), with its own cooldown. */
const keySets = new Map<string, JWTVerifyGetKey>();

function keySet(team: string): JWTVerifyGetKey {
  let ks = keySets.get(team);
  if (!ks) {
    ks = createRemoteJWKSet(new URL(`${team}/cdn-cgi/access/certs`));
    keySets.set(team, ks);
  }
  return ks;
}

/** Both Access settings are real values, not wrangler.toml's REPLACE_ placeholders. */
export function accessConfigured(env: Env): boolean {
  return /^https:\/\/[a-z0-9-]+\.cloudflareaccess\.com$/.test(env.ACCESS_TEAM_DOMAIN)
    && env.ACCESS_AUD.length > 0 && !env.ACCESS_AUD.startsWith("REPLACE");
}

/** The Access JWT: the header Access injects on gated paths, else the CF_Authorization cookie — the only carrier on
 *  the Bypass paths, where the dashboard's "Delete this install's data" call lands. */
export function accessToken(request: Request): string | null {
  const header = request.headers.get("Cf-Access-Jwt-Assertion");
  if (header) return header;
  const cookie = request.headers.get("Cookie");
  if (!cookie) return null;
  for (const part of cookie.split(";")) {
    const eq = part.indexOf("=");
    if (eq > 0 && part.slice(0, eq).trim() === "CF_Authorization") return part.slice(eq + 1).trim() || null;
  }
  return null;
}

/** RS256 signature against the team's published keys, `aud` = this application's AUD tag, `iss` = the team domain,
 *  expiry. Fails closed: unconfigured, missing, forged, foreign or expired → false. */
export async function hasValidAccess(request: Request, env: Env): Promise<boolean> {
  if (!accessConfigured(env)) return false;
  const token = accessToken(request);
  if (!token) return false;
  try {
    await jwtVerify(token, keySet(env.ACCESS_TEAM_DOMAIN), {
      issuer: env.ACCESS_TEAM_DOMAIN,
      audience: env.ACCESS_AUD,
      algorithms: ["RS256"],
    });
    return true;
  } catch {
    return false;
  }
}
```

### `src/index.ts`, `src/types.ts`, `src/cors.ts`

- Delete `src/cors.ts`. `json()` and `handleReportPart` lose `...corsHeaders(...)`; `route()` loses the `OPTIONS` line.
- Delete `requireAccess` and its comment. `routeAccessGated`: `if (!(await hasValidAccess(request, env))) → 401`.
  `handleDeleteInstall`: `if (!hasValidIngestKey(request, env) && !(await hasValidAccess(request, env))) → 401`.
- `Env`: drop `DASHBOARD_ORIGIN`; add `ACCESS_TEAM_DOMAIN: string; ACCESS_AUD: string;` (doc comments say where they
  come from). No `ASSETS` binding — the Worker never serves assets itself.

### `package.json` scripts

```json
"dev": "npm --prefix ../dashboard run build && npx wrangler@latest dev --local",
"deploy": "npm --prefix ../dashboard run build && npx wrangler@latest deploy"
```
(`jose` ^6 is already a dependency.)

### Tests

- Shared helper (`test/access.ts`): one RS256 key pair (`generateKeyPair("RS256")`, `exportJWK` + `kid`), team
  `https://wavee-test.cloudflareaccess.com`, aud `test-aud`; `vi.stubGlobal("fetch", …)` answers the certs URL with
  `{ keys: [jwk] }` (jose's `fetchJwks` defaults to the global `fetch` at call time); `signAccess(overrides)` →
  token; `accessHeader()` / `accessCookie()`.
- Every `makeEnv` gets `ACCESS_TEAM_DOMAIN`/`ACCESS_AUD` and loses `DASHBOARD_ORIGIN`; every `"opaque-jwt"` becomes a
  real token; the CORS test is deleted; test hostnames become `crash.cproducts.dev`.
- New `test/access.test.ts`: valid header → 200; valid cookie only → 200; opaque/forged header → 401 (the old check
  admitted it); wrong `aud`, wrong `iss`, expired, signed by another key → 401; placeholder config with an otherwise
  valid token → 401; `DELETE /v1/installs/:id` with a forged header → 401, with a valid cookie → not 401.

## Dashboard (`ops/crash/dashboard`)

- `src/api/client.ts`: no `API_BASE`, no `credentials: "include"` (same-origin `fetch` sends the Access cookie by
  default); paths stay `/v1/...`.
- `src/pages/ReportDetail/parts.tsx`: `apiBase()` deleted; fetches use relative paths; any URL shown to a human (the
  WinDbg/download one-liner) uses `window.location.origin`.
- `src/app/AppFrame.tsx`: caption `crash.cproducts.dev`.
- README: served by the Worker on `crash.cproducts.dev` (no Pages); all seven pages are real (the "placeholders"
  paragraph is stale); `VITE_API_BASE` gone; real data only on the deployed host (locally Access verification fails
  closed, so `/v1/*` answers 401 — use mock mode).

## Runbook (`ops/crash/README.md`) and guides

- New **step 0 — move cproducts.dev DNS to Cloudflare**. Records today on Vercel DNS (`vercel dns ls cproducts.dev`,
  2026-10-01) and what to recreate:

  | Type | Name | Value | Proxy |
  |---|---|---|---|
  | MX | `@` | `mx1.improvmx.com` priority 10 | — |
  | MX | `@` | `mx2.improvmx.com` priority 20 | — |
  | TXT | `@` | `v=spf1 include:spf.improvmx.com ~all` | — |
  | CNAME (flattened) | `@` | `f93bd6a5b1bd3ebb.vercel-dns-017.com` (or `A 76.76.21.21`) | DNS only |
  | CNAME | `www` | `cname.vercel-dns-017.com` | DNS only |

  Not recreated: Vercel's default `*` wildcard (no project uses one — `vercel domains inspect` lists only
  `cproducts.dev`, `www.cproducts.dev`) and its default CAA set. `crash` is created by `wrangler deploy` itself.
  Then Vercel → Domains → cproducts.dev → Nameservers → the pair Cloudflare assigns; wait for the zone to go Active;
  check the site, `www`, and mail (MX) still resolve, and Vercel's domain page shows Valid Configuration.
- Step 5 deploy builds the dashboard and attaches the custom domain; step 6 Access becomes the two applications in the
  diagram, then the AUD tag of "Wavee crashes" + the team domain go into `wrangler.toml` `[vars]` and redeploy. Step 6
  verification: private window → login page; `curl -X POST …/v1/report -H "X-Wavee-Ingest: wrong"` → JSON 401 (not a
  302 to login); same for `DELETE …/v1/installs/x`; `curl …/v1/issues` → 302 to login; `DELETE …/v1/installs/x` with a
  forged `Cf-Access-Jwt-Assertion` → 401.
- Route table auth column: "verified Access JWT (header or `CF_Authorization` cookie)".
- `docs/guide/crash-diagnostics.md` §1 diagram + §8, `docs/guide/releasing-wavee.md` (`-CrashIngestUrl
  https://crash.cproducts.dev`): Pages and `crash.wavee.app` replaced.

## Work packages (disjoint files)

| Agent | Files |
|---|---|
| W · Worker | `ops/crash/worker/{wrangler.toml, package.json scripts, src/access.ts, src/index.ts, src/types.ts, src/cors.ts (delete), test/**}` |
| D · Dashboard + docs | `ops/crash/dashboard/{src/api/client.ts, src/pages/ReportDetail/parts.tsx, src/app/AppFrame.tsx, README.md}`, `ops/crash/README.md`, `docs/guide/crash-diagnostics.md`, `docs/guide/releasing-wavee.md` |

Gates (orchestrator): worker `npm test`, `npm run typecheck`, `npm run typecheck:test`; dashboard `npm test`,
`npm run build`.
