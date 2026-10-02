import worker, { route, type Deps } from "../src/index.js";
import { D1Store } from "../src/store.js";
import type { AlertSink, IssueEvent } from "../src/alerts.js";
import type { Env, Summary } from "../src/types.js";
import { buildSymmap, makeFakeD1, makeFakeR2, makeFakeRate, type SymmapEntryFixture } from "./fixtures.js";
import { AUD, TEAM } from "./access.js";

// Shared by every Worker test file (#165). Access JWTs stay in ./access.ts: each test file signs its own.

export const BASE = "https://crash.cproducts.dev";

/** A fresh in-memory D1 + R2 per call. `DISCORD_WEBHOOK_URL` stays unset, so no test alert ever reaches `fetch`
 *  unless the test sets it and stubs `fetch` itself. */
export function makeEnv(overrides: Partial<Env> = {}): Env {
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

/** A valid Managed summary with a fresh report/install/session id. */
export function makeSummary(overrides: Partial<Summary> = {}): Summary {
  return {
    reportId: crypto.randomUUID(),
    installId: crypto.randomUUID(),
    kind: "Managed",
    stampUtc: "2026-09-24T14:30:12.118Z",
    version: "0.3.0",
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
    rvas: [0x1000],
    moduleBase: 0x140000000,
    moduleSize: 0x2800000,
    debugId: "7E2C1234-AB12-CD34-EF56-1234567890AB-1",
    exitCode: 0,
    hasDump: false,
    dumpBytes: 0,
    exceptionCode: 0,
    faultModule: "",
    faultOffset: 0,
    ...overrides,
  };
}

/** An `AlertSink` that keeps every event it is handed. */
export interface RecordingSink extends AlertSink {
  readonly events: IssueEvent[];
}

export function recordingSink(): RecordingSink {
  const events: IssueEvent[] = [];
  return { events, issueEvent: (e) => void events.push(e) };
}

/** Deps with a recording sink and a fixed (or real) clock. */
export function makeDeps(now: number | (() => number) = () => Date.now()): Deps & { alerts: RecordingSink } {
  return { alerts: recordingSink(), now: typeof now === "number" ? () => now : now };
}

export interface IngestOptions {
  /** Route through `route()` with these deps (recorded alerts, fixed clock); omitted → the default export's
   *  `fetch(request, env, ctx)`, i.e. production deps (`discordSink`, `Date.now`). */
  deps?: Deps;
  /** Only without `deps`: the ExecutionContext handed to the default export's `fetch`. */
  ctx?: ExecutionContext;
  report?: string;
  tail?: string;
  dump?: Uint8Array;
}

/** `POST /v1/report` with a well-formed multipart body for `summary`. */
export function ingest(env: Env, summary: unknown, opts: IngestOptions = {}): Promise<Response> {
  const form = new FormData();
  form.set("summary", JSON.stringify(summary));
  form.set("report", opts.report ?? "System.InvalidOperationException: --crash-probe\n");
  form.set("tail", opts.tail ?? "ts=2026-09-24T14:30:00Z level=Info cat=app event=boot\n");
  if (opts.dump) form.set("dump", new File([opts.dump], "minidump.dmp", { type: "application/octet-stream" }));
  const req = new Request(`${BASE}/v1/report`, {
    method: "POST",
    headers: { "X-Wavee-Ingest": env.INGEST_KEY },
    body: form,
  });
  return opts.deps ? route(req, env, new D1Store(env.DB), opts.deps) : worker.fetch(req, env, opts.ctx);
}

/** Uploads a `.symmap` for quad/arch, as the release script does — symbolication resolves against it and alerts
 *  require it. */
export async function putSymmap(env: Env, quad: string, arch: string, entries: SymmapEntryFixture[]): Promise<void> {
  await env.BUCKET.put(`symbols/${quad}/win-${arch}.symmap`, buildSymmap(entries));
}

/** An `ExecutionContext` double whose `waitUntil` promises the test can await. */
export function makeCtx(): { ctx: ExecutionContext; waited: Promise<unknown>[] } {
  const waited: Promise<unknown>[] = [];
  const ctx = {
    waitUntil(p: Promise<unknown>) {
      waited.push(p);
    },
    passThroughOnException() {},
  } as unknown as ExecutionContext;
  return { ctx, waited };
}

export const days = (n: number): number => n * 86_400_000;
