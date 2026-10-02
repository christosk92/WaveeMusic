// Shapes shared with the app (WP-C, `Platform/Crash.Upload.cs`) and the release tool (WP-G,
// `Wavee.ReleaseTool symbol-map`). See docs/plans/wavee/crash-diagnostics-implementation.md §I.
//
// `Summary` is the camelCase JSON wire shape of the C# record `Crash.Summary` (Platform/Crash.cs).
// The C# `Kind` enum must serialize as one of these five strings (a `JsonStringEnumConverter`, or
// equivalent, on the source-generated `SummaryJson` context) — a numeric enum would break both
// validation here and the fingerprint, which folds `kind` into its input string.

export const KINDS = ["Managed", "Native", "Hang", "ExitCode", "UncleanExit"] as const;
export type Kind = (typeof KINDS)[number];

export interface Summary {
  reportId: string;
  installId: string;
  kind: Kind;
  stampUtc: string;
  version: string;
  quad: string;
  commit: string;
  channel: string;
  arch: string;
  osBuild: string;
  gpu: string;
  gpuTier: string;
  softwareAdapter: boolean;
  packaged: boolean;
  locale: string;
  sessionId: string;
  uptimeMs: number;
  beforeFirstFrame: boolean;
  lastRoute: string;
  exceptionType: string;
  exceptionMessage: string;
  rvas: number[];
  moduleBase: number;
  moduleSize: number;
  debugId: string;
  exitCode: number;
  /** The client's claim only — the Worker derives `has_dump`/`dump_bytes` from the actual `dump` part. */
  hasDump: boolean;
  dumpBytes: number;
  /** NTSTATUS of a Native fault (uint32); 0 for every other kind or an older client. */
  exceptionCode: number;
  /** Faulting module's base name, lower-case `[a-z0-9._-]` (validate.ts sanitizes); "" when unknown. */
  faultModule: string;
  /** Fault address minus the faulting module's base; 0 when unknown. */
  faultOffset: number;
}

/** One resolved (or unresolved) stack frame, as written into `reports.frames_json`. */
export interface Frame {
  rva: number;
  /** Byte offset of `rva` past the resolved symbol's start; 0 when `name` is null. */
  offset: number;
  /** Null when no `.symmap` was found for this quad/arch, or `rva` precedes the first entry. */
  name: string | null;
}

export interface Env {
  DB: D1Database;
  BUCKET: R2Bucket;
  RATE: RateLimiter;
  INGEST_KEY: string;
  /** `https://<team>.cloudflareaccess.com` — wrangler.toml [vars]; the placeholder fails closed (`access.ts`). */
  ACCESS_TEAM_DOMAIN: string;
  /** The "Wavee crashes" Access application's AUD tag — wrangler.toml [vars]; the placeholder fails closed. */
  ACCESS_AUD: string;
  /** Discord webhook for new-issue/regression alerts — a secret only (`wrangler secret put`); unset → no alerts. */
  DISCORD_WEBHOOK_URL?: string;
}

/** The subset of the Cloudflare Rate Limiting binding this worker uses. */
export interface RateLimiter {
  limit(options: { key: string }): Promise<{ success: boolean }>;
}

export const ISSUE_STATUSES = ["open", "resolved", "ignored"] as const;
export type IssueStatus = (typeof ISSUE_STATUSES)[number];

export interface IssueRow {
  fingerprint: string;
  title: string;
  kind: string;
  first_seen: string;
  last_seen: string;
  /** Lifetime counters (incremental since migration 0001) — they survive the 90-day report purge. */
  count: number;
  installs: number;
  /** `{ "<semver>": count }`. */
  versions_json: string;
  status: string;
  github_issue: number | null;
  // ── migrations/0001 ───────────────────────────────────────────────────────────────────────────────
  /** Last time a report from a newer semver than `resolved_version` reopened this issue. */
  regressed_at: string | null;
  regressions: number;
  resolved_at: string | null;
  /** Newest semver in `versions_json` when the issue was resolved; only a newer one reopens it. */
  resolved_version: string | null;
  /** The newest report's `frames_json` — the stack outlives the purge of the reports themselves. */
  last_frames_json: string | null;
  /** Grouping version the fingerprint was computed with (2 = src/grouping.ts). */
  fp_version: number;
}

export interface ReportRow {
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
  // ── migrations/0001 ───────────────────────────────────────────────────────────────────────────────
  exception_code: number;
  fault_module: string;
  fault_offset: number;
  fp_version: number;
}

export interface VersionCount {
  semver: string;
  quad: string;
  arch: string;
  kind: string;
  count: number;
}

// ── Dashboard read API (crash-dashboard-implementation.md §4) ──────────────────────────────────────

export interface DayBucket {
  day: string; // "yyyy-MM-dd"
  crash: number; // kind in (Managed, Native, ExitCode)
  hang: number; // kind = Hang
  closed: number; // kind = UncleanExit
}

export interface StatsResult {
  reports: number;
  openIssues: number;
  installs: number;
  hangs: number;
  native: number;
  perDay: DayBucket[];
  perKind: Record<string, number>;
}

export interface OccurrenceRow {
  id: string;
  received_at: string;
  arch: string;
  gpu: string;
  has_dump: number;
  dump_bytes: number;
}

export interface SparklineDay {
  day: string;
  count: number;
}

export interface BreakdownEntry {
  label: string;
  count: number;
}

export interface IssueBreakdowns {
  version: BreakdownEntry[];
  arch: BreakdownEntry[];
  gpu_tier: BreakdownEntry[];
}

export interface SymbolRow {
  quad: string;
  arch: string;
  debug_id: string | null;
  uploaded_at: string | null;
  entries: number | null;
}

export interface ThisInstall {
  reports_30d: number;
  first_seen_quad: string | null;
}

export interface ReportsPageFilter {
  since?: string;
  kind?: string;
  quad?: string;
  q?: string;
  limit: number;
  cursor?: string;
}

export interface ReportsPage {
  rows: ReportRow[];
  nextCursor: string | null;
}
