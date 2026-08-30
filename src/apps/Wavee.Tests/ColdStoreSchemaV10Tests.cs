using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Wavee.Backend.Persistence;
using Xunit;

namespace Wavee.Tests;

// ── schema v10: the one-time collection sync-token repair ───────────────────────────────────────────────────────────
// No DDL. Every collection_rev row is deleted once: a pre-v10 token was keyed by LOGICAL set and could have been stored
// by a page walk that lost its tail — which swept the newest members AND parked the token past them, so no delta could
// ever ship them back (the 274-vs-293 Liked Songs drift). Clearing the table makes the next InitialHydrate re-walk each
// WIRE set once under the ledger + shields; the new tokens land under the wire keys.
public class ColdStoreSchemaV10Tests
{
    const string Locale = "en";

    static string TempDb() => Path.Combine(Path.GetTempPath(), "wavee-v10-" + Guid.NewGuid().ToString("N") + ".db");
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

    static string Expected => SqliteColdStore.CurrentSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A v9 file: the v9 ladder's end state (v8 columns + v9 chart columns present) with the drifted install's
    /// shape — per-LOGICAL-set tokens, plus the members those tokens claim to cover, which must survive untouched.</summary>
    static void SeedV9(string path)
    {
        Exec(path, """
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE collection_items(account TEXT NOT NULL, set_id TEXT NOT NULL, item_uri TEXT NOT NULL,
                added_at INTEGER NOT NULL DEFAULT 0, position INTEGER, sync INTEGER NOT NULL, PRIMARY KEY(account, set_id, item_uri));
            CREATE INDEX ix_collection_added ON collection_items(account, set_id, added_at);
            CREATE TABLE collection_rev(account TEXT NOT NULL, set_id TEXT NOT NULL, revision TEXT, synced_at INTEGER, PRIMARY KEY(account, set_id));
            CREATE TABLE playlists(uri TEXT PRIMARY KEY, base_rev BLOB, adopted_at INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE playlist_items(playlist_uri TEXT NOT NULL, position INTEGER NOT NULL, item_id TEXT,
                item_uri TEXT NOT NULL, added_by TEXT, added_at INTEGER,
                chart_status INTEGER NOT NULL DEFAULT 0, chart_current_pos INTEGER NOT NULL DEFAULT 0,
                chart_previous_pos INTEGER NOT NULL DEFAULT 0, chart_rank INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(playlist_uri, position));
            CREATE TABLE rootlist(account TEXT NOT NULL, position INTEGER NOT NULL, kind INTEGER, uri TEXT, group_name TEXT, depth INTEGER,
                added_at INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(account, position));
            CREATE TABLE outbox(id INTEGER PRIMARY KEY, type TEXT NOT NULL, entity_key TEXT NOT NULL, set_id TEXT,
                target_saved INTEGER, op BLOB, base_rev BLOB, attempts INTEGER NOT NULL DEFAULT 0, parent_folder TEXT);
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
            INSERT INTO meta(key,value) VALUES('schema_version','9');
            INSERT INTO collection_rev(account,set_id,revision,synced_at) VALUES('wavee','liked','7,truncated',1700);
            INSERT INTO collection_rev(account,set_id,revision,synced_at) VALUES('wavee','albums','7,truncated',1700);
            INSERT INTO collection_rev(account,set_id,revision,synced_at) VALUES('wavee','artists','3,abc',1700);
            INSERT INTO collection_items(account,set_id,item_uri,added_at,position,sync)
                VALUES('wavee','liked','spotify:track:kept',1755000000000,NULL,1);
            """);
    }

    [Fact]
    public void V10_ClearsCollectionRevisions_KeepsMembers_AndIsIdempotent()
    {
        string path = TempDb();
        try
        {
            SeedV9(path);
            Assert.Equal(3L, Convert.ToInt64(Scalar(path, "SELECT COUNT(*) FROM collection_rev;")));

            using (var cold = new SqliteColdStore(path, "wavee", Locale))
            {
                Assert.Null(cold.GetCollectionRevision("liked"));        // the logical-set tokens are gone …
                Assert.Null(cold.GetCollectionRevision("collection"));   // … and nothing was invented under the wire key
                Assert.Equal("spotify:track:kept", Assert.Single(cold.LoadAllSaved()).Uri);   // members untouched
            }

            Assert.Equal(0L, Convert.ToInt64(Scalar(path, "SELECT COUNT(*) FROM collection_rev;")));
            Assert.Equal(Expected, Scalar(path, "SELECT value FROM meta WHERE key='schema_version';") as string);

            // A token stored AFTER the migration (a verified walk's) persists — the reopen must not clear it again.
            using (var cold = new SqliteColdStore(path, "wavee", Locale))
            {
                cold.SetCollectionRevision("collection", "9,verified", 1800);
                cold.Flush();
            }
            using (var cold = new SqliteColdStore(path, "wavee", Locale))
                Assert.Equal("9,verified", cold.GetCollectionRevision("collection"));
            Assert.Equal(Expected, Scalar(path, "SELECT value FROM meta WHERE key='schema_version';") as string);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void FreshDatabase_IsAtV10_WithNoTokens()
    {
        string path = TempDb();
        try
        {
            using (var cold = new SqliteColdStore(path, "wavee", Locale))
                Assert.Null(cold.GetCollectionRevision("collection"));
            Assert.Equal(Expected, Scalar(path, "SELECT value FROM meta WHERE key='schema_version';") as string);
            Assert.Equal(0L, Convert.ToInt64(Scalar(path, "SELECT COUNT(*) FROM collection_rev;")));
        }
        finally { TryDelete(path); }
    }
}
