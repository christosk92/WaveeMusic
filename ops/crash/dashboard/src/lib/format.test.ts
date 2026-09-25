import { describe, expect, it } from "vitest";
import { formatBytes, formatCount, formatDeltaPct, formatDurationMs, pctChange, shortId } from "./format";

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
