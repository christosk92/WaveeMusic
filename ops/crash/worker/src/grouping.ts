import type { Frame, Kind } from "./types.js";

/**
 * Grouping v3 (crash round 2026-11-07) over v2 (#165, plan §W1 + appendix A1) — the issue fingerprint and title for a
 * report. v1 keyed on `kind|exceptionType|top-3 raw names` (usually a .NET ThrowHelper) and collapsed every Native, Hang
 * and ExitCode report into one issue each; v2 keyed frame-bearing kinds on the top THREE app frames.
 *
 * v3 keys Managed and frame-bearing Native reports on the innermost TWO app frames (`KEY_APP_FRAMES`): the third frame is
 * usually "who called the faulting method", and one bug reached from two callers became two issues (#5 / #6: the same
 * `SceneStore.ClearSubtreeLayoutDirtyBits` ← `AppHost.Paint` crash, once under a WndProc paint and once under
 * `Shell.Run` → `RunFrameCore`). Kinds whose key did not change (Hang, UncleanExit, ExitCode, a frameless Native) keep
 * their v2 key, so their open issues keep their identity.
 *
 * Bump FINGERPRINT_VERSION whenever `fingerprintKey` changes shape: a new version opens new issues (the key carries a
 * `v<N>|` prefix, so its SHA-1 never collides with an older version's), and `fp_version` records which one a row used
 * (`fingerprintVersion`, read from the key's own prefix). v2 issues of the changed kinds stop receiving reports; nothing
 * is re-fingerprinted.
 */
export const FINGERPRINT_VERSION = 3;

/** How many innermost app frames a v3 Managed / Native key is built from. */
export const KEY_APP_FRAMES = 2;

/** BCL, NativeAOT runtime, ILC thunks, C runtime and the NuGet libraries Wavee.csproj links. Matched against
 *  normalizeSymbol(name). App frames = everything else (Wavee_*, FluentGpu_* — the engine is ours). */
export const FRAMEWORK_FRAME_PATTERNS: readonly RegExp[] = [
  /^S_P_[A-Za-z]+_/, // System.Private.* (CoreLib, Reflection.Execution, Interop, TypeLoader…)
  /^System_/, // every System.* assembly
  /^Microsoft_/, // Microsoft.Data.Sqlite, Microsoft.Win32.*
  /^Internal_/, // Internal.Runtime.* inside CoreLib
  /^(SQLitePCL|Google_Protobuf|NLayer|ZstdSharp)_/, // third-party packages
  /^__/, // __GenericDict_*, __GetNonGCStaticBase_*, __security_check_cookie…
  /^Rhp?[A-Z]/, /^Pal[A-Z]/, // RhpThrowEx, RhThrowHwEx, RhpNewFast, PalRaiseFailFast…
  /::/, /^(WKS|SVR)_/, // C++ runtime / GC
  /^(mem(set|cpy|move|cmp)|strlen|wcslen|RtlRaiseException|RaiseException|RaiseFailFastException|KiUserExceptionDispatcher|DebugBreak)$/,
];

/** Compiler-generated ordinals shift whenever code moves inside a method; they must not split an issue.
 *  `unbox_` stubs fold into their target; `DisplayClass28_0`, `_b__0_1`, `_d__24` and local-function
 *  `_g__Name_324_0` lose their numbers. */
export function normalizeSymbol(raw: string): string {
  return raw
    .replace(/^unbox_/, "")
    .replace(/DisplayClass\d+_\d+/g, "DisplayClass")
    .replace(/_b__\d+(?:_\d+)?/g, "_b__")
    .replace(/_d__\d+/g, "_d__")
    .replace(/(_g__[A-Za-z0-9]+)_\d+_\d+/g, "$1");
}

export function isFrameworkFrame(name: string): boolean {
  const n = normalizeSymbol(name);
  return FRAMEWORK_FRAME_PATTERNS.some((rx) => rx.test(n));
}

export interface GroupingInput {
  kind: Kind;
  exceptionType: string;
  exitCode: number;
  exceptionCode: number;
  faultModule: string;
  faultOffset: number;
  frames: readonly Frame[];
}

const hex8 = (n: number) => (n >>> 0).toString(16).toUpperCase().padStart(8, "0");

/** The innermost `count` app frames (framework skipped; all-framework → the innermost raw names); RVAs when nothing
 *  resolved (no symmap). An unresolved frame among resolved ones keeps its slot as "?". Frames are innermost first. */
export function topFrameKey(frames: readonly Frame[], count = KEY_APP_FRAMES): string {
  if (frames.length === 0) return "";
  if (!frames.some((f) => f.name !== null)) return frames.slice(0, count).map((f) => f.rva.toString(16)).join("|");
  const app = frames.filter((f) => f.name === null || !isFrameworkFrame(f.name));
  return (app.length > 0 ? app : frames)
    .slice(0, count)
    .map((f) => (f.name === null ? "?" : normalizeSymbol(f.name)))
    .join("|");
}

/** The grouping key, by kind (plan §W1 table). Hang and UncleanExit are one bucket each; ExitCode groups by code;
 *  Native by code + faulting module + Wavee stack (module+offset when no frame was captured); Managed by type + stack. */
export function fingerprintKey(i: GroupingInput): string {
  switch (i.kind) {
    case "Hang":
      return "v2|Hang";
    case "UncleanExit":
      return "v2|UncleanExit";
    case "ExitCode":
      return `v2|ExitCode|${hex8(i.exitCode)}`;
    case "Native": {
      const top = topFrameKey(i.frames), mod = i.faultModule || "?";
      return top !== ""
        ? `v3|Native|${hex8(i.exceptionCode)}|${mod}|${top}`
        : `v2|Native|${hex8(i.exceptionCode)}|${mod}+${i.faultOffset.toString(16)}`;
    }
    default:
      return `v3|Managed|${i.exceptionType}|${topFrameKey(i.frames)}`;
  }
}

/** The `fp_version` a key was built with: its own `v<N>|` prefix (a kind whose key did not change keeps an older one). */
export function fingerprintVersion(i: GroupingInput): number {
  const m = /^v(\d+)\|/.exec(fingerprintKey(i));
  return m ? Number(m[1]) : FINGERPRINT_VERSION;
}

/** SHA-1 hex — the `issues.fingerprint` primary key. */
export async function sha1Hex(input: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-1", new TextEncoder().encode(input));
  return Array.from(new Uint8Array(digest))
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}

export async function computeFingerprint(i: GroupingInput): Promise<string> {
  return sha1Hex(fingerprintKey(i));
}

const STATUS_NAMES: Record<number, string> = {
  0xc0000005: "ACCESS_VIOLATION",
  0xc0000006: "IN_PAGE_ERROR",
  0xc000001d: "ILLEGAL_INSTRUCTION",
  0xc000008c: "ARRAY_BOUNDS_EXCEEDED",
  0xc0000094: "INTEGER_DIVIDE_BY_ZERO",
  0xc00000fd: "STACK_OVERFLOW",
  0xc0000409: "STACK_BUFFER_OVERRUN (fail-fast)",
  0xc0000374: "HEAP_CORRUPTION",
  0xc0000602: "FAIL_FAST_EXCEPTION",
  0xc000041d: "FATAL_USER_CALLBACK_EXCEPTION",
  0x80000003: "BREAKPOINT",
  0xc0000135: "DLL_NOT_FOUND",
  0xc0000142: "DLL_INIT_FAILED",
  0x40000015: "FATAL_APP_EXIT",
};

/** The NTSTATUS name for a code, else `0xXXXXXXXX`. Takes the signed form too (the client's exit code is an int). */
export const codeName = (c: number): string => STATUS_NAMES[c >>> 0] ?? `0x${hex8(c)}`;

/** The innermost normalized app frame, engine (FluentGpu_*) or app (Wavee_*) alike: where it broke, not who called it
 *  (v2 preferred a Wavee_* frame, which titled an engine crash after the app's outer loop). */
function firstApp(frames: readonly Frame[]): string | null {
  const named = frames
    .filter((x): x is Frame & { name: string } => x.name !== null && !isFrameworkFrame(x.name))
    .map((x) => normalizeSymbol(x.name));
  return named[0] ?? null;
}

/** `issues.title` — insert-only on the issues row (store.ts `recordIssueOccurrence`). */
export function deriveTitle(i: GroupingInput): string {
  switch (i.kind) {
    case "Hang":
      return "Hang (UI stopped responding)";
    case "UncleanExit":
      return "Unclean exit";
    case "ExitCode":
      return `Exit code ${codeName(i.exitCode)}`;
    case "Native": {
      const top = firstApp(i.frames);
      const where = `${codeName(i.exceptionCode)} in ${i.faultModule || "unknown module"}`;
      return top ? `${where} · ${top}` : `${where}+0x${i.faultOffset.toString(16)}`;
    }
    default: {
      const top = firstApp(i.frames);
      const head = i.exceptionType || "Managed";
      return top ? `${head} · ${top}` : head;
    }
  }
}
