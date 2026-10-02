import { describe, expect, it, afterAll, beforeAll, beforeEach, vi } from "vitest";
import worker from "../src/index.js";
import { D1Store } from "../src/store.js";
import { clearSymmapCacheForTests } from "../src/symbolicate.js";
import { PURGE_BATCH, R2_PART_FILES, RETENTION_DAYS, purgeExpiredReports, retentionCutoffIso } from "../src/retention.js";
import { accessHeader, installAccessFetchStub } from "./access.js";
import { BASE, days, ingest, makeCtx, makeDeps, makeEnv, makeSummary } from "./helpers.js";
import type { Env, Summary } from "../src/types.js";

// The 90-day retention (#165): reports rows + their R2 objects go, issues keep their lifetime stats.

const NOW = Date.parse("2026-10-02T12:00:00.000Z");

/** Ingests `summary` as if it had arrived `ageDays` before NOW, with a dump so all four R2 parts exist. */
async function ingestAged(env: Env, summary: Summary, ageDays: number): Promise<void> {
  const res = await ingest(env, summary, { deps: makeDeps(NOW - days(ageDays)), dump: new Uint8Array([1, 2, 3]) });
  expect(res.status).toBe(201);
}

async function partsPresent(env: Env, s: Summary): Promise<number> {
  let n = 0;
  for (const f of R2_PART_FILES) if (await env.BUCKET.get(`reports/${s.quad}/${s.reportId}/${f}`)) n++;
  return n;
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

describe("purgeExpiredReports", () => {
  it("cuts off at exactly 90 days", () => {
    expect(RETENTION_DAYS).toBe(90);
    expect(PURGE_BATCH * R2_PART_FILES.length).toBeLessThanOrEqual(1000);
    expect(retentionCutoffIso(NOW)).toBe("2026-07-04T12:00:00.000Z");
  });

  it("deletes rows older than 90 days with all their R2 parts, keeps an 89-day-old one, leaves the issue's stats", async () => {
    const env = makeEnv();
    const install = crypto.randomUUID();
    const old = makeSummary({ installId: install, version: "0.2.9" });
    const recent = makeSummary({ installId: install, version: "0.3.0" });
    await ingestAged(env, old, 91);
    await ingestAged(env, recent, 89);

    const store = new D1Store(env.DB);
    const fingerprint = (await store.getReport(old.reportId))!.fingerprint;
    const before = await store.getIssue(fingerprint);
    expect(await partsPresent(env, old)).toBe(4);

    const r = await purgeExpiredReports(store, env.BUCKET, NOW);
    expect(r).toEqual({ deleted: 1, batches: 1, more: false });

    expect(await store.getReport(old.reportId)).toBeNull();
    expect(await partsPresent(env, old)).toBe(0);
    expect(await store.getReport(recent.reportId)).not.toBeNull();
    expect(await partsPresent(env, recent)).toBe(4);

    const after = await store.getIssue(fingerprint);
    expect(after).toEqual(before); // lifetime count/installs/versions/first_seen untouched
    expect(after!.count).toBe(2);
    expect(JSON.parse(after!.versions_json)).toEqual({ "0.2.9": 1, "0.3.0": 1 });
  });

  it("keeps the issue row even when every one of its reports is purged", async () => {
    const env = makeEnv();
    const s = makeSummary();
    await ingestAged(env, s, 120);
    const store = new D1Store(env.DB);
    const fingerprint = (await store.getReport(s.reportId))!.fingerprint;

    await purgeExpiredReports(store, env.BUCKET, NOW);
    expect(await store.getReport(s.reportId)).toBeNull();
    const issue = await store.getIssue(fingerprint);
    expect(issue!.count).toBe(1);
    expect(issue!.last_frames_json).not.toBeNull();
  });

  it("works in batches and reports `more` when it stops at maxBatches", async () => {
    const env = makeEnv();
    const olds = Array.from({ length: 5 }, () => makeSummary());
    for (const [i, s] of olds.entries()) await ingestAged(env, s, 100 + i);
    const store = new D1Store(env.DB);

    expect(await purgeExpiredReports(store, env.BUCKET, NOW, { batch: 2, maxBatches: 2 })).toEqual({
      deleted: 4,
      batches: 2,
      more: true,
    });
    // Oldest first: only the newest of the five (100 days) is left.
    expect(await store.getReport(olds[0]!.reportId)).not.toBeNull();
    for (const s of olds.slice(1)) expect(await store.getReport(s.reportId)).toBeNull();

    expect(await purgeExpiredReports(store, env.BUCKET, NOW, { batch: 2, maxBatches: 2 })).toEqual({
      deleted: 1,
      batches: 1,
      more: false,
    });
    expect(await purgeExpiredReports(store, env.BUCKET, NOW)).toEqual({ deleted: 0, batches: 0, more: false });
  });

  it("an exactly full last batch ends with an empty listing, not `more`", async () => {
    const env = makeEnv();
    for (let i = 0; i < 4; i++) await ingestAged(env, makeSummary(), 95);
    const store = new D1Store(env.DB);
    expect(await purgeExpiredReports(store, env.BUCKET, NOW, { batch: 2, maxBatches: 5 })).toEqual({
      deleted: 4,
      batches: 2,
      more: false,
    });
  });
});

describe("scheduled() — the daily cron", () => {
  it("purges through ctx.waitUntil at the controller's scheduledTime", async () => {
    const env = makeEnv();
    const old = makeSummary();
    const recent = makeSummary();
    await ingestAged(env, old, 91);
    await ingestAged(env, recent, 1);

    const { ctx, waited } = makeCtx();
    worker.scheduled({ scheduledTime: NOW, cron: "17 3 * * *", noRetry() {} }, env, ctx);
    expect(waited).toHaveLength(1);
    expect(await waited[0]).toEqual({ deleted: 1, batches: 1, more: false });

    const store = new D1Store(env.DB);
    expect(await store.getReport(old.reportId)).toBeNull();
    expect(await store.getReport(recent.reportId)).not.toBeNull();
  });
});

describe("POST /v1/retention/run", () => {
  function run(env: Env, headers: Record<string, string>): Promise<Response> {
    return worker.fetch(new Request(`${BASE}/v1/retention/run`, { method: "POST", headers }), env);
  }

  it("401s without a verified Access JWT, and with the ingest key", async () => {
    const env = makeEnv();
    const old = makeSummary();
    await ingest(env, old, { deps: makeDeps(Date.now() - days(100)) });
    expect((await run(env, {})).status).toBe(401);
    expect((await run(env, { "X-Wavee-Ingest": env.INGEST_KEY })).status).toBe(401);
    expect(await new D1Store(env.DB).getReport(old.reportId)).not.toBeNull();
  });

  it("runs the purge now and answers its counts", async () => {
    const env = makeEnv();
    const old = makeSummary();
    await ingest(env, old, { deps: makeDeps(Date.now() - days(100)) });
    await ingest(env, makeSummary());

    const res = await run(env, await accessHeader());
    expect(res.status).toBe(200);
    expect(await res.json()).toEqual({ deleted: 1, batches: 1, more: false });
    expect(await new D1Store(env.DB).getReport(old.reportId)).toBeNull();
  });
});
