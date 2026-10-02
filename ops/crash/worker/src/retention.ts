import type { Store } from "./store.js";

/**
 * The 90-day retention PRIVACY.md promises (#165, plan §W1 + appendix A2): a daily cron (`scheduled()` in index.ts,
 * wrangler.toml `[triggers]`) deletes `reports` rows older than RETENTION_DAYS together with their R2 objects.
 * `issues` rows are untouched — their counts, versions and status are lifetime stats. The R2 lifecycle rule on
 * `reports/` stays as a backstop only.
 *
 * Driven from D1, never from an R2 listing: each batch lists the expired rows, deletes all their objects in ONE
 * `bucket.delete([...])` (PURGE_BATCH × 4 keys ≤ R2's 1000), then deletes the same rows. ≤ 3 subrequests a batch,
 * ≤ 30 a run (the free plan allows 50); a backlog larger than one run's worth reports `more: true` and the next run
 * (or `POST /v1/retention/run`) continues it.
 */
export const RETENTION_DAYS = 90;
export const PURGE_BATCH = 200; // ×4 parts = 800 ≤ R2's 1000 keys/delete
export const PURGE_MAX_BATCHES = 10;

/** The four R2 objects a report can have under `reports/<quad>/<id>/` (the dump only when one was sent; deleting a
 *  missing key is a no-op). */
export const R2_PART_FILES = ["summary.json", "report.txt", "log-tail.txt", "minidump.dmp"] as const;

export function reportObjectKeys(r: { quad: string; id: string }): string[] {
  return R2_PART_FILES.map((f) => `reports/${r.quad}/${r.id}/${f}`);
}

export const retentionCutoffIso = (nowMs: number, days = RETENTION_DAYS): string =>
  new Date(nowMs - days * 86_400_000).toISOString();

export interface PurgeOptions {
  batch: number;
  maxBatches: number;
}

export interface PurgeResult {
  deleted: number;
  batches: number;
  /** True when the run stopped at `maxBatches` with expired rows possibly left. */
  more: boolean;
}

export async function purgeExpiredReports(
  store: Store,
  bucket: R2Bucket,
  nowMs: number,
  o: PurgeOptions = { batch: PURGE_BATCH, maxBatches: PURGE_MAX_BATCHES },
): Promise<PurgeResult> {
  const cutoff = retentionCutoffIso(nowMs);
  let deleted = 0, batches = 0;
  while (batches < o.maxBatches) {
    const refs = await store.listExpiredReportRefs(cutoff, o.batch);
    if (refs.length === 0) return { deleted, batches, more: false };
    await bucket.delete(refs.flatMap(reportObjectKeys));
    deleted += await store.deleteExpiredReports(cutoff, refs.length);
    batches++;
    if (refs.length < o.batch) return { deleted, batches, more: false };
  }
  return { deleted, batches, more: true };
}
