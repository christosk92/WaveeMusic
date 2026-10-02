import { describe, expect, it, afterEach, beforeEach, vi } from "vitest";
import { buildDiscordPayload, postDiscord, type IssueEvent } from "../src/alerts.js";
import { D1Store } from "../src/store.js";
import { clearSymmapCacheForTests } from "../src/symbolicate.js";
import { BASE, ingest, makeCtx, makeDeps, makeEnv, makeSummary, putSymmap } from "./helpers.js";
import type { Env, Summary } from "../src/types.js";

// Discord alerts on a NEW issue and a REGRESSION (#165). No Access routes here, so no Access fetch stub: the only
// `fetch` any test in this file may reach is the one it stubs itself.

const WEBHOOK = "https://discord.com/api/webhooks/123456789012345678/abc-DEF_ghi";
const ARCH = "arm64";
const QUAD_OLD = "0.2.9.5";
const QUAD = "0.3.0.10";
const QUAD_NEW = "0.3.1.0";
const SITE = [{ rva: 0x1000, size: 0x40, name: "Wavee_Wavee_Player__Play" }];

const event: IssueEvent = {
  transition: "new",
  fingerprint: "abc123def4567890abc123def4567890abc123de",
  title: "System.InvalidOperationException · Wavee_Wavee_Player__Play",
  kind: "Managed",
  semver: "0.3.0",
  quad: QUAD,
  arch: ARCH,
  channel: "stable",
  count: 3,
  installs: 2,
  githubIssue: null,
  dashboardOrigin: BASE,
  at: "2026-10-02T12:00:00.000Z",
};

/** A Worker that holds the symmap for all three quads, so every report below groups onto one issue and may alert. */
async function setup() {
  const env = makeEnv();
  for (const quad of [QUAD_OLD, QUAD, QUAD_NEW]) await putSymmap(env, quad, ARCH, SITE);
  return { env, deps: makeDeps(), store: new D1Store(env.DB) };
}

const report = (version: string, quad: string, overrides: Partial<Summary> = {}): Summary =>
  makeSummary({ version, quad, arch: ARCH, rvas: [0x1010], ...overrides });

async function fingerprintOf(env: Env, s: Summary): Promise<string> {
  return (await new D1Store(env.DB).getReport(s.reportId))!.fingerprint;
}

beforeEach(() => {
  clearSymmapCacheForTests();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("buildDiscordPayload — no PII, no mentions", () => {
  it("carries the title, dashboard link, kind, version, channel and counts", () => {
    const p = buildDiscordPayload({ ...event, githubIssue: 165 });
    const embed = p.embeds[0]!;
    expect(p.username).toBe("Wavee crashes");
    expect(p.allowed_mentions).toEqual({ parse: [] });
    expect(embed.title.startsWith("New crash issue: System.InvalidOperationException · ")).toBe(true);
    expect(embed.url).toBe(`${BASE}/issues/${event.fingerprint}`);
    expect(embed.footer.text).toBe("fingerprint abc123def456");
    expect(embed.timestamp).toBe(event.at);
    expect(Object.fromEntries(embed.fields.map((f) => [f.name, f.value]))).toEqual({
      Kind: "Managed",
      Version: "0.3.0 (0.3.0.10) · arm64",
      Channel: "stable",
      Reports: "3 from 2 installs",
      GitHub: "#165",
    });
  });

  it("omits the GitHub field when no issue is linked, and says 'install' for one", () => {
    const p = buildDiscordPayload({ ...event, installs: 1, count: 1 });
    const names = p.embeds[0]!.fields.map((f) => f.name);
    expect(names).not.toContain("GitHub");
    expect(p.embeds[0]!.fields.find((f) => f.name === "Reports")!.value).toBe("1 from 1 install");
  });

  it("strips mentions, markdown and control characters, and caps the title at 256", () => {
    const p = buildDiscordPayload({
      ...event,
      title: "Boom @everyone `code` **bold** [link](https://x) ~~s~~ ||spoiler||\u0007\n" + "x".repeat(400),
      channel: "@here",
    });
    const embed = p.embeds[0]!;
    expect(embed.title).not.toMatch(/[@`*_~|[\]()\u0000-\u001f]/);
    expect(embed.title.startsWith("New crash issue: Boom everyone code bold linkhttps://x s spoilerxxx")).toBe(true);
    expect(embed.title.length).toBeLessThanOrEqual(256);
    expect(embed.fields.find((f) => f.name === "Channel")!.value).toBe("here");
  });

  it("words and colours a regression differently", () => {
    const fresh = buildDiscordPayload(event).embeds[0]!;
    const back = buildDiscordPayload({ ...event, transition: "regressed" }).embeds[0]!;
    expect(back.title.startsWith("Regression — a resolved issue is back: ")).toBe(true);
    expect(back.color).not.toBe(fresh.color);
  });
});

describe("postDiscord", () => {
  it("skips — without fetching — when the URL is unset or not a Discord webhook", async () => {
    const f = vi.fn();
    for (const url of [
      undefined,
      "",
      "http://discord.com/api/webhooks/1/x",
      "https://discord.com.evil.example/api/webhooks/1/x",
      "https://evil.example/api/webhooks/1/x",
      "https://discord.com/api/webhooks/abc/x",
      "https://discord.com/api/webhooks/1/x?wait=true",
      "https://discord.com/api/webhooks/1/x/slack",
    ]) {
      expect(await postDiscord(url, event, f as unknown as typeof fetch), String(url)).toBe("skipped");
    }
    expect(f).not.toHaveBeenCalled();
  });

  it("posts JSON to discord.com, discordapp.com, ptb. and canary. webhooks", async () => {
    for (const url of [
      WEBHOOK,
      "https://discordapp.com/api/webhooks/1/tok",
      "https://ptb.discord.com/api/webhooks/1/tok",
      "https://canary.discord.com/api/webhooks/1/tok",
    ]) {
      const f = vi.fn(async (_u: unknown, _i?: RequestInit) => new Response(null, { status: 204 }));
      expect(await postDiscord(url, event, f as unknown as typeof fetch), url).toBe("sent");
      expect(f).toHaveBeenCalledTimes(1);
      const [calledUrl, init] = f.mock.calls[0]!;
      expect(calledUrl).toBe(url);
      expect(init!.method).toBe("POST");
      expect(JSON.parse(String(init!.body))).toEqual(JSON.parse(JSON.stringify(buildDiscordPayload(event))));
    }
  });

  it("answers failed on a non-2xx and on a thrown error, logging only the status / error name", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const notOk = vi.fn(async () => new Response("nope", { status: 500 }));
    expect(await postDiscord(WEBHOOK, event, notOk as unknown as typeof fetch)).toBe("failed");
    expect(warn).toHaveBeenLastCalledWith("alerts.discord.failed", 500);

    const throws = vi.fn(async () => {
      throw new TypeError("network down " + WEBHOOK);
    });
    expect(await postDiscord(WEBHOOK, event, throws as unknown as typeof fetch)).toBe("failed");
    expect(warn).toHaveBeenLastCalledWith("alerts.discord.failed", "TypeError");
  });
});

describe("issue transitions → alerts", () => {
  it("a new issue alerts once; further reports of it don't", async () => {
    const { env, deps } = await setup();
    const first = report("0.3.0", QUAD);
    await ingest(env, first, { deps });
    await ingest(env, report("0.3.0", QUAD), { deps });
    await ingest(env, report("0.3.0", QUAD), { deps });

    expect(deps.alerts.events).toHaveLength(1);
    const e = deps.alerts.events[0]!;
    expect(e).toMatchObject({
      transition: "new",
      fingerprint: await fingerprintOf(env, first),
      title: "System.InvalidOperationException · Wavee_Wavee_Player__Play",
      kind: "Managed",
      semver: "0.3.0",
      quad: QUAD,
      arch: ARCH,
      channel: "stable",
      count: 1,
      installs: 1,
      githubIssue: null,
      dashboardOrigin: BASE,
    });
  });

  it("no symmap for the build → the issue is recorded but nothing alerts", async () => {
    const env = makeEnv();
    const deps = makeDeps();
    const s = report("0.3.0", "9.9.9.9");
    expect((await ingest(env, s, { deps })).status).toBe(201);
    expect(deps.alerts.events).toEqual([]);
    expect(await new D1Store(env.DB).getIssue(await fingerprintOf(env, s))).not.toBeNull();
  });

  it("resolved: the same or an older version stays resolved; a newer one reopens it as a regression, once", async () => {
    const { env, deps, store } = await setup();
    const first = report("0.3.0", QUAD);
    await ingest(env, first, { deps });
    const fp = await fingerprintOf(env, first);
    expect(await store.patchIssue(fp, { status: "resolved" }, new Date().toISOString())).toBe(true);
    expect((await store.getIssue(fp))!.resolved_version).toBe("0.3.0");

    await ingest(env, report("0.3.0", QUAD), { deps });
    await ingest(env, report("0.2.9", QUAD_OLD), { deps });
    await ingest(env, report("0.3.1-beta.1", QUAD_NEW), { deps }); // a prerelease of 0.3.1 is newer than 0.3.0
    let issue = await store.getIssue(fp);
    expect(issue!.status).toBe("open");
    expect(deps.alerts.events.map((e) => e.transition)).toEqual(["new", "regressed"]);
    expect(issue!.regressions).toBe(1);
    expect(issue!.regressed_at).not.toBeNull();
    expect(issue!.count).toBe(4);
    expect(deps.alerts.events[1]).toMatchObject({ semver: "0.3.1-beta.1", quad: QUAD_NEW, count: 4 });

    await ingest(env, report("0.3.1", QUAD_NEW), { deps }); // already open → no second alert
    issue = await store.getIssue(fp);
    expect(issue!.regressions).toBe(1);
    expect(deps.alerts.events).toHaveLength(2);
  });

  it("the same version after resolve never reopens and never alerts", async () => {
    const { env, deps, store } = await setup();
    const first = report("0.3.0", QUAD);
    await ingest(env, first, { deps });
    const fp = await fingerprintOf(env, first);
    await store.patchIssue(fp, { status: "resolved" }, new Date().toISOString());

    for (let i = 0; i < 3; i++) await ingest(env, report("0.3.0", QUAD), { deps });
    const issue = await store.getIssue(fp);
    expect(issue!.status).toBe("resolved");
    expect(issue!.regressions).toBe(0);
    expect(issue!.count).toBe(4);
    expect(deps.alerts.events.map((e) => e.transition)).toEqual(["new"]);
  });

  it("ignored never reopens, whatever the version", async () => {
    const { env, deps, store } = await setup();
    const first = report("0.3.0", QUAD);
    await ingest(env, first, { deps });
    const fp = await fingerprintOf(env, first);
    await store.patchIssue(fp, { status: "ignored" }, new Date().toISOString());

    await ingest(env, report("0.3.1", QUAD_NEW), { deps });
    expect((await store.getIssue(fp))!.status).toBe("ignored");
    expect(deps.alerts.events).toHaveLength(1);
  });

  it("an issue resolved with no recorded version (empty baseline) never auto-reopens", async () => {
    const { env, deps, store } = await setup();
    const first = report("", QUAD);
    await ingest(env, first, { deps });
    const fp = await fingerprintOf(env, first);
    await store.patchIssue(fp, { status: "resolved" }, new Date().toISOString());
    expect((await store.getIssue(fp))!.resolved_version).toBe("");

    await ingest(env, report("0.3.1", QUAD_NEW), { deps });
    expect((await store.getIssue(fp))!.status).toBe("resolved");
    expect(deps.alerts.events).toHaveLength(1);
  });
});

describe("discordSink end to end (the default export's fetch with a ctx)", () => {
  it("posts one alert via ctx.waitUntil, carrying no message, ids, route or log text", async () => {
    const env = makeEnv({ DISCORD_WEBHOOK_URL: WEBHOOK });
    await putSymmap(env, QUAD, ARCH, SITE);
    const calls: { url: string; body: string }[] = [];
    vi.stubGlobal("fetch", async (url: unknown, init?: RequestInit) => {
      calls.push({ url: String(url), body: String(init?.body) });
      return new Response(null, { status: 204 });
    });
    const { ctx, waited } = makeCtx();
    const s = report("0.3.0", QUAD, { exceptionMessage: "secret-message-7f3a", lastRoute: "secret-route-91" });

    const res = await ingest(env, s, { ctx, report: "secret-report-text", tail: "secret-tail-text" });
    expect(res.status).toBe(201);
    expect(waited).toHaveLength(1);
    expect(await waited[0]).toBe("sent");

    expect(calls).toHaveLength(1);
    expect(calls[0]!.url).toBe(WEBHOOK);
    const body = calls[0]!.body;
    for (const secret of [
      s.exceptionMessage,
      s.installId,
      s.reportId,
      s.sessionId,
      s.lastRoute,
      "secret-report-text",
      "secret-tail-text",
    ]) {
      expect(body).not.toContain(secret);
    }
    const payload = JSON.parse(body) as { allowed_mentions: unknown; embeds: { url: string }[] };
    expect(payload.allowed_mentions).toEqual({ parse: [] });
    expect(payload.embeds[0]!.url).toBe(`${BASE}/issues/${await fingerprintOf(env, s)}`);
  });

  it("with no webhook configured nothing is fetched", async () => {
    const env = makeEnv();
    await putSymmap(env, QUAD, ARCH, SITE);
    const f = vi.fn();
    vi.stubGlobal("fetch", f);
    const { ctx, waited } = makeCtx();

    expect((await ingest(env, report("0.3.0", QUAD), { ctx })).status).toBe(201);
    expect(await Promise.all(waited)).toEqual(["skipped"]);
    expect(f).not.toHaveBeenCalled();
  });
});
