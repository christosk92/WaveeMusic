using System;
using System.Linq;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Backend.Persistence;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class CatalogRetentionTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    static ResourceKey Key(string uri, FacetKind facet = FacetKind.TrackIdentity, ResourceArguments args = default)
        => new(LibrarySearchFixture.Scope, uri, facet, args);
    static CatalogRecord Record(ResourceKey key, CatalogValue value) => LibrarySearchFixture.Record(key, value);

    [Fact]
    public async Task PinsTakeExactlyOneRelationHop_AndKeepActiveFacts()
    {
        using var db = new CatalogTestDb(); using var storage = new SqliteColdStore(db.Path);
        var artist = Key("spotify:artist:a", FacetKind.ArtistIdentity);
        var album = Key("spotify:album:b", FacetKind.AlbumIdentity);
        var track = Key("spotify:track:c");
        await storage.CommitAsync(new CatalogCommit(1, [Record(artist, new ArtistIdentityValue("Artist")), Record(album, new AlbumIdentityValue("Album")),
            Record(track, new TrackIdentityValue("Track")),
            Record(Key(artist.Subject, FacetKind.ArtistDiscography, new(0, 50)), new RelationPageValue(FacetKind.ArtistDiscography, "artist", null, 0, 1, null, RelationCoverage.Complete, [new("album", album.Subject)])),
            Record(Key(album.Subject, FacetKind.AlbumTracks, new(0, 50)), new RelationPageValue(FacetKind.AlbumTracks, "album", null, 0, 1, null, RelationCoverage.Complete, [new("track", track.Subject)]))]), TestContext.Current.CancellationToken);
        Age(db, 60);
        var evicted = storage.RunCatalogGcBatch([artist], long.MaxValue, Now, false);
        Assert.Contains(track, evicted.Resources);
        Assert.DoesNotContain(artist, evicted.Resources); Assert.DoesNotContain(album, evicted.Resources);
        Assert.NotNull(await storage.ReadOneAsync(album, TestContext.Current.CancellationToken));
        Assert.Null(await storage.ReadOneAsync(track, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PinnedBytes_DoNotConsumeTheEvictableBudget_AndStatsReportTheRealFloor()
    {
        using var db = new CatalogTestDb(); using var storage = new SqliteColdStore(db.Path);
        var pinned = Key("spotify:track:pinned");
        var unpinned = Key("spotify:track:unpinned");
        await storage.CommitAsync(new CatalogCommit(1, [Record(pinned, new TrackIdentityValue("Pinned")), Record(unpinned, new TrackIdentityValue("Unpinned"))],
            [new(pinned.Scope, pinned.Subject, 186, "etag", new byte[64 * 1024], Now.AddDays(-1))]), TestContext.Current.CancellationToken);
        Age(db, 1);
        var report = storage.RunCatalogGcBatch([pinned], 4096, Now, false);
        Assert.Equal(0, report.Rows);
        var stats = storage.ReadCatalogStats(4096, [pinned], Now);
        Assert.True(stats.PinnedBytes >= 64 * 1024);
        Assert.Equal(1, stats.PinnedRows);
        Assert.InRange(stats.EvictableBytes, 1, 4096);
        Assert.True(stats.CacheBytes > stats.BudgetBytes);
    }

    [Fact]
    public async Task TtlSelection_SkipsNonVictimsBeforeTheBatchLimit()
    {
        using var db = new CatalogTestDb(); using var storage = new SqliteColdStore(db.Path);
        var rows = Enumerable.Range(0, 1001).Select(i => Record(Key("spotify:track:fresh" + i), new TrackIdentityValue("Fresh"))).ToList();
        var stale = Key("spotify:artist:overview", FacetKind.ArtistOverview);
        rows.Add(Record(stale, new ArtistOverviewValue(Bio: "Expired overview")));
        await storage.CommitAsync(new CatalogCommit(1, rows), TestContext.Current.CancellationToken);
        Age(db, 20);
        db.Exec("UPDATE catalog_resource SET last_access=$stamp WHERE facet=21;", ("$stamp", Now.AddDays(-8).ToUnixTimeMilliseconds()));
        var report = storage.RunCatalogGcBatch([], long.MaxValue, Now, false);
        Assert.Equal(stale, Assert.Single(report.Resources));
        Assert.Equal(1001, db.Number("SELECT COUNT(*) FROM catalog_resource;"));
    }

    [Fact]
    public async Task EvictingTransportBytes_AlsoRemovesTheirFreshDocumentPointerAtomically()
    {
        using var db = new CatalogTestDb(); using var storage = new SqliteColdStore(db.Path);
        const string uri = "spotify:track:document";
        var pointer = Key(uri, FacetKind.ExtensionDocument, new(Filter: "186"));
        var identity = Key(uri);
        await storage.CommitAsync(new CatalogCommit(1, [Record(pointer, new ExtensionDocumentValue(186, "etag")), Record(identity, new TrackIdentityValue("Track"))],
            [new(pointer.Scope, uri, 186, "etag", [1, 2, 3], Now.AddDays(-60))]), TestContext.Current.CancellationToken);
        db.Exec("UPDATE catalog_resource SET last_access=$now,updated_at=$now;", ("$now", Now.ToUnixTimeMilliseconds()));
        var report = storage.RunCatalogGcBatch([], long.MaxValue, Now, false);
        Assert.Contains(pointer, report.Resources);
        Assert.Null(await storage.ReadOneAsync(pointer, TestContext.Current.CancellationToken));
        Assert.NotNull(await storage.ReadOneAsync(identity, TestContext.Current.CancellationToken));
        Assert.Null(await storage.ReadTransportAsync(pointer.Scope, uri, 186, TestContext.Current.CancellationToken));
        AssertAccounting(db);
    }

    [Fact]
    public async Task RelationReplacementAndClear_KeepByteCountersAndSearchRowsExact()
    {
        using var db = new CatalogTestDb(); using var storage = new SqliteColdStore(db.Path);
        var identity = Key("spotify:album:a", FacetKind.AlbumIdentity);
        var relation = Key(identity.Subject, FacetKind.AlbumTracks, new(0, 50));
        RelationPageValue Page(params string[] uris) => new(FacetKind.AlbumTracks, "page", null, 0, uris.Length,
            null, RelationCoverage.Complete, uris.Select((uri, i) => new CatalogRelationItem(i.ToString(), uri)).ToArray());
        await storage.CommitAsync(new CatalogCommit(1, [Record(identity, new AlbumIdentityValue("Album")), Record(relation, Page("spotify:track:a", "spotify:track:b"))]), TestContext.Current.CancellationToken);
        AssertAccounting(db);
        await storage.CommitAsync(new CatalogCommit(2, [Record(relation, Page("spotify:track:b"))]), TestContext.Current.CancellationToken);
        Assert.Equal(1, db.Number("SELECT COUNT(*) FROM catalog_relation_item;"));
        AssertAccounting(db);
        storage.RunCatalogGcBatch([], long.MaxValue, Now, true);
        Assert.Equal(0, db.Number("SELECT COUNT(*) FROM catalog_resource;"));
        Assert.Equal(0, db.Number("SELECT COUNT(*) FROM catalog_relation_item;"));
        Assert.Equal(0, db.Number("SELECT COUNT(*) FROM catalog_search;"));
        AssertAccounting(db);
    }

    [Fact]
    public async Task IdleCompaction_HonorsActiveDemandAndClearsItsMarkerOnlyOnce()
    {
        using var db = new CatalogTestDb();
        // A schema reset is what sets cache_vacuum_pending; seed an older version so opening triggers one.
        db.Exec("CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT); INSERT INTO meta VALUES('schema_version','11');");
        using var storage = new SqliteColdStore(db.Path);
        Assert.False(await storage.CompactCatalogIfIdleAsync(true, TestContext.Current.CancellationToken));
        Assert.Equal(1, db.Number("SELECT value FROM meta WHERE key='cache_vacuum_pending';"));
        Assert.True(await storage.CompactCatalogIfIdleAsync(false, TestContext.Current.CancellationToken));
        Assert.False(await storage.CompactCatalogIfIdleAsync(false, TestContext.Current.CancellationToken));
        Assert.Equal(0, db.Number("SELECT COUNT(*) FROM meta WHERE key='cache_vacuum_pending';"));
    }

    [Fact]
    public async Task FollowedPlaylistMembers_SurviveTheTtlSweep()
    {
        // Finding #7: a followed rootlist playlist's tracks are pinned directly from `replica_playlist_item`,
        // not via the (unrelated) `catalog_relation_item` hop — playlists never appear as relation parents.
        using var db = new CatalogTestDb(); using var storage = new SqliteColdStore(db.Path);
        var member = Key("spotify:track:member");
        var unrelated = Key("spotify:track:unrelated");
        await storage.CommitAsync(new CatalogCommit(1, [Record(member, new TrackIdentityValue("Member")),
            Record(unrelated, new TrackIdentityValue("Unrelated"))]), TestContext.Current.CancellationToken);
        Age(db, 60);
        db.Exec("INSERT INTO replica_rootlist_entry VALUES('default','account',0,0,'spotify:playlist:p',NULL,0,0);");
        db.Exec("INSERT INTO replica_playlist_item VALUES('default','account','spotify:playlist:p',0,'i0','spotify:track:member',NULL,0,0,0,0,0);");

        var evicted = storage.RunCatalogGcBatch([], long.MaxValue, Now, false);

        Assert.Contains(unrelated, evicted.Resources);
        Assert.DoesNotContain(member, evicted.Resources);
        Assert.NotNull(await storage.ReadOneAsync(member, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RelationHop_IsBoundedPerRoot_SoOneHugeRootDoesNotStarveTheOthers()
    {
        // Finding #7: the single relation hop used to cap at a GLOBAL 5,000 across every root; ordered by
        // (scope_id, parent_subject, ordinal), one 5,000-item root would consume the entire budget and leave a
        // second root with nothing. The bound is now per root/parent.
        using var db = new CatalogTestDb(); using var storage = new SqliteColdStore(db.Path);
        var artistA = Key("spotify:artist:a", FacetKind.ArtistIdentity);
        var artistB = Key("spotify:artist:b", FacetKind.ArtistIdentity);
        var relA = Key(artistA.Subject, FacetKind.ArtistDiscography, new(0, 50));
        var relB = Key(artistB.Subject, FacetKind.ArtistDiscography, new(0, 50));
        static string ChildUri(char root, int i) => $"spotify:track:{root}{i:0000}";
        var itemsA = Enumerable.Range(0, 5000).Select(i => new CatalogRelationItem(i.ToString(), ChildUri('a', i))).ToArray();
        var itemsB = Enumerable.Range(0, 1000).Select(i => new CatalogRelationItem(i.ToString(), ChildUri('b', i))).ToArray();

        var records = new List<CatalogRecord>
        {
            Record(artistA, new ArtistIdentityValue("A")), Record(artistB, new ArtistIdentityValue("B")),
            Record(relA, new RelationPageValue(FacetKind.ArtistDiscography, "artist", null, 0, itemsA.Length, null, RelationCoverage.Complete, itemsA)),
            Record(relB, new RelationPageValue(FacetKind.ArtistDiscography, "artist", null, 0, itemsB.Length, null, RelationCoverage.Complete, itemsB)),
        };
        for (int i = 0; i < itemsA.Length; i++) records.Add(Record(Key(ChildUri('a', i)), new TrackIdentityValue("A" + i)));
        for (int i = 0; i < itemsB.Length; i++) records.Add(Record(Key(ChildUri('b', i)), new TrackIdentityValue("B" + i)));
        await storage.CommitAsync(new CatalogCommit(1, records), TestContext.Current.CancellationToken);
        Age(db, 60);

        var evicted = storage.RunCatalogGcBatch([artistA, artistB], long.MaxValue, Now, false);
        var evictedUris = evicted.Resources.Select(r => r.Subject).ToHashSet();

        Assert.DoesNotContain(ChildUri('a', 0), evictedUris);
        Assert.DoesNotContain(ChildUri('a', 999), evictedUris);
        Assert.Contains(ChildUri('a', 1000), evictedUris);          // beyond root A's per-root bound
        for (int i = 0; i < itemsB.Length; i++)
            Assert.DoesNotContain(ChildUri('b', i), evictedUris);   // root B's full 1,000 survive regardless of A's size
    }

    [Fact]
    public async Task RecentSurfaces_KeepTheNewestFiftyAtDeterministicTies()
    {
        using var db = new CatalogTestDb(); using var storage = new SqliteColdStore(db.Path);
        for (int i = 0; i < 70; i++) storage.RecordRecentCatalogSurface("spotify:album:" + i, 3, i);
        Assert.Equal(50, db.Number("SELECT COUNT(*) FROM recent_surfaces;"));
        Assert.Equal(20, db.Number("SELECT MIN(last_opened) FROM recent_surfaces;"));
    }

    // ── The cheap evictability probe ────────────────────────────────────────────────────────────────────────────
    // RunCatalogGcBatch pays whole-table scan cost whether it deletes 1,000 rows or none: 3,429 ms on the shared
    // commit owner in the native ARM64 tour of 2026-09-09 (log seq 195), for zero deleted rows, which cost the
    // artist navigation behind it 1,483 ms of reveal against 55-68 ms for every other cold page in that session.
    // CatalogGcHasWork answers "would a batch delete anything?" from indexes only, and must never answer "no"
    // when the batch would have deleted something — that is the whole licence to skip it.

    [Fact]
    public async Task GcProbe_SaysNo_ExactlyWhenTheBatchWouldDeleteNothing()
    {
        using var db = new CatalogTestDb(); using var storage = new SqliteColdStore(db.Path);
        var track = Key("spotify:track:c");
        await storage.CommitAsync(new CatalogCommit(1, [Record(track, new TrackIdentityValue("Track"))]), TestContext.Current.CancellationToken);

        Age(db, 5);
        Assert.False(storage.CatalogGcHasWork(long.MaxValue, Now));
        Assert.Equal(0, storage.RunCatalogGcBatch([], long.MaxValue, Now, false).Rows);

        // Byte pressure alone is enough to say yes, with nothing stale at all.
        Assert.True(storage.CatalogGcHasWork(1, Now));
        Assert.Equal(1, storage.RunCatalogGcBatch([], 1, Now, false).Rows);
    }

    [Fact]
    public async Task GcProbe_HonorsThePerFacetTtlTiersRatherThanTheLoosestOne()
    {
        using var db = new CatalogTestDb(); using var storage = new SqliteColdStore(db.Path);
        var overview = Key("spotify:artist:a", FacetKind.ArtistOverview);
        await storage.CommitAsync(new CatalogCommit(1, [Record(overview, new ArtistOverviewValue(Bio: "Overview"))]), TestContext.Current.CancellationToken);

        // An overview 8 days old is past its 7-day tier even though a track identity would still have 22 days left.
        Age(db, 8);
        Assert.True(storage.CatalogGcHasWork(long.MaxValue, Now));
        Assert.Equal(overview, Assert.Single(storage.RunCatalogGcBatch([], long.MaxValue, Now, false).Resources));
    }

    [Fact]
    public async Task GcProbe_SeesAStaleTransportDocument()
    {
        using var db = new CatalogTestDb(); using var storage = new SqliteColdStore(db.Path);
        const string uri = "spotify:track:document";
        var pointer = Key(uri, FacetKind.ExtensionDocument, new(Filter: "186"));
        await storage.CommitAsync(new CatalogCommit(1, [Record(pointer, new ExtensionDocumentValue(186, "etag"))],
            [new(pointer.Scope, uri, 186, "etag", [1, 2, 3], Now.AddDays(-60))]), TestContext.Current.CancellationToken);
        // Only the transport row is stale: the pointer resource is fresh, so the resource leg of the probe is silent
        // and the extension leg is what has to speak up.
        db.Exec("UPDATE catalog_resource SET last_access=$now,updated_at=$now;", ("$now", Now.ToUnixTimeMilliseconds()));
        db.Exec("UPDATE extension_cache SET updated_at=$then;", ("$then", Now.AddDays(-60).ToUnixTimeMilliseconds()));

        Assert.True(storage.CatalogGcHasWork(long.MaxValue, Now));
        Assert.Contains(pointer, storage.RunCatalogGcBatch([], long.MaxValue, Now, false).Resources);
    }

    [Fact]
    public async Task EvictingAnIdentity_StillDropsItsSearchRow()
    {
        // The orphan anti-join is skipped by a batch that deleted nothing (only these deletes can orphan a search
        // row — the other delete site removes both in one transaction), so the batch that DOES delete must still run it.
        using var db = new CatalogTestDb(); using var storage = new SqliteColdStore(db.Path);
        var track = Key("spotify:track:c");
        await storage.CommitAsync(new CatalogCommit(1, [Record(track, new TrackIdentityValue("Track"))]), TestContext.Current.CancellationToken);
        Assert.Equal(1, db.Number("SELECT COUNT(*) FROM catalog_search;"));
        Age(db, 60);

        Assert.Equal(1, storage.RunCatalogGcBatch([], long.MaxValue, Now, false).Rows);

        Assert.Equal(0, db.Number("SELECT COUNT(*) FROM catalog_search;"));
        AssertAccounting(db);
    }

    // ── Maintenance and the shared commit owner ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APeriodicSweep_RunsNoCommandOnTheSharedCommitOwner()
    {
        // The regression, stated as an invariant: retention writes through the cold store's own writer lock and
        // publishes evictions under the publication gate, so it must never admit a command to the queue that a
        // navigation's own commands would then have to queue behind.
        await using var fixture = await MaintenanceFixture.CreateAsync(ageDays: 60);
        long before = fixture.Commits.Contention.Commits;

        var eviction = await fixture.Maintenance.SweepAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(before, fixture.Commits.Contention.Commits);
        Assert.Contains(fixture.Unpinned, eviction.Resources);
        Assert.DoesNotContain(fixture.Pinned, eviction.Resources);
        Assert.Null(await fixture.Storage.ReadOneAsync(fixture.Unpinned, TestContext.Current.CancellationToken));
        Assert.NotNull(await fixture.Storage.ReadOneAsync(fixture.Pinned, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASweepWithNothingToEvict_RunsNoBatchAtAll()
    {
        // Five days old, well inside every tier, and under budget: the sweep's answer is the same zero-row
        // CatalogEviction it used to spend seconds arriving at. Pinned rows are rolled forward by the batch, so an
        // untouched last_access is the observable proof that no batch ran.
        await using var fixture = await MaintenanceFixture.CreateAsync(ageDays: 5);
        long pinnedAccessBefore = fixture.Db.Number("SELECT last_access FROM catalog_resource WHERE subject='" + fixture.Pinned.Subject + "';");
        long commitsBefore = fixture.Commits.Contention.Commits;

        var eviction = await fixture.Maintenance.SweepAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, eviction.Rows);
        Assert.Equal(0, eviction.Bytes);
        Assert.Empty(eviction.Resources);
        Assert.Equal(pinnedAccessBefore, fixture.Db.Number("SELECT last_access FROM catalog_resource WHERE subject='" + fixture.Pinned.Subject + "';"));
        Assert.Equal(commitsBefore, fixture.Commits.Contention.Commits);
    }

    [Fact]
    public async Task ASweepThatDoesRun_RollsPinnedRowsForwardSoTheyDoNotAgeOutWhileUnpinned()
    {
        await using var fixture = await MaintenanceFixture.CreateAsync(ageDays: 60);

        await fixture.Maintenance.SweepAsync(ct: TestContext.Current.CancellationToken);

        long day = Now.ToUnixTimeMilliseconds() - Now.ToUnixTimeMilliseconds() % (24L * 60 * 60 * 1000);
        Assert.Equal(day, fixture.Db.Number("SELECT last_access FROM catalog_resource WHERE subject='" + fixture.Pinned.Subject + "';"));
    }

    [Fact]
    public async Task ClearAsync_StillEmptiesTheCache_ExceptPinnedRows()
    {
        await using var fixture = await MaintenanceFixture.CreateAsync(ageDays: 0);
        long before = fixture.Commits.Contention.Commits;

        var cleared = await fixture.Maintenance.ClearAsync(TestContext.Current.CancellationToken);

        // A clear ignores age and budget but never the pins: library-backed rows survive "clear metadata cache"
        // exactly as they did before the sweep left the commit owner (the candidate query excludes temp.catalog_pins
        // unconditionally), so the settings action cannot evict what the replicas still point at.
        Assert.DoesNotContain(fixture.Pinned, cleared.Resources);
        Assert.Contains(fixture.Unpinned, cleared.Resources);
        Assert.Equal(1, fixture.Db.Number("SELECT COUNT(*) FROM catalog_resource;"));
        Assert.Equal(0, fixture.Db.Number("SELECT COUNT(*) FROM catalog_search WHERE uri<>'" + fixture.Pinned.Subject + "';"));
        Assert.Equal(before, fixture.Commits.Contention.Commits);
        AssertAccounting(fixture.Db);
    }

    [Fact]
    public async Task RecordingAnOpenedSurface_AlsoStaysOffTheCommitOwner()
    {
        await using var fixture = await MaintenanceFixture.CreateAsync(ageDays: 0);
        long before = fixture.Commits.Contention.Commits;

        await fixture.Maintenance.RecordRecentAsync("spotify:album:opened", 3, TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.Db.Number("SELECT COUNT(*) FROM recent_surfaces WHERE uri='spotify:album:opened';"));
        Assert.Equal(before, fixture.Commits.Contention.Commits);
    }

    [Fact]
    public async Task StatsAlsoStayOffTheCommitOwner()
    {
        // The same whole-table pinned-bytes census the sweep pays for: opening the storage settings page must not
        // hand the owner a multi-second command either.
        await using var fixture = await MaintenanceFixture.CreateAsync(ageDays: 0);
        long before = fixture.Commits.Contention.Commits;

        var stats = await fixture.Maintenance.GetStatsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, stats.EntityRows);
        Assert.Equal(1, stats.PinnedRows);
        Assert.Equal(before, fixture.Commits.Contention.Commits);
    }

    sealed class MaintenanceFixture : IAsyncDisposable
    {
        // A pinned wall clock but REAL timers: the scheduler's deferral backoff is a Task.Delay on this provider, so
        // a manual-timer clock (CatalogClock) would hang a deferred sweep instead of letting it come back and run.
        sealed class FixedTime(DateTimeOffset now) : TimeProvider
        { public override DateTimeOffset GetUtcNow() => now; }

        public required CatalogTestDb Db { get; init; }
        public required SqliteColdStore Storage { get; init; }
        public required DataCommitQueue Commits { get; init; }
        public required CatalogCacheMaintenance Maintenance { get; init; }
        public required ResourceKey Pinned { get; init; }
        public required ResourceKey Unpinned { get; init; }

        public static async Task<MaintenanceFixture> CreateAsync(int ageDays, long budget = long.MaxValue)
        {
            var db = new CatalogTestDb();
            var storage = new SqliteColdStore(db.Path);
            var pinned = Key("spotify:track:pinned");
            var unpinned = Key("spotify:track:unpinned");
            await storage.CommitAsync(new CatalogCommit(1, [Record(pinned, new TrackIdentityValue("Pinned")),
                Record(unpinned, new TrackIdentityValue("Unpinned"))]), TestContext.Current.CancellationToken);
            if (ageDays > 0) CatalogRetentionTests.Age(db, ageDays);
            var commits = new DataCommitQueue();
            var time = new FixedTime(Now);
            var catalog = new CatalogRepository(commits, storage, time, LibrarySearchFixture.Scope, LibrarySearchFixture.Scope.ProviderAccount);
            return new()
            {
                Db = db, Storage = storage, Commits = commits, Pinned = pinned, Unpinned = unpinned,
                Maintenance = new(storage, catalog, commits, () => new[] { pinned }, time, budget,
                    error => Assert.Fail("Cache maintenance failed: " + error)),
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Maintenance.DisposeAsync();
            await Commits.DisposeAsync();
            Storage.Dispose();
            Db.Dispose();
        }
    }

    static void Age(CatalogTestDb db, int days) => db.Exec("UPDATE catalog_resource SET last_access=$stamp,updated_at=$stamp;",
        ("$stamp", Now.AddDays(-days).ToUnixTimeMilliseconds()));
    static void AssertAccounting(CatalogTestDb db) => Assert.Equal(
        db.Number("SELECT (SELECT COALESCE(SUM(size),0) FROM catalog_resource)+(SELECT COALESCE(SUM(size),0) FROM catalog_relation_item)+(SELECT COALESCE(SUM(length(payload)),0) FROM extension_cache);"),
        db.Number("SELECT CAST(value AS INTEGER) FROM meta WHERE key='catalog_bytes';"));
}
