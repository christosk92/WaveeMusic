import type {
  AppBreakdownEntry,
  AppFrame,
  AppIssue,
  AppIssueBreakdowns,
  AppIssueDetail,
  AppOccurrence,
  AppReport,
  AppReportDetail,
  AppReportsPage,
  AppSparklineDay,
  AppStats,
  AppSymbol,
  AppThisInstall,
  AppVersionCount,
  WireBreakdownEntry,
  WireFrame,
  WireIssueBreakdowns,
  WireIssueDetailResponse,
  WireIssueRow,
  WireOccurrenceRow,
  WireReportDetailResponse,
  WireReportRow,
  WireReportsPageResponse,
  WireSparklineDay,
  WireStats,
  WireSymbolRow,
  WireThisInstall,
  WireVersionCount,
} from "./types";

function bool(n: number): boolean {
  return n !== 0;
}

function parseVersionsJson(json: string): Record<string, number> {
  try {
    const parsed = JSON.parse(json) as unknown;
    if (parsed && typeof parsed === "object") return parsed as Record<string, number>;
    return {};
  } catch {
    return {};
  }
}

export function mapFrame(w: WireFrame): AppFrame {
  return { rva: w.rva, offset: w.offset, name: w.name };
}

export function mapStats(w: WireStats): AppStats {
  return {
    reports: w.reports,
    openIssues: w.openIssues,
    installs: w.installs,
    hangs: w.hangs,
    native: w.native,
    perDay: w.perDay.map((d) => ({ day: d.day, crash: d.crash, hang: d.hang, closed: d.closed })),
    perKind: w.perKind,
  };
}

export function mapIssue(w: WireIssueRow): AppIssue {
  return {
    fingerprint: w.fingerprint,
    title: w.title,
    kind: w.kind,
    firstSeen: w.first_seen,
    lastSeen: w.last_seen,
    count: w.count,
    installs: w.installs,
    versions: parseVersionsJson(w.versions_json),
    status: w.status,
    githubIssue: w.github_issue,
  };
}

export function mapReport(w: WireReportRow): AppReport {
  let frames: AppFrame[] = [];
  try {
    const parsed = JSON.parse(w.frames_json) as WireFrame[];
    frames = Array.isArray(parsed) ? parsed.map(mapFrame) : [];
  } catch {
    frames = [];
  }
  return {
    id: w.id,
    installId: w.install_id,
    sessionId: w.session_id,
    kind: w.kind,
    quad: w.quad,
    semver: w.semver,
    commitSha: w.commit_sha,
    channel: w.channel,
    arch: w.arch,
    osBuild: w.os_build,
    gpu: w.gpu,
    gpuTier: w.gpu_tier,
    softwareAdapter: bool(w.software_adapter),
    packaged: bool(w.packaged),
    locale: w.locale,
    uptimeMs: w.uptime_ms,
    beforeFirstFrame: bool(w.before_first_frame),
    lastRoute: w.last_route,
    exceptionType: w.exception_type,
    exceptionMessage: w.exception_message,
    exitCode: w.exit_code,
    hasDump: bool(w.has_dump),
    dumpBytes: w.dump_bytes,
    frames,
    fingerprint: w.fingerprint,
    receivedAt: w.received_at,
    debugId: w.debug_id,
  };
}

export function mapOccurrence(w: WireOccurrenceRow): AppOccurrence {
  return {
    id: w.id,
    receivedAt: w.received_at,
    arch: w.arch,
    gpu: w.gpu,
    hasDump: bool(w.has_dump),
    dumpBytes: w.dump_bytes,
  };
}

export function mapSparklineDay(w: WireSparklineDay): AppSparklineDay {
  return { day: w.day, count: w.count };
}

function mapBreakdownEntry(w: WireBreakdownEntry): AppBreakdownEntry {
  return { label: w.label, count: w.count };
}

export function mapBreakdowns(w: WireIssueBreakdowns): AppIssueBreakdowns {
  return {
    version: w.version.map(mapBreakdownEntry),
    arch: w.arch.map(mapBreakdownEntry),
    gpuTier: w.gpu_tier.map(mapBreakdownEntry),
  };
}

export function mapIssueDetail(w: WireIssueDetailResponse): AppIssueDetail {
  return {
    issue: mapIssue(w.issue),
    reports: w.reports.map(mapReport),
    frames: w.frames ? w.frames.map(mapFrame) : null,
    occurrences: w.occurrences.map(mapOccurrence),
    sparkline14d: w.sparkline14d.map(mapSparklineDay),
    breakdowns: mapBreakdowns(w.breakdowns),
  };
}

export function mapVersionCount(w: WireVersionCount): AppVersionCount {
  return { semver: w.semver, quad: w.quad, arch: w.arch, kind: w.kind, count: w.count };
}

export function mapSymbol(w: WireSymbolRow): AppSymbol {
  return { quad: w.quad, arch: w.arch, debugId: w.debug_id, uploadedAt: w.uploaded_at, entries: w.entries };
}

export function mapThisInstall(w: WireThisInstall): AppThisInstall {
  return { reports30d: w.reports_30d, firstSeenQuad: w.first_seen_quad };
}

export function mapReportDetail(w: WireReportDetailResponse): AppReportDetail {
  return { report: mapReport(w.report), thisInstall: mapThisInstall(w.this_install) };
}

export function mapReportsPage(w: WireReportsPageResponse): AppReportsPage {
  return { reports: w.reports.map(mapReport), nextCursor: w.nextCursor };
}
