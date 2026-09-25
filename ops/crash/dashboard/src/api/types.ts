// Wire shapes exactly as `ops/crash/worker` returns them (snake_case D1 rows verbatim; see
// worker/src/types.ts + worker/src/store.ts + worker/src/index.ts). App* types below are the
// camelCase shapes the dashboard's components consume; `mapping.ts` converts one to the other.

export const KINDS = ["Managed", "Native", "Hang", "ExitCode", "UncleanExit"] as const;
export type Kind = (typeof KINDS)[number];

export interface WireFrame {
  rva: number;
  offset: number;
  name: string | null;
}

export interface WireDayBucket {
  day: string; // "yyyy-MM-dd"
  crash: number;
  hang: number;
  closed: number;
}

export interface WireStats {
  reports: number;
  openIssues: number;
  installs: number;
  hangs: number;
  native: number;
  perDay: WireDayBucket[];
  perKind: Record<string, number>;
}

export interface WireIssueRow {
  fingerprint: string;
  title: string;
  kind: string;
  first_seen: string;
  last_seen: string;
  count: number;
  installs: number;
  versions_json: string;
  status: string;
  github_issue: number | null;
}

export interface WireReportRow {
  id: string;
  install_id: string;
  session_id: string;
  kind: string;
  quad: string;
  semver: string;
  commit_sha: string;
  channel: string;
  arch: string;
  os_build: string;
  gpu: string;
  gpu_tier: string;
  software_adapter: number;
  packaged: number;
  locale: string;
  uptime_ms: number;
  before_first_frame: number;
  last_route: string;
  exception_type: string;
  exception_message: string;
  exit_code: number;
  has_dump: number;
  dump_bytes: number;
  frames_json: string;
  fingerprint: string;
  received_at: string;
  debug_id: string | null;
}

export interface WireOccurrenceRow {
  id: string;
  received_at: string;
  arch: string;
  gpu: string;
  has_dump: number;
  dump_bytes: number;
}

export interface WireSparklineDay {
  day: string;
  count: number;
}

export interface WireBreakdownEntry {
  label: string;
  count: number;
}

export interface WireIssueBreakdowns {
  version: WireBreakdownEntry[];
  arch: WireBreakdownEntry[];
  gpu_tier: WireBreakdownEntry[];
}

export interface WireVersionCount {
  semver: string;
  quad: string;
  arch: string;
  kind: string;
  count: number;
}

export interface WireSymbolRow {
  quad: string;
  arch: string;
  debug_id: string | null;
  uploaded_at: string | null;
  entries: number | null;
}

export interface WireThisInstall {
  reports_30d: number;
  first_seen_quad: string | null;
}

// ── /v1/issues, /v1/issues/:fp ──────────────────────────────────────────────────────────────────────

export interface WireIssuesListResponse {
  issues: WireIssueRow[];
}

export interface WireIssueDetailResponse {
  issue: WireIssueRow;
  reports: WireReportRow[];
  frames: WireFrame[] | null;
  occurrences: WireOccurrenceRow[];
  sparkline14d: WireSparklineDay[];
  breakdowns: WireIssueBreakdowns;
}

// ── /v1/reports, /v1/reports/:id ────────────────────────────────────────────────────────────────────

export interface WireReportsPageResponse {
  reports: WireReportRow[];
  nextCursor: string | null;
}

export interface WireReportDetailResponse {
  report: WireReportRow;
  this_install: WireThisInstall;
}

export interface WireVersionsResponse {
  versions: WireVersionCount[];
}

export interface WireSymbolsResponse {
  symbols: WireSymbolRow[];
}

// ── App (camelCase) shapes used inside the dashboard's components ─────────────────────────────────────

export interface AppFrame {
  rva: number;
  offset: number;
  name: string | null;
}

export interface AppDayBucket {
  day: string;
  crash: number;
  hang: number;
  closed: number;
}

export interface AppStats {
  reports: number;
  openIssues: number;
  installs: number;
  hangs: number;
  native: number;
  perDay: AppDayBucket[];
  perKind: Record<string, number>;
}

export interface AppIssue {
  fingerprint: string;
  title: string;
  kind: string;
  firstSeen: string;
  lastSeen: string;
  count: number;
  installs: number;
  versions: Record<string, number>;
  status: string;
  githubIssue: number | null;
}

export interface AppReport {
  id: string;
  installId: string;
  sessionId: string;
  kind: string;
  quad: string;
  semver: string;
  commitSha: string;
  channel: string;
  arch: string;
  osBuild: string;
  gpu: string;
  gpuTier: string;
  softwareAdapter: boolean;
  packaged: boolean;
  locale: string;
  uptimeMs: number;
  beforeFirstFrame: boolean;
  lastRoute: string;
  exceptionType: string;
  exceptionMessage: string;
  exitCode: number;
  hasDump: boolean;
  dumpBytes: number;
  frames: AppFrame[];
  fingerprint: string;
  receivedAt: string;
  debugId: string | null;
}

export interface AppOccurrence {
  id: string;
  receivedAt: string;
  arch: string;
  gpu: string;
  hasDump: boolean;
  dumpBytes: number;
}

export interface AppSparklineDay {
  day: string;
  count: number;
}

export interface AppBreakdownEntry {
  label: string;
  count: number;
}

export interface AppIssueBreakdowns {
  version: AppBreakdownEntry[];
  arch: AppBreakdownEntry[];
  gpuTier: AppBreakdownEntry[];
}

export interface AppIssueDetail {
  issue: AppIssue;
  reports: AppReport[];
  frames: AppFrame[] | null;
  occurrences: AppOccurrence[];
  sparkline14d: AppSparklineDay[];
  breakdowns: AppIssueBreakdowns;
}

export interface AppVersionCount {
  semver: string;
  quad: string;
  arch: string;
  kind: string;
  count: number;
}

export interface AppSymbol {
  quad: string;
  arch: string;
  debugId: string | null;
  uploadedAt: string | null;
  entries: number | null;
}

export interface AppThisInstall {
  reports30d: number;
  firstSeenQuad: string | null;
}

export interface AppReportDetail {
  report: AppReport;
  thisInstall: AppThisInstall;
}

export interface AppReportsPage {
  reports: AppReport[];
  nextCursor: string | null;
}

// ── Filters (query params the hooks build) ─────────────────────────────────────────────────────────

export interface IssuesFilter {
  since?: string;
  version?: string;
  status?: string;
  [key: string]: string | number | undefined;
}

export interface ReportsFilter {
  since?: string;
  kind?: string;
  quad?: string;
  q?: string;
  limit?: number;
  cursor?: string;
  [key: string]: string | number | undefined;
}

export interface IssuePatch {
  status?: string;
  githubIssue?: number;
}
