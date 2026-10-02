import type { Frame } from "./types.js";

/**
 * `.symmap` binary format — plan §I "Symbol map (.symmap) binary format". Written by WP-G
 * (Wavee.ReleaseTool `symbol-map`), read here. Little-endian throughout.
 *
 *   offset  size  field
 *   0       4     magic "WSYM"
 *   4       4     u32 version (=1)
 *   8       4     u32 count
 *   12      4     u32 stringTableBytes
 *   16      16    PDB GUID (raw bytes, .NET Guid field layout)
 *   32      4     u32 age
 *   36      8     u64 imageSize
 *   44      count*12   entries, sorted by rva ascending: { u32 rva, u32 size, u32 nameOffset }
 *   44+count*12  stringTableBytes   UTF-8 string table, each name NUL-terminated
 *
 * Header is exactly 44 bytes (verified below), which is 4-byte aligned, so the entry table can be
 * viewed as a single `Uint32Array` with zero copying: element `i*3` = rva, `i*3+1` = size,
 * `i*3+2` = nameOffset. No JSON parsing, no per-entry allocation — this has to be cheap enough for a
 * Workers free-plan request (10 ms CPU budget), per plan §H.3.
 */
const HEADER_BYTES = 44;
const MAGIC = "WSYM";

export interface ParsedSymmap {
  /** Stride-3 view: rva, size, nameOffset per entry, sorted by rva ascending. */
  entries: Uint32Array;
  count: number;
  stringTable: Uint8Array;
  /** Upper-case GUID (with dashes, no braces) + "-" + age, e.g. "7E2C1234-AB12-CD34-EF56-1234567890AB-1". */
  debugId: string;
  imageSize: bigint;
}

export function parseSymmap(buffer: ArrayBuffer): ParsedSymmap {
  if (buffer.byteLength < HEADER_BYTES) {
    throw new Error(`symmap too small: ${buffer.byteLength} bytes`);
  }
  const dv = new DataView(buffer);
  const magic = String.fromCharCode(dv.getUint8(0), dv.getUint8(1), dv.getUint8(2), dv.getUint8(3));
  if (magic !== MAGIC) throw new Error(`bad symmap magic: ${JSON.stringify(magic)}`);

  const version = dv.getUint32(4, true);
  if (version !== 1) throw new Error(`unsupported symmap version: ${version}`);

  const count = dv.getUint32(8, true);
  const stringTableBytes = dv.getUint32(12, true);
  const guidBytes = new Uint8Array(buffer, 16, 16);
  const age = dv.getUint32(32, true);
  const imageSize = dv.getBigUint64(36, true);

  const entriesBytes = count * 12;
  const expected = HEADER_BYTES + entriesBytes + stringTableBytes;
  if (buffer.byteLength < expected) {
    throw new Error(`symmap truncated: have ${buffer.byteLength}, need ${expected}`);
  }

  // HEADER_BYTES (44) is a multiple of 4, so this view needs no copy or re-alignment.
  const entries = new Uint32Array(buffer, HEADER_BYTES, count * 3);
  const stringTable = new Uint8Array(buffer, HEADER_BYTES + entriesBytes, stringTableBytes);

  return { entries, count, stringTable, debugId: formatDebugId(guidBytes, age), imageSize };
}

function hex(n: number, width: number): string {
  return n.toString(16).toUpperCase().padStart(width, "0");
}

/** .NET `Guid` field layout: Data1 (u32 LE), Data2 (u16 LE), Data3 (u16 LE), Data4 (8 raw bytes). */
function formatDebugId(guid: Uint8Array, age: number): string {
  const d1 = ((guid[3]! << 24) | (guid[2]! << 16) | (guid[1]! << 8) | guid[0]!) >>> 0;
  const d2 = ((guid[5]! << 8) | guid[4]!) & 0xffff;
  const d3 = ((guid[7]! << 8) | guid[6]!) & 0xffff;
  const d4a = ((guid[8]! << 8) | guid[9]!) & 0xffff;
  let d4b = "";
  for (let i = 10; i < 16; i++) d4b += hex(guid[i]!, 2);
  return `${hex(d1, 8)}-${hex(d2, 4)}-${hex(d3, 4)}-${hex(d4a, 4)}-${d4b}-${age}`;
}

function readCString(table: Uint8Array, offset: number): string {
  let end = offset;
  while (end < table.length && table[end] !== 0) end++;
  return new TextDecoder().decode(table.subarray(offset, end));
}

/** Binary search for the nearest entry at or below `rva` (same rule cdb uses — "the nearest symbol at
 *  or below the offset", docs/guide/releasing-wavee.md §5b). Null name when nothing qualifies. */
export function resolveFrame(map: ParsedSymmap, rva: number): Frame {
  const { entries, count, stringTable } = map;
  if (count === 0 || rva < entries[0]!) return { rva, offset: 0, name: null };

  let lo = 0;
  let hi = count - 1;
  let ans = -1;
  while (lo <= hi) {
    const mid = (lo + hi) >>> 1;
    const midRva = entries[mid * 3]!;
    if (midRva <= rva) {
      ans = mid;
      lo = mid + 1;
    } else {
      hi = mid - 1;
    }
  }
  if (ans === -1) return { rva, offset: 0, name: null };

  const entryRva = entries[ans * 3]!;
  const nameOffset = entries[ans * 3 + 2]!;
  return { rva, offset: rva - entryRva, name: readCString(stringTable, nameOffset) };
}

export function symmapR2Key(quad: string, arch: string): string {
  return `symbols/${quad}/win-${arch}.symmap`;
}

/** Per-isolate cache of parsed maps, keyed by `<quad>/win-<arch>`. A map, once found, is kept for the isolate's
 *  lifetime (a quad's map never changes). */
const maps = new Map<string, ParsedSymmap>();

/** How long a confirmed-absent map is remembered (#165): a report that arrives before its build's map is uploaded
 *  must not leave that quad unresolved for the isolate's whole lifetime — only for at most 5 minutes. */
export const NEGATIVE_TTL_MS = 300_000;

/** `<quad>/win-<arch>` → epoch ms until which the map is known to be absent (no repeat R2 GETs inside the window). */
const missing = new Map<string, number>();

export function clearSymmapCacheForTests(): void {
  maps.clear();
  missing.clear();
}

export interface SymbolicateResult {
  frames: Frame[];
  /** The resolved map's debug id, when one was found and parsed — for the `symbols` table upsert. */
  debugId: string | null;
  entryCount: number | null;
}

export async function symbolicate(
  bucket: R2Bucket,
  quad: string,
  arch: string,
  rvas: readonly number[],
  nowMs: number,
): Promise<SymbolicateResult> {
  const cacheKey = `${quad}/win-${arch}`;
  let map = maps.get(cacheKey) ?? null;
  const missingUntil = missing.get(cacheKey);
  if (map === null && (missingUntil === undefined || nowMs >= missingUntil)) {
    const obj = await bucket.get(symmapR2Key(quad, arch));
    if (obj) {
      map = parseSymmap(await obj.arrayBuffer());
      maps.set(cacheKey, map);
      missing.delete(cacheKey);
    } else {
      missing.set(cacheKey, nowMs + NEGATIVE_TTL_MS);
    }
  }

  if (!map) {
    return { frames: rvas.map((rva) => ({ rva, offset: 0, name: null })), debugId: null, entryCount: null };
  }
  const resolved = map;
  return {
    frames: rvas.map((rva) => resolveFrame(resolved, rva)),
    debugId: resolved.debugId,
    entryCount: resolved.count,
  };
}
