import type {
  BreakdownEntry,
  DayBucket,
  Frame,
  IssueBreakdowns,
  IssueRow,
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
  receivedAt: string;
}

export interface IssueFilter {
  since?: string;
  version?: string;
  status?: string;
}

export interface IssuePatch {
  status?: string;
  githubIssue?: number;
}

/** The R2/issue bookkeeping a `reports` row needs erasing — plan §J. */
export interface InstallReportRef {
  id: string;
  quad: string;
  fingerprint: string;
}

/** D1 access, isolated behind an interface so tests can swap in an in-memory SQLite double
 *  (test/fixtures.ts `makeFakeD1`) instead of a real D1 binding — see test/index.test.ts. */
export interface Store {
  reportExists(id: string): Promise<boolean>;
  insertReport(input: InsertReportInput): Promise<void>;
  upsertIssueFromReports(fingerprint: string, title: string, kind: string): Promise<void>;
  getIssue(fingerprint: string): Promise<IssueRow | null>;
  listIssues(filter: IssueFilter): Promise<IssueRow[]>;
  listReportsForIssue(fingerprint: string, limit: number): Promise<ReportRow[]>;
  getReport(id: string): Promise<ReportRow | null>;
  listVersions(): Promise<VersionCount[]>;
  patchIssue(fingerprint: string, patch: IssuePatch): Promise<boolean>;
  touchSymbolsMeta(quad: string, arch: string, debugId: string, entries: number, seenAtUtc: string): Promise<void>;
  // ── Right to erasure (plan §J) ─────────────────────────────────────────────────────────────────
  isInstallDeleted(installId: string): Promise<boolean>;
  listReportRefsForInstall(installId: string): Promise<InstallReportRef[]>;
  deleteReportsForInstall(installId: string): Promise<void>;
  /** Recomputes count/installs/first_seen/last_seen/versions_json for `fingerprint` after some of its
   *  reports were deleted; deletes the `issues` row outright once its count reaches 0. */
  recomputeOrDeleteIssue(fingerprint: string): Promise<void>;
  tombstoneInstall(installId: string, deletedAtUtc: string): Promise<void>;
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

/** `issues.title` — a short human label. Deterministic from the report so re-ingesting the same
 *  crash never changes it once written (the insert-only column, preserved by upsertIssueFromReports's
 *  ON CONFLICT clause). */
export function deriveTitle(summary: Summary, frames: readonly Frame[]): string {
  const top = frames.find((f) => f.name !== null)?.name;
  if (summary.kind === "Hang") return top ? `Hang · ${top}` : "Hang";
  if (summary.kind === "ExitCode") return `ExitCode ${formatHexCode(summary.exitCode)}`;
  if (summary.kind === "UncleanExit") return "Unclean exit";
  const head = summary.exceptionType || summary.kind;
  return top ? `${head} · ${top}` : head;
}

function formatHexCode(code: number): string {
  const u = code >>> 0;
  return `0x${u.toString(16).toUpperCase().padStart(8, "0")}`;
}

export class D1Store implements Store {
  constructor(private readonly db: D1Database) {}

  async reportExists(id: string): Promise<boolean> {
    const row = await this.db.prepare("SELECT 1 FROM reports WHERE id = ?1 LIMIT 1").bind(id).first();
    return row !== null;
  }

  async insertReport({ summary: s, framesJson, fingerprint, receivedAt }: InsertReportInput): Promise<void> {
    await this.db
      .prepare(
        `INSERT INTO reports(
          id, install_id, session_id, kind, quad, semver, commit_sha, channel, arch, os_build,
          gpu, gpu_tier, software_adapter, packaged, locale, uptime_ms, before_first_frame, last_route,
          exception_type, exception_message, exit_code, has_dump, dump_bytes, frames_json, fingerprint, received_at, debug_id
        ) VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?13,?14,?15,?16,?17,?18,?19,?20,?21,?22,?23,?24,?25,?26,?27)`,
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
        b(s.hasDump),
        s.dumpBytes,
        framesJson,
        fingerprint,
        receivedAt,
        s.debugId,
      )
      .run();
  }

  private async computeAggregates(fingerprint: string): Promise<{
    cnt: number;
    installs: number;
    first_seen: string;
    last_seen: string;
    versionsJson: string;
  } | null> {
    const agg = await this.db
      .prepare(
        `SELECT COUNT(*) AS cnt, COUNT(DISTINCT install_id) AS installs,
                MIN(received_at) AS first_seen, MAX(received_at) AS last_seen
         FROM reports WHERE fingerprint = ?1`,
      )
      .bind(fingerprint)
      .first<{ cnt: number; installs: number; first_seen: string; last_seen: string }>();
    if (!agg || agg.cnt === 0) return agg ? { ...agg, versionsJson: "{}" } : null;

    const versionsResult = await this.db
      .prepare("SELECT semver, COUNT(*) AS cnt FROM reports WHERE fingerprint = ?1 GROUP BY semver")
      .bind(fingerprint)
      .all<{ semver: string; cnt: number }>();
    const versions: Record<string, number> = {};
    for (const row of versionsResult.results) versions[row.semver] = row.cnt;
    return { ...agg, versionsJson: JSON.stringify(versions) };
  }

  /** Recomputes count/installs/first_seen/last_seen/versions_json for `fingerprint` from the
   *  `reports` table (the source of truth — no hand-maintained counters to drift), and upserts the
   *  `issues` row. `title`/`kind`/`status`/`github_issue` are set only on first insert. */
  async upsertIssueFromReports(fingerprint: string, title: string, kind: string): Promise<void> {
    const agg = await this.computeAggregates(fingerprint);
    if (!agg || agg.cnt === 0) return;

    await this.db
      .prepare(
        `INSERT INTO issues(fingerprint, title, kind, first_seen, last_seen, count, installs, versions_json, status, github_issue)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, 'open', NULL)
         ON CONFLICT(fingerprint) DO UPDATE SET
           count = excluded.count, installs = excluded.installs,
           first_seen = excluded.first_seen, last_seen = excluded.last_seen,
           versions_json = excluded.versions_json`,
      )
      .bind(fingerprint, title, kind, agg.first_seen, agg.last_seen, agg.cnt, agg.installs, agg.versionsJson)
      .run();
  }

  /** plan §J: called for every fingerprint touched by an erasure delete. An issue whose last report
   *  just got deleted is removed outright rather than left at count 0. */
  async recomputeOrDeleteIssue(fingerprint: string): Promise<void> {
    const agg = await this.computeAggregates(fingerprint);
    if (!agg || agg.cnt === 0) {
      await this.db.prepare("DELETE FROM issues WHERE fingerprint = ?1").bind(fingerprint).run();
      return;
    }
    await this.db
      .prepare(
        `UPDATE issues SET count = ?1, installs = ?2, first_seen = ?3, last_seen = ?4, versions_json = ?5
         WHERE fingerprint = ?6`,
      )
      .bind(agg.cnt, agg.installs, agg.first_seen, agg.last_seen, agg.versionsJson, fingerprint)
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

  async patchIssue(fingerprint: string, patch: IssuePatch): Promise<boolean> {
    const existing = await this.getIssue(fingerprint);
    if (!existing) return false;
    await this.db
      .prepare("UPDATE issues SET status = ?1, github_issue = ?2 WHERE fingerprint = ?3")
      .bind(patch.status ?? existing.status, patch.githubIssue ?? existing.github_issue, fingerprint)
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

  async listReportRefsForInstall(installId: string): Promise<InstallReportRef[]> {
    const result = await this.db
      .prepare("SELECT id, quad, fingerprint FROM reports WHERE install_id = ?1")
      .bind(installId)
      .all<InstallReportRef>();
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
    try {
      return JSON.parse(row.frames_json) as Frame[];
    } catch {
      return null;
    }
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
