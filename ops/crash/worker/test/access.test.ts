import { describe, expect, it, afterAll, afterEach, beforeAll, vi } from "vitest";
import { generateKeyPair } from "jose";
import worker from "../src/index.js";
import { accessConfigured, accessToken } from "../src/access.js";
import { makeFakeD1, makeFakeR2, makeFakeRate } from "./fixtures.js";
import { AUD, TEAM, accessCookie, accessHeader, installAccessFetchStub, strayFetches } from "./access.js";
import type { Env } from "../src/types.js";

// crash-hosting-implementation.md: the Worker verifies the Access JWT itself (RS256 against the team's certs,
// `aud`, `iss`, expiry) — on one hostname the Bypass paths let anyone forge the header, so presence means nothing.

const BASE = "https://crash.cproducts.dev";
const PLACEHOLDER_TEAM = "https://REPLACE_WITH_TEAM.cloudflareaccess.com";
const PLACEHOLDER_AUD = "REPLACE_WITH_APPLICATION_AUD_TAG";

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

function listIssues(env: Env, headers: Record<string, string>): Promise<Response> {
  return worker.fetch(new Request(`${BASE}/v1/issues`, { headers }), env);
}

function deleteInstall(env: Env, installId: string, headers: Record<string, string>): Promise<Response> {
  return worker.fetch(new Request(`${BASE}/v1/installs/${installId}`, { method: "DELETE", headers }), env);
}

function epochSeconds(): number {
  return Math.floor(Date.now() / 1000);
}

beforeAll(() => {
  installAccessFetchStub();
});

afterAll(() => {
  vi.unstubAllGlobals();
});

afterEach(() => {
  // Only the configured team's certs URL may ever be fetched (a placeholder config must not fetch at all).
  expect(strayFetches.splice(0)).toEqual([]);
});

describe("Access-gated routes verify the JWT (GET /v1/issues)", () => {
  it("200s with a valid Cf-Access-Jwt-Assertion header", async () => {
    const res = await listIssues(makeEnv(), await accessHeader());
    expect(res.status).toBe(200);
  });

  it("200s with only the CF_Authorization cookie", async () => {
    const res = await listIssues(makeEnv(), await accessCookie());
    expect(res.status).toBe(200);
  });

  it("401s an opaque, forged header (the old presence check admitted it)", async () => {
    const res = await listIssues(makeEnv(), { "Cf-Access-Jwt-Assertion": "opaque-jwt" });
    expect(res.status).toBe(401);
  });

  it("401s a token minted for another Access application (wrong aud)", async () => {
    const res = await listIssues(makeEnv(), await accessHeader({ aud: "another-application-aud" }));
    expect(res.status).toBe(401);
  });

  it("401s a token from another team (wrong iss)", async () => {
    const res = await listIssues(makeEnv(), await accessHeader({ iss: "https://another-team.cloudflareaccess.com" }));
    expect(res.status).toBe(401);
  });

  it("401s an expired token", async () => {
    const now = epochSeconds();
    const res = await listIssues(makeEnv(), await accessHeader({ iat: now - 7200, exp: now - 3600 }));
    expect(res.status).toBe(401);
  });

  it("401s a token signed by a key the team does not publish", async () => {
    const other = await generateKeyPair("RS256");
    const res = await listIssues(makeEnv(), await accessHeader({ key: other.privateKey }));
    expect(res.status).toBe(401);
  });

  it("401s while wrangler.toml still holds the placeholders, even for a token valid against them", async () => {
    const env = makeEnv({ ACCESS_TEAM_DOMAIN: PLACEHOLDER_TEAM, ACCESS_AUD: PLACEHOLDER_AUD });
    const res = await listIssues(env, await accessHeader({ iss: PLACEHOLDER_TEAM, aud: PLACEHOLDER_AUD }));
    expect(res.status).toBe(401);
  });

  it("401s the ingest key alone — it opens only the app's own routes", async () => {
    const env = makeEnv();
    const res = await listIssues(env, { "X-Wavee-Ingest": env.INGEST_KEY });
    expect(res.status).toBe(401);
  });
});

describe("DELETE /v1/installs/:id — ingest key OR verified Access", () => {
  it("401s a forged Cf-Access-Jwt-Assertion (the path sits under an Access Bypass)", async () => {
    const res = await deleteInstall(makeEnv(), "whoever", { "Cf-Access-Jwt-Assertion": "opaque-jwt" });
    expect(res.status).toBe(401);
  });

  it("admits the dashboard's call carrying only the CF_Authorization cookie", async () => {
    const res = await deleteInstall(makeEnv(), "never-seen", await accessCookie());
    expect(res.status).not.toBe(401);
    expect(res.status).toBe(404); // authorised; nothing stored under this id
  });
});

describe("accessToken", () => {
  function req(headers: Record<string, string>): Request {
    return new Request(`${BASE}/v1/issues`, { headers });
  }

  it("prefers the Cf-Access-Jwt-Assertion header over the cookie", () => {
    expect(accessToken(req({ "Cf-Access-Jwt-Assertion": "from-header", Cookie: "CF_Authorization=from-cookie" })))
      .toBe("from-header");
  });

  it("finds CF_Authorization among other cookies", () => {
    expect(accessToken(req({ Cookie: "a=1; CF_Authorization=tok.en.sig; b=2" }))).toBe("tok.en.sig");
  });

  it("is null without the header or the cookie", () => {
    expect(accessToken(req({}))).toBeNull();
    expect(accessToken(req({ Cookie: "a=1; b=2" }))).toBeNull();
  });

  it("is null for an empty CF_Authorization value", () => {
    expect(accessToken(req({ Cookie: "a=1; CF_Authorization=; b=2" }))).toBeNull();
  });

  it("matches the cookie name exactly", () => {
    expect(accessToken(req({ Cookie: "XCF_Authorization=nope; CF_Authorization_x=nope" }))).toBeNull();
  });
});

describe("accessConfigured", () => {
  it("is true for a real team domain and AUD tag", () => {
    expect(accessConfigured(makeEnv())).toBe(true);
  });

  it("is false for wrangler.toml's placeholders, either one", () => {
    expect(accessConfigured(makeEnv({ ACCESS_TEAM_DOMAIN: PLACEHOLDER_TEAM }))).toBe(false);
    expect(accessConfigured(makeEnv({ ACCESS_AUD: PLACEHOLDER_AUD }))).toBe(false);
  });

  it("is false for an empty AUD tag", () => {
    expect(accessConfigured(makeEnv({ ACCESS_AUD: "" }))).toBe(false);
  });

  it("is false for a team domain that is not exactly https://<team>.cloudflareaccess.com", () => {
    for (const team of [
      "https://wavee-test.cloudflareaccess.com/",
      "http://wavee-test.cloudflareaccess.com",
      "https://wavee-test.cloudflareaccess.com.evil.example",
      "https://evil.example",
    ]) {
      expect(accessConfigured(makeEnv({ ACCESS_TEAM_DOMAIN: team })), team).toBe(false);
    }
  });
});
