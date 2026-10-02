import { describe, expect, it } from "vitest";
import {
  formatBytes,
  formatCount,
  formatDeltaPct,
  formatDurationMs,
  hex8,
  nativeFaultText,
  normalizeIdQuery,
  ntStatusName,
  pctChange,
  shortId,
} from "./format";

describe("ntStatusName / hex8", () => {
  it("names the NTSTATUS codes the Worker's titles use", () => {
    expect(ntStatusName(0xc0000005)).toBe("ACCESS_VIOLATION");
    expect(ntStatusName(0xc0000409)).toBe("STACK_BUFFER_OVERRUN (fail-fast)");
    expect(ntStatusName(0xc00000fd)).toBe("STACK_OVERFLOW");
    expect(ntStatusName(0xc0000374)).toBe("HEAP_CORRUPTION");
    expect(ntStatusName(0xc0000094)).toBe("INTEGER_DIVIDE_BY_ZERO");
    expect(ntStatusName(0xc000001d)).toBe("ILLEGAL_INSTRUCTION");
    expect(ntStatusName(0xc0000602)).toBe("FAIL_FAST_EXCEPTION");
  });

  it("falls back to 8-digit upper-case hex, also for a signed 32-bit value", () => {
    expect(ntStatusName(0xc0000135)).toBe("0xC0000135");
    expect(ntStatusName(0xc0000005 | 0)).toBe("ACCESS_VIOLATION");
    expect(hex8(0x9a2)).toBe("000009A2");
  });
});

describe("nativeFaultText", () => {
  it("formats code, module and offset", () => {
    expect(nativeFaultText({ exceptionCode: 0xc0000005, faultModule: "nvwgf2umx.dll", faultOffset: 0x2f10c })).toBe(
      "ACCESS_VIOLATION in nvwgf2umx.dll+0x2f10c",
    );
  });

  it("names an unknown module and is null without any fault facts", () => {
    expect(nativeFaultText({ exceptionCode: 0xc0000409, faultModule: "", faultOffset: 0 })).toBe(
      "STACK_BUFFER_OVERRUN (fail-fast) in unknown module+0x0",
    );
    expect(nativeFaultText({ exceptionCode: 0, faultModule: "", faultOffset: 0 })).toBeNull();
  });
});

describe("normalizeIdQuery", () => {
  it("strips dashes so the app's short id matches a dash-less id prefix", () => {
    expect(normalizeIdQuery("  3f9c-2b1a ")).toBe("3f9c2b1a");
    expect(normalizeIdQuery("3f9c2b1a-0000-4000-8000-000000000000")).toBe("3f9c2b1a000040008000000000000000");
    expect(normalizeIdQuery("")).toBe("");
  });
});

describe("shortId", () => {
  it("truncates long ids with an ellipsis", () => {
    expect(shortId("0123456789abcdef")).toBe("01234567…");
  });

  it("leaves short ids untouched", () => {
    expect(shortId("short")).toBe("short");
  });
});

describe("formatBytes", () => {
  it("formats zero and negative as 0 B", () => {
    expect(formatBytes(0)).toBe("0 B");
    expect(formatBytes(-5)).toBe("0 B");
  });

  it("formats bytes without a decimal", () => {
    expect(formatBytes(512)).toBe("512 B");
  });

  it("formats KB/MB/GB with one decimal", () => {
    expect(formatBytes(2048)).toBe("2.0 KB");
    expect(formatBytes(5 * 1024 * 1024)).toBe("5.0 MB");
  });
});

describe("formatCount", () => {
  it("adds thousands separators", () => {
    expect(formatCount(1234567)).toBe("1,234,567");
  });
});

describe("pctChange", () => {
  it("returns null when both values are zero", () => {
    expect(pctChange(0, 0)).toBeNull();
  });

  it("returns 100 when previous is zero but current is not", () => {
    expect(pctChange(5, 0)).toBe(100);
  });

  it("computes a rounded percentage change", () => {
    expect(pctChange(120, 100)).toBe(20);
    expect(pctChange(80, 100)).toBe(-20);
  });
});

describe("formatDeltaPct", () => {
  it("prefixes positive deltas with a plus sign", () => {
    expect(formatDeltaPct(120, 100)).toBe("+20%");
  });

  it("does not prefix negative deltas", () => {
    expect(formatDeltaPct(80, 100)).toBe("-20%");
  });

  it("treats a zero/zero comparison as 0%", () => {
    expect(formatDeltaPct(0, 0)).toBe("0%");
  });
});

describe("formatDurationMs", () => {
  it("formats sub-second durations in ms", () => {
    expect(formatDurationMs(500)).toBe("500 ms");
  });

  it("formats seconds with one decimal", () => {
    expect(formatDurationMs(31_000)).toBe("31.0 s");
  });

  it("formats minutes and seconds", () => {
    expect(formatDurationMs(90_000)).toBe("1m 30s");
  });
});
