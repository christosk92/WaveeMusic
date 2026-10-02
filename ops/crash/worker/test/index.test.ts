import { describe, expect, it, afterAll, beforeAll, beforeEach, vi } from "vitest";
import worker, { CAP_BYTES } from "../src/index.js";
import { D1Store } from "../src/store.js";
import { clearSymmapCacheForTests } from "../src/symbolicate.js";
import { FINGERPRINT_VERSION } from "../src/grouping.js";
import { makeFakeRate } from "./fixtures.js";
import { accessHeader, installAccessFetchStub } from "./access.js";
import { BASE, days, ingest, makeDeps, makeEnv, makeSummary, putSymmap } from "./helpers.js";
import type { Env } from "../src/types.js";

const INGEST_URL = `${BASE}/v1/report`;
/** A real, verifiable Access JWT header — signed in `beforeAll`. */
let ACCESS: Record<string, string>;

function buildReportRequest(
  env: Env,
  opts: {
    summary?: unknown;
    report?: string;
    tail?: string;
    dump?: Uint8Array;
    ingestKey?: string;
    headers?: Record<string, string>;
    omit?: ("summary" | "report" | "tail")[];
  } = {},
): Request {
  const form = new FormData();
  if (!opts.omit?.includes("summary")) {
    form.set("summary", JSON.stringify(opts.summary ?? makeSummary()));
  }
  if (!opts.omit?.includes("report")) {
    form.set("report", opts.report ?? "System.InvalidOperationException: --crash-probe\n   at Wavee!<BaseAddress>+0x7b1fc6\n");
  }
  if (!opts.omit?.includes("tail")) {
    form.set("tail", opts.tail ?? "ts=2026-09-24T14:30:00Z level=Info cat=app event=boot\n");
  }
  if (opts.dump) {
    form.set("dump", new File([opts.dump], "minidump.dmp", { type: "application/octet-stream" }));
  }
  return new Request(INGEST_URL, {
    method: "POST",
    headers: { "X-Wavee-Ingest": opts.ingestKey ?? env.INGEST_KEY, ...opts.headers },
    body: form,
  });
}

function accessRequest(path: string, init: RequestInit = {}): Request {
  return new Request(`${BASE}${path}`, { ...init, headers: { ...ACCESS, ...(init.headers as Record<string, string>) } });
}

function patchIssue(env: Env, fingerprint: string, body: unknown): Promise<Response> {
  return worker.fetch(
    accessRequest(`/v1/issues/${fingerprint}`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    }),
    env,
  );
}

beforeAll(async () => {
  installAccessFetchStub();
  ACCESS = await accessHeader();
});

afterAll(() => {
  vi.unstubAllGlobals();
});

beforeEach(() => {
  clearSymmapCacheForTests();
});

describe("POST /v1/report — auth and shape", () => {
  it("401s when X-Wavee-Ingest is missing", async () => {
    const env = makeEnv();
    const req = buildReportRequest(env, { headers: {} });
    req.headers.delete("X-Wavee-Ingest");
    const res = await worker.fetch(req, env);
    expect(res.status).toBe(401);
  });

  it("401s when X-Wavee-Ingest does not match INGEST_KEY", async () => {
    const env = makeEnv();
    const res = await worker.fetch(buildReportRequest(env, { ingestKey: "wrong-key" }), env);
    expect(res.status).toBe(401);
  });

  it("429s when the rate limiter refuses", async () => {
    const env = makeEnv({ RATE: makeFakeRate(false) });
    const res = await worker.fetch(buildReportRequest(env), env);
    expect(res.status).toBe(429);
  });

  it("400s when a required part is missing", async () => {
    const env = makeEnv();
    const res = await worker.fetch(buildReportRequest(env, { omit: ["tail"] }), env);
    expect(res.status).toBe(400);
  });

  it("400s when summary.json fails validation (missing debugId)", async () => {
    const env = makeEnv();
    const bad = makeSummary() as unknown as Record<string, unknown>;
    delete bad.debugId;
    const res = await worker.fetch(buildReportRequest(env, { summary: bad }), env);
    expect(res.status).toBe(400);
  });

  it("413s when the bundle exceeds the 20 MB cap", async () => {
    const env = makeEnv();
    const dump = new Uint8Array(CAP_BYTES + 1024);
    const res = await worker.fetch(buildReportRequest(env, { dump }), env);
    expect(res.status).toBe(413);
  });

  it("201s on a valid report and echoes the id", async () => {
    const env = makeEnv();
    const summary = makeSummary();
    const res = await worker.fetch(buildReportRequest(env, { summary }), env);
    expect(res.status).toBe(201);
    const body = await res.json();
    expect(body).toEqual({ id: summary.reportId });
  });

  it("409s on a duplicate reportId", async () => {
    const env = makeEnv();
    const summary = makeSummary();
    const first = await worker.fetch(buildReportRequest(env, { summary }), env);
    expect(first.status).toBe(201);
    const second = await worker.fetch(buildReportRequest(env, { summary }), env);
    expect(second.status).toBe(409);
  });
});

describe("POST /v1/report — R2 objects and symbolication", () => {
  it("writes summary/report/tail (and dump when present) under reports/<quad>/<reportId>/", async () => {
    const env = makeEnv();
    const summary = makeSummary({ quad: "0.3.0.99" });
    const dump = new Uint8Array([1, 2, 3, 4]);
    const res = await worker.fetch(buildReportRequest(env, { summary, dump }), env);
    expect(res.status).toBe(201);

    const prefix = `reports/${summary.quad}/${summary.reportId}/`;
    for (const name of ["summary.json", "report.txt", "log-tail.txt", "minidump.dmp"]) {
      const obj = await env.BUCKET.get(prefix + name);
      expect(obj, `${name} should exist in R2`).not.toBeNull();
    }
  });

  it("resolves RVAs against a fixture .symmap and stores frames_json with names", async () => {
    const env = makeEnv();
    const quad = "0.3.0.50";
    const arch = "arm64";
    await putSymmap(env, quad, arch, [
      { rva: 0x1000, size: 0x40, name: "Wavee_Entities_Detail_UI_Hero__Render" },
      { rva: 0x2000, size: 0x80, name: "Wavee_App_Main" },
    ]);

    const summary = makeSummary({ quad, arch, rvas: [0x1010, 0x2050, 0x50] });
    const res = await worker.fetch(buildReportRequest(env, { summary }), env);
    expect(res.status).toBe(201);

    const store = new D1Store(env.DB);
    const row = await store.getReport(summary.reportId);
    expect(row).not.toBeNull();
    const frames = JSON.parse(row!.frames_json);
    expect(frames).toEqual([
      { rva: 0x1010, offset: 0x10, name: "Wavee_Entities_Detail_UI_Hero__Render" },
      { rva: 0x2050, offset: 0x50, name: "Wavee_App_Main" },
      { rva: 0x50, offset: 0, name: null }, // below the first entry
    ]);
  });

  it("keeps frame names null when no .symmap exists for the quad/arch", async () => {
    const env = makeEnv();
    const summary = makeSummary({ quad: "0.3.0.no-map", arch: "x64", rvas: [0x1234] });
    const res = await worker.fetch(buildReportRequest(env, { summary }), env);
    expect(res.status).toBe(201);

    const store = new D1Store(env.DB);
    const row = await store.getReport(summary.reportId);
    const frames = JSON.parse(row!.frames_json);
    expect(frames).toEqual([{ rva: 0x1234, offset: 0, name: null }]);
  });
});

describe("POST /v1/report — server-derived and native fault fields (#165)", () => {
  it("derives has_dump/dump_bytes from the dump part, never from summary.hasDump", async () => {
    const env = makeEnv();
    const store = new D1Store(env.DB);

    const claimsDump = makeSummary({ hasDump: true, dumpBytes: 999 });
    expect((await ingest(env, claimsDump)).status).toBe(201);
    const noDumpRow = await store.getReport(claimsDump.reportId);
    expect(noDumpRow!.has_dump).toBe(0);
    expect(noDumpRow!.dump_bytes).toBe(0);
    expect(await env.BUCKET.get(`reports/${claimsDump.quad}/${claimsDump.reportId}/minidump.dmp`)).toBeNull();

    const sendsDump = makeSummary({ hasDump: false, dumpBytes: 0 });
    expect((await ingest(env, sendsDump, { dump: new Uint8Array([0x4d, 0x44, 0x4d, 0x50]) })).status).toBe(201);
    const dumpRow = await store.getReport(sendsDump.reportId);
    expect(dumpRow!.has_dump).toBe(1);
    expect(dumpRow!.dump_bytes).toBe(4);
  });

  it("stores exceptionCode/faultModule/faultOffset and the grouping version", async () => {
    const env = makeEnv();
    const summary = makeSummary({
      kind: "Native",
      exceptionType: "",
      exceptionCode: 0xc0000005,
      faultModule: "C:\\Windows\\System32\\NTDLL.DLL",
      faultOffset: 0x1234,
    });
    expect((await ingest(env, summary)).status).toBe(201);

    const row = await new D1Store(env.DB).getReport(summary.reportId);
    expect(row!.exception_code).toBe(3221225477);
    expect(row!.fault_module).toBe("ntdll.dll");
    expect(row!.fault_offset).toBe(0x1234);
    expect(row!.fp_version).toBe(FINGERPRINT_VERSION);

    const issue = await new D1Store(env.DB).getIssue(row!.fingerprint);
    expect(issue!.fp_version).toBe(FINGERPRINT_VERSION);
    expect(issue!.title).toBe("ACCESS_VIOLATION in ntdll.dll+0x1234"); // no symmap → no frame names
  });

  it("sanitizes malformed fault fields and a version unfit for a JSON path to 0/\"\" instead of refusing", async () => {
    const env = makeEnv();
    const summary = {
      ...makeSummary({ kind: "Native" }),
      exceptionCode: -1,
      faultModule: "evil module.dll",
      faultOffset: -5,
      version: '0.3.0"]',
    };
    expect((await ingest(env, summary)).status).toBe(201);

    const row = await new D1Store(env.DB).getReport(summary.reportId);
    expect(row!.exception_code).toBe(0);
    expect(row!.fault_module).toBe("");
    expect(row!.fault_offset).toBe(0);
    expect(row!.semver).toBe("");
  });

  it("caps rvas at 64 frames", async () => {
    const env = makeEnv();
    const summary = makeSummary({ rvas: Array.from({ length: 100 }, (_, i) => 0x1000 + i) });
    expect((await ingest(env, summary)).status).toBe(201);
    const row = await new D1Store(env.DB).getReport(summary.reportId);
    expect(JSON.parse(row!.frames_json)).toHaveLength(64);
  });
});

describe("fingerprint + issue aggregation", () => {
  it("groups two reports with the same kind/exceptionType/top-3 frame names into one issue", async () => {
    const env = makeEnv();
    const quad = "0.3.0.60";
    const arch = "arm64";
    await putSymmap(env, quad, arch, [{ rva: 0x1000, size: 0x40, name: "Wavee_Crash_Site" }]);

    const s1 = makeSummary({ quad, arch, rvas: [0x1010], installId: "install-a" });
    const s2 = makeSummary({ quad, arch, rvas: [0x1020], installId: "install-b" });
    expect((await worker.fetch(buildReportRequest(env, { summary: s1 }), env)).status).toBe(201);
    expect((await worker.fetch(buildReportRequest(env, { summary: s2 }), env)).status).toBe(201);

    const store = new D1Store(env.DB);
    const r1 = await store.getReport(s1.reportId);
    const r2 = await store.getReport(s2.reportId);
    expect(r1!.fingerprint).toBe(r2!.fingerprint);

    const issue = await store.getIssue(r1!.fingerprint);
    expect(issue).not.toBeNull();
    expect(issue!.count).toBe(2);
    expect(issue!.installs).toBe(2);
    expect(issue!.title).toBe("System.InvalidOperationException · Wavee_Crash_Site");
  });

  it("gives different exception types different fingerprints", async () => {
    const env = makeEnv();
    const s1 = makeSummary({ exceptionType: "System.NullReferenceException" });
    const s2 = makeSummary({ exceptionType: "System.InvalidOperationException" });
    await worker.fetch(buildReportRequest(env, { summary: s1 }), env);
    await worker.fetch(buildReportRequest(env, { summary: s2 }), env);

    const store = new D1Store(env.DB);
    const r1 = await store.getReport(s1.reportId);
    const r2 = await store.getReport(s2.reportId);
    expect(r1!.fingerprint).not.toBe(r2!.fingerprint);
  });

  it("keeps lifetime counters incrementally: count, distinct installs, per-version counts, newest frames", async () => {
    const env = makeEnv();
    const a = crypto.randomUUID(), b = crypto.randomUUID();
    await ingest(env, makeSummary({ installId: a, version: "0.3.0" }));
    await ingest(env, makeSummary({ installId: a, version: "0.3.1" }));
    const last = makeSummary({ installId: b, version: "0.3.1" });
    await ingest(env, last);

    const store = new D1Store(env.DB);
    const fp = (await store.getReport(last.reportId))!.fingerprint;
    const issue = await store.getIssue(fp);
    expect(issue!.count).toBe(3);
    expect(issue!.installs).toBe(2);
    expect(JSON.parse(issue!.versions_json)).toEqual({ "0.3.0": 1, "0.3.1": 2 });
    expect(JSON.parse(issue!.last_frames_json!)).toEqual([{ rva: 0x1000, offset: 0, name: null }]);
    expect(issue!.status).toBe("open");
    expect(issue!.regressions).toBe(0);
  });
});

describe("no IP is ever stored", () => {
  it("the stored report row carries no IP field and not the caller's address", async () => {
    const env = makeEnv();
    const distinctiveIp = "203.0.113.42"; // TEST-NET-3, RFC 5737
    const summary = makeSummary();
    const res = await worker.fetch(
      buildReportRequest(env, { summary, headers: { "cf-connecting-ip": distinctiveIp } }),
      env,
    );
    expect(res.status).toBe(201);

    const store = new D1Store(env.DB);
    const row = await store.getReport(summary.reportId);
    expect(row).not.toBeNull();

    const keys = Object.keys(row!);
    expect(keys.some((k) => /ip/i.test(k))).toBe(false);
    expect(JSON.stringify(row)).not.toContain(distinctiveIp);

    const issue = await store.getIssue(row!.fingerprint);
    expect(JSON.stringify(issue)).not.toContain(distinctiveIp);
  });
});

describe("Access-gated GET/PATCH routes", () => {
  async function seedOneReport(env: Env, overrides: Parameters<typeof makeSummary>[0] = {}) {
    const summary = makeSummary(overrides);
    const res = await worker.fetch(buildReportRequest(env, { summary }), env);
    expect(res.status).toBe(201);
    const row = await new D1Store(env.DB).getReport(summary.reportId);
    return { summary, fingerprint: row!.fingerprint };
  }

  it("401s GET /v1/issues without Cf-Access-Jwt-Assertion", async () => {
    const env = makeEnv();
    const res = await worker.fetch(new Request(`${BASE}/v1/issues`), env);
    expect(res.status).toBe(401);
  });

  it("lists issues with a verified Access JWT", async () => {
    const env = makeEnv();
    await seedOneReport(env);
    const res = await worker.fetch(accessRequest("/v1/issues"), env);
    expect(res.status).toBe(200);
    const body = (await res.json()) as { issues: unknown[] };
    expect(body.issues.length).toBe(1);
  });

  it("GET /v1/issues/:fp returns the issue plus its reports", async () => {
    const env = makeEnv();
    const { fingerprint } = await seedOneReport(env);

    const res = await worker.fetch(accessRequest(`/v1/issues/${fingerprint}`), env);
    expect(res.status).toBe(200);
    const body = (await res.json()) as { issue: { fingerprint: string; regressions: number }; reports: unknown[] };
    expect(body.issue.fingerprint).toBe(fingerprint);
    expect(body.issue.regressions).toBe(0);
    expect(body.reports.length).toBe(1);
  });

  it("GET /v1/issues/:fp falls back to last_frames_json once every report is purged", async () => {
    const env = makeEnv();
    const quad = "0.3.0.61";
    await putSymmap(env, quad, "arm64", [{ rva: 0x1000, size: 0x40, name: "Wavee_Purged_Site" }]);
    const old = makeSummary({ quad, rvas: [0x1004] });
    expect((await ingest(env, old, { deps: makeDeps(Date.now() - days(100)) })).status).toBe(201);
    const fingerprint = (await new D1Store(env.DB).getReport(old.reportId))!.fingerprint;

    expect((await worker.fetch(accessRequest("/v1/retention/run", { method: "POST" }), env)).status).toBe(200);

    const res = await worker.fetch(accessRequest(`/v1/issues/${fingerprint}`), env);
    expect(res.status).toBe(200);
    const body = (await res.json()) as {
      issue: { count: number };
      reports: unknown[];
      frames: { name: string | null }[] | null;
    };
    expect(body.reports).toEqual([]);
    expect(body.issue.count).toBe(1);
    expect(body.frames).toEqual([{ rva: 0x1004, offset: 4, name: "Wavee_Purged_Site" }]);
  });

  it("PATCH /v1/issues/:fp updates status and github_issue", async () => {
    const env = makeEnv();
    const { fingerprint } = await seedOneReport(env);

    const res = await patchIssue(env, fingerprint, { status: "resolved", github_issue: 42 });
    expect(res.status).toBe(200);

    const updated = await new D1Store(env.DB).getIssue(fingerprint);
    expect(updated!.status).toBe("resolved");
    expect(updated!.github_issue).toBe(42);
  });

  it("PATCH to resolved records resolved_at and the newest semver seen; reopening by hand keeps them", async () => {
    const env = makeEnv();
    const { fingerprint } = await seedOneReport(env, { version: "0.3.0-beta.2" });
    await seedOneReport(env, { version: "0.3.0" });
    await seedOneReport(env, { version: "0.2.9" });

    expect((await patchIssue(env, fingerprint, { status: "resolved" })).status).toBe(200);
    const store = new D1Store(env.DB);
    const resolved = await store.getIssue(fingerprint);
    expect(resolved!.resolved_version).toBe("0.3.0");
    expect(resolved!.resolved_at).not.toBeNull();

    expect((await patchIssue(env, fingerprint, { status: "open" })).status).toBe(200);
    const reopened = await store.getIssue(fingerprint);
    expect(reopened!.status).toBe("open");
    expect(reopened!.resolved_version).toBe("0.3.0");
    expect(reopened!.resolved_at).toBe(resolved!.resolved_at);
  });

  it("PATCH /v1/issues/:fp 400s a status other than open/resolved/ignored", async () => {
    const env = makeEnv();
    const { fingerprint } = await seedOneReport(env);
    for (const status of ["closed", "", 1, null]) {
      const res = await patchIssue(env, fingerprint, { status });
      expect(res.status, JSON.stringify(status)).toBe(400);
    }
    expect((await new D1Store(env.DB).getIssue(fingerprint))!.status).toBe("open");
  });

  it("PATCH /v1/issues/:fp 404s for an unknown fingerprint", async () => {
    const env = makeEnv();
    const res = await patchIssue(env, "does-not-exist", { status: "resolved" });
    expect(res.status).toBe(404);
  });

  it("GET /v1/reports/:id/summary and /report and /tail return inline text", async () => {
    const env = makeEnv();
    const { summary } = await seedOneReport(env);
    for (const part of ["summary", "report", "tail"]) {
      const res = await worker.fetch(accessRequest(`/v1/reports/${summary.reportId}/${part}`), env);
      expect(res.status, part).toBe(200);
      expect(res.headers.get("Content-Disposition")).toBeNull();
    }
  });

  it("GET /v1/reports/:id/dump returns an attachment download", async () => {
    const env = makeEnv();
    const summary = makeSummary();
    const dump = new Uint8Array([9, 9, 9]);
    await worker.fetch(buildReportRequest(env, { summary, dump }), env);

    const res = await worker.fetch(accessRequest(`/v1/reports/${summary.reportId}/dump`), env);
    expect(res.status).toBe(200);
    expect(res.headers.get("Content-Disposition")).toContain("attachment");
    expect(res.headers.get("Content-Type")).toBe("application/octet-stream");
  });

  it("GET /v1/versions aggregates counts per quad/arch/kind", async () => {
    const env = makeEnv();
    await worker.fetch(buildReportRequest(env, { summary: makeSummary({ quad: "0.3.0.70", arch: "arm64", kind: "Managed" }) }), env);
    await worker.fetch(buildReportRequest(env, { summary: makeSummary({ quad: "0.3.0.70", arch: "arm64", kind: "Managed" }) }), env);
    await worker.fetch(buildReportRequest(env, { summary: makeSummary({ quad: "0.3.0.70", arch: "x64", kind: "Hang" }) }), env);

    const res = await worker.fetch(accessRequest("/v1/versions"), env);
    expect(res.status).toBe(200);
    const body = (await res.json()) as { versions: { semver: string; quad: string; arch: string; kind: string; count: number }[] };
    const managedArm64 = body.versions.find((v) => v.quad === "0.3.0.70" && v.arch === "arm64" && v.kind === "Managed");
    const hangX64 = body.versions.find((v) => v.quad === "0.3.0.70" && v.arch === "x64" && v.kind === "Hang");
    expect(managedArm64?.count).toBe(2);
    expect(hangX64?.count).toBe(1);
  });
});

describe("DELETE /v1/reports/:id (#165)", () => {
  function deleteReport(env: Env, id: string, headers: Record<string, string> = ACCESS): Promise<Response> {
    return worker.fetch(new Request(`${BASE}/v1/reports/${id}`, { method: "DELETE", headers }), env);
  }

  it("401s without a verified Access JWT — the ingest key does not open it", async () => {
    const env = makeEnv();
    const summary = makeSummary();
    await ingest(env, summary);
    expect((await deleteReport(env, summary.reportId, {})).status).toBe(401);
    expect((await deleteReport(env, summary.reportId, { "X-Wavee-Ingest": env.INGEST_KEY })).status).toBe(401);
    expect(await new D1Store(env.DB).getReport(summary.reportId)).not.toBeNull();
  });

  it("404s an unknown report id", async () => {
    const env = makeEnv();
    expect((await deleteReport(env, "never-seen")).status).toBe(404);
  });

  it("deletes the row and its R2 objects and recomputes the issue; the last report takes the issue with it", async () => {
    const env = makeEnv();
    const s1 = makeSummary({ version: "0.3.0" });
    const s2 = makeSummary({ version: "0.3.1" });
    await ingest(env, s1, { dump: new Uint8Array([1, 2, 3]) });
    await ingest(env, s2);
    const store = new D1Store(env.DB);
    const fingerprint = (await store.getReport(s1.reportId))!.fingerprint;
    expect((await store.getIssue(fingerprint))!.count).toBe(2);

    const res = await deleteReport(env, s1.reportId);
    expect(res.status).toBe(200);
    expect(await res.json()).toEqual({ deleted: 1, fingerprint });

    expect(await store.getReport(s1.reportId)).toBeNull();
    for (const name of ["summary.json", "report.txt", "log-tail.txt", "minidump.dmp"]) {
      expect(await env.BUCKET.get(`reports/${s1.quad}/${s1.reportId}/${name}`), name).toBeNull();
    }
    expect(await env.BUCKET.get(`reports/${s2.quad}/${s2.reportId}/summary.json`)).not.toBeNull();

    const after = await store.getIssue(fingerprint);
    expect(after!.count).toBe(1);
    expect(after!.installs).toBe(1);
    expect(JSON.parse(after!.versions_json)).toEqual({ "0.3.1": 1 });

    expect((await deleteReport(env, s2.reportId)).status).toBe(200);
    expect(await store.getIssue(fingerprint)).toBeNull();
    // No tombstone: the install may keep reporting.
    expect((await ingest(env, makeSummary({ installId: s2.installId }))).status).toBe(201);
  });
});
