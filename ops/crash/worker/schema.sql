-- Wavee crash service — D1 schema. Applied by hand (no CI):
--   wrangler d1 execute wavee-crash --remote --file schema.sql
-- See ops/crash/README.md for the full runbook.
--
-- No column here ever holds the client IP (cf-connecting-ip). The worker never reads it into a row.

CREATE TABLE reports(
  id TEXT PRIMARY KEY,
  install_id TEXT,
  session_id TEXT,
  kind TEXT,
  quad TEXT,
  semver TEXT,
  commit_sha TEXT,
  channel TEXT,
  arch TEXT,
  os_build TEXT,
  gpu TEXT,
  gpu_tier TEXT,
  software_adapter INTEGER,
  packaged INTEGER,
  locale TEXT,
  uptime_ms INTEGER,
  before_first_frame INTEGER,
  last_route TEXT,
  exception_type TEXT,
  exception_message TEXT,
  exit_code INTEGER,
  has_dump INTEGER,
  dump_bytes INTEGER,
  frames_json TEXT,
  fingerprint TEXT,
  received_at TEXT,
  -- `summary.debugId` (plan §I "upper-case GUID + '-' + age") — the crashing build's OWN PE debug id,
  -- sent by the client regardless of whether a matching .symmap exists server-side. Lets the dashboard's
  -- Report detail show exactly which build's symbols resolve this report's frames, even when the
  -- matching symmap hasn't been uploaded yet (crash-dashboard-implementation.md §4 `GET /v1/reports/:id`).
  debug_id TEXT
);

CREATE TABLE issues(
  fingerprint TEXT PRIMARY KEY,
  title TEXT,
  kind TEXT,
  first_seen TEXT,
  last_seen TEXT,
  count INTEGER,
  installs INTEGER,
  versions_json TEXT,
  status TEXT DEFAULT 'open',
  github_issue INTEGER
);

CREATE INDEX reports_fp ON reports(fingerprint, received_at);
CREATE INDEX reports_quad ON reports(quad, received_at);

-- Metadata about uploaded symbol maps, for the dashboard's Symbols tab. The binary map itself lives in
-- R2 at symbols/<quad>/win-<arch>.symmap (written directly by the release script via `wrangler r2 object
-- put` — see ops/crash/README.md). This table is NOT written by that upload step; the worker upserts a row
-- here opportunistically the first time it successfully parses a given quad/arch map while symbolicating
-- an ingested report, so "uploaded_at" here really means "first seen by the worker", not the true upload time.
CREATE TABLE symbols(
  quad TEXT NOT NULL,
  arch TEXT NOT NULL,
  debug_id TEXT,
  uploaded_at TEXT,
  entries INTEGER,
  PRIMARY KEY (quad, arch)
);

-- Right to erasure (plan §J, added 2026-09-24). A tombstone for every install id that has ever asked
-- to be forgotten: `DELETE /v1/installs/:installId` inserts one of these after deleting the install's
-- `reports` rows and R2 objects. `POST /v1/report` checks this table and refuses a tombstoned install id
-- with 410 Gone rather than silently re-admitting deleted data.
CREATE TABLE deleted_installs(
  install_id TEXT PRIMARY KEY,
  deleted_at TEXT
);
