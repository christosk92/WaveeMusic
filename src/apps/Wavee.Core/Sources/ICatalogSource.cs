namespace Wavee.Core;

/// <summary>A connected source of music (a Spotify account, a local-files library, the synthetic fallback). It owns a
/// URI namespace and declares which facets it supports. Facets are SEGREGATED into narrow ports (this file is the
/// Catalog facet; see SeamPorts.cs for the playback/remote/session/lyrics ports) so a source implements only what it
/// can — no god-interface (docs/plans/wavee/architecture.md §4).</summary>
public interface ISource
{
    /// <summary>Stable id, e.g. "spotify", "local", "fake".</summary>
    string Id { get; }

    /// <summary>True if this source owns/can-resolve the given URI (by scheme/namespace). The aggregate routes
    /// single-item reads to the first owning source.</summary>
    bool Owns(string uri);

    /// <summary>The facets this source supports.</summary>
    SourceCapabilities Capabilities { get; }
}

/// <summary>The Catalog facet: the read surface for one source. Single-item reads return null when the source has no
/// data for the URI (the aggregate then tries the next source / fallback). Collection reads return EMPTY when the
/// source has nothing to contribute (so the aggregate's concat-merge yields clean, non-duplicated lists).</summary>
public interface ICatalogSource : ISource
{
    /// <summary>How this source UPGRADES an entity it owns (the hydration façade — design §1.3). Default: a
    /// complete-at-construction source (export / local / fake / user playlists / every test fake) has nothing to
    /// fetch, so every rung is already reached. Only a networked source overrides it — which is also why this is a
    /// default interface member rather than a new required member every fake would have to implement.</summary>
    IEntityHydrator Hydrator => CompleteEntityHydrator.Instance;

    // ── single-item reads (the owning source answers; null = not mine / no data) ──
    // `level` is the HYDRATION RUNG the caller needs before it can paint (design §1.2 / §3): the source ensures it
    // through its own Hydrator and only then reads. Defaulted to Open — "this surface paints its primary content" —
    // so every existing caller keeps its meaning; a page that wants the second-transport facets asks for Rich/Full.
    Task<Playlist?> GetPlaylistAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default);
    Task<Album?> GetAlbumAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default);
    Task<Artist?> GetArtistAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default);

    /// <summary>Stream a context's tracks in pages (skeleton-then-stream). Yields nothing for a context it doesn't own.</summary>
    IAsyncEnumerable<TrackPage> StreamTracksAsync(string contextUri, CancellationToken ct = default);

    /// <summary>Page an artist's discography facet. Default impl serves from the overview slice — correct for any
    /// non-paging source (fake/export/local): the in-memory TopAlbums filtered by kind, total = the filtered count. A
    /// probe (<c>limit &lt;= 0</c>) returns <c>(empty, total)</c> so it never materializes the whole list as a bogus page.
    /// The live source (StoreLibrarySource) overrides this to page the real facet.</summary>
    async Task<DiscographyPage> GetDiscographyAsync(string artistUri, DiscographyKind kind, int offset, int limit, CancellationToken ct = default)
    {
        var artist = await GetArtistAsync(artistUri, HydrationLevel.Open, ct).ConfigureAwait(false);
        var all = artist?.TopAlbums ?? System.Array.Empty<Album>();
        var filtered = new List<Album>();
        foreach (var a in all) if (AggregateCatalog.KindMatches(a.Kind, kind)) filtered.Add(a);
        var items = new List<Album>();
        for (int i = offset; i < filtered.Count && items.Count < limit; i++) items.Add(filtered[i]);   // limit <= 0 → empty window
        return new DiscographyPage(items, filtered.Count);
    }

    // ── collection contributions (merged by the aggregate; EMPTY when this source has none) ──
    Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Track>> GetLikedSongsAsync(HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default);
    Task<SearchResults> SearchAsync(string query, CancellationToken ct = default);
    Task<SearchResults> SearchAsync(string query, SearchFacet facet, int offset, int limit, CancellationToken ct = default)
        => SearchAsync(query, ct);
    /// <summary>Offline, cache-only full-text search scoped per <see cref="LibrarySearchScope"/>. Default empty — only
    /// the persistent store-backed source (<c>StoreLibrarySource</c>) overrides it.</summary>
    Task<LibrarySearchResults> SearchLibraryAsync(string query, LibrarySearchScope scope, CancellationToken ct = default)
        => Task.FromResult(LibrarySearchResults.Empty);
    /// <summary>As-you-type search suggestions (the omnibar dropdown). Default empty — only an online source provides them.</summary>
    Task<IReadOnlyList<string>> SuggestAsync(string query, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(System.Array.Empty<string>());
    async Task<SearchSuggestions> SuggestRichAsync(string query, CancellationToken ct = default)
        => new(await SuggestAsync(query, ct).ConfigureAwait(false), System.Array.Empty<SearchSuggestionItem>());
    Task<HomeContribution> GetHomeAsync(CancellationToken ct = default);
    Task<LibraryStats> GetStatsAsync(CancellationToken ct = default);

    /// <summary>This source's FOLDER-CAPABLE playlist tree. Default: a leaves-only projection of
    /// <see cref="GetPlaylistsAsync"/> — a source with no rootlist markers has no folders to report, and every existing
    /// source (and test fake) keeps compiling untouched. Only the store-backed source overrides it.</summary>
    async Task<IReadOnlyList<PlaylistNode>> GetPlaylistTreeAsync(CancellationToken ct = default)
        => SidebarTree.FromFlat(await GetPlaylistsAsync(ct).ConfigureAwait(false));

    /// <summary>uri → added-at (unix ms) for this source's timestamped saved collections (albums / artists / shows).
    /// Default: empty — a source with no save timestamps reports nothing rather than fabricating an order.</summary>
    Task<IReadOnlyDictionary<string, long>> GetLibraryAddedAtAsync(CancellationToken ct = default)
        => Task.FromResult(SidebarTree.NoAddedAt);
}
