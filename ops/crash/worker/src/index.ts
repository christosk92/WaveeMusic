import { corsHeaders, handleOptions } from "./cors.js";
import { computeFingerprint } from "./fingerprint.js";
import { symbolicate } from "./symbolicate.js";
import { D1Store, deriveTitle, type Store } from "./store.js";
import { validateSummary } from "./validate.js";
import type { Env } from "./types.js";

/** Total multipart body cap — matches `Crash.Bundle.CapBytes` on the client (plan §I ingest contract). */
export const CAP_BYTES = 20 * 1024 * 1024;

const PART_FILES: Record<string, string> = {
  summary: "summary.json",
  report: "report.txt",
  tail: "log-tail.txt",
  dump: "minidump.dmp",
};

function json(body: unknown, status: number, request: Request, env: Env): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json", ...corsHeaders(request, env) },
  });
}

/**
 * Cloudflare Access sits in front of this worker's route (zone-level, configured in the dashboard —
 * see ops/crash/README.md "Configure Access"). We do not verify the JWT ourselves: Access already
 * refused the request before it reached the worker if the visitor wasn't authenticated, and it always
 * attaches this header on a request it let through. Checking for the header's mere presence is a cheap
 * defence-in-depth if the worker were ever exposed on a route Access doesn't cover — it is NOT a
 * substitute for verifying the JWT's signature/audience.
 * TODO(#12 follow-up): verify the `Cf-Access-Jwt-Assertion` JWT signature against the team's public keys
 * if this worker ever needs to run on a route Access does not gate — see
 * https://developers.cloudflare.com/cloudflare-one/identity/authorization-cookie/validating-json/
 */
function requireAccess(request: Request): boolean {
  return request.headers.get("Cf-Access-Jwt-Assertion") !== null;
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
function resolveSince(param: string | null, defaultDays: number): string {
  if (!param) return daysAgoIso(defaultDays);
  if (/^\d+$/.test(param)) return daysAgoIso(Number(param));
  return param;
}

function daysAgoIso(days: number): string {
  return new Date(Date.now() - days * 24 * 60 * 60 * 1000).toISOString();
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

export async function handleReport(request: Request, env: Env, store: Store): Promise<Response> {
  if (!hasValidIngestKey(request, env)) {
    return json({ error: "unauthorized" }, 401, request, env);
  }

  const rl = await env.RATE.limit({ key: rateLimitKey(request) });
  if (!rl.success) {
    return json({ error: "rate limited" }, 429, request, env);
  }

  const contentLength = request.headers.get("content-length");
  if (contentLength && Number(contentLength) > CAP_BYTES) {
    return json({ error: "payload too large" }, 413, request, env);
  }

  let form: FormData;
  try {
    form = await request.formData();
  } catch {
    return json({ error: "malformed multipart body" }, 400, request, env);
  }

  const summaryText = await partToText(formEntry(form, "summary"));
  const reportText = await partToText(formEntry(form, "report"));
  const tailText = await partToText(formEntry(form, "tail"));
  const dumpEntry = formEntry(form, "dump");
  const dumpBuffer =
    dumpEntry !== null && typeof dumpEntry !== "string" ? await dumpEntry.arrayBuffer() : null;

  if (summaryText === null || reportText === null || tailText === null) {
    return json({ error: "missing required part (summary, report, tail)" }, 400, request, env);
  }

  const totalBytes =
    utf8Length(summaryText) + utf8Length(reportText) + utf8Length(tailText) + (dumpBuffer?.byteLength ?? 0);
  if (totalBytes > CAP_BYTES) {
    return json({ error: "payload too large" }, 413, request, env);
  }

  let summaryJson: unknown;
  try {
    summaryJson = JSON.parse(summaryText);
  } catch {
    return json({ error: "summary is not valid JSON" }, 400, request, env);
  }
  const validated = validateSummary(summaryJson);
  if (!validated.ok) {
    return json({ error: validated.error }, 400, request, env);
  }
  const summary = validated.value;

  // plan §J: an install that asked to be forgotten stays forgotten — refuse new reports under the
  // same id rather than silently re-admitting data for it. The client treats 410 like any other 4xx
  // (drop the outbox item); reporting itself isn't disabled, `Crash.Uploader.DeleteRemote` rotates the
  // install id afterwards so the *next* report, if any, arrives under a fresh, unlinkable id.
  if (await store.isInstallDeleted(summary.installId)) {
    return json({ error: "install id deleted" }, 410, request, env);
  }

  if (await store.reportExists(summary.reportId)) {
    return json({ error: "duplicate report id" }, 409, request, env);
  }

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
  if (dumpBuffer) {
    await env.BUCKET.put(prefix + "minidump.dmp", dumpBuffer, {
      httpMetadata: { contentType: "application/octet-stream" },
    });
  }

  const { frames, debugId, entryCount } = await symbolicate(env.BUCKET, summary.quad, summary.arch, summary.rvas);
  if (debugId !== null && entryCount !== null) {
    await store.touchSymbolsMeta(summary.quad, summary.arch, debugId, entryCount, new Date().toISOString());
  }

  const fingerprint = await computeFingerprint(summary.kind, summary.exceptionType, frames);
  const receivedAt = new Date().toISOString();

  await store.insertReport({
    summary,
    framesJson: JSON.stringify(frames),
    fingerprint,
    receivedAt,
  });
  await store.upsertIssueFromReports(fingerprint, deriveTitle(summary, frames), summary.kind);

  return json({ id: summary.reportId }, 201, request, env);
}

async function handleListIssues(request: Request, url: URL, env: Env, store: Store): Promise<Response> {
  const issues = await store.listIssues({
    since: url.searchParams.get("since") ?? undefined,
    version: url.searchParams.get("version") ?? undefined,
    status: url.searchParams.get("status") ?? undefined,
  });
  return json({ issues }, 200, request, env);
}

async function handleIssueDetail(request: Request, env: Env, store: Store, fingerprint: string): Promise<Response> {
  const issue = await store.getIssue(fingerprint);
  if (!issue) return json({ error: "not found" }, 404, request, env);

  // crash-dashboard-implementation.md §4: the issue detail page's Stack/Occurrences/Environment tabs
  // and the aside cards (sparkline, breakdowns) all come off this one call.
  const [reports, frames, occurrences, sparkline14d, breakdowns] = await Promise.all([
    store.listReportsForIssue(fingerprint, 50),
    store.getLatestFrames(fingerprint),
    store.getOccurrences(fingerprint, 50),
    store.getSparkline(fingerprint, daysAgoIso(14)),
    store.getBreakdowns(fingerprint),
  ]);
  return json({ issue, reports, frames, occurrences, sparkline14d, breakdowns }, 200, request, env);
}

async function handlePatchIssue(request: Request, env: Env, store: Store, fingerprint: string): Promise<Response> {
  let body: unknown;
  try {
    body = await request.json();
  } catch {
    return json({ error: "malformed JSON body" }, 400, request, env);
  }
  const o = (typeof body === "object" && body !== null ? body : {}) as Record<string, unknown>;
  const status = typeof o.status === "string" ? o.status : undefined;
  const githubIssue = typeof o.github_issue === "number" ? o.github_issue : undefined;
  const ok = await store.patchIssue(fingerprint, { status, githubIssue });
  if (!ok) return json({ error: "not found" }, 404, request, env);
  const issue = await store.getIssue(fingerprint);
  return json({ issue }, 200, request, env);
}

async function handleReportDetail(request: Request, env: Env, store: Store, id: string): Promise<Response> {
  const report = await store.getReport(id);
  if (!report) return json({ error: "not found" }, 404, request, env);
  // crash-dashboard-implementation.md §4: Report detail's "This install" card.
  const thisInstall = await store.getThisInstall(report.install_id, daysAgoIso(30));
  return json({ report, this_install: thisInstall }, 200, request, env);
}

async function handleReportPart(request: Request, env: Env, store: Store, id: string, part: string): Promise<Response> {
  const fileName = PART_FILES[part];
  if (!fileName) return json({ error: "unknown part" }, 404, request, env);

  const report = await store.getReport(id);
  if (!report) return json({ error: "not found" }, 404, request, env);

  const objectKey = `reports/${report.quad}/${id}/${fileName}`;
  const obj = await env.BUCKET.get(objectKey);
  if (!obj) return json({ error: "not found" }, 404, request, env);

  if (part === "dump") {
    return new Response(obj.body, {
      status: 200,
      headers: {
        "Content-Type": "application/octet-stream",
        "Content-Disposition": `attachment; filename="${id}-minidump.dmp"`,
        ...corsHeaders(request, env),
      },
    });
  }

  const contentType = part === "summary" ? "application/json" : "text/plain; charset=utf-8";
  const text = await obj.text();
  return new Response(text, { status: 200, headers: { "Content-Type": contentType, ...corsHeaders(request, env) } });
}

async function handleVersions(request: Request, env: Env, store: Store): Promise<Response> {
  const versions = await store.listVersions();
  return json({ versions }, 200, request, env);
}

/** `GET /v1/stats?since=<iso|days>` — the Overview page's stat cards + charts. */
async function handleStats(request: Request, url: URL, env: Env, store: Store): Promise<Response> {
  const since = resolveSince(url.searchParams.get("since"), 30);
  const stats = await store.getStats(since);
  return json(stats, 200, request, env);
}

/** `GET /v1/reports?since=&kind=&quad=&q=&limit=&cursor=` — the Reports page's paged grid. `q` matches
 *  a report id or install id prefix. Keyset-paginated on `(received_at, id)` — see `store.ts`'s cursor
 *  helpers — rather than `OFFSET`, so a page doesn't shift under a caller as new reports arrive. */
async function handleReportsList(request: Request, url: URL, env: Env, store: Store): Promise<Response> {
  const page = await store.listReportsPage({
    since: url.searchParams.get("since") ?? undefined,
    kind: url.searchParams.get("kind") ?? undefined,
    quad: url.searchParams.get("quad") ?? undefined,
    q: url.searchParams.get("q") ?? undefined,
    limit: clampLimit(url.searchParams.get("limit"), 50, 200),
    cursor: url.searchParams.get("cursor") ?? undefined,
  });
  return json({ reports: page.rows, nextCursor: page.nextCursor }, 200, request, env);
}

/** `GET /v1/symbols` — the Symbols page: every `.symmap` the Worker has actually resolved a report
 *  against (see `schema.sql`'s note on why `uploaded_at` here means "first seen", not "uploaded"). */
async function handleSymbolsList(request: Request, env: Env, store: Store): Promise<Response> {
  const symbols = await store.listSymbols();
  return json({ symbols }, 200, request, env);
}

const R2_PART_FILES = ["summary.json", "report.txt", "log-tail.txt", "minidump.dmp"];

/**
 * `DELETE /v1/installs/:installId` — right to erasure (plan §J). Accepted with EITHER the app's own
 * `X-Wavee-Ingest` key (the in-product "Delete my data" button, `Crash.Uploader.DeleteRemote`) OR a
 * Cloudflare Access session (the dashboard's Report detail → "Delete this install's data") — unlike
 * every other route, it is not gated by exactly one of those.
 */
async function handleDeleteInstall(request: Request, env: Env, store: Store, installId: string): Promise<Response> {
  if (!hasValidIngestKey(request, env) && !requireAccess(request)) {
    return json({ error: "unauthorized" }, 401, request, env);
  }

  const rl = await env.RATE.limit({ key: rateLimitKey(request) });
  if (!rl.success) {
    return json({ error: "rate limited" }, 429, request, env);
  }

  const refs = await store.listReportRefsForInstall(installId);
  if (refs.length === 0) {
    return json({ error: "not found" }, 404, request, env);
  }

  for (const ref of refs) {
    const prefix = `reports/${ref.quad}/${ref.id}/`;
    await env.BUCKET.delete(R2_PART_FILES.map((f) => prefix + f));
  }

  await store.deleteReportsForInstall(installId);

  const fingerprints = new Set(refs.map((r) => r.fingerprint));
  for (const fingerprint of fingerprints) {
    await store.recomputeOrDeleteIssue(fingerprint);
  }

  await store.tombstoneInstall(installId, new Date().toISOString());

  return json({ deleted: refs.length }, 200, request, env);
}

/** Routes GET/PATCH under Cloudflare Access; `POST /v1/report` is the app's own ingest call and is
 *  authenticated by `X-Wavee-Ingest` instead (see plan §C route table). */
async function routeAccessGated(request: Request, url: URL, env: Env, store: Store): Promise<Response> {
  if (!requireAccess(request)) {
    return json({ error: "unauthorized" }, 401, request, env);
  }

  if (url.pathname === "/v1/issues" && request.method === "GET") {
    return handleListIssues(request, url, env, store);
  }
  if (url.pathname === "/v1/versions" && request.method === "GET") {
    return handleVersions(request, env, store);
  }
  if (url.pathname === "/v1/stats" && request.method === "GET") {
    return handleStats(request, url, env, store);
  }
  if (url.pathname === "/v1/reports" && request.method === "GET") {
    return handleReportsList(request, url, env, store);
  }
  if (url.pathname === "/v1/symbols" && request.method === "GET") {
    return handleSymbolsList(request, env, store);
  }
  const issueMatch = /^\/v1\/issues\/([^/]+)$/.exec(url.pathname);
  if (issueMatch && request.method === "GET") {
    return handleIssueDetail(request, env, store, decodeURIComponent(issueMatch[1]!));
  }
  if (issueMatch && request.method === "PATCH") {
    return handlePatchIssue(request, env, store, decodeURIComponent(issueMatch[1]!));
  }
  const reportPartMatch = /^\/v1\/reports\/([^/]+)\/([^/]+)$/.exec(url.pathname);
  if (reportPartMatch && request.method === "GET") {
    return handleReportPart(request, env, store, decodeURIComponent(reportPartMatch[1]!), reportPartMatch[2]!);
  }
  const reportMatch = /^\/v1\/reports\/([^/]+)$/.exec(url.pathname);
  if (reportMatch && request.method === "GET") {
    return handleReportDetail(request, env, store, decodeURIComponent(reportMatch[1]!));
  }
  return json({ error: "not found" }, 404, request, env);
}

export async function route(request: Request, env: Env, store: Store): Promise<Response> {
  const url = new URL(request.url);

  if (request.method === "OPTIONS") return handleOptions(request, env);
  if (url.pathname === "/v1/report" && request.method === "POST") return handleReport(request, env, store);
  const deleteInstallMatch = /^\/v1\/installs\/([^/]+)$/.exec(url.pathname);
  if (deleteInstallMatch && request.method === "DELETE") {
    return handleDeleteInstall(request, env, store, decodeURIComponent(deleteInstallMatch[1]!));
  }
  if (url.pathname.startsWith("/v1/")) return routeAccessGated(request, url, env, store);
  return json({ error: "not found" }, 404, request, env);
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    try {
      return await route(request, env, new D1Store(env.DB));
    } catch (err) {
      // Deliberately logs only the error, never the request (which could carry cf-connecting-ip).
      console.error("crash-worker unhandled error", err instanceof Error ? err.message : String(err));
      return json({ error: "internal error" }, 500, request, env);
    }
  },
};
