using System.Linq;
using System.Runtime.CompilerServices;

namespace Wavee.Core;

/// <summary>The source-agnostic façade the UI binds against (docs/plans/wavee/architecture.md §4.3). Implements the UI-facing
/// <see cref="IMusicLibrary"/> by federating over a <see cref="SourceRegistry"/>: single-item reads route to the first
/// owning source; collection reads MERGE (concat) across catalog sources — each source contributes only what it has,
/// so the union is clean. Provider-mappings / dedup / fallback chains are the documented extension point (trivial with
/// one real source today). This is the layer Connect/playback federation (FederatedPlayback/Remote) will sit beside.</summary>
public sealed class AggregateCatalog : IMusicLibrary, ICollectionEvents
{
    readonly SourceRegistry _reg;
    readonly ICatalogSource? _fallback;
    readonly SimpleSubject<CollectionKind> _collections = new();

    public AggregateCatalog(SourceRegistry registry)
    {
        _reg = registry;
        // The EXPLICIT last-resort step (design §2.1): the first source declaring SourceCapabilities.Fallback answers a
        // single-item read that no source OWNS (or whose owner had no data). Ownership stayed with the real owners when
        // FakeSource stopped claiming "everything that isn't spotify:", so this is what keeps an unrecognized uri
        // opening a populated page in the demo backend — and its ABSENCE in the real backend is deliberate: a real
        // account must not invent an entity.
        _fallback = registry.OfCapability(SourceCapabilities.Fallback).OfType<ICatalogSource>().FirstOrDefault();
        // Fan-in: any source that emits its own collection deltas forwards into the ONE aggregate stream the cache
        // subscribes to (off-page library freshness, docs/plans/wavee/architecture.md §6). No source raises it today → neutral seam.
        foreach (var s in registry.All.OfType<ISourceCollectionEvents>())
            s.CollectionsChanged.Subscribe(new ActionObserver<CollectionKind>(k => _collections.OnNext(k)));
    }

    /// <summary>The aggregated library-delta stream — the cache refreshes the named collection in place, even off-page.</summary>
    public IObservable<CollectionKind> CollectionsChanged => _collections;

    // ── single-item reads: first owning source that returns non-null wins; else a minimal empty shape ──
    public async Task<Playlist> GetPlaylistAsync(string id, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
    {
        foreach (var s in _reg.CatalogSources)
            if (s.Owns(id) && await s.GetPlaylistAsync(id, level, ct).ConfigureAwait(false) is { } p) return p;
        if (Fallback(id) is { } fb && await fb.GetPlaylistAsync(id, level, ct).ConfigureAwait(false) is { } fp) return fp;
        return new Playlist(id, id, "", null, "", null, 0, System.Array.Empty<Track>());
    }

    public async Task<Album> GetAlbumAsync(string id, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
    {
        foreach (var s in _reg.CatalogSources)
            if (s.Owns(id) && await s.GetAlbumAsync(id, level, ct).ConfigureAwait(false) is { } a) return a;
        if (Fallback(id) is { } fb && await fb.GetAlbumAsync(id, level, ct).ConfigureAwait(false) is { } fa) return fa;
        return new Album(id, id, "", null, System.Array.Empty<ArtistRef>(), 0, 0, System.Array.Empty<Track>());
    }

    public async Task<Artist> GetArtistAsync(string id, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
    {
        foreach (var s in _reg.CatalogSources)
            if (s.Owns(id) && await s.GetArtistAsync(id, level, ct).ConfigureAwait(false) is { } a) return a;
        if (Fallback(id) is { } fb && await fb.GetArtistAsync(id, level, ct).ConfigureAwait(false) is { } fa) return fa;
        return new Artist(id, id, "", null);
    }

    // Same three-step shape as the single-item reads: owner → fallback → empty. The fallback step is what keeps a
    // context nobody owns (a synthetic podcast, an unrecognized uri) playable in the demo backend.
    public IAsyncEnumerable<TrackPage> StreamTracksAsync(string contextUri, CancellationToken ct = default)
        => _reg.OwnerOf(contextUri)?.StreamTracksAsync(contextUri, ct)
           ?? Fallback(contextUri)?.StreamTracksAsync(contextUri, ct)
           ?? EmptyPages(ct);

    // Paged discography window + facet total — routed to the owning source (mirrors GetArtistAsync). The live source
    // (StoreLibrarySource) owns paging + the real facet total; the DIM on non-paging sources serves the overview slice.
    public async Task<DiscographyPage> GetDiscographyAsync(string artistUri, DiscographyKind kind, int offset, int limit, CancellationToken ct = default)
    {
        foreach (var s in _reg.CatalogSources)
            if (s.Owns(artistUri)) return await s.GetDiscographyAsync(artistUri, kind, offset, limit, ct).ConfigureAwait(false);
        if (Fallback(artistUri) is { } fb) return await fb.GetDiscographyAsync(artistUri, kind, offset, limit, ct).ConfigureAwait(false);
        return new DiscographyPage(System.Array.Empty<Album>(), 0);
    }

    /// <summary>The fallback source for a uri — null when there is none, or when it already had its turn in the loop
    /// above (it OWNS the uri), so the last resort is never asked the same question twice.</summary>
    ICatalogSource? Fallback(string uri) => _fallback is { } f && !f.Owns(uri) ? f : null;

    /// <summary>The one discography kind↔AlbumKind filter (Singles ⇒ Single OR EP), shared by the overview-slice DIM
    /// (<see cref="ICatalogSource"/>) and the live source so the offline count matches the live facet grouping.</summary>
    public static bool KindMatches(AlbumKind ak, DiscographyKind dk) => dk switch
    {
        DiscographyKind.Singles => ak is AlbumKind.Single or AlbumKind.EP,
        DiscographyKind.Compilations => ak == AlbumKind.Compilation,
        _ => ak == AlbumKind.Album,
    };

    // ── merged collections (each source returns EMPTY where it has no data → clean union, no dups) ──
    public async Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct = default)
    {
        var r = new List<LibraryItem>();
        foreach (var s in _reg.CatalogSources) r.AddRange(await s.GetLibraryAsync(ct).ConfigureAwait(false));
        return r;
    }

    public async Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken ct = default)
    {
        var r = new List<PlaylistSummary>();
        foreach (var s in _reg.CatalogSources) r.AddRange(await s.GetPlaylistsAsync(ct).ConfigureAwait(false));
        return r;
    }

    /// <summary>The folder-capable tree, CONCATENATED in source-registration order (the same merge shape as
    /// <see cref="GetPlaylistsAsync"/>), so a source's user-created <c>wavee:playlist:*</c> leaves federate in alongside
    /// another source's folder tree. Nothing is re-nested across sources — a source owns its own folder structure.</summary>
    public async Task<IReadOnlyList<PlaylistNode>> GetPlaylistTreeAsync(CancellationToken ct = default)
    {
        var r = new List<PlaylistNode>();
        foreach (var s in _reg.CatalogSources) r.AddRange(await s.GetPlaylistTreeAsync(ct).ConfigureAwait(false));
        return r;
    }

    /// <summary>Merged uri → added-at map. First writer wins per uri (registration order), matching the "first owning
    /// source answers" rule for single-item reads; a later source never overwrites an earlier source's timestamp.</summary>
    public async Task<IReadOnlyDictionary<string, long>> GetLibraryAddedAtAsync(CancellationToken ct = default)
    {
        Dictionary<string, long>? merged = null;
        foreach (var s in _reg.CatalogSources)
        {
            var part = await s.GetLibraryAddedAtAsync(ct).ConfigureAwait(false);
            if (part.Count == 0) continue;
            merged ??= new Dictionary<string, long>(part.Count, StringComparer.Ordinal);
            foreach (var kv in part) merged.TryAdd(kv.Key, kv.Value);
        }
        return merged ?? SidebarTree.NoAddedAt;
    }

    public async Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default)
    {
        var r = new List<Album>();
        foreach (var s in _reg.CatalogSources) r.AddRange(await s.GetAlbumsAsync(ct).ConfigureAwait(false));
        return r;
    }

    public async Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default)
    {
        var r = new List<Artist>();
        foreach (var s in _reg.CatalogSources) r.AddRange(await s.GetArtistsAsync(ct).ConfigureAwait(false));
        return r;
    }

    public async Task<IReadOnlyList<Track>> GetLikedSongsAsync(HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
    {
        var r = new List<Track>();
        foreach (var s in _reg.CatalogSources) r.AddRange(await s.GetLikedSongsAsync(level, ct).ConfigureAwait(false));
        return r;
    }

    public Task<SearchResults> SearchAsync(string query, CancellationToken ct = default)
        => SearchAsync(query, SearchFacet.All, 0, 30, ct);

    public async Task<SearchResults> SearchAsync(string query, SearchFacet facet, int offset, int limit, CancellationToken ct = default)
    {
        var t = new List<Track>(); var al = new List<Album>(); var ar = new List<Artist>(); var pl = new List<Playlist>();
        IReadOnlyList<SearchTopHit>? topHits = null;
        IReadOnlyList<SearchChip>? chipOrder = null;
        IReadOnlyList<Show>? shows = null;
        IReadOnlyList<Episode>? episodes = null;
        IReadOnlyList<SearchTopHit>? audiobooks = null;
        IReadOnlyList<SearchTopHit>? profiles = null;
        IReadOnlyList<SearchGenre>? genres = null;
        IReadOnlyList<SearchTopHit>? authors = null;
        int tt = 0, at = 0, art = 0, pt = 0;
        int showsT = -1, epT = -1, booksT = -1, profT = -1, genT = -1, authT = -1;
        foreach (var s in _reg.CatalogSources)
        {
            var x = await s.SearchAsync(query, facet, offset, limit, ct).ConfigureAwait(false);
            t.AddRange(x.Tracks); al.AddRange(x.Albums); ar.AddRange(x.Artists); pl.AddRange(x.Playlists);
            topHits ??= x.TopHits;
            chipOrder ??= x.ChipOrder;
            shows ??= x.Shows;
            episodes ??= x.Episodes;
            audiobooks ??= x.Audiobooks;
            profiles ??= x.Profiles;
            genres ??= x.Genres;
            authors ??= x.Authors;
            tt += x.TracksTotal >= 0 ? x.TracksTotal : x.Tracks.Count;
            at += x.AlbumsTotal >= 0 ? x.AlbumsTotal : x.Albums.Count;
            art += x.ArtistsTotal >= 0 ? x.ArtistsTotal : x.Artists.Count;
            pt += x.PlaylistsTotal >= 0 ? x.PlaylistsTotal : x.Playlists.Count;
            if (showsT < 0 && x.ShowsTotal >= 0) showsT = x.ShowsTotal;
            if (epT < 0 && x.EpisodesTotal >= 0) epT = x.EpisodesTotal;
            if (booksT < 0 && x.AudiobooksTotal >= 0) booksT = x.AudiobooksTotal;
            if (profT < 0 && x.ProfilesTotal >= 0) profT = x.ProfilesTotal;
            if (genT < 0 && x.GenresTotal >= 0) genT = x.GenresTotal;
            if (authT < 0 && x.AuthorsTotal >= 0) authT = x.AuthorsTotal;
        }
        return new SearchResults(t, al, ar, pl, topHits, tt, at, art, pt,
            Shows: shows, ShowsTotal: showsT,
            Episodes: episodes, EpisodesTotal: epT,
            Audiobooks: audiobooks, AudiobooksTotal: booksT,
            Profiles: profiles, ProfilesTotal: profT,
            ChipOrder: chipOrder,
            Genres: genres, GenresTotal: genT,
            Authors: authors, AuthorsTotal: authT);
    }

    // Offline library search: the first source that has cached data wins (the store-backed source); others default empty.
    public async Task<LibrarySearchResults> SearchLibraryAsync(string query, LibrarySearchScope scope, CancellationToken ct = default)
    {
        foreach (var s in _reg.CatalogSources)
        {
            var x = await s.SearchLibraryAsync(query, scope, ct).ConfigureAwait(false);
            if (!x.IsEmpty) return x;
        }
        return LibrarySearchResults.Empty;
    }

    public async Task<IReadOnlyList<string>> SuggestAsync(string query, CancellationToken ct = default)
    {
        var x = await SuggestRichAsync(query, ct).ConfigureAwait(false);
        return x.Queries;
    }

    public async Task<SearchSuggestions> SuggestRichAsync(string query, CancellationToken ct = default)
    {
        // First source that returns suggestions wins (the online source); offline sources default to empty.
        foreach (var s in _reg.CatalogSources)
        {
            var x = await s.SuggestRichAsync(query, ct).ConfigureAwait(false);
            if (x.Queries.Count + x.Items.Count > 0) return x;
        }
        return SearchSuggestions.Empty;
    }

    public async Task<LibraryStats> GetStatsAsync(CancellationToken ct = default)
    {
        int al = 0, ar = 0, lk = 0, pod = 0;
        foreach (var s in _reg.CatalogSources)
        {
            var st = await s.GetStatsAsync(ct).ConfigureAwait(false);
            al += st.Albums; ar += st.Artists; lk += st.LikedSongs; pod += st.Podcasts;
        }
        return new LibraryStats(al, ar, lk, pod);
    }

    public async Task<HomeFeed> GetHomeAsync(CancellationToken ct = default)
    {
        var contribs = new List<HomeContribution>();
        foreach (var s in _reg.CatalogSources)
        {
            var c = await s.GetHomeAsync(ct).ConfigureAwait(false);
            if (c.Groups.Count > 0 || c.Sections is { Count: > 0 }) contribs.Add(c);
        }
        contribs.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        var groups = contribs.SelectMany(c => c.Groups).ToList();
        var sections = contribs.Where(c => c.Sections is { Count: > 0 }).SelectMany(c => c.Sections!).ToList();
        // The facet row belongs to whichever source actually publishes one (only the streaming source does). First
        // non-empty wins by priority order, so a local-library contribution can never blank out Spotify's chips.
        var chips = contribs.FirstOrDefault(c => c.Chips is { Count: > 0 })?.Chips;
        // The greeting is the SERVER's, by the same first-non-empty-wins rule as the chips: it arrives already localized
        // for the account, so it is right even when the machine clock and the account locale disagree. Sources that
        // publish none (local library, fakes) leave it empty and the view shows the bare page — deliberately NOT a
        // client-side one synthesised from DateTime.Now, which is what this used to do and got wrong for anyone
        // travelling or running a differently-localized OS.
        var greeting = contribs.FirstOrDefault(c => c.Greeting is { Length: > 0 })?.Greeting ?? "";
        return new HomeFeed(greeting, groups, chips, sections);
    }

    // ── podcasts: federated to the Podcasts-capable sources (route single-show reads to the owner; merge the grid) ──
    public async Task<IReadOnlyList<Show>> GetShowsAsync(CancellationToken ct = default)
    {
        var r = new List<Show>();
        foreach (var s in _reg.OfCapability(SourceCapabilities.Podcasts).OfType<IPodcastSource>())
            r.AddRange(await s.GetShowsAsync(ct).ConfigureAwait(false));
        return r;
    }

    public async Task<Show?> GetShowAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
    {
        foreach (var s in _reg.OfCapability(SourceCapabilities.Podcasts).OfType<IPodcastSource>())
            if (s.Owns(uri) && await s.GetShowAsync(uri, level, ct).ConfigureAwait(false) is { } show) return show;
        return null;
    }

    public async Task<int> LoadMoreEpisodesAsync(string showUri, int from, CancellationToken ct = default)
    {
        foreach (var s in _reg.OfCapability(SourceCapabilities.Podcasts).OfType<IPodcastSource>())
            if (s.Owns(showUri)) return await s.LoadMoreEpisodesAsync(showUri, from, ct).ConfigureAwait(false);
        return from;   // nobody owns it ⇒ the cursor did not move
    }

    static async IAsyncEnumerable<TrackPage> EmptyPages([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
