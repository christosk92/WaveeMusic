namespace Wavee.Core;

public enum LibraryItemKind { Track, Album, Artist, Playlist }

public sealed record LibraryItem(string Uri, string Title, string? Subtitle, Image? Image, LibraryItemKind Kind);

public enum SearchSuggestionKind { Track, Artist, Album, Playlist, Genre, Episode, Podcast, Audiobook, User }

public sealed record SearchSuggestionItem(
    SearchSuggestionKind Kind,
    string Uri,
    string Title,
    string? Subtitle,
    Image? Image,
    bool IsExplicit = false);

public sealed record SearchSuggestions(
    IReadOnlyList<string> Queries,
    IReadOnlyList<SearchSuggestionItem> Items)
{
    public static readonly SearchSuggestions Empty = new(
        System.Array.Empty<string>(), System.Array.Empty<SearchSuggestionItem>());

    /// <summary>First autocomplete query that starts with <paramref name="typed"/> (ordinal-ignore-case) and is
    /// longer — the inline ghost. Not blindly <c>Queries[0]</c>: <c>loff</c> ghosts <c>loffler</c> even when
    /// <c>koffie</c> ranks first.</summary>
    public static string? GhostFor(string typed, IReadOnlyList<string> queries)
    {
        if (string.IsNullOrEmpty(typed) || queries is null) return null;
        for (int i = 0; i < queries.Count; i++)
        {
            string q = queries[i];
            if (q.Length > typed.Length && q.StartsWith(typed, System.StringComparison.OrdinalIgnoreCase))
                return q;
        }
        return null;
    }
}

// Ordered as the result tabs read left-to-right. Every member maps to a real Pathfinder operation.
public enum SearchFacet { All, Tracks, Albums, Playlists, Audiobooks, Podcasts, Artists, Episodes, Profiles, Genres, Authors }

public enum SearchHitKind { Track, Artist, Album, Playlist, Audiobook, Podcast, Episode, Author, User, Genre, Unknown }

/// <summary>One chip from <c>searchV2.chipOrder</c> — server-ranked facet plus its <c>totalCount</c>.</summary>
public sealed record SearchChip(SearchFacet Facet, int Total);

/// <summary>A search-genres tile: name, <c>spotify:genre:…</c> uri, and extracted color as opaque ARGB (0 = none).</summary>
public sealed record SearchGenre(string Uri, string Name, uint Accent);

/// <summary>One row of Spotify's unified search top-results (topResultsV2.itemsV2) — SERVER ORDER preserved (the first
/// item IS the Top Result), each carrying its type and the per-hit eyebrow signals: a "LYRICS" lyric match, and an
/// audiobook's access note ("Included in Premium") plus optional secondary metadata such as publish date/duration.</summary>
public sealed record SearchTopHit(
    SearchHitKind Kind, string Uri, string Name, string Subtitle, string TypeLabel,
    Image? Image, bool RoundImage, bool Followable, bool MatchedLyrics, string? AccessLabel,
    string? Detail = null,
    string? Meta = null,
    bool MatchedTitle = false);

public sealed record SearchResults(
    IReadOnlyList<Track> Tracks,
    IReadOnlyList<Album> Albums,
    IReadOnlyList<Artist> Artists,
    IReadOnlyList<Playlist> Playlists,
    IReadOnlyList<SearchTopHit>? TopHits = null,
    int TracksTotal = -1,
    int AlbumsTotal = -1,
    int ArtistsTotal = -1,
    int PlaylistsTotal = -1,
    // The facets Spotify serves from their own operations. Shows/Episodes use the real podcast domain records because
    // the app has podcast surfaces to route into; audiobooks and profiles are LIST-ONLY today, so they reuse the
    // generic SearchTopHit (which already carries the access signifier and the round-image flag) instead of inventing
    // domain records nothing else consumes. Total -1 = "facet not queried", 0 = "queried, no results".
    IReadOnlyList<Show>? Shows = null,
    int ShowsTotal = -1,
    IReadOnlyList<Episode>? Episodes = null,
    int EpisodesTotal = -1,
    IReadOnlyList<SearchTopHit>? Audiobooks = null,
    int AudiobooksTotal = -1,
    IReadOnlyList<SearchTopHit>? Profiles = null,
    int ProfilesTotal = -1,
    IReadOnlyList<SearchChip>? ChipOrder = null,
    IReadOnlyList<SearchGenre>? Genres = null,
    int GenresTotal = -1,
    IReadOnlyList<SearchTopHit>? Authors = null,
    int AuthorsTotal = -1)
{
    public static readonly SearchResults Empty = new(
        System.Array.Empty<Track>(), System.Array.Empty<Album>(), System.Array.Empty<Artist>(), System.Array.Empty<Playlist>());

    public int TotalFor(SearchFacet facet) => facet switch
    {
        SearchFacet.Tracks => TracksTotal >= 0 ? TracksTotal : Tracks.Count,
        SearchFacet.Albums => AlbumsTotal >= 0 ? AlbumsTotal : Albums.Count,
        SearchFacet.Artists => ArtistsTotal >= 0 ? ArtistsTotal : Artists.Count,
        SearchFacet.Playlists => PlaylistsTotal >= 0 ? PlaylistsTotal : Playlists.Count,
        SearchFacet.Podcasts => ShowsTotal >= 0 ? ShowsTotal : Shows?.Count ?? 0,
        SearchFacet.Episodes => EpisodesTotal >= 0 ? EpisodesTotal : Episodes?.Count ?? 0,
        SearchFacet.Audiobooks => AudiobooksTotal >= 0 ? AudiobooksTotal : Audiobooks?.Count ?? 0,
        SearchFacet.Profiles => ProfilesTotal >= 0 ? ProfilesTotal : Profiles?.Count ?? 0,
        SearchFacet.Genres => GenresTotal >= 0 ? GenresTotal : Genres?.Count ?? 0,
        SearchFacet.Authors => AuthorsTotal >= 0 ? AuthorsTotal : Authors?.Count ?? 0,
        _ => TopHits?.Count ?? Tracks.Count + Albums.Count + Artists.Count + Playlists.Count,
    };

    /// <summary>True when the facet returned nothing — used to hide an empty result tab rather than showing a tab that
    /// leads to an empty pane.</summary>
    public bool HasAny(SearchFacet facet) => facet switch
    {
        SearchFacet.Tracks => Tracks.Count > 0,
        SearchFacet.Albums => Albums.Count > 0,
        SearchFacet.Artists => Artists.Count > 0,
        SearchFacet.Playlists => Playlists.Count > 0,
        SearchFacet.Podcasts => Shows is { Count: > 0 },
        SearchFacet.Episodes => Episodes is { Count: > 0 },
        SearchFacet.Audiobooks => Audiobooks is { Count: > 0 },
        SearchFacet.Profiles => Profiles is { Count: > 0 },
        SearchFacet.Genres => Genres is { Count: > 0 },
        SearchFacet.Authors => Authors is { Count: > 0 },
        _ => TopHits is { Count: > 0 },
    };
}

/// <summary>One page of a streamed track list (skeleton-then-stream — see docs/plans/wavee/architecture.md §3/§6): the tracks
/// resolved so far, the running loaded count, and the known total (so the UI can size a progress cue up front).</summary>
public sealed record TrackPage(IReadOnlyList<Track> Tracks, int Loaded, int Total);

/// <summary>The discography facet to page through on an artist page (the Spotify <c>discography.albums/singles/compilations</c>
/// split). A real source can have hundreds–thousands per facet, so the grid virtualizes — see <see cref="DiscographyPage"/>.</summary>
public enum DiscographyKind { Albums, Singles, Compilations }

/// <summary>One window of an artist's discography: the items <c>[offset, offset+Items.Count)</c> and the facet's known
/// <see cref="Total"/> — so the virtualized grid reserves extent for the whole facet up front and fills as you scroll.</summary>
public sealed record DiscographyPage(IReadOnlyList<Album> Items, int Total);

/// <summary>The read-API facade. Collapses WaveeMusic's library / album / artist / search read
/// paths (Pathfinder + SpClient) behind one async surface the UI binds against.</summary>
public interface IMusicLibrary
{
    // `level` = the hydration rung the caller needs before it paints (design §1.2). Defaulted to Open so every
    // existing call keeps its meaning; the album page asks Rich, the below-the-fold panel Full, the artist page Rich.
    Task<Playlist> GetPlaylistAsync(string id, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default);
    Task<Album> GetAlbumAsync(string id, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default);
    Task<Artist> GetArtistAsync(string id, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default);

    /// <summary>Page an artist's discography facet (the virtualized grid pulls windows as you scroll). Returns the slice
    /// <c>[offset, offset+limit)</c> + the facet total so the grid can reserve full extent before everything has loaded.</summary>
    Task<DiscographyPage> GetDiscographyAsync(string artistUri, DiscographyKind kind, int offset, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct = default);
    Task<SearchResults> SearchAsync(string query, CancellationToken ct = default);
    Task<SearchResults> SearchAsync(string query, SearchFacet facet, int offset, int limit, CancellationToken ct = default);
    /// <summary>Offline full-text search over the CACHED library, scoped per <see cref="LibrarySearchScope"/> (artists ▸
    /// albums ▸ tracks / saved albums ▸ tracks). Cache-only — never hits the network. Default empty (only the
    /// store-backed source contributes).</summary>
    Task<LibrarySearchResults> SearchLibraryAsync(string query, LibrarySearchScope scope, CancellationToken ct = default)
        => Task.FromResult(LibrarySearchResults.Empty);
    /// <summary>As-you-type search suggestions for the omnibar dropdown (online catalog; empty offline).</summary>
    Task<IReadOnlyList<string>> SuggestAsync(string query, CancellationToken ct = default);
    /// <summary>As-you-type search suggestions with typed rich hits from the same online response.</summary>
    Task<SearchSuggestions> SuggestRichAsync(string query, CancellationToken ct = default);

    // Per-collection read paths — the sidebar's "Your Library" rows route to their own page, each loading its own slice.
    Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Track>> GetLikedSongsAsync(HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default);

    // Sidebar IA read paths — the "Your Library" badge counts, the FLAT playlist list (what every pre-folder consumer
    // reads), and the folder-capable TREE beside it. Async so the shell can skeleton-load them like everything else.
    Task<LibraryStats> GetStatsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken ct = default);

    /// <summary>The FOLDER-CAPABLE playlist tree. Default: a leaves-only projection of <see cref="GetPlaylistsAsync"/> —
    /// a source with no rootlist markers has no folders to report, so every existing implementation keeps compiling
    /// untouched. Flat consumers keep reading <see cref="GetPlaylistsAsync"/> (or <see cref="SidebarTree.Flatten(IReadOnlyList{PlaylistNode})"/>).</summary>
    async Task<IReadOnlyList<PlaylistNode>> GetPlaylistTreeAsync(CancellationToken ct = default)
        => SidebarTree.FromFlat(await GetPlaylistsAsync(ct).ConfigureAwait(false));

    /// <summary>uri → added-at (unix ms) for the timestamped library collections (saved albums / artists / shows). The
    /// side-channel exists because <see cref="Album"/>/<see cref="Artist"/>/<see cref="Show"/> have nowhere to carry the
    /// stamp the store already has, and a breaking record change to add one is not worth it. Default: empty (a source
    /// that cannot date its saves reports nothing rather than fabricating an order).</summary>
    Task<IReadOnlyDictionary<string, long>> GetLibraryAddedAtAsync(CancellationToken ct = default)
        => Task.FromResult(SidebarTree.NoAddedAt);

    // Streamed track loading (skeleton-then-stream): the detail header loads via GetPlaylist/GetAlbum, then the rows
    // page in from here so a big context fills progressively instead of blocking on the whole list.
    IAsyncEnumerable<TrackPage> StreamTracksAsync(string contextUri, CancellationToken ct = default);

    // The condensed, grouped home feed (replaces the four-separate-collection-calls home). Merged across sources.
    Task<HomeFeed> GetHomeAsync(CancellationToken ct = default);

    // Podcasts (federated to the Podcasts-capable sources): the library grid of shows + a single show's episodes.
    Task<IReadOnlyList<Show>> GetShowsAsync(CancellationToken ct = default);
    Task<Show?> GetShowAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default);
    /// <summary>Page the next block of a show's episodes into residency (see <see cref="IPodcastSource"/>). Returns the
    /// new paging cursor (<c>Show.PagedThrough</c>); unchanged (<c>== from</c>) means the show has no further members,
    /// so the episode list drops its load-more affordance.</summary>
    Task<int> LoadMoreEpisodesAsync(string showUri, int from, CancellationToken ct = default);
}
