import { describe, expect, it, afterAll, beforeAll, beforeEach, vi } from "vitest";
import worker, { CAP_BYTES } from "../src/index.js";
import { D1Store } from "../src/store.js";
import { clearSymmapCacheForTests } from "../src/symbolicate.js";
import { makeFakeD1, makeFakeR2, makeFakeRate, buildSymmap } from "./fixtures.js";
import { AUD, TEAM, accessHeader, installAccessFetchStub } from "./access.js";
import type { Env, Summary } from "../src/types.js";

const INGEST_URL = "https://crash.cproducts.dev/v1/report";
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
    exceptionType: "System.InvalidOperationException",
    exceptionMessage: "--crash-probe",
    rvas: [0x7b1fc6, 0x1b616c],
    moduleBase: 0x140000000,
    moduleSize: 0x2800000,
    debugId: "7E2C1234-AB12-CD34-EF56-1234567890AB-1",
    exitCode: 0,
    hasDump: false,
    dumpBytes: 0,
    ...overrides,
  };
}

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
    const symmap = buildSymmap([
      { rva: 0x1000, size: 0x40, name: "Wavee_Entities_Detail_UI_Hero__Render" },
      { rva: 0x2000, size: 0x80, name: "Wavee_App_Main" },
    ]);
    await env.BUCKET.put(`symbols/${quad}/win-${arch}.symmap`, symmap);

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

describe("fingerprint + issue aggregation", () => {
  it("groups two reports with the same kind/exceptionType/top-3 frame names into one issue", async () => {
    const env = makeEnv();
    const quad = "0.3.0.60";
    const arch = "arm64";
    const symmap = buildSymmap([{ rva: 0x1000, size: 0x40, name: "Wavee_Crash_Site" }]);
    await env.BUCKET.put(`symbols/${quad}/win-${arch}.symmap`, symmap);

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
    expect(issue!.installs).toBe(2); // COUNT DISTINCT install_id
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
  async function seedOneReport(env: Env): Promise<Summary> {
    const summary = makeSummary();
    const res = await worker.fetch(buildReportRequest(env, { summary }), env);
    expect(res.status).toBe(201);
    return summary;
  }

  it("401s GET /v1/issues without Cf-Access-Jwt-Assertion", async () => {
    const env = makeEnv();
    const res = await worker.fetch(new Request("https://crash.cproducts.dev/v1/issues"), env);
    expect(res.status).toBe(401);
  });

  it("lists issues with a verified Access JWT", async () => {
    const env = makeEnv();
    await seedOneReport(env);
    const res = await worker.fetch(
      new Request("https://crash.cproducts.dev/v1/issues", { headers: ACCESS }),
      env,
    );
    expect(res.status).toBe(200);
    const body = (await res.json()) as { issues: unknown[] };
    expect(body.issues.length).toBe(1);
  });

  it("GET /v1/issues/:fp returns the issue plus its reports", async () => {
    const env = makeEnv();
    const summary = await seedOneReport(env);
    const store = new D1Store(env.DB);
    const row = await store.getReport(summary.reportId);

    const res = await worker.fetch(
      new Request(`https://crash.cproducts.dev/v1/issues/${row!.fingerprint}`, {
        headers: ACCESS,
      }),
      env,
    );
    expect(res.status).toBe(200);
    const body = (await res.json()) as { issue: { fingerprint: string }; reports: unknown[] };
    expect(body.issue.fingerprint).toBe(row!.fingerprint);
    expect(body.reports.length).toBe(1);
  });

  it("PATCH /v1/issues/:fp updates status and github_issue", async () => {
    const env = makeEnv();
    const summary = await seedOneReport(env);
    const store = new D1Store(env.DB);
    const row = await store.getReport(summary.reportId);

    const res = await worker.fetch(
      new Request(`https://crash.cproducts.dev/v1/issues/${row!.fingerprint}`, {
        method: "PATCH",
        headers: { ...ACCESS, "Content-Type": "application/json" },
        body: JSON.stringify({ status: "resolved", github_issue: 42 }),
      }),
      env,
    );
    expect(res.status).toBe(200);

    const updated = await store.getIssue(row!.fingerprint);
    expect(updated!.status).toBe("resolved");
    expect(updated!.github_issue).toBe(42);
  });

  it("PATCH /v1/issues/:fp 404s for an unknown fingerprint", async () => {
    const env = makeEnv();
    const res = await worker.fetch(
      new Request("https://crash.cproducts.dev/v1/issues/does-not-exist", {
        method: "PATCH",
        headers: { ...ACCESS, "Content-Type": "application/json" },
        body: JSON.stringify({ status: "resolved" }),
      }),
      env,
    );
    expect(res.status).toBe(404);
  });

  it("GET /v1/reports/:id/summary and /report and /tail return inline text", async () => {
    const env = makeEnv();
    const summary = await seedOneReport(env);
    for (const part of ["summary", "report", "tail"]) {
      const res = await worker.fetch(
        new Request(`https://crash.cproducts.dev/v1/reports/${summary.reportId}/${part}`, {
          headers: ACCESS,
        }),
        env,
      );
      expect(res.status, part).toBe(200);
      expect(res.headers.get("Content-Disposition")).toBeNull();
    }
  });

  it("GET /v1/reports/:id/dump returns an attachment download", async () => {
    const env = makeEnv();
    const summary = makeSummary();
    const dump = new Uint8Array([9, 9, 9]);
    await worker.fetch(buildReportRequest(env, { summary, dump }), env);

    const res = await worker.fetch(
      new Request(`https://crash.cproducts.dev/v1/reports/${summary.reportId}/dump`, {
        headers: ACCESS,
      }),
      env,
    );
    expect(res.status).toBe(200);
    expect(res.headers.get("Content-Disposition")).toContain("attachment");
    expect(res.headers.get("Content-Type")).toBe("application/octet-stream");
  });

  it("GET /v1/versions aggregates counts per quad/arch/kind", async () => {
    const env = makeEnv();
    await worker.fetch(buildReportRequest(env, { summary: makeSummary({ quad: "0.3.0.70", arch: "arm64", kind: "Managed" }) }), env);
    await worker.fetch(buildReportRequest(env, { summary: makeSummary({ quad: "0.3.0.70", arch: "arm64", kind: "Managed" }) }), env);
    await worker.fetch(buildReportRequest(env, { summary: makeSummary({ quad: "0.3.0.70", arch: "x64", kind: "Hang" }) }), env);

    const res = await worker.fetch(
      new Request("https://crash.cproducts.dev/v1/versions", { headers: ACCESS }),
      env,
    );
    expect(res.status).toBe(200);
    const body = (await res.json()) as { versions: { semver: string; quad: string; arch: string; kind: string; count: number }[] };
    const managedArm64 = body.versions.find((v) => v.quad === "0.3.0.70" && v.arch === "arm64" && v.kind === "Managed");
    const hangX64 = body.versions.find((v) => v.quad === "0.3.0.70" && v.arch === "x64" && v.kind === "Hang");
    expect(managedArm64?.count).toBe(2);
    expect(hangX64?.count).toBe(1);
  });
});
