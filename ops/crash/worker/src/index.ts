import { hasValidAccess } from "./access.js";
import { discordSink, type AlertSink } from "./alerts.js";
import { computeFingerprint, deriveTitle, FINGERPRINT_VERSION, type GroupingInput } from "./grouping.js";
import { purgeExpiredReports, reportObjectKeys, type PurgeResult } from "./retention.js";
import { symbolicate } from "./symbolicate.js";
import { D1Store, parseFramesJson, type Store } from "./store.js";
import { validateSummary } from "./validate.js";
import { ISSUE_STATUSES, type Env, type IssueStatus } from "./types.js";

/** Total multipart body cap — matches `Crash.Bundle.CapBytes` on the client (plan §I ingest contract). */
export const CAP_BYTES = 20 * 1024 * 1024;

/** R2 keys per delete call — R2's own limit. */
const R2_DELETE_MAX_KEYS = 1000;

const PART_FILES: Record<string, string> = {
  summary: "summary.json",
  report: "report.txt",
  tail: "log-tail.txt",
  dump: "minidump.dmp",
};

/** What a request handler needs besides env/store, injected so tests can record alerts and fix the clock
 *  (production: `discordSink` + `Date.now`, see the default export). */
export interface Deps {
  alerts: AlertSink;
  now: () => number;
}

/** No CORS headers: the dashboard is served from this Worker's own hostname, so every caller is same-origin
 *  (crash-hosting-implementation.md). */
function json(body: unknown, status: number): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function hasValidIngestKey(request: Request, env: Env): boolean {
  const key = request.headers.get("X-Wavee-Ingest");
  return !!key && key === env.INGEST_KEY;
}

/** Rate limiting binding key. This is the ONLY place the connecting IP is read; it is handed straight
 *  to Cloudflare's own rate-limiting infrastructure and never appears in a D1 row, an R2 object, or a
 *  console.log call anywhere in this worker — see PRIVACY.md "Crash reports (opt-in)". */
function rateLimitKey(request: Request): string {
  return request.headers.get("cf-connecting-ip") ?? "unknown";
}

/** `?since=` on the dashboard read routes accepts either an ISO timestamp or a bare number of days —
 *  crash-dashboard-implementation.md §4 (`GET /v1/stats?since=<iso|days>`). Missing → `defaultDays` ago. */
function resolveSince(param: string | null, defaultDays: number, nowMs: number): string {
  if (!param) return daysAgoIso(defaultDays, nowMs);
  if (/^\d+$/.test(param)) return daysAgoIso(Number(param), nowMs);
  return param;
}

function daysAgoIso(days: number, nowMs: number): string {
  return new Date(nowMs - days * 24 * 60 * 60 * 1000).toISOString();
}

function clampLimit(param: string | null, fallback: number, max: number): number {
  const n = param ? Number(param) : NaN;
  if (!Number.isFinite(n) || n <= 0) return fallback;
  return Math.min(Math.floor(n), max);
}

// Workers' FormData is the standard Fetch one: a part with a filename in its Content-Disposition comes
// back as a File, one without comes back as a string. @cloudflare/workers-types' `FormData.get` is
// typed as `string | null` only (it does not model the File branch), so callers here go through a cast.
function formEntry(form: FormData, name: string): File | string | null {
  return form.get(name) as unknown as File | string | null;
}

async function partToText(entry: File | string | null): Promise<string | null> {
  if (entry === null) return null;
  if (typeof entry === "string") return entry;
  return entry.text();
}

function utf8Length(s: string): number {
  return new TextEncoder().encode(s).length;
}

/** Deletes R2 objects in calls of at most R2_DELETE_MAX_KEYS keys (one subrequest each). */
async function deleteObjects(bucket: R2Bucket, keys: readonly string[]): Promise<void> {
  for (let i = 0; i < keys.length; i += R2_DELETE_MAX_KEYS) {
    await bucket.delete(keys.slice(i, i + R2_DELETE_MAX_KEYS));
  }
}

export async function handleReport(request: Request, env: Env, store: Store, deps: Deps): Promise<Response> {
  if (!hasValidIngestKey(request, env)) {
    return json({ error: "unauthorized" }, 401);
  }

  const rl = await env.RATE.limit({ key: rateLimitKey(request) });
  if (!rl.success) {
    return json({ error: "rate limited" }, 429);
  }

  const contentLength = request.headers.get("content-length");
  if (contentLength && Number(contentLength) > CAP_BYTES) {
    return json({ error: "payload too large" }, 413);
  }

  let form: FormData;
  try {
    form = await request.formData();
  } catch {
    return json({ error: "malformed multipart body" }, 400);
  }

  const summaryText = await partToText(formEntry(form, "summary"));
  const reportText = await partToText(formEntry(form, "report"));
  const tailText = await partToText(formEntry(form, "tail"));
  const dumpEntry = formEntry(form, "dump");
  const dumpBuffer =
    dumpEntry !== null && typeof dumpEntry !== "string" ? await dumpEntry.arrayBuffer() : null;

  if (summaryText === null || reportText === null || tailText === null) {
    return json({ error: "missing required part (summary, report, tail)" }, 400);
  }

  const totalBytes =
    utf8Length(summaryText) + utf8Length(reportText) + utf8Length(tailText) + (dumpBuffer?.byteLength ?? 0);
  if (totalBytes > CAP_BYTES) {
    return json({ error: "payload too large" }, 413);
  }

  let summaryJson: unknown;
  try {
    summaryJson = JSON.parse(summaryText);
  } catch {
    return json({ error: "summary is not valid JSON" }, 400);
  }
  const validated = validateSummary(summaryJson);
  if (!validated.ok) {
    return json({ error: validated.error }, 400);
  }
  const summary = validated.value;

  // plan §J: an install that asked to be forgotten stays forgotten — refuse new reports under the
  // same id rather than silently re-admitting data for it. The client treats 410 like any other 4xx
  // (drop the outbox item); reporting itself isn't disabled, `Crash.Uploader.DeleteRemote` rotates the
  // install id afterwards so the *next* report, if any, arrives under a fresh, unlinkable id.
  if (await store.isInstallDeleted(summary.installId)) {
    return json({ error: "install id deleted" }, 410);
  }

  if (await store.reportExists(summary.reportId)) {
    return json({ error: "duplicate report id" }, 409);
  }

  // has_dump/dump_bytes come from the part actually received, never from summary.hasDump (#165).
  const dumpBytes = dumpBuffer?.byteLength ?? 0;
  const hasDump = dumpBytes > 0;

  const prefix = `reports/${summary.quad}/${summary.reportId}/`;
  await env.BUCKET.put(prefix + "summary.json", summaryText, {
    httpMetadata: { contentType: "application/json" },
  });
  await env.BUCKET.put(prefix + "report.txt", reportText, {
    httpMetadata: { contentType: "text/plain; charset=utf-8" },
  });
  await env.BUCKET.put(prefix + "log-tail.txt", tailText, {
    httpMetadata: { contentType: "text/plain; charset=utf-8" },
  });
  if (dumpBuffer && hasDump) {
    await env.BUCKET.put(prefix + "minidump.dmp", dumpBuffer, {
      httpMetadata: { contentType: "application/octet-stream" },
    });
  }

  const receivedAt = new Date(deps.now()).toISOString();
  const sym = await symbolicate(env.BUCKET, summary.quad, summary.arch, summary.rvas, deps.now());
  if (sym.debugId !== null && sym.entryCount !== null) {
    await store.touchSymbolsMeta(summary.quad, summary.arch, sym.debugId, sym.entryCount, receivedAt);
  }

  // Grouping v2 (src/grouping.ts, plan appendix A1).
  const g: GroupingInput = {
    kind: summary.kind,
    exceptionType: summary.exceptionType,
    exitCode: summary.exitCode,
    exceptionCode: summary.exceptionCode,
    faultModule: summary.faultModule,
    faultOffset: summary.faultOffset,
    frames: sym.frames,
  };
  const fingerprint = await computeFingerprint(g);
  const title = deriveTitle(g);
  const framesJson = JSON.stringify(sym.frames);

  const newInstall = !(await store.installSeenForIssue(fingerprint, summary.installId)); // BEFORE the insert
  await store.insertReport({
    summary,
    framesJson,
    fingerprint,
    fpVersion: FINGERPRINT_VERSION,
    receivedAt,
    hasDump,
    dumpBytes,
  });
  const transition = await store.recordIssueOccurrence({
    fingerprint,
    title,
    kind: summary.kind,
    semver: summary.version,
    receivedAt,
    newInstall,
    framesJson,
    fpVersion: FINGERPRINT_VERSION,
  });

  // Alerts only for builds whose symmap we hold: the ingest key ships in every install, so a made-up quad must not
  // be able to post to Discord (plan §W1 alerts).
  if (transition !== "existing" && sym.debugId !== null) {
    const issue = await store.getIssue(fingerprint);
    if (issue) {
      deps.alerts.issueEvent({
        transition,
        fingerprint,
        title: issue.title,
        kind: summary.kind,
        semver: summary.version,
        quad: summary.quad,
        arch: summary.arch,
        channel: summary.channel,
        count: issue.count,
        installs: issue.installs,
        githubIssue: issue.github_issue,
        dashboardOrigin: new URL(request.url).origin,
        at: receivedAt,
      });
    }
  }

  return json({ id: summary.reportId }, 201);
}

async function handleListIssues(url: URL, store: Store): Promise<Response> {
  const issues = await store.listIssues({
    since: url.searchParams.get("since") ?? undefined,
    version: url.searchParams.get("version") ?? undefined,
    status: url.searchParams.get("status") ?? undefined,
  });
  return json({ issues }, 200);
}

async function handleIssueDetail(store: Store, fingerprint: string, nowMs: number): Promise<Response> {
  const issue = await store.getIssue(fingerprint);
  if (!issue) return json({ error: "not found" }, 404);

  // crash-dashboard-implementation.md §4: the issue detail page's Stack/Occurrences/Environment tabs
  // and the aside cards (sparkline, breakdowns) all come off this one call.
  const [reports, latestFrames, occurrences, sparkline14d, breakdowns] = await Promise.all([
    store.listReportsForIssue(fingerprint, 50),
    store.getLatestFrames(fingerprint),
    store.getOccurrences(fingerprint, 50),
    store.getSparkline(fingerprint, daysAgoIso(14, nowMs)),
    store.getBreakdowns(fingerprint),
  ]);
  // Every report may have been purged (90 days); the issue keeps the newest stack in last_frames_json.
  const frames = latestFrames ?? parseFramesJson(issue.last_frames_json);
  return json({ issue, reports, frames, occurrences, sparkline14d, breakdowns }, 200);
}

async function handlePatchIssue(request: Request, store: Store, fingerprint: string, nowMs: number): Promise<Response> {
  let body: unknown;
  try {
    body = await request.json();
  } catch {
    return json({ error: "malformed JSON body" }, 400);
  }
  const o = (typeof body === "object" && body !== null ? body : {}) as Record<string, unknown>;
  if (o.status !== undefined && !(ISSUE_STATUSES as readonly unknown[]).includes(o.status)) {
    return json({ error: `status must be one of ${ISSUE_STATUSES.join(", ")}` }, 400);
  }
  const status = o.status as IssueStatus | undefined;
  const githubIssue = typeof o.github_issue === "number" ? o.github_issue : undefined;
  const ok = await store.patchIssue(fingerprint, { status, githubIssue }, new Date(nowMs).toISOString());
  if (!ok) return json({ error: "not found" }, 404);
  const issue = await store.getIssue(fingerprint);
  return json({ issue }, 200);
}

async function handleReportDetail(store: Store, id: string, nowMs: number): Promise<Response> {
  const report = await store.getReport(id);
  if (!report) return json({ error: "not found" }, 404);
  // crash-dashboard-implementation.md §4: Report detail's "This install" card.
  const thisInstall = await store.getThisInstall(report.install_id, daysAgoIso(30, nowMs));
  return json({ report, this_install: thisInstall }, 200);
}

/** `DELETE /v1/reports/:id` — the dashboard's "Delete report" (#165): the report's R2 objects and row go, and its
 *  issue is recomputed from the remaining reports (deleted with its last one). No tombstone — that is an install's
 *  erasure, not one report's. */
async function handleDeleteReport(env: Env, store: Store, id: string): Promise<Response> {
  const ref = await store.getReportRef(id);
  if (!ref) return json({ error: "not found" }, 404);
  await deleteObjects(env.BUCKET, reportObjectKeys(ref));
  await store.deleteReport(id);
  await store.recomputeOrDeleteIssue(ref.fingerprint);
  return json({ deleted: 1, fingerprint: ref.fingerprint }, 200);
}

async function handleReportPart(env: Env, store: Store, id: string, part: string): Promise<Response> {
  const fileName = PART_FILES[part];
  if (!fileName) return json({ error: "unknown part" }, 404);

  const report = await store.getReport(id);
  if (!report) return json({ error: "not found" }, 404);

  const objectKey = `reports/${report.quad}/${id}/${fileName}`;
  const obj = await env.BUCKET.get(objectKey);
  if (!obj) return json({ error: "not found" }, 404);

  if (part === "dump") {
    return new Response(obj.body, {
      status: 200,
      headers: {
        "Content-Type": "application/octet-stream",
        "Content-Disposition": `attachment; filename="${id}-minidump.dmp"`,
      },
    });
  }

  const contentType = part === "summary" ? "application/json" : "text/plain; charset=utf-8";
  const text = await obj.text();
  return new Response(text, { status: 200, headers: { "Content-Type": contentType } });
}

async function handleVersions(store: Store): Promise<Response> {
  const versions = await store.listVersions();
  return json({ versions }, 200);
}

/** `GET /v1/stats?since=<iso|days>` — the Overview page's stat cards + charts. */
async function handleStats(url: URL, store: Store, nowMs: number): Promise<Response> {
  const since = resolveSince(url.searchParams.get("since"), 30, nowMs);
  const stats = await store.getStats(since);
  return json(stats, 200);
}

/** `GET /v1/reports?since=&kind=&quad=&q=&limit=&cursor=` — the Reports page's paged grid. `q` matches
 *  a report id or install id prefix. Keyset-paginated on `(received_at, id)` — see `store.ts`'s cursor
 *  helpers — rather than `OFFSET`, so a page doesn't shift under a caller as new reports arrive. */
async function handleReportsList(url: URL, store: Store): Promise<Response> {
  const page = await store.listReportsPage({
    since: url.searchParams.get("since") ?? undefined,
    kind: url.searchParams.get("kind") ?? undefined,
    quad: url.searchParams.get("quad") ?? undefined,
    q: url.searchParams.get("q") ?? undefined,
    limit: clampLimit(url.searchParams.get("limit"), 50, 200),
    cursor: url.searchParams.get("cursor") ?? undefined,
  });
  return json({ reports: page.rows, nextCursor: page.nextCursor }, 200);
}

/** `GET /v1/symbols` — the Symbols page: every `.symmap` the Worker has actually resolved a report
 *  against (see `schema.sql`'s note on why `uploaded_at` here means "first seen", not "uploaded"). */
async function handleSymbolsList(store: Store): Promise<Response> {
  const symbols = await store.listSymbols();
  return json({ symbols }, 200);
}

/**
 * `DELETE /v1/installs/:installId` — right to erasure (plan §J). Accepted with EITHER the app's own
 * `X-Wavee-Ingest` key (the in-product "Delete my data" button, `Crash.Uploader.DeleteRemote`) OR a
 * verified Cloudflare Access JWT (the dashboard's Report detail → "Delete this install's data"). The path
 * sits under an Access Bypass so the app can reach it, which means Access neither blocks a forged
 * `Cf-Access-Jwt-Assertion` here nor injects one — the dashboard's call carries only the
 * `CF_Authorization` cookie, and `hasValidAccess` verifies whichever is present.
 */
async function handleDeleteInstall(
  request: Request,
  env: Env,
  store: Store,
  installId: string,
  nowMs: number,
): Promise<Response> {
  if (!hasValidIngestKey(request, env) && !(await hasValidAccess(request, env))) {
    return json({ error: "unauthorized" }, 401);
  }

  const rl = await env.RATE.limit({ key: rateLimitKey(request) });
  if (!rl.success) {
    return json({ error: "rate limited" }, 429);
  }

  const refs = await store.listReportRefsForInstall(installId);
  if (refs.length === 0) {
    return json({ error: "not found" }, 404);
  }

  // One R2 call per 250 reports, not one per report: the free plan allows 50 subrequests per invocation.
  await deleteObjects(env.BUCKET, refs.flatMap(reportObjectKeys));

  await store.deleteReportsForInstall(installId);

  const fingerprints = new Set(refs.map((r) => r.fingerprint));
  for (const fingerprint of fingerprints) {
    await store.recomputeOrDeleteIssue(fingerprint);
  }

  await store.tombstoneInstall(installId, new Date(nowMs).toISOString());

  return json({ deleted: refs.length }, 200);
}

/** The retention purge (src/retention.ts) — run daily by `scheduled()` and on demand by `POST /v1/retention/run`.
 *  Logs counts only. */
export async function runRetention(env: Env, nowMs: number, store: Store = new D1Store(env.DB)): Promise<PurgeResult> {
  const r = await purgeExpiredReports(store, env.BUCKET, nowMs);
  console.log(JSON.stringify({ event: "retention.purge", ...r }));
  return r;
}

/** Routes GET/PATCH/DELETE/POST behind a verified Cloudflare Access JWT (`access.ts`: signature, `aud`, `iss`,
 *  expiry — never the header's mere presence); `POST /v1/report` is the app's own ingest call and is authenticated
 *  by `X-Wavee-Ingest` instead (see plan §C route table). */
async function routeAccessGated(request: Request, url: URL, env: Env, store: Store, deps: Deps): Promise<Response> {
  if (!(await hasValidAccess(request, env))) {
    return json({ error: "unauthorized" }, 401);
  }

  if (url.pathname === "/v1/issues" && request.method === "GET") {
    return handleListIssues(url, store);
  }
  if (url.pathname === "/v1/versions" && request.method === "GET") {
    return handleVersions(store);
  }
  if (url.pathname === "/v1/stats" && request.method === "GET") {
    return handleStats(url, store, deps.now());
  }
  if (url.pathname === "/v1/reports" && request.method === "GET") {
    return handleReportsList(url, store);
  }
  if (url.pathname === "/v1/symbols" && request.method === "GET") {
    return handleSymbolsList(store);
  }
  if (url.pathname === "/v1/retention/run" && request.method === "POST") {
    return json(await runRetention(env, deps.now(), store), 200);
  }
  const issueMatch = /^\/v1\/issues\/([^/]+)$/.exec(url.pathname);
  if (issueMatch && request.method === "GET") {
    return handleIssueDetail(store, decodeURIComponent(issueMatch[1]!), deps.now());
  }
  if (issueMatch && request.method === "PATCH") {
    return handlePatchIssue(request, store, decodeURIComponent(issueMatch[1]!), deps.now());
  }
  const reportPartMatch = /^\/v1\/reports\/([^/]+)\/([^/]+)$/.exec(url.pathname);
  if (reportPartMatch && request.method === "GET") {
    return handleReportPart(env, store, decodeURIComponent(reportPartMatch[1]!), reportPartMatch[2]!);
  }
  const reportMatch = /^\/v1\/reports\/([^/]+)$/.exec(url.pathname);
  if (reportMatch && request.method === "GET") {
    return handleReportDetail(store, decodeURIComponent(reportMatch[1]!), deps.now());
  }
  if (reportMatch && request.method === "DELETE") {
    return handleDeleteReport(env, store, decodeURIComponent(reportMatch[1]!));
  }
  return json({ error: "not found" }, 404);
}

/** wrangler.toml's `run_worker_first = ["/v1/*"]` means only `/v1/*` reaches this; every other path is the
 *  dashboard's static assets, served by Cloudflare directly. */
export async function route(request: Request, env: Env, store: Store, deps: Deps): Promise<Response> {
  const url = new URL(request.url);

  if (url.pathname === "/v1/report" && request.method === "POST") return handleReport(request, env, store, deps);
  const deleteInstallMatch = /^\/v1\/installs\/([^/]+)$/.exec(url.pathname);
  if (deleteInstallMatch && request.method === "DELETE") {
    return handleDeleteInstall(request, env, store, decodeURIComponent(deleteInstallMatch[1]!), deps.now());
  }
  if (url.pathname.startsWith("/v1/")) return routeAccessGated(request, url, env, store, deps);
  return json({ error: "not found" }, 404);
}

export default {
  /** `ctx` is optional so tests can call `fetch(request, env)`; without one, an alert is fire-and-forget. */
  async fetch(request: Request, env: Env, ctx?: ExecutionContext): Promise<Response> {
    try {
      return await route(request, env, new D1Store(env.DB), { alerts: discordSink(env, ctx), now: () => Date.now() });
    } catch (err) {
      // Deliberately logs only the error, never the request (which could carry cf-connecting-ip).
      console.error("crash-worker unhandled error", err instanceof Error ? err.message : String(err));
      return json({ error: "internal error" }, 500);
    }
  },

  /** wrangler.toml `[triggers]` — the daily retention purge. */
  scheduled(controller: ScheduledController, env: Env, ctx: ExecutionContext): void {
    ctx.waitUntil(runRetention(env, controller.scheduledTime));
  },
};
