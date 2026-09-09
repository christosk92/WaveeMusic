using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Catalog;
using Wavee.Backend.Persistence;
using Wavee.Backend.Queries;
using Wavee.Backend.Sync;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class LibrarySearchTests
{
    [Theory]
    [InlineData("michael", 2, 2, LibraryMatchKind.None)]
    [InlineData("thriller", 1, 2, LibraryMatchKind.Album)]
    [InlineData("billie", 1, 1, LibraryMatchKind.Track)]
    public async Task FollowedArtistSearch_CascadesMatchingNamesAndExplainsChildHits(string text, int albums, int firstTracks, LibraryMatchKind reason)
    {
        await using var fixture = await LibrarySearchFixture.CreateAsync();
        var result = await fixture.Search(text);
        var artist = Assert.Single(result.Artists);
        Assert.Equal("Michael Jackson", artist.Name);
        Assert.Equal(albums, artist.Albums.Count);
        Assert.Equal(firstTracks, artist.Albums[0].Tracks.Count);
        Assert.Equal(reason, artist.Match.Kind);
        Assert.Equal(0, fixture.Network.Calls);
    }

    [Theory]
    [InlineData("queen")]
    [InlineData("bohemian")]
    [InlineData("opera")]
    [InlineData("   ")]
    [InlineData("unfindable")]
    public async Task SearchNeverIncludesAnUnfollowedArtistOrUnmatchedTree(string text)
    {
        await using var fixture = await LibrarySearchFixture.CreateAsync();
        Assert.True((await fixture.Search(text)).IsEmpty);
    }

    [Fact]
    public async Task OrderedRelations_PreserveDuplicateUrisAndTheirActualAlbumIndices()
    {
        await using var fixture = await LibrarySearchFixture.CreateAsync();
        var key = LibrarySearchFixture.Key("spotify:album:thriller", FacetKind.AlbumTracks, new(0, 50));
        var record = LibrarySearchFixture.Record(key, new RelationPageValue(FacetKind.AlbumTracks, "replacement", null,
            0, 3, null, RelationCoverage.Complete, [new("one", "spotify:track:bj"), new("two", "spotify:track:bi"), new("three", "spotify:track:bj")]));
        await fixture.Storage.CommitAsync(new(2, [record]), TestContext.Current.CancellationToken);
        var album = Assert.Single((await fixture.Search("billie", LibrarySearchScope.Albums)).Albums);
        Assert.Equal(new[] { 0, 2 }, album.Tracks.Select(track => track.AlbumIndex));
        Assert.All(album.Tracks, track => Assert.Equal("spotify:track:bj", track.Uri));
    }

    [Fact]
    public void ImportedAlbumLinks_DoNotInventAnOrdering_AndUnicodeMatchingUsesOrdinalCaseFolding()
    {
        var scope = LibrarySearchFixture.Scope;
        var rows = new CatalogSearchRow[] { new(scope, "spotify:album:a", EntityKind.Album, "Album", null, []),
            new(scope, "spotify:track:t", EntityKind.Track, "small ω song", "spotify:album:a", []) };
        var selected = LibrarySearchSelection.Select(new(rows, []), scope, LibrarySearchScope.Albums,
            ["spotify:album:a"], "Ω", _ => "spotify", TestContext.Current.CancellationToken);
        Assert.Equal(-1, Assert.Single(Assert.Single(selected.Albums).Tracks).AlbumIndex);
    }

    [Fact]
    public void RelationReplacement_DoesNotCombineOtherSnapshotsOrResurrectOldAlbumLinks()
    {
        var scope = LibrarySearchFixture.Scope;
        CatalogSearchPage Page(int offset, string snapshot, params string[] children) => new(scope, "spotify:album:a",
            FacetKind.AlbumTracks, new ResourceArguments(offset, 50).ToStorageKey(), snapshot, offset, children, offset == 0 ? 5 : 4);
        var corpus = new CatalogSearchCorpus([
            new(scope, "spotify:album:a", EntityKind.Album, "Album", null, []),
            new(scope, "spotify:track:old", EntityKind.Track, "old match", "spotify:album:a", [])],
            [Page(0, "new"), Page(50, "old", "spotify:track:old")]);
        Assert.Empty(LibrarySearchSelection.Select(corpus, scope, LibrarySearchScope.Albums,
            ["spotify:album:a"], "old", _ => "spotify", TestContext.Current.CancellationToken).Albums);
    }
}

sealed class LibrarySearchFixture : IAsyncDisposable
{
    public static readonly CatalogScope Scope = new("spotify", "account", "en", "NL", "premium", 1, false);
    public SearchPersistence Storage { get; }
    public CatalogRuntime Data { get; }
    public NoSearchNetwork Network { get; } = new();
    LibrarySearchFixture(SearchPersistence storage, IReplicaPersistence replicas)
    {
        Storage = storage;
        Data = new(Scope, Scope.ProviderAccount, storage, replicas, new MemoryReplicaProjection(new InMemoryStore()), [Network]);
    }
    public static async Task<LibrarySearchFixture> CreateAsync(ICatalogPersistence? storage = null,
        IReplicaPersistence? replicas = null, ICatalogSearchPersistence? search = null, bool seed = true)
    {
        var memory = new MemoryDataPersistence();
        storage ??= memory; replicas ??= memory; search ??= (ICatalogSearchPersistence)storage;
        if (seed)
        {
            await storage.CommitAsync(new CatalogCommit(1, Records()), TestContext.Current.CancellationToken);
            await replicas.CommitAsync(new(new(Scope.ProviderAccount, 1), [], null,
                [new("artists", [new("spotify:artist:mj", 1)]), new("albums", [new("spotify:album:thriller", 1)])], [], [], [], []),
                TestContext.Current.CancellationToken);
        }
        var fixture = new LibrarySearchFixture(new(storage, search), replicas);
        await fixture.Data.InitializeAsync(TestContext.Current.CancellationToken);
        await fixture.Data.SetSessionAsync(Scope, Scope.ProviderAccount, false, TestContext.Current.CancellationToken);
        if (seed) Assert.Contains(fixture.Data.Replicas.ReadCollection("artists").Items, item => item.Uri == "spotify:artist:mj");
        return fixture;
    }
    public async Task<LibrarySearchResults> Search(string text, LibrarySearchScope scope = LibrarySearchScope.Artists)
        => (await Data.Queries.ReadOnceAsync(new LibrarySearchQuery(Scope, text, scope), cancellationToken: TestContext.Current.CancellationToken)).Value;
    public ValueTask DisposeAsync() => Data.DisposeAsync();
    public static ResourceKey Key(string uri, FacetKind facet, ResourceArguments args = default) => new(Scope, uri, facet, args);
    public static CatalogRecord Record(ResourceKey key, CatalogValue value) => new(key, Knowledge.Present, value,
        CatalogProvenance.Provider, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), 1);
    public static CatalogRecord[] Records() => [
        Record(Key("spotify:artist:mj", FacetKind.ArtistIdentity), new ArtistIdentityValue("Michael Jackson")),
        Record(Key("spotify:artist:q", FacetKind.ArtistIdentity), new ArtistIdentityValue("Queen")),
        Record(Key("spotify:album:thriller", FacetKind.AlbumIdentity), new AlbumIdentityValue("Thriller", ArtistUris: ["spotify:artist:mj"], Year: 1982)),
        Record(Key("spotify:album:bad", FacetKind.AlbumIdentity), new AlbumIdentityValue("Bad", ArtistUris: ["spotify:artist:mj"], Year: 1980)),
        Record(Key("spotify:album:opera", FacetKind.AlbumIdentity), new AlbumIdentityValue("A Night at the Opera", ArtistUris: ["spotify:artist:q"])),
        Record(Key("spotify:track:bj", FacetKind.TrackIdentity), new TrackIdentityValue("Billie Jean", ["spotify:artist:mj"], "spotify:album:thriller")),
        Record(Key("spotify:track:bi", FacetKind.TrackIdentity), new TrackIdentityValue("Beat It", ["spotify:artist:mj"], "spotify:album:thriller")),
        Record(Key("spotify:track:smooth", FacetKind.TrackIdentity), new TrackIdentityValue("Smooth Criminal", ["spotify:artist:mj"], "spotify:album:bad")),
        Record(Key("spotify:track:queen", FacetKind.TrackIdentity), new TrackIdentityValue("Bohemian Rhapsody", ["spotify:artist:q"], "spotify:album:opera")),
        Record(Key("spotify:album:thriller", FacetKind.AlbumTracks, new(0, 50)), new RelationPageValue(FacetKind.AlbumTracks,
            "thriller", null, 0, 2, null, RelationCoverage.Complete, [new("one", "spotify:track:bj"), new("two", "spotify:track:bi")])),
    ];
    public sealed class NoSearchNetwork : ICatalogResourceProvider
    {
        public int Calls;
        public string Provider => "spotify";
        public ValueTask<IReadOnlyList<ResourceResponse>> FetchAsync(IReadOnlyList<ResourceRequest> requests, CancellationToken ct)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("Offline search must never request transport metadata."); }
    }
}

sealed class SearchPersistence(ICatalogPersistence storage, ICatalogSearchPersistence search) : ICatalogPersistence, ICatalogSearchPersistence
{
    public readonly List<ResourceKey> ReadKeys = [];
    public int CorpusReads;
    public Func<int, CancellationToken, ValueTask<CatalogSearchCorpus>>? ReadCorpus;
    public ValueTask<IReadOnlyList<CatalogRecord?>> ReadManyAsync(IReadOnlyList<ResourceKey> keys, CancellationToken ct)
    { lock (ReadKeys) ReadKeys.AddRange(keys); return storage.ReadManyAsync(keys, ct); }
    public ValueTask CommitAsync(CatalogCommit commit, CancellationToken ct) => storage.CommitAsync(commit, ct);
    public ValueTask<CatalogTransportRecord?> ReadTransportAsync(CatalogScope scope, string subject, int extensionKind, CancellationToken ct)
        => storage.ReadTransportAsync(scope, subject, extensionKind, ct);
    public ValueTask<CatalogSearchCorpus> ReadSearchCorpusAsync(CatalogScope scope, CancellationToken ct)
    { int call = Interlocked.Increment(ref CorpusReads); return ReadCorpus?.Invoke(call, ct) ?? search.ReadSearchCorpusAsync(scope, ct); }
}
