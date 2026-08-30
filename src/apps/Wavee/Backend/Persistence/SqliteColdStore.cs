using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Channels = System.Threading.Channels;   // alias: 'Channel' alone collides with Wavee.Backend.Channel (transport enum)
using Microsoft.Data.Sqlite;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;

namespace Wavee.Backend.Persistence;

// The SQLite cold tier. WAL mode + a single-reader WRITE-BEHIND queue that batches upserts into transactions, so the
// caller (UI thread) never blocks on disk.
//
// TWO CONNECTIONS (schema v5): the WRITER connection (guarded by _connLock) owns every mutation — the write-behind drain,
// the coarse membership/rootlist replaces, the outbox, GC/vacuum. A second READ-ONLY connection (guarded by _readLock,
// opened AFTER Migrate() so the schema exists) serves every point/bulk read. WAL means the reader sees the latest
// committed snapshot without ever queueing behind a background write batch, a GC delete run, or a vacuum slice.
//
// Schema is versioned through meta(schema_version) + an ordered migration runner that runs once on open, BEFORE the writer
// task starts (so it never races the queue). The account column is carried on every per-account table for the (deferred)
// per-account-DB-file split; until then a single file holds one logical account (DefaultAccount).
public sealed class SqliteColdStore : IColdStore, IMutationOutbox, IExtensionCacheStore
{
    public const string DefaultAccount = "default";

    /// <summary>The end-state schema version (v5 = the one `entity` table + entity_refs/artist_overview/recent_surfaces,
    /// fmt-framed payloads, thin columns, cache accounting; legacy `entities`/`localized_entities` dropped. v6 = the
    /// `playlists.adopted_at` stamp the membership GC dates its victims by — critique #6. v7 = `localized_extension_cache.last_access`
    /// + its LRU index, which is what finally makes the extension tier EVICTABLE: before it, `cache_bytes` counted those
    /// payloads but no sweep could delete them, so the byte budget had an unbounded floor it could never get under.
    /// v8 = `rootlist.added_at` (a folder rename resends the marker's ORIGINAL create timestamp) + `outbox.parent_folder`
    /// (a queued rootlist ADD remembers its folder, because the INDEX moves while the op waits). v9 =
    /// `playlist_items.chart_*` (status/current_pos/previous_pos/rank) — a chart playlist's per-row rank movement,
    /// so a cold-opened chart shows its arrows before the live re-fetch lands.</summary>
    public const int CurrentSchemaVersion = 9;

    /// <summary>Cap for the bulk <see cref="LoadAllExtensions"/> read. NOT used by the live path any more — the cache is
    /// point-read per miss — so this only bounds tests and offline tooling.</summary>
    public const int DefaultExtensionLimit = 2048;

    // meta keys owned by the cache tier.
    public const string MetaCacheBytes = "cache_bytes";
    public const string MetaVacuumPending = "vacuum_pending";
    public const string MetaCacheBudget = "cache_budget_bytes";

    /// <summary>The default cache-tier byte budget (§C.4). User-settable through <see cref="SetCacheBudgetBytes"/>.</summary>
    public const long DefaultCacheBudgetBytes = 64L * 1024 * 1024;

    const int MigrationBatchRows = 2000;    // chunked migrate: a 30k-row v4 db must never ride one giant transaction
    const int MaxInParams = 900;            // SQLITE_MAX_VARIABLE_NUMBER headroom for IN (...) lists
    const int RecentSurfaceCap = 50;
    const long ExtensionGraceSeconds = 7 * 24 * 60 * 60;   // +7d past expires_at keeps ETag 304-revalidation working

    readonly SqliteConnection _conn;
    readonly object _connLock = new();
    readonly SqliteConnection _read;        // read-only, WAL: UI-facing reads never queue behind background writer work
    readonly object _readLock = new();
    readonly string _account;
    readonly string? _spotifyLocale;
    readonly string _localeKey;             // the `entity.locale` value for this store ("" for a locale-less open)
    readonly Channels.Channel<WriteOp> _queue = Channels.Channel.CreateUnbounded<WriteOp>(new Channels.UnboundedChannelOptions { SingleReader = true });
    readonly Task _writer;

    // Prepared once, reused across batches: Microsoft.Data.Sqlite has no cross-command statement cache, so rebuilding the
    // commands + parameters every drain re-compiles statements and allocates per batch (and the steady-state drain often
    // processes a batch of 1).
    SqliteCommand? _entityCmd, _savedUpCmd, _savedDelCmd, _revCmd, _videoCmd, _extensionCmd, _ovrUpCmd, _ovrDelCmd;
    SqliteCommand? _sizeProbeCmd, _extSizeProbeCmd, _refDelCmd, _refInsCmd, _cacheDeltaCmd;
    SqliteParameter _eu = null!, _ek = null!, _ep = null!, _el = null!, _et = null!;
    SqliteParameter _eti = null!, _esu = null!, _eim = null!, _edu = null!, _efl = null!, _eal = null!, _efm = null!, _esz = null!, _ela = null!;
    SqliteParameter _zpu = null!, _zpl = null!;
    SqliteParameter _zxu = null!, _zxk = null!;
    SqliteParameter _rdp = null!, _rip = null!, _ric = null!;
    SqliteParameter _cbd = null!;
    SqliteParameter _vu = null!, _vp = null!;
    SqliteParameter _ou = null!, _op = null!, _oi = null!, _od = null!, _os = null!, _om = null!, _oa = null!, _oxu = null!;
    SqliteParameter _sa = null!, _ss = null!, _su = null!, _sy = null!, _st = null!;
    SqliteParameter _da = null!, _ds = null!, _du = null!;
    SqliteParameter _ra = null!, _rs = null!, _rr = null!, _rt = null!;
    SqliteParameter _xu = null!, _xl = null!, _xk = null!, _xp = null!, _xe = null!, _xo = null!, _xm = null!, _xx = null!, _xt = null!;

    public SqliteColdStore(string path) : this(path, DefaultAccount, null) { }

    public SqliteColdStore(string path, string account) : this(path, account, null) { }

    public SqliteColdStore(string path, string account, string? spotifyLocale)
    {
        _account = account;
        _spotifyLocale = string.IsNullOrWhiteSpace(spotifyLocale) ? null : SpotifyHeaders.NormalizeLanguage(spotifyLocale);
        _localeKey = _spotifyLocale ?? "";
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        // NOTE: the legacy `entities` table is deliberately NOT created here any more — v5 drops it, and re-creating it on
        // every open would resurrect it forever. Migrate() guards every legacy read on sqlite_master.
        // Video↔audio associations: own table (shares the track uri with the entity tables, so it can't reuse that PK).
        Exec("CREATE TABLE IF NOT EXISTS video_assoc(uri TEXT PRIMARY KEY, payload BLOB NOT NULL);");
        Exec("CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);");
        Exec("CREATE TABLE IF NOT EXISTS collection_items(account TEXT NOT NULL, set_id TEXT NOT NULL, item_uri TEXT NOT NULL, " +
             "added_at INTEGER NOT NULL DEFAULT 0, position INTEGER, sync INTEGER NOT NULL, PRIMARY KEY(account, set_id, item_uri));");
        Exec("CREATE INDEX IF NOT EXISTS ix_collection_added ON collection_items(account, set_id, added_at);");
        Exec("CREATE TABLE IF NOT EXISTS collection_rev(account TEXT NOT NULL, set_id TEXT NOT NULL, revision TEXT, synced_at INTEGER, PRIMARY KEY(account, set_id));");
        // Ordered playlists: the header lives in `entities` (kind=Playlist, thin); `playlists` carries only the opaque
        // revision; `playlist_items` is the ordered membership. (No header columns here → no duplication with the entity.)
        // `adopted_at` (v6) is the membership-GC clock: when this playlist's membership was last adopted. A fresh db gets
        // the column here; an existing v5 file gets it through the guarded ALTER in MigrateToV6().
        Exec("CREATE TABLE IF NOT EXISTS playlists(uri TEXT PRIMARY KEY, base_rev BLOB, adopted_at INTEGER NOT NULL DEFAULT 0);");
        Exec("CREATE TABLE IF NOT EXISTS playlist_items(playlist_uri TEXT NOT NULL, position INTEGER NOT NULL, item_id TEXT, " +
             "item_uri TEXT NOT NULL, added_by TEXT, added_at INTEGER, " +
             "chart_status INTEGER NOT NULL DEFAULT 0, chart_current_pos INTEGER NOT NULL DEFAULT 0, " +
             "chart_previous_pos INTEGER NOT NULL DEFAULT 0, chart_rank INTEGER NOT NULL DEFAULT 0, " +
             "PRIMARY KEY(playlist_uri, position));");
        // `added_at` (v8) is the row's playlist4 ADD timestamp: a folder RENAME has to resend the marker's ORIGINAL
        // create timestamp, so it must survive a restart. A fresh db gets the column here; an existing file gets it
        // through the guarded ALTER in MigrateToV8().
        // `chart_*` (v9) is the row's chart rank-movement snapshot (ItemAttributes.format_attributes on a
        // format=="chart" list) — all zero = no chart facts, indistinguishable from "not a chart row". A fresh db gets
        // the columns here; an existing file gets them through the guarded ALTERs in MigrateToV9().
        Exec("CREATE TABLE IF NOT EXISTS rootlist(account TEXT NOT NULL, position INTEGER NOT NULL, kind INTEGER, uri TEXT, group_name TEXT, depth INTEGER, added_at INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(account, position));");
        // Durable mutation outbox: pending intents survive a restart. `op` holds the wire ListChanges body for oprebase edits.
        Exec("CREATE TABLE IF NOT EXISTS outbox(id INTEGER PRIMARY KEY, type TEXT NOT NULL, entity_key TEXT NOT NULL, set_id TEXT, target_saved INTEGER, op BLOB, base_rev BLOB, attempts INTEGER NOT NULL DEFAULT 0, parent_folder TEXT);");
        Exec("CREATE TABLE IF NOT EXISTS dead_letter(id INTEGER PRIMARY KEY, type TEXT, entity_key TEXT, reason TEXT, created_at INTEGER);");
        Migrate();
        // NO open-time extension sweep. It used to run here — one unbatched DELETE of every expired row, in a single
        // transaction, before the reader connection even opened — which is O(expired rows) squarely on the pre-first-paint
        // path and a steady source of WAL growth. EntityCacheGc already calls the identical sweep on its recurring pass
        // (armed ~30 s after warm, off the UI thread), and the +7 d ETag grace means nothing cares about a 30 s delay.
        // The reader opens only AFTER Migrate() + the open-time sweep: the schema (and the v5 DDL) must exist first, and a
        // read-only connection can only attach to the WAL once the writer has created it.
        _read = OpenReader(path);
        _writer = Task.Run(WriteLoopAsync);
    }

    static SqliteConnection OpenReader(string path)
    {
        try
        {
            var ro = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
            ro.Open();
            return ro;
        }
        catch (SqliteException)
        {
            // Read-only attach can fail on exotic filesystems (no -shm creation rights). A second read/write connection is
            // still the point — it just isn't enforced by the driver.
            var rw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            rw.Open();
            return rw;
        }
    }

    public string Account => _account;
    public string? MetadataLocale => _spotifyLocale;

    void Exec(string sql)
    {
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = sql;
            c.ExecuteNonQuery();
        }
    }

    // Writer-side, caller already holds _connLock.
    void ExecLocked(string sql, SqliteTransaction? tx = null)
    {
        using var c = _conn.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    bool TableExistsLocked(string name)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n;";
        c.Parameters.AddWithValue("$n", name);
        return c.ExecuteScalar() is not null;
    }

    // Ordered, one-time schema migrations. Runs in the ctor before the writer starts, so there is no queue contention.
    void Migrate()
    {
        lock (_connLock)
        {
            string? ver;
            using (var c = _conn.CreateCommand()) { c.CommandText = "SELECT value FROM meta WHERE key='schema_version';"; ver = c.ExecuteScalar() as string; }
            if (ver is null)
            {

            using var tx = _conn.BeginTransaction();
            // v0 → v1: fold a legacy `saved(setid,uri,sync)` table into collection_items, then drop it.
            bool legacySaved;
            using (var c = _conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='saved';"; legacySaved = c.ExecuteScalar() is not null; }
            if (legacySaved)
            {
                using (var c = _conn.CreateCommand())
                {
                    c.Transaction = tx;
                    c.CommandText = "INSERT OR IGNORE INTO collection_items(account,set_id,item_uri,added_at,position,sync) SELECT $a,setid,uri,0,NULL,sync FROM saved;";
                    c.Parameters.AddWithValue("$a", _account);
                    c.ExecuteNonQuery();
                }
                using (var c = _conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DROP TABLE saved;"; c.ExecuteNonQuery(); }
            }
            using (var c = _conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "INSERT OR REPLACE INTO meta(key,value) VALUES('schema_version','1');"; c.ExecuteNonQuery(); }
            tx.Commit();
            ver = "1";
            }

            if (ver == "1")
            {
                using var tx = _conn.BeginTransaction();
                using (var c = _conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "UPDATE playlists SET base_rev = NULL;"; c.ExecuteNonQuery(); }
                using (var c = _conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM meta WHERE key='rootlist_rev';"; c.ExecuteNonQuery(); }
                using (var c = _conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "INSERT OR REPLACE INTO meta(key,value) VALUES('schema_version','2');"; c.ExecuteNonQuery(); }
                tx.Commit();
                ver = "2";
            }

            if (ver == "2")
            {
                using var tx = _conn.BeginTransaction();
                using (var c = _conn.CreateCommand())
                {
                    c.Transaction = tx;
                    c.CommandText = """
                        CREATE TABLE IF NOT EXISTS localized_entities(
                            uri TEXT NOT NULL,
                            locale TEXT NOT NULL,
                            kind INTEGER NOT NULL,
                            payload BLOB NOT NULL,
                            updated_at INTEGER NOT NULL,
                            PRIMARY KEY(uri, locale));
                        CREATE INDEX IF NOT EXISTS ix_localized_entities_locale ON localized_entities(locale);
                        CREATE INDEX IF NOT EXISTS ix_localized_entities_updated ON localized_entities(updated_at);
                        CREATE TABLE IF NOT EXISTS localized_extension_cache(
                            entity_uri TEXT NOT NULL,
                            locale TEXT NOT NULL,
                            extension_kind INTEGER NOT NULL,
                            payload BLOB,
                            etag TEXT,
                            offline_ttl INTEGER NOT NULL DEFAULT 0,
                            missing INTEGER NOT NULL DEFAULT 0,
                            expires_at INTEGER NOT NULL,
                            updated_at INTEGER NOT NULL,
                            PRIMARY KEY(entity_uri, locale, extension_kind));
                        CREATE INDEX IF NOT EXISTS ix_localized_extension_locale ON localized_extension_cache(locale);
                        CREATE INDEX IF NOT EXISTS ix_localized_extension_expiry ON localized_extension_cache(expires_at);
                        """;
                    c.ExecuteNonQuery();
                }
                using (var c = _conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "INSERT OR REPLACE INTO meta(key,value) VALUES('schema_version','3');"; c.ExecuteNonQuery(); }
                tx.Commit();
                ver = "3";
            }

            // v3 → v4: user-attached local video overrides. Purely ADDITIVE (one CREATE TABLE) — an existing library.db
            // keeps every row it had. `uri` is the exact playable uri (any namespace) and the PK, so an attach onto an
            // already-overridden playable IS the replace. No `account` column: the db file is device-wide by construction,
            // which is exactly the disclosed sharing model ("applied on this computer, for every account").
            if (ver == "3")
            {
                using var tx = _conn.BeginTransaction();
                using (var c = _conn.CreateCommand())
                {
                    c.Transaction = tx;
                    c.CommandText = """
                        CREATE TABLE IF NOT EXISTS video_override(
                            uri TEXT PRIMARY KEY,
                            path TEXT NOT NULL,
                            id TEXT NOT NULL,
                            duration_ms INTEGER DEFAULT 0,
                            size INTEGER DEFAULT 0,
                            mtime INTEGER DEFAULT 0,
                            added_at INTEGER DEFAULT 0);
                        """;
                    c.ExecuteNonQuery();
                }
                using (var c = _conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "INSERT OR REPLACE INTO meta(key,value) VALUES('schema_version','4');"; c.ExecuteNonQuery(); }
                tx.Commit();
                ver = "4";
            }

            if (ver == "4") { MigrateToV5(); ver = "5"; }
            if (ver == "5") { MigrateToV6(); ver = "6"; }
            if (ver == "6") { MigrateToV7(); ver = "7"; }
            if (ver == "7") { MigrateToV8(); ver = "8"; }
            if (ver == "8") { MigrateToV9(); ver = "9"; }
        }
    }

    // ── v8 → v9: chart-playlist per-row rank movement ────────────────────────────────────────────────────────────────
    // Purely ADDITIVE (four guarded ALTERs, no row rewritten): the chart status/positions/rank an ordinary playlist
    // never carries default to 0, which FromCold reads back as "no chart facts" — a pre-v9 row round-trips unchanged.
    void MigrateToV9()
    {
        using var tx = _conn.BeginTransaction();
        if (!ColumnExistsLocked("playlist_items", "chart_status", tx))
            ExecLocked("ALTER TABLE playlist_items ADD COLUMN chart_status INTEGER NOT NULL DEFAULT 0;", tx);
        if (!ColumnExistsLocked("playlist_items", "chart_current_pos", tx))
            ExecLocked("ALTER TABLE playlist_items ADD COLUMN chart_current_pos INTEGER NOT NULL DEFAULT 0;", tx);
        if (!ColumnExistsLocked("playlist_items", "chart_previous_pos", tx))
            ExecLocked("ALTER TABLE playlist_items ADD COLUMN chart_previous_pos INTEGER NOT NULL DEFAULT 0;", tx);
        if (!ColumnExistsLocked("playlist_items", "chart_rank", tx))
            ExecLocked("ALTER TABLE playlist_items ADD COLUMN chart_rank INTEGER NOT NULL DEFAULT 0;", tx);
        ExecLocked("INSERT OR REPLACE INTO meta(key,value) VALUES('schema_version','9');", tx);
        tx.Commit();
    }

    // ── v7 → v8: rootlist ADD timestamps + the queued create's rootlist placement ─────────────────────────────────────
    // Purely ADDITIVE (two guarded ALTERs, no row rewritten):
    //   rootlist.added_at   — the marker's playlist4 ItemAttributes.timestamp. A folder rename resends the ORIGINAL
    //                         create timestamp (golden b037), so it has to outlive a restart. Backfilled to 0 =
    //                         "not captured": the rename path bootstraps the rootlist rather than inventing one.
    //   outbox.parent_folder — the folder a queued create/follow rootlist ADD belongs in. The INDEX it resolves to
    //                         moves while the op waits, so the durable row remembers the folder id, not the index.
    void MigrateToV8()
    {
        using var tx = _conn.BeginTransaction();
        if (!ColumnExistsLocked("rootlist", "added_at", tx))
            ExecLocked("ALTER TABLE rootlist ADD COLUMN added_at INTEGER NOT NULL DEFAULT 0;", tx);
        if (!ColumnExistsLocked("outbox", "parent_folder", tx))
            ExecLocked("ALTER TABLE outbox ADD COLUMN parent_folder TEXT;", tx);
        ExecLocked("INSERT OR REPLACE INTO meta(key,value) VALUES('schema_version','8');", tx);
        tx.Commit();
    }


    // ── v6 → v7: make the extension tier evictable ───────────────────────────────────────────────────────────────────
    // `cache_bytes` has always counted localized_extension_cache payloads, but GcEnforceBudget only ever deleted from
    // `entity` — so those bytes were an un-evictable FLOOR under the byte budget (36 MB under a 64 MB budget on a real
    // profile, and it only grows). A last_access column plus its index gives the sweep an LRU order to delete in, exactly
    // like ix_entity_gc does for entities. Purely ADDITIVE: one column, one index, no row rewritten.
    void MigrateToV7()
    {
        // Every read for the life of this file resolves pages through the WAL, and the index build below wants a clean
        // sequential pass over the table. Truncate first — a pragma CANNOT run inside a transaction, so both calls sit
        // outside one. Safe here: _read does not open until after Migrate(), so this is the only connection.
        ExecLocked("PRAGMA wal_checkpoint(TRUNCATE);");
        using (var tx = _conn.BeginTransaction())
        {
            if (!ColumnExistsLocked("localized_extension_cache", "last_access", tx))
                ExecLocked("ALTER TABLE localized_extension_cache ADD COLUMN last_access INTEGER NOT NULL DEFAULT 0;", tx);
            // Backfill from updated_at so nothing looks infinitely cold on the first pass after the upgrade.
            ExecLocked("UPDATE localized_extension_cache SET last_access=updated_at WHERE last_access=0;", tx);
            ExecLocked("CREATE INDEX IF NOT EXISTS ix_localized_extension_lru ON localized_extension_cache(last_access);", tx);
            ExecLocked("INSERT OR REPLACE INTO meta(key,value) VALUES('schema_version','7');", tx);
            tx.Commit();
        }
        ExecLocked("PRAGMA wal_checkpoint(TRUNCATE);");   // land the new index pages in the db file
    }

    // ── v5 → v6: the membership-GC clock ─────────────────────────────────────────────────────────────────────────────
    // Purely ADDITIVE on an IDENTITY table (locked decision 2): one column, backfilled to "now" so no existing playlist
    // is instantly 14 days stale. Guarded on the column already existing, so the fresh-db path (which creates it in the
    // ctor DDL) and a re-run after a crash are both no-ops.
    void MigrateToV6()
    {
        using var tx = _conn.BeginTransaction();
        if (!ColumnExistsLocked("playlists", "adopted_at", tx))
            ExecLocked("ALTER TABLE playlists ADD COLUMN adopted_at INTEGER NOT NULL DEFAULT 0;", tx);
        using (var c = _conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "UPDATE playlists SET adopted_at=$t WHERE adopted_at IS NULL OR adopted_at=0;";
            c.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            c.ExecuteNonQuery();
        }
        ExecLocked("INSERT OR REPLACE INTO meta(key,value) VALUES('schema_version','6');", tx);
        tx.Commit();
    }

    bool ColumnExistsLocked(string table, string column, SqliteTransaction? tx)
    {
        using var c = _conn.CreateCommand();
        c.Transaction = tx;
        c.CommandText = $"PRAGMA table_info({table});";
        using var r = c.ExecuteReader();
        while (r.Read()) if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ── v4 → v5: the end-state cache tier ────────────────────────────────────────────────────────────────────────────
    // Creates `entity` (thin columns + payload LAST + fmt framing + cache accounting), `entity_refs`, `artist_overview`
    // and `recent_surfaces`; migrates ONLY the PIN-REACHABLE rows out of the legacy `entities` ∪ `localized_entities`
    // generation (plus the album/artist closure of the migrated tracks) and drops the legacy tables. Not migrating the
    // rest IS the one-time GC — every unmigrated row is re-fetchable cache by construction.
    //
    // Chunked: the DDL, each ~2000-row batch, and the final drop/accounting each commit separately, so a 30k-row migrate
    // never holds one giant transaction. A crash mid-migration leaves schema_version at 4 and the whole pass re-runs
    // idempotently (ON CONFLICT DO NOTHING + a recomputed cache_bytes).
    void MigrateToV5()
    {
        ExecLocked("""
            CREATE TABLE IF NOT EXISTS entity(
                uri         TEXT NOT NULL,
                locale      TEXT NOT NULL,
                kind        INTEGER NOT NULL,
                title       TEXT,
                subtitle    TEXT,
                image_url   TEXT,
                duration_ms INTEGER,
                flags       INTEGER NOT NULL DEFAULT 0,
                album_uri   TEXT,
                fmt         INTEGER NOT NULL DEFAULT 0,
                size        INTEGER NOT NULL,
                updated_at  INTEGER NOT NULL,
                last_access INTEGER NOT NULL,
                payload     BLOB,
                PRIMARY KEY(uri, locale));
            CREATE INDEX IF NOT EXISTS ix_entity_gc ON entity(kind, last_access);
            CREATE TABLE IF NOT EXISTS entity_refs(
                parent_uri TEXT NOT NULL,
                child_uri  TEXT NOT NULL,
                PRIMARY KEY(parent_uri, child_uri)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS artist_overview(
                uri         TEXT PRIMARY KEY,
                locale      TEXT NOT NULL,
                fmt         INTEGER NOT NULL DEFAULT 1,
                payload     BLOB,
                size        INTEGER NOT NULL,
                fetched_at  INTEGER NOT NULL,
                last_access INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS recent_surfaces(
                uri TEXT PRIMARY KEY, kind INTEGER, last_opened INTEGER);
            -- Belt-and-braces: the extension cache is part of the v5 end state (it is swept, capped and accounted here),
            -- but a db that skipped the v2→v3 rung (hand-seeded fixtures) would not have it. Same DDL as v3, IF NOT EXISTS.
            CREATE TABLE IF NOT EXISTS localized_extension_cache(
                entity_uri     TEXT NOT NULL,
                locale         TEXT NOT NULL,
                extension_kind INTEGER NOT NULL,
                payload        BLOB,
                etag           TEXT,
                offline_ttl    INTEGER NOT NULL DEFAULT 0,
                missing        INTEGER NOT NULL DEFAULT 0,
                expires_at     INTEGER NOT NULL,
                updated_at     INTEGER NOT NULL,
                PRIMARY KEY(entity_uri, locale, extension_kind));
            CREATE INDEX IF NOT EXISTS ix_localized_extension_expiry ON localized_extension_cache(expires_at);
            """);

        bool legacyBase = TableExistsLocked("entities");
        bool legacyLocalized = TableExistsLocked("localized_entities");
        if (legacyBase || legacyLocalized)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var migrated = new HashSet<string>(StringComparer.Ordinal);

            BuildPinTempTable();
            MigrateLegacyRows("pin_uri", legacyBase, legacyLocalized, now, migrated);
            // Closure: albums/artists referenced by the pinned tracks we just migrated, even if not directly pinned.
            BuildClosureTempTable();
            MigrateLegacyRows("pin_closure", legacyBase, legacyLocalized, now, migrated);
            ExecLocked("DROP TABLE IF EXISTS temp.pin_uri; DROP TABLE IF EXISTS temp.pin_closure;");
        }

        using var final = _conn.BeginTransaction();
        ExecLocked("""
            DROP TABLE IF EXISTS entities;
            DROP TABLE IF EXISTS localized_entities;
            DROP INDEX IF EXISTS ix_localized_entities_locale;
            DROP INDEX IF EXISTS ix_localized_entities_updated;
            DROP INDEX IF EXISTS ix_localized_extension_locale;
            """, final);   // ix_localized_extension_expiry is KEPT — the TTL sweep is its first real consumer
        ExecLocked($"""
            INSERT INTO meta(key,value) VALUES('{MetaCacheBytes}', CAST(
                (SELECT IFNULL(SUM(size),0) FROM entity)
              + (SELECT IFNULL(SUM(size),0) FROM artist_overview)
              + (SELECT IFNULL(SUM(length(payload)),0) FROM localized_extension_cache) AS TEXT))
            ON CONFLICT(key) DO UPDATE SET value=excluded.value;
            INSERT OR REPLACE INTO meta(key,value) VALUES('{MetaVacuumPending}','1');
            INSERT OR REPLACE INTO meta(key,value) VALUES('schema_version','5');
            """, final);
        final.Commit();
    }

    // The pin set, in pure SQL over the identity tables (never a stored boolean — §A.3). recent_surfaces is deliberately
    // absent here: at migration time it is empty by construction.
    void BuildPinTempTable()
    {
        ExecLocked("DROP TABLE IF EXISTS temp.pin_uri; CREATE TEMP TABLE pin_uri(uri TEXT PRIMARY KEY);");
        ExecLocked($"""
            INSERT OR IGNORE INTO temp.pin_uri(uri) {PinSetSql};
            DELETE FROM temp.pin_uri WHERE uri IS NULL OR uri='';
            """);
    }

    void BuildClosureTempTable()
    {
        ExecLocked("""
            DROP TABLE IF EXISTS temp.pin_closure;
            CREATE TEMP TABLE pin_closure(uri TEXT PRIMARY KEY);
            INSERT OR IGNORE INTO temp.pin_closure(uri)
                SELECT DISTINCT child_uri FROM entity_refs
                WHERE child_uri<>'' AND child_uri NOT IN (SELECT uri FROM temp.pin_uri);
            """);
    }

    // The three legacy legs in the SAME precedence order the old 3-leg UNION + ROW_NUMBER query used: current-locale
    // localized rows win, then the base `entities` generation, then any other locale (newest first).
    void MigrateLegacyRows(string pinTable, bool legacyBase, bool legacyLocalized, long now, HashSet<string> migrated)
    {
        if (legacyLocalized && _spotifyLocale is not null)
            MigrateLegacyPass($"SELECT uri,kind,payload FROM localized_entities WHERE locale=$loc AND uri>$last " +
                              $"AND uri IN (SELECT uri FROM temp.{pinTable}) ORDER BY uri LIMIT $n;", true, now, migrated);
        if (legacyBase)
            MigrateLegacyPass($"SELECT uri,kind,payload FROM entities WHERE uri>$last " +
                              $"AND uri IN (SELECT uri FROM temp.{pinTable}) ORDER BY uri LIMIT $n;", false, now, migrated);
        if (legacyLocalized)
            MigrateLegacyPass(_spotifyLocale is not null
                ? $"SELECT uri,kind,payload FROM localized_entities WHERE locale<>$loc AND uri>$last " +
                  $"AND uri IN (SELECT uri FROM temp.{pinTable}) ORDER BY uri, updated_at DESC LIMIT $n;"
                : $"SELECT uri,kind,payload FROM localized_entities WHERE uri>$last " +
                  $"AND uri IN (SELECT uri FROM temp.{pinTable}) ORDER BY uri, updated_at DESC LIMIT $n;",
                _spotifyLocale is not null, now, migrated);
    }

    // Keyset-paged by uri (never OFFSET), one transaction per batch. Because the ordering groups a uri's rows together and
    // the winner is that group's first row, advancing the cursor past the page's last uri can only skip already-beaten rows.
    void MigrateLegacyPass(string sql, bool bindLocale, long now, HashSet<string> migrated)
    {
        string last = "";
        var rows = new List<(string Uri, int Kind, byte[] Payload)>(MigrationBatchRows);
        while (true)
        {
            rows.Clear();
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = sql;
                c.Parameters.AddWithValue("$last", last);
                c.Parameters.AddWithValue("$n", MigrationBatchRows);
                if (bindLocale) c.Parameters.AddWithValue("$loc", _spotifyLocale!);
                using var r = c.ExecuteReader();
                while (r.Read())
                    rows.Add((r.GetString(0), r.GetInt32(1), r.IsDBNull(2) ? Array.Empty<byte>() : r.GetFieldValue<byte[]>(2)));
            }
            if (rows.Count == 0) return;
            last = rows[rows.Count - 1].Uri;

            using (var tx = _conn.BeginTransaction())
            {
                using var ins = _conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText =
                    "INSERT INTO entity(uri,locale,kind,title,subtitle,image_url,duration_ms,flags,album_uri,fmt,size,updated_at,last_access,payload) " +
                    "VALUES($u,$l,$k,$ti,$sub,$img,$dur,$fl,$al,$fmt,$sz,$t,$la,$p) ON CONFLICT(uri,locale) DO NOTHING;";
                var pu = ins.Parameters.Add("$u", SqliteType.Text);
                var pl = ins.Parameters.Add("$l", SqliteType.Text); pl.Value = _localeKey;
                var pk = ins.Parameters.Add("$k", SqliteType.Integer);
                var pti = ins.Parameters.Add("$ti", SqliteType.Text);
                var psub = ins.Parameters.Add("$sub", SqliteType.Text);
                var pimg = ins.Parameters.Add("$img", SqliteType.Text);
                var pdur = ins.Parameters.Add("$dur", SqliteType.Integer);
                var pfl = ins.Parameters.Add("$fl", SqliteType.Integer);
                var pal = ins.Parameters.Add("$al", SqliteType.Text);
                var pfmt = ins.Parameters.Add("$fmt", SqliteType.Integer);
                var psz = ins.Parameters.Add("$sz", SqliteType.Integer);
                var pt = ins.Parameters.Add("$t", SqliteType.Integer); pt.Value = now;
                var pla = ins.Parameters.Add("$la", SqliteType.Integer); pla.Value = now;
                var pp = ins.Parameters.Add("$p", SqliteType.Blob);

                using var refIns = _conn.CreateCommand();
                refIns.Transaction = tx;
                refIns.CommandText = "INSERT OR IGNORE INTO entity_refs(parent_uri,child_uri) VALUES($p,$c);";
                var rp = refIns.Parameters.Add("$p", SqliteType.Text);
                var rc = refIns.Parameters.Add("$c", SqliteType.Text);

                foreach (var row in rows)
                {
                    if (!migrated.Add(row.Uri)) continue;
                    var kind = (EntityKind)row.Kind;
                    // A row whose JSON will not parse is copied VERBATIM as fmt=0 with null thin columns — a pinned row is
                    // never thrown away, and one bad payload never fails the migration.
                    bool parsed = EntityThinExtractor.TryExtract(row.Payload, kind, out var thin);
                    int fmt = parsed ? PayloadCodec.FmtZstd : PayloadCodec.FmtRawJson;
                    var stored = PayloadCodec.Encode(row.Payload, fmt);

                    pu.Value = row.Uri;
                    pk.Value = row.Kind;
                    pti.Value = (object?)thin.Title ?? DBNull.Value;
                    psub.Value = (object?)thin.Subtitle ?? DBNull.Value;
                    pimg.Value = (object?)thin.ImageUrl ?? DBNull.Value;
                    pdur.Value = thin.DurationMs is { } d ? d : DBNull.Value;
                    pfl.Value = thin.Flags;
                    pal.Value = (object?)thin.AlbumUri ?? DBNull.Value;
                    pfmt.Value = fmt;
                    psz.Value = stored.Length;
                    pp.Value = stored;
                    ins.ExecuteNonQuery();

                    if (kind == EntityKind.Track && thin.Refs is { Count: > 0 } refs)
                    {
                        rp.Value = row.Uri;
                        for (int i = 0; i < refs.Count; i++) { rc.Value = refs[i]; refIns.ExecuteNonQuery(); }
                    }
                }
                tx.Commit();
            }
            if (rows.Count < MigrationBatchRows) return;
        }
    }

    // ── the pin set (§A.3 P0) ────────────────────────────────────────────────────────────────────────────────────────
    // Derived at read time, never stored: a persisted pin bit drifts on unlike/unfollow/account switch. recent_surfaces
    // joins the union post-v5 (it is a pin REASON: the last 50 opened detail pages must survive a restart offline).
    const string PinSetSql = """
        SELECT item_uri AS uri FROM collection_items
        UNION SELECT item_uri FROM playlist_items
        UNION SELECT uri FROM playlists
        UNION SELECT uri FROM rootlist WHERE uri IS NOT NULL
        UNION SELECT entity_key FROM outbox
        UNION SELECT uri FROM video_override
        """;

    const string PinSetWithRecentSql = PinSetSql + """

        UNION SELECT uri FROM recent_surfaces
        """;

    // One table, one predicate (the v4 3-leg UNION + ROW_NUMBER ranking is gone). The cross-locale leg survives only as a
    // covering-index probe over the (uri,locale) PK — in the single-locale steady state it reads no table pages at all.
    public IEnumerable<ColdEntity> LoadAllEntities()
    {
        var list = new List<ColdEntity>(4096);   // the app targets 10k+ entities — pre-size, skip the doubling-realloc chain
        var seen = new HashSet<string>(StringComparer.Ordinal);
        lock (_readLock)
        {
            using (var c = _read.CreateCommand())
            {
                c.CommandText = "SELECT uri, kind, payload FROM entity WHERE locale=$l;";
                c.Parameters.AddWithValue("$l", _localeKey);
                using var r = c.ExecuteReader();
                while (r.Read())
                {
                    var uri = r.GetString(0);
                    seen.Add(uri);
                    list.Add(new ColdEntity(uri, (EntityKind)r.GetInt32(1),
                        PayloadCodec.Decode(r.IsDBNull(2) ? null : r.GetFieldValue<byte[]>(2))));
                }
            }

            List<string>? strays = null;
            using (var c = _read.CreateCommand())
            {
                c.CommandText = "SELECT DISTINCT uri FROM entity WHERE locale<>$l;";
                c.Parameters.AddWithValue("$l", _localeKey);
                using var r = c.ExecuteReader();
                while (r.Read()) { var u = r.GetString(0); if (!seen.Contains(u)) (strays ??= new List<string>()).Add(u); }
            }
            if (strays is not null)
                foreach (var u in strays)
                    if (ReadCrossLocaleLocked(u) is { } e) list.Add(e);
        }
        return list;
    }

    /// <summary>Batched PK read for the Wave-B warm set. Chunked at <see cref="MaxInParams"/> so a 30k-uri warm is a
    /// handful of statements, and the read lock is taken PER CHUNK — a UI-facing point read never waits for the whole
    /// warm pass. Only the current locale is read: a stray cross-locale row still rehydrates through
    /// <see cref="GetEntity"/>'s fallback probe on first access.</summary>
    public IEnumerable<ColdEntity> LoadEntities(IReadOnlyCollection<string> uris)
    {
        var list = new List<ColdEntity>(uris.Count);
        if (uris.Count == 0) return list;
        var chunk = new List<string>(Math.Min(uris.Count, MaxInParams));
        foreach (var uri in uris)
        {
            if (string.IsNullOrEmpty(uri)) continue;
            chunk.Add(uri);
            if (chunk.Count == MaxInParams) { LoadEntityChunk(chunk, list); chunk.Clear(); }
        }
        if (chunk.Count > 0) LoadEntityChunk(chunk, list);
        return list;
    }

    void LoadEntityChunk(List<string> chunk, List<ColdEntity> into)
    {
        var sql = new System.Text.StringBuilder("SELECT uri,kind,payload FROM entity WHERE locale=$l AND uri IN (");
        for (int i = 0; i < chunk.Count; i++) { if (i > 0) sql.Append(','); sql.Append("$u").Append(i); }
        sql.Append(");");
        lock (_readLock)
        {
            using var c = _read.CreateCommand();
            c.CommandText = sql.ToString();
            c.Parameters.AddWithValue("$l", _localeKey);
            for (int i = 0; i < chunk.Count; i++) c.Parameters.AddWithValue("$u" + i, chunk[i]);
            using var r = c.ExecuteReader();
            while (r.Read())
                into.Add(new ColdEntity(r.GetString(0), (EntityKind)r.GetInt32(1),
                    PayloadCodec.Decode(r.IsDBNull(2) ? null : r.GetFieldValue<byte[]>(2))));
        }
    }

    // Indexed single-row rehydration (CachedStore cold-fallback): a PK point read on (uri, locale), with the rare
    // cross-locale probe behind it. Rides the READ connection, so it never queues behind the write-behind drain or GC.
    public ColdEntity? GetEntity(string uri)
    {
        lock (_readLock)
        {
            using (var c = _read.CreateCommand())
            {
                c.CommandText = "SELECT kind,payload FROM entity WHERE uri=$u AND locale=$l;";
                c.Parameters.AddWithValue("$u", uri);
                c.Parameters.AddWithValue("$l", _localeKey);
                using var r = c.ExecuteReader();
                if (r.Read())
                    return new ColdEntity(uri, (EntityKind)r.GetInt32(0),
                        PayloadCodec.Decode(r.IsDBNull(1) ? null : r.GetFieldValue<byte[]>(1)));
            }
            return ReadCrossLocaleLocked(uri);
        }
    }

    // Same precedence the v4 UNION gave a locale miss: the locale-less ("canonical") row first, then the freshest other
    // locale. Caller holds _readLock.
    ColdEntity? ReadCrossLocaleLocked(string uri)
    {
        using var c = _read.CreateCommand();
        c.CommandText = "SELECT kind,payload FROM entity WHERE uri=$u AND locale<>$l ORDER BY (locale='') DESC, updated_at DESC LIMIT 1;";
        c.Parameters.AddWithValue("$u", uri);
        c.Parameters.AddWithValue("$l", _localeKey);
        using var r = c.ExecuteReader();
        return r.Read()
            ? new ColdEntity(uri, (EntityKind)r.GetInt32(0), PayloadCodec.Decode(r.IsDBNull(1) ? null : r.GetFieldValue<byte[]>(1)))
            : null;
    }

    /// <summary>POINT-READ the persisted extension rows for a set of uris at one kind — the live path's ONLY read.
    /// Rides `sqlite_autoindex_localized_extension_cache_1` on (entity_uri, locale, extension_kind): O(log n) per uri, so
    /// the cost is the working set and NEVER the table. Chunked at <see cref="MaxInParams"/> and the read lock is taken
    /// PER CHUNK, exactly like <see cref="LoadEntities"/>, so a screenful never blocks behind a larger request.
    /// Expired rows are returned too — their ETag is what makes the follow-up fetch a cheap 304.</summary>
    public IReadOnlyList<ColdExtension> LoadExtensions(IReadOnlyCollection<string> uris, int extensionKind)
    {
        var list = new List<ColdExtension>(uris.Count);
        if (uris.Count == 0 || _spotifyLocale is null) return list;
        var chunk = new List<string>(Math.Min(uris.Count, MaxInParams));
        foreach (var uri in uris)
        {
            if (string.IsNullOrEmpty(uri)) continue;
            chunk.Add(uri);
            if (chunk.Count == MaxInParams) { LoadExtensionChunk(chunk, extensionKind, list); chunk.Clear(); }
        }
        if (chunk.Count > 0) LoadExtensionChunk(chunk, extensionKind, list);
        return list;
    }

    void LoadExtensionChunk(List<string> chunk, int extensionKind, List<ColdExtension> into)
    {
        var sql = new System.Text.StringBuilder(
            "SELECT entity_uri,extension_kind,payload,etag,offline_ttl,missing,expires_at,updated_at " +
            "FROM localized_extension_cache WHERE locale=$l AND extension_kind=$k AND entity_uri IN (");
        for (int i = 0; i < chunk.Count; i++) { if (i > 0) sql.Append(','); sql.Append("$u").Append(i); }
        sql.Append(");");
        lock (_readLock)
        {
            using var c = _read.CreateCommand();
            c.CommandText = sql.ToString();
            c.Parameters.AddWithValue("$l", _spotifyLocale);
            c.Parameters.AddWithValue("$k", extensionKind);
            for (int i = 0; i < chunk.Count; i++) c.Parameters.AddWithValue("$u" + i, chunk[i]);
            using var r = c.ExecuteReader();
            while (r.Read())
                into.Add(new ColdExtension(
                    r.GetString(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetFieldValue<byte[]>(2),
                    r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4), r.GetInt64(5) != 0,
                    r.GetInt64(6), r.GetInt64(7)));
        }
    }

    /// <summary>Stamp last_access at DAY granularity for a set of extension rows (v7 LRU). Day-truncated and guarded with
    /// `last_access &lt; $d`, so a row already touched today costs nothing and the writer never churns pages for re-reads.
    /// Mirrors <see cref="TouchEntities"/> — the read path itself never writes; the caller batches and calls this.</summary>
    public void TouchExtensions(IReadOnlyCollection<string> uris, int extensionKind, long day)
    {
        if (uris is null || uris.Count == 0 || _spotifyLocale is null) return;
        var buffer = new List<string>(Math.Min(uris.Count, MaxInParams));
        lock (_connLock)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var uri in uris)
            {
                if (string.IsNullOrEmpty(uri)) continue;
                buffer.Add(uri);
                if (buffer.Count == MaxInParams) { TouchExtensionChunkLocked(buffer, extensionKind, day, tx); buffer.Clear(); }
            }
            if (buffer.Count > 0) TouchExtensionChunkLocked(buffer, extensionKind, day, tx);
            tx.Commit();
        }
    }

    void TouchExtensionChunkLocked(List<string> chunk, int extensionKind, long day, SqliteTransaction tx)
    {
        var sql = new System.Text.StringBuilder(
            "UPDATE localized_extension_cache SET last_access=$d WHERE locale=$l AND extension_kind=$k AND last_access<$d AND entity_uri IN (");
        for (int i = 0; i < chunk.Count; i++) { if (i > 0) sql.Append(','); sql.Append("$u").Append(i); }
        sql.Append(");");
        using var c = _conn.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql.ToString();
        c.Parameters.AddWithValue("$d", day);
        c.Parameters.AddWithValue("$l", _spotifyLocale);
        c.Parameters.AddWithValue("$k", extensionKind);
        for (int i = 0; i < chunk.Count; i++) c.Parameters.AddWithValue("$u" + i, chunk[i]);
        c.ExecuteNonQuery();
    }

    public IEnumerable<ColdExtension> LoadAllExtensions(int limit = DefaultExtensionLimit)
    {
        var list = new List<ColdExtension>();
        if (_spotifyLocale is null) return list;
        lock (_readLock)
        {
            using var c = _read.CreateCommand();
            c.CommandText = "SELECT entity_uri,extension_kind,payload,etag,offline_ttl,missing,expires_at,updated_at " +
                            "FROM localized_extension_cache WHERE locale=$locale ORDER BY updated_at DESC" +
                            (limit > 0 ? " LIMIT $n;" : ";");
            c.Parameters.AddWithValue("$locale", _spotifyLocale);
            if (limit > 0) c.Parameters.AddWithValue("$n", limit);
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new ColdExtension(
                    r.GetString(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetFieldValue<byte[]>(2),
                    r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4), r.GetInt64(5) != 0,
                    r.GetInt64(6), r.GetInt64(7)));
        }
        return list;
    }


    /// <summary>The expired-extension sweep — a TTL tier like any other (§C.3), run by the RECURRING GC pass, no longer at
    /// open. `expires_at` + the 7 d ETag-revalidation grace; returns what it freed.
    ///
    /// BATCHED. It used to be one SELECT + one DELETE covering every expired row in a single transaction, which is fine at
    /// 70k rows and a multi-GB write transaction at 10M. Bounded batches match <c>GcDeleteLoop</c>'s discipline: each is
    /// atomic on its own, an abort between batches just leaves the rest for the next pass, and the accounting can never
    /// drift because the byte sum and the delete share a transaction.</summary>
    public (int Rows, long Bytes) SweepExpiredExtensionsNow(int batchRows = GcDeleteBatchRows, int maxBatches = int.MaxValue)
    {
        long cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ExtensionGraceSeconds;
        // expires_at>0 keeps a hand-seeded 0 ("no TTL recorded") row out of the sweep; every real row is now+ttl.
        const string Victims = "SELECT rowid FROM localized_extension_cache " +
                               "WHERE expires_at IS NOT NULL AND expires_at>0 AND expires_at<$c LIMIT $n";
        int rows = 0;
        long freed = 0;
        lock (_connLock)
        {
            for (int batch = 0; batch < maxBatches; batch++)
            {
                using var tx = _conn.BeginTransaction();
                long batchBytes;
                using (var c = _conn.CreateCommand())
                {
                    c.Transaction = tx;
                    c.CommandText = $"SELECT IFNULL(SUM(length(payload)),0) FROM localized_extension_cache WHERE rowid IN ({Victims});";
                    c.Parameters.AddWithValue("$c", cutoff);
                    c.Parameters.AddWithValue("$n", batchRows);
                    batchBytes = Convert.ToInt64(c.ExecuteScalar());
                }
                int deleted;
                using (var d = _conn.CreateCommand())
                {
                    d.Transaction = tx;
                    d.CommandText = $"DELETE FROM localized_extension_cache WHERE rowid IN ({Victims});";
                    d.Parameters.AddWithValue("$c", cutoff);
                    d.Parameters.AddWithValue("$n", batchRows);
                    deleted = d.ExecuteNonQuery();
                }
                if (deleted == 0) { tx.Rollback(); break; }
                if (batchBytes > 0) ApplyCacheBytesDeltaLocked(-batchBytes, tx);
                tx.Commit();
                rows += deleted;
                freed += batchBytes;
            }
        }
        return (rows, freed);
    }

    public void UpsertExtension(ColdExtension extension)
    {
        if (_spotifyLocale is null) return;
        _queue.Writer.TryWrite(WriteOp.ExtensionValue(extension));
    }

    public IEnumerable<ColdVideoAssoc> LoadAllVideoAssociations()
    {
        var list = new List<ColdVideoAssoc>(256);
        lock (_readLock)
        {
            using var c = _read.CreateCommand();
            c.CommandText = "SELECT uri, payload FROM video_assoc;";
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new ColdVideoAssoc(r.GetString(0), r.GetFieldValue<byte[]>(1)));
        }
        return list;
    }

    public IEnumerable<VideoOverride> LoadAllVideoOverrides()
    {
        var list = new List<VideoOverride>(32);   // a curation list, not a catalog — tens, not thousands
        lock (_readLock)
        {
            using var c = _read.CreateCommand();
            c.CommandText = "SELECT uri, path, id, duration_ms, size, mtime, added_at FROM video_override;";
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new VideoOverride(r.GetString(0), r.GetString(1), r.GetString(2),
                    r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6)));
        }
        return list;
    }

    public IEnumerable<ColdSaved> LoadAllSaved()
    {
        var list = new List<ColdSaved>(512);
        lock (_readLock)
        {
            using var c = _read.CreateCommand();
            c.CommandText = "SELECT set_id, item_uri, sync, added_at FROM collection_items WHERE account=$a;";
            c.Parameters.AddWithValue("$a", _account);
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new ColdSaved(r.GetString(0), r.GetString(1), (SyncState)r.GetInt32(2), r.GetInt64(3)));
        }
        return list;
    }

    public string? GetCollectionRevision(string setId)
    {
        lock (_readLock)
        {
            using var c = _read.CreateCommand();
            c.CommandText = "SELECT revision FROM collection_rev WHERE account=$a AND set_id=$s;";
            c.Parameters.AddWithValue("$a", _account);
            c.Parameters.AddWithValue("$s", setId);
            return c.ExecuteScalar() as string;   // null for no-row OR a NULL revision column
        }
    }

    // The rootlist revision lives in the shared meta(key,value) table as hex text under 'rootlist_rev' (no revision column
    // on the rootlist table). Synchronous, like ReplaceRootlist — a rootlist sync is a coarse op, not a hot per-item write.
    public byte[]? GetRootlistRevision()
    {
        lock (_readLock)
        {
            using var c = _read.CreateCommand();
            c.CommandText = "SELECT value FROM meta WHERE key='rootlist_rev';";
            return c.ExecuteScalar() is string s && s.Length > 0 ? Convert.FromHexString(s) : null;
        }
    }

    public void SetRootlistRevision(byte[]? rev)
    {
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            if (rev is null || rev.Length == 0)
                c.CommandText = "DELETE FROM meta WHERE key='rootlist_rev';";
            else
            {
                c.CommandText = "INSERT INTO meta(key,value) VALUES('rootlist_rev',$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
                c.Parameters.AddWithValue("$v", Convert.ToHexString(rev));
            }
            c.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<ColdPlaylistItem> LoadMembership(string playlistUri)
    {
        var list = new List<ColdPlaylistItem>(64);
        lock (_readLock)
        {
            using var c = _read.CreateCommand();
            c.CommandText = "SELECT item_id, item_uri, added_by, added_at, chart_status, chart_current_pos, chart_previous_pos, chart_rank " +
                             "FROM playlist_items WHERE playlist_uri=$p ORDER BY position;";
            c.Parameters.AddWithValue("$p", playlistUri);
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new ColdPlaylistItem(
                    r.IsDBNull(0) ? "" : r.GetString(0), r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? 0 : r.GetInt64(3),
                    r.IsDBNull(4) ? (byte)0 : (byte)r.GetInt64(4), r.IsDBNull(5) ? 0 : r.GetInt32(5),
                    r.IsDBNull(6) ? 0 : r.GetInt32(6), r.IsDBNull(7) ? 0 : r.GetInt64(7)));
        }
        return list;
    }

    public byte[]? GetPlaylistRevision(string playlistUri)
    {
        lock (_readLock)
        {
            using var c = _read.CreateCommand();
            c.CommandText = "SELECT base_rev FROM playlists WHERE uri=$p;";
            c.Parameters.AddWithValue("$p", playlistUri);
            return c.ExecuteScalar() as byte[];
        }
    }

    // Synchronous + atomic: a bulk membership replace is a coarse op (one playlist sync), not a hot per-item write, so it
    // runs in its own transaction rather than through the per-entity write-behind queue. Delete-all + reinsert + bump rev,
    // all-or-nothing, so a torn write can never leave a half-applied membership.
    public void ReplaceMembership(string playlistUri, IReadOnlyList<ColdPlaylistItem> rows, byte[]? baseRev)
    {
        lock (_connLock)
        {
            using var tx = _conn.BeginTransaction();
            using (var del = _conn.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM playlist_items WHERE playlist_uri=$p;"; del.Parameters.AddWithValue("$p", playlistUri); del.ExecuteNonQuery(); }
            using (var ins = _conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO playlist_items(playlist_uri,position,item_id,item_uri,added_by,added_at," +
                                   "chart_status,chart_current_pos,chart_previous_pos,chart_rank) " +
                                   "VALUES($p,$pos,$id,$u,$by,$at,$cs,$ccp,$cpp,$cr);";
                var pp = ins.Parameters.Add("$p", SqliteType.Text); pp.Value = playlistUri;
                var ppos = ins.Parameters.Add("$pos", SqliteType.Integer);
                var pid = ins.Parameters.Add("$id", SqliteType.Text);
                var pu = ins.Parameters.Add("$u", SqliteType.Text);
                var pby = ins.Parameters.Add("$by", SqliteType.Text);
                var pat = ins.Parameters.Add("$at", SqliteType.Integer);
                var pcs = ins.Parameters.Add("$cs", SqliteType.Integer);
                var pccp = ins.Parameters.Add("$ccp", SqliteType.Integer);
                var pcpp = ins.Parameters.Add("$cpp", SqliteType.Integer);
                var pcr = ins.Parameters.Add("$cr", SqliteType.Integer);
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    ppos.Value = i;
                    pid.Value = string.IsNullOrEmpty(row.ItemId) ? DBNull.Value : row.ItemId;
                    pu.Value = row.ItemUri;
                    pby.Value = (object?)row.AddedBy ?? DBNull.Value;
                    pat.Value = row.AddedAt;
                    pcs.Value = row.ChartStatus;
                    pccp.Value = row.ChartCurrentPos;
                    pcpp.Value = row.ChartPreviousPos;
                    pcr.Value = row.ChartRank;
                    ins.ExecuteNonQuery();
                }
            }
            using (var rev = _conn.CreateCommand())
            {
                rev.Transaction = tx;
                // adopted_at (v6) is stamped on EVERY adoption — it is the membership GC's "last time this playlist was
                // actually synced" clock, so re-opening a foreign playlist keeps it alive for another 14 days.
                rev.CommandText = "INSERT INTO playlists(uri,base_rev,adopted_at) VALUES($p,$r,$t) " +
                                  "ON CONFLICT(uri) DO UPDATE SET base_rev=excluded.base_rev, adopted_at=excluded.adopted_at;";
                rev.Parameters.AddWithValue("$p", playlistUri);
                rev.Parameters.AddWithValue("$r", (object?)baseRev ?? DBNull.Value);
                rev.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                rev.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public IReadOnlyList<ColdRootlistEntry> LoadRootlist()
    {
        var list = new List<ColdRootlistEntry>(64);
        lock (_readLock)
        {
            using var c = _read.CreateCommand();
            c.CommandText = "SELECT position, kind, uri, group_name, depth, added_at FROM rootlist WHERE account=$a ORDER BY position;";
            c.Parameters.AddWithValue("$a", _account);
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new ColdRootlistEntry(
                    r.GetInt32(0), r.IsDBNull(1) ? 0 : r.GetInt32(1), r.IsDBNull(2) ? "" : r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? 0 : r.GetInt32(4),
                    r.IsDBNull(5) ? 0L : r.GetInt64(5)));
        }
        return list;
    }

    public void ReplaceRootlist(IReadOnlyList<ColdRootlistEntry> entries)
    {
        lock (_connLock)
        {
            using var tx = _conn.BeginTransaction();
            using (var del = _conn.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM rootlist WHERE account=$a;"; del.Parameters.AddWithValue("$a", _account); del.ExecuteNonQuery(); }
            using (var ins = _conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO rootlist(account,position,kind,uri,group_name,depth,added_at) VALUES($a,$pos,$k,$u,$g,$d,$t);";
                var pa = ins.Parameters.Add("$a", SqliteType.Text); pa.Value = _account;
                var ppos = ins.Parameters.Add("$pos", SqliteType.Integer);
                var pk = ins.Parameters.Add("$k", SqliteType.Integer);
                var pu = ins.Parameters.Add("$u", SqliteType.Text);
                var pg = ins.Parameters.Add("$g", SqliteType.Text);
                var pd = ins.Parameters.Add("$d", SqliteType.Integer);
                var pt = ins.Parameters.Add("$t", SqliteType.Integer);
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    ppos.Value = e.Position;
                    pk.Value = e.Kind;
                    pu.Value = e.Uri;
                    pg.Value = (object?)e.GroupName ?? DBNull.Value;
                    pd.Value = e.Depth;
                    pt.Value = e.AddedAtMs;
                    ins.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
    }

    public void UpsertEntity(string uri, EntityKind kind, byte[] payload) => _queue.Writer.TryWrite(WriteOp.Entity(uri, (int)kind, payload));
    public void UpsertVideoAssociation(string uri, byte[] payload) => _queue.Writer.TryWrite(WriteOp.VideoAssoc(uri, payload));
    public void UpsertVideoOverride(VideoOverride o) => _queue.Writer.TryWrite(WriteOp.VideoOverrideSet(o));
    public void DeleteVideoOverride(string uri) => _queue.Writer.TryWrite(WriteOp.VideoOverrideDelete(uri));
    public void UpsertSaved(string setId, string uri, bool saved, SyncState sync, long addedAtMs = 0) => _queue.Writer.TryWrite(WriteOp.Saved(setId, uri, saved, (int)sync, addedAtMs));
    public void SetCollectionRevision(string setId, string? revision, long syncedAt) => _queue.Writer.TryWrite(WriteOp.Revision(setId, revision, syncedAt));

    public void Flush()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Bounded: a pathological backlog (or a writer task that stopped) must never wedge app shutdown on this call.
        if (_queue.Writer.TryWrite(WriteOp.FlushMarker(done))) { try { done.Task.Wait(TimeSpan.FromSeconds(30)); } catch { } }
    }

    async Task WriteLoopAsync()
    {
        var batch = new List<WriteOp>(512);
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Count < 2000 && _queue.Reader.TryRead(out var op)) batch.Add(op);
            try { WriteBatch(batch); }
            catch { /* a cache-write failure is non-fatal: the data is in memory + re-fetchable */ }
            foreach (var op in batch) op.Done?.SetResult();   // complete any flush markers (after commit)
        }
    }

    void EnsureCommands()
    {
        if (_entityCmd != null) return;
        _entityCmd = _conn.CreateCommand();
        _entityCmd.CommandText =
            "INSERT INTO entity(uri,locale,kind,title,subtitle,image_url,duration_ms,flags,album_uri,fmt,size,updated_at,last_access,payload) " +
            "VALUES($u,$l,$k,$ti,$sub,$img,$dur,$fl,$al,$fmt,$sz,$t,$la,$p) " +
            "ON CONFLICT(uri,locale) DO UPDATE SET kind=excluded.kind,title=excluded.title,subtitle=excluded.subtitle," +
            "image_url=excluded.image_url,duration_ms=excluded.duration_ms,flags=excluded.flags,album_uri=excluded.album_uri," +
            "fmt=excluded.fmt,size=excluded.size,updated_at=excluded.updated_at,last_access=excluded.last_access,payload=excluded.payload;";
        _eu = _entityCmd.Parameters.Add("$u", SqliteType.Text);
        _el = _entityCmd.Parameters.Add("$l", SqliteType.Text); _el.Value = _localeKey;
        _ek = _entityCmd.Parameters.Add("$k", SqliteType.Integer);
        _eti = _entityCmd.Parameters.Add("$ti", SqliteType.Text);
        _esu = _entityCmd.Parameters.Add("$sub", SqliteType.Text);
        _eim = _entityCmd.Parameters.Add("$img", SqliteType.Text);
        _edu = _entityCmd.Parameters.Add("$dur", SqliteType.Integer);
        _efl = _entityCmd.Parameters.Add("$fl", SqliteType.Integer);
        _eal = _entityCmd.Parameters.Add("$al", SqliteType.Text);
        _efm = _entityCmd.Parameters.Add("$fmt", SqliteType.Integer);
        _esz = _entityCmd.Parameters.Add("$sz", SqliteType.Integer);
        _et = _entityCmd.Parameters.Add("$t", SqliteType.Integer);
        _ela = _entityCmd.Parameters.Add("$la", SqliteType.Integer);
        _ep = _entityCmd.Parameters.Add("$p", SqliteType.Blob);

        // cache_bytes is a RUNNING counter maintained in the same transaction as the row it accounts for: the delta needs
        // the row's previous size, and `size` sits before `payload` so this probe never touches an overflow page.
        _sizeProbeCmd = _conn.CreateCommand();
        _sizeProbeCmd.CommandText = "SELECT size FROM entity WHERE uri=$u AND locale=$l;";
        _zpu = _sizeProbeCmd.Parameters.Add("$u", SqliteType.Text);
        _zpl = _sizeProbeCmd.Parameters.Add("$l", SqliteType.Text); _zpl.Value = _localeKey;

        _refDelCmd = _conn.CreateCommand();
        _refDelCmd.CommandText = "DELETE FROM entity_refs WHERE parent_uri=$p;";
        _rdp = _refDelCmd.Parameters.Add("$p", SqliteType.Text);

        _refInsCmd = _conn.CreateCommand();
        _refInsCmd.CommandText = "INSERT OR IGNORE INTO entity_refs(parent_uri,child_uri) VALUES($p,$c);";
        _rip = _refInsCmd.Parameters.Add("$p", SqliteType.Text);
        _ric = _refInsCmd.Parameters.Add("$c", SqliteType.Text);

        _cacheDeltaCmd = _conn.CreateCommand();
        _cacheDeltaCmd.CommandText =
            $"INSERT INTO meta(key,value) VALUES('{MetaCacheBytes}', CAST(MAX(0,$d) AS TEXT)) " +
            "ON CONFLICT(key) DO UPDATE SET value=CAST(MAX(0, CAST(meta.value AS INTEGER) + $d) AS TEXT);";
        _cbd = _cacheDeltaCmd.Parameters.Add("$d", SqliteType.Integer);

        if (_spotifyLocale is not null)
        {
            _extSizeProbeCmd = _conn.CreateCommand();
            _extSizeProbeCmd.CommandText = "SELECT IFNULL(length(payload),0) FROM localized_extension_cache WHERE entity_uri=$u AND locale=$l AND extension_kind=$k;";
            _zxu = _extSizeProbeCmd.Parameters.Add("$u", SqliteType.Text);
            _extSizeProbeCmd.Parameters.Add("$l", SqliteType.Text).Value = _spotifyLocale;
            _zxk = _extSizeProbeCmd.Parameters.Add("$k", SqliteType.Integer);

            _extensionCmd = _conn.CreateCommand();
            _extensionCmd.CommandText = "INSERT INTO localized_extension_cache(entity_uri,locale,extension_kind,payload,etag,offline_ttl,missing,expires_at,updated_at) " +
                "VALUES($u,$l,$k,$p,$e,$o,$m,$x,$t) ON CONFLICT(entity_uri,locale,extension_kind) DO UPDATE SET " +
                "payload=excluded.payload,etag=excluded.etag,offline_ttl=excluded.offline_ttl,missing=excluded.missing," +
                "expires_at=excluded.expires_at,updated_at=excluded.updated_at;";
            _xu = _extensionCmd.Parameters.Add("$u", SqliteType.Text);
            _xl = _extensionCmd.Parameters.Add("$l", SqliteType.Text);
            _xk = _extensionCmd.Parameters.Add("$k", SqliteType.Integer);
            _xp = _extensionCmd.Parameters.Add("$p", SqliteType.Blob);
            _xe = _extensionCmd.Parameters.Add("$e", SqliteType.Text);
            _xo = _extensionCmd.Parameters.Add("$o", SqliteType.Integer);
            _xm = _extensionCmd.Parameters.Add("$m", SqliteType.Integer);
            _xx = _extensionCmd.Parameters.Add("$x", SqliteType.Integer);
            _xt = _extensionCmd.Parameters.Add("$t", SqliteType.Integer);
        }

        _videoCmd = _conn.CreateCommand();
        _videoCmd.CommandText = "INSERT INTO video_assoc(uri,payload) VALUES($u,$p) ON CONFLICT(uri) DO UPDATE SET payload=excluded.payload;";
        _vu = _videoCmd.Parameters.Add("$u", SqliteType.Text);
        _vp = _videoCmd.Parameters.Add("$p", SqliteType.Blob);

        // User video overrides: uri is the PK, so the attach-onto-an-already-overridden-playable case IS this upsert.
        _ovrUpCmd = _conn.CreateCommand();
        _ovrUpCmd.CommandText = "INSERT INTO video_override(uri,path,id,duration_ms,size,mtime,added_at) VALUES($u,$p,$i,$d,$s,$m,$a) " +
            "ON CONFLICT(uri) DO UPDATE SET path=excluded.path, id=excluded.id, duration_ms=excluded.duration_ms, " +
            "size=excluded.size, mtime=excluded.mtime, added_at=excluded.added_at;";
        _ou = _ovrUpCmd.Parameters.Add("$u", SqliteType.Text);
        _op = _ovrUpCmd.Parameters.Add("$p", SqliteType.Text);
        _oi = _ovrUpCmd.Parameters.Add("$i", SqliteType.Text);
        _od = _ovrUpCmd.Parameters.Add("$d", SqliteType.Integer);
        _os = _ovrUpCmd.Parameters.Add("$s", SqliteType.Integer);
        _om = _ovrUpCmd.Parameters.Add("$m", SqliteType.Integer);
        _oa = _ovrUpCmd.Parameters.Add("$a", SqliteType.Integer);

        _ovrDelCmd = _conn.CreateCommand();
        _ovrDelCmd.CommandText = "DELETE FROM video_override WHERE uri=$u;";
        _oxu = _ovrDelCmd.Parameters.Add("$u", SqliteType.Text);

        _savedUpCmd = _conn.CreateCommand();
        // added_at: a non-zero incoming timestamp wins; 0 preserves whatever is stored (the optimistic/fold writers don't
        // know the server timestamp — the delta/paging apply does).
        _savedUpCmd.CommandText = "INSERT INTO collection_items(account,set_id,item_uri,added_at,position,sync) VALUES($a,$s,$u,$t,NULL,$y) " +
                                  "ON CONFLICT(account,set_id,item_uri) DO UPDATE SET sync=excluded.sync, " +
                                  "added_at=CASE WHEN excluded.added_at!=0 THEN excluded.added_at ELSE collection_items.added_at END;";
        _sa = _savedUpCmd.Parameters.Add("$a", SqliteType.Text);
        _ss = _savedUpCmd.Parameters.Add("$s", SqliteType.Text);
        _su = _savedUpCmd.Parameters.Add("$u", SqliteType.Text);
        _sy = _savedUpCmd.Parameters.Add("$y", SqliteType.Integer);
        _st = _savedUpCmd.Parameters.Add("$t", SqliteType.Integer);

        _savedDelCmd = _conn.CreateCommand();
        _savedDelCmd.CommandText = "DELETE FROM collection_items WHERE account=$a AND set_id=$s AND item_uri=$u;";
        _da = _savedDelCmd.Parameters.Add("$a", SqliteType.Text);
        _ds = _savedDelCmd.Parameters.Add("$s", SqliteType.Text);
        _du = _savedDelCmd.Parameters.Add("$u", SqliteType.Text);

        _revCmd = _conn.CreateCommand();
        _revCmd.CommandText = "INSERT INTO collection_rev(account,set_id,revision,synced_at) VALUES($a,$s,$r,$t) " +
                              "ON CONFLICT(account,set_id) DO UPDATE SET revision=excluded.revision, synced_at=excluded.synced_at;";
        _ra = _revCmd.Parameters.Add("$a", SqliteType.Text);
        _rs = _revCmd.Parameters.Add("$s", SqliteType.Text);
        _rr = _revCmd.Parameters.Add("$r", SqliteType.Text);
        _rt = _revCmd.Parameters.Add("$t", SqliteType.Integer);
    }

    void WriteBatch(List<WriteOp> batch)
    {
        lock (_connLock)
        {
            EnsureCommands();
            using var tx = _conn.BeginTransaction();
            _entityCmd!.Transaction = tx;
            _sizeProbeCmd!.Transaction = tx;
            _refDelCmd!.Transaction = tx;
            _refInsCmd!.Transaction = tx;
            _cacheDeltaCmd!.Transaction = tx;
            if (_extensionCmd is not null) _extensionCmd.Transaction = tx;
            if (_extSizeProbeCmd is not null) _extSizeProbeCmd.Transaction = tx;
            _videoCmd!.Transaction = tx;
            _ovrUpCmd!.Transaction = tx;
            _ovrDelCmd!.Transaction = tx;
            _savedUpCmd!.Transaction = tx;
            _savedDelCmd!.Transaction = tx;
            _revCmd!.Transaction = tx;
            long cacheDelta = 0;
            foreach (var op in batch)
            {
                switch (op.Op)
                {
                    case OpKind.Entity:
                        cacheDelta += WriteEntityRowLocked(op.A, (EntityKind)op.Kind, op.Payload!, op.L);
                        break;
                    case OpKind.Extension when _extensionCmd is not null && op.Extension is { } x:
                        if (_extSizeProbeCmd is not null)
                        {
                            _zxu.Value = x.EntityUri; _zxk.Value = x.ExtensionKind;
                            cacheDelta += (x.Payload?.Length ?? 0) - Convert.ToInt64(_extSizeProbeCmd.ExecuteScalar() ?? 0L);
                        }
                        _xu.Value = x.EntityUri;
                        _xl.Value = _spotifyLocale!;
                        _xk.Value = x.ExtensionKind;
                        _xp.Value = (object?)x.Payload ?? DBNull.Value;
                        _xe.Value = (object?)x.Etag ?? DBNull.Value;
                        _xo.Value = x.OfflineTtlSeconds;
                        _xm.Value = x.Missing ? 1 : 0;
                        _xx.Value = x.ExpiresAtUnixSeconds;
                        _xt.Value = x.UpdatedAtUnixSeconds;
                        _extensionCmd.ExecuteNonQuery();
                        break;
                    case OpKind.VideoAssoc: _vu.Value = op.A; _vp.Value = op.Payload!; _videoCmd.ExecuteNonQuery(); break;
                    case OpKind.VideoOverrideSet when op.Override is { } o:
                        _ou.Value = o.Uri; _op.Value = o.Path; _oi.Value = o.Id;
                        _od.Value = o.DurationMs; _os.Value = o.SizeBytes; _om.Value = o.MTimeUnix; _oa.Value = o.AddedAtUnix;
                        _ovrUpCmd.ExecuteNonQuery();
                        break;
                    case OpKind.VideoOverrideDelete: _oxu.Value = op.A; _ovrDelCmd.ExecuteNonQuery(); break;
                    case OpKind.SavedSet: _sa.Value = _account; _ss.Value = op.A; _su.Value = op.B!; _sy.Value = op.Kind; _st.Value = op.L; _savedUpCmd.ExecuteNonQuery(); break;
                    case OpKind.SavedRemove: _da.Value = _account; _ds.Value = op.A; _du.Value = op.B!; _savedDelCmd.ExecuteNonQuery(); break;
                    case OpKind.Revision: _ra.Value = _account; _rs.Value = op.A; _rr.Value = (object?)op.B ?? DBNull.Value; _rt.Value = op.L; _revCmd.ExecuteNonQuery(); break;
                    case OpKind.Flush: break;
                }
            }
            if (cacheDelta != 0) ApplyCacheBytesDeltaLocked(cacheDelta, tx);
            tx.Commit();
        }
    }

    // One entity upsert: thin-column extraction, fmt=1 framing, cache accounting, and the pin-closure refs for a track.
    // Returns the cache_bytes delta. Caller holds _connLock and has attached the batch transaction to the commands.
    long WriteEntityRowLocked(string uri, EntityKind kind, byte[] payload, long now)
    {
        _zpu.Value = uri;
        long oldSize = Convert.ToInt64(_sizeProbeCmd!.ExecuteScalar() ?? 0L);

        bool parsed = EntityThinExtractor.TryExtract(payload, kind, out var thin);
        var stored = PayloadCodec.Encode(payload, PayloadCodec.FmtZstd);

        _eu.Value = uri;
        _ek.Value = (int)kind;
        _eti.Value = (object?)thin.Title ?? DBNull.Value;
        _esu.Value = (object?)thin.Subtitle ?? DBNull.Value;
        _eim.Value = (object?)thin.ImageUrl ?? DBNull.Value;
        _edu.Value = thin.DurationMs is { } d ? d : DBNull.Value;
        _efl.Value = thin.Flags;
        _eal.Value = (object?)thin.AlbumUri ?? DBNull.Value;
        _efm.Value = PayloadCodec.FormatOf(stored);   // read back off the frame, so the column can never disagree with it
        _esz.Value = stored.Length;
        _et.Value = now;
        _ela.Value = now;
        _ep.Value = stored;
        _entityCmd!.ExecuteNonQuery();

        // The pin closure (pinned track → its album/artists) is REPLACED per track write, so a re-hydration that changes
        // the album never leaves a stale edge behind. Non-track kinds own no outgoing edges.
        if (kind == EntityKind.Track && parsed)
        {
            _rdp.Value = uri;
            _refDelCmd!.ExecuteNonQuery();
            if (thin.Refs is { Count: > 0 } refs)
            {
                _rip.Value = uri;
                for (int i = 0; i < refs.Count; i++) { _ric.Value = refs[i]; _refInsCmd!.ExecuteNonQuery(); }
            }
        }
        return stored.Length - oldSize;
    }

    void ApplyCacheBytesDeltaLocked(long delta, SqliteTransaction? tx)
    {
        if (delta == 0) return;
        if (_cacheDeltaCmd is null)
        {
            using var c = _conn.CreateCommand();
            c.Transaction = tx;
            c.CommandText = $"INSERT INTO meta(key,value) VALUES('{MetaCacheBytes}', CAST(MAX(0,$d) AS TEXT)) " +
                            "ON CONFLICT(key) DO UPDATE SET value=CAST(MAX(0, CAST(meta.value AS INTEGER) + $d) AS TEXT);";
            c.Parameters.AddWithValue("$d", delta);
            c.ExecuteNonQuery();
            return;
        }
        _cacheDeltaCmd.Transaction = tx;
        _cbd.Value = delta;
        _cacheDeltaCmd.ExecuteNonQuery();
    }

    // ── IMutationOutbox (durable, synchronous — pending intents must be on disk before the call returns) ──
    public IReadOnlyList<OutboxOp> Load()
    {
        var list = new List<OutboxOp>();
        lock (_readLock)
        {
            using var c = _read.CreateCommand();
            c.CommandText = "SELECT id, type, entity_key, set_id, target_saved, op, base_rev, attempts, parent_folder FROM outbox ORDER BY id;";
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                long id = r.GetInt64(0);
                string type = r.GetString(1);
                string entityKey = r.GetString(2);
                string setId = r.IsDBNull(3) ? "" : r.GetString(3);
                bool targetSaved = !r.IsDBNull(4) && r.GetInt64(4) != 0;
                byte[]? opBlob = r.IsDBNull(5) ? null : r.GetFieldValue<byte[]>(5);
                byte[]? baseRev = r.IsDBNull(6) ? null : r.GetFieldValue<byte[]>(6);
                int attempts = r.IsDBNull(7) ? 0 : r.GetInt32(7);
                string? parentFolder = r.IsDBNull(8) ? null : r.GetString(8);
                IReadOnlyList<PlaylistOp>? ops = null;
                // Both op-carrying types re-hydrate their ordered ops from the blob: "oprebase" (playlist edits) and
                // "create" (the queued create's UPDATE_LIST{name}).
                if ((type == "oprebase" || type == "create") && opBlob is not null)
                {
                    var parsed = PlaylistWireMapper.ParseOutboxBlob(opBlob);
                    ops = parsed.Ops;
                    baseRev ??= parsed.BaseRev;
                }
                list.Add(new OutboxOp(id, type, entityKey, setId, targetSaved, id, attempts, ops, baseRev, parentFolder));

            }
        }
        return list;
    }

    public void Save(OutboxOp op)
    {
        byte[]? opBlob = (op.Type == "oprebase" || op.Type == "create") && op.Ops is not null ? PlaylistWireMapper.BuildOutboxBlob(op.BaseRev, op.Ops) : null;
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "INSERT INTO outbox(id,type,entity_key,set_id,target_saved,op,base_rev,attempts,parent_folder) VALUES($i,$t,$e,$s,$ts,$op,$br,$a,$pf) " +
                            "ON CONFLICT(id) DO UPDATE SET attempts=excluded.attempts, target_saved=excluded.target_saved, op=excluded.op, base_rev=excluded.base_rev, parent_folder=excluded.parent_folder;";
            c.Parameters.AddWithValue("$i", op.Id);
            c.Parameters.AddWithValue("$t", op.Type);
            c.Parameters.AddWithValue("$e", op.EntityKey);
            c.Parameters.AddWithValue("$s", op.SetId);
            c.Parameters.AddWithValue("$ts", op.TargetSaved ? 1 : 0);
            c.Parameters.AddWithValue("$op", (object?)opBlob ?? DBNull.Value);
            c.Parameters.AddWithValue("$br", (object?)op.BaseRev ?? DBNull.Value);
            c.Parameters.AddWithValue("$a", op.Attempts);
            c.Parameters.AddWithValue("$pf", (object?)op.ParentFolderId ?? DBNull.Value);
            c.ExecuteNonQuery();
        }
    }

    public void Remove(long id)
    {
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "DELETE FROM outbox WHERE id=$i;";
            c.Parameters.AddWithValue("$i", id);
            c.ExecuteNonQuery();
        }
    }

    public void DeadLetter(OutboxOp op, string reason)
    {
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "INSERT OR REPLACE INTO dead_letter(id,type,entity_key,reason,created_at) VALUES($i,$t,$e,$r,0);";
            c.Parameters.AddWithValue("$i", op.Id);
            c.Parameters.AddWithValue("$t", op.Type);
            c.Parameters.AddWithValue("$e", op.EntityKey);
            c.Parameters.AddWithValue("$r", reason);
            c.ExecuteNonQuery();
        }
    }

    // ── schema v5 APIs (wired by the later waves: GC, warm set, recent surfaces, artist split) ───────────────────────

    /// <summary>Batched last-access stamp (DAY granularity — §C.5). Writer-side, chunked under the SQLite parameter cap
    /// so a 10k-uri flush is a handful of statements in one transaction rather than 10k round-trips.</summary>
    public void TouchEntities(IReadOnlyCollection<string> uris, long day)
    {
        if (uris is null || uris.Count == 0) return;
        var buffer = new List<string>(Math.Min(uris.Count, MaxInParams));
        lock (_connLock)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var uri in uris)
            {
                if (string.IsNullOrEmpty(uri)) continue;
                buffer.Add(uri);
                if (buffer.Count == MaxInParams) { TouchChunkLocked(buffer, day, tx); buffer.Clear(); }
            }
            if (buffer.Count > 0) TouchChunkLocked(buffer, day, tx);
            tx.Commit();
        }
    }

    void TouchChunkLocked(List<string> chunk, long day, SqliteTransaction tx)
    {
        var sql = new System.Text.StringBuilder("UPDATE entity SET last_access=$d WHERE locale=$l AND uri IN (");
        for (int i = 0; i < chunk.Count; i++) { if (i > 0) sql.Append(','); sql.Append("$u").Append(i); }
        sql.Append(");");
        using var c = _conn.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql.ToString();
        c.Parameters.AddWithValue("$d", day);
        c.Parameters.AddWithValue("$l", _localeKey);
        for (int i = 0; i < chunk.Count; i++) c.Parameters.AddWithValue("$u" + i, chunk[i]);
        c.ExecuteNonQuery();
    }

    /// <summary>Record a detail-page open. LRU-capped at 50 rows IN the same transaction as the insert, so the pin reason
    /// can never grow unbounded.</summary>
    public void UpsertRecentSurface(string uri, int kind, long nowUnixSeconds)
    {
        if (string.IsNullOrEmpty(uri)) return;
        lock (_connLock)
        {
            using var tx = _conn.BeginTransaction();
            using (var c = _conn.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = "INSERT INTO recent_surfaces(uri,kind,last_opened) VALUES($u,$k,$t) " +
                                "ON CONFLICT(uri) DO UPDATE SET kind=excluded.kind,last_opened=excluded.last_opened;";
                c.Parameters.AddWithValue("$u", uri);
                c.Parameters.AddWithValue("$k", kind);
                c.Parameters.AddWithValue("$t", nowUnixSeconds);
                c.ExecuteNonQuery();
            }
            using (var trim = _conn.CreateCommand())
            {
                trim.Transaction = tx;
                trim.CommandText = "DELETE FROM recent_surfaces WHERE uri NOT IN " +
                                   "(SELECT uri FROM recent_surfaces ORDER BY last_opened DESC, uri ASC LIMIT $n);";
                trim.Parameters.AddWithValue("$n", RecentSurfaceCap);
                trim.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>The recently-opened surfaces, newest first (at most 50).</summary>
    public IReadOnlyList<ColdRecentSurface> LoadRecentSurfaces()
    {
        var list = new List<ColdRecentSurface>(RecentSurfaceCap);
        lock (_readLock)
        {
            using var c = _read.CreateCommand();
            c.CommandText = "SELECT uri, kind, last_opened FROM recent_surfaces ORDER BY last_opened DESC, uri ASC;";
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new ColdRecentSurface(r.GetString(0), r.IsDBNull(1) ? 0 : r.GetInt32(1), r.IsDBNull(2) ? 0 : r.GetInt64(2)));
        }
        return list;
    }

    /// <summary>The fat artist facets for <paramref name="uri"/> (decoded JSON), or null when never fetched.</summary>
    public ColdArtistOverview? GetArtistOverview(string uri)
    {
        lock (_readLock)
        {
            using var c = _read.CreateCommand();
            c.CommandText = "SELECT locale, payload, fetched_at FROM artist_overview WHERE uri=$u;";
            c.Parameters.AddWithValue("$u", uri);
            using var r = c.ExecuteReader();
            if (!r.Read()) return null;
            return new ColdArtistOverview(uri, r.GetString(0),
                PayloadCodec.Decode(r.IsDBNull(1) ? null : r.GetFieldValue<byte[]>(1)), r.GetInt64(2));
        }
    }

    /// <summary>Persist the artist overview facets (fmt=1). Synchronous on the writer — an overview write is a page-open
    /// event, not a hot per-row write.</summary>
    public void UpsertArtistOverview(string uri, string locale, byte[] payloadJson, long nowUnixSeconds)
    {
        if (string.IsNullOrEmpty(uri)) return;
        var stored = PayloadCodec.Encode(payloadJson ?? Array.Empty<byte>(), PayloadCodec.FmtZstd);
        lock (_connLock)
        {
            using var tx = _conn.BeginTransaction();
            long oldSize;
            using (var probe = _conn.CreateCommand())
            {
                probe.Transaction = tx;
                probe.CommandText = "SELECT size FROM artist_overview WHERE uri=$u;";
                probe.Parameters.AddWithValue("$u", uri);
                oldSize = Convert.ToInt64(probe.ExecuteScalar() ?? 0L);
            }
            using (var c = _conn.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = "INSERT INTO artist_overview(uri,locale,fmt,payload,size,fetched_at,last_access) " +
                                "VALUES($u,$l,$f,$p,$s,$t,$t) ON CONFLICT(uri) DO UPDATE SET locale=excluded.locale," +
                                "fmt=excluded.fmt,payload=excluded.payload,size=excluded.size,fetched_at=excluded.fetched_at," +
                                "last_access=excluded.last_access;";
                c.Parameters.AddWithValue("$u", uri);
                c.Parameters.AddWithValue("$l", string.IsNullOrEmpty(locale) ? _localeKey : locale);
                c.Parameters.AddWithValue("$f", PayloadCodec.FormatOf(stored));
                c.Parameters.AddWithValue("$p", stored);
                c.Parameters.AddWithValue("$s", stored.Length);
                c.Parameters.AddWithValue("$t", nowUnixSeconds);
                c.ExecuteNonQuery();
            }
            ApplyCacheBytesDeltaLocked(stored.Length - oldSize, tx);
            tx.Commit();
        }
    }

    /// <summary>Replace the pin-closure edges out of <paramref name="parentUri"/> (delete-all + reinsert, atomic).</summary>
    public void ReplaceEntityRefs(string parentUri, IEnumerable<string> children)
    {
        if (string.IsNullOrEmpty(parentUri)) return;
        lock (_connLock)
        {
            using var tx = _conn.BeginTransaction();
            using (var del = _conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM entity_refs WHERE parent_uri=$p;";
                del.Parameters.AddWithValue("$p", parentUri);
                del.ExecuteNonQuery();
            }
            if (children is not null)
            {
                using var ins = _conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT OR IGNORE INTO entity_refs(parent_uri,child_uri) VALUES($p,$c);";
                var pp = ins.Parameters.Add("$p", SqliteType.Text); pp.Value = parentUri;
                var pc = ins.Parameters.Add("$c", SqliteType.Text);
                foreach (var child in children)
                {
                    if (string.IsNullOrEmpty(child)) continue;
                    pc.Value = child;
                    ins.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
    }

    // ── the offline-search candidate corpus (Addendum A4) ────────────────────────────────────────────────────────────
    // At most THREE set-based statements per scope, all on the READ connection and all THIN: `payload` is the LAST column
    // of an `entity` row and is never selected here, so streaming a whole library's candidates touches no overflow page.
    // There is deliberately NO `LIKE`/`instr` pre-filter — SQLite `NOCASE` folds ASCII only, so it is NARROWER than the
    // `OrdinalIgnoreCase` the UI matches with, and a pre-pass would silently DROP non-ASCII hits (Ω/ω, İ, ı). SQL answers
    // "what is in scope"; C# answers "what matches, and where" (see LibrarySearchIndex).
    const string SavedArtistsSql = "SELECT item_uri FROM collection_items WHERE account=$a AND set_id='artists'";
    const string SavedAlbumsSql = "SELECT item_uri FROM collection_items WHERE account=$a AND set_id='albums'";
    const string ThinColumnsSql = "SELECT uri,kind,title,subtitle,image_url,duration_ms,flags,album_uri FROM entity";

    public ColdCandidates LoadLibraryCandidates(ColdCandidateScope scope)
    {
        int track = (int)EntityKind.Track, album = (int)EntityKind.Album, artist = (int)EntityKind.Artist;
        switch (scope)
        {
            case ColdCandidateScope.LibraryTracks:
            {
                // Everything the offline QueryTracks may see: the liked set plus every adopted playlist's membership.
                // `playlist_items` is scanned whole on purpose — the membership GC already bounds it to the playlists
                // the user actually keeps, and a per-playlist fan-out would be thousands of statements per keystroke.
                var rows = QueryThin($"""
                    {ThinColumnsSql}
                    WHERE locale=$l AND kind=$k AND (
                        uri IN (SELECT item_uri FROM collection_items WHERE account=$a AND set_id='liked')
                        OR uri IN (SELECT item_uri FROM playlist_items));
                    """, track);
                return new ColdCandidates(Array.Empty<ColdThinRow>(), Array.Empty<ColdThinRow>(), rows, Array.Empty<ColdRefEdge>());
            }

            case ColdCandidateScope.SavedAlbums:
            {
                var rows = QueryThin($"""
                    WITH alb(uri) AS ({SavedAlbumsSql})
                    {ThinColumnsSql}
                      WHERE locale=$l AND kind={album} AND uri IN (SELECT uri FROM alb)
                    UNION ALL
                    {ThinColumnsSql}
                      WHERE locale=$l AND kind=$k AND album_uri IN (SELECT uri FROM alb);
                    """, track);
                return Split(Array.Empty<ColdThinRow>(), rows, Array.Empty<ColdRefEdge>());
            }

            default:
            {
                // (1) the followed artists themselves — a saved artist with no `entity` row simply has no candidate here
                //     (the caller still walks it through the store, whose cold fallback covers the row-exists case).
                var roots = QueryThin($"""
                    {ThinColumnsSql} WHERE locale=$l AND kind=$k AND uri IN ({SavedArtistsSql});
                    """, artist);
                // (2) the artist↔album edges, BOTH directions unioned and normalized artist-first: artist→albums comes
                //     from the overview projection (ArtistSplit.ReferencedAlbums), album→artists from every album
                //     persist, and either leg alone misses part of a saved artist's offline discography.
                var edges = QueryEdges($"""
                    SELECT parent_uri, child_uri FROM entity_refs WHERE parent_uri IN ({SavedArtistsSql})
                    UNION
                    SELECT child_uri, parent_uri FROM entity_refs WHERE child_uri IN ({SavedArtistsSql});
                    """);
                // (3) those albums ∪ those albums' tracks. The `kind={album}` wrapper is what keeps the reversed edge leg
                //     honest (a track→artist edge also has a saved artist on its child side).
                var rows = QueryThin($"""
                    WITH art(uri) AS ({SavedArtistsSql}),
                         alb(uri) AS (SELECT uri FROM entity WHERE locale=$l AND kind={album} AND uri IN (
                             SELECT child_uri FROM entity_refs WHERE parent_uri IN (SELECT uri FROM art)
                             UNION
                             SELECT parent_uri FROM entity_refs WHERE child_uri IN (SELECT uri FROM art)))
                    {ThinColumnsSql}
                      WHERE locale=$l AND kind={album} AND uri IN (SELECT uri FROM alb)
                    UNION ALL
                    {ThinColumnsSql}
                      WHERE locale=$l AND kind=$k AND album_uri IN (SELECT uri FROM alb);
                    """, track);
                return Split(roots, rows, edges);
            }
        }
    }

    // The UNION ALL leg returns albums and tracks interleaved by kind — fan them into the two lists the caller indexes.
    static ColdCandidates Split(IReadOnlyList<ColdThinRow> roots, List<ColdThinRow> mixed, IReadOnlyList<ColdRefEdge> edges)
    {
        var albums = new List<ColdThinRow>(mixed.Count);
        var tracks = new List<ColdThinRow>(mixed.Count);
        for (int i = 0; i < mixed.Count; i++) (mixed[i].Kind == EntityKind.Track ? tracks : albums).Add(mixed[i]);
        return new ColdCandidates(roots, albums, tracks, edges);
    }

    List<ColdThinRow> QueryThin(string sql, int kind)
    {
        var list = new List<ColdThinRow>(256);
        lock (_readLock)
        {
            using var c = _read.CreateCommand();
            c.CommandText = sql;
            c.Parameters.AddWithValue("$l", _localeKey);
            c.Parameters.AddWithValue("$a", _account);
            c.Parameters.AddWithValue("$k", kind);
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new ColdThinRow(
                    r.GetString(0), (EntityKind)r.GetInt32(1),
                    r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? 0 : r.GetInt64(5),
                    r.IsDBNull(6) ? 0 : r.GetInt64(6), r.IsDBNull(7) ? null : r.GetString(7)));
        }
        return list;
    }

    List<ColdRefEdge> QueryEdges(string sql)
    {
        var list = new List<ColdRefEdge>(256);
        lock (_readLock)
        {
            using var c = _read.CreateCommand();
            c.CommandText = sql;
            c.Parameters.AddWithValue("$a", _account);
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                var parent = r.IsDBNull(0) ? "" : r.GetString(0);
                var child = r.IsDBNull(1) ? "" : r.GetString(1);
                if (parent.Length > 0 && child.Length > 0) list.Add(new ColdRefEdge(parent, child));
            }
        }
        return list;
    }

    /// <summary>The running cache-tier byte counter (entity + artist_overview + extension payloads).</summary>
    public long GetCacheBytes()
    {
        lock (_readLock)
        {
            using var c = _read.CreateCommand();
            c.CommandText = $"SELECT value FROM meta WHERE key='{MetaCacheBytes}';";
            return c.ExecuteScalar() is string s && long.TryParse(s, out var v) ? v : 0;
        }
    }

    /// <summary>Reclaim up to <paramref name="pages"/> freelist pages. A no-op until auto_vacuum=INCREMENTAL is active
    /// (which <see cref="RunFullVacuumIfPending"/> turns on).</summary>
    public void RunIncrementalVacuum(int pages)
    {
        if (pages <= 0) return;
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = $"PRAGMA incremental_vacuum({Math.Min(pages, 1_000_000)});";   // PRAGMA args cannot be bound
            c.ExecuteNonQuery();
        }
    }

    /// <summary>One-time post-migration compaction, gated on the `vacuum_pending` meta flag: switch the file to
    /// auto_vacuum=INCREMENTAL and VACUUM once (the mode change only takes effect THROUGH a full vacuum), then clear the
    /// flag. Safe to call from a background thread — it rides the writer lock, so the write-behind drain simply waits.
    /// Returns true when it actually vacuumed.</summary>
    public bool RunFullVacuumIfPending()
    {
        lock (_connLock)
        {
            using (var probe = _conn.CreateCommand())
            {
                probe.CommandText = $"SELECT value FROM meta WHERE key='{MetaVacuumPending}';";
                if (probe.ExecuteScalar() is not string pending || pending != "1") return false;
            }
            using (var c = _conn.CreateCommand())
            {
                // Must NOT run inside a transaction, and the auto_vacuum change is only realized by the VACUUM itself.
                c.CommandText = "PRAGMA auto_vacuum=INCREMENTAL; VACUUM;";
                c.ExecuteNonQuery();
            }
            using (var clear = _conn.CreateCommand())
            {
                clear.CommandText = $"INSERT OR REPLACE INTO meta(key,value) VALUES('{MetaVacuumPending}','0');";
                clear.ExecuteNonQuery();
            }
            return true;
        }
    }

    /// <summary>The derived pin set (§A.3 P0 + recent_surfaces). One indexed union over the identity tables — no stored
    /// pin bit, so an unlike/unfollow/account switch can never leave a stale pin behind.</summary>
    public IReadOnlyList<string> EnumeratePinnedUris()
    {
        var list = new List<string>(4096);
        lock (_readLock)
        {
            using var c = _read.CreateCommand();
            c.CommandText = $"SELECT uri FROM ({PinSetWithRecentSql}) WHERE uri IS NOT NULL AND uri<>'';";
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
        }
        return list;
    }

    // ── the cache GC SQL (Wave C, §C) ────────────────────────────────────────────────────────────────────────────────
    // POLICY lives in EntityCacheGc; the SQL lives here because every statement below must ride the WRITER connection
    // under `_connLock` (temp tables are per-connection, and a DELETE cannot go anywhere else). The pass is a sequence of
    // INDIVIDUALLY ATOMIC steps + INDIVIDUALLY ATOMIC ≤1000-row DELETE batches: aborting between any two of them leaves a
    // fully consistent database (every batch updates `cache_bytes` inside its own transaction), which is exactly what
    // makes shutdown-cancellation safe.

    /// <summary>DELETE batch size (§C.4 — Chromium's bounded-transaction pattern).</summary>
    public const int GcDeleteBatchRows = 1000;
    /// <summary>Never evict a row written in the last 15 minutes (critique #11 / Firefox bug 913808).</summary>
    public const long GcNewRowGraceSeconds = 15 * 60;

    /// <summary>Open a GC pass: materialize the pin table (§A.3 P0 ∪ recent_surfaces ∪ the one-level `entity_refs`
    /// closure ∪ the caller's UI-thread in-memory snapshot), the reusable victim-batch table, and reconcile the running
    /// `cache_bytes` counter against the real SUM(size) (§C.4). Every later sweep reads `temp.gc_pin`.</summary>
    public void GcBeginPass(IReadOnlyCollection<string>? inMemoryExempt)
    {
        lock (_connLock)
        {
            ExecLocked("DROP TABLE IF EXISTS temp.gc_pin; CREATE TEMP TABLE gc_pin(uri TEXT PRIMARY KEY);");
            ExecLocked($"INSERT OR IGNORE INTO temp.gc_pin(uri) {PinSetWithRecentSql};");
            if (inMemoryExempt is { Count: > 0 }) GcInsertExemptLocked(inMemoryExempt);
            // ONE level of closure is sufficient (track→album/artists, album→artists, artist→albums, show→episodes): the
            // parent is what a pin reason names, the child is what renders with it. Materialized through a second temp
            // table rather than INSERT…SELECT over the table being written, so the result never depends on whether SQLite
            // sees rows added by the same statement.
            ExecLocked("""
                DROP TABLE IF EXISTS temp.gc_kids;
                CREATE TEMP TABLE gc_kids(uri TEXT PRIMARY KEY);
                INSERT OR IGNORE INTO temp.gc_kids(uri)
                    SELECT DISTINCT child_uri FROM entity_refs
                    WHERE parent_uri IN (SELECT uri FROM temp.gc_pin) AND child_uri IS NOT NULL AND child_uri<>'';
                INSERT OR IGNORE INTO temp.gc_pin(uri) SELECT uri FROM temp.gc_kids;
                DROP TABLE IF EXISTS temp.gc_kids;
                DELETE FROM temp.gc_pin WHERE uri IS NULL OR uri='';
                DROP TABLE IF EXISTS temp.gc_batch;
                CREATE TEMP TABLE gc_batch(uri TEXT PRIMARY KEY);
                """);
            ReconcileCacheBytesLocked(null);
        }
    }

    /// <summary>Tear the pass's temp tables down. Safe to call twice / after a cancellation.</summary>
    public void GcEndPass()
    {
        lock (_connLock)
            ExecLocked("DROP TABLE IF EXISTS temp.gc_pin; DROP TABLE IF EXISTS temp.gc_batch; DROP TABLE IF EXISTS temp.gc_kids;");
    }

    /// <summary>The number of uris the current pass considers pinned (the per-GC report's `pinned` field).</summary>
    public long GcPinnedCount()
    {
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT count(*) FROM temp.gc_pin;";
            try { return Convert.ToInt64(c.ExecuteScalar() ?? 0L); } catch (SqliteException) { return 0; }
        }
    }

    void GcInsertExemptLocked(IReadOnlyCollection<string> exempt)
    {
        var chunk = new List<string>(Math.Min(exempt.Count, MaxInParams));
        foreach (var uri in exempt)
        {
            if (string.IsNullOrEmpty(uri)) continue;
            chunk.Add(uri);
            if (chunk.Count == MaxInParams) { GcInsertExemptChunkLocked(chunk); chunk.Clear(); }
        }
        if (chunk.Count > 0) GcInsertExemptChunkLocked(chunk);
    }

    void GcInsertExemptChunkLocked(List<string> chunk)
    {
        var sql = new System.Text.StringBuilder("INSERT OR IGNORE INTO temp.gc_pin(uri) VALUES ");
        for (int i = 0; i < chunk.Count; i++) { if (i > 0) sql.Append(','); sql.Append("($u").Append(i).Append(')'); }
        sql.Append(';');
        using var c = _conn.CreateCommand();
        c.CommandText = sql.ToString();
        for (int i = 0; i < chunk.Count; i++) c.Parameters.AddWithValue("$u" + i, chunk[i]);
        c.ExecuteNonQuery();
    }

    /// <summary>`artist_overview` TTL (§C.3): 7 d since `fetched_at`, pinned artists exempt.</summary>
    public (int Rows, long Bytes) GcSweepArtistOverviews(long fetchedBefore)
    {
        lock (_connLock)
        {
            using var tx = _conn.BeginTransaction();
            const string where = " WHERE fetched_at<$t AND uri NOT IN (SELECT uri FROM temp.gc_pin)";
            long freed;
            using (var c = _conn.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = "SELECT IFNULL(SUM(size),0) FROM artist_overview" + where + ";";
                c.Parameters.AddWithValue("$t", fetchedBefore);
                freed = Convert.ToInt64(c.ExecuteScalar() ?? 0L);
            }
            int rows;
            using (var c = _conn.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = "DELETE FROM artist_overview" + where + ";";
                c.Parameters.AddWithValue("$t", fetchedBefore);
                rows = c.ExecuteNonQuery();
            }
            if (rows == 0) { tx.Rollback(); return (0, 0); }
            if (freed > 0) ApplyCacheBytesDeltaLocked(-freed, tx);
            tx.Commit();
            return (rows, freed);
        }
    }

    /// <summary>Unpinned-entity TTL (§C.3): 30 d since `last_access`, in ≤<see cref="GcDeleteBatchRows"/>-row atomic
    /// batches. `updatedBefore` is the 15-minute new-row grace. Cancelling between batches is safe by construction.</summary>
    /// <param name="batchRows">Rows per atomic DELETE batch. Production always uses <see cref="GcDeleteBatchRows"/>;
    /// the tests shrink it (together with <paramref name="maxBatches"/>) to reproduce an aborted-mid-sweep database.</param>
    /// <param name="maxBatches">Stop after this many batches — the deterministic stand-in for a shutdown cancellation
    /// landing between two batches.</param>
    /// <param name="evicted">Optional sink receiving every uri whose row was deleted. The caller (EntityCacheGc) feeds it
    /// back to <c>CachedStore.OnEntitiesEvicted</c> so the in-memory COLD-PRESENCE map cannot keep claiming a row that
    /// this sweep just removed — a stale presence bit would make the pin-transition flush and the payload-hash elision
    /// BOTH skip the row forever (critique #1, re-armed by the GC).</param>
    public (int Rows, long Bytes) GcSweepUnpinnedEntities(long accessBefore, long updatedBefore, System.Threading.CancellationToken ct,
                                                          int batchRows = GcDeleteBatchRows, int maxBatches = int.MaxValue,
                                                          ICollection<string>? evicted = null)
        => GcDeleteLoop(
            "SELECT DISTINCT uri FROM entity WHERE last_access<$a AND updated_at<$u " +
            "AND uri NOT IN (SELECT uri FROM temp.gc_pin) LIMIT $n;",
            accessBefore, updatedBefore, budget: 0, ct, batchRows, maxBatches, evicted);

    /// <summary>Byte-budget LRU (§C.4): while the EVICTABLE cache bytes exceed the budget, delete unpinned entities
    /// oldest-`last_access` first until ≤ 0.9 × budget. Same atomic bounded batches.
    ///
    /// The trigger is <see cref="GcEvictableBytes"/>, NOT the whole `cache_bytes` counter (Wave F / K1): `cache_bytes`
    /// also carries the extension cache and every PINNED entity row, none of which this sweep is allowed to delete. A
    /// budget below that floor could therefore never be reached — the loop would evict the ENTIRE unpinned tier and still
    /// be "over budget". Counting only what is actually evictable makes the sweep converge exactly and makes the number
    /// the Settings readout shows mean what it says.</summary>
    public (int Rows, long Bytes) GcEnforceBudget(long budget, long updatedBefore, System.Threading.CancellationToken ct,
                                                  int batchRows = GcDeleteBatchRows, int maxBatches = int.MaxValue,
                                                  ICollection<string>? evicted = null)
        => budget <= 0
            ? (0, 0)
            : GcDeleteLoop(
                "SELECT DISTINCT uri FROM entity WHERE updated_at<$u AND uri NOT IN (SELECT uri FROM temp.gc_pin) " +
                "ORDER BY last_access ASC LIMIT $n;",
                accessBefore: 0, updatedBefore, budget, ct, batchRows, maxBatches, evicted);

    /// <summary>Extension-tier byte cap (v7) — the leg that was MISSING, and the reason the whole byte budget could never
    /// be met. `cache_bytes` has always counted localized_extension_cache payloads, but every sweep above deletes from
    /// `entity` only, so those bytes were a floor the budget sat under rather than a tier the budget governed (36 MB under
    /// a 64 MB budget on a real profile, growing ~9k rows/day). Deletes oldest-`last_access` first — the index the v7
    /// migration adds — in the same bounded, atomic batches, down to 0.9 × budget for hysteresis.
    ///
    /// Nothing here is pinned: an extension row is a pure cache of a wire response, always re-fetchable, and its ETag
    /// going away costs one full body instead of a 304. That is why this needs no gc_pin interaction.</summary>
    public (int Rows, long Bytes) GcTrimExtensions(long budgetBytes, System.Threading.CancellationToken ct,
                                                   int batchRows = GcDeleteBatchRows, int maxBatches = int.MaxValue)
    {
        if (budgetBytes <= 0) return (0, 0);
        int rows = 0;
        long freed = 0;
        lock (_connLock)
        {
            long total = ExtensionBytesLocked();
            long target = (long)(budgetBytes * 0.9);
            for (int batch = 0; batch < maxBatches && total > target && !ct.IsCancellationRequested; batch++)
            {
                // rowid IN (… ORDER BY last_access LIMIT n) rides ix_localized_extension_lru; the SELECT and the DELETE
                // share one transaction so the byte accounting can never drift from the rows actually removed.
                const string Victims = "SELECT rowid FROM localized_extension_cache ORDER BY last_access ASC LIMIT $n";
                using var tx = _conn.BeginTransaction();
                long batchBytes;
                using (var c = _conn.CreateCommand())
                {
                    c.Transaction = tx;
                    c.CommandText = $"SELECT IFNULL(SUM(length(payload)),0) FROM localized_extension_cache WHERE rowid IN ({Victims});";
                    c.Parameters.AddWithValue("$n", batchRows);
                    batchBytes = Convert.ToInt64(c.ExecuteScalar());
                }
                int deleted;
                using (var d = _conn.CreateCommand())
                {
                    d.Transaction = tx;
                    d.CommandText = $"DELETE FROM localized_extension_cache WHERE rowid IN ({Victims});";
                    d.Parameters.AddWithValue("$n", batchRows);
                    deleted = d.ExecuteNonQuery();
                }
                if (deleted == 0) { tx.Rollback(); break; }   // nothing left to take — do not spin
                if (batchBytes > 0) ApplyCacheBytesDeltaLocked(-batchBytes, tx);
                tx.Commit();
                rows += deleted;
                freed += batchBytes;
                total -= batchBytes;
            }
        }
        return (rows, freed);
    }

    long ExtensionBytesLocked()
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT IFNULL(SUM(length(payload)),0) FROM localized_extension_cache;";
        return Convert.ToInt64(c.ExecuteScalar());
    }

    /// <summary>The bytes this pass could actually reclaim: `entity` rows the current `temp.gc_pin` does NOT protect.
    /// Only valid between <see cref="GcBeginPass"/> and <see cref="GcEndPass"/>; outside a pass it degrades to the whole
    /// cache counter.</summary>
    public long GcEvictableBytes() { lock (_connLock) return GcEvictableBytesLocked(); }

    long GcEvictableBytesLocked()
    {
        try
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT IFNULL(SUM(size),0) FROM entity WHERE uri NOT IN (SELECT uri FROM temp.gc_pin);";
            return Convert.ToInt64(c.ExecuteScalar() ?? 0L);
        }
        catch (SqliteException) { return GetCacheBytesLocked(null); }   // no pass open — fall back to the gross counter
    }

    // One loop for both sweeps. `budget > 0` ⇒ stop as soon as the EVICTABLE bytes are ≤ 0.9 × budget (the Chromium
    // watermark); otherwise run until the predicate stops matching. Each iteration is ONE transaction: pick ≤batchRows
    // victims into temp.gc_batch, sum their bytes, delete entity + entity_refs + video_assoc rows, apply the cache_bytes
    // delta. The evictable total is seeded once and decremented by each batch's exact freed bytes — O(1) per batch, and
    // exact because the pick predicate can only ever choose unpinned rows.
    (int Rows, long Bytes) GcDeleteLoop(string pickSql, long accessBefore, long updatedBefore, long budget,
                                        System.Threading.CancellationToken ct, int batchRows, int maxBatches,
                                        ICollection<string>? evicted)
    {
        long watermark = budget > 0 ? (long)(budget * 0.9) : 0;
        long evictable = -1;
        int total = 0;
        long freed = 0;
        int batches = 0;
        if (batchRows <= 0) batchRows = GcDeleteBatchRows;
        while (!ct.IsCancellationRequested && batches++ < maxBatches)
        {
            lock (_connLock)
            {
                if (budget > 0)
                {
                    if (evictable < 0) evictable = GcEvictableBytesLocked();
                    if (evictable <= watermark) return (total, freed);
                }
                using var tx = _conn.BeginTransaction();
                ExecLocked("DELETE FROM temp.gc_batch;", tx);
                using (var pick = _conn.CreateCommand())
                {
                    pick.Transaction = tx;
                    pick.CommandText = "INSERT OR IGNORE INTO temp.gc_batch(uri) " + pickSql;
                    if (pickSql.Contains("$a", StringComparison.Ordinal)) pick.Parameters.AddWithValue("$a", accessBefore);
                    pick.Parameters.AddWithValue("$u", updatedBefore);
                    pick.Parameters.AddWithValue("$n", batchRows);
                    pick.ExecuteNonQuery();
                }
                long bytes;
                using (var c = _conn.CreateCommand())
                {
                    c.Transaction = tx;
                    c.CommandText = "SELECT IFNULL(SUM(size),0) FROM entity WHERE uri IN (SELECT uri FROM temp.gc_batch);";
                    bytes = Convert.ToInt64(c.ExecuteScalar() ?? 0L);
                }
                int rows;
                using (var c = _conn.CreateCommand())
                {
                    c.Transaction = tx;
                    c.CommandText = "DELETE FROM entity WHERE uri IN (SELECT uri FROM temp.gc_batch);";
                    rows = c.ExecuteNonQuery();
                }
                if (rows == 0) { tx.Rollback(); return (total, freed); }
                // The pin closure and the video↔audio map are cache rows keyed by the entity we just dropped: a dangling
                // edge would keep a stale parent alive on the next pass, and a dangling video_assoc row is unreadable.
                ExecLocked("DELETE FROM entity_refs WHERE parent_uri IN (SELECT uri FROM temp.gc_batch);", tx);
                ExecLocked("DELETE FROM video_assoc WHERE uri IN (SELECT uri FROM temp.gc_batch);", tx);
                if (bytes > 0) ApplyCacheBytesDeltaLocked(-bytes, tx);
                // Report the victims BEFORE the commit's scope ends — temp.gc_batch is this pass's own table, so it is
                // exactly the set that just left the disk.
                if (evicted is not null)
                {
                    using var pickBack = _conn.CreateCommand();
                    pickBack.Transaction = tx;
                    pickBack.CommandText = "SELECT uri FROM temp.gc_batch;";
                    using var rr = pickBack.ExecuteReader();
                    while (rr.Read()) evicted.Add(rr.GetString(0));
                }
                tx.Commit();
                total += rows;
                freed += bytes;
                if (budget > 0) evictable -= bytes;
            }
        }
        return (total, freed);
    }

    /// <summary>Membership GC (critique #6 / locked decision 11): a playlist that is NOT in the rootlist, NOT a recent
    /// surface, has no pending outbox intent, is not a collection item, and was last adopted before
    /// <paramref name="adoptedBefore"/> loses its `playlist_items` rows AND its `playlists` header row — the pin leg that
    /// kept every track of every editorial playlist the user ever OPENED alive forever. Returns the purged playlist uris
    /// so the caller can prune its in-memory mirrors. Own-library playlists are always in the rootlist, so user data is
    /// untouched by construction.</summary>
    public IReadOnlyList<string> GcSweepMemberships(long adoptedBefore)
    {
        var victims = new List<string>();
        lock (_connLock)
        {
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = """
                    SELECT p.uri FROM playlists p
                    WHERE p.adopted_at > 0 AND p.adopted_at < $t
                      AND p.uri NOT IN (SELECT uri FROM rootlist WHERE uri IS NOT NULL AND uri<>'')
                      AND p.uri NOT IN (SELECT uri FROM recent_surfaces)
                      AND p.uri NOT IN (SELECT entity_key FROM outbox)
                      AND p.uri NOT IN (SELECT item_uri FROM collection_items);
                    """;
                c.Parameters.AddWithValue("$t", adoptedBefore);
                using var r = c.ExecuteReader();
                while (r.Read()) victims.Add(r.GetString(0));
            }
            if (victims.Count == 0) return victims;

            using var tx = _conn.BeginTransaction();
            var chunk = new List<string>(Math.Min(victims.Count, MaxInParams));
            for (int i = 0; i < victims.Count; i++)
            {
                chunk.Add(victims[i]);
                if (chunk.Count == MaxInParams) { GcPurgeMembershipChunkLocked(chunk, tx); chunk.Clear(); }
            }
            if (chunk.Count > 0) GcPurgeMembershipChunkLocked(chunk, tx);
            tx.Commit();
        }
        return victims;
    }

    void GcPurgeMembershipChunkLocked(List<string> chunk, SqliteTransaction tx)
    {
        var list = new System.Text.StringBuilder();
        for (int i = 0; i < chunk.Count; i++) { if (i > 0) list.Append(','); list.Append("$u").Append(i); }
        foreach (var sql in new[]
                 {
                     $"DELETE FROM playlist_items WHERE playlist_uri IN ({list});",
                     $"DELETE FROM playlists WHERE uri IN ({list});",
                 })
        {
            using var c = _conn.CreateCommand();
            c.Transaction = tx;
            c.CommandText = sql;
            for (int i = 0; i < chunk.Count; i++) c.Parameters.AddWithValue("$u" + i, chunk[i]);
            c.ExecuteNonQuery();
        }
    }

    /// <summary>Idle hygiene after a GC pass: truncate the WAL so the freed pages actually leave the file.</summary>
    public void CheckpointWal()
    {
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            try { c.ExecuteNonQuery(); } catch (SqliteException) { /* a busy checkpoint is a no-op, never an error */ }
        }
    }

    // ── escape hatches (§G) ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"Clear metadata cache": every UNPINNED `entity` row (plus its refs), every `artist_overview` row and
    /// every extension row. IDENTITY TABLES ARE NEVER TOUCHED (collection_items / playlists / playlist_items / rootlist /
    /// outbox / dead_letter / video_override / collection_rev), and neither is `video_assoc` (the video-edge map is
    /// eagerly loaded and fragile). Pinned entity rows survive, so the app stays offline-capable. Returns rows freed.
    ///
    /// It builds its pin table under its OWN name (`temp.clear_pin`, Wave F): both this and a GC pass run on the writer
    /// connection, where temp tables are connection-scoped — sharing `temp.gc_pin` let a settings-triggered clear DROP
    /// and rebuild the table out from under an interleaved GC batch, which both aborted that pass and (worse) swapped in
    /// a pin table WITHOUT the GC's UI-thread exempt snapshot.</summary>
    public (int Rows, long Bytes) ClearMetadataCache()
    {
        long before, after;
        int rows;
        lock (_connLock)
        {
            ExecLocked("DROP TABLE IF EXISTS temp.clear_pin; CREATE TEMP TABLE clear_pin(uri TEXT PRIMARY KEY);");
            ExecLocked($"INSERT OR IGNORE INTO temp.clear_pin(uri) {PinSetWithRecentSql};");
            ExecLocked("""
                DROP TABLE IF EXISTS temp.clear_kids;
                CREATE TEMP TABLE clear_kids(uri TEXT PRIMARY KEY);
                INSERT OR IGNORE INTO temp.clear_kids(uri)
                    SELECT DISTINCT child_uri FROM entity_refs
                    WHERE parent_uri IN (SELECT uri FROM temp.clear_pin) AND child_uri IS NOT NULL AND child_uri<>'';
                INSERT OR IGNORE INTO temp.clear_pin(uri) SELECT uri FROM temp.clear_kids;
                DROP TABLE IF EXISTS temp.clear_kids;
                DELETE FROM temp.clear_pin WHERE uri IS NULL OR uri='';
                """);
            before = GetCacheBytesLocked(null);
            using (var tx = _conn.BeginTransaction())
            {
                using (var c = _conn.CreateCommand())
                {
                    c.Transaction = tx;
                    c.CommandText = "DELETE FROM entity WHERE uri NOT IN (SELECT uri FROM temp.clear_pin);";
                    rows = c.ExecuteNonQuery();
                }
                ExecLocked("""
                    DELETE FROM entity_refs WHERE parent_uri NOT IN (SELECT uri FROM temp.clear_pin);
                    DELETE FROM artist_overview;
                    DELETE FROM localized_extension_cache;
                    """, tx);
                ReconcileCacheBytesLocked(tx);
                tx.Commit();
            }
            after = GetCacheBytesLocked(null);
            ExecLocked("DROP TABLE IF EXISTS temp.clear_pin;");
        }
        RunIncrementalVacuum(2000);
        return (rows, Math.Max(0, before - after));
    }

    /// <summary>The user-settable cache-tier byte budget (§G). 0/absent ⇒ <see cref="DefaultCacheBudgetBytes"/>.</summary>
    public long GetCacheBudgetBytes()
    {
        lock (_readLock)
        {
            using var c = _read.CreateCommand();
            c.CommandText = $"SELECT value FROM meta WHERE key='{MetaCacheBudget}';";
            return c.ExecuteScalar() is string s && long.TryParse(s, out var v) && v > 0 ? v : DefaultCacheBudgetBytes;
        }
    }

    public void SetCacheBudgetBytes(long bytes)
    {
        if (bytes <= 0) return;
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = $"INSERT INTO meta(key,value) VALUES('{MetaCacheBudget}',$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
            c.Parameters.AddWithValue("$v", bytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            c.ExecuteNonQuery();
        }
    }

    /// <summary>One cheap diagnostics snapshot for the Settings → Storage readout (§G metrics). Writer-side (it needs the
    /// pin subquery and the file pragmas); a settings-page action, never a hot path.</summary>
    public EntityCacheStats GetCacheStats()
    {
        lock (_connLock)
        {
            long pageCount = ScalarLongLocked("PRAGMA page_count;");
            long pageSize = ScalarLongLocked("PRAGMA page_size;");
            long freelist = ScalarLongLocked("PRAGMA freelist_count;");
            long pinnedBytes = ScalarLongLocked($"SELECT IFNULL(SUM(size),0) FROM entity WHERE uri IN ({PinSetWithRecentSql});");
            long pinnedRows = ScalarLongLocked($"SELECT count(*) FROM entity WHERE uri IN ({PinSetWithRecentSql});");
            long budget = ScalarLongLocked($"SELECT CAST(value AS INTEGER) FROM meta WHERE key='{MetaCacheBudget}';");
            long entityBytes = ScalarLongLocked("SELECT IFNULL(SUM(size),0) FROM entity;");
            return new EntityCacheStats(
                DbBytes: pageCount * pageSize,
                ReclaimableBytes: freelist * pageSize,
                CacheBytes: GetCacheBytesLocked(null),
                PinnedBytes: pinnedBytes,
                BudgetBytes: budget > 0 ? budget : DefaultCacheBudgetBytes,
                EntityBytes: entityBytes,
                EntityRows: ScalarLongLocked("SELECT count(*) FROM entity;"),
                PinnedRows: pinnedRows,
                OverviewRows: ScalarLongLocked("SELECT count(*) FROM artist_overview;"),
                ExtensionRows: ScalarLongLocked("SELECT count(*) FROM localized_extension_cache;"));
        }
    }

    long ScalarLongLocked(string sql)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = sql;
        var v = c.ExecuteScalar();
        return v is null or DBNull ? 0 : Convert.ToInt64(v);
    }

    long GetCacheBytesLocked(SqliteTransaction? tx)
    {
        using var c = _conn.CreateCommand();
        c.Transaction = tx;
        c.CommandText = $"SELECT value FROM meta WHERE key='{MetaCacheBytes}';";
        return c.ExecuteScalar() is string s && long.TryParse(s, out var v) ? v : 0;
    }

    // The running counter is reconciled against the truth once per GC pass (§C.4): the delta bookkeeping is exact, but a
    // torn shutdown mid-batch (or a hand-seeded fixture) can leave it drifted, and every eviction decision reads it.
    void ReconcileCacheBytesLocked(SqliteTransaction? tx)
        => ExecLocked($"""
            INSERT INTO meta(key,value) VALUES('{MetaCacheBytes}', CAST(
                (SELECT IFNULL(SUM(size),0) FROM entity)
              + (SELECT IFNULL(SUM(size),0) FROM artist_overview)
              + (SELECT IFNULL(SUM(length(payload)),0) FROM localized_extension_cache) AS TEXT))
            ON CONFLICT(key) DO UPDATE SET value=excluded.value;
            """, tx);

    public void Dispose()
    {
        _queue.Writer.TryComplete();   // no more writes — the writer drains the backlog then exits
        bool drained;
        try { drained = _writer.Wait(TimeSpan.FromSeconds(30)); } catch { drained = true; }   // faulted → already stopped
        lock (_readLock) { _read.Dispose(); }   // the reader is independent of the writer task — always safe to close
        // If the writer is STILL running (pathological backlog / stuck), do NOT dispose the connection + commands under it —
        // a mid-ExecuteNonQuery dispose corrupts/crashes. Leaking on shutdown is the safer choice (the process is exiting).
        if (!drained) return;
        _entityCmd?.Dispose();
        _sizeProbeCmd?.Dispose();
        _extSizeProbeCmd?.Dispose();
        _refDelCmd?.Dispose();
        _refInsCmd?.Dispose();
        _cacheDeltaCmd?.Dispose();
        _extensionCmd?.Dispose();
        _videoCmd?.Dispose();
        _ovrUpCmd?.Dispose();
        _ovrDelCmd?.Dispose();
        _savedUpCmd?.Dispose();
        _savedDelCmd?.Dispose();
        _revCmd?.Dispose();
        _conn.Dispose();
    }

    enum OpKind : byte { Entity, Extension, VideoAssoc, VideoOverrideSet, VideoOverrideDelete, SavedSet, SavedRemove, Revision, Flush }

    readonly struct WriteOp
    {
        public readonly OpKind Op;
        public readonly string A;            // entity uri, or set_id
        public readonly string? B;           // saved item_uri, or the revision token (nullable)
        public readonly int Kind;            // EntityKind, or SyncState
        public readonly long L;              // revision synced_at
        public readonly byte[]? Payload;
        public readonly ColdExtension? Extension;
        public readonly VideoOverride? Override;
        public readonly TaskCompletionSource? Done;

        WriteOp(OpKind op, string a, string? b, int kind, long l, byte[]? payload, ColdExtension? extension, VideoOverride? ovr, TaskCompletionSource? done)
        { Op = op; A = a; B = b; Kind = kind; L = l; Payload = payload; Extension = extension; Override = ovr; Done = done; }

        public static WriteOp Entity(string uri, int kind, byte[] payload)
            => new(OpKind.Entity, uri, null, kind, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), payload, null, null, null);
        public static WriteOp ExtensionValue(ColdExtension extension) => new(OpKind.Extension, extension.EntityUri, null, 0, 0, null, extension, null, null);
        public static WriteOp VideoAssoc(string uri, byte[] payload) => new(OpKind.VideoAssoc, uri, null, 0, 0, payload, null, null, null);
        public static WriteOp VideoOverrideSet(VideoOverride o) => new(OpKind.VideoOverrideSet, o.Uri, null, 0, 0, null, null, o, null);
        public static WriteOp VideoOverrideDelete(string uri) => new(OpKind.VideoOverrideDelete, uri, null, 0, 0, null, null, null, null);
        public static WriteOp Saved(string set, string uri, bool saved, int sync, long addedAtMs = 0) => new(saved ? OpKind.SavedSet : OpKind.SavedRemove, set, uri, sync, addedAtMs, null, null, null, null);
        public static WriteOp Revision(string setId, string? revision, long syncedAt) => new(OpKind.Revision, setId, revision, 0, syncedAt, null, null, null, null);
        public static WriteOp FlushMarker(TaskCompletionSource done) => new(OpKind.Flush, "", null, 0, 0, null, null, null, done);
    }
}
