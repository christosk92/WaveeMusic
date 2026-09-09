using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Wavee.Backend.Spotify;

namespace Wavee.Backend.Persistence;

/// <summary>Normalized durable catalog and account-scoped library replicas. DataCommitQueue owns runtime writes.</summary>
public sealed partial class SqliteColdStore : IDisposable, Wavee.Backend.Sync.IReplicaPersistence
{
    public const string DefaultAccount = "default";
    public const int CurrentSchemaVersion = 12;
    public const long DefaultCacheBudgetBytes = 64L * 1024 * 1024;
    public const string MetaCacheBytes = "cache_bytes";
    public const string MetaCacheBudget = "cache_budget_bytes";
    public const string MetaVacuumPending = "cache_vacuum_pending";
    readonly SqliteConnection _conn, _read;
    readonly object _connLock = new(), _readLock = new();
    readonly string _account;
    readonly string? _spotifyLocale;
    int _disposed;

    /// <summary>The schema version this open replaced, or null when the file was already current (or brand new).</summary>
    public int? ResetFromSchema { get; }

    public SqliteColdStore(string path) : this(path, DefaultAccount, null) { }
    public SqliteColdStore(string path, string account) : this(path, account, null) { }
    public SqliteColdStore(string path, string account, string? spotifyLocale)
    {
        _account = account;
        _spotifyLocale = string.IsNullOrWhiteSpace(spotifyLocale) ? null : SpotifyHeaders.NormalizeLanguage(spotifyLocale);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _conn.Open();
        try
        {
            // foreign_keys stays OFF until the reset has run: DROP TABLE under enforced foreign keys fails on a
            // parent table (catalog_scope) that still has children, and the pragma is a no-op inside a transaction.
            ExecLocked("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;" +
                "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT);");
            int? previous = null;
            using (var version = _conn.CreateCommand())
            {
                version.CommandText = "SELECT value FROM meta WHERE key='schema_version';";
                if (int.TryParse(version.ExecuteScalar() as string, out int schema)) previous = schema;
            }
            if (previous > CurrentSchemaVersion)
                throw new InvalidOperationException("The library database belongs to a newer Wavee version.");
            if (previous is { } old && old != CurrentSchemaVersion) { ResetForSchemaLocked(); ResetFromSchema = old; }
            if (previous != CurrentSchemaVersion)
                ExecLocked("INSERT OR REPLACE INTO meta(key,value) VALUES('schema_version','" + CurrentSchemaVersion + "');");
            ExecLocked("PRAGMA foreign_keys=ON;");
            ExecLocked("CREATE TABLE IF NOT EXISTS video_override(uri TEXT PRIMARY KEY,path TEXT NOT NULL,id TEXT NOT NULL," +
                "duration_ms INTEGER DEFAULT 0,size INTEGER DEFAULT 0,mtime INTEGER DEFAULT 0,added_at INTEGER DEFAULT 0);" +
                "CREATE TABLE IF NOT EXISTS recent_surfaces(uri TEXT PRIMARY KEY,kind INTEGER,last_opened INTEGER);");
            EnsureReplicaSchema();
            EnsureCatalogSchema();
            EnsureCatalogAccountingLocked();
            _read = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path,
                Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            _read.Open();
        }
        catch { _conn.Dispose(); throw; }
    }

    // Children before parents. Every table any earlier schema (≤10 JSON cache, 11 facets, the interim migration
    // bookkeeping) or the current one creates for catalog/replica state. Unknown tables are left alone.
    static readonly string[] ResetTables =
    [
        "catalog_relation_item", "catalog_search", "extension_cache", "catalog_resource", "catalog_scope",
        "replica_playlist_item", "replica_rootlist_entry", "replica_collection_item", "replica_recovery", "replica_state",
        "outbox", "dead_letter",
        "entity", "entities", "localized_entities", "localized_extension_cache", "artist_overview", "video_assoc",
        "entity_refs", "catalog_relation_recovery", "playlists", "playlist_items", "rootlist", "collection_items",
        "collection_rev", "replica_legacy_state", "replica_legacy_recovery",
        "catalog_migration_progress", "catalog_migration_recovery", "catalog_migration_pending",
        "catalog_migration_recovery_chunk", "catalog_migration_row_cursor",
    ];

    void ResetForSchemaLocked()
    {
        using var tx = _conn.BeginTransaction();
        foreach (var table in ResetTables) ExecLocked("DROP TABLE IF EXISTS " + table + ";", tx);
        ExecLocked("DELETE FROM meta WHERE key<>'" + MetaCacheBudget + "';", tx);
        ExecLocked("INSERT OR REPLACE INTO meta(key,value) VALUES('" + MetaVacuumPending + "','1');", tx);
        tx.Commit();
    }

    public string Account => _account;
    public string? MetadataLocale => _spotifyLocale;
    void ExecLocked(string sql, SqliteTransaction? tx = null)
    { using var command = _conn.CreateCommand(); command.Transaction = tx; command.CommandText = sql; command.ExecuteNonQuery(); }

    public async ValueTask<IReadOnlyList<VideoOverride>> ReadVideoOverridesAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            lock (_readLock)
            {
                var result = new List<VideoOverride>();
                using var command = _read.CreateCommand();
                command.CommandText = "SELECT uri,path,id,duration_ms,size,mtime,added_at FROM video_override ORDER BY uri;";
                using var reader = command.ExecuteReader();
                while (reader.Read()) { ct.ThrowIfCancellationRequested(); result.Add(new(reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6))); }
                return (IReadOnlyList<VideoOverride>)result;
            }
        }, ct).ConfigureAwait(false);
    }
    internal void WriteVideoOverride(VideoOverride value)
    {
        lock (_connLock)
            ExecuteReplicaLocked(null, "INSERT INTO video_override VALUES($u,$p,$i,$d,$s,$m,$a) ON CONFLICT(uri) DO UPDATE SET " +
                "path=excluded.path,id=excluded.id,duration_ms=excluded.duration_ms,size=excluded.size,mtime=excluded.mtime,added_at=excluded.added_at;",
                ("$u", value.Uri), ("$p", value.Path), ("$i", value.Id), ("$d", value.DurationMs), ("$s", value.SizeBytes),
                ("$m", value.MTimeUnix), ("$a", value.AddedAtUnix));
    }
    internal void DeleteVideoOverride(string uri)
    { lock (_connLock) ExecuteReplicaLocked(null, "DELETE FROM video_override WHERE uri=$u;", ("$u", uri)); }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_readLock) _read.Dispose();
        lock (_connLock) _conn.Dispose();
    }
}
