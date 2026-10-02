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

/** `issues` row. The fields after `github_issue` came with the Worker's retention/alerts/grouping-v2
 *  migration; they are optional here so a row from before it (or a fixture without them) still maps. */
export interface WireIssueRow {
  fingerprint: string;
  title: string;
  kind: string;
  first_seen: string;
  last_seen: string;
  count: number;
  installs: number;
  versions_json: string;
  /** `open` | `resolved` | `ignored` — a regression reopens to `open` and sets `regressed_at`. */
  status: string;
  github_issue: number | null;
  regressed_at?: string | null;
  regressions?: number;
  resolved_at?: string | null;
  /** The newest semver seen when the issue was resolved; only a report from a newer one reopens it. */
  resolved_version?: string | null;
  /** The newest report's frames, kept on the issue so the stack outlives the 90-day report purge. */
  last_frames_json?: string | null;
  fp_version?: number;
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
  /** Server-derived from the uploaded dump part, never the client's own claim. */
  has_dump: number;
  dump_bytes: number;
  frames_json: string;
  fingerprint: string;
  received_at: string;
  debug_id: string | null;
  // Native fault facts + grouping version (optional: rows from before the migration lack them).
  /** NTSTATUS as an unsigned 32-bit integer (0xC0000005 = 3221225477); 0 when not a native fault. */
  exception_code?: number;
  /** Faulting module's lower-case base name (`nvwgf2umx.dll`); "" when unknown. */
  fault_module?: string;
  fault_offset?: number;
  fp_version?: number;
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

// ── DELETE /v1/reports/:id, POST /v1/retention/run ──────────────────────────────────────────────────

export interface WireDeleteReportResponse {
  deleted: number;
  /** The issue the report belonged to — recomputed (or deleted, when this was its last report). */
  fingerprint: string;
}

export interface WireRetentionRunResponse {
  deleted: number;
  batches: number;
  /** The run hit its batch cap with expired reports left; the next run (or the daily cron) continues. */
  more: boolean;
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
  /** When a report from a newer semver than `resolvedVersion` reopened it (status is `open` again). */
  regressedAt: string | null;
  regressions: number;
  resolvedAt: string | null;
  /** "Resolved in ≤ this version"; null when never resolved or resolved before any version was recorded. */
  resolvedVersion: string | null;
  /** The stack kept on the issue itself — what the Stack tab shows once every report has been purged. */
  lastFrames: AppFrame[] | null;
  fpVersion: number;
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
  exceptionCode: number;
  faultModule: string;
  faultOffset: number;
  fpVersion: number;
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

export interface AppDeleteReportResult {
  deleted: number;
  fingerprint: string;
}

export interface AppRetentionResult {
  deleted: number;
  batches: number;
  more: boolean;
}

/** The plain-text parts `GET /v1/reports/:id/:part` serves that the dashboard renders. */
export type ReportPart = "report" | "tail";

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
