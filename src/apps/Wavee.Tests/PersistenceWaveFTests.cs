using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

// ── Wave F: the adversarial-review fixes ─────────────────────────────────────────────────────────────────────────────
// Each test here pins ONE finding the Wave-F review confirmed against the landed Waves A–E:
//
//   1. GC EVICTION vs THE COLD-PRESENCE MAP. `CachedStore._coldRows` doubles as "this uri has a row on disk" and as the
//      payload-hash elision key. Nothing told it when the collector DELETED a row, so the bit outlived the row and both
//      guards that read it failed closed: the pin-transition flush short-circuits ("already on disk") and the ordinary
//      re-write elides ("bytes unchanged"). A like → unlike → age-out → re-like sequence therefore stranded the track
//      off disk permanently — critique #1 re-armed by the GC instead of by the write gate.
//   2. THE BYTE BUDGET vs UNEVICTABLE BYTES (open item K1). `cache_bytes` also carries the extension cache and every
//      pinned row; the budget sweep can only delete UNPINNED entity rows. Measuring the ceiling against the gross
//      counter made a budget below that floor unreachable — so the sweep emptied the entire unpinned tier and was
//      STILL over. The trigger is now the evictable bytes, which converges exactly.
//   3. THE FAT MIGRATED ARTIST CORE (open item K2). v4→v5 copies an Artist payload verbatim (the thin split is a
//      write-path transform), so a saved artist that is never re-fetched online kept a fat core forever. The warm
//      replay now splits it once, offline, with no network fetch.
//   4. `temp.gc_pin` COLLISION. ClearMetadataCache and a GC pass both ran on the writer connection under the SAME temp
//      table name, so the Settings escape hatch could drop and rebuild the pin table out from under an interleaved GC.
public class PersistenceWaveFTests
{
    const string Locale = "en";
    const long Day = 24L * 60 * 60;

    static string TempDb() => Path.Combine(Path.GetTempPath(), "wavee-wf-" + Guid.NewGuid().ToString("N") + ".db");
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

    static long Count(string path, string sql)
    {
        using var c = Open(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? 0 : Convert.ToInt64(v);
    }

    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    static Track Trk(string id, string title = "Title") => new(
        id, "spotify:track:" + id, title,
        [new ArtistRef("a", "spotify:artist:a", "Artist")], new AlbumRef("al", "spotify:album:al", "Album"),
        200_000, false, null);

    static byte[] TrackJson(string id, string title = "Title")
        => JsonSerializer.SerializeToUtf8Bytes(Trk(id, title), EntityJson.Default.Track);

    static void Age(string path, string uri, long lastAccess, long updatedAt)
        => Exec(path, $"UPDATE entity SET last_access={lastAccess}, updated_at={updatedAt} WHERE uri='{uri}';");

    // ── 1. the GC must invalidate the cold-presence map ──────────────────────────────────────────────────────────────

    [Fact]
    public void GcEviction_ClearsColdPresence_SoARePinnedEntityReachesDiskAgain()
    {
        string path = TempDb();
        const string uri = "spotify:track:relike";
        try
        {
            long now = Now();
            using (var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale))
            using (var store = new CachedStore(cold))
            {
                // Liked ⇒ pin-reachable ⇒ the write gate persists it, and the presence map learns the uri.
                store.SetSaved("liked", uri, true, SyncState.Confirmed);
                store.UpsertTrack(Trk("relike"));
                store.Flush();
                Assert.Equal(1, Count(path, $"SELECT count(*) FROM entity WHERE uri='{uri}';"));

                // Unliked ⇒ nothing pins it any more.
                store.SetSaved("liked", uri, false, SyncState.Confirmed);
                store.Flush();
                Assert.Equal(0, Count(path, "SELECT count(*) FROM collection_items;"));

                // …it ages past the 30 d TTL and the collector takes it.
                Age(path, uri, now - 60 * Day, now - 60 * Day);
                var report = new EntityCacheGc(cold, store).RunPass(Array.Empty<string>(), CancellationToken.None);
                Assert.True(report.TtlRows >= 1);
                Assert.Equal(0, Count(path, $"SELECT count(*) FROM entity WHERE uri='{uri}';"));

                // The user likes it again. Pre-Wave-F the stale presence bit made the pin-transition flush skip it
                // ("already on disk") AND the payload-hash elision skip the re-write ("bytes unchanged").
                store.SetSaved("liked", uri, true, SyncState.Confirmed);
                store.Flush();
                Assert.Equal(1, Count(path, $"SELECT count(*) FROM entity WHERE uri='{uri}';"));
            }

            // …and it survives the restart, which is the entire point of the pin-transition flush.
            using (var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale))
            using (var store = new CachedStore(cold))
                Assert.NotNull(store.GetTrack(uri));
        }
        finally { TryDelete(path); }
    }

    /// <summary>The same invalidation, at the seam: the sweep reports its victims and the store forgets exactly those.</summary>
    [Fact]
    public void GcSweep_ReportsEvictedUris_AndOnEntitiesEvictedIsPreciseAndNullSafe()
    {
        string path = TempDb();
        try
        {
            long now = Now();
            using var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale);
            using var store = new CachedStore(cold);

            for (int i = 0; i < 3; i++) cold.UpsertEntity("spotify:track:e" + i, EntityKind.Track, TrackJson("e" + i));
            cold.Flush();
            for (int i = 0; i < 3; i++) Age(path, "spotify:track:e" + i, now - 60 * Day, now - 60 * Day);

            var evicted = new List<string>();
            cold.GcBeginPass(Array.Empty<string>());
            var (rows, _) = cold.GcSweepUnpinnedEntities(now - EntityCacheGc.EntityTtlSeconds,
                now - SqliteColdStore.GcNewRowGraceSeconds, CancellationToken.None, batchRows: 1, evicted: evicted);
            cold.GcEndPass();

            Assert.Equal(3, rows);
            Assert.Equal(new[] { "spotify:track:e0", "spotify:track:e1", "spotify:track:e2" },
                evicted.OrderBy(u => u, StringComparer.Ordinal).ToArray());

            store.OnEntitiesEvicted(evicted);   // precise
            store.OnEntitiesEvicted(null);      // the overflow path ("drop the whole map") must be safe too
            store.OnOverviewsEvicted();
        }
        finally { TryDelete(path); }
    }

    // ── 2. the byte budget measures EVICTABLE bytes (K1) ─────────────────────────────────────────────────────────────

    [Fact]
    public void Budget_MeasuresEvictableBytesOnly_SoAnUnreachableCeilingDoesNotNukeTheTier()
    {
        string path = TempDb();
        try
        {
            long now = Now();
            using var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale);
            using var store = new CachedStore(cold);

            cold.UpsertSaved("liked", "spotify:track:p", true, SyncState.Confirmed);
            cold.UpsertEntity("spotify:track:p", EntityKind.Track, TrackJson("p"));
            for (int i = 0; i < 3; i++) cold.UpsertEntity("spotify:track:u" + i, EntityKind.Track, TrackJson("u" + i));
            // The unevictable floor. On the developer's real database this leg is 18.5 MB of extension rows, which is
            // exactly why a 4 MiB budget could never converge before this fix.
            cold.UpsertExtension(new ColdExtension("spotify:track:p", 1, new byte[16 * 1024], "e", 3600, false, now + 3600, now));
            cold.Flush();
            for (int i = 0; i < 3; i++) Age(path, "spotify:track:u" + i, now - 60 * Day, now - 60 * Day);
            Age(path, "spotify:track:p", now - 60 * Day, now - 60 * Day);

            cold.GcBeginPass(Array.Empty<string>());
            long evictable = cold.GcEvictableBytes();
            long gross = cold.GetCacheBytes();
            Assert.True(evictable > 0);
            Assert.True(evictable < gross);              // the extension cache + the pinned row are NOT reclaimable
            long budget = (long)(evictable / 0.9) + 1;   // above everything the sweep may take …
            Assert.True(budget < gross);                 // … and still below the gross counter: unreachable, pre-Wave-F

            var (rows, bytes) = cold.GcEnforceBudget(budget, now - SqliteColdStore.GcNewRowGraceSeconds, CancellationToken.None);
            cold.GcEndPass();

            Assert.Equal(0, rows);
            Assert.Equal(0, bytes);
            Assert.Equal(4, Count(path, "SELECT count(*) FROM entity;"));   // nothing was sacrificed to an impossible goal
        }
        finally { TryDelete(path); }
    }

    /// <summary>The Settings readout must show the number the budget actually governs, not the gross counter.</summary>
    [Fact]
    public void CacheStats_ReportEvictableBytesSeparatelyFromPinnedAndExtensions()
    {
        string path = TempDb();
        try
        {
            long now = Now();
            using var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale);

            cold.UpsertSaved("liked", "spotify:track:p", true, SyncState.Confirmed);
            cold.UpsertEntity("spotify:track:p", EntityKind.Track, TrackJson("p"));
            cold.UpsertEntity("spotify:track:u", EntityKind.Track, TrackJson("u"));
            cold.UpsertExtension(new ColdExtension("spotify:track:p", 1, new byte[8 * 1024], "e", 3600, false, now + 3600, now));
            cold.Flush();

            var s = cold.GetCacheStats();
            Assert.True(s.EntityBytes > 0);
            Assert.True(s.PinnedBytes > 0);
            Assert.Equal(s.EntityBytes - s.PinnedBytes, s.EvictableBytes);
            Assert.True(s.EvictableBytes > 0);
            Assert.True(s.CacheBytes > s.EntityBytes);   // the extension payload rides the gross counter, not the budget
        }
        finally { TryDelete(path); }
    }

    // ── 3. the fat migrated artist core heals on the warm replay (K2) ────────────────────────────────────────────────

    [Fact]
    public void WarmReplay_SplitsAFatMigratedArtistCore_WithoutANetworkFetch()
    {
        string path = TempDb();
        const string uri = "spotify:artist:fat";
        try
        {
            var albums = new List<Album>();
            for (int i = 0; i < 24; i++)
                albums.Add(new Album("a" + i, "spotify:album:fat" + i, "Fat Album " + i,
                    new Image("https://cdn.example/fat" + i + ".jpg"), [new ArtistRef("fa", uri, "Fat Artist")], 2000 + i, 10));
            var fat = new Artist("fa", uri, "Fat Artist", new Image("https://cdn.example/ar.jpg"),
                TopAlbums: albums, Bio: new string('b', 4000), AlbumsTotal: 24);
            byte[] fatJson = JsonSerializer.SerializeToUtf8Bytes(fat, EntityJson.Default.Artist);

            long fatSize;
            // Exactly what the v4 → v5 migration leaves behind: the artist is pin-reachable (a followed artist) and its
            // payload was copied VERBATIM, with no `artist_overview` row.
            using (var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale))
            {
                cold.UpsertSaved("artists", uri, true, SyncState.Confirmed);
                cold.UpsertEntity(uri, EntityKind.Artist, fatJson);
                cold.Flush();
                fatSize = Count(path, $"SELECT size FROM entity WHERE uri='{uri}';");
                Assert.Equal(0, Count(path, "SELECT count(*) FROM artist_overview;"));
            }

            // Next launch: no network, no page open — just the background warm pass over the pin head-set.
            using (var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale))
            using (var store = new CachedStore(cold))
            {
                Assert.True(store.WarmComplete.Wait(TimeSpan.FromSeconds(30)));
                store.Flush();

                // The HOT record is still the whole artist — the split is a persist-time projection, nothing more.
                var hot = store.GetArtist(uri)!;
                Assert.Equal(24, hot.TopAlbums!.Count);
                Assert.Equal("Fat Album 0", hot.TopAlbums[0].Name);
            }

            Assert.True(Count(path, $"SELECT size FROM entity WHERE uri='{uri}';") < fatSize / 2,
                "the migrated fat core must be re-persisted as the thin core");
            Assert.Equal(1, Count(path, "SELECT count(*) FROM artist_overview;"));

            // And a third launch re-fattens from the overview: the discography survives the split intact.
            using (var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale))
            using (var store = new CachedStore(cold))
            {
                Assert.True(store.WarmComplete.Wait(TimeSpan.FromSeconds(30)));
                var again = store.GetArtist(uri)!;
                Assert.Equal("Fat Artist", again.Name);
                Assert.Equal(24, again.TopAlbums!.Count);
                Assert.Equal(24, again.AlbumsTotal);
                Assert.NotNull(again.Bio);
            }
        }
        finally { TryDelete(path); }
    }

    // ── 4. the escape hatch must not stomp an open GC pass's temp tables ─────────────────────────────────────────────

    [Fact]
    public void ClearMetadataCache_DoesNotDisturbAnOpenGcPass()
    {
        string path = TempDb();
        try
        {
            long now = Now();
            using var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale);

            cold.UpsertSaved("liked", "spotify:track:pinned", true, SyncState.Confirmed);
            cold.UpsertEntity("spotify:track:pinned", EntityKind.Track, TrackJson("pinned"));
            cold.UpsertEntity("spotify:track:loose", EntityKind.Track, TrackJson("loose"));
            cold.Flush();

            cold.GcBeginPass(new[] { "spotify:track:exempt" });
            long pinnedBefore = cold.GcPinnedCount();
            Assert.True(pinnedBefore >= 2);   // the liked track + the UI-thread exempt snapshot

            cold.ClearMetadataCache();        // the Settings escape hatch fires mid-pass, on the same writer connection

            // Pre-Wave-F the clear DROPped `temp.gc_pin`, so the pass's own pin table vanished: this query threw
            // "no such table: temp.gc_pin" and the sweep below aborted the whole pass.
            Assert.Equal(pinnedBefore, cold.GcPinnedCount());
            var (rows, _) = cold.GcSweepUnpinnedEntities(now, now, CancellationToken.None);
            cold.GcEndPass();

            Assert.Equal(0, rows);   // the clear already took the only unpinned row
            Assert.Equal(1, Count(path, "SELECT count(*) FROM entity WHERE uri='spotify:track:pinned';"));
        }
        finally { TryDelete(path); }
    }
}
