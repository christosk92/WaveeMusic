using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public class AggregateCatalogSearchTests
{
    [Fact]
    public async Task SearchAsync_ForwardsChipOrderAndExtendedFacetTotals()
    {
        var chips = new[] { new SearchChip(SearchFacet.Playlists, 128), new SearchChip(SearchFacet.Genres, 8) };
        var genres = new[] { new SearchGenre("spotify:genre:sleep", "Sleep", 0xFF1A237Eu) };
        var online = new SearchMetaSource(new SearchResults(
            Array.Empty<Track>(), Array.Empty<Album>(), Array.Empty<Artist>(), Array.Empty<Playlist>(),
            ChipOrder: chips, Genres: genres, GenresTotal: 8));
        var cat = new AggregateCatalog(new SourceRegistry(new ISource[] { new FakeSource(), online }));

        var r = await cat.SearchAsync("sleep");

        Assert.NotNull(r.ChipOrder);
        Assert.Equal(SearchFacet.Playlists, r.ChipOrder![0].Facet);
        Assert.Equal(SearchFacet.Genres, r.ChipOrder[1].Facet);
        Assert.Equal(8, r.GenresTotal);
        var genre = Assert.Single(r.Genres!);
        Assert.Equal("Sleep", genre.Name);
        Assert.Equal("spotify:genre:sleep", genre.Uri);
    }

    /// <summary>Catalog stub that answers search with a canned payload and otherwise behaves like
    /// <see cref="FakeSource"/> so the aggregate can concat-merge the four core collections.</summary>
    sealed class SearchMetaSource : ICatalogSource
    {
        readonly FakeSource _inner = new();
        readonly SearchResults _payload;

        public SearchMetaSource(SearchResults payload) => _payload = payload;

        public string Id => "search-meta";
        public bool Owns(string uri) => false;
        public SourceCapabilities Capabilities => SourceCapabilities.Catalog;

        public Task<Playlist?> GetPlaylistAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
            => _inner.GetPlaylistAsync(uri, level, ct);
        public Task<Album?> GetAlbumAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
            => _inner.GetAlbumAsync(uri, level, ct);
        public Task<Artist?> GetArtistAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
            => _inner.GetArtistAsync(uri, level, ct);
        public IAsyncEnumerable<TrackPage> StreamTracksAsync(string contextUri, CancellationToken ct = default)
            => _inner.StreamTracksAsync(contextUri, ct);
        public Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct = default)
            => _inner.GetLibraryAsync(ct);
        public Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken ct = default)
            => _inner.GetPlaylistsAsync(ct);
        public Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default)
            => _inner.GetAlbumsAsync(ct);
        public Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default)
            => _inner.GetArtistsAsync(ct);
        public Task<IReadOnlyList<Track>> GetLikedSongsAsync(HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
            => _inner.GetLikedSongsAsync(level, ct);
        public Task<SearchResults> SearchAsync(string query, CancellationToken ct = default)
            => Task.FromResult(_payload);
        public Task<HomeContribution> GetHomeAsync(CancellationToken ct = default)
            => _inner.GetHomeAsync(ct);
        public Task<LibraryStats> GetStatsAsync(CancellationToken ct = default)
            => _inner.GetStatsAsync(ct);
    }
}
