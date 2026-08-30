using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Spotify;
using Wavee.Core;

namespace Wavee.SpotifyLive.Hydration;

// ── The Spotify arm of the ONLINE-READ seam (hydration façade design §2.7) ───────────────────────────────────────────
// Search / suggest / suggest-rich / home, moved verbatim off LiveSessionHost's statics and the four `libSrc.Live*` hook
// assignments they fed. StoreLibrarySource now holds ONE IOnlineCatalog and calls it unconditionally; the composition
// root swaps this in at go-live and back to OfflineOnlineCatalog on logout.
//
// ENGINE-FREE by construction (this folder is source-globbed into Wavee.Tests, which has no FluentGpu.Engine reference):
// every engine-shaped dependency the moved code had is a delegate the live bootstrap supplies —
//   • the Home facet          → Func<string?>   (was `() => svc.HomeFacet.Peek()`)
//   • the Home module titles  → Func<HomeModuleTitles> (was `HomeModuleCopy.Titles`, which resolves through Loc)
//   • the Home epoch bump     → Action          (was `() => postUi(() => svc.HomeFeedEpoch.Value++)`)
// — exactly the shape the epoch bump already had before the move.
sealed class SpotifyOnlineCatalog : IOnlineCatalog, IDisposable
{
    readonly PathfinderClient _pathfinder;
    readonly IEntityHydrator _hydrator;
    readonly LiveHomeCache _home;
    // The SESSION's token, not the caller's: the search trait post-step is fire-and-forget work that must die with the
    // session rather than with the keystroke that started it (the read it warms outlives its own request).
    readonly CancellationToken _sessionCt;

    public SpotifyOnlineCatalog(
        PathfinderClient pathfinder,
        PathfinderResource pathfinderResource,
        IStore store,
        IEntityHydrator hydrator,
        Func<string?> homeFacet,
        Func<HomeModuleTitles> homeTitles,
        Func<string, CancellationToken, Task> fetchPlaylistHeader,
        Func<string, CancellationToken, Task<byte[]?>> probePlaylistRevision,
        Action? bumpHomeEpoch = null,
        CancellationToken sessionCt = default)
    {
        _pathfinder = pathfinder;
        _hydrator = hydrator;
        _sessionCt = sessionCt;
        _home = new LiveHomeCache(pathfinderResource, homeFacet, homeTitles, store,
            fetchPlaylistHeader, probePlaylistRevision, bumpHomeEpoch);
    }

    // ── search ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Paged online search. Never returns null (a failure THROWS — see <see cref="IOnlineCatalog"/>), so the
    /// caller's null branch stays unambiguously "logged out".</summary>
    public async Task<SearchResults?> SearchAsync(string query, SearchFacet facet, int offset, int limit, CancellationToken ct = default)
    {
        // Online search rows are transient mapper output (never store joins), so warm their traits at read time: the
        // badge comes from the mapped totalCount, but PLAY-time correctness needs the cache.
        var results = await FetchSearchAsync(_pathfinder, query, facet, offset, limit, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Spotify search returned no response.");
        if (results.Tracks.Count > 0)
        {
            var trackUris = new List<string>(results.Tracks.Count);
            foreach (var t in results.Tracks) trackUris.Add(t.Uri);
            _ = _hydrator.EnsureTraitsAsync(trackUris, TraitSurface.Search, _sessionCt);
        }
        return results;
    }

    static async Task<SearchResults?> FetchSearchAsync(PathfinderClient pf, string query, SearchFacet facet, int offset, int limit, CancellationToken ct)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 50);

        void Vars(Utf8JsonWriter w)
        {
            w.WriteBoolean("includePreReleases", false);
            w.WriteBoolean("includeAlbumPreReleases", true);
            w.WriteNumber("numberOfTopResults", limit);
            w.WriteString("searchTerm", query);
            w.WriteNumber("offset", offset);
            w.WriteNumber("limit", limit);
            w.WriteBoolean("includeAudiobooks", true);
            w.WriteBoolean("includeAuthors", true);
            w.WriteBoolean("includeEpisodeContentRatingsV2", true);
        }
        // The unified top-results op (the "All" tab) declares a DIFFERENT variable set, keyed on "query" (not "searchTerm").
        void VarsTop(Utf8JsonWriter w)
        {
            w.WriteString("query", query);
            w.WriteNumber("limit", limit);
            w.WriteNumber("offset", offset);
            w.WriteNumber("numberOfTopResults", 50);
            w.WriteBoolean("includeArtistHasConcertsField", false);
            w.WriteBoolean("includeAudiobooks", true);
            w.WriteBoolean("includeAuthors", true);
            w.WriteBoolean("includePreReleases", true);
            w.WriteBoolean("includeAlbumPreReleases", false);
            w.WriteBoolean("includeEpisodeContentRatingsV2", true);
            w.WriteNull("isPrefix");
            w.WriteStartArray("sectionFilters");
            w.WriteStringValue("GENERIC");
            w.WriteStringValue("VIDEO_CONTENT");
            w.WriteEndArray();
        }

        // Audiobooks is the ONE facet whose op sends includePreReleases:true (wire-verified, omg.saz sid 0671).
        void VarsAudiobooks(Utf8JsonWriter w)
        {
            w.WriteBoolean("includePreReleases", true);
            w.WriteBoolean("includeAlbumPreReleases", true);
            w.WriteNumber("numberOfTopResults", limit);
            w.WriteString("searchTerm", query);
            w.WriteNumber("offset", offset);
            w.WriteNumber("limit", limit);
            w.WriteBoolean("includeAudiobooks", true);
            w.WriteBoolean("includeAuthors", true);
            w.WriteBoolean("includeEpisodeContentRatingsV2", true);
        }

        // searchFullEpisodes takes a MINIMAL shape — sending the shared one would not match the persisted query.
        void VarsEpisodes(Utf8JsonWriter w)
        {
            w.WriteString("searchTerm", query);
            w.WriteNumber("offset", offset);
            w.WriteNumber("limit", limit);
            w.WriteBoolean("includeEpisodeContentRatingsV2", true);
        }

        // searchGenres: capture 1.2.96.518 (search.saz SID 098). Keyed on searchTerm; includeAlbumPreReleases false.
        void VarsGenres(Utf8JsonWriter w)
        {
            w.WriteBoolean("includePreReleases", false);
            w.WriteBoolean("includeAlbumPreReleases", false);
            w.WriteNumber("numberOfTopResults", 20);
            w.WriteString("searchTerm", query);
            w.WriteNumber("offset", offset);
            w.WriteNumber("limit", limit);
            w.WriteBoolean("includeAudiobooks", true);
            w.WriteBoolean("includeAuthors", true);
            w.WriteBoolean("includeEpisodeContentRatingsV2", true);
        }

        var callerCt = ct;
        using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(callerCt);
        searchCts.CancelAfter(TimeSpan.FromSeconds(8));
        ct = searchCts.Token;

        try
        {
            if (facet == SearchFacet.All)
            {
                using var topd = await pf.QueryAsync(PathfinderOps.SearchTopResults, PathfinderOps.SearchTopResultsHash, VarsTop, PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
                if (topd is null) throw new InvalidOperationException("Spotify top-results search failed.");
                var topHits = Wavee.Core.SpotifyExportMapper.TopHitsFromV2(topd.RootElement);
                var totals = Wavee.Core.SpotifyExportMapper.SearchFromV2(topd.RootElement);
                return totals with { TopHits = topHits };
            }

            var (op, hash) = facet switch
            {
                SearchFacet.Tracks => (PathfinderOps.SearchTracks, PathfinderOps.SearchTracksHash),
                SearchFacet.Albums => (PathfinderOps.SearchAlbums, PathfinderOps.SearchAlbumsHash),
                SearchFacet.Artists => (PathfinderOps.SearchArtists, PathfinderOps.SearchArtistsHash),
                SearchFacet.Playlists => (PathfinderOps.SearchPlaylists, PathfinderOps.SearchPlaylistsHash),
                SearchFacet.Podcasts => (PathfinderOps.SearchPodcasts, PathfinderOps.SearchPodcastsHash),
                SearchFacet.Audiobooks => (PathfinderOps.SearchAudiobooks, PathfinderOps.SearchAudiobooksHash),
                SearchFacet.Episodes => (PathfinderOps.SearchFullEpisodes, PathfinderOps.SearchFullEpisodesHash),
                SearchFacet.Profiles => (PathfinderOps.SearchUsers, PathfinderOps.SearchUsersHash),
                SearchFacet.Genres => (PathfinderOps.SearchGenres, PathfinderOps.SearchGenresHash),
                SearchFacet.Authors => (PathfinderOps.SearchAuthors, PathfinderOps.SearchAuthorsHash),
                // Unreachable: every SearchFacet member is mapped above. Kept as a loud failure so a NEW enum member
                // added without an operation fails at the call instead of silently returning empty results.
                _ => throw new NotSupportedException($"Search facet '{facet}' is not wired to a Pathfinder operation."),
            };

            // Two ops do NOT take the shared variable shape:
            //   searchAudiobooks  — the only op sending includePreReleases:TRUE
            //   searchFullEpisodes — a completely different, minimal shape (no numberOfTopResults / include* flags)
            Action<Utf8JsonWriter> vars = facet switch
            {
                SearchFacet.Audiobooks => VarsAudiobooks,
                SearchFacet.Episodes => VarsEpisodes,
                SearchFacet.Genres => VarsGenres,
                _ => Vars,
            };

            using var doc = await pf.QueryAsync(op, hash, vars, PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
            if (doc is null) throw new InvalidOperationException($"Spotify {facet} search failed.");
            return Wavee.Core.SpotifyExportMapper.SearchFromV2(doc.RootElement);
        }
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
        {
            throw new TimeoutException($"Spotify {facet} search timed out.");
        }
    }

    // ── suggest ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The omnibar's as-you-type completions — the query half of the SAME searchSuggestions response the rich
    /// shape returns (one op, never two).</summary>
    public async Task<IReadOnlyList<string>> SuggestAsync(string query, CancellationToken ct = default)
        => (await FetchSuggestRichAsync(_pathfinder, query, ct).ConfigureAwait(false)).Queries;

    public Task<SearchSuggestions> SuggestRichAsync(string query, CancellationToken ct = default)
        => FetchSuggestRichAsync(_pathfinder, query, ct);

    static async Task<SearchSuggestions> FetchSuggestRichAsync(PathfinderClient pf, string query, CancellationToken ct)
    {
        using var doc = await pf.QueryAsync(PathfinderOps.SearchSuggestions, PathfinderOps.SearchSuggestionsHash,
            w =>
            {
                w.WriteString("query", query);
                w.WriteNumber("limit", 30);
                w.WriteNumber("numberOfTopResults", 30);
                w.WriteNumber("offset", 0);
                w.WriteBoolean("includeAuthors", true);
                w.WriteBoolean("includeAlbumPreReleases", false);
                w.WriteBoolean("includeEpisodeContentRatingsV2", true);
            }, PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
        return doc is null ? SearchSuggestions.Empty : Wavee.Core.SpotifyExportMapper.SuggestionsFromV2(doc.RootElement);
    }

    // ── home ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The cached editorial home + separately refreshed recents.</summary>
    public async Task<LiveHomeResult?> GetHomeAsync(CancellationToken ct = default)
        => await _home.GetAsync(ct).ConfigureAwait(false);

    /// <summary>The reactivation compare a returning KeepAlive-parked Home page runs — the seam behind
    /// <c>Services.HomeFeedRevalidate</c>.</summary>
    public Task RevalidateHomeAsync(CancellationToken ct) => _home.RevalidateAsync(ct);

    /// <summary>Disposes the Home cache's store watch — the Home feed epoch's second publisher must not outlive the
    /// session that created it (one accumulated subscription per login otherwise).</summary>
    public void Dispose() => _home.Dispose();

    // The editorial/personalized home via Pathfinder → the existing composer (data.home.sectionContainer.sections).
    // The desktop query embeds recently-played inline, so the composer builds the recents shelf too — no extra call.
    // facet: a homeChips[].id ("music-chip", "podcasts-following-chip", …) or null/"" for the unfiltered feed.
    static async Task<LiveHomeResult> FetchHomeAsync(PathfinderResource pf, string? facet, Func<HomeModuleTitles> titles,
        CancellationToken ct, bool invalidate = false)
    {
        // The real local zone, as IANA. "Etc/UTC" used to be hardcoded here, which asked Spotify for someone else's
        // afternoon: the zone drives the greeting bucket and the time-of-day shelves.
        string tz = SpotifyTimeZone.LocalIana;
        Action<Utf8JsonWriter> variables = w => WriteHomeVariables(w, tz, facet);
        if (invalidate)
            pf.Invalidate(PathfinderOps.Home, PathfinderOps.HomeHash, variables, PathfinderClient.Platform.Desktop);
        using var doc = await pf.UseQueryAsync(PathfinderOps.Home, PathfinderOps.HomeHash, variables,
            PathfinderClient.Platform.Desktop, ct).ConfigureAwait(false);
        if (doc is null) return LiveHomeResult.Empty;
        var homeRoot = Wavee.Core.SpotifyExportMapper.Dig(doc.RootElement, "data", "home");
        var contribution = Wavee.Core.SpotifyHomeComposer.Compose(homeRoot, System.Array.Empty<Wavee.Core.PlaylistSummary>(),
            titles());
        // The greeting rides along: it is part of the SAME response (home.greeting), already localized for the account
        // by the server that also picked the time-of-day shelves for `tz` above.
        return new LiveHomeResult(contribution.Groups, contribution.Chips, contribution.Greeting, contribution.Sections);
    }

    static void WriteHomeVariables(Utf8JsonWriter w, string timeZone, string? facet)
    {
        w.WriteString("homeEndUserIntegration", "INTEGRATION_DESKTOP");
        w.WriteString("timeZone", timeZone);
        w.WriteString("sp_t", "");
        w.WriteString("facet", facet ?? "");
        w.WriteNumber("sectionItemsLimit", 10);
        w.WriteBoolean("includeEpisodeContentRatingsV2", true);
    }

    /// <summary>The live Home transport cache and the single publisher of the Home feed EPOCH. A Home page acquires its
    /// feed by READ, and a KeepAlive-parked page issues no reads, so without a published revision the only thing that
    /// could ever correct a page that came back was its own 60 s poll tick. Both events that can supersede a rendered
    /// feed are routed to one bump: a read resolving a new daylist identity, and the store rewriting the header of an
    /// identity this cache has already hydrated (which is exactly what opening the daylist detail page does).</summary>
    sealed class LiveHomeCache : IDisposable
    {
        readonly PathfinderResource _pf;
        readonly Func<string?> _facet;
        readonly Func<HomeModuleTitles> _titles;
        readonly IStore _store;
        // ONE hydrator for the cache's lifetime. It remembers which daylist identities already cost an invalidating Home
        // requery; a per-read hydrator would forget that and make every 60 s Home poll an uncached network fetch.
        readonly HomeDaylistHydrator _hydrator;
        readonly Action _bumpEpoch;
        readonly IDisposable? _storeSub;
        // -1 until the first resolve establishes the baseline, so a session's opening read (which always claims its
        // first identity) does not publish a bump nobody needed.
        long _publishedIdentity = -1;

        public LiveHomeCache(PathfinderResource pf, Func<string?> facet, Func<HomeModuleTitles> titles, IStore store,
            Func<string, CancellationToken, Task> fetchHeader,
            Func<string, CancellationToken, Task<byte[]?>> probeRevision, Action? bumpEpoch = null)
        {
            _pf = pf;
            _facet = facet;
            _titles = titles;
            _store = store;
            _bumpEpoch = bumpEpoch ?? (static () => { });
            _hydrator = new HomeDaylistHydrator(ReadHeader, fetchHeader, probeRevision,
                c => FetchHomeAsync(_pf, _facet(), _titles, c, invalidate: true));
            if (bumpEpoch is not null)
                _storeSub = store.Changes.Subscribe(Observers.From<Wavee.Backend.StoreChange>(OnStoreChanged));
        }

        /// <summary>The reactivation compare a returning KeepAlive-parked Home page runs: head-probe the hydrated
        /// daylist URI and, only if its revision has advanced, publish the epoch — which is what makes the page re-read
        /// through the ordinary path. Equal revision ⇒ the cached body is served untouched and nothing re-renders.
        /// Swallows its own failures: a compare is an optimisation, never a page state.</summary>
        public async Task RevalidateAsync(CancellationToken ct)
        {
            try { if (await _hydrator.RevalidateAsync(ct).ConfigureAwait(false)) _bumpEpoch(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch { }
        }

        // The facet is read at FETCH time, not at construction: the chip row writes Services.HomeFacet and asks for a
        // refresh, and PathfinderResource keys its TTL cache on the request body — so a facet change is a distinct
        // cache entry rather than a stale hit. The requery closure above re-reads it for the same reason: it must
        // invalidate the key the read it is repairing actually used, and a chip switch mid-read simply re-enters here.
        public async Task<LiveHomeResult> GetAsync(CancellationToken ct)
        {
            var source = await FetchHomeAsync(_pf, _facet(), _titles, ct).ConfigureAwait(false);
            var resolved = await _hydrator.ResolveAsync(source, ct).ConfigureAwait(false);
            long identity = _hydrator.IdentityVersion;
            long previous = Interlocked.Exchange(ref _publishedIdentity, identity);
            if (previous >= 0 && previous != identity) _bumpEpoch();   // this read superseded every OTHER page's feed
            return resolved;
        }

        // Fires on the WRITER's thread, sometimes from inside the store's own lock, so this deliberately reads nothing
        // back: the filter is a dictionary probe on the URIs the composed feed actually depends on, and the bump only
        // wakes a Home read that is TTL-cached and re-decides for itself. A rewrite that changes nothing therefore
        // costs one cache-hit recompose, not a network fetch.
        void OnStoreChanged(Wavee.Backend.StoreChange change)
        {
            if (change.Uri.Length == 0) return;   // a bulk wave names no uri and says nothing about a specific header
            if (!_hydrator.Hydrated(change.Uri)) return;
            _bumpEpoch();
        }

        public void Dispose() => _storeSub?.Dispose();

        HomePlaylistHeader? ReadHeader(string uri)
        {
            var playlist = _store.GetPlaylist(uri);
            if (playlist is null) return null;
            return new HomePlaylistHeader(playlist.Name,
                string.IsNullOrWhiteSpace(playlist.Description) ? null : playlist.Description,
                string.IsNullOrWhiteSpace(playlist.OwnerName) ? null : playlist.OwnerName,
                playlist.Cover, playlist.TrackCount);
        }
    }
}
