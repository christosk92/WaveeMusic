import { describe, expect, it } from "vitest";
import {
  FINGERPRINT_VERSION,
  codeName,
  computeFingerprint,
  deriveTitle,
  fingerprintKey,
  isFrameworkFrame,
  normalizeSymbol,
  topFrameKey,
  type GroupingInput,
} from "../src/grouping.js";
import type { Frame, Kind } from "../src/types.js";

// Grouping v2 (#165, plan appendix A1). Symbol names below are real ones from 1.2.1011.0's Wavee.map.xml.

const named = (...names: string[]): Frame[] => names.map((name, i) => ({ rva: 0x1000 * (i + 1), offset: 0x10, name }));
const unresolved = (...rvas: number[]): Frame[] => rvas.map((rva) => ({ rva, offset: 0, name: null }));

function input(kind: Kind, overrides: Partial<GroupingInput> = {}): GroupingInput {
  return {
    kind,
    exceptionType: "",
    exitCode: 0,
    exceptionCode: 0,
    faultModule: "",
    faultOffset: 0,
    frames: [],
    ...overrides,
  };
}

const APP_FRAMES = [
  "Wavee_Wavee_Entities_ArtistReader__Build",
  "Wavee_Wavee_Entities_Detail_UI__Render",
  "FluentGpu_Engine_FluentGpu_Hooks_Effect__Run",
  "Wavee_Wavee_App__Main",
];

describe("normalizeSymbol — compiler-generated ordinals", () => {
  it.each([
    [
      "Wavee_Wavee_ArtistPopular___c__DisplayClass28_0___Render_b__0_d____GetFieldHelper",
      "Wavee_Wavee_ArtistPopular___c__DisplayClass___Render_b___d____GetFieldHelper",
    ],
    [
      "FluentGpu_Engine_FluentGpu_Media_PcmAudioPlayer__OpenAsync_d__24__MoveNext",
      "FluentGpu_Engine_FluentGpu_Media_PcmAudioPlayer__OpenAsync_d____MoveNext",
    ],
    [
      "System_Linq_System_Linq_Enumerable___ToArray_g__EnumerableToArray_324_0",
      "System_Linq_System_Linq_Enumerable___ToArray_g__EnumerableToArray",
    ],
    ["unbox_Wavee_Wavee_Entities_EntityRef__Equals", "Wavee_Wavee_Entities_EntityRef__Equals"],
    ["Wavee_Wavee_Shell_Queue___c___Pack_b__12_3", "Wavee_Wavee_Shell_Queue___c___Pack_b__"],
    ["Wavee_Wavee_App__Main", "Wavee_Wavee_App__Main"],
  ])("%s → %s", (raw, expected) => {
    expect(normalizeSymbol(raw)).toBe(expected);
  });

  it("maps two builds' ordinals for the same lambda to one name", () => {
    expect(normalizeSymbol("Wavee_X___c__DisplayClass12_0___Foo_b__3")).toBe(
      normalizeSymbol("Wavee_X___c__DisplayClass14_1___Foo_b__5"),
    );
  });
});

describe("isFrameworkFrame — classifier table", () => {
  it.each([
    "S_P_CoreLib_System_ThrowHelper__ThrowInvalidOperationException",
    "S_P_CoreLib_System_Runtime_EH__RhThrowEx",
    "S_P_Reflection_Execution_Internal_Reflection_Execution_ExecutionEnvironmentImplementation__GetMethodInvoker",
    "System_Linq_System_Linq_Enumerable___ToArray_g__EnumerableToArray_324_0",
    "System_Private_Uri_System_Uri__CreateThis",
    "Microsoft_Data_Sqlite_Microsoft_Data_Sqlite_SqliteConnection__Open",
    "Internal_Runtime_CompilerHelpers_ThrowHelpers__ThrowNullReferenceException",
    "SQLitePCL_SQLitePCL_raw__sqlite3_step",
    "Google_Protobuf_Google_Protobuf_CodedInputStream__ReadTag",
    "NLayer_NLayer_MpegFrameDecoder__DecodeFrame",
    "ZstdSharp_ZstdSharp_Decompressor__Unwrap",
    "__GenericDict_Wavee_Wavee_Store_Signal_1<System___Canon>",
    "__GetNonGCStaticBase_Wavee_Wavee_App",
    "__security_check_cookie",
    "RhpThrowEx",
    "RhThrowHwEx",
    "RhpNewFast",
    "PalRaiseFailFast",
    "WKS::gc_heap::allocate_more_space",
    "SVR_gc_heap_mark",
    "memset",
    "memcpy",
    "RtlRaiseException",
    "RaiseException",
    "RaiseFailFastException",
    "KiUserExceptionDispatcher",
    "unbox_S_P_CoreLib_System_Collections_Generic_List_1<System___Canon>__get_Item",
  ])("framework: %s", (name) => {
    expect(isFrameworkFrame(name)).toBe(true);
  });

  it.each([
    ...APP_FRAMES,
    "Wavee_Wavee_ArtistPopular___c__DisplayClass28_0___Render_b__0_d____GetFieldHelper",
    "FluentGpu_Engine_FluentGpu_Media_PcmAudioPlayer__OpenAsync_d__24__MoveNext",
    "unbox_Wavee_Wavee_Entities_EntityRef__Equals",
    "Rhythm_Should_Not_Match_lowercase_h", // `Rh` + lower-case is not a runtime helper
    "Palette_Wavee_Like", // `Pal` + lower-case
  ])("app: %s", (name) => {
    expect(isFrameworkFrame(name)).toBe(false);
  });
});

describe("topFrameKey", () => {
  it("skips framework frames and keeps the top 3 app frames, normalized", () => {
    const frames = named(
      "S_P_CoreLib_System_ThrowHelper__ThrowInvalidOperationException",
      "Wavee_X___c__DisplayClass12_0___Foo_b__3",
      ...APP_FRAMES,
    );
    expect(topFrameKey(frames)).toBe(
      ["Wavee_X___c__DisplayClass___Foo_b__", APP_FRAMES[0], APP_FRAMES[1]].join("|"),
    );
  });

  it("falls back to the top 3 raw names when every frame is framework", () => {
    const frames = named("RhpThrowEx", "S_P_CoreLib_System_ThrowHelper__Throw", "memset", "System_Foo");
    expect(topFrameKey(frames)).toBe("RhpThrowEx|S_P_CoreLib_System_ThrowHelper__Throw|memset");
  });

  it("falls back to RVAs when nothing resolved (no symmap)", () => {
    expect(topFrameKey(unresolved(0x1010, 0x2050, 0x3090, 0x40a0))).toBe("1010|2050|3090");
  });

  it("keeps an unresolved frame's slot among resolved ones as ?", () => {
    const frames: Frame[] = [{ rva: 1, offset: 0, name: null }, ...named("Wavee_A", "Wavee_B")];
    expect(topFrameKey(frames)).toBe("?|Wavee_A|Wavee_B");
  });

  it("is empty for no frames", () => {
    expect(topFrameKey([])).toBe("");
  });
});

describe("fingerprintKey / computeFingerprint", () => {
  it("every key carries the v2 prefix", () => {
    expect(FINGERPRINT_VERSION).toBe(2);
    for (const kind of ["Managed", "Native", "Hang", "ExitCode", "UncleanExit"] as Kind[]) {
      expect(fingerprintKey(input(kind, { frames: named(...APP_FRAMES) }))).toMatch(/^v2\|/);
    }
  });

  it("is a 40-char SHA-1 hex, deterministic", async () => {
    const i = input("Managed", { exceptionType: "System.InvalidOperationException", frames: named(...APP_FRAMES) });
    const a = await computeFingerprint(i);
    expect(a).toMatch(/^[0-9a-f]{40}$/);
    expect(await computeFingerprint({ ...i })).toBe(a);
  });

  it("Managed: a difference only in the ThrowHelper frame groups into one issue", async () => {
    const a = input("Managed", {
      exceptionType: "System.InvalidOperationException",
      frames: named("S_P_CoreLib_System_ThrowHelper__ThrowInvalidOperationException", ...APP_FRAMES),
    });
    const b = input("Managed", {
      exceptionType: "System.InvalidOperationException",
      frames: named(
        "S_P_CoreLib_System_ThrowHelper__ThrowInvalidOperationException_NoValue",
        "S_P_CoreLib_System_Runtime_EH__RhThrowEx",
        ...APP_FRAMES,
      ),
    });
    expect(await computeFingerprint(a)).toBe(await computeFingerprint(b));
  });

  it("Managed: lambda ordinals that moved between builds group into one issue", async () => {
    const a = input("Managed", { exceptionType: "X", frames: named("Wavee_X___c__DisplayClass12_0___Foo_b__3", "Wavee_App__Main") });
    const b = input("Managed", { exceptionType: "X", frames: named("Wavee_X___c__DisplayClass14_1___Foo_b__5", "Wavee_App__Main") });
    expect(await computeFingerprint(a)).toBe(await computeFingerprint(b));
  });

  it("Managed: a different exception type or app stack splits the issue", async () => {
    const base = input("Managed", { exceptionType: "System.InvalidOperationException", frames: named(...APP_FRAMES) });
    const otherType = { ...base, exceptionType: "System.NullReferenceException" };
    const otherStack = { ...base, frames: named("Wavee_Somewhere_Else", ...APP_FRAMES.slice(1)) };
    const fp = await computeFingerprint(base);
    expect(await computeFingerprint(otherType)).not.toBe(fp);
    expect(await computeFingerprint(otherStack)).not.toBe(fp);
  });

  it("Managed: the RVA fallback groups the same unresolved stack and splits a different one", async () => {
    const a = input("Managed", { exceptionType: "X", frames: unresolved(0x1010, 0x2050) });
    const b = input("Managed", { exceptionType: "X", frames: unresolved(0x1010, 0x2050) });
    const c = input("Managed", { exceptionType: "X", frames: unresolved(0x1011, 0x2050) });
    expect(fingerprintKey(a)).toBe("v2|Managed|X|1010|2050");
    expect(await computeFingerprint(a)).toBe(await computeFingerprint(b));
    expect(await computeFingerprint(a)).not.toBe(await computeFingerprint(c));
  });

  it("ExitCode: groups by code (signed or unsigned form), ignoring frames", () => {
    const a = input("ExitCode", { exitCode: 0xc0000409, frames: named("Wavee_A") });
    const b = input("ExitCode", { exitCode: 0xc0000409 | 0, frames: named("Wavee_B") }); // the client's int form
    expect(fingerprintKey(a)).toBe("v2|ExitCode|C0000409");
    expect(fingerprintKey(b)).toBe(fingerprintKey(a));
    expect(fingerprintKey(input("ExitCode", { exitCode: 1 }))).toBe("v2|ExitCode|00000001");
  });

  it("Hang and UncleanExit: one bucket each, whatever the frames", () => {
    expect(fingerprintKey(input("Hang", { frames: named("Wavee_A") }))).toBe("v2|Hang");
    expect(fingerprintKey(input("Hang", { frames: named("Wavee_B"), exceptionType: "Y" }))).toBe("v2|Hang");
    expect(fingerprintKey(input("UncleanExit", { frames: named("Wavee_A") }))).toBe("v2|UncleanExit");
  });

  it("Native: code + module + Wavee stack; the offset only matters without frames", () => {
    const withFrames = input("Native", {
      exceptionCode: 0xc0000005,
      faultModule: "ntdll.dll",
      faultOffset: 0x1234,
      frames: named("RtlRaiseException", ...APP_FRAMES),
    });
    expect(fingerprintKey(withFrames)).toBe(`v2|Native|C0000005|ntdll.dll|${APP_FRAMES.slice(0, 3).join("|")}`);
    expect(fingerprintKey({ ...withFrames, faultOffset: 0x9999 })).toBe(fingerprintKey(withFrames));
    expect(fingerprintKey({ ...withFrames, faultModule: "d3d12core.dll" })).not.toBe(fingerprintKey(withFrames));

    const noFrames = input("Native", { exceptionCode: 0xc0000005, faultModule: "ntdll.dll", faultOffset: 0x1234 });
    expect(fingerprintKey(noFrames)).toBe("v2|Native|C0000005|ntdll.dll+1234");
    expect(fingerprintKey(input("Native", { exceptionCode: 0xc0000005 }))).toBe("v2|Native|C0000005|?+0");
  });
});

describe("deriveTitle", () => {
  it("Hang", () => {
    expect(deriveTitle(input("Hang", { frames: named("Wavee_A") }))).toBe("Hang (UI stopped responding)");
  });

  it("UncleanExit", () => {
    expect(deriveTitle(input("UncleanExit"))).toBe("Unclean exit");
  });

  it("ExitCode names the NTSTATUS, else hex", () => {
    expect(deriveTitle(input("ExitCode", { exitCode: 0xc0000409 | 0 }))).toBe("Exit code STACK_BUFFER_OVERRUN (fail-fast)");
    expect(deriveTitle(input("ExitCode", { exitCode: 0xdead }))).toBe("Exit code 0x0000DEAD");
    expect(codeName(0xc0000005)).toBe("ACCESS_VIOLATION");
  });

  it("Native: code in module · first Wavee frame, else +offset", () => {
    const frames = named("RtlRaiseException", "FluentGpu_Engine_Thing__Run", "Wavee_Wavee_Media__Decode");
    expect(deriveTitle(input("Native", { exceptionCode: 0xc0000005, faultModule: "ntdll.dll", frames }))).toBe(
      "ACCESS_VIOLATION in ntdll.dll · Wavee_Wavee_Media__Decode",
    );
    expect(deriveTitle(input("Native", { exceptionCode: 0xc0000005, faultModule: "ntdll.dll", faultOffset: 0xbeef }))).toBe(
      "ACCESS_VIOLATION in ntdll.dll+0xbeef",
    );
    expect(deriveTitle(input("Native", { exceptionCode: 0xc0000374 }))).toBe("HEAP_CORRUPTION in unknown module+0x0");
  });

  it("Managed: type · first Wavee frame (not the ThrowHelper), else the type", () => {
    const frames = named("S_P_CoreLib_System_ThrowHelper__ThrowInvalidOperationException", "Wavee_X___c__DisplayClass12_0___Foo_b__3");
    expect(deriveTitle(input("Managed", { exceptionType: "System.InvalidOperationException", frames }))).toBe(
      "System.InvalidOperationException · Wavee_X___c__DisplayClass___Foo_b__",
    );
    expect(deriveTitle(input("Managed", { exceptionType: "System.InvalidOperationException", frames: unresolved(1) }))).toBe(
      "System.InvalidOperationException",
    );
    expect(deriveTitle(input("Managed"))).toBe("Managed");
  });

  it("falls back to an engine frame when no Wavee frame resolved", () => {
    const frames = named("S_P_CoreLib_System_ThrowHelper__Throw", "FluentGpu_Engine_FluentGpu_Hooks_Effect__Run");
    expect(deriveTitle(input("Managed", { exceptionType: "E", frames }))).toBe("E · FluentGpu_Engine_FluentGpu_Hooks_Effect__Run");
  });
});
