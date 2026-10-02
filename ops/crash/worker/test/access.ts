import { vi } from "vitest";
import { exportJWK, generateKeyPair, SignJWT, type KeyInput } from "jose";

/** The Access settings every test `Env` carries. TEAM has the real `https://<team>.cloudflareaccess.com` shape, so
 *  `accessConfigured` accepts it. */
export const TEAM = "https://wavee-test.cloudflareaccess.com";
export const AUD = "test-aud";

const CERTS_URL = `${TEAM}/cdn-cgi/access/certs`;

// ONE key pair per test file (module instance): src/access.ts keeps one remote key set per team domain in a
// module-level Map and jose caches the fetched keys inside it, so a key swapped in later would never be seen.
const { publicKey, privateKey } = await generateKeyPair("RS256", { extractable: true });
const kid = crypto.randomUUID();
const jwk = { ...(await exportJWK(publicKey)), kid, alg: "RS256" };

/** Every URL the stub refused. jose's fetch error is swallowed by `hasValidAccess` (→ 401), so tests assert this
 *  stays empty to make a stray fetch loud. */
export const strayFetches: string[] = [];

/** Stubs the global `fetch` jose's `createRemoteJWKSet` resolves at call time (`fetchJwks(…, fetchImpl = fetch)`):
 *  the team's certs URL answers with the one public key; anything else is recorded and throws. Pair with
 *  `vi.unstubAllGlobals()`. */
export function installAccessFetchStub(): void {
  vi.stubGlobal("fetch", async (input: unknown): Promise<Response> => {
    const url = typeof input === "string" ? input : input instanceof URL ? input.href : (input as Request).url;
    if (url === CERTS_URL) {
      return new Response(JSON.stringify({ keys: [jwk] }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      });
    }
    strayFetches.push(url);
    throw new Error(`access test fetch stub: unexpected request to ${url} (only ${CERTS_URL} is served)`);
  });
}

export interface AccessOverrides {
  iss?: string;
  aud?: string;
  /** Seconds since the epoch. Defaults to now. */
  iat?: number;
  /** Seconds since the epoch, or a span from now as jose takes it ("1h"). Defaults to "1h". */
  exp?: number | string;
  /** Sign with this key instead of the served one — under the served `kid`, i.e. a forgery. */
  key?: KeyInput;
}

/** An Access-shaped RS256 JWT for TEAM/AUD, signed by the served key unless overridden. */
export function signAccess(overrides: AccessOverrides = {}): Promise<string> {
  return new SignJWT({ email: "owner@example.com" })
    .setProtectedHeader({ alg: "RS256", kid })
    .setIssuer(overrides.iss ?? TEAM)
    .setAudience(overrides.aud ?? AUD)
    .setIssuedAt(overrides.iat)
    .setExpirationTime(overrides.exp ?? "1h")
    .sign(overrides.key ?? privateKey);
}

/** The header Access injects on the paths it gates. */
export async function accessHeader(overrides?: AccessOverrides): Promise<Record<string, string>> {
  return { "Cf-Access-Jwt-Assertion": await signAccess(overrides) };
}

/** The browser's cookie — the only carrier on Access Bypass paths (the dashboard's erasure call). */
export async function accessCookie(overrides?: AccessOverrides): Promise<Record<string, string>> {
  return { Cookie: `foo=bar; CF_Authorization=${await signAccess(overrides)}` };
}
