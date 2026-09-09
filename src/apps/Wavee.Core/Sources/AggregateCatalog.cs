using System.Runtime.CompilerServices;
using Wavee.Core.Catalog;

namespace Wavee.Core;

/// <summary>Task/stream boundary for non-mounted consumers. Public reads use normalized query projections.</summary>
public sealed class AggregateCatalog(SourceRegistry registry, IQueryService queries,
    Func<string, CatalogScope> scopeForSubject) : IMusicLibrary
{
    CatalogScope LibraryScope => scopeForSubject(CatalogSubjects.Home);
    async Task<T> Read<T>(QuerySpec<T> query, CancellationToken ct, QueryDemand? demand = null)
    {
        var snapshot = await queries.ReadOnceAsync(query, demand, ct).ConfigureAwait(false);
        if (!snapshot.Status.HasPrimaryData) throw new InvalidOperationException("The requested catalog data is unavailable.");
        return snapshot.Value;
    }
    public Task<Playlist> GetPlaylistAsync(string id, CancellationToken ct = default)
        => Read(new PlaylistDetailQuery(scopeForSubject(id), id), ct);
    public Task<Album> GetAlbumAsync(string id, CancellationToken ct = default)
        => Read(new AlbumDetailQuery(scopeForSubject(id), id), ct);
    public Task<Artist> GetArtistAsync(string id, CancellationToken ct = default)
        => Read(new ArtistDetailQuery(scopeForSubject(id), id), ct);
    public bool TryPeekAlbum(string uri, out Album? album)
    {
        using var handle = queries.Acquire(new AlbumDetailQuery(scopeForSubject(uri), uri));
        album = handle.Current.Status.HasPrimaryData ? handle.Current.Value : null;
        return album is not null;
    }
    public Task<DiscographyPage> GetDiscographyAsync(string artistUri, DiscographyKind kind, int offset, int limit, CancellationToken ct = default)
        => Read(new ArtistDiscographyQuery(scopeForSubject(artistUri), artistUri, kind, offset, limit), ct);
    Task<LibraryQuerySnapshot> Library(CancellationToken ct) => Read(new SidebarLibraryQuery(LibraryScope), ct);
    public async Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct = default)
        => (await Library(ct).ConfigureAwait(false)).Entries;
    public async Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken ct = default)
        => (await Library(ct).ConfigureAwait(false)).Playlists;
    public async Task<IReadOnlyList<PlaylistNode>> GetPlaylistTreeAsync(CancellationToken ct = default)
        => (await Library(ct).ConfigureAwait(false)).Tree;
    public async Task<IReadOnlyDictionary<string, long>> GetLibraryAddedAtAsync(CancellationToken ct = default)
        => (await Library(ct).ConfigureAwait(false)).AddedAt;
    public async Task<LibraryStats> GetStatsAsync(CancellationToken ct = default)
        => (await Library(ct).ConfigureAwait(false)).Stats;
    public Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default) => Read(new SavedAlbumsQuery(LibraryScope), ct);
    public Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default) => Read(new SavedArtistsQuery(LibraryScope), ct);
    public Task<IReadOnlyList<Show>> GetShowsAsync(CancellationToken ct = default) => Read(new SavedShowsQuery(LibraryScope), ct);
    public Task<IReadOnlyList<Track>> GetLikedSongsAsync(CancellationToken ct = default) => Read(new LikedSongsQuery(LibraryScope), ct);
    public Task<LibrarySearchResults> SearchLibraryAsync(string query, LibrarySearchScope scope, CancellationToken ct = default)
        => Read(new LibrarySearchQuery(LibraryScope, query, scope), ct);
    public Task<SearchResults> SearchAsync(string query, CancellationToken ct = default)
        => SearchAsync(query, SearchFacet.All, 0, 30, ct);
    public async Task<SearchResults> SearchAsync(string query, SearchFacet facet, int offset, int limit, CancellationToken ct = default)
    {
        var parts = new List<SearchResults>();
        foreach (var source in registry.OfCapability(SourceCapabilities.Search))
        {
            var snapshot = await queries.ReadOnceAsync(new SearchQuery(LibraryScope with { Provider = source.Id }, query, facet, offset, limit), cancellationToken: ct).ConfigureAwait(false);
            if (snapshot.Status.HasPrimaryData) parts.Add(snapshot.Value);
        }
        if (parts.Count == 0) return SearchResults.Empty;
        return parts[0] with
        {
            Tracks = parts.SelectMany(part => part.Tracks).DistinctBy(track => track.Uri).ToArray(),
            Albums = parts.SelectMany(part => part.Albums).DistinctBy(album => album.Uri).ToArray(),
            Artists = parts.SelectMany(part => part.Artists).DistinctBy(artist => artist.Uri).ToArray(),
            Playlists = parts.SelectMany(part => part.Playlists).DistinctBy(playlist => playlist.Uri).ToArray(),
            TracksTotal = parts.Sum(part => part.TotalFor(SearchFacet.Tracks)), AlbumsTotal = parts.Sum(part => part.TotalFor(SearchFacet.Albums)),
            ArtistsTotal = parts.Sum(part => part.TotalFor(SearchFacet.Artists)), PlaylistsTotal = parts.Sum(part => part.TotalFor(SearchFacet.Playlists)),
        };
    }
    public async Task<IReadOnlyList<string>> SuggestAsync(string query, CancellationToken ct = default)
        => (await SuggestRichAsync(query, ct).ConfigureAwait(false)).Queries;
    public async Task<SearchSuggestions> SuggestRichAsync(string query, CancellationToken ct = default)
    {
        foreach (var source in registry.OfCapability(SourceCapabilities.Search))
        {
            var snapshot = await queries.ReadOnceAsync(new SearchSuggestionsQuery(LibraryScope with { Provider = source.Id }, query), cancellationToken: ct).ConfigureAwait(false);
            if (snapshot.Status.HasPrimaryData && snapshot.Value is { } value && value.Queries.Count + value.Items.Count > 0) return value;
        }
        return SearchSuggestions.Empty;
    }
    public async Task<HomeFeed> GetHomeAsync(string? facet, CancellationToken ct = default)
    {
        var parts = new List<HomeFeed>();
        foreach (var source in registry.OfCapability(SourceCapabilities.Home))
        {
            var snapshot = await queries.ReadOnceAsync(new HomeQuery(LibraryScope with { Provider = source.Id }, facet), cancellationToken: ct).ConfigureAwait(false);
            if (snapshot.Status.HasPrimaryData) parts.Add(snapshot.Value);
        }
        return new(parts.FirstOrDefault(part => part.Greeting.Length > 0)?.Greeting ?? "",
            parts.SelectMany(part => part.Groups).ToArray(), parts.FirstOrDefault(part => part.Chips is { Count: > 0 })?.Chips,
            parts.SelectMany(part => part.Sections ?? []).ToArray(), facet ?? "");
    }
    public async Task<Show?> GetShowAsync(string uri, CancellationToken ct = default)
    {
        var snapshot = await queries.ReadOnceAsync(new ShowDetailQuery(scopeForSubject(uri), uri), cancellationToken: ct).ConfigureAwait(false);
        return snapshot.Status.HasPrimaryData ? snapshot.Value : null;
    }
    public async Task<int> LoadMoreEpisodesAsync(string showUri, int from, CancellationToken ct = default)
    {
        var snapshot = await queries.ReadOnceAsync(new ShowDetailQuery(scopeForSubject(showUri), showUri),
            QueryDemand.Initial, ct).ConfigureAwait(false);
        return snapshot.Status.HasPrimaryData ? Math.Max(from, snapshot.Value.PagedThrough) : from;
    }
    public async IAsyncEnumerable<TrackPage> StreamTracksAsync(string contextUri, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var scope = scopeForSubject(contextUri);
        var demand = QueryDemand.Initial;
        IReadOnlyList<Track> tracks;
        int total;
        switch (EntityUri.Parse(contextUri).Kind)
        {
            case EntityKind.Album:
                var album = await Read(new AlbumDetailQuery(scope, contextUri), ct, demand).ConfigureAwait(false);
                tracks = album.Tracks ?? []; total = album.TrackCount; break;
            case EntityKind.Artist:
                var artist = await Read(new ArtistDetailQuery(scope, contextUri), ct, demand).ConfigureAwait(false);
                tracks = artist.TopTracks ?? []; total = tracks.Count; break;
            case EntityKind.Show:
                var show = await Read(new ShowDetailQuery(scope, contextUri), ct, demand).ConfigureAwait(false);
                tracks = (show.Episodes ?? []).Select(episode => EpisodeAsTrack.From(episode, contextUri)!).ToArray(); total = show.TotalEpisodes; break;
            default:
                if (contextUri.EndsWith(":collection:tracks", StringComparison.Ordinal))
                { tracks = await Read(new LikedSongsQuery(scope), ct, demand).ConfigureAwait(false); total = tracks.Count; }
                else
                { var playlist = await Read(new PlaylistDetailQuery(scope, contextUri), ct, demand).ConfigureAwait(false); tracks = playlist.Tracks ?? []; total = playlist.TrackCount; }
                break;
        }
        yield return new(tracks, tracks.Count, total);
    }
    public static bool KindMatches(AlbumKind album, DiscographyKind kind) => kind switch
    {
        DiscographyKind.Singles => album is AlbumKind.Single or AlbumKind.EP,
        DiscographyKind.Compilations => album == AlbumKind.Compilation,
        _ => album == AlbumKind.Album,
    };
}
