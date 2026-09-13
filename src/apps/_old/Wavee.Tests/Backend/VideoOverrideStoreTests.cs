using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Persistence;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// P2 of the universal video overrides — the PERSISTENCE half: the v3→v4 migration, the typed `video_override` table, and
// the same three-tier plumbing (cold write-behind → CachedStore dual-write → InMemoryStore hot mirror) the video↔audio
// association map already rides. What is pinned here:
//   (a) the migration is purely ADDITIVE: an existing library.db keeps every row and only gains the table + version 4,
//   (b) a FRESH db lands on 4 through the same ordered runner (no separate create path to drift),
//   (c) attach/replace/remove survive a full close + reopen (write-behind really reached disk),
//   (d) the hot tier bumps BOTH the playable's uri and the roster sentinel, and elides a no-op removal.
public class VideoOverrideStoreTests
{
    static string TempDb() => Path.Combine(Path.GetTempPath(), "wavee-ovr-" + Guid.NewGuid().ToString("N") + ".db");
    static void TryDelete(string p) { foreach (var f in new[] { p, p + "-wal", p + "-shm" }) { try { File.Delete(f); } catch { } } }

    static VideoOverride Ovr(string uri, string path, string id, long dur = 0, long size = 0, long mtime = 0, long added = 7)
        => new(uri, path, id, dur, size, mtime, added);

    static string SchemaVersion(string path)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT value FROM meta WHERE key='schema_version';";
        return (string)q.ExecuteScalar()!;
    }

    static bool HasTable(string path, string table)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$n;";
        q.Parameters.AddWithValue("$n", table);
        return (long)q.ExecuteScalar()! > 0;
    }

    // ── (a) v3 → v4 ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Migration_V3ToV4_AddsTheOverrideTable_AndLeavesEveryExistingRowIntact()
    {
        var path = TempDb();
        try
        {
            // Seed a v3 database by hand: the tables the ctor creates unconditionally, some real data, and version 3.
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText =
                    "CREATE TABLE entities(uri TEXT PRIMARY KEY, kind INTEGER NOT NULL, payload BLOB NOT NULL);" +
                    "CREATE TABLE video_assoc(uri TEXT PRIMARY KEY, payload BLOB NOT NULL);" +
                    "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);" +
                    "CREATE TABLE collection_items(account TEXT NOT NULL, set_id TEXT NOT NULL, item_uri TEXT NOT NULL, " +
                    "added_at INTEGER NOT NULL DEFAULT 0, position INTEGER, sync INTEGER NOT NULL, PRIMARY KEY(account, set_id, item_uri));" +
                    "INSERT INTO collection_items VALUES('default','liked','spotify:track:keep',42,NULL,0);" +
                    "INSERT INTO meta(key,value) VALUES('schema_version','3');";
                cmd.ExecuteNonQuery();
            }
            Assert.False(HasTable(path, "video_override"));

            using (var cold = new SqliteColdStore(path))
            {
                Assert.Empty(cold.LoadAllVideoOverrides());                 // the new table opens empty, not missing
                Assert.Single(cold.LoadAllSaved());                         // and the pre-existing data is untouched
                Assert.Equal("spotify:track:keep", cold.LoadAllSaved().Single().Uri);
                Assert.Equal(42, cold.LoadAllSaved().Single().AddedAtMs);
            }

            Assert.True(HasTable(path, "video_override"));
            Assert.Equal(SqliteColdStore.CurrentSchemaVersion.ToString(), SchemaVersion(path));   // the ladder keeps walking into the v5 cache-tier consolidation + the v6 membership clock
        }
        finally { TryDelete(path); }
    }

    // ── (b) fresh create ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FreshDatabase_RunsTheWholeLadder_AndLandsOnV4WithTheOverrideTable()
    {
        var path = TempDb();
        try
        {
            using (var cold = new SqliteColdStore(path)) Assert.Empty(cold.LoadAllVideoOverrides());
            Assert.Equal(SqliteColdStore.CurrentSchemaVersion.ToString(), SchemaVersion(path));
            Assert.True(HasTable(path, "video_override"));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void ReopeningACurrentDatabase_IsANoOp()
    {
        var path = TempDb();
        try
        {
            using (var cold = new SqliteColdStore(path)) { cold.UpsertVideoOverride(Ovr("spotify:track:a", @"C:\v\a.mp4", "aaaa")); cold.Flush(); }
            using (var cold = new SqliteColdStore(path)) Assert.Single(cold.LoadAllVideoOverrides());
            Assert.Equal(SqliteColdStore.CurrentSchemaVersion.ToString(), SchemaVersion(path));
        }
        finally { TryDelete(path); }
    }

    // ── (c) the write-behind roundtrip ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ColdStore_AttachReplaceRemove_RoundTripAcrossInstances()
    {
        var path = TempDb();
        try
        {
            using (var cold = new SqliteColdStore(path))
            {
                cold.UpsertVideoOverride(Ovr("spotify:track:a", @"C:\v\a.mp4", "aaaa", dur: 1000, size: 10, mtime: 20, added: 30));
                cold.UpsertVideoOverride(Ovr("spotify:episode:e", @"C:\v\e.mp4", "eeee"));
                cold.Flush();
            }

            using (var cold = new SqliteColdStore(path))
            {
                var rows = cold.LoadAllVideoOverrides().OrderBy(o => o.Uri, StringComparer.Ordinal).ToList();
                Assert.Equal(2, rows.Count);
                Assert.Equal("spotify:episode:e", rows[0].Uri);          // episodes are just another playable uri
                var a = rows[1];
                Assert.Equal(@"C:\v\a.mp4", a.Path);
                Assert.Equal("aaaa", a.Id);
                Assert.Equal(1000, a.DurationMs);
                Assert.Equal(10, a.SizeBytes);
                Assert.Equal(20, a.MTimeUnix);
                Assert.Equal(30, a.AddedAtUnix);
                Assert.Equal("local:video:aaaa", a.SourceKey);

                // uri is the PK, so a second attach onto the same playable IS the replace.
                cold.UpsertVideoOverride(Ovr("spotify:track:a", @"D:\other.mp4", "bbbb"));
                cold.DeleteVideoOverride("spotify:episode:e");
                cold.Flush();
            }

            using (var cold = new SqliteColdStore(path))
            {
                var rows = cold.LoadAllVideoOverrides().ToList();
                Assert.Single(rows);
                Assert.Equal(@"D:\other.mp4", rows[0].Path);
                Assert.Equal("bbbb", rows[0].Id);
            }
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void CachedStore_DualWrites_AndReplaysTheRosterOnStartup()
    {
        var path = TempDb();
        try
        {
            using (var store = new CachedStore(new SqliteColdStore(path)))
            {
                store.UpsertTrack(new Track("t", "spotify:track:t", "T", [], new AlbumRef("", "", ""), 1000, false, null));
                store.UpsertVideoOverride(Ovr("spotify:track:t", @"C:\v\t.mp4", "tttt", size: 99));
                Assert.Equal(@"C:\v\t.mp4", store.GetVideoOverride("spotify:track:t")!.Value.Path);   // hot, synchronously
                store.Flush();
            }

            using (var store = new CachedStore(new SqliteColdStore(path)))
            {
                Assert.Single(store.VideoOverrides());                    // bulk-loaded from the cold tier at startup
                Assert.Equal(99, store.GetVideoOverride("spotify:track:t")!.Value.SizeBytes);
                Assert.Null(store.GetVideoOverride("spotify:track:other"));

                store.RemoveVideoOverride("spotify:track:t");
                Assert.Null(store.GetVideoOverride("spotify:track:t"));
                store.Flush();
            }

            using (var store = new CachedStore(new SqliteColdStore(path)))
                Assert.Empty(store.VideoOverrides());                     // the removal reached disk too
        }
        finally { TryDelete(path); }
    }

    // ── (d) the hot tier's change signals ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HotStore_Bumps_ThePlayableUri_AndTheRosterSentinel()
    {
        var store = new InMemoryStore();
        var seen = new List<string>();
        using var sub = store.Changes.Subscribe(Observers.From<StoreChange>(c => seen.Add(c.Uri)));

        store.UpsertVideoOverride(Ovr("spotify:track:a", @"C:\v\a.mp4", "aaaa"));
        Assert.Equal(new[] { "spotify:track:a", VideoOverride.ChangeKey }, seen.ToArray());

        seen.Clear();
        store.RemoveVideoOverride("spotify:track:a");
        Assert.Equal(new[] { "spotify:track:a", VideoOverride.ChangeKey }, seen.ToArray());

        seen.Clear();
        store.RemoveVideoOverride("spotify:track:a");   // already gone → no-op elision: literal silence
        Assert.Empty(seen);
    }
}
