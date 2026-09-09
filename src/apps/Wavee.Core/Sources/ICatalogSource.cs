namespace Wavee.Core;

public interface ISource
{
    string Id { get; }
    bool Owns(string uri);
    SourceCapabilities Capabilities { get; }
}

/// <summary>Complete native source input. Resource providers normalize these finite reads; UI consumes query projections.</summary>
public interface ICatalogSource : ISource
{
    Task<Track?> GetTrackAsync(string uri, CancellationToken ct = default) => Task.FromResult<Track?>(null);
    Task<Playlist?> GetPlaylistAsync(string uri, CancellationToken ct = default);
    Task<Album?> GetAlbumAsync(string uri, CancellationToken ct = default);
    Task<Artist?> GetArtistAsync(string uri, CancellationToken ct = default);
    IAsyncEnumerable<TrackPage> StreamTracksAsync(string contextUri, CancellationToken ct = default);

    async Task<DiscographyPage> GetDiscographyAsync(string artistUri, DiscographyKind kind, int offset, int limit, CancellationToken ct = default)
    {
        var artist = await GetArtistAsync(artistUri, ct).ConfigureAwait(false);
        var filtered = (artist?.TopAlbums ?? []).Where(album => AggregateCatalog.KindMatches(album.Kind, kind)).ToArray();
        return new(filtered.Skip(Math.Max(0, offset)).Take(Math.Max(0, limit)).ToArray(), filtered.Length);
    }

    Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Track>> GetLikedSongsAsync(CancellationToken ct = default);
    Task<SearchResults> SearchAsync(string query, CancellationToken ct = default);
    async Task<SearchResults> SearchAsync(string query, SearchFacet facet, int offset, int limit, CancellationToken ct = default)
    {
        var all = await SearchAsync(query, ct).ConfigureAwait(false);
        int first = Math.Max(0, offset), count = Math.Max(0, limit);
        return all with
        {
            Tracks = all.Tracks.Skip(first).Take(count).ToArray(), Albums = all.Albums.Skip(first).Take(count).ToArray(),
            Artists = all.Artists.Skip(first).Take(count).ToArray(), Playlists = all.Playlists.Skip(first).Take(count).ToArray(),
            TracksTotal = all.TotalFor(SearchFacet.Tracks), AlbumsTotal = all.TotalFor(SearchFacet.Albums),
            ArtistsTotal = all.TotalFor(SearchFacet.Artists), PlaylistsTotal = all.TotalFor(SearchFacet.Playlists),
            TopHits = first == 0 ? all.TopHits : [],
        };
    }
    Task<IReadOnlyList<string>> SuggestAsync(string query, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);
    async Task<SearchSuggestions> SuggestRichAsync(string query, CancellationToken ct = default)
        => new(await SuggestAsync(query, ct).ConfigureAwait(false), []);
    Task<HomeContribution> GetHomeAsync(string? facet, CancellationToken ct = default);
    Task<LibraryStats> GetStatsAsync(CancellationToken ct = default);
    async Task<IReadOnlyList<PlaylistNode>> GetPlaylistTreeAsync(CancellationToken ct = default)
        => SidebarTree.FromFlat(await GetPlaylistsAsync(ct).ConfigureAwait(false));
    Task<IReadOnlyDictionary<string, long>> GetLibraryAddedAtAsync(CancellationToken ct = default)
        => Task.FromResult(SidebarTree.NoAddedAt);
}
