/** Base URL for the Worker (`ops/crash/worker`); empty means same-origin (Pages + Worker on one
 *  domain, or a dev proxy). Set via `VITE_API_BASE` at build time. */
const API_BASE = (import.meta.env.VITE_API_BASE as string | undefined) ?? "";

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

/** Fetches JSON from the crash Worker with the Cloudflare Access cookie attached
 *  (`credentials: "include"` — plan §4). Throws `ApiError` on a non-2xx response. */
export async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${API_BASE}${path}`, {
    ...init,
    credentials: "include",
    headers: {
      Accept: "application/json",
      ...(init?.body ? { "Content-Type": "application/json" } : {}),
      ...init?.headers,
    },
  });

  if (!response.ok) {
    let message = response.statusText || `HTTP ${response.status}`;
    try {
      const body = (await response.json()) as { error?: string };
      if (body?.error) message = body.error;
    } catch {
      // response wasn't JSON — keep the status text.
    }
    throw new ApiError(response.status, message);
  }

  return (await response.json()) as T;
}

export function buildQuery(params: Record<string, string | number | undefined>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== "") search.set(key, String(value));
  }
  const qs = search.toString();
  return qs ? `?${qs}` : "";
}
