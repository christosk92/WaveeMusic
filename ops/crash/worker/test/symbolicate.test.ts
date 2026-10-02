import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  NEGATIVE_TTL_MS,
  clearSymmapCacheForTests,
  parseSymmap,
  resolveFrame,
  symbolicate,
  symmapR2Key,
} from "../src/symbolicate.js";
import { buildSymmap, makeFakeR2 } from "./fixtures.js";

describe(".symmap parsing (plan §I binary format)", () => {
  it("round-trips header fields: version, count, debug id, image size", () => {
    const guid = new Uint8Array([
      0x78, 0x56, 0x34, 0x12, 0xbc, 0x9a, 0x12, 0x34, 0xde, 0xf0, 0x01, 0x23, 0x45, 0x67, 0x89, 0xab,
    ]);
    const buf = buildSymmap(
      [
        { rva: 0x100, size: 0x10, name: "A" },
        { rva: 0x200, size: 0x10, name: "B" },
      ],
      guid,
      7,
      BigInt(0xabcdef),
    );
    const map = parseSymmap(buf);
    expect(map.count).toBe(2);
    // Data1=0x12345678, Data2=0x9abc, Data3=0x3412, Data4 = de f0 01 23 45 67 89 ab
    expect(map.debugId).toBe("12345678-9ABC-3412-DEF0-0123456789AB-7");
    expect(map.imageSize).toBe(BigInt(0xabcdef));
  });

  it("rejects a bad magic", () => {
    const buf = buildSymmap([{ rva: 0, size: 0, name: "x" }]);
    new Uint8Array(buf, 0, 4).set([0, 0, 0, 0]);
    expect(() => parseSymmap(buf)).toThrow(/magic/);
  });

  it("rejects a truncated buffer", () => {
    const buf = buildSymmap([{ rva: 0, size: 0, name: "somewhat-longer-name" }]);
    expect(() => parseSymmap(buf.slice(0, buf.byteLength - 5))).toThrow(/truncated/);
  });

  it("entries stay sorted by rva ascending regardless of insertion order", () => {
    const buf = buildSymmap([
      { rva: 0x3000, size: 0x10, name: "third" },
      { rva: 0x1000, size: 0x10, name: "first" },
      { rva: 0x2000, size: 0x10, name: "second" },
    ]);
    const map = parseSymmap(buf);
    const rvas = Array.from({ length: map.count }, (_, i) => map.entries[i * 3]);
    expect(rvas).toEqual([0x1000, 0x2000, 0x3000]);
  });
});

describe("resolveFrame — nearest-at-or-below binary search", () => {
  const buf = buildSymmap([
    { rva: 0x1000, size: 0x40, name: "MethodA" },
    { rva: 0x2000, size: 0x80, name: "MethodB" },
    { rva: 0x3000, size: 0x20, name: "MethodC" },
  ]);
  const map = parseSymmap(buf);

  it("resolves an exact rva match with offset 0", () => {
    expect(resolveFrame(map, 0x2000)).toEqual({ rva: 0x2000, offset: 0, name: "MethodB" });
  });

  it("resolves an rva inside a symbol's range to that symbol + offset", () => {
    expect(resolveFrame(map, 0x2050)).toEqual({ rva: 0x2050, offset: 0x50, name: "MethodB" });
  });

  it("resolves an rva past the last entry to the last entry (cdb's own nearest-below rule)", () => {
    expect(resolveFrame(map, 0x3fff)).toEqual({ rva: 0x3fff, offset: 0xfff, name: "MethodC" });
  });

  it("returns a null name for an rva before the first entry", () => {
    expect(resolveFrame(map, 0x500)).toEqual({ rva: 0x500, offset: 0, name: null });
  });

  it("handles an empty map", () => {
    const emptyBuf = buildSymmap([]);
    const emptyMap = parseSymmap(emptyBuf);
    expect(resolveFrame(emptyMap, 0x1000)).toEqual({ rva: 0x1000, offset: 0, name: null });
  });

  it("resolves every entry in a larger, randomly-ordered map (binary search correctness)", () => {
    const entries = Array.from({ length: 200 }, (_, i) => ({ rva: i * 16, size: 8, name: `M${i}` }));
    const shuffled = [...entries].sort(() => Math.random() - 0.5);
    const bigBuf = buildSymmap(shuffled);
    const bigMap = parseSymmap(bigBuf);
    for (const e of entries) {
      expect(resolveFrame(bigMap, e.rva + 2)).toEqual({ rva: e.rva + 2, offset: 2, name: e.name });
    }
  });
});

describe("symbolicate — map cache (#165)", () => {
  const quad = "0.3.0.77";
  const arch = "arm64";
  const T0 = Date.parse("2026-10-02T12:00:00.000Z");

  beforeEach(() => {
    clearSymmapCacheForTests();
  });

  it("remembers a missing map for at most NEGATIVE_TTL_MS, then finds one uploaded meanwhile", async () => {
    expect(NEGATIVE_TTL_MS).toBe(300_000);
    const bucket = makeFakeR2();
    const get = vi.spyOn(bucket, "get");

    const before = await symbolicate(bucket, quad, arch, [0x1010], T0);
    expect(before.frames).toEqual([{ rva: 0x1010, offset: 0, name: null }]);
    expect(before.debugId).toBeNull();

    // The release uploads the map after the first report landed.
    await bucket.put(symmapR2Key(quad, arch), buildSymmap([{ rva: 0x1000, size: 0x40, name: "Wavee_Late_Map" }]));

    const within = await symbolicate(bucket, quad, arch, [0x1010], T0 + NEGATIVE_TTL_MS - 1);
    expect(within.frames[0]!.name).toBeNull();
    expect(get).toHaveBeenCalledTimes(1); // still cached as missing — no second R2 GET

    const after = await symbolicate(bucket, quad, arch, [0x1010], T0 + NEGATIVE_TTL_MS);
    expect(after.frames).toEqual([{ rva: 0x1010, offset: 0x10, name: "Wavee_Late_Map" }]);
    expect(after.debugId).not.toBeNull();
    expect(get).toHaveBeenCalledTimes(2);
  });

  it("keeps a found map for the isolate's lifetime (no further R2 GETs)", async () => {
    const bucket = makeFakeR2();
    await bucket.put(symmapR2Key(quad, arch), buildSymmap([{ rva: 0x1000, size: 0x40, name: "Wavee_Kept" }]));
    const get = vi.spyOn(bucket, "get");

    await symbolicate(bucket, quad, arch, [0x1000], T0);
    const later = await symbolicate(bucket, quad, arch, [0x1000], T0 + 365 * 86_400_000);
    expect(later.frames[0]!.name).toBe("Wavee_Kept");
    expect(get).toHaveBeenCalledTimes(1);
  });
});
