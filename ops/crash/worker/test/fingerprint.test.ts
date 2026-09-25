import { describe, expect, it } from "vitest";
import { computeFingerprint } from "../src/fingerprint.js";
import type { Frame } from "../src/types.js";

const framesWithNames: Frame[] = [
  { rva: 0x1010, offset: 0x10, name: "Wavee_Entities_Detail_UI_Hero__Render" },
  { rva: 0x2050, offset: 0x50, name: "Wavee_App_Main" },
  { rva: 0x3090, offset: 0x90, name: "Wavee_Shell_Run" },
];

const framesUnresolved: Frame[] = [
  { rva: 0x1010, offset: 0, name: null },
  { rva: 0x2050, offset: 0, name: null },
  { rva: 0x3090, offset: 0, name: null },
];

describe("fingerprint stability", () => {
  it("is deterministic for the same input", async () => {
    const a = await computeFingerprint("Managed", "System.NullReferenceException", framesWithNames);
    const b = await computeFingerprint("Managed", "System.NullReferenceException", framesWithNames);
    expect(a).toBe(b);
    expect(a).toMatch(/^[0-9a-f]{40}$/); // SHA-1 hex
  });

  it("is stable across two reports that share kind/exceptionType/top-3 names even with different RVAs", async () => {
    const framesOtherBuild: Frame[] = [
      { rva: 0x9010, offset: 0x10, name: "Wavee_Entities_Detail_UI_Hero__Render" },
      { rva: 0x9050, offset: 0x50, name: "Wavee_App_Main" },
      { rva: 0x9090, offset: 0x90, name: "Wavee_Shell_Run" },
    ];
    const a = await computeFingerprint("Managed", "System.NullReferenceException", framesWithNames);
    const b = await computeFingerprint("Managed", "System.NullReferenceException", framesOtherBuild);
    expect(a).toBe(b);
  });

  it("changes when the kind changes", async () => {
    const a = await computeFingerprint("Managed", "System.NullReferenceException", framesWithNames);
    const b = await computeFingerprint("Hang", "System.NullReferenceException", framesWithNames);
    expect(a).not.toBe(b);
  });

  it("changes when the exception type changes", async () => {
    const a = await computeFingerprint("Managed", "System.NullReferenceException", framesWithNames);
    const b = await computeFingerprint("Managed", "System.InvalidOperationException", framesWithNames);
    expect(a).not.toBe(b);
  });

  it("changes when the top frame names change", async () => {
    const a = await computeFingerprint("Managed", "System.NullReferenceException", framesWithNames);
    const other = [...framesWithNames];
    other[0] = { ...other[0]!, name: "Wavee_Somewhere_Else" };
    const b = await computeFingerprint("Managed", "System.NullReferenceException", other);
    expect(a).not.toBe(b);
  });

  it("falls back to RVAs when no frame resolved a name (no symbol map)", async () => {
    const a = await computeFingerprint("Native", "", framesUnresolved);
    const sameRvasDifferentKind = await computeFingerprint("Native", "", framesUnresolved.map((f) => ({ ...f })));
    expect(a).toBe(sameRvasDifferentKind);

    const differentRvas: Frame[] = framesUnresolved.map((f) => ({ ...f, rva: f.rva + 1 }));
    const b = await computeFingerprint("Native", "", differentRvas);
    expect(a).not.toBe(b);
  });

  it("a partially-resolved set (some names null) still uses the name branch, not RVAs", async () => {
    const partial: Frame[] = [
      { rva: 0x1010, offset: 0x10, name: "Wavee_Known_Frame" },
      { rva: 0x2050, offset: 0, name: null },
      { rva: 0x3090, offset: 0, name: null },
    ];
    const samePartialDifferentRvas: Frame[] = [
      { rva: 0x9010, offset: 0x10, name: "Wavee_Known_Frame" },
      { rva: 0x9050, offset: 0, name: null },
      { rva: 0x9090, offset: 0, name: null },
    ];
    const a = await computeFingerprint("Managed", "X", partial);
    const b = await computeFingerprint("Managed", "X", samePartialDifferentRvas);
    expect(a).toBe(b);
  });
});
