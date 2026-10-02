-- Retention, alerts and grouping v2 (#165, docs/plans/wavee/crash-production-readiness-implementation.md §W1).
-- schema.sql stays the frozen baseline, and every later change is a numbered file here, applied with
--   npx wrangler d1 migrations apply wavee-crash --remote
-- (never `d1 execute --file` again — wrangler tracks applied migrations in d1_migrations).
--
-- Backward compatible with the Worker that predates it: only added columns (all defaulted or nullable) and indexes.

-- Native fault facts (summary.exceptionCode / faultModule / faultOffset) and the grouping version of `fingerprint`.
ALTER TABLE reports ADD COLUMN exception_code INTEGER NOT NULL DEFAULT 0;
ALTER TABLE reports ADD COLUMN fault_module  TEXT    NOT NULL DEFAULT '';
ALTER TABLE reports ADD COLUMN fault_offset  INTEGER NOT NULL DEFAULT 0;
ALTER TABLE reports ADD COLUMN fp_version    INTEGER NOT NULL DEFAULT 1;

-- Issues keep lifetime stats after their reports are purged (90 days), so counts are incremental from here on.
ALTER TABLE issues  ADD COLUMN regressed_at TEXT;
ALTER TABLE issues  ADD COLUMN regressions  INTEGER NOT NULL DEFAULT 0;
ALTER TABLE issues  ADD COLUMN resolved_at  TEXT;
ALTER TABLE issues  ADD COLUMN resolved_version TEXT;          -- newest semver seen when resolved (regression rule)
ALTER TABLE issues  ADD COLUMN last_frames_json TEXT;          -- stack outlives the 90-day purge
ALTER TABLE issues  ADD COLUMN fp_version   INTEGER NOT NULL DEFAULT 1;

-- received_at is the retention purge key, install_id backs erasure and installSeenForIssue.
CREATE INDEX reports_received ON reports(received_at);
CREATE INDEX reports_install  ON reports(install_id);
