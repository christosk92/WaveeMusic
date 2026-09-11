using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Backend.Persistence;
using Wavee.Backend.Queries;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class LibrarySearchColdTests
{
    [Fact]
    public async Task Restart_SearchesDurableNormalizedFactsAndLoadsOnlySelectedEntityPayloads()
    {
        string path = Path.Combine(Path.GetTempPath(), "wavee-catalog-search-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var first = new SqliteColdStore(path))
            {
                await using var seeded = await LibrarySearchFixture.CreateAsync(first, first, first);
                Assert.Equal("Billie Jean", Assert.Single(Assert.Single(Assert.Single((await seeded.Search("billie")).Artists).Albums).Tracks).Title);
            }
            using var reopened = new SqliteColdStore(path);
            await using var fixture = await LibrarySearchFixture.CreateAsync(reopened, reopened, reopened, seed: false);
            var result = await fixture.Search("billie");
            Assert.Equal("Michael Jackson", Assert.Single(result.Artists).Name);
            Assert.DoesNotContain(fixture.Storage.ReadKeys, key => key.Subject is "spotify:artist:q" or "spotify:album:opera" or "spotify:track:queen");
            Assert.Equal(1, fixture.Storage.CorpusReads);
            Assert.Equal(0, fixture.Network.Calls);
        }
        finally { DeleteDb(path); }
    }

    [Fact]
    public async Task CandidateScopes_ExcludeOtherAccountsAndLocales_AndCurrentAbsenceOverridesImportedTitle()
    {
        string path = Path.Combine(Path.GetTempPath(), "wavee-catalog-search-scope-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var storage = new SqliteColdStore(path);
            var scope = LibrarySearchFixture.Scope;
            CatalogRecord Row(CatalogScope owner, string uri, string title) => LibrarySearchFixture.Record(new(owner, uri, FacetKind.AlbumIdentity), new AlbumIdentityValue(title));
            var imported = scope with { ContextKnown = false, ProviderAccount = "", Market = "", Catalogue = "", Tier = -1 };
            const string removed = "spotify:album:removed";
            await storage.CommitAsync(new CatalogCommit(1, [
                Row(scope with { ProviderAccount = "someone-else" }, "spotify:album:account", "match"),
                Row(scope with { Locale = "nl" }, "spotify:album:locale", "match"),
                Row(imported, removed, "match"),
                new(new(scope, removed, FacetKind.AlbumIdentity), Knowledge.Absent, null, CatalogProvenance.Provider, default, default, 1),
                Row(imported, "spotify:album:imported", "match"),
                Row(imported with { Locale = "nl" }, "spotify:album:foreign-import", "match")]), TestContext.Current.CancellationToken);
            var corpus = await storage.ReadSearchCorpusAsync(scope, TestContext.Current.CancellationToken);
            var selected = LibrarySearchSelection.Select(corpus, scope, LibrarySearchScope.Albums,
                [removed, "spotify:album:account", "spotify:album:locale", "spotify:album:imported", "spotify:album:foreign-import"],
                "match", _ => "spotify", TestContext.Current.CancellationToken);
            Assert.Equal("spotify:album:imported", Assert.Single(selected.Albums).Uri);
        }
        finally { DeleteDb(path); }
    }

    static void DeleteDb(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(path + suffix); } catch (IOException) { }
    }
}
