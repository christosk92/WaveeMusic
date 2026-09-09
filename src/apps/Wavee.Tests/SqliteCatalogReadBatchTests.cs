using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Backend.Persistence;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

/// <summary>One cold read answers a whole page: the store groups keys by (scope, facet) and issues a single
/// `subject IN (…)` statement per group instead of a scope lookup plus a resource lookup per key (times three,
/// once the imported-context fallbacks run). These tests pin the answers that grouping must not change.</summary>
public sealed class SqliteCatalogReadBatchTests
{
    static readonly CatalogScope Scope = new("spotify", "account", "en", "NL", "premium", 1, false);
    static ResourceKey Key(string uri, FacetKind facet = FacetKind.TrackIdentity, ResourceArguments args = default)
        => new(Scope, uri, facet, args);
    static CatalogRecord Record(ResourceKey key, CatalogValue value, long revision = 1) => new(key, Knowledge.Present,
        value, CatalogProvenance.Provider, System.DateTimeOffset.UnixEpoch, System.DateTimeOffset.UnixEpoch.AddHours(1), revision);
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OneBatch_AnswersHitsMissesAndCorruptRows_IndexAligned()
    {
        using var db = new CatalogTestDb();
        using var storage = new SqliteColdStore(db.Path);
        var hit = Key("spotify:track:hit");
        var corrupt = Key("spotify:track:corrupt");
        var absent = Key("spotify:track:absent");
        var miss = Key("spotify:track:never-written");
        await storage.CommitAsync(new CatalogCommit(1, [
            Record(hit, new TrackIdentityValue("Hit")),
            Record(corrupt, new TrackIdentityValue("Corrupt")),
            new CatalogRecord(absent, Knowledge.Absent, null, CatalogProvenance.Provider, default, default, 1)]), Ct);
        db.Exec("UPDATE catalog_resource SET payload_version=99 WHERE subject='spotify:track:corrupt';");

        var records = await storage.ReadManyAsync([hit, corrupt, absent, miss], Ct);

        Assert.Equal(4, records.Count);
        Assert.Equal("Hit", Assert.IsType<TrackIdentityValue>(records[0]!.Value).Title);
        Assert.Equal(hit, records[0]!.Key);
        Assert.Null(records[1]);                                    // corrupt reads as a miss …
        Assert.Equal(Knowledge.Absent, records[2]!.Knowledge);       // … while a stored Absent is a real answer
        Assert.Null(records[3]);

        // The corrupt row was queued for purge, exactly as the single-key read did.
        await storage.CommitAsync(new CatalogCommit(2, []), Ct);
        Assert.Equal(0, db.Number("SELECT COUNT(*) FROM catalog_resource WHERE subject='spotify:track:corrupt';"));
        Assert.Equal(1, db.Number("SELECT COUNT(*) FROM catalog_resource WHERE subject='spotify:track:hit';"));
    }

    [Fact]
    public async Task MixedScopesAndFacets_AreGroupedAndStayAlignedWithTheirKeys()
    {
        using var db = new CatalogTestDb();
        using var storage = new SqliteColdStore(db.Path);
        var otherScope = Scope with { Market = "SE" };
        var track = Key("spotify:track:a");
        var album = Key("spotify:album:a", FacetKind.AlbumIdentity);
        var count = Key("spotify:track:a", FacetKind.PlayCount);
        var elsewhere = new ResourceKey(otherScope, "spotify:track:a", FacetKind.TrackIdentity);
        await storage.CommitAsync(new CatalogCommit(1, [
            Record(track, new TrackIdentityValue("Here")),
            Record(album, new AlbumIdentityValue("Album")),
            Record(count, new PlayCountValue(7)),
            Record(elsewhere, new TrackIdentityValue("Elsewhere"))]), Ct);

        // Same subject under three (scope, facet) groups, plus a repeat of one key.
        var records = await storage.ReadManyAsync([count, elsewhere, track, album, track], Ct);

        Assert.Equal(7, Assert.IsType<PlayCountValue>(records[0]!.Value).Count);
        Assert.Equal("Elsewhere", Assert.IsType<TrackIdentityValue>(records[1]!.Value).Title);
        Assert.Equal(otherScope, records[1]!.Key.Scope);
        Assert.Equal("Here", Assert.IsType<TrackIdentityValue>(records[2]!.Value).Title);
        Assert.Equal("Album", Assert.IsType<AlbumIdentityValue>(records[3]!.Value).Name);
        Assert.Equal("Here", Assert.IsType<TrackIdentityValue>(records[4]!.Value).Title);
    }

    [Fact]
    public async Task RelationPages_KeepTheirItemsInABatch()
    {
        using var db = new CatalogTestDb();
        using var storage = new SqliteColdStore(db.Path);
        var first = Key("spotify:album:a", FacetKind.AlbumTracks, new(0, 50));
        var second = Key("spotify:album:b", FacetKind.AlbumTracks, new(0, 50));
        var page = Key("spotify:album:a", FacetKind.AlbumTracks, new(50, 50));
        await storage.CommitAsync(new CatalogCommit(1, [
            Record(first, Page("a", [new("t0", "spotify:track:one"), new("t1", "spotify:track:two")])),
            Record(second, Page("b", [new("t0", "spotify:track:three")])),
            Record(page, Page("a", [new("t2", "spotify:track:four")], offset: 50))]), Ct);

        var records = await storage.ReadManyAsync([first, second, page], Ct);

        // Same subject and facet, different arguments: the arguments must select the right page's items.
        Assert.Equal(new[] { "spotify:track:one", "spotify:track:two" },
            Assert.IsType<RelationPageValue>(records[0]!.Value).Items.Select(item => item.EntityUri).ToArray());
        Assert.Equal(new[] { "spotify:track:three" }, Assert.IsType<RelationPageValue>(records[1]!.Value).Items.Select(item => item.EntityUri).ToArray());
        Assert.Equal(new[] { "spotify:track:four" }, Assert.IsType<RelationPageValue>(records[2]!.Value).Items.Select(item => item.EntityUri).ToArray());
    }

    [Fact]
    public async Task ImportedContextRows_AnswerTheAuthenticatedKeys_ThroughTheLocaleLessFallbackToo()
    {
        using var db = new CatalogTestDb();
        using var storage = new SqliteColdStore(db.Path);
        var importedScope = Scope with { ProviderAccount = "", Market = "", Catalogue = "", Tier = -1,
            ExplicitFilter = false, ContextKnown = false };
        var localized = new ResourceKey(importedScope, "spotify:track:localized", FacetKind.TrackIdentity);
        var neutral = new ResourceKey(importedScope with { Locale = "" }, "spotify:track:neutral", FacetKind.TrackIdentity);
        var page = new ResourceKey(importedScope, "spotify:album:a", FacetKind.AlbumTracks, new(0, 50));
        await storage.CommitAsync(new CatalogCommit(1, [
            Record(localized, new TrackIdentityValue("Localized")),
            Record(neutral, new TrackIdentityValue("Neutral")),
            Record(page, Page("a", [new("t0", "spotify:track:one")]))]), Ct);

        var wanted = new[] { Key("spotify:track:localized"), Key("spotify:track:neutral"),
            Key("spotify:album:a", FacetKind.AlbumTracks, new(0, 50)), Key("spotify:track:nowhere") };
        var records = await storage.ReadManyAsync(wanted, Ct);

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(wanted[i], records[i]!.Key);                       // re-keyed to what the caller asked for
            Assert.Equal(CatalogProvenance.ImportedUnknownContext, records[i]!.Provenance);
            Assert.Equal(0, records[i]!.Revision);
            Assert.Equal(default(System.DateTimeOffset), records[i]!.FetchedAt);
        }
        Assert.Equal("Localized", Assert.IsType<TrackIdentityValue>(records[0]!.Value).Title);
        Assert.Equal("Neutral", Assert.IsType<TrackIdentityValue>(records[1]!.Value).Title);
        Assert.Equal(new[] { "spotify:track:one" }, Assert.IsType<RelationPageValue>(records[2]!.Value).Items.Select(item => item.EntityUri).ToArray());
        Assert.Null(records[3]);
    }

    [Fact]
    public async Task AnImportedPlaylistHeader_DropsEveryContextDependentField()
    {
        using var db = new CatalogTestDb();
        using var storage = new SqliteColdStore(db.Path);
        var importedScope = Scope with { ProviderAccount = "", Market = "", Catalogue = "", Tier = -1,
            ExplicitFilter = false, ContextKnown = false };
        var stored = new ResourceKey(importedScope, "spotify:playlist:p", FacetKind.PlaylistHeader);
        await storage.CommitAsync(new CatalogCommit(1, [Record(stored, new PlaylistHeaderValue("Mix")
            { IsPublic = true, BasePermissionRevision = "rev" })]), Ct);

        var record = (await storage.ReadManyAsync([Key("spotify:playlist:p", FacetKind.PlaylistHeader)], Ct))[0];

        var header = Assert.IsType<PlaylistHeaderValue>(record!.Value);
        Assert.Equal("Mix", header.Name);
        Assert.Null(header.IsPublic);
        Assert.Null(header.Capabilities);
        Assert.Null(header.Tuning);
        Assert.Null(header.BasePermissionRevision);
    }

    [Fact]
    public async Task AnImportedAbsentRow_IsAMiss_AndDoesNotFallThroughToTheLocaleLessScope()
    {
        using var db = new CatalogTestDb();
        using var storage = new SqliteColdStore(db.Path);
        var importedScope = Scope with { ProviderAccount = "", Market = "", Catalogue = "", Tier = -1,
            ExplicitFilter = false, ContextKnown = false };
        var localized = new ResourceKey(importedScope, "spotify:track:x", FacetKind.TrackIdentity);
        var neutral = new ResourceKey(importedScope with { Locale = "" }, "spotify:track:x", FacetKind.TrackIdentity);
        await storage.CommitAsync(new CatalogCommit(1, [
            new CatalogRecord(localized, Knowledge.Absent, null, CatalogProvenance.Provider, default, default, 1),
            Record(neutral, new TrackIdentityValue("Would have matched"))]), Ct);

        // The locale-scoped imported row exists but is not Present: the read stops there rather than answering
        // from the locale-less row behind it.
        Assert.Null((await storage.ReadManyAsync([Key("spotify:track:x")], Ct))[0]);
    }

    [Fact]
    public async Task ABatchLargerThanOneStatement_ReadsEveryKey()
    {
        using var db = new CatalogTestDb();
        using var storage = new SqliteColdStore(db.Path);
        var keys = new List<ResourceKey>();
        var records = new List<CatalogRecord>();
        for (int i = 0; i < 950; i++)
        {
            var key = Key("spotify:track:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            keys.Add(key);
            if (i % 2 == 0) records.Add(Record(key, new TrackIdentityValue("Track " + i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }
        await storage.CommitAsync(new CatalogCommit(1, records), Ct);

        var read = await storage.ReadManyAsync(keys, Ct);

        Assert.Equal(950, read.Count);
        for (int i = 0; i < keys.Count; i++)
            if (i % 2 == 0) Assert.Equal("Track " + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Assert.IsType<TrackIdentityValue>(read[i]!.Value).Title);
            else Assert.Null(read[i]);
    }

    [Fact]
    public async Task AnEmptyBatchReadsNothing_AndAnUnknownScopeIsAllMisses()
    {
        using var db = new CatalogTestDb();
        using var storage = new SqliteColdStore(db.Path);
        Assert.Empty(await storage.ReadManyAsync([], Ct));
        var foreign = new ResourceKey(Scope with { ProviderAccount = "nobody" }, "spotify:track:a", FacetKind.TrackIdentity);
        Assert.Null((await storage.ReadManyAsync([foreign], Ct))[0]);
    }

    static RelationPageValue Page(string snapshot, CatalogRelationItem[] items, int offset = 0)
        => new(FacetKind.AlbumTracks, snapshot, null, offset, offset + items.Length, null, RelationCoverage.Complete, items);
}

internal static class CatalogPersistenceTestExtensions
{
    /// <summary>Test sugar for the one-key case: persistence itself only exposes the batched read.</summary>
    public static async ValueTask<CatalogRecord?> ReadOneAsync(this ICatalogPersistence persistence, ResourceKey key,
        CancellationToken ct = default)
        => (await persistence.ReadManyAsync([key], ct).ConfigureAwait(false))[0];
}
