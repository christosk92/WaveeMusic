import { describe, expect, it, afterAll, beforeAll, beforeEach, vi } from "vitest";
import worker from "../src/index.js";
import { D1Store } from "../src/store.js";
import { clearSymmapCacheForTests } from "../src/symbolicate.js";
import { makeFakeD1, makeFakeR2, makeFakeRate, buildSymmap } from "./fixtures.js";
import { AUD, TEAM, accessHeader, installAccessFetchStub } from "./access.js";
import type { Env, Summary } from "../src/types.js";

const BASE = "https://crash.cproducts.dev";
/** A real, verifiable Access JWT header — signed in `beforeAll`. */
let ACCESS: Record<string, string>;

function makeEnv(overrides: Partial<Env> = {}): Env {
  return {
    DB: makeFakeD1(),
    BUCKET: makeFakeR2(),
    RATE: makeFakeRate(true),
    INGEST_KEY: "test-ingest-key",
    ACCESS_TEAM_DOMAIN: TEAM,
    ACCESS_AUD: AUD,
    ...overrides,
  };
}

function makeSummary(overrides: Partial<Summary> = {}): Summary {
  return {
    reportId: crypto.randomUUID(),
    installId: crypto.randomUUID(),
    kind: "Managed",
    stampUtc: "2026-09-24T14:30:12.118Z",
    version: "0.3.0.41",
    quad: "0.3.0.41",
    commit: "7e209e37",
    channel: "stable",
    arch: "arm64",
    osBuild: "26100",
    gpu: "NVIDIA GeForce RTX 4070",
    gpuTier: "Strong",
    softwareAdapter: false,
    packaged: true,
    locale: "en-US",
    sessionId: crypto.randomUUID(),
    uptimeMs: 12_345,
    beforeFirstFrame: false,
    lastRoute: "artist",
    exceptionType: "System.NullReferenceException",
    exceptionMessage: "boom",
    rvas: [0x1000],
    moduleBase: 0x140000000,
    moduleSize: 0x2800000,
    debugId: "7E2C1234-AB12-CD34-EF56-1234567890AB-1",
    exitCode: 0,
    hasDump: false,
    dumpBytes: 0,
    ...overrides,
  };
}

async function ingest(env: Env, summary: Summary): Promise<Response> {
  const form = new FormData();
  form.set("summary", JSON.stringify(summary));
  form.set("report", `${summary.exceptionType}: ${summary.exceptionMessage}\n`);
  form.set("tail", "ts=2026-09-24T14:30:00Z level=Info cat=app event=boot\n");
  const req = new Request(`${BASE}/v1/report`, {
    method: "POST",
    headers: { "X-Wavee-Ingest": env.INGEST_KEY },
    body: form,
  });
  return worker.fetch(req, env);
}

function get(path: string, env: Env, headers: Record<string, string> = ACCESS): Promise<Response> {
  return worker.fetch(new Request(`${BASE}${path}`, { headers }), env);
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

describe("GET /v1/stats", () => {
  it("401s without an Access header", async () => {
    const env = makeEnv();
    const res = await get("/v1/stats", env, {});
    expect(res.status).toBe(401);
  });

  it("aggregates reports/installs/hangs/native, perDay and perKind within the window", async () => {
    const env = makeEnv();
    await ingest(env, makeSummary({ kind: "Managed" }));
    await ingest(env, makeSummary({ kind: "Managed" }));
    await ingest(env, makeSummary({ kind: "Hang" }));
    await ingest(env, makeSummary({ kind: "Native" }));
    await ingest(env, makeSummary({ kind: "UncleanExit" }));

    const res = await get("/v1/stats?since=30", env);
    expect(res.status).toBe(200);
    const body = (await res.json()) as {
      reports: number;
      installs: number;
      hangs: number;
      native: number;
      perDay: { day: string; crash: number; hang: number; closed: number }[];
      perKind: Record<string, number>;
    };
    expect(body.reports).toBe(5);
    expect(body.installs).toBe(5); // each makeSummary() gets its own random installId
    expect(body.hangs).toBe(1);
    expect(body.native).toBe(1);
    expect(body.perKind.Managed).toBe(2);
    expect(body.perKind.Hang).toBe(1);
    expect(body.perDay.length).toBeGreaterThan(0);
    const today = body.perDay[0]!;
    // 2 Managed + 1 Native + 0 ExitCode = 3 "crash"; 1 hang; 1 closed (UncleanExit)
    expect(today.crash).toBe(3);
    expect(today.hang).toBe(1);
    expect(today.closed).toBe(1);
  });

  it("accepts a bare day count or an ISO timestamp for ?since=", async () => {
    const env = makeEnv();
    await ingest(env, makeSummary());
    const byDays = await get("/v1/stats?since=7", env);
    const byIso = await get(`/v1/stats?since=${new Date(Date.now() - 86_400_000).toISOString()}`, env);
    expect(byDays.status).toBe(200);
    expect(byIso.status).toBe(200);
    expect(((await byDays.json()) as { reports: number }).reports).toBe(1);
    expect(((await byIso.json()) as { reports: number }).reports).toBe(1);
  });
});

describe("GET /v1/reports (paged list)", () => {
  it("401s without an Access header", async () => {
    const env = makeEnv();
    const res = await get("/v1/reports", env, {});
    expect(res.status).toBe(401);
  });

  it("filters by kind and quad", async () => {
    const env = makeEnv();
    await ingest(env, makeSummary({ kind: "Hang", quad: "0.3.0.90" }));
    await ingest(env, makeSummary({ kind: "Managed", quad: "0.3.0.90" }));
    await ingest(env, makeSummary({ kind: "Managed", quad: "0.3.0.91" }));

    const res = await get("/v1/reports?kind=Managed&quad=0.3.0.90", env);
    expect(res.status).toBe(200);
    const body = (await res.json()) as { reports: { kind: string; quad: string }[] };
    expect(body.reports.length).toBe(1);
    expect(body.reports[0]!.kind).toBe("Managed");
    expect(body.reports[0]!.quad).toBe("0.3.0.90");
  });

  it("q matches a report id or install id prefix", async () => {
    const env = makeEnv();
    const s = makeSummary();
    await ingest(env, s);
    await ingest(env, makeSummary());

    const byReportId = await get(`/v1/reports?q=${s.reportId.slice(0, 8)}`, env);
    const byInstallId = await get(`/v1/reports?q=${s.installId.slice(0, 8)}`, env);
    const byReportBody = (await byReportId.json()) as { reports: { id: string }[] };
    const byInstallBody = (await byInstallId.json()) as { reports: { id: string }[] };
    expect(byReportBody.reports.some((r) => r.id === s.reportId)).toBe(true);
    expect(byInstallBody.reports.some((r) => r.id === s.reportId)).toBe(true);
  });

  it("paginates with a cursor, newest first, with no overlap or gaps across pages", async () => {
    const env = makeEnv();
    const ids: string[] = [];
    for (let i = 0; i < 5; i++) {
      const s = makeSummary();
      ids.push(s.reportId);
      await ingest(env, s);
    }

    const page1 = await get("/v1/reports?limit=2", env);
    const body1 = (await page1.json()) as { reports: { id: string }[]; nextCursor: string | null };
    expect(body1.reports.length).toBe(2);
    expect(body1.nextCursor).not.toBeNull();

    const page2 = await get(`/v1/reports?limit=2&cursor=${encodeURIComponent(body1.nextCursor!)}`, env);
    const body2 = (await page2.json()) as { reports: { id: string }[]; nextCursor: string | null };
    expect(body2.reports.length).toBe(2);

    const page3 = await get(`/v1/reports?limit=2&cursor=${encodeURIComponent(body2.nextCursor!)}`, env);
    const body3 = (await page3.json()) as { reports: { id: string }[]; nextCursor: string | null };
    expect(body3.reports.length).toBe(1);
    expect(body3.nextCursor).toBeNull();

    const allIds = [...body1.reports, ...body2.reports, ...body3.reports].map((r) => r.id);
    expect(new Set(allIds).size).toBe(5); // no duplicates across pages
    expect(allIds.sort()).toEqual([...ids].sort());
  });
});

describe("GET /v1/symbols", () => {
  it("401s without an Access header", async () => {
    const env = makeEnv();
    const res = await get("/v1/symbols", env, {});
    expect(res.status).toBe(401);
  });

  it("lists a symbols row once the Worker has resolved a report against it", async () => {
    const env = makeEnv();
    const quad = "0.3.0.95";
    const arch = "arm64";
    await env.BUCKET.put(`symbols/${quad}/win-${arch}.symmap`, buildSymmap([{ rva: 0x1000, size: 0x10, name: "M" }]));
    await ingest(env, makeSummary({ quad, arch, rvas: [0x1000] }));

    const res = await get("/v1/symbols", env);
    expect(res.status).toBe(200);
    const body = (await res.json()) as { symbols: { quad: string; arch: string; entries: number }[] };
    const row = body.symbols.find((s) => s.quad === quad && s.arch === arch);
    expect(row).toBeDefined();
    expect(row!.entries).toBe(1);
  });
});

describe("GET /v1/issues/:fp — extended fields", () => {
  it("includes frames, occurrences, sparkline14d and breakdowns", async () => {
    const env = makeEnv();
    const quad = "0.3.0.96";
    const arch = "arm64";
    await env.BUCKET.put(
      `symbols/${quad}/win-${arch}.symmap`,
      buildSymmap([{ rva: 0x1000, size: 0x10, name: "Wavee_Crash_Site" }]),
    );
    const s1 = makeSummary({ quad, arch, rvas: [0x1000], version: "0.3.0.96" });
    const s2 = makeSummary({ quad, arch, rvas: [0x1000], version: "0.3.0.97", gpuTier: "Baseline" });
    await ingest(env, s1);
    await ingest(env, s2);

    const store = new D1Store(env.DB);
    const r1 = await store.getReport(s1.reportId);
    const fingerprint = r1!.fingerprint;

    const res = await get(`/v1/issues/${fingerprint}`, env);
    expect(res.status).toBe(200);
    const body = (await res.json()) as {
      issue: { fingerprint: string };
      reports: unknown[];
      frames: { name: string | null }[];
      occurrences: { id: string; arch: string; gpu: string; has_dump: number; dump_bytes: number }[];
      sparkline14d: { day: string; count: number }[];
      breakdowns: { version: { label: string; count: number }[]; arch: unknown[]; gpu_tier: unknown[] };
    };
    expect(body.issue.fingerprint).toBe(fingerprint);
    expect(body.frames[0]!.name).toBe("Wavee_Crash_Site");
    expect(body.occurrences.length).toBe(2);
    expect(body.occurrences[0]).toHaveProperty("has_dump");
    expect(body.occurrences[0]).toHaveProperty("dump_bytes");
    expect(body.sparkline14d.length).toBeGreaterThan(0);
    expect(body.sparkline14d.reduce((sum, d) => sum + d.count, 0)).toBe(2);
    const versionLabels = body.breakdowns.version.map((v) => v.label).sort();
    expect(versionLabels).toEqual(["0.3.0.96", "0.3.0.97"]);
  });
});

describe("GET /v1/reports/:id — extended fields", () => {
  it("includes this_install (reports_30d, first_seen_quad) and debug_id", async () => {
    const env = makeEnv();
    const installId = crypto.randomUUID();
    const s1 = makeSummary({ installId, quad: "0.3.0.50" });
    await ingest(env, s1);
    const s2 = makeSummary({ installId, quad: "0.3.0.51" });
    await ingest(env, s2);

    const res = await get(`/v1/reports/${s2.reportId}`, env);
    expect(res.status).toBe(200);
    const body = (await res.json()) as {
      report: { debug_id: string | null };
      this_install: { reports_30d: number; first_seen_quad: string | null };
    };
    expect(body.report.debug_id).toBe(s2.debugId);
    expect(body.this_install.reports_30d).toBe(2);
    expect(body.this_install.first_seen_quad).toBe("0.3.0.50"); // the earlier of the two reports
  });
});
