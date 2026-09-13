using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Wavee.Backend;
using Wavee.Backend.Persistence;
using Wavee.Backend.Playlists;
using Xunit;

namespace Wavee.Tests;

// ── schema v8: rootlist ADD timestamps + the queued rootlist ADD's folder ────────────────────────────────────────────
// Two purely additive columns, both load-bearing for P3:
//   rootlist.added_at    — a folder RENAME resends the marker's ORIGINAL create timestamp (golden b037), so the value
//                          has to survive a restart. Without it every rename after a restart would stamp "now".
//   outbox.parent_folder — a queued create/follow rootlist ADD remembers the FOLDER it belongs in, because the index it
//                          resolves to moves while the op sits in the outbox.
public class ColdStoreSchemaV8Tests
{
    const string Locale = "en";

    static string TempDb() => Path.Combine(Path.GetTempPath(), "wavee-v8-" + Guid.NewGuid().ToString("N") + ".db");
    static void TryDelete(string p) { foreach (var f in new[] { p, p + "-wal", p + "-shm" }) { try { File.Delete(f); } catch { } } }

    static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        c.Open();
        return c;
    }

    static object? Scalar(string path, string sql)
    {
        using var c = Open(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    static void Exec(string path, string sql)
    {
        using var c = Open(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static bool HasColumn(string path, string table, string column)
    {
        using var c = Open(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var r = cmd.ExecuteReader();
        while (r.Read()) if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static string Expected => SqliteColdStore.CurrentSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A v7 file: everything the v7 ladder produces for the two tables this migration touches, plus one row in
    /// each so the ALTERs are exercised against real data rather than empty tables.</summary>
    static void SeedV7(string path)
    {
        Exec(path, """
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE collection_items(account TEXT NOT NULL, set_id TEXT NOT NULL, item_uri TEXT NOT NULL,
                added_at INTEGER NOT NULL DEFAULT 0, position INTEGER, sync INTEGER NOT NULL, PRIMARY KEY(account, set_id, item_uri));
            CREATE TABLE collection_rev(account TEXT NOT NULL, set_id TEXT NOT NULL, revision TEXT, synced_at INTEGER, PRIMARY KEY(account, set_id));
            CREATE TABLE playlists(uri TEXT PRIMARY KEY, base_rev BLOB, adopted_at INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE playlist_items(playlist_uri TEXT NOT NULL, position INTEGER NOT NULL, item_id TEXT,
                item_uri TEXT NOT NULL, added_by TEXT, added_at INTEGER, PRIMARY KEY(playlist_uri, position));
            CREATE TABLE rootlist(account TEXT NOT NULL, position INTEGER NOT NULL, kind INTEGER, uri TEXT, group_name TEXT, depth INTEGER, PRIMARY KEY(account, position));
            CREATE TABLE outbox(id INTEGER PRIMARY KEY, type TEXT NOT NULL, entity_key TEXT NOT NULL, set_id TEXT,
                target_saved INTEGER, op BLOB, base_rev BLOB, attempts INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE dead_letter(id INTEGER PRIMARY KEY, type TEXT, entity_key TEXT, reason TEXT, created_at INTEGER);
            CREATE TABLE video_assoc(uri TEXT PRIMARY KEY, payload BLOB NOT NULL);
            CREATE TABLE video_override(uri TEXT PRIMARY KEY, path TEXT NOT NULL, id TEXT NOT NULL,
                duration_ms INTEGER DEFAULT 0, size INTEGER DEFAULT 0, mtime INTEGER DEFAULT 0, added_at INTEGER DEFAULT 0);
            CREATE TABLE entity(uri TEXT NOT NULL, locale TEXT NOT NULL, kind INTEGER NOT NULL, title TEXT, subtitle TEXT,
                image_url TEXT, duration_ms INTEGER, flags INTEGER NOT NULL DEFAULT 0, album_uri TEXT,
                fmt INTEGER NOT NULL DEFAULT 0, size INTEGER NOT NULL, updated_at INTEGER NOT NULL,
                last_access INTEGER NOT NULL, payload BLOB, PRIMARY KEY(uri, locale));
            CREATE INDEX ix_entity_gc ON entity(kind, last_access);
            CREATE TABLE entity_refs(parent_uri TEXT NOT NULL, child_uri TEXT NOT NULL, PRIMARY KEY(parent_uri, child_uri)) WITHOUT ROWID;
            CREATE TABLE artist_overview(uri TEXT PRIMARY KEY, locale TEXT NOT NULL, fmt INTEGER NOT NULL DEFAULT 1,
                payload BLOB, size INTEGER NOT NULL, fetched_at INTEGER NOT NULL, last_access INTEGER NOT NULL);
            CREATE TABLE recent_surfaces(uri TEXT PRIMARY KEY, kind INTEGER, last_opened INTEGER);
            CREATE TABLE localized_extension_cache(entity_uri TEXT NOT NULL, locale TEXT NOT NULL, extension_kind INTEGER NOT NULL,
                payload BLOB, etag TEXT, offline_ttl INTEGER NOT NULL DEFAULT 0, missing INTEGER NOT NULL DEFAULT 0,
                expires_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, last_access INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(entity_uri, locale, extension_kind));
            CREATE INDEX ix_localized_extension_expiry ON localized_extension_cache(expires_at);
            CREATE INDEX ix_localized_extension_lru ON localized_extension_cache(last_access);
            INSERT INTO meta(key,value) VALUES('schema_version','7');
            INSERT INTO rootlist(account,position,kind,uri,group_name,depth)
                VALUES('wavee',0,1,'spotify:start-group:g1:Trips','Trips',0);
            INSERT INTO outbox(id,type,entity_key,set_id,target_saved,attempts)
                VALUES(41,'rootlist','spotify:playlist:legacy','playlists',1,0);
            """);
    }

    [Fact]
    public void MigrateV7ToV8_AddsBothColumns_KeepsEveryRow_AndIsIdempotent()
    {
        string path = TempDb();
        try
        {
            SeedV7(path);
            Assert.False(HasColumn(path, "rootlist", "added_at"));
            Assert.False(HasColumn(path, "outbox", "parent_folder"));

            using (var cold = new SqliteColdStore(path, "wavee", Locale)) { }

            Assert.True(HasColumn(path, "rootlist", "added_at"));
            Assert.True(HasColumn(path, "outbox", "parent_folder"));
            Assert.Equal(Expected, Scalar(path, "SELECT value FROM meta WHERE key='schema_version';") as string);
            // additive: the pre-existing rows survive, defaulted rather than dropped
            Assert.Equal("spotify:start-group:g1:Trips", Scalar(path, "SELECT uri FROM rootlist WHERE position=0;") as string);
            Assert.Equal(0L, Convert.ToInt64(Scalar(path, "SELECT added_at FROM rootlist WHERE position=0;")));
            Assert.Null(Scalar(path, "SELECT parent_folder FROM outbox WHERE id=41;"));
            Assert.Equal("spotify:playlist:legacy", Scalar(path, "SELECT entity_key FROM outbox WHERE id=41;") as string);

            // reopening runs no second ALTER
            using (var cold = new SqliteColdStore(path, "wavee", Locale)) { }
            Assert.Equal(Expected, Scalar(path, "SELECT value FROM meta WHERE key='schema_version';") as string);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void FreshDatabase_HasBothColumns()
    {
        string path = TempDb();
        try
        {
            using (var cold = new SqliteColdStore(path, "wavee", Locale)) { }
            Assert.True(HasColumn(path, "rootlist", "added_at"));
            Assert.True(HasColumn(path, "outbox", "parent_folder"));
            Assert.Equal(Expected, Scalar(path, "SELECT value FROM meta WHERE key='schema_version';") as string);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void RootlistTimestamps_RoundTripThroughSqlite()
    {
        string path = TempDb();
        try
        {
            using (var cold = new SqliteColdStore(path, "wavee", Locale))
                cold.ReplaceRootlist(new[]
                {
                    new ColdRootlistEntry(0, 1, "spotify:start-group:g1:Trips", "Trips", 0, 1786796469000L),
                    new ColdRootlistEntry(1, 0, "spotify:playlist:p", null, 1, 1786796470000L),
                    new ColdRootlistEntry(2, 2, "spotify:end-group:g1", null, 0, 1786796469000L),
                });

            using (var cold = new SqliteColdStore(path, "wavee", Locale))
            {
                var rows = cold.LoadRootlist();
                Assert.Equal(3, rows.Count);
                Assert.Equal(1786796469000L, rows[0].AddedAtMs);
                Assert.Equal(1786796470000L, rows[1].AddedAtMs);
                Assert.Equal(1786796469000L, rows[2].AddedAtMs);
            }
        }
        finally { TryDelete(path); }
    }

    /// <summary>The whole point of the column: a folder rename issued AFTER a restart still resends the marker's
    /// original create timestamp, because the value came back off disk.</summary>
    [Fact]
    public void RenameAfterRestart_StillCarriesTheOriginalTimestamp()
    {
        string path = TempDb();
        try
        {
            using (var cold = new SqliteColdStore(path, "wavee", Locale))
                cold.ReplaceRootlist(new[]
                {
                    new ColdRootlistEntry(0, 1, "spotify:start-group:g1:Trips", "Trips", 0, 1786796469000L),
                    new ColdRootlistEntry(1, 2, "spotify:end-group:g1", null, 0, 1786796469000L),
                });

            using var reopened = new SqliteColdStore(path, "wavee", Locale);
            using var store = new CachedStore(reopened);
            var ops = RootlistOps.BuildRenameFolder(store.Rootlist(), "g1", "Road trips", nowMs: 999)!;
            Assert.Equal(1786796469000L, ops[1].Items![0].AddedAt);
        }
        finally { TryDelete(path); }
    }
}
