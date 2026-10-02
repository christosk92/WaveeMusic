import { isNewerSemver, maxSemver } from "./semver.js";
import type {
  BreakdownEntry,
  DayBucket,
  Frame,
  IssueBreakdowns,
  IssueRow,
  IssueStatus,
  OccurrenceRow,
  ReportRow,
  ReportsPage,
  ReportsPageFilter,
  SparklineDay,
  StatsResult,
  Summary,
  SymbolRow,
  ThisInstall,
  VersionCount,
} from "./types.js";

export interface InsertReportInput {
  summary: Summary;
  framesJson: string;
  fingerprint: string;
  /** grouping.ts `FINGERPRINT_VERSION` the fingerprint was computed with. */
  fpVersion: number;
  receivedAt: string;
  /** Derived from the actual `dump` part — never `summary.hasDump`/`summary.dumpBytes`. */
  hasDump: boolean;
  dumpBytes: number;
}

/** One ingested report's effect on its issue (`recordIssueOccurrence`). */
export interface IssueOccurrence {
  fingerprint: string;
  /** Written only when the issue row is created. */
  title: string;
  kind: string;
  semver: string;
  receivedAt: string;
  /** No earlier stored report of this install under this fingerprint (`installSeenForIssue`, asked BEFORE the
   *  report's own insert). */
  newInstall: boolean;
  framesJson: string;
  fpVersion: number;
}

/** "new" = the issue row was created; "regressed" = a resolved issue reopened (a report from a newer semver than
 *  `resolved_version`); "existing" = counters bumped, nothing else. */
export type IssueTransition = "new" | "regressed" | "existing";

export interface IssueFilter {
  since?: string;
  version?: string;
  status?: string;
}

export interface IssuePatch {
  status?: IssueStatus;
  githubIssue?: number;
}

/** The R2/issue bookkeeping deleting a `reports` row needs — erasure (plan §J), DELETE report, retention. */
export interface ReportRef {
  id: string;
  quad: string;
  fingerprint: string;
}

/** D1 access, isolated behind an interface so tests can swap in an in-memory SQLite double
 *  (test/fixtures.ts `makeFakeD1`) instead of a real D1 binding — see test/index.test.ts. */
export interface Store {
  reportExists(id: string): Promise<boolean>;
  insertReport(input: InsertReportInput): Promise<void>;
  /** Whether `installId` already has a stored report under `fingerprint` — asked before inserting the new one. */
  installSeenForIssue(fingerprint: string, installId: string): Promise<boolean>;
  /** Creates the issue or bumps its lifetime counters; reopens a resolved one on a newer semver (see the impl). */
  recordIssueOccurrence(o: IssueOccurrence): Promise<IssueTransition>;
  getIssue(fingerprint: string): Promise<IssueRow | null>;
  listIssues(filter: IssueFilter): Promise<IssueRow[]>;
  listReportsForIssue(fingerprint: string, limit: number): Promise<ReportRow[]>;
  getReport(id: string): Promise<ReportRow | null>;
  getReportRef(id: string): Promise<ReportRef | null>;
  /** Deletes one `reports` row (its R2 objects are the caller's). */
  deleteReport(id: string): Promise<void>;
  listVersions(): Promise<VersionCount[]>;
  /** False when the issue doesn't exist. The transition to `resolved` records `resolved_at` + `resolved_version`. */
  patchIssue(fingerprint: string, patch: IssuePatch, nowIso: string): Promise<boolean>;
  touchSymbolsMeta(quad: string, arch: string, debugId: string, entries: number, seenAtUtc: string): Promise<void>;
  // ── Right to erasure (plan §J) ─────────────────────────────────────────────────────────────────
  isInstallDeleted(installId: string): Promise<boolean>;
  listReportRefsForInstall(installId: string): Promise<ReportRef[]>;
  deleteReportsForInstall(installId: string): Promise<void>;
  /** Recomputes count/installs/first_seen/last_seen/versions_json/last_frames_json for `fingerprint` from its
   *  remaining reports after some were deleted; deletes the `issues` row outright once none remain. */
  recomputeOrDeleteIssue(fingerprint: string): Promise<void>;
  tombstoneInstall(installId: string, deletedAtUtc: string): Promise<void>;
  // ── Retention (retention.ts) ───────────────────────────────────────────────────────────────────
  /** The oldest `limit` reports received before `cutoffIso`, ordered (received_at, id). */
  listExpiredReportRefs(cutoffIso: string, limit: number): Promise<ReportRef[]>;
  /** Deletes the same oldest `limit` expired rows `listExpiredReportRefs` lists; returns how many went. */
  deleteExpiredReports(cutoffIso: string, limit: number): Promise<number>;
  // ── Dashboard read API (crash-dashboard-implementation.md §4) ─────────────────────────────────
  getStats(sinceIso: string): Promise<StatsResult>;
  listReportsPage(filter: ReportsPageFilter): Promise<ReportsPage>;
  listSymbols(): Promise<SymbolRow[]>;
  /** `frames_json` of the most recently received report carrying this fingerprint, parsed. */
  getLatestFrames(fingerprint: string): Promise<Frame[] | null>;
  getOccurrences(fingerprint: string, limit: number): Promise<OccurrenceRow[]>;
  getSparkline(fingerprint: string, sinceIso: string): Promise<SparklineDay[]>;
  getBreakdowns(fingerprint: string): Promise<IssueBreakdowns>;
  getThisInstall(installId: string, sinceIso30d: string): Promise<ThisInstall>;
}

function b(v: boolean): number {
  return v ? 1 : 0;
}

/** The semvers an issue has seen — the keys of its `versions_json`. */
function versionKeys(versionsJson: string | null): string[] {
  try {
    const v: unknown = JSON.parse(versionsJson || "{}");
    return typeof v === "object" && v !== null && !Array.isArray(v) ? Object.keys(v) : [];
  } catch {
    return [];
  }
}

export class D1Store implements Store {
  constructor(private readonly db: D1Database) {}

  async reportExists(id: string): Promise<boolean> {
    const row = await this.db.prepare("SELECT 1 FROM reports WHERE id = ?1 LIMIT 1").bind(id).first();
    return row !== null;
  }

  async insertReport({
    summary: s,
    framesJson,
    fingerprint,
    fpVersion,
    receivedAt,
    hasDump,
    dumpBytes,
  }: InsertReportInput): Promise<void> {
    await this.db
      .prepare(
        `INSERT INTO reports(
          id, install_id, session_id, kind, quad, semver, commit_sha, channel, arch, os_build,
          gpu, gpu_tier, software_adapter, packaged, locale, uptime_ms, before_first_frame, last_route,
          exception_type, exception_message, exit_code, has_dump, dump_bytes, frames_json, fingerprint, received_at, debug_id,
          exception_code, fault_module, fault_offset, fp_version
        ) VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?13,?14,?15,?16,?17,?18,?19,?20,?21,?22,?23,?24,?25,?26,?27,
                  ?28,?29,?30,?31)`,
      )
      .bind(
        s.reportId,
        s.installId,
        s.sessionId,
        s.kind,
        s.quad,
        s.version,
        s.commit,
        s.channel,
        s.arch,
        s.osBuild,
        s.gpu,
        s.gpuTier,
        b(s.softwareAdapter),
        b(s.packaged),
        s.locale,
        s.uptimeMs,
        b(s.beforeFirstFrame),
        s.lastRoute,
        s.exceptionType,
        s.exceptionMessage,
        s.exitCode,
        b(hasDump),
        dumpBytes,
        framesJson,
        fingerprint,
        receivedAt,
        s.debugId,
        s.exceptionCode,
        s.faultModule,
        s.faultOffset,
        fpVersion,
      )
      .run();
  }

  async installSeenForIssue(fingerprint: string, installId: string): Promise<boolean> {
    const row = await this.db
      .prepare("SELECT 1 FROM reports WHERE fingerprint = ?1 AND install_id = ?2 LIMIT 1")
      .bind(fingerprint, installId)
      .first();
    return row !== null;
  }

  /** Lifetime counters, incremental (#165): an issue keeps its stats after its reports are purged, so they can no
   *  longer be recomputed from `reports` on every ingest. `title`/`kind`/`first_seen` are written once, on insert.
   *
   *  Regression rule (plan §A2): a `resolved` issue reopens only for a report from a semver NEWER than its
   *  `resolved_version`; `ignored` never reopens. The reopen is one conditional UPDATE, so of two concurrent reports
   *  exactly one sees `changes === 1` — one alert. */
  async recordIssueOccurrence(o: IssueOccurrence): Promise<IssueTransition> {
    const at = o.receivedAt;
    const ins = await this.db
      .prepare(
        `INSERT INTO issues(fingerprint, title, kind, first_seen, last_seen, count, installs, versions_json,
                            status, github_issue, last_frames_json, fp_version)
         VALUES (?1, ?2, ?3, ?4, ?5, 1, 1, ?6, 'open', NULL, ?7, ?8)
         ON CONFLICT(fingerprint) DO NOTHING`,
      )
      .bind(o.fingerprint, o.title, o.kind, at, at, JSON.stringify({ [o.semver]: 1 }), o.framesJson, o.fpVersion)
      .run();
    if (ins.meta.changes === 1) return "new";

    // `semver` passed validate.ts's [0-9A-Za-z.+-] check, so it is safe inside the quoted JSON path label. It is
    // bound twice (?4, ?5) because every statement here keeps its placeholders strictly increasing and unreused.
    await this.db
      .prepare(
        `UPDATE issues SET count = count + 1, installs = installs + ?1, last_seen = ?2, last_frames_json = ?3,
           versions_json = json_set(COALESCE(NULLIF(versions_json, ''), '{}'), '$."' || ?4 || '"',
             COALESCE(json_extract(NULLIF(versions_json, ''), '$."' || ?5 || '"'), 0) + 1)
         WHERE fingerprint = ?6`,
      )
      .bind(o.newInstall ? 1 : 0, at, o.framesJson, o.semver, o.semver, o.fingerprint)
      .run();

    const cur = await this.getIssue(o.fingerprint);
    if (cur?.status !== "resolved" || !isNewerSemver(o.semver, cur.resolved_version ?? "")) return "existing";
    const re = await this.db
      .prepare(
        `UPDATE issues SET status = 'open', regressed_at = ?1, regressions = regressions + 1
         WHERE fingerprint = ?2 AND status = 'resolved'`,
      )
      .bind(at, o.fingerprint)
      .run();
    return re.meta.changes === 1 ? "regressed" : "existing";
  }

  private async computeAggregates(fingerprint: string): Promise<{
    cnt: number;
    installs: number;
    first_seen: string;
    last_seen: string;
    versionsJson: string;
    lastFramesJson: string | null;
  } | null> {
    const agg = await this.db
      .prepare(
        `SELECT COUNT(*) AS cnt, COUNT(DISTINCT install_id) AS installs,
                MIN(received_at) AS first_seen, MAX(received_at) AS last_seen
         FROM reports WHERE fingerprint = ?1`,
      )
      .bind(fingerprint)
      .first<{ cnt: number; installs: number; first_seen: string; last_seen: string }>();
    if (!agg || agg.cnt === 0) return agg ? { ...agg, versionsJson: "{}", lastFramesJson: null } : null;

    const versionsResult = await this.db
      .prepare("SELECT semver, COUNT(*) AS cnt FROM reports WHERE fingerprint = ?1 GROUP BY semver")
      .bind(fingerprint)
      .all<{ semver: string; cnt: number }>();
    const versions: Record<string, number> = {};
    for (const row of versionsResult.results) versions[row.semver] = row.cnt;
    const latest = await this.db
      .prepare("SELECT frames_json FROM reports WHERE fingerprint = ?1 ORDER BY received_at DESC LIMIT 1")
      .bind(fingerprint)
      .first<{ frames_json: string }>();
    return { ...agg, versionsJson: JSON.stringify(versions), lastFramesJson: latest?.frames_json ?? null };
  }

  /** plan §J: called for every fingerprint an erasure or a single-report delete touched. Recompute semantics (owner
   *  decision 2026-10-02): the lifetime counters shrink to what the remaining reports — at most 90 days of them —
   *  say, and an issue whose last report just went is removed outright rather than left at count 0. Status, title
   *  and the resolve/regression fields are left alone. */
  async recomputeOrDeleteIssue(fingerprint: string): Promise<void> {
    const agg = await this.computeAggregates(fingerprint);
    if (!agg || agg.cnt === 0) {
      await this.db.prepare("DELETE FROM issues WHERE fingerprint = ?1").bind(fingerprint).run();
      return;
    }
    await this.db
      .prepare(
        `UPDATE issues SET count = ?1, installs = ?2, first_seen = ?3, last_seen = ?4, versions_json = ?5,
                           last_frames_json = ?6
         WHERE fingerprint = ?7`,
      )
      .bind(agg.cnt, agg.installs, agg.first_seen, agg.last_seen, agg.versionsJson, agg.lastFramesJson, fingerprint)
      .run();
  }

  async getIssue(fingerprint: string): Promise<IssueRow | null> {
    return this.db.prepare("SELECT * FROM issues WHERE fingerprint = ?1").bind(fingerprint).first<IssueRow>();
  }

  async listIssues(filter: IssueFilter): Promise<IssueRow[]> {
    const clauses: string[] = [];
    const args: unknown[] = [];
    if (filter.since) {
      args.push(filter.since);
      clauses.push(`last_seen >= ?${args.length}`);
    }
    if (filter.status) {
      args.push(filter.status);
      clauses.push(`status = ?${args.length}`);
    }
    if (filter.version) {
      args.push(`%"${filter.version}"%`);
      clauses.push(`versions_json LIKE ?${args.length}`);
    }
    const where = clauses.length > 0 ? `WHERE ${clauses.join(" AND ")}` : "";
    const stmt = this.db.prepare(`SELECT * FROM issues ${where} ORDER BY last_seen DESC`);
    const bound = args.length > 0 ? stmt.bind(...args) : stmt;
    const result = await bound.all<IssueRow>();
    return result.results;
  }

  async listReportsForIssue(fingerprint: string, limit: number): Promise<ReportRow[]> {
    const result = await this.db
      .prepare("SELECT * FROM reports WHERE fingerprint = ?1 ORDER BY received_at DESC LIMIT ?2")
      .bind(fingerprint, limit)
      .all<ReportRow>();
    return result.results;
  }

  async getReport(id: string): Promise<ReportRow | null> {
    return this.db.prepare("SELECT * FROM reports WHERE id = ?1").bind(id).first<ReportRow>();
  }

  async getReportRef(id: string): Promise<ReportRef | null> {
    return this.db.prepare("SELECT id, quad, fingerprint FROM reports WHERE id = ?1").bind(id).first<ReportRef>();
  }

  async deleteReport(id: string): Promise<void> {
    await this.db.prepare("DELETE FROM reports WHERE id = ?1").bind(id).run();
  }

  async listVersions(): Promise<VersionCount[]> {
    const result = await this.db
      .prepare(
        `SELECT semver, quad, arch, kind, COUNT(*) AS count
         FROM reports GROUP BY semver, quad, arch, kind
         ORDER BY semver DESC, quad, arch, kind`,
      )
      .all<VersionCount>();
    return result.results;
  }

  /** The transition to `resolved` records `resolved_at` and `resolved_version` = the newest semver in
   *  `versions_json` — the regression baseline (plan §A2). Re-resolving an already resolved issue keeps both;
   *  reopening by hand (`open`, `ignored`) clears nothing. */
  async patchIssue(fingerprint: string, patch: IssuePatch, nowIso: string): Promise<boolean> {
    const existing = await this.getIssue(fingerprint);
    if (!existing) return false;
    const status = patch.status ?? existing.status;
    const githubIssue = patch.githubIssue ?? existing.github_issue;
    if (status === "resolved" && existing.status !== "resolved") {
      await this.db
        .prepare(
          `UPDATE issues SET status = ?1, github_issue = ?2, resolved_at = ?3, resolved_version = ?4
           WHERE fingerprint = ?5`,
        )
        .bind(status, githubIssue, nowIso, maxSemver(versionKeys(existing.versions_json)), fingerprint)
        .run();
      return true;
    }
    await this.db
      .prepare("UPDATE issues SET status = ?1, github_issue = ?2 WHERE fingerprint = ?3")
      .bind(status, githubIssue, fingerprint)
      .run();
    return true;
  }

  async touchSymbolsMeta(quad: string, arch: string, debugId: string, entries: number, seenAtUtc: string): Promise<void> {
    await this.db
      .prepare(
        `INSERT INTO symbols(quad, arch, debug_id, uploaded_at, entries) VALUES (?1, ?2, ?3, ?4, ?5)
         ON CONFLICT(quad, arch) DO UPDATE SET debug_id = excluded.debug_id, uploaded_at = excluded.uploaded_at, entries = excluded.entries`,
      )
      .bind(quad, arch, debugId, seenAtUtc, entries)
      .run();
  }

  // ── Right to erasure (plan §J) ───────────────────────────────────────────────────────────────────

  async isInstallDeleted(installId: string): Promise<boolean> {
    const row = await this.db
      .prepare("SELECT 1 FROM deleted_installs WHERE install_id = ?1 LIMIT 1")
      .bind(installId)
      .first();
    return row !== null;
  }

  async listReportRefsForInstall(installId: string): Promise<ReportRef[]> {
    const result = await this.db
      .prepare("SELECT id, quad, fingerprint FROM reports WHERE install_id = ?1")
      .bind(installId)
      .all<ReportRef>();
    return result.results;
  }

  async deleteReportsForInstall(installId: string): Promise<void> {
    await this.db.prepare("DELETE FROM reports WHERE install_id = ?1").bind(installId).run();
  }

  async tombstoneInstall(installId: string, deletedAtUtc: string): Promise<void> {
    await this.db
      .prepare(
        `INSERT INTO deleted_installs(install_id, deleted_at) VALUES (?1, ?2)
         ON CONFLICT(install_id) DO UPDATE SET deleted_at = excluded.deleted_at`,
      )
      .bind(installId, deletedAtUtc)
      .run();
  }

  // ── Retention (retention.ts) ─────────────────────────────────────────────────────────────────────

  async listExpiredReportRefs(cutoffIso: string, limit: number): Promise<ReportRef[]> {
    const result = await this.db
      .prepare("SELECT id, quad, fingerprint FROM reports WHERE received_at < ?1 ORDER BY received_at, id LIMIT ?2")
      .bind(cutoffIso, limit)
      .all<ReportRef>();
    return result.results;
  }

  /** A subquery rather than `id IN (?…)` over the listed ids: D1 binds at most 100 parameters per statement. */
  async deleteExpiredReports(cutoffIso: string, limit: number): Promise<number> {
    const r = await this.db
      .prepare(
        `DELETE FROM reports WHERE id IN (
           SELECT id FROM reports WHERE received_at < ?1 ORDER BY received_at, id LIMIT ?2)`,
      )
      .bind(cutoffIso, limit)
      .run();
    return r.meta.changes;
  }

  // ── Dashboard read API (crash-dashboard-implementation.md §4) ─────────────────────────────────────

  async getStats(sinceIso: string): Promise<StatsResult> {
    const totals = await this.db
      .prepare(
        `SELECT COUNT(*) AS reports, COUNT(DISTINCT install_id) AS installs,
                SUM(CASE WHEN kind = 'Hang' THEN 1 ELSE 0 END) AS hangs,
                SUM(CASE WHEN kind = 'Native' THEN 1 ELSE 0 END) AS native
         FROM reports WHERE received_at >= ?1`,
      )
      .bind(sinceIso)
      .first<{ reports: number; installs: number; hangs: number; native: number }>();

    const openIssuesRow = await this.db
      .prepare("SELECT COUNT(*) AS cnt FROM issues WHERE status = 'open' AND last_seen >= ?1")
      .bind(sinceIso)
      .first<{ cnt: number }>();

    const perDayResult = await this.db
      .prepare(
        `SELECT substr(received_at, 1, 10) AS day,
                SUM(CASE WHEN kind IN ('Managed', 'Native', 'ExitCode') THEN 1 ELSE 0 END) AS crash,
                SUM(CASE WHEN kind = 'Hang' THEN 1 ELSE 0 END) AS hang,
                SUM(CASE WHEN kind = 'UncleanExit' THEN 1 ELSE 0 END) AS closed
         FROM reports WHERE received_at >= ?1
         GROUP BY day ORDER BY day ASC`,
      )
      .bind(sinceIso)
      .all<DayBucket>();

    const perKindResult = await this.db
      .prepare("SELECT kind, COUNT(*) AS cnt FROM reports WHERE received_at >= ?1 GROUP BY kind")
      .bind(sinceIso)
      .all<{ kind: string; cnt: number }>();
    const perKind: Record<string, number> = {};
    for (const row of perKindResult.results) perKind[row.kind] = row.cnt;

    return {
      reports: totals?.reports ?? 0,
      installs: totals?.installs ?? 0,
      hangs: totals?.hangs ?? 0,
      native: totals?.native ?? 0,
      openIssues: openIssuesRow?.cnt ?? 0,
      perDay: perDayResult.results,
      perKind,
    };
  }

  async listReportsPage(filter: ReportsPageFilter): Promise<ReportsPage> {
    const clauses: string[] = [];
    const args: unknown[] = [];

    if (filter.since) {
      args.push(filter.since);
      clauses.push(`received_at >= ?${args.length}`);
    }
    if (filter.kind) {
      args.push(filter.kind);
      clauses.push(`kind = ?${args.length}`);
    }
    if (filter.quad) {
      args.push(filter.quad);
      clauses.push(`quad = ?${args.length}`);
    }
    if (filter.q) {
      args.push(`${filter.q}%`);
      const idIdx = args.length;
      args.push(`${filter.q}%`);
      const installIdx = args.length;
      clauses.push(`(id LIKE ?${idIdx} OR install_id LIKE ?${installIdx})`);
    }
    if (filter.cursor) {
      const [cReceivedAt, cId] = decodeReportsCursor(filter.cursor);
      args.push(cReceivedAt);
      const ltIdx = args.length;
      args.push(cReceivedAt);
      const eqIdx = args.length;
      args.push(cId);
      const idLtIdx = args.length;
      clauses.push(`(received_at < ?${ltIdx} OR (received_at = ?${eqIdx} AND id < ?${idLtIdx}))`);
    }

    const where = clauses.length > 0 ? `WHERE ${clauses.join(" AND ")}` : "";
    args.push(filter.limit + 1); // fetch one extra row to know whether a next page exists
    const limitIdx = args.length;

    const stmt = this.db.prepare(
      `SELECT * FROM reports ${where} ORDER BY received_at DESC, id DESC LIMIT ?${limitIdx}`,
    );
    const result = await stmt.bind(...args).all<ReportRow>();
    const rows = result.results;

    let nextCursor: string | null = null;
    if (rows.length > filter.limit) {
      rows.length = filter.limit;
      const last = rows[rows.length - 1]!;
      nextCursor = encodeReportsCursor(last.received_at, last.id);
    }
    return { rows, nextCursor };
  }

  async listSymbols(): Promise<SymbolRow[]> {
    const result = await this.db.prepare("SELECT * FROM symbols ORDER BY quad DESC, arch").all<SymbolRow>();
    return result.results;
  }

  async getLatestFrames(fingerprint: string): Promise<Frame[] | null> {
    const row = await this.db
      .prepare("SELECT frames_json FROM reports WHERE fingerprint = ?1 ORDER BY received_at DESC LIMIT 1")
      .bind(fingerprint)
      .first<{ frames_json: string }>();
    if (!row) return null;
    return parseFramesJson(row.frames_json);
  }

  async getOccurrences(fingerprint: string, limit: number): Promise<OccurrenceRow[]> {
    const result = await this.db
      .prepare(
        `SELECT id, received_at, arch, gpu, has_dump, dump_bytes FROM reports
         WHERE fingerprint = ?1 ORDER BY received_at DESC LIMIT ?2`,
      )
      .bind(fingerprint, limit)
      .all<OccurrenceRow>();
    return result.results;
  }

  async getSparkline(fingerprint: string, sinceIso: string): Promise<SparklineDay[]> {
    const result = await this.db
      .prepare(
        `SELECT substr(received_at, 1, 10) AS day, COUNT(*) AS count FROM reports
         WHERE fingerprint = ?1 AND received_at >= ?2 GROUP BY day ORDER BY day ASC`,
      )
      .bind(fingerprint, sinceIso)
      .all<SparklineDay>();
    return result.results;
  }

  private async groupCount(column: "semver" | "arch" | "gpu_tier", fingerprint: string): Promise<BreakdownEntry[]> {
    // `column` is one of a fixed 3-value internal whitelist, never request input, so string-building the
    // column name here is safe (D1/SQLite has no parameter placeholder for identifiers).
    const result = await this.db
      .prepare(`SELECT ${column} AS label, COUNT(*) AS count FROM reports WHERE fingerprint = ?1 GROUP BY ${column} ORDER BY count DESC`)
      .bind(fingerprint)
      .all<BreakdownEntry>();
    return result.results;
  }

  async getBreakdowns(fingerprint: string): Promise<IssueBreakdowns> {
    const [version, arch, gpuTier] = await Promise.all([
      this.groupCount("semver", fingerprint),
      this.groupCount("arch", fingerprint),
      this.groupCount("gpu_tier", fingerprint),
    ]);
    return { version, arch, gpu_tier: gpuTier };
  }

  async getThisInstall(installId: string, sinceIso30d: string): Promise<ThisInstall> {
    const countRow = await this.db
      .prepare("SELECT COUNT(*) AS cnt FROM reports WHERE install_id = ?1 AND received_at >= ?2")
      .bind(installId, sinceIso30d)
      .first<{ cnt: number }>();
    const firstRow = await this.db
      .prepare("SELECT quad FROM reports WHERE install_id = ?1 ORDER BY received_at ASC LIMIT 1")
      .bind(installId)
      .first<{ quad: string }>();
    return { reports_30d: countRow?.cnt ?? 0, first_seen_quad: firstRow?.quad ?? null };
  }
}

/** A stored `frames_json` (a report's, or an issue's `last_frames_json`), parsed; null when absent or malformed. */
export function parseFramesJson(json: string | null | undefined): Frame[] | null {
  if (!json) return null;
  try {
    const v: unknown = JSON.parse(json);
    return Array.isArray(v) ? (v as Frame[]) : null;
  } catch {
    return null;
  }
}

/** Opaque keyset-pagination cursor for `GET /v1/reports` — base64 of `received_at|id`, ordered the same
 *  way the page query orders (`received_at DESC, id DESC`). */
function encodeReportsCursor(receivedAt: string, id: string): string {
  return btoa(`${receivedAt}|${id}`);
}

function decodeReportsCursor(cursor: string): [string, string] {
  const decoded = atob(cursor);
  const sep = decoded.indexOf("|");
  if (sep < 0) return [decoded, ""];
  return [decoded.slice(0, sep), decoded.slice(sep + 1)];
}
