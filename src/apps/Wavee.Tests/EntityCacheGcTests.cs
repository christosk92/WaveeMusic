using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Data.Sqlite;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Persistence;
using Wavee.Core;
using EntityKind = Wavee.Backend.Metadata.EntityKind;   // the PERSISTED transport vocabulary (Wavee.Core.EntityKind is the routing one)
using Xunit;

namespace Wavee.Tests;

// ── Wave C: the metadata-cache GC, the escape hatches, and the v6 membership clock ───────────────────────────────────
// What is pinned here:
//   (a) the unpinned-entity TTL deletes stale rows, keeps pinned ones, and honours the 15-minute new-row grace,
//   (b) the byte-budget LRU evicts oldest-last_access-first down to the 0.9 × watermark and never touches a pin,
//   (c) membership GC purges a stale FOREIGN playlist's items AND header while rootlist / recent-surface playlists
//       survive (own-library playlists are always in the rootlist — asserted), and the in-memory mirrors resync,
//   (d) ClearMetadataCache keeps every identity table and every pinned row, and zeroes overviews + extensions,
//   (e) the touch flush stamps last_access at DAY granularity and is a no-op when the uri was already stamped today,
//   (f) the v5 → v6 migration adds + backfills `adopted_at` and is idempotent across a reopen,
//   (g) a GC aborted BETWEEN delete batches leaves cache_bytes == the real SUM(size) (what makes shutdown safe),
//   (h) the album facet strip: the hot record stays fat, the cold row loses the three facet lists, and a re-loaded
//       thin album cannot clobber the fat hot one.
public class EntityCacheGcTests
{
    const string Locale = "en";
    const long Day = 24L * 60 * 60;

    static string TempDb() => Path.Combine(Path.GetTempPath(), "wavee-gc-" + Guid.NewGuid().ToString("N") + ".db");
    static void TryDelete(string p) { foreach (var f in new[] { p, p + "-wal", p + "-shm" }) { try { File.Delete(f); } catch { } } }

    static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        c.Open();
        return c;
    }

    static void Exec(string path, string sql)
    {
        using var c = Open(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static object? Scalar(string path, string sql)
    {
        using var c = Open(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    static long Count(string path, string sql) => Convert.ToInt64(Scalar(path, sql) ?? 0L);

    static List<string> Uris(string path, string sql)
    {
        var list = new List<string>();
        using var c = Open(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    static Track Trk(string id, string title = "Title") => new(
        id, "spotify:track:" + id, title,
        [new ArtistRef("a", "spotify:artist:a", "Artist")], new AlbumRef("al", "spotify:album:al", "Album"),
        200_000, false, null);

    static byte[] TrackJson(string id, string title = "Title")
        => JsonSerializer.SerializeToUtf8Bytes(Trk(id, title), EntityJson.Default.Track);

    // Age a row: `last_access` drives the TTL/LRU ranking, `updated_at` drives the 15-minute new-row grace.
    static void Age(string path, string uri, long lastAccess, long updatedAt)
        => Exec(path, $"UPDATE entity SET last_access={lastAccess}, updated_at={updatedAt} WHERE uri='{uri}';");

    // ── (a) unpinned-entity TTL ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ttl_DeletesStaleUnpinned_KeepsPinnedAndGraced()
    {
        string path = TempDb();
        try
        {
            long now = Now();
            using (var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale))
            using (var store = new CachedStore(cold))
            {
                cold.UpsertSaved("liked", "spotify:track:pinned", true, SyncState.Confirmed);
                cold.UpsertEntity("spotify:track:pinned", EntityKind.Track, TrackJson("pinned"));
                cold.UpsertEntity("spotify:track:stale", EntityKind.Track, TrackJson("stale"));
                cold.UpsertEntity("spotify:track:graced", EntityKind.Track, TrackJson("graced"));
                cold.UpsertEntity("spotify:track:recent", EntityKind.Track, TrackJson("recent"));
                cold.Flush();

                // pinned + stale: untouched for 60 days. graced: equally stale by last_access but WRITTEN a minute ago
                // (critique #11 — a brand-new row is never a victim). recent: touched today.
                Age(path, "spotify:track:pinned", now - 60 * Day, now - 60 * Day);
                Age(path, "spotify:track:stale", now - 60 * Day, now - 60 * Day);
                Age(path, "spotify:track:graced", now - 60 * Day, now - 60);
                Age(path, "spotify:track:recent", now, now - 60 * Day);

                var gc = new EntityCacheGc(cold, store);
                var report = gc.RunPass(Array.Empty<string>(), CancellationToken.None);
                Assert.True(report.TtlRows >= 1);
            }

            Assert.Equal(0, Count(path, "SELECT count(*) FROM entity WHERE uri='spotify:track:stale';"));
            Assert.Equal(1, Count(path, "SELECT count(*) FROM entity WHERE uri='spotify:track:pinned';"));
            Assert.Equal(1, Count(path, "SELECT count(*) FROM entity WHERE uri='spotify:track:graced';"));
            Assert.Equal(1, Count(path, "SELECT count(*) FROM entity WHERE uri='spotify:track:recent';"));
        }
        finally { TryDelete(path); }
    }

    // The in-memory exempt list (the UI-thread BuildPinSet snapshot) must protect a row the SQL pin set cannot see.
    [Fact]
    public void Ttl_HonoursInMemoryExemptSnapshot()
    {
        string path = TempDb();
        try
        {
            long now = Now();
            using (var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale))
            using (var store = new CachedStore(cold))
            {
                cold.UpsertEntity("spotify:track:nowplaying", EntityKind.Track, TrackJson("nowplaying"));
                cold.Flush();
                Age(path, "spotify:track:nowplaying", now - 60 * Day, now - 60 * Day);
                new EntityCacheGc(cold, store).RunPass(new[] { "spotify:track:nowplaying" }, CancellationToken.None);
            }
            Assert.Equal(1, Count(path, "SELECT count(*) FROM entity WHERE uri='spotify:track:nowplaying';"));
        }
        finally { TryDelete(path); }
    }

    // ── (b) byte-budget LRU ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Budget_EvictsOldestFirst_ToWatermark_ExemptingPins()
    {
        string path = TempDb();
        try
        {
            long now = Now();
            using var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale);
            using var store = new CachedStore(cold);

            cold.UpsertSaved("liked", "spotify:track:p0", true, SyncState.Confirmed);
            cold.UpsertEntity("spotify:track:p0", EntityKind.Track, TrackJson("p0"));
            for (int i = 1; i <= 6; i++) cold.UpsertEntity("spotify:track:u" + i, EntityKind.Track, TrackJson("u" + i));
            cold.Flush();

            // The pinned row is the OLDEST, so a pin-blind LRU would take it first. Everything is far outside the grace.
            Age(path, "spotify:track:p0", now - 90 * Day, now - 90 * Day);
            for (int i = 1; i <= 6; i++) Age(path, "spotify:track:u" + i, now - (10 - i) * Day, now - 90 * Day);

            long rowSize = Count(path, "SELECT size FROM entity WHERE uri='spotify:track:u1';");
            Assert.True(rowSize > 0);

            cold.GcBeginPass(Array.Empty<string>());
            // The budget governs the EVICTABLE bytes (Wave F / K1) — the pinned row and the extension cache are not
            // reclaimable, so measuring them against the ceiling could never converge. Ask for a ceiling whose 0.9
            // watermark sits two rows below what is actually evictable.
            long evictable = cold.GcEvictableBytes();
            Assert.True(evictable > 0 && evictable < cold.GetCacheBytes());   // the pinned row is excluded
            long budget = (long)((evictable - 2 * rowSize) / 0.9);
            long watermark = (long)(budget * 0.9);
            var (rows, bytes) = cold.GcEnforceBudget(budget, now - SqliteColdStore.GcNewRowGraceSeconds, CancellationToken.None, batchRows: 1);
            long after = cold.GcEvictableBytes();
            cold.GcEndPass();

            Assert.True(rows > 0);
            Assert.True(bytes > 0);
            Assert.True(after <= watermark);
            Assert.Equal(1, Count(path, "SELECT count(*) FROM entity WHERE uri='spotify:track:p0';"));   // pin exempt

            // Oldest-first: the survivors must be a SUFFIX of the last_access ordering (u1 is oldest … u6 newest).
            var survivors = Uris(path, "SELECT uri FROM entity WHERE uri LIKE 'spotify:track:u%' ORDER BY last_access;");
            int firstSurvivor = survivors.Count == 0 ? 7 : int.Parse(survivors[0]["spotify:track:u".Length..]);
            for (int i = 1; i < firstSurvivor; i++)
                Assert.Equal(0, Count(path, $"SELECT count(*) FROM entity WHERE uri='spotify:track:u{i}';"));
            for (int i = 0; i < survivors.Count; i++)
                Assert.Equal("spotify:track:u" + (firstSurvivor + i), survivors[i]);
        }
        finally { TryDelete(path); }
    }

    // ── (c) membership GC (critique #6 / locked decision 11) ─────────────────────────────────────────────────────────

    [Fact]
    public void MembershipGc_PurgesStaleForeignPlaylist_KeepsRootlistAndRecentSurface()
    {
        string path = TempDb();
        const string own = "spotify:playlist:own";       // in the rootlist ⇒ a library playlist ⇒ NEVER a victim
        const string recent = "spotify:playlist:recent"; // a recently opened surface
        const string foreign = "spotify:playlist:foreign";
        try
        {
            long now = Now();
            using (var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale))
            using (var store = new CachedStore(cold))
            {
                foreach (var pl in new[] { own, recent, foreign })
                    cold.ReplaceMembership(pl, [new ColdPlaylistItem("i1", "spotify:track:m-" + pl[^3..], null, 0)], null);
                cold.ReplaceRootlist([new ColdRootlistEntry(0, 0, own, null, 0)]);
                cold.UpsertRecentSurface(recent, 0, now);

                // Every one of them was adopted 20 days ago — only the reachability rule may separate them.
                Exec(path, $"UPDATE playlists SET adopted_at={now - 20 * Day};");

                var report = new EntityCacheGc(cold, store).RunPass(Array.Empty<string>(), CancellationToken.None);
                Assert.Equal(1, report.MembershipPlaylists);

                // The mirrors resynced: the purged membership is gone from the resident tier too.
                Assert.Empty(store.Membership(foreign));
                Assert.NotEmpty(store.Membership(own));
            }

            Assert.Equal(0, Count(path, $"SELECT count(*) FROM playlist_items WHERE playlist_uri='{foreign}';"));
            Assert.Equal(0, Count(path, $"SELECT count(*) FROM playlists WHERE uri='{foreign}';"));
            Assert.Equal(1, Count(path, $"SELECT count(*) FROM playlists WHERE uri='{own}';"));
            Assert.Equal(1, Count(path, $"SELECT count(*) FROM playlist_items WHERE playlist_uri='{own}';"));
            Assert.Equal(1, Count(path, $"SELECT count(*) FROM playlists WHERE uri='{recent}';"));
            Assert.Equal(1, Count(path, $"SELECT count(*) FROM playlist_items WHERE playlist_uri='{recent}';"));
        }
        finally { TryDelete(path); }
    }

    // A freshly adopted foreign playlist is inside the 14 d window and must survive; a pending outbox intent pins it
    // regardless of age (deleting `playlists.base_rev` under an in-flight OpRebase would break the mutation).
    [Fact]
    public void MembershipGc_KeepsFreshAndOutboxPinnedPlaylists()
    {
        string path = TempDb();
        const string fresh = "spotify:playlist:fresh";
        const string pending = "spotify:playlist:pending";
        try
        {
            long now = Now();
            using (var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale))
            using (var store = new CachedStore(cold))
            {
                cold.ReplaceMembership(fresh, [new ColdPlaylistItem("i", "spotify:track:f", null, 0)], null);
                cold.ReplaceMembership(pending, [new ColdPlaylistItem("i", "spotify:track:p", null, 0)], null);
                Exec(path, $"UPDATE playlists SET adopted_at={now - 20 * Day} WHERE uri='{pending}';");
                Exec(path, $"INSERT INTO outbox(id,type,entity_key,set_id,target_saved,attempts) VALUES(1,'oprebase','{pending}','',0,0);");

                var report = new EntityCacheGc(cold, store).RunPass(Array.Empty<string>(), CancellationToken.None);
                Assert.Equal(0, report.MembershipPlaylists);
            }
            Assert.Equal(1, Count(path, $"SELECT count(*) FROM playlists WHERE uri='{fresh}';"));
            Assert.Equal(1, Count(path, $"SELECT count(*) FROM playlists WHERE uri='{pending}';"));
        }
        finally { TryDelete(path); }
    }

    // ── (d) ClearMetadataCache (§G escape hatch) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClearMetadataCache_KeepsIdentityAndPinnedRows_ZeroesOverviewsAndExtensions()
    {
        string path = TempDb();
        try
        {
            using (var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale))
            using (var store = new CachedStore(cold))
            {
                cold.UpsertSaved("liked", "spotify:track:pinned", true, SyncState.Confirmed);
                cold.UpsertEntity("spotify:track:pinned", EntityKind.Track, TrackJson("pinned"));
                cold.UpsertEntity("spotify:track:loose", EntityKind.Track, TrackJson("loose"));
                cold.UpsertExtension(new ColdExtension("spotify:track:pinned", 1, Encoding.UTF8.GetBytes("payload"),
                    "etag", 3600, false, Now() + 3600, Now()));
                cold.Flush();
                cold.UpsertArtistOverview("spotify:artist:a", Locale, Encoding.UTF8.GetBytes("{}"), Now());
                cold.ReplaceMembership("spotify:playlist:p", [new ColdPlaylistItem("i", "spotify:track:m", null, 0)], null);
                cold.ReplaceRootlist([new ColdRootlistEntry(0, 0, "spotify:playlist:p", null, 0)]);
                cold.UpsertVideoOverride(new VideoOverride("spotify:track:pinned", @"C:\v.mp4", "id", 1, 2, 3, 4));
                cold.Flush();

                // The §G diagnostics query (the Settings → Storage readout) must actually execute — its pin subquery is
                // the only place the whole pin UNION is inlined into an IN(...).
                var before = cold.GetCacheStats();
                Assert.True(before.DbBytes > 0);
                Assert.True(before.EntityRows >= 2);
                Assert.True(before.PinnedRows >= 1);
                Assert.Equal(SqliteColdStore.DefaultCacheBudgetBytes, before.BudgetBytes);
                cold.SetCacheBudgetBytes(128L << 20);
                Assert.Equal(128L << 20, cold.GetCacheStats().BudgetBytes);

                var (rows, _) = store.ClearMetadataCache();
                Assert.True(rows >= 1);
                var after = cold.GetCacheStats();
                Assert.Equal(0, after.OverviewRows);
                Assert.Equal(0, after.ExtensionRows);
            }

            Assert.Equal(1, Count(path, "SELECT count(*) FROM entity WHERE uri='spotify:track:pinned';"));
            Assert.Equal(0, Count(path, "SELECT count(*) FROM entity WHERE uri='spotify:track:loose';"));
            Assert.Equal(0, Count(path, "SELECT count(*) FROM artist_overview;"));
            Assert.Equal(0, Count(path, "SELECT count(*) FROM localized_extension_cache;"));
            // IDENTITY TABLES ARE SACRED (locked decision 2).
            Assert.Equal(1, Count(path, "SELECT count(*) FROM collection_items;"));
            Assert.Equal(1, Count(path, "SELECT count(*) FROM playlists;"));
            Assert.Equal(1, Count(path, "SELECT count(*) FROM playlist_items;"));
            Assert.Equal(1, Count(path, "SELECT count(*) FROM rootlist;"));
            Assert.Equal(1, Count(path, "SELECT count(*) FROM video_override;"));
        }
        finally { TryDelete(path); }
    }

    // ── (e) touch tracking (§C.5) ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Touch_StampsLastAccessAtDayGranularity_AndIsNoOpWhenAlreadyToday()
    {
        string path = TempDb();
        const string uri = "spotify:track:t";
        try
        {
            long today = CachedStore.TouchDayOf(Now());
            using var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale);
            using var store = new CachedStore(cold);

            store.SetSaved("liked", uri, true, SyncState.Confirmed);   // pin-reachable → the write gate lets it through
            store.UpsertTrack(Trk("t"));
            store.Flush();
            Exec(path, $"UPDATE entity SET last_access=0 WHERE uri='{uri}';");

            Assert.NotNull(store.GetTrack(uri));   // hot hit → records the touch
            store.FlushTouches();
            Assert.Equal(today, Count(path, $"SELECT last_access FROM entity WHERE uri='{uri}';"));   // DAY-truncated

            // Second read on the same day must not enqueue anything at all: the injected sentinel survives.
            Exec(path, $"UPDATE entity SET last_access=12345 WHERE uri='{uri}';");
            Assert.NotNull(store.GetTrack(uri));
            store.FlushTouches();
            Assert.Equal(12345, Count(path, $"SELECT last_access FROM entity WHERE uri='{uri}';"));
        }
        finally { TryDelete(path); }
    }

    // ── (f) the v5 → v6 migration ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The version the runner must land on: <c>CurrentSchemaVersion</c>, not the literal "6". Opening a v5 file
    /// runs the WHOLE ordered chain in one pass (v5→v6→v7→…), so hard-coding this step's own number made the test fail
    /// the moment a later migration was added — which says nothing about the v6 step this case is actually about.</summary>
    static string ExpectedSchemaVersion =>
        SqliteColdStore.CurrentSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void MigrateV6_AddsAndBackfillsAdoptedAt_AndIsIdempotent()
    {
        string path = TempDb();
        try
        {
            SeedV5(path);
            Assert.False(HasColumn(path, "playlists", "adopted_at"));

            long stamped;
            using (var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale)) { }
            Assert.True(HasColumn(path, "playlists", "adopted_at"));
            Assert.Equal(ExpectedSchemaVersion, Scalar(path, "SELECT value FROM meta WHERE key='schema_version';") as string);
            stamped = Count(path, "SELECT adopted_at FROM playlists WHERE uri='spotify:playlist:legacy';");
            Assert.True(stamped > 0, "the backfill must date existing playlists to now, not leave them instantly stale");

            // Reopen: no second ALTER, no re-stamp.
            using (var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale)) { }
            Assert.Equal(ExpectedSchemaVersion, Scalar(path, "SELECT value FROM meta WHERE key='schema_version';") as string);
            Assert.Equal(stamped, Count(path, "SELECT adopted_at FROM playlists WHERE uri='spotify:playlist:legacy';"));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void ReplaceMembership_StampsAdoptedAt()
    {
        string path = TempDb();
        try
        {
            long before = Now();
            using (var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale))
                cold.ReplaceMembership("spotify:playlist:x", [new ColdPlaylistItem("i", "spotify:track:a", null, 0)], null);
            Assert.True(Count(path, "SELECT adopted_at FROM playlists WHERE uri='spotify:playlist:x';") >= before);
        }
        finally { TryDelete(path); }
    }

    // ── (g) aborting between DELETE batches leaves a consistent database ─────────────────────────────────────────────
    // This is the property that makes app-shutdown cancellation safe: every batch is its own transaction and maintains
    // cache_bytes inside it, so ANY prefix of the batch sequence is a valid database state.
    [Fact]
    public void Gc_AbortedBetweenBatches_LeavesCacheBytesConsistent()
    {
        string path = TempDb();
        try
        {
            long now = Now();
            using var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale);
            using var store = new CachedStore(cold);

            for (int i = 0; i < 5; i++) cold.UpsertEntity("spotify:track:s" + i, EntityKind.Track, TrackJson("s" + i));
            cold.Flush();
            for (int i = 0; i < 5; i++) Age(path, "spotify:track:s" + i, now - 60 * Day, now - 60 * Day);

            cold.GcBeginPass(Array.Empty<string>());
            // Two one-row batches, then "cancelled" — exactly the aborted-mid-sweep state.
            var (rows, _) = cold.GcSweepUnpinnedEntities(now - EntityCacheGc.EntityTtlSeconds,
                now - SqliteColdStore.GcNewRowGraceSeconds, CancellationToken.None, batchRows: 1, maxBatches: 2);
            cold.GcEndPass();

            Assert.Equal(2, rows);
            Assert.Equal(3, Count(path, "SELECT count(*) FROM entity;"));
            long truth = Count(path, "SELECT IFNULL(SUM(size),0) FROM entity;")
                       + Count(path, "SELECT IFNULL(SUM(size),0) FROM artist_overview;")
                       + Count(path, "SELECT IFNULL(SUM(length(payload)),0) FROM localized_extension_cache;");
            Assert.Equal(truth, cold.GetCacheBytes());

            // An already-cancelled token deletes nothing and still leaves the counter honest.
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            cold.GcBeginPass(Array.Empty<string>());
            var (none, _) = cold.GcSweepUnpinnedEntities(now - EntityCacheGc.EntityTtlSeconds,
                now - SqliteColdStore.GcNewRowGraceSeconds, cts.Token, batchRows: 1);
            cold.GcEndPass();
            Assert.Equal(0, none);
            Assert.Equal(3, Count(path, "SELECT count(*) FROM entity;"));
            Assert.Equal(truth, cold.GetCacheBytes());
        }
        finally { TryDelete(path); }
    }

    // ── (h) the album facet strip (design §D.1) ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void AlbumFacets_AreStrippedAtPersist_HotStaysFat_ReloadDoesNotClobber()
    {
        string path = TempDb();
        const string uri = "spotify:album:fat";
        try
        {
            using var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale);
            using var store = new CachedStore(cold);

            var fat = new Album("id", uri, "Fat Album", new Image("https://cdn/a.jpg"),
                [new ArtistRef("a", "spotify:artist:a", "Artist")], 2021, 2,
                Tracks: [Trk("t1"), Trk("t2")],
                MoreByArtist: [new Album("m", "spotify:album:more", "More", null, [], 2020, 1)],
                Label: "Label", Copyright: "(C) 2021",
                ArtistsDetailed: [new Artist("a", "spotify:artist:a", "Artist", null)],
                OtherVersions: [new Album("o", "spotify:album:other", "Deluxe", null, [], 2022, 1)],
                Hydration: AlbumHydrationLevel.Full);

            store.SetSaved("albums", uri, true, SyncState.Confirmed);   // pin it so the write gate persists it
            store.UpsertAlbum(fat);
            store.Flush();

            // HOT stays fat — the split is a persist-time projection, nothing more.
            var hot = store.GetAlbum(uri)!;
            Assert.NotNull(hot.Tracks);
            Assert.NotNull(hot.MoreByArtist);
            Assert.NotNull(hot.ArtistsDetailed);
            Assert.NotNull(hot.OtherVersions);
            Assert.Equal(AlbumHydrationLevel.Full, hot.Hydration);

            // COLD is thin: no track list, none of the three facet lists, and Hydration capped so the below-the-fold
            // getAlbum upgrade still fires after a restart (it is what rebuilds them).
            var row = cold.GetEntity(uri);
            Assert.NotNull(row);
            var stored = JsonSerializer.Deserialize(row!.Value.Payload, EntityJson.Default.Album)!;
            Assert.Null(stored.Tracks);
            Assert.Null(stored.MoreByArtist);
            Assert.Null(stored.ArtistsDetailed);
            Assert.Null(stored.OtherVersions);
            Assert.NotEqual(AlbumHydrationLevel.Full, stored.Hydration);
            Assert.Equal("Label", stored.Label);          // the scalar "About this release" facts DO persist
            Assert.Equal("Fat Album", stored.Name);

            // And re-upserting that thin row (the cold-fallback promote) must not clobber the fat hot record.
            store.UpsertAlbum(stored);
            var after = store.GetAlbum(uri)!;
            Assert.NotNull(after.MoreByArtist);
            Assert.NotNull(after.ArtistsDetailed);
            Assert.NotNull(after.OtherVersions);
            Assert.Equal(AlbumHydrationLevel.Full, after.Hydration);
        }
        finally { TryDelete(path); }
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────────────────────

    static bool HasColumn(string path, string table, string column)
    {
        using var c = Open(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var r = cmd.ExecuteReader();
        while (r.Read()) if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // A schema-v5 database by hand: the end-state tables, but `playlists` WITHOUT the v6 `adopted_at` column.
    static void SeedV5(string path)
    {
        Exec(path, """
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE video_assoc(uri TEXT PRIMARY KEY, payload BLOB NOT NULL);
            CREATE TABLE collection_items(account TEXT NOT NULL, set_id TEXT NOT NULL, item_uri TEXT NOT NULL,
                added_at INTEGER NOT NULL DEFAULT 0, position INTEGER, sync INTEGER NOT NULL, PRIMARY KEY(account, set_id, item_uri));
            CREATE TABLE collection_rev(account TEXT NOT NULL, set_id TEXT NOT NULL, revision TEXT, synced_at INTEGER, PRIMARY KEY(account, set_id));
            CREATE TABLE playlists(uri TEXT PRIMARY KEY, base_rev BLOB);
            CREATE TABLE playlist_items(playlist_uri TEXT NOT NULL, position INTEGER NOT NULL, item_id TEXT,
                item_uri TEXT NOT NULL, added_by TEXT, added_at INTEGER, PRIMARY KEY(playlist_uri, position));
            CREATE TABLE rootlist(account TEXT NOT NULL, position INTEGER NOT NULL, kind INTEGER, uri TEXT, group_name TEXT, depth INTEGER, PRIMARY KEY(account, position));
            CREATE TABLE outbox(id INTEGER PRIMARY KEY, type TEXT NOT NULL, entity_key TEXT NOT NULL, set_id TEXT, target_saved INTEGER, op BLOB, base_rev BLOB, attempts INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE dead_letter(id INTEGER PRIMARY KEY, type TEXT, entity_key TEXT, reason TEXT, created_at INTEGER);
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
                expires_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, PRIMARY KEY(entity_uri, locale, extension_kind));
            CREATE INDEX ix_localized_extension_expiry ON localized_extension_cache(expires_at);
            INSERT INTO meta(key,value) VALUES('schema_version','5');
            INSERT INTO playlists(uri,base_rev) VALUES('spotify:playlist:legacy',NULL);
            """);
    }
}
