/**
 * Version ordering for the regression rule (#165, plan §A2): resolving an issue records the newest semver it has
 * seen, and only a report from a NEWER semver reopens it. Pure.
 *
 * Accepts what Wavee's `WaveeVersionInfo.SemVer` produces ("0.3.0", "0.3.0-beta.1", "0.3.0-dev") and any number of
 * dot-separated numeric core parts (a missing part counts as 0, so "0.3" == "0.3.0"). Build metadata (`+…`) is
 * ignored. Prerelease < release; prerelease identifiers compare per SemVer 2.0 §11 (numeric numerically and below
 * alphanumeric, alphanumeric in ASCII order, a shorter equal prefix first). Anything unparsable is the oldest.
 */

const SEMVER_RX = /^(\d+(?:\.\d+)*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$/;

interface Parsed {
  core: string[];
  pre: string[];
}

function parse(v: string): Parsed | null {
  const m = SEMVER_RX.exec(v);
  if (!m) return null;
  return { core: m[1]!.split("."), pre: m[2] ? m[2].split(".") : [] };
}

/** Compares two digit strings numerically without overflowing (leading zeros ignored). */
function compareDigits(a: string, b: string): number {
  const x = a.replace(/^0+(?=\d)/, ""), y = b.replace(/^0+(?=\d)/, "");
  if (x.length !== y.length) return x.length - y.length;
  return x < y ? -1 : x > y ? 1 : 0;
}

function compareIdentifier(a: string, b: string): number {
  const an = /^\d+$/.test(a), bn = /^\d+$/.test(b);
  if (an && bn) return compareDigits(a, b);
  if (an !== bn) return an ? -1 : 1;
  return a < b ? -1 : a > b ? 1 : 0;
}

/** `<0` when `a` is older than `b`, `0` when equal in precedence, `>0` when newer. */
export function compareSemver(a: string, b: string): number {
  const pa = parse(a), pb = parse(b);
  if (!pa || !pb) return pa ? 1 : pb ? -1 : 0;

  for (let i = 0; i < Math.max(pa.core.length, pb.core.length); i++) {
    const c = compareDigits(pa.core[i] ?? "0", pb.core[i] ?? "0");
    if (c !== 0) return c;
  }

  if (pa.pre.length === 0 || pb.pre.length === 0) return pb.pre.length - pa.pre.length; // release > prerelease
  for (let i = 0; i < Math.min(pa.pre.length, pb.pre.length); i++) {
    const c = compareIdentifier(pa.pre[i]!, pb.pre[i]!);
    if (c !== 0) return c;
  }
  return pa.pre.length - pb.pre.length;
}

/** An empty baseline never counts: an issue resolved before any version was recorded never auto-reopens. */
export const isNewerSemver = (candidate: string, baseline: string): boolean =>
  baseline === "" ? false : compareSemver(candidate, baseline) > 0;

/** The newest of `versions` ("" entries skipped); "" for none. */
export function maxSemver(versions: readonly string[]): string {
  let best = "";
  for (const v of versions) {
    if (v === "") continue;
    if (best === "" || compareSemver(v, best) > 0) best = v;
  }
  return best;
}
