import { createRemoteJWKSet, jwtVerify, type JWTVerifyGetKey } from "jose";
import type { Env } from "./types.js";

/** One remote key set per team domain for the isolate's lifetime. jose caches the fetched keys and refetches on an
 *  unknown `kid` (Access rotates its signing keys), with its own cooldown. */
const keySets = new Map<string, JWTVerifyGetKey>();

function keySet(team: string): JWTVerifyGetKey {
  let ks = keySets.get(team);
  if (!ks) {
    ks = createRemoteJWKSet(new URL(`${team}/cdn-cgi/access/certs`));
    keySets.set(team, ks);
  }
  return ks;
}

/** Both Access settings are real values, not wrangler.toml's REPLACE_ placeholders. */
export function accessConfigured(env: Env): boolean {
  return /^https:\/\/[a-z0-9-]+\.cloudflareaccess\.com$/.test(env.ACCESS_TEAM_DOMAIN)
    && env.ACCESS_AUD.length > 0 && !env.ACCESS_AUD.startsWith("REPLACE");
}

/** The Access JWT: the header Access injects on gated paths, else the CF_Authorization cookie — the only carrier on
 *  the Bypass paths, where the dashboard's "Delete this install's data" call lands. */
export function accessToken(request: Request): string | null {
  const header = request.headers.get("Cf-Access-Jwt-Assertion");
  if (header) return header;
  const cookie = request.headers.get("Cookie");
  if (!cookie) return null;
  for (const part of cookie.split(";")) {
    const eq = part.indexOf("=");
    if (eq > 0 && part.slice(0, eq).trim() === "CF_Authorization") return part.slice(eq + 1).trim() || null;
  }
  return null;
}

/** RS256 signature against the team's published keys, `aud` = this application's AUD tag, `iss` = the team domain,
 *  expiry. Fails closed: unconfigured, missing, forged, foreign or expired → false. */
export async function hasValidAccess(request: Request, env: Env): Promise<boolean> {
  if (!accessConfigured(env)) return false;
  const token = accessToken(request);
  if (!token) return false;
  try {
    await jwtVerify(token, keySet(env.ACCESS_TEAM_DOMAIN), {
      issuer: env.ACCESS_TEAM_DOMAIN,
      audience: env.ACCESS_AUD,
      algorithms: ["RS256"],
    });
    return true;
  } catch {
    return false;
  }
}
