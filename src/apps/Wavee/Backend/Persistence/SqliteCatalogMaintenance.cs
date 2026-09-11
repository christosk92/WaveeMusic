using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Persistence;

/// <summary>Rows counts selected eviction units; a transport document and its pointer form one atomic unit.</summary>
public sealed record CatalogEviction(IReadOnlyList<ResourceKey> Resources, int Rows, long Bytes);

public sealed partial class SqliteColdStore
{
    // Finding #7: per-root (not global) bound on the single relation hop, so one huge root cannot starve the rest.
    const int RelationHopPerRootLimit = 1000;

    void EnsureCatalogAccountingLocked()
    {
        foreach (var (table, bytes) in new[] { ("catalog_resource", "size"), ("catalog_relation_item", "size"), ("extension_cache", "length(payload)") })
        {
            string New(string alias) => bytes == "size" ? alias + ".size" : "length(" + alias + ".payload)";
            foreach (var (operation, delta) in new[] { ("INSERT", "COALESCE(" + New("NEW") + ",0)"),
                ("DELETE", "-COALESCE(" + New("OLD") + ",0)"), ("UPDATE", "COALESCE(" + New("NEW") + ",0)-COALESCE(" + New("OLD") + ",0)") })
                ExecLocked("CREATE TRIGGER IF NOT EXISTS " + table + "_bytes_" + operation + " AFTER " + operation + " ON " + table +
                    " BEGIN INSERT INTO meta(key,value) VALUES('catalog_bytes',CAST(" + delta + " AS TEXT)) " +
                    "ON CONFLICT(key) DO UPDATE SET value=CAST(CAST(value AS INTEGER)+(" + delta + ") AS TEXT); END;");
        }
        // INSERT OR IGNORE still evaluates its SELECT before finding the existing key. Avoid rescanning all
        // catalog/transport rows on every launch; the triggers maintain this counter after its one-time seed.
        using var existing = _conn.CreateCommand();
        existing.CommandText = "SELECT 1 FROM meta WHERE key='catalog_bytes';";
        if (existing.ExecuteScalar() is not null) return;
        ExecLocked("INSERT INTO meta(key,value) SELECT 'catalog_bytes',CAST(" +
            "(SELECT COALESCE(SUM(size),0) FROM catalog_resource)+(SELECT COALESCE(SUM(size),0) FROM catalog_relation_item)+" +
            "(SELECT COALESCE(SUM(length(payload)),0) FROM extension_cache) AS TEXT);");
    }

    internal void SetCatalogBudget(long bytes)
    { lock (_connLock) ExecuteReplicaLocked(null, "INSERT INTO meta VALUES('cache_budget_bytes',$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value;", ("$v", bytes.ToString(System.Globalization.CultureInfo.InvariantCulture))); }
    internal void RecordRecentCatalogSurface(string uri, int kind, long now)
    {
        lock (_connLock)
        {
            using var tx = _conn.BeginTransaction();
            ExecuteReplicaLocked(tx, "INSERT INTO recent_surfaces VALUES($u,$k,$t) ON CONFLICT(uri) DO UPDATE SET kind=excluded.kind,last_opened=excluded.last_opened;",
                ("$u", uri), ("$k", kind), ("$t", now));
            ExecLocked("DELETE FROM recent_surfaces WHERE uri NOT IN(SELECT uri FROM recent_surfaces ORDER BY last_opened DESC,uri LIMIT 50);", tx);
            tx.Commit();
        }
    }

    void BuildCatalogPins(IReadOnlyCollection<ResourceKey> pins, long now, SqliteTransaction tx)
    {
        ExecLocked("CREATE TEMP TABLE IF NOT EXISTS catalog_pins(scope_id INTEGER NOT NULL,subject TEXT NOT NULL,PRIMARY KEY(scope_id,subject)) WITHOUT ROWID; DELETE FROM temp.catalog_pins;", tx);
        foreach (var pin in pins)
            if (FindCatalogScope(_conn, pin.Scope, tx) is { } id)
                ExecuteReplicaLocked(tx, "INSERT OR IGNORE INTO temp.catalog_pins VALUES($s,$u);", ("$s", id), ("$u", pin.Subject));
        ExecLocked("INSERT OR IGNORE INTO temp.catalog_pins SELECT c.scope_id,r.uri FROM catalog_scope c JOIN replica_rootlist_entry r " +
            "ON c.storage_account=r.storage_account AND c.provider_account=r.owner_account WHERE r.kind=0;" +
            // Finding #7: a followed playlist's member tracks/episodes never appear as `catalog_relation_item`
            // parents (playlist membership lives only in the replica tables), so they need their own pin leg —
            // otherwise they age out of the TTL/LRU sweep even though the playlist itself stays pinned above.
            "INSERT OR IGNORE INTO temp.catalog_pins SELECT c.scope_id,pi.item_uri FROM catalog_scope c " +
            "JOIN replica_rootlist_entry ro ON c.storage_account=ro.storage_account AND c.provider_account=ro.owner_account AND ro.kind=0 " +
            "JOIN replica_playlist_item pi ON pi.storage_account=ro.storage_account AND pi.owner_account=ro.owner_account AND pi.playlist_uri=ro.uri;" +
            "INSERT OR IGNORE INTO temp.catalog_pins SELECT c.scope_id,r.item_uri FROM catalog_scope c JOIN replica_collection_item r " +
            "ON c.storage_account=r.storage_account AND c.provider_account=r.owner_account;" +
            "INSERT OR IGNORE INTO temp.catalog_pins SELECT c.scope_id,r.entity_key FROM catalog_scope c JOIN outbox r " +
            "ON c.storage_account=r.storage_account AND c.provider_account=r.owner_account;" +
            "INSERT OR IGNORE INTO temp.catalog_pins SELECT c.scope_id,r.uri FROM catalog_scope c CROSS JOIN recent_surfaces r;", tx);
        // Materialize the roots before adding children; the SELECT must not observe rows it is inserting.
        ExecLocked("CREATE TEMP TABLE IF NOT EXISTS catalog_pin_roots(scope_id INTEGER NOT NULL,subject TEXT NOT NULL,PRIMARY KEY(scope_id,subject)) WITHOUT ROWID; " +
            "DELETE FROM temp.catalog_pin_roots; INSERT INTO temp.catalog_pin_roots SELECT * FROM temp.catalog_pins;", tx);
        // Exactly one bounded relation hop, bounded PER ROOT (finding #7): a global LIMIT let one 5,000-item root
        // (e.g. one huge relation page) starve every other root's single hop. A followed artist still cannot
        // recursively pin its entire discography's tracks — the hop is one level deep regardless of the bound.
        ExecLocked("INSERT OR IGNORE INTO temp.catalog_pins SELECT scope_id,child_uri FROM (" +
            "SELECT r.scope_id,r.child_uri,ROW_NUMBER() OVER (PARTITION BY r.scope_id,r.parent_subject ORDER BY r.ordinal) AS rn " +
            "FROM catalog_relation_item r WHERE EXISTS(SELECT 1 FROM temp.catalog_pin_roots p WHERE p.scope_id=r.scope_id AND p.subject=r.parent_subject)) " +
            "WHERE rn<=" + RelationHopPerRootLimit.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";", tx);
        long day = now - now % (24L * 60 * 60 * 1000);
        ExecuteReplicaLocked(tx, "UPDATE catalog_resource SET last_access=$day WHERE last_access<$day AND EXISTS(" +
            "SELECT 1 FROM temp.catalog_pins p WHERE p.scope_id=catalog_resource.scope_id AND p.subject=catalog_resource.subject);", ("$day", day));
    }

    /// <summary>Can a non-clearing sweep evict anything? Answered on the READ connection with index-only probes
    /// (≈8 ms on a 124 MB, 54.8k-row library) instead of <see cref="RunCatalogGcBatch"/>'s whole-table scans (3,429 ms on the
    /// commit owner in the 2026-09-09 ARM64 tour, for zero deleted rows). It takes neither the commit owner nor
    /// <c>_connLock</c>, so a navigation's cold read is the only thing it can ever wait behind.
    ///
    /// Deliberately CONSERVATIVE: every leg over-reports rather than under-reports (pins, the 15-minute write
    /// grace and the per-row staleness re-check are all ignored here), so a <c>false</c> means the batch would
    /// have deleted nothing and skipping it is behaviour-identical. A <c>true</c> only costs a sweep that finds
    /// less than it hoped.</summary>
    internal bool CatalogGcHasWork(long budget, DateTimeOffset now)
    {
        long stamp = now.ToUnixTimeMilliseconds();
        lock (_readLock)
        {
            // Byte pressure. `catalog_bytes` is trigger-maintained, so this is a single meta row, and pinned bytes
            // only ever shrink `remaining` below `before` — a cache that fits the budget can never be over it.
            if (ReadCatalogScalar("SELECT CAST(value AS INTEGER) FROM meta WHERE key='catalog_bytes';") > budget) return true;
            // TTL, resource leg: one covering pass over ix_catalog_resource_gc(facet,last_access,updated_at) —
            // ~25 groups, no payload pages touched.
            using (var command = _read.CreateCommand())
            {
                command.CommandText = "SELECT facet,MIN(last_access) FROM catalog_resource GROUP BY facet;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    if (reader.GetInt64(1) < stamp - CatalogSweepSchedule.TtlDays((FacetKind)reader.GetInt32(0)) * 86_400_000L)
                        return true;
            }
            // TTL, transport leg: ix_extension_cache_gc(updated_at) turns this into a seek. Without that index it
            // is a full scan of every cached payload (73 ms measured) — the index exists for exactly this probe.
            using var transport = _read.CreateCommand();
            transport.CommandText = "SELECT EXISTS(SELECT 1 FROM extension_cache WHERE updated_at<$cutoff);";
            transport.Parameters.AddWithValue("$cutoff", stamp - 30L * 24 * 60 * 60 * 1000);
            return Convert.ToInt64(transport.ExecuteScalar() ?? 0, System.Globalization.CultureInfo.InvariantCulture) != 0;
        }
    }

    long ReadCatalogScalar(string sql)
    {
        using var command = _read.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0, System.Globalization.CultureInfo.InvariantCulture);
    }

    internal CatalogEviction RunCatalogGcBatch(IReadOnlyCollection<ResourceKey> pins, long budget, DateTimeOffset now, bool clear)
    {
        lock (_connLock)
        {
            using var tx = _conn.BeginTransaction();
            long stamp = now.ToUnixTimeMilliseconds();
            BuildCatalogPins(pins, stamp, tx);
            long before = CatalogScalar("SELECT CAST(value AS INTEGER) FROM meta WHERE key='catalog_bytes';", tx);
            // `remaining` exists to answer "over budget?" and is only ever decremented from here, so when the whole
            // cache already fits the budget the answer is `false` for every value `CatalogPinnedBytes` could return
            // and every later `remaining <= budget` test holds too. Skipping it drops three whole-table SUM scans —
            // 1,079 ms of the tour's 3,429 ms batch, over 54.8k resource + 23.7k relation + 31.1k payload rows.
            long remaining = before <= budget ? before : Math.Max(0, before - CatalogPinnedBytes(tx));
            var resources = new List<ResourceKey>();
            int rowsDeleted = 0;
            using (var candidates = ReplicaCommand(tx, "SELECT r.scope_id,r.subject,r.facet,r.arguments,r.size+COALESCE((SELECT SUM(i.size) " +
                "FROM catalog_relation_item i WHERE i.scope_id=r.scope_id AND i.parent_subject=r.subject AND i.facet=r.facet AND i.arguments=r.arguments),0)," +
                "r.last_access,r.updated_at,c.provider,c.provider_account,c.locale,c.market,c.catalogue,c.tier,c.explicit_filter,c.context_known,c.storage_account " +
                "FROM catalog_resource r JOIN catalog_scope c USING(scope_id) WHERE NOT EXISTS(SELECT 1 FROM temp.catalog_pins p " +
                "WHERE p.scope_id=r.scope_id AND p.subject=r.subject) AND ($clear OR (r.updated_at<$grace AND " +
                "($over OR r.last_access < $stamp - CASE WHEN r.facet=21 THEN 7 WHEN r.facet BETWEEN 30 AND 36 THEN 14 ELSE 30 END * 86400000))) " +
                "ORDER BY r.last_access,r.updated_at,r.subject,r.facet,r.arguments LIMIT 1000;",
                ("$clear", clear), ("$over", remaining > budget), ("$stamp", stamp), ("$grace", stamp - 15 * 60 * 1000)))
            {
                var victims = new List<(long Scope, ResourceKey Key, long Size)>();
                using (var reader = candidates.ExecuteReader())
                    while (reader.Read())
                    {
                        var facet = (FacetKind)reader.GetInt32(2);
                        int days = facet == FacetKind.ArtistOverview ? 7 : (int)facet is >= 30 and <= 36 ? 14 : 30;
                        bool stale = reader.GetInt64(5) < stamp - TimeSpan.FromDays(days).TotalMilliseconds;
                        if (!clear && !stale && remaining <= budget) continue;
                        var scope = new CatalogScope(reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10),
                            reader.GetString(11), reader.GetInt32(12), reader.GetInt32(13) != 0, reader.GetInt32(14) != 0, reader.GetString(15));
                        var key = new ResourceKey(scope, reader.GetString(1), facet, ResourceArguments.FromStorageKey(reader.GetString(3)));
                        long bytes = reader.GetInt64(4); remaining -= bytes;
                        victims.Add((reader.GetInt64(0), key, bytes));
                    }
                foreach (var victim in victims)
                {
                    using var delete = ReplicaCommand(tx, "DELETE FROM catalog_resource WHERE scope_id=$scope AND subject=$subject AND facet=$facet AND arguments=$args;");
                    AddCatalogKey(delete, victim.Scope, victim.Key); delete.ExecuteNonQuery();
                    resources.Add(victim.Key); rowsDeleted++;
                }
            }
            int allowance = 1000 - rowsDeleted;
            if (allowance > 0)
            {
                using var select = ReplicaCommand(tx, "SELECT scope_id,entity_uri,extension_kind,COALESCE(length(payload),0),updated_at FROM extension_cache e WHERE NOT EXISTS(SELECT 1 FROM temp.catalog_pins p " +
                    "WHERE p.scope_id=e.scope_id AND p.subject=e.entity_uri) AND ($clear OR e.updated_at<$cutoff OR ($over AND e.updated_at<$grace)) " +
                    "ORDER BY last_access,scope_id,entity_uri,extension_kind LIMIT $limit;", ("$clear", clear), ("$cutoff", stamp - 30L * 24 * 60 * 60 * 1000),
                    ("$over", remaining > budget), ("$grace", stamp - 15 * 60 * 1000), ("$limit", allowance));
                var victims = new List<(long Scope, string Uri, int Kind, long Bytes, long Updated)>();
                using (var reader = select.ExecuteReader()) while (reader.Read()) victims.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4)));
                foreach (var victim in victims)
                {
                    if (!clear && victim.Updated >= stamp - 30L * 24 * 60 * 60 * 1000 && remaining <= budget) continue;
                    using (var pointer = ReplicaCommand(tx, "SELECT provider,provider_account,locale,market,catalogue,tier,explicit_filter,context_known,storage_account " +
                        "FROM catalog_scope WHERE scope_id=$scope;", ("$scope", victim.Scope)))
                    using (var reader = pointer.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            var scope = new CatalogScope(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                                reader.GetInt32(5), reader.GetBoolean(6), reader.GetBoolean(7), reader.GetString(8));
                            resources.Add(new(scope, victim.Uri, FacetKind.ExtensionDocument,
                                new ResourceArguments(Filter: victim.Kind.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                        }
                    }
                    using (var pointer = ReplicaCommand(tx, "DELETE FROM catalog_resource WHERE scope_id=$scope AND subject=$uri AND facet=45 AND arguments=$args RETURNING size;",
                        ("$scope", victim.Scope), ("$uri", victim.Uri), ("$args", new ResourceArguments(Filter: victim.Kind.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToStorageKey())))
                        remaining -= Convert.ToInt64(pointer.ExecuteScalar() ?? 0, System.Globalization.CultureInfo.InvariantCulture);
                    using var delete = ReplicaCommand(tx, "DELETE FROM extension_cache WHERE scope_id=$scope AND entity_uri=$uri AND extension_kind=$kind;",
                        ("$scope", victim.Scope), ("$uri", victim.Uri), ("$kind", victim.Kind));
                    rowsDeleted += delete.ExecuteNonQuery();
                    remaining -= victim.Bytes;
                }
            }
            // A `catalog_search` row can only orphan when this batch deletes its identity resource: the one other
            // delete site (ForgetCorrupt) removes both rows inside a single transaction. So a batch that deleted
            // nothing cannot have created an orphan, and this whole-table anti-join (103 ms measured) is skipped.
            if (clear || rowsDeleted > 0)
                ExecLocked("DELETE FROM catalog_search WHERE NOT EXISTS(SELECT 1 FROM catalog_resource r WHERE r.scope_id=catalog_search.scope_id AND r.subject=catalog_search.uri AND r.facet BETWEEN 1 AND 7);", tx);
            long after = CatalogScalar("SELECT CAST(value AS INTEGER) FROM meta WHERE key='catalog_bytes';", tx);
            tx.Commit();
            return new(resources, rowsDeleted, Math.Max(0, before - after));
        }
    }

    long CatalogPinnedBytes(SqliteTransaction tx)
        => CatalogScalar("SELECT (SELECT COALESCE(SUM(size),0) FROM catalog_resource r WHERE EXISTS(SELECT 1 FROM temp.catalog_pins p WHERE p.scope_id=r.scope_id AND p.subject=r.subject)) + " +
            "(SELECT COALESCE(SUM(size),0) FROM catalog_relation_item r WHERE EXISTS(SELECT 1 FROM temp.catalog_pins p WHERE p.scope_id=r.scope_id AND p.subject=r.parent_subject)) + " +
            "(SELECT COALESCE(SUM(length(payload)),0) FROM extension_cache r WHERE EXISTS(SELECT 1 FROM temp.catalog_pins p WHERE p.scope_id=r.scope_id AND p.subject=r.entity_uri));", tx);

    internal EntityCacheStats ReadCatalogStats(long budget, IReadOnlyCollection<ResourceKey> pins, DateTimeOffset now)
    {
        lock (_connLock)
        {
            using var tx = _conn.BeginTransaction();
            BuildCatalogPins(pins, now.ToUnixTimeMilliseconds(), tx);
            long Scalar(string sql) => CatalogScalar(sql, tx);
            long page = Scalar("PRAGMA page_size;"), bytes = Scalar("SELECT CAST(value AS INTEGER) FROM meta WHERE key='catalog_bytes';");
            var stats = new EntityCacheStats(page * Scalar("PRAGMA page_count;"), page * Scalar("PRAGMA freelist_count;"), bytes, CatalogPinnedBytes(tx), budget,
                bytes, Scalar("SELECT COUNT(*) FROM catalog_resource;"), Scalar("SELECT COUNT(*) FROM catalog_resource r WHERE EXISTS(SELECT 1 FROM temp.catalog_pins p WHERE p.scope_id=r.scope_id AND p.subject=r.subject);"),
                Scalar("SELECT COUNT(*) FROM catalog_resource WHERE facet=21;"), Scalar("SELECT COUNT(*) FROM extension_cache;"));
            tx.Commit();
            return stats;
        }
    }
    long CatalogScalar(string sql, SqliteTransaction? tx = null)
    { using var command = ReplicaCommand(tx, sql); return Convert.ToInt64(command.ExecuteScalar() ?? 0, System.Globalization.CultureInfo.InvariantCulture); }

    internal Task<bool> CompactCatalogIfIdleAsync(bool hasActivePins, CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (hasActivePins) return false;
            lock (_connLock)
            lock (_readLock)
            {
                if (CatalogScalar("SELECT CAST(value AS INTEGER) FROM meta WHERE key='cache_vacuum_pending';") != 1) return false;
                ct.ThrowIfCancellationRequested();
                ExecLocked("PRAGMA wal_checkpoint(TRUNCATE); VACUUM; PRAGMA wal_checkpoint(TRUNCATE);");
                ExecLocked("DELETE FROM meta WHERE key='cache_vacuum_pending';");
                return true;
            }
        }, ct);
}
