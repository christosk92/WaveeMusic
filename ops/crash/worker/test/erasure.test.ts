import { describe, expect, it, afterAll, beforeAll, beforeEach, vi } from "vitest";
import worker from "../src/index.js";
import { D1Store } from "../src/store.js";
import { clearSymmapCacheForTests } from "../src/symbolicate.js";
import { makeFakeD1, makeFakeR2, makeFakeRate } from "./fixtures.js";
import { AUD, TEAM, accessHeader, installAccessFetchStub } from "./access.js";
import type { Env, Summary } from "../src/types.js";

const BASE = "https://crash.cproducts.dev";

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
  form.set("report", "System.NullReferenceException: boom\n");
  form.set("tail", "ts=2026-09-24T14:30:00Z level=Info cat=app event=boot\n");
  const req = new Request(`${BASE}/v1/report`, {
    method: "POST",
    headers: { "X-Wavee-Ingest": env.INGEST_KEY },
    body: form,
  });
  return worker.fetch(req, env);
}

function deleteInstallRequest(installId: string, headers: Record<string, string>): Request {
  return new Request(`${BASE}/v1/installs/${installId}`, { method: "DELETE", headers });
}

beforeAll(() => {
  installAccessFetchStub();
});

afterAll(() => {
  vi.unstubAllGlobals();
});

beforeEach(() => {
  clearSymmapCacheForTests();
});

describe("DELETE /v1/installs/:installId — right to erasure (plan §J)", () => {
  it("401s when neither the ingest key nor an Access header is present", async () => {
    const env = makeEnv();
    const res = await worker.fetch(deleteInstallRequest("whoever", {}), env);
    expect(res.status).toBe(401);
  });

  it("429s when the rate limiter refuses", async () => {
    const env = makeEnv({ RATE: makeFakeRate(false) });
    const res = await worker.fetch(
      deleteInstallRequest("whoever", { "X-Wavee-Ingest": env.INGEST_KEY }),
      env,
    );
    expect(res.status).toBe(429);
  });

  it("404s for an install id with nothing stored", async () => {
    const env = makeEnv();
    const res = await worker.fetch(
      deleteInstallRequest("never-seen", { "X-Wavee-Ingest": env.INGEST_KEY }),
      env,
    );
    expect(res.status).toBe(404);
  });

  it("removes every report row and its R2 objects, and leaves other installs untouched", async () => {
    const env = makeEnv();
    const installA = crypto.randomUUID();
    const installB = crypto.randomUUID();
    const s1 = makeSummary({ installId: installA, quad: "0.3.0.80" });
    const s2 = makeSummary({ installId: installA, quad: "0.3.0.80", exceptionType: "System.IOException" });
    const s3 = makeSummary({ installId: installB, quad: "0.3.0.80" });

    for (const s of [s1, s2, s3]) {
      expect((await ingest(env, s)).status).toBe(201);
    }

    const res = await worker.fetch(
      deleteInstallRequest(installA, { "X-Wavee-Ingest": env.INGEST_KEY }),
      env,
    );
    expect(res.status).toBe(200);
    const body = (await res.json()) as { deleted: number };
    expect(body.deleted).toBe(2);

    const store = new D1Store(env.DB);
    expect(await store.getReport(s1.reportId)).toBeNull();
    expect(await store.getReport(s2.reportId)).toBeNull();
    expect(await store.getReport(s3.reportId)).not.toBeNull(); // install B untouched

    for (const s of [s1, s2]) {
      const prefix = `reports/${s.quad}/${s.reportId}/`;
      for (const name of ["summary.json", "report.txt", "log-tail.txt"]) {
        expect(await env.BUCKET.get(prefix + name), `${name} for ${s.reportId}`).toBeNull();
      }
    }
    // install B's objects are untouched
    const prefixB = `reports/${s3.quad}/${s3.reportId}/`;
    expect(await env.BUCKET.get(prefixB + "summary.json")).not.toBeNull();
  });

  it("recomputes an affected issue's count/installs rather than leaving stale numbers", async () => {
    const env = makeEnv();
    const installA = crypto.randomUUID();
    const installB = crypto.randomUUID();
    // Same exceptionType/kind/no-symmap RVAs ⇒ same fingerprint (fingerprint falls back to RVAs).
    const s1 = makeSummary({ installId: installA, rvas: [0x4242] });
    const s2 = makeSummary({ installId: installB, rvas: [0x4242] });
    await ingest(env, s1);
    await ingest(env, s2);

    const store = new D1Store(env.DB);
    const r1 = await store.getReport(s1.reportId);
    const fingerprint = r1!.fingerprint;
    const before = await store.getIssue(fingerprint);
    expect(before!.count).toBe(2);
    expect(before!.installs).toBe(2);

    const res = await worker.fetch(
      deleteInstallRequest(installA, { "X-Wavee-Ingest": env.INGEST_KEY }),
      env,
    );
    expect(res.status).toBe(200);

    const after = await store.getIssue(fingerprint);
    expect(after).not.toBeNull();
    expect(after!.count).toBe(1);
    expect(after!.installs).toBe(1);
  });

  it("deletes the issue outright once its count reaches 0", async () => {
    const env = makeEnv();
    const installId = crypto.randomUUID();
    const summary = makeSummary({ installId, rvas: [0x9999], exceptionType: "System.UniqueOne" });
    await ingest(env, summary);

    const store = new D1Store(env.DB);
    const report = await store.getReport(summary.reportId);
    const fingerprint = report!.fingerprint;
    expect(await store.getIssue(fingerprint)).not.toBeNull();

    const res = await worker.fetch(
      deleteInstallRequest(installId, { "X-Wavee-Ingest": env.INGEST_KEY }),
      env,
    );
    expect(res.status).toBe(200);
    expect(await store.getIssue(fingerprint)).toBeNull();
  });

  it("accepts a verified Access JWT as an alternative to the ingest key (the dashboard's own delete action)", async () => {
    const env = makeEnv();
    const installId = crypto.randomUUID();
    await ingest(env, makeSummary({ installId }));

    const res = await worker.fetch(deleteInstallRequest(installId, await accessHeader()), env);
    expect(res.status).toBe(200);
  });

  it("tombstones the install id, and POST /v1/report for it afterwards 410s", async () => {
    const env = makeEnv();
    const installId = crypto.randomUUID();
    await ingest(env, makeSummary({ installId }));

    const deleteRes = await worker.fetch(
      deleteInstallRequest(installId, { "X-Wavee-Ingest": env.INGEST_KEY }),
      env,
    );
    expect(deleteRes.status).toBe(200);

    const retryRes = await ingest(env, makeSummary({ installId }));
    expect(retryRes.status).toBe(410);

    // a different, never-deleted install id is unaffected
    const otherRes = await ingest(env, makeSummary({ installId: crypto.randomUUID() }));
    expect(otherRes.status).toBe(201);
  });

  it("a second delete of an already-erased install (nothing left to delete) 404s", async () => {
    const env = makeEnv();
    const installId = crypto.randomUUID();
    await ingest(env, makeSummary({ installId }));
    const first = await worker.fetch(deleteInstallRequest(installId, { "X-Wavee-Ingest": env.INGEST_KEY }), env);
    expect(first.status).toBe(200);

    const second = await worker.fetch(deleteInstallRequest(installId, { "X-Wavee-Ingest": env.INGEST_KEY }), env);
    expect(second.status).toBe(404);
  });
});
