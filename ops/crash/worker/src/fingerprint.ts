import type { Frame, Kind } from "./types.js";

async function sha1Hex(input: string): Promise<string> {
  const bytes = new TextEncoder().encode(input);
  const digest = await crypto.subtle.digest("SHA-1", bytes);
  return Array.from(new Uint8Array(digest))
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}

/**
 * fingerprint = SHA-1 of `kind|exceptionType|name1|name2|name3` — the top 3 resolved frame names —
 * falling back to the RVAs when no symbol map was available (plan §C). "No map" is when every one of
 * the top 3 frames failed to resolve a name; a partially-resolved set still uses names (with an empty
 * slot for any unresolved frame) so two builds with the same map produce the same fingerprint even if
 * one frame is missing.
 */
export async function computeFingerprint(kind: Kind, exceptionType: string, frames: readonly Frame[]): Promise<string> {
  const top3 = frames.slice(0, 3);
  const anyResolved = top3.some((f) => f.name !== null);
  const parts = anyResolved
    ? [0, 1, 2].map((i) => top3[i]?.name ?? "")
    : [0, 1, 2].map((i) => (top3[i] !== undefined ? String(top3[i]!.rva) : ""));
  return sha1Hex(`${kind}|${exceptionType}|${parts.join("|")}`);
}
