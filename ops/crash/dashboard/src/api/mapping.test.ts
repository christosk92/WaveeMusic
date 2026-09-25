import { describe, expect, it } from "vitest";
import {
  mapBreakdowns,
  mapIssue,
  mapIssueDetail,
  mapOccurrence,
  mapReport,
  mapReportDetail,
  mapReportsPage,
  mapStats,
  mapSymbol,
  mapThisInstall,
  mapVersionCount,
} from "./mapping";
import type {
  WireIssueBreakdowns,
  WireIssueDetailResponse,
  WireIssueRow,
  WireOccurrenceRow,
  WireReportDetailResponse,
  WireReportRow,
  WireReportsPageResponse,
  WireStats,
  WireSymbolRow,
  WireThisInstall,
  WireVersionCount,
} from "./types";

const wireReport: WireReportRow = {
  id: "r1",
  install_id: "install-1",
  session_id: "sess-1",
  kind: "Managed",
  quad: "0.3.2-stable",
  semver: "0.3.2",
  commit_sha: "abc1234",
  channel: "stable",
  arch: "x64",
  os_build: "10.0.26100",
  gpu: "NVIDIA GeForce RTX 3060",
  gpu_tier: "high",
  software_adapter: 0,
  packaged: 1,
  locale: "en-US",
  uptime_ms: 12345,
  before_first_frame: 0,
  last_route: "/album/1",
  exception_type: "System.NullReferenceException",
  exception_message: "Object reference not set to an instance of an object.",
  exit_code: 0,
  has_dump: 1,
  dump_bytes: 204800,
  frames_json: JSON.stringify([{ rva: 100, offset: 4, name: "Detail.UI.Hero.Render" }]),
  fingerprint: "fp-1",
  received_at: "2026-09-01T12:00:00.000Z",
  debug_id: "3F2504E0-4F89-11D3-9A0C-0305E82C3301",
};

describe("mapReport", () => {
  it("converts snake_case wire fields and booleans, and parses frames_json", () => {
    const app = mapReport(wireReport);
    expect(app.installId).toBe("install-1");
    expect(app.softwareAdapter).toBe(false);
    expect(app.packaged).toBe(true);
    expect(app.hasDump).toBe(true);
    expect(app.frames).toEqual([{ rva: 100, offset: 4, name: "Detail.UI.Hero.Render" }]);
  });

  it("falls back to an empty frames array on malformed JSON", () => {
    const app = mapReport({ ...wireReport, frames_json: "not json" });
    expect(app.frames).toEqual([]);
  });
});

describe("mapIssue", () => {
  it("parses versions_json and renames snake_case fields", () => {
    const wire: WireIssueRow = {
      fingerprint: "fp-1",
      title: "NullReferenceException · Detail.UI.Hero.Render",
      kind: "Managed",
      first_seen: "2026-08-01T00:00:00.000Z",
      last_seen: "2026-09-01T00:00:00.000Z",
      count: 67,
      installs: 19,
      versions_json: JSON.stringify({ "0.3.2": 40, "0.3.1": 27 }),
      status: "open",
      github_issue: 214,
    };
    const app = mapIssue(wire);
    expect(app.firstSeen).toBe(wire.first_seen);
    expect(app.lastSeen).toBe(wire.last_seen);
    expect(app.versions).toEqual({ "0.3.2": 40, "0.3.1": 27 });
    expect(app.githubIssue).toBe(214);
  });

  it("defaults to an empty object on malformed versions_json", () => {
    const app = mapIssue({
      fingerprint: "fp-2",
      title: "Hang",
      kind: "Hang",
      first_seen: "x",
      last_seen: "y",
      count: 1,
      installs: 1,
      versions_json: "{not json",
      status: "open",
      github_issue: null,
    });
    expect(app.versions).toEqual({});
  });
});

describe("mapStats", () => {
  it("passes through the already-camelCase wire stats shape", () => {
    const wire: WireStats = {
      reports: 128,
      openIssues: 20,
      installs: 40,
      hangs: 9,
      native: 3,
      perDay: [{ day: "2026-09-01", crash: 5, hang: 1, closed: 0 }],
      perKind: { Managed: 100, Hang: 9, Native: 3, ExitCode: 10, UncleanExit: 6 },
    };
    expect(mapStats(wire)).toEqual(wire);
  });
});

describe("mapOccurrence / mapThisInstall / mapVersionCount / mapSymbol", () => {
  it("maps occurrence booleans and snake_case", () => {
    const wire: WireOccurrenceRow = {
      id: "r1",
      received_at: "2026-09-01T00:00:00.000Z",
      arch: "x64",
      gpu: "NVIDIA GeForce RTX 3060",
      has_dump: 1,
      dump_bytes: 1024,
    };
    const app = mapOccurrence(wire);
    expect(app.receivedAt).toBe(wire.received_at);
    expect(app.hasDump).toBe(true);
  });

  it("maps this_install", () => {
    const wire: WireThisInstall = { reports_30d: 4, first_seen_quad: "0.3.0-stable" };
    expect(mapThisInstall(wire)).toEqual({ reports30d: 4, firstSeenQuad: "0.3.0-stable" });
  });

  it("maps version counts", () => {
    const wire: WireVersionCount = { semver: "0.3.2", quad: "0.3.2-stable", arch: "x64", kind: "Managed", count: 10 };
    expect(mapVersionCount(wire)).toEqual({ semver: "0.3.2", quad: "0.3.2-stable", arch: "x64", kind: "Managed", count: 10 });
  });

  it("maps symbol rows including nulls", () => {
    const wire: WireSymbolRow = { quad: "0.3.0-stable", arch: "arm64", debug_id: null, uploaded_at: null, entries: null };
    expect(mapSymbol(wire)).toEqual({ quad: "0.3.0-stable", arch: "arm64", debugId: null, uploadedAt: null, entries: null });
  });
});

describe("mapBreakdowns", () => {
  it("renames gpu_tier to gpuTier", () => {
    const wire: WireIssueBreakdowns = {
      version: [{ label: "0.3.2", count: 5 }],
      arch: [{ label: "x64", count: 5 }],
      gpu_tier: [{ label: "high", count: 5 }],
    };
    expect(mapBreakdowns(wire)).toEqual({
      version: [{ label: "0.3.2", count: 5 }],
      arch: [{ label: "x64", count: 5 }],
      gpuTier: [{ label: "high", count: 5 }],
    });
  });
});

describe("mapIssueDetail / mapReportDetail / mapReportsPage", () => {
  it("maps the full issue detail response", () => {
    const wire: WireIssueDetailResponse = {
      issue: {
        fingerprint: "fp-1",
        title: "t",
        kind: "Managed",
        first_seen: "a",
        last_seen: "b",
        count: 1,
        installs: 1,
        versions_json: "{}",
        status: "open",
        github_issue: null,
      },
      reports: [wireReport],
      frames: [{ rva: 1, offset: 0, name: "x" }],
      occurrences: [],
      sparkline14d: [{ day: "2026-09-01", count: 2 }],
      breakdowns: { version: [], arch: [], gpu_tier: [] },
    };
    const app = mapIssueDetail(wire);
    expect(app.reports).toHaveLength(1);
    expect(app.frames).toEqual([{ rva: 1, offset: 0, name: "x" }]);
    expect(app.breakdowns.gpuTier).toEqual([]);
  });

  it("maps report detail's this_install", () => {
    const wire: WireReportDetailResponse = {
      report: wireReport,
      this_install: { reports_30d: 2, first_seen_quad: "0.3.1-stable" },
    };
    const app = mapReportDetail(wire);
    expect(app.thisInstall).toEqual({ reports30d: 2, firstSeenQuad: "0.3.1-stable" });
  });

  it("maps the paged reports response, keeping nextCursor as-is", () => {
    const wire: WireReportsPageResponse = { reports: [wireReport], nextCursor: "abc" };
    const app = mapReportsPage(wire);
    expect(app.reports).toHaveLength(1);
    expect(app.nextCursor).toBe("abc");
  });
});
