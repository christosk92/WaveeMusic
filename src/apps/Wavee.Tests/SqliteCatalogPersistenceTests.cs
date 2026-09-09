using Wavee.Backend.Catalog;
using System;
using System.Threading.Tasks;
using Wavee.Backend.Persistence;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

// Finding #8: a corrupt catalog row (a version bump, or a payload nothing can decode) must read as a miss — never
// throw and poison every ReadManyAsync/AcceptAsync batch that touches its key forever — and must be purged so the
// next fetch's fresh value actually persists instead of tripping over the same bad row again.
public sealed class SqliteCatalogPersistenceTests
{
    static ResourceKey Key(string uri, FacetKind facet = FacetKind.TrackIdentity, ResourceArguments args = default)
        => new(LibrarySearchFixture.Scope, uri, facet, args);
    static CatalogRecord Record(ResourceKey key, CatalogValue value) => LibrarySearchFixture.Record(key, value);

    [Fact]
    public async Task PayloadVersionMismatch_ReadsAsAMiss_PurgesTheRow_AndAFreshWriteThenPersists()
    {
        using var db = new CatalogTestDb(); using var storage = new SqliteColdStore(db.Path);
        var key = Key("spotify:track:versionbump");
        await storage.CommitAsync(new CatalogCommit(1, [Record(key, new TrackIdentityValue("Track"))]), TestContext.Current.CancellationToken);
        db.Exec("UPDATE catalog_resource SET payload_version=99;");

        // A miss, not a throw.
        Assert.Null(await storage.ReadOneAsync(key, TestContext.Current.CancellationToken));
        Assert.Equal(1, db.Number("SELECT COUNT(*) FROM catalog_resource;"));   // not yet purged: `_read` cannot write

        // The next write-connection operation purges the bad row (and its catalog_search row).
        await storage.CommitAsync(new CatalogCommit(2, []), TestContext.Current.CancellationToken);
        Assert.Equal(0, db.Number("SELECT COUNT(*) FROM catalog_resource;"));
        Assert.Equal(0, db.Number("SELECT COUNT(*) FROM catalog_search;"));

        // A fresh write for the same key persists normally afterward — the bad row never poisons it.
        await storage.CommitAsync(new CatalogCommit(3, [Record(key, new TrackIdentityValue("Fixed"))]), TestContext.Current.CancellationToken);
        var reread = await storage.ReadOneAsync(key, TestContext.Current.CancellationToken);
        Assert.Equal("Fixed", Assert.IsType<TrackIdentityValue>(reread!.Value).Title);
    }

    [Fact]
    public async Task UndecodablePayload_ReadsAsAMissAndPurgesTheRow()
    {
        using var db = new CatalogTestDb(); using var storage = new SqliteColdStore(db.Path);
        var key = Key("spotify:track:truncated");
        await storage.CommitAsync(new CatalogCommit(1, [Record(key, new TrackIdentityValue("Track"))]), TestContext.Current.CancellationToken);
        // Format byte 0 (raw JSON) followed by one byte that is not valid JSON on its own.
        db.Exec("UPDATE catalog_resource SET payload=X'0000';");

        Assert.Null(await storage.ReadOneAsync(key, TestContext.Current.CancellationToken));

        await storage.CommitAsync(new CatalogCommit(2, []), TestContext.Current.CancellationToken);
        Assert.Equal(0, db.Number("SELECT COUNT(*) FROM catalog_resource;"));
    }

    [Fact]
    public async Task CorruptRelationParent_AlsoDropsItsRelationItemsViaCascade()
    {
        using var db = new CatalogTestDb(); using var storage = new SqliteColdStore(db.Path);
        var album = Key("spotify:album:corrupt", FacetKind.AlbumIdentity);
        var relation = Key(album.Subject, FacetKind.AlbumTracks, new(0, 50));
        await storage.CommitAsync(new CatalogCommit(1, [Record(album, new AlbumIdentityValue("Album")),
            Record(relation, new RelationPageValue(FacetKind.AlbumTracks, "album", null, 0, 1, null,
                RelationCoverage.Complete, [new("t0", "spotify:track:child")]))]), TestContext.Current.CancellationToken);
        Assert.Equal(1, db.Number("SELECT COUNT(*) FROM catalog_relation_item;"));
        db.Exec("UPDATE catalog_resource SET payload_version=99 WHERE facet=30;");   // corrupt only the relation row

        Assert.Null(await storage.ReadOneAsync(relation, TestContext.Current.CancellationToken));
        await storage.CommitAsync(new CatalogCommit(2, []), TestContext.Current.CancellationToken);

        Assert.Equal(0, db.Number("SELECT COUNT(*) FROM catalog_relation_item;"));
        Assert.NotNull(await storage.ReadOneAsync(album, TestContext.Current.CancellationToken));   // sibling row untouched
    }
}
