import type { Env } from "./types.js";

/** CORS for the dashboard origin only (plan §H deliverable 1). The ingest route (`POST /v1/report`) is
 *  called by the desktop app, never a browser, so it gets no CORS headers. */
export function corsHeaders(request: Request, env: Env): Record<string, string> {
  const origin = request.headers.get("Origin");
  if (!origin || origin !== env.DASHBOARD_ORIGIN) return {};
  return {
    "Access-Control-Allow-Origin": origin,
    "Access-Control-Allow-Methods": "GET, PATCH, DELETE, OPTIONS",
    "Access-Control-Allow-Headers": "Content-Type, Cf-Access-Jwt-Assertion",
    "Access-Control-Max-Age": "86400",
    Vary: "Origin",
  };
}

export function handleOptions(request: Request, env: Env): Response {
  const headers = corsHeaders(request, env);
  if (Object.keys(headers).length === 0) return new Response(null, { status: 403 });
  return new Response(null, { status: 204, headers });
}
