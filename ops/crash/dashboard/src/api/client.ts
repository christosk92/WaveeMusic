// The Worker (`ops/crash/worker`) serves this dashboard as its static assets on the same hostname
// (`crash.cproducts.dev`, docs/plans/wavee/crash-hosting-implementation.md), so every request is a relative,
// same-origin `/v1/...` path — no base URL, no CORS.

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

/** Fetches JSON from the crash Worker. Cloudflare Access sits in front of the hostname; a same-origin
 *  `fetch` sends its `CF_Authorization` cookie by default, and the Worker verifies that JWT itself.
 *  Throws `ApiError` on a non-2xx response. */
export async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
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
