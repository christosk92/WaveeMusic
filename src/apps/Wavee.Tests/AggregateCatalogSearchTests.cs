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
        await using var host = new CatalogQueryTestHost(online);
        var cat = host.Library;

        var r = await cat.SearchAsync("sleep");

        Assert.NotNull(r.ChipOrder);
        Assert.Equal(SearchFacet.Playlists, r.ChipOrder![0].Facet);
        Assert.Equal(SearchFacet.Genres, r.ChipOrder[1].Facet);
        Assert.Equal(8, r.GenresTotal);
        var genre = Assert.Single(r.Genres!);
        Assert.Equal("Sleep", genre.Name);
        Assert.Equal("spotify:genre:sleep", genre.Uri);
    }

    // The facet is a REQUEST parameter, not ambient state, so the aggregate has exactly two jobs with it: hand it to
    // every source it fans out to, and stamp it on the merged feed so the page can tell a late answer for a facet the
    // user has already left from the one it is waiting for. null ("no facet") and "" are the same unfiltered feed.
    [Fact]
    public async Task GetHomeAsync_ForwardsTheFacetToEverySource_AndStampsItOnTheFeed()
    {
        var online = new SearchMetaSource(SearchResults.Empty);
        await using var host = new CatalogQueryTestHost(online);
        var cat = host.Library;

        var feed = await cat.GetHomeAsync("music-chip");

        Assert.Equal("music-chip", online.LastFacet);
        Assert.Equal("music-chip", feed.Facet);

        var unfiltered = await cat.GetHomeAsync(null);
        Assert.Equal("", online.LastFacet);
        Assert.Equal("", unfiltered.Facet);
    }

    /// <summary>Catalog stub that answers search with a canned payload and otherwise behaves like
    /// <see cref="FakeSource"/> so the aggregate can concat-merge the four core collections.</summary>
    sealed class SearchMetaSource : ICatalogSource
    {
        readonly FakeSource _inner = new();
        readonly SearchResults _payload;

        public SearchMetaSource(SearchResults payload) => _payload = payload;

        /// <summary>The facet of the last home read this source was asked for — null until it is asked.</summary>
        public string? LastFacet { get; private set; }

        public string Id => "search-meta";
        public bool Owns(string uri) => false;
        public SourceCapabilities Capabilities => SourceCapabilities.Catalog | SourceCapabilities.Search | SourceCapabilities.Home;
        public IAsyncEnumerable<TrackPage> StreamTracksAsync(string uri, CancellationToken ct = default)
            => _inner.StreamTracksAsync(uri, ct);

        public Task<Playlist?> GetPlaylistAsync(string uri, CancellationToken ct = default)
            => _inner.GetPlaylistAsync(uri, ct);
        public Task<Album?> GetAlbumAsync(string uri, CancellationToken ct = default)
            => _inner.GetAlbumAsync(uri, ct);
        public Task<Artist?> GetArtistAsync(string uri, CancellationToken ct = default)
            => _inner.GetArtistAsync(uri, ct);
        public Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct = default)
            => _inner.GetLibraryAsync(ct);
        public Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken ct = default)
            => _inner.GetPlaylistsAsync(ct);
        public Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default)
            => _inner.GetAlbumsAsync(ct);
        public Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default)
            => _inner.GetArtistsAsync(ct);
        public Task<IReadOnlyList<Track>> GetLikedSongsAsync(CancellationToken ct = default)
            => _inner.GetLikedSongsAsync(ct);
        public Task<SearchResults> SearchAsync(string query, CancellationToken ct = default)
            => Task.FromResult(_payload);
        public Task<HomeContribution> GetHomeAsync(string? facet, CancellationToken ct = default)
        {
            LastFacet = facet;
            return _inner.GetHomeAsync(facet, ct);
        }
        public Task<LibraryStats> GetStatsAsync(CancellationToken ct = default)
            => _inner.GetStatsAsync(ct);
    }
}
