using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Localization;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Backend.Playlists;
using Wavee.Core;

namespace Wavee.Backend.Library;

// Disambiguate from the UI type Wavee.DiscographyPage (a Component) that is otherwise in scope under the Wavee.* namespace.
// (Declared inside the namespace so it wins over the enclosing-namespace member.)
using DiscographyPage = Wavee.Core.DiscographyPage;

// ── The catalog↔Store bridge ─────────────────────────────────────────────────────────────────────────────────────────
// A catalog source (the UI binds against ICatalogSource via AggregateCatalog) whose reads project the PERSISTENT Store:
// the unordered library sets (collection_items, via SavedUris) and the ordered playlist membership are JOINED at read to
// the shared entity rows. Heavy Track/Album/Artist/Show records live once in the Store; this never duplicates them — it
// joins by URI. Membership-scoped facts (added_by/added_at) come from the membership row, not the shared entity. The
// source also raises CollectionsChanged when a Store change lands, so the UI cache refreshes off-page without a reskeleton.
public sealed class StoreLibrarySource : ICatalogSource, IPodcastSource, ISourceCollectionEvents, IDisposable
{
    readonly IStore _store;
    readonly SwitchableEntityHydrator _hydration;
    readonly IOnlineCatalog _online;
    readonly WaveeLogger _log;

    /// <summary>Liked Songs' canonical collection uri — the entity the Liked surface's rung is measured on.</summary>
    const string LikedCollectionUri = "spotify:collection:tracks";
    // The OTHER saved sets' collection uris (CollectionHydration.SetOf — design §2.3): same background-ask shape as
    // Liked, so GetAlbumsAsync/GetArtistsAsync/GetShowsAsync stop being the one library read that never asks the
    // façade for its own collection rung.
    const string SavedAlbumsCollectionUri = "spotify:collection:albums";
    const string SavedArtistsCollectionUri = "spotify:collection:artists";
    const string SavedShowsCollectionUri = "spotify:collection:shows";

    /// <summary>The one background ask every saved-set read fires after its join: Background mode so nothing on
    /// screen ever waits on it, tagged <see cref="TraitSurface.LikedSongs"/> because that is
    /// <see cref="CollectionHydration"/>'s only row-trait surface (its trait pass is gated to the "liked"/"episodes"
    /// sets — <c>CollectionHydration.ContinueAsync</c>) — inert rather than misleading for albums/artists/shows,
    /// which this options value only ever pages at Identity, never decorates with a row bundle.</summary>
    static readonly HydrationOptions BackgroundCollectionAsk = new(HydrationMode.Background, Surface: TraitSurface.LikedSongs);
    readonly SimpleSubject<CollectionKind> _collections = new();
    readonly IDisposable _sub;

    /// <summary>The most recent non-empty <c>homeChips</c> set. A faceted home response does not reliably repeat the
    /// chip row, so it is remembered rather than re-read from every response — see the pin in <c>GetHomeAsync</c>.</summary>
    IReadOnlyList<HomeChip>? _lastHomeChips;

    /// <param name="hydration">THE hydration façade for this source (design §1.4/§3) — REQUIRED, never null. It is
    /// the <see cref="SwitchableEntityHydrator"/> the composition root owns: <c>OfflineEntityHydrator</c> until go-live,
    /// the <c>SpotifyProviderHydrator</c> after it, and back again on logout. Every read below asks it for the rung it
    /// needs and THEN reads the store — which is why this source has no fetch hooks and no "is it cold?" predicates
    /// left; both are the façade's job now.</param>
    /// <param name="online">THE online-read seam (design §2.7) — REQUIRED, never null. Search / suggest / home are the
    /// reads this source cannot answer from the Store; the composition root owns a <see cref="SwitchableOnlineCatalog"/>
    /// whose inner is <see cref="OfflineOnlineCatalog"/> until go-live and again after logout, so the four
    /// "is the live session up?" probes that used to live on this class are gone.</param>
    /// <remarks>P4-C: there is no user-profile seam here any more. A playlist owner / added-by contributor is a STORE
    /// ENTITY (<c>IStore.GetOwner</c>, hydrated by <c>UserHydration</c> through the same façade every other read uses),
    /// so this class no longer holds a service, a dependency map from user to playlists, or a subscription whose
    /// callback <c>store.Bump</c>ed those playlists — a READ source writing to the store to fake a change notification
    /// for data the store did not hold. A profile that lands late now repaints through <c>IStore.Changes</c>.</remarks>
    /// <param name="log">Diagnostics only (default = the no-op logger): the Liked Songs join reports its member/hydrated
    /// split so a count that disagrees with the server can be told apart from a hydration gap.</param>
    public StoreLibrarySource(IStore store, SwitchableEntityHydrator hydration, IOnlineCatalog online, WaveeLogger log = default)
    {
        _store = store;
        _hydration = hydration ?? throw new ArgumentNullException(nameof(hydration));
        _online = online ?? throw new ArgumentNullException(nameof(online));
        _log = log;
        _sub = _store.Changes.Subscribe(new ChangeObserver(this));
    }

    public string Id => "spotify-store";
    public bool Owns(string uri) => EntityUri.Parse(uri).Provider == EntityProviders.Spotify;
    public SourceCapabilities Capabilities => SourceCapabilities.Catalog | SourceCapabilities.Podcasts;

    /// <summary>P4: how the ROUTER reaches this source's ladder. It is the very same switchable every read below asks
    /// (offline store hydrator → <c>SpotifyProviderHydrator</c> at go-live → back on logout), never a second seam — so
    /// <c>Services.Hydrator</c> routing a <c>spotify:</c> uri here lands in exactly the implementation the source is
    /// already using. Overrides the <see cref="ICatalogSource.Hydrator"/> default (a complete-at-construction source).</summary>
    public IEntityHydrator Hydrator => _hydration;
    public IObservable<CollectionKind> CollectionsChanged => _collections;

    // ── single-item reads ──
    public async Task<Playlist?> GetPlaylistAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
    {
        if (level == HydrationLevel.None) return ComposePlaylist(uri, prefetchUsers: false);

        // The playlist plane is LibrarySync's, so the open plan is the one place that knows a baseline changes the
        // shape of the ask: no membership ⇒ nothing to paint ⇒ block on Open; a baseline ⇒ paint the cache now and let
        // the loop's own 5-minute/dirty gates decide whether it revalidates (OpenPolicy — design §2.1).
        var plan = OpenPolicy.For(EntityKind.Playlist, hasBaseline: _store.HasMembership(uri));
        if (plan.Blocking != HydrationLevel.None)
            await _hydration.EnsureAsync(uri, Max(plan.Blocking, level),
                new HydrationOptions(Surface: TraitSurface.PlaylistOpen), ct).ConfigureAwait(false);
        if (plan.Background != HydrationLevel.None)
            _ = _hydration.EnsureAsync(uri, Max(plan.Background, level),
                new HydrationOptions(HydrationMode.Background, plan.Revalidate, TraitSurface.PlaylistOpen), ct);
        return ComposePlaylist(uri, prefetchUsers: true);
    }

    Playlist? ComposePlaylist(string uri, bool prefetchUsers)
    {
        var header = _store.GetPlaylist(uri);
        if (header is null) return null;
        var revision = _store.PlaylistRevision(uri);
        if (header.Tuning is { } tuning && (revision is null || !BytesEqual(tuning.Revision, revision)))
            header = header with { Tuning = null };
        var members = _store.Membership(uri);
        if (prefetchUsers) PrefetchPlaylistUsers(uri, header, members);
        var owner = OverlayOwner(uri, header, collectionDependency: false, requestMissing: prefetchUsers);
        var tracks = JoinMembership(uri, members);
        Image? cover = header.Cover ?? MosaicCover(TilesFromTracks(tracks));   // cover-less → mosaic/single for the detail hero too
        return header with
        {
            OwnerName = owner?.Name ?? header.Owner?.Name ?? header.OwnerName,
            Owner = owner ?? header.Owner,
            Collaborators = BuildCollaborators(header, owner, members),
            Cover = cover,
            Tracks = tracks,
            TrackCount = tracks.Count,
        };
    }

    // 4+ distinct album covers → a 2×2 mosaic Image (Url empty + tiles, detected by Surfaces.Artwork/Shelf); 1–3 → the
    // first as a single cover (Url set, renders everywhere); 0 → null (placeholder).
    static Image? MosaicCover(IReadOnlyList<string>? tiles)
        => tiles is not { Count: > 0 } ? null
         : tiles.Count >= 4 ? new Image("", MosaicTiles: tiles)
         : new Image(tiles[0]);

    static IReadOnlyList<string>? TilesFromTracks(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) return null;
        var urls = new List<string>(4);
        var seen = new HashSet<string>();
        for (int i = 0; i < tracks.Count && urls.Count < 4; i++)
        {
            if (tracks[i].Image?.Url is not { Length: > 0 } u) continue;
            if (!seen.Add(tracks[i].Album?.Uri ?? u)) continue;
            urls.Add(u);
        }
        return urls.Count > 0 ? urls : null;
    }

    public async Task<Album?> GetAlbumAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
    {
        if (level == HydrationLevel.None) return ReJoinTracks(_store.GetAlbum(uri));

        // Captured BEFORE the ensure: was this album already past Open — i.e. is this a RE-open, not the ladder's
        // first-ever pass over it? AlbumHydration's own Rich branch already fires ONE trait pass on that first pass
        // (Rich_AwaitsOneTraitPassCarryingRowBundlePlayCountAndPublishing); the companion ask below exists for every
        // ask AFTER that one, so it must not fire on a cold open or it would simply duplicate the ladder's own ask.
        bool wasAlreadyOpen = _hydration.LevelOf(uri) >= HydrationLevel.Open;

        // Ensure THEN read: the ladder writes the store, the store is the single read model. The surface tag is what
        // picks the trait bundle the album rung awaits (RowBundle|PlayCount|Publishing) — see TraitPolicy.
        await _hydration.EnsureAsync(uri, level, new HydrationOptions(Surface: TraitSurface.AlbumOpen), ct).ConfigureAwait(false);
        var album = ReJoinTracks(_store.GetAlbum(uri));

        // An album with no ©/℗ facet at all can never reach Rich (HydrationLevels.Of), so it seals Exhausted for a
        // full day (HydrationPolicy.ExhaustedAlbumRichTtl) — and THAT seal skips AlbumHydration.ContinueAsync
        // WHOLESALE on every later open, its own PlayCount/RowBundle trait step included, for the same day, even
        // though a play count changing has nothing to do with why the album has no publishing facet. Firing the same
        // bundle again here, beside the ladder rather than only from inside it, keeps PLAYS/BPM·KEY alive on a re-open
        // regardless of the seal: EnsureTraitsAsync never consults the ledger (only the trait pipeline's own per-row
        // AlreadyHas mark + the session NegativeMemo), so a warm row costs nothing and a genuinely absent facet costs
        // one request per SESSION, never one per day. Gated to "still short of Rich after an already-open album" so a
        // cold open (where the ladder's own pass is what is running right now) or an album that DID reach Rich never
        // sees a duplicate ask.
        if (level >= HydrationLevel.Rich && wasAlreadyOpen && album is { Uri.Length: > 0 }
            && HydrationLevels.Of(album) < HydrationLevel.Rich)
            _ = _hydration.EnsureTraitsAsync(AlbumTraitUris(album), TraitSet.RowBundle | TraitSet.PlayCount | TraitSet.Publishing,
                TraitSurface.AlbumOpen);
        return album;
    }

    static List<string> AlbumTraitUris(Album album)
    {
        var uris = new List<string>((album.Tracks?.Count ?? 0) + 1) { album.Uri };
        if (album.Tracks is { Count: > 0 } tracks)
            for (int i = 0; i < tracks.Count; i++)
                if (tracks[i].Uri.Length > 0) uris.Add(tracks[i].Uri);
        return uris;
    }

    /// <summary>Re-join <see cref="Album.Tracks"/> against the store at read — the album equivalent of
    /// <see cref="ReJoinTopTracks"/> below, for the same reason: <c>AlbumHydration.RebuildTracklists</c> only re-joins
    /// from INSIDE its own Rich/Open post-steps, so a play count or tempo that landed on the shared Track row from any
    /// OTHER surface (a playlist trait pass, this class's own <see cref="AlbumTraitUris"/> companion ask above) stays
    /// invisible on the album page until that ladder step runs again — which the Rich-exhausted seal can delay a full
    /// day. Read-only: never writes back through <c>UpsertAlbum</c>, exactly like <see cref="ReJoinTopTracks"/>.</summary>
    Album? ReJoinTracks(Album? a)
    {
        if (a?.Tracks is not { Count: > 0 } tracks) return a;
        var rebuilt = new List<Track>(tracks.Count);
        bool changed = false;
        for (int i = 0; i < tracks.Count; i++)
        {
            var row = _store.GetTrack(tracks[i].Uri);
            if (row is not null && !ReferenceEquals(row, tracks[i])) changed = true;
            rebuilt.Add(row ?? tracks[i]);
        }
        return changed ? a with { Tracks = rebuilt } : a;
    }

    public async Task<Artist?> GetArtistAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
    {
        if (level == HydrationLevel.None) return ReJoinTopTracks(_store.GetArtist(uri));
        // No trait surface: an artist rung's own traits ride the chart step inside ArtistHydration (ArtistPopular).
        await _hydration.EnsureAsync(uri, level, HydrationOptions.Default, ct).ConfigureAwait(false);
        return ReJoinTopTracks(_store.GetArtist(uri));
    }

    /// <summary>Re-join <see cref="Artist.TopTracks"/> against the store at read — the same re-join shape as
    /// <c>AlbumHydration.RebuildTracklists</c> (AlbumHydration.cs:174-189). TopTracks is a denormalized snapshot
    /// written by ArtistHydration's chart pass on its own cadence (<c>HydrationPolicy.ArtistRichTtl</c>, ~12h — see
    /// ArtistHydration.cs's <c>ChartFetchedAt</c> gate); a Track row healed by any OTHER surface in the meantime (a
    /// TrackV4 identity landing, a trait pass) would otherwise not reach ArtistPopular until that stamp expires and
    /// the chart re-fetches. Only allocates the rebuilt list's replacement (the <c>with</c>) when at least one row
    /// instance actually differs — never writes back through <c>UpsertArtist</c>: this is a read-model copy, and the
    /// chart pass owns the stored shape (its own PlayCount-authoritative merge, ArtistHydration.cs §"stats rewrote
    /// TopTracks").</summary>
    Artist? ReJoinTopTracks(Artist? a)
    {
        if (a?.TopTracks is not { Count: > 0 } top) return a;
        var rebuilt = new List<Track>(top.Count);
        bool changed = false;
        for (int i = 0; i < top.Count; i++)
        {
            var row = _store.GetTrack(top[i].Uri);
            if (row is not null && !ReferenceEquals(row, top[i])) changed = true;
            rebuilt.Add(row ?? top[i]);
        }
        return changed ? a with { TopTracks = rebuilt } : a;
    }

    // Discography paging — now a pure in-memory slice. TopAlbums holds the WHOLE discography (ArtistV4 groups → stubs,
    // upgraded to resident cards by ArtistDiscography.Assemble), so paging is client-side and needs no network beyond the
    // V4 ensure GetArtistAsync already triggers. The facet total is simply the filtered count (what we actually hold).
    public async Task<DiscographyPage> GetDiscographyAsync(string uri, DiscographyKind kind, int offset, int limit, CancellationToken ct = default)
    {
        var artist = await GetArtistAsync(uri, HydrationLevel.Open, ct).ConfigureAwait(false);   // Open = the assembled discography
        var all = artist?.TopAlbums ?? Array.Empty<Album>();
        var filtered = new List<Album>();
        foreach (var a in all) if (AggregateCatalog.KindMatches(a.Kind, kind)) filtered.Add(a);   // shared kind filter (Singles ⇒ Single/EP)
        if (limit <= 0) return new DiscographyPage(Array.Empty<Album>(), filtered.Count);   // total-only probe
        var window = new List<Album>();
        for (int i = offset; i < filtered.Count && window.Count < limit; i++)
        {
            var a = filtered[i];
            // Heal a count-less card from the resident album row. ArtistDiscography.Assemble keeps TrackCount when the
            // full row is resident (it strips only Tracks), but a card re-fattened from a persisted stub written before
            // the stub carried a count still arrives as 0 — and the artist page's inline drawer reserves height from
            // this number. This heals existing stores with no migration; the stub field keeps new writes whole.
            if (a.TrackCount == 0 && _store.GetAlbum(a.Uri) is { TrackCount: > 0 } row) a = a with { TrackCount = row.TrackCount };
            window.Add(a);
        }
        // The per-card cover tint used to ride a fire-and-forget detect hook fired with ALBUM uris (which the video
        // detector dropped outright — only the adornment pass consumed them). It is now the trait pipeline's job,
        // addressed by uri and asked for by the surface that actually opens these albums; paging is a pure slice.
        return new DiscographyPage(window, filtered.Count);
    }

    /// <summary>The larger of two rungs — a caller may ask for MORE than the kind's open plan (the album page asks
    /// Rich, the below-the-fold panel Full); it may never ask for less than what the surface needs to paint.</summary>
    static HydrationLevel Max(HydrationLevel a, HydrationLevel b) => a >= b ? a : b;

    public async IAsyncEnumerable<TrackPage> StreamTracksAsync(string contextUri, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // The PLAY path never waits on Rich: Open is "this context has a usable, named tracklist", which is exactly
        // what the order below needs. (A page open asking for Rich runs concurrently and the ledger dedupes.)
        await _hydration.EnsureAsync(contextUri, HydrationLevel.Open, HydrationOptions.Default, ct).ConfigureAwait(false);
        IReadOnlyList<Track> tracks =
            EntityUri.KindOf(contextUri) == EntityKind.Playlist ? JoinMembership(contextUri)
            : _store.GetAlbum(contextUri)?.Tracks ?? Array.Empty<Track>();
        if (tracks.Count > 0) yield return new TrackPage(tracks, tracks.Count, tracks.Count);
    }

    // ── collection contributions (empty when this source has nothing for a kind) ──
    public Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LibraryItem>>(Array.Empty<LibraryItem>());

    public Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken ct = default)
    {
        var list = new List<PlaylistSummary>();
        foreach (var e in _store.Rootlist())
            if (e.Kind == 0 && EntityUri.KindOf(e.Uri) == EntityKind.Playlist)
                list.Add(SummaryOf(e.Uri));
        return Task.FromResult<IReadOnlyList<PlaylistSummary>>(list);
    }

    /// <summary>The FOLDER-CAPABLE tree over the very same rootlist rows <see cref="GetPlaylistsAsync"/> flattens: the
    /// builder keeps the kind-1/2 group markers as real (recursive) folder nodes instead of dropping them. The flat
    /// sibling above stays exactly as it was — every pre-folder consumer keeps its shape.</summary>
    public Task<IReadOnlyList<PlaylistNode>> GetPlaylistTreeAsync(CancellationToken ct = default)
        => Task.FromResult(RootlistTreeBuilder.Build(_store.Rootlist(), SummaryOf));

    /// <summary>The uri → added-at side-channel for the timestamped saved sets. These are the very timestamps
    /// <see cref="JoinSet"/> reads for ordering and then discards (the read-model records have nowhere to carry them), so
    /// the sidebar's "Recently added" sort gets a REAL server stamp for albums/artists/shows. Playlists are absent by
    /// construction: the rootlist is an ordered marker stream with no per-item date.</summary>
    public Task<IReadOnlyDictionary<string, long>> GetLibraryAddedAtAsync(CancellationToken ct = default)
    {
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        AddSet(map, "albums"); AddSet(map, "artists"); AddSet(map, "shows");
        return Task.FromResult<IReadOnlyDictionary<string, long>>(map);

        void AddSet(Dictionary<string, long> into, string setId)
        {
            var items = _store.SavedItems(setId);
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it.AddedAtMs <= 0) continue;                                  // unknown stamp → absent, never faked
                if (!into.TryGetValue(it.Uri, out var cur) || it.AddedAtMs > cur) into[it.Uri] = it.AddedAtMs;
            }
        }
    }

    public Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default)
    {
        var albums = JoinSet("albums", _store.GetAlbum);
        // Saved albums are a COLLECTION too (CollectionHydration, design §2.3): the same background ask Liked fires
        // in GetLikedSongsAsync, so a member that never got named through some OTHER surface still reaches Open.
        _ = _hydration.EnsureAsync(SavedAlbumsCollectionUri, HydrationLevel.Open, BackgroundCollectionAsk, ct);
        return Task.FromResult(albums);
    }

    public Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default)
    {
        var artists = JoinSet("artists", _store.GetArtist);
        _ = _hydration.EnsureAsync(SavedArtistsCollectionUri, HydrationLevel.Open, BackgroundCollectionAsk, ct);
        return Task.FromResult(artists);
    }

    // Liked Songs is an ADD-ORDERED collection (newest first — the Spotify default) with the add date a first-class,
    // sortable column: join the timestamped set (LikedMembershipJoin — an OUTER join: every member is a row, an
    // unhydrated one a titleless Placeholder), so the detail surface derives the Date-added column + default sort from
    // the data itself and its header count IS the membership count — the same number the sidebar, home and stats read
    // from SavedUris("liked"). A hydration gap therefore shows as shimmer rows, never as a smaller library.
    public Task<IReadOnlyList<Track>> GetLikedSongsAsync(HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
    {
        var rows = LikedMembershipJoin.Join(SortedByAddedDesc(_store.SavedItems("liked")), _store.GetTrack);
        _log.Event(WaveeLogLevel.Debug, "library.liked.rows", "Liked Songs rows joined",
            fields: [WaveeLogField.Of("members", rows.MemberCount), WaveeLogField.Of("hydrated", rows.HydratedCount)]);
        // Liked is a COLLECTION: its rung is about its members, so one background ask replaces the two fire-and-forget
        // hooks this used to fan out (paged member hydrate + video/adornment detect). CollectionHydration pages the
        // saved set at 300 and asks for the LikedSongs trait bundle itself — see design §2.3. It is also what turns a
        // placeholder row into a real one: the store change it lands re-runs this read.
        if (level != HydrationLevel.None)
            _ = _hydration.EnsureAsync(LikedCollectionUri, HydrationLevel.Open, BackgroundCollectionAsk, ct);
        return Task.FromResult(rows.Tracks);
    }

    public Task<SearchResults> SearchAsync(string query, CancellationToken ct = default)
        => SearchAsync(query, SearchFacet.All, 0, 30, ct);

    public async Task<SearchResults> SearchAsync(string query, SearchFacet facet, int offset, int limit, CancellationToken ct = default)
    {
        var q = query.Trim();
        if (q.Length == 0) return SearchResults.Empty;
        // Online catalog search (Pathfinder) is primary — the WHOLE Spotify catalog (tracks/albums/artists/playlists).
        // The Store's offline track index (the cached library) is the fallback when the live session isn't up.
        // The seam returns null for "no online catalog" and THROWS when a live catalog fails, so a broken session stays
        // loud instead of silently degrading to the (much smaller) offline index.
        if (await _online.SearchAsync(q, facet, offset, limit, ct).ConfigureAwait(false) is { } online) return online;
        // Offline: the resident scan PLUS the cold library (liked ∪ playlist membership) — a saved track that is on disk
        // but not resident must still be findable (Addendum A4). Facet gating and the 200-row cap are unchanged.
        var tracks = facet is SearchFacet.All or SearchFacet.Tracks ? LibraryTrackSearch.Search(_store, q) : Array.Empty<Track>();
        return new SearchResults(tracks, Array.Empty<Album>(), Array.Empty<Artist>(), Array.Empty<Playlist>(),
            TracksTotal: tracks.Count);
    }

    // Offline, cache-only full-text library search (the library page's left search box). Never touches the network, so it
    // stays instant; ranked+grouped by LibrarySearchIndex. Off the UI thread (Store reads are lock-safe); an empty store /
    // empty query → Empty.
    //
    // `ct` is now REAL, not just a start gate. `Task.Run(work, ct)` only refuses to START a cancelled work item; once the
    // walk was running, a superseded keystroke used to run to completion holding the cold store's read lock. The token is
    // threaded into LibrarySearchIndex.Run, which checks it at the top of both walks.
    public Task<LibrarySearchResults> SearchLibraryAsync(string query, LibrarySearchScope scope, CancellationToken ct = default)
        => query.Trim().Length == 0
            ? Task.FromResult(LibrarySearchResults.Empty)
            : Task.Run(() => RunLibrarySearch(query, scope, ct), ct);

    LibrarySearchResults RunLibrarySearch(string query, LibrarySearchScope scope, CancellationToken ct)
    {
        var corpus = CorpusFor(scope);
        // The search HYDRATES its survivors, and on a CachedStore each promotion into the hot tier raises a StoreChange
        // on THIS thread — which would otherwise invalidate the very corpus we are searching, once per keystroke, for
        // ever. The suppression is thread-scoped and covers only the walk, so a genuine concurrent write (the sync loop,
        // a user save — always another thread) still invalidates normally.
        bool prev = s_searchHydrating;
        s_searchHydrating = true;
        try { return LibrarySearchIndex.Run(_store, scope, query, corpus, ct); }
        finally { s_searchHydrating = prev; }
    }

    // ── the per-scope search corpus cache ────────────────────────────────────────────────────────────────────────────
    // Typing one query used to stream the WHOLE cold candidate corpus (up to three set-based statements, each holding the
    // cold store's read lock for its duration) once per character. The corpus is a pure function of (scope, library
    // state), so it is cached on this long-lived source and reused across keystrokes.
    //
    // WHAT INVALIDATES IT:
    //   • any StoreChange this source already observes (OnStoreChange — the same seam that drives CollectionsChanged):
    //     a saved-set add/remove, a membership adoption, a metadata upsert, and the single coalesced Bulk signal that a
    //     sync / the CachedStore warm pass ends with. That is deliberately coarser than "the corpus actually moved" —
    //     a stale corpus would hide a just-saved artist, and rebuilding is three statements.
    //   • a 30 s TTL, the conservative backstop for anything that mutates the cold tier WITHOUT a hot-tier change signal
    //     (the write-behind lane landing a row, the cache GC, another process).
    // EXCEPT the search's own hydration — see RunLibrarySearch.
    const long CorpusTtlMs = 30_000;
    sealed record CorpusEntry(int Gen, long Stamp, LibrarySearchCorpus Corpus);
    CorpusEntry? _corpusArtists;
    CorpusEntry? _corpusAlbums;
    int _corpusGen;
    [ThreadStatic] static bool s_searchHydrating;

    LibrarySearchCorpus CorpusFor(LibrarySearchScope scope)
    {
        bool artists = scope == LibrarySearchScope.Artists;
        // Read the generation BEFORE the load: a mutation that lands while we are streaming stamps the entry stale, so
        // the NEXT search rebuilds rather than serving a corpus that raced the write.
        int gen = Volatile.Read(ref _corpusGen);
        long now = Environment.TickCount64;
        var cached = artists ? Volatile.Read(ref _corpusArtists) : Volatile.Read(ref _corpusAlbums);
        if (cached is not null && cached.Gen == gen && now - cached.Stamp < CorpusTtlMs) return cached.Corpus;

        var built = LibrarySearchCorpus.Load(_store as Wavee.Backend.Persistence.ILibraryCandidateStore, scope);
        var entry = new CorpusEntry(gen, now, built);
        // A plain reference swap of an IMMUTABLE snapshot: two threadpool searches may both build, and the loser's work
        // is simply discarded — never a torn or half-indexed corpus.
        if (artists) Volatile.Write(ref _corpusArtists, entry); else Volatile.Write(ref _corpusAlbums, entry);
        return built;
    }

    // The plain suggest shape is best-effort chrome on a keystroke: a failure degrades to "no suggestions" rather than
    // surfacing an error, and offline the seam answers empty. Cancellation still propagates — a superseded keystroke
    // is not a failure.
    public async Task<IReadOnlyList<string>> SuggestAsync(string query, CancellationToken ct = default)
    {
        var q = query.Trim();
        if (q.Length == 0) return Array.Empty<string>();
        try { return await _online.SuggestAsync(q, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<string>(); }
    }

    // The rich shape is the omnibar's dropdown, and there a swallowed transport failure is indistinguishable from a
    // genuine "no results" — the box flashed "No results found" on a keystroke whose fetch had actually failed. So this
    // one PROPAGATES: the omnibar owns the distinction ("Couldn't load" vs empty) and cancellation keeps its meaning.
    public async Task<SearchSuggestions> SuggestRichAsync(string query, CancellationToken ct = default)
    {
        var q = query.Trim();
        if (q.Length == 0) return SearchSuggestions.Empty;
        return await _online.SuggestRichAsync(q, ct).ConfigureAwait(false);
    }

    // A home built from the synced library without appending a second library tail: pinned jump-back-in quick picks
    // (Liked + first playlists), followed by the live section-owned modules. Empty only on a truly empty store.
    //
    // DEGRADED sessions are the exception. When there is no live fetch at all (offline / tests) or the fetch failed or
    // came back with nothing, the live tail is empty and Home would collapse to at most nine quick-pick tiles. The
    // synced library is still resident, so the three shelves it can always build — "Your playlists" / "Your albums" /
    // "Your artists" — are re-emitted as the FALLBACK. They are never appended when live modules landed: that second
    // library tail is exactly what the section-owned Home replaced.
    //
    // …and every word of that is the UNFILTERED feed's story. A facet ("music-chip", "podcasts-chip", …) asks the
    // server for a DIFFERENT document, so both local contributions are off — see the two notes inside.
    public async Task<HomeContribution> GetHomeAsync(string? facet, CancellationToken ct = default)
    {
        bool faceted = facet is { Length: > 0 };
        var playlists = await GetPlaylistsAsync(ct).ConfigureAwait(false);
        int likedCount = _store.SavedUris("liked").Count;

        var groups = new List<HomeGroup>();

        // The personal quick matrix is the stable first module of the UNFILTERED home only. A facet is a different
        // document (Music drops recents/shows/books; Podcasts is shows) and Spotify does not re-inject the user's
        // playlists into it — neither do we. Re-prepending it was why Music looked like All at the fold.
        var quick = faceted ? null : QuickPicks(playlists, likedCount);

        IReadOnlyList<HomeChip>? chips = null;
        string greeting = "";
        IReadOnlyList<HomeSection>? liveSections = null;
        var liveGroups = new List<HomeGroup>();
        // A null answer is "no live Home" (logged out / no online catalog): the chip row is NOT pinned or carried in
        // that case — an offline feed has no facets to filter — which is exactly what the absent hook used to mean.
        LiveHomeResult? live = null;
        try { live = await _online.GetHomeAsync(facet, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A FACETED read has no local fallback: the library shelves are the unfiltered feed's degraded shape, and
            // serving them under "Podcasts" is a lie. Fail loud; the page keeps the previous feed and says so.
            if (faceted) throw;
            // Editorial Home is best-effort, but never SILENT: swallowed, this is indistinguishable from an account
            // the server has no modules for, and the only visible symptom is a Home that quietly lost its shelves.
            WaveeLog.Instance.Warn("library", "home.live.failed",
                "live home fetch failed; falling back to the library shelves: " + ex.Message);
        }
        // …and "offline" is the same lie by a quieter route: there is no such thing as a locally-computed "Podcasts".
        if (faceted && live is null)
            throw new InvalidOperationException("no live home for facet '" + facet + "' (offline)");
        if (live is { } feed)
        {
            liveGroups.AddRange(feed.Groups);
            // Pin the last non-empty set. A FACETED home response does not always repeat homeChips, and taking it
            // verbatim meant selecting a facet could drop the row that produced the selection: the chips vanished,
            // the greeting collapsed back to a bare hero, and the feed stayed filtered with no way to see or undo
            // it. The chip set is a near-static piece of account chrome, so the previous one is always a better
            // answer than none. A response that DOES carry chips still replaces it wholesale.
            chips = feed.Chips is { Count: > 0 } fresh ? _lastHomeChips = fresh : _lastHomeChips;
            greeting = feed.Greeting;
            liveSections = feed.Sections;
        }

        if (quick is { Count: > 0 })
            groups.Add(new HomeGroup(HomeGroupKind.QuickGrid, Loc.Get(Strings.Home.JumpBackIn), quick));
        groups.AddRange(liveGroups);

        // The degraded-session fallback (see the note on this method). Albums/artists are joined lazily — a healthy live
        // session never pays for two library reads it will not use. Never under a facet: an empty faceted document is
        // the server's own answer, and answering it with the user's playlists is the same lie as the failure branch.
        if (!faceted && liveGroups.Count == 0)
            await AddLibraryShelvesAsync(groups, playlists, ct).ConfigureAwait(false);

        return new HomeContribution(groups, Priority: 100, Chips: chips, Greeting: greeting, Sections: liveSections);
    }

    // "Jump back in": Liked Songs first (when there are any), then the head of the rootlist, capped at nine tiles.
    static List<HomeCard> QuickPicks(IReadOnlyList<PlaylistSummary> playlists, int likedCount)
    {
        var quick = new List<HomeCard>();
        if (likedCount > 0)
            quick.Add(new HomeCard("spotify:collection:tracks", Loc.Get(Strings.Detail.LikedSongs),
                Strings.Detail.SongCount(likedCount), null, HomeCardKind.Liked));
        for (int i = 0; i < playlists.Count && quick.Count < 9; i++)
            quick.Add(new HomeCard(playlists[i].Uri, playlists[i].Name, null, playlists[i].Cover, HomeCardKind.Playlist, playlists[i].MosaicTiles));
        return quick;
    }

    // The offline/failed-session library tail. Kinded Shelf — the deliberate "conventional shelf" fallback of
    // HomeGroupKind — and titled from the SAME three loc keys the shelves have always used, so a degraded Home keeps
    // its identity (and its drill pages) rather than rendering nine tiles and nothing else.
    async Task AddLibraryShelvesAsync(List<HomeGroup> groups, IReadOnlyList<PlaylistSummary> playlists, CancellationToken ct)
    {
        if (playlists.Count > 0)
        {
            var cards = new List<HomeCard>(playlists.Count);
            for (int i = 0; i < playlists.Count; i++)
            {
                var p = playlists[i];
                cards.Add(new HomeCard(p.Uri, p.Name, p.OwnerName, p.Cover, HomeCardKind.Playlist, p.MosaicTiles));
            }
            groups.Add(new HomeGroup(HomeGroupKind.Shelf, Loc.Get(Strings.Home.YourPlaylists), cards, TotalCount: cards.Count));
        }

        var albums = await GetAlbumsAsync(ct).ConfigureAwait(false);
        if (albums.Count > 0)
        {
            var cards = new List<HomeCard>(albums.Count);
            string subtitle = Loc.Get(Strings.Detail.Column.Album);
            for (int i = 0; i < albums.Count; i++)
            {
                var a = albums[i];
                cards.Add(new HomeCard(a.Uri, a.Name, subtitle, a.Cover, HomeCardKind.Album));
            }
            groups.Add(new HomeGroup(HomeGroupKind.Shelf, Loc.Get(Strings.Home.YourAlbums), cards, TotalCount: cards.Count));
        }

        var artists = await GetArtistsAsync(ct).ConfigureAwait(false);
        if (artists.Count > 0)
        {
            var cards = new List<HomeCard>(artists.Count);
            string subtitle = Loc.Get(Strings.Detail.Column.Artist);
            for (int i = 0; i < artists.Count; i++)
            {
                var a = artists[i];
                cards.Add(new HomeCard(a.Uri, a.Name, subtitle, a.Image, HomeCardKind.Artist));
            }
            groups.Add(new HomeGroup(HomeGroupKind.Shelf, Loc.Get(Strings.Home.YourArtists), cards, TotalCount: cards.Count));
        }
    }

    public Task<LibraryStats> GetStatsAsync(CancellationToken ct = default)
        => Task.FromResult(new LibraryStats(
            _store.SavedUris("albums").Count, _store.SavedUris("artists").Count,
            _store.SavedUris("liked").Count, _store.SavedUris("shows").Count));

    // ── IPodcastSource ──
    public Task<IReadOnlyList<Show>> GetShowsAsync(CancellationToken ct = default)
    {
        var shows = JoinSet("shows", _store.GetShow);
        _ = _hydration.EnsureAsync(SavedShowsCollectionUri, HydrationLevel.Open, BackgroundCollectionAsk, ct);
        return Task.FromResult(shows);
    }

    public async Task<Show?> GetShowAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
    {
        if (level == HydrationLevel.None) return ComposeShow(uri);

        // THE open plan, from the ONE table (OpenPolicy — design §2.1), exactly like GetPlaylistAsync above. A show is
        // (Open, Full): Open = header + membership + the FIRST page of episodes at Episode.Open, awaited because it is
        // the page the user is looking at; Full = the remaining pages, enqueued on the pump and repainted in place as
        // they land through IStore.Changes. The background arm used to be dropped on the floor here — the policy said
        // Full, this method asked for `level` and nothing else — so a 700-episode show stayed at 300 resident rows
        // unless the user tapped "Load more" twice, and OpenPolicy.For(Show).Background was dead code.
        var plan = OpenPolicy.For(EntityKind.Show);
        if (plan.Blocking != HydrationLevel.None)
            await _hydration.EnsureAsync(uri, Max(plan.Blocking, level),
                new HydrationOptions(Surface: TraitSurface.ShowOpen), ct).ConfigureAwait(false);
        if (plan.Background != HydrationLevel.None)
            _ = _hydration.EnsureAsync(uri, Max(plan.Background, level),
                new HydrationOptions(HydrationMode.Background, plan.Revalidate, TraitSurface.ShowOpen, Priority: -1), ct);
        return ComposeShow(uri);
    }

    Show? ComposeShow(string uri)
    {
        var show = _store.GetShow(uri);
        if (show is null) return null;
        var members = _store.Membership(uri);
        if (members.Count == 0) return show;
        var eps = new List<Episode>(members.Count);
        // The paging cursor's DERIVED floor: one past the last member that actually has a row. Derived rather than
        // remembered because it survives a restart and reflects every writer (the ladder's own background paging, a
        // Liked-Episodes sweep, a playlist carrying this show's episodes), not just this source's foreground asks.
        int resolvedThrough = 0;
        for (int i = 0; i < members.Count; i++)
            if (_store.GetEpisode(members[i].ItemUri) is { } e) { eps.Add(e); resolvedThrough = i + 1; }
        // TotalEpisodes is the MEMBERSHIP count, not the resident one: a 700-episode show opens with 300 rows joined
        // and the episode list has to know the other 400 exist before it can offer to page them in.
        // PagedThrough is the max of what LANDED and what was ASKED — "asked" matters on its own, because a page of
        // members that cannot hydrate (withdrawn / region-locked) lands nothing and must still advance the cursor.
        return show with
        {
            Episodes = eps.Count > 0 ? eps : show.Episodes,
            TotalEpisodes = members.Count,
            PagedThrough = Math.Min(members.Count, Math.Max(resolvedThrough, AskedThrough(uri))),
        };
    }

    /// <summary>The explicit next page of a show's episodes — one EpisodeV4 POST for members [from, from+300).
    /// The ladder itself pages the whole tail onto the pump at <c>HydrationLevel.Full</c>; this is the FOREGROUND ask
    /// for exactly the page the user scrolled to, so a long show fills on demand instead of waiting on a background
    /// queue that a nav-away could out-live.
    /// <para>Returns the NEW cursor (<c>Show.PagedThrough</c>): the membership offset now asked for, or
    /// <paramref name="from"/> unchanged when there was nothing left to ask. It advances even when the page produced no
    /// rows at all, which is the point — the old bool, read against a resident-vs-total count, pinned the load-more
    /// pill on screen forever and re-asked the same unanswerable members on every tap as soon as a show had one
    /// episode that could not hydrate.</para></summary>
    public async Task<int> LoadMoreEpisodesAsync(string showUri, int from, CancellationToken ct = default)
    {
        if (from < 0) from = 0;
        var members = _store.Membership(showUri);
        if (from >= members.Count) return from;
        int end = Math.Min(members.Count, from + HydrationLevels.ShowOpenPage);
        var page = new List<string>(end - from);
        for (int i = from; i < end; i++)
            if (members[i].ItemUri is { Length: > 0 } u) page.Add(u);
        // A page of uri-less membership rows is still a page WALKED: advance the cursor, or the caller re-asks it forever.
        MarkAskedThrough(showUri, end);
        if (page.Count == 0) return end;
        await _hydration.EnsureManyAsync(page, HydrationLevel.Open,
            new HydrationOptions(Surface: TraitSurface.ShowOpen), ct).ConfigureAwait(false);
        return end;
    }

    // ── the show paging cursor (design §2.3) ─────────────────────────────────────────────────────────────────────────
    // Session state, one int per show the user actually paged, because "we asked for these members" is not a fact the
    // store can hold: a member that answers with nothing is indistinguishable from a member nobody asked about. The
    // DERIVED floor in GetShowAsync (one past the last resident member) does the heavy lifting and survives restarts;
    // this covers only the gap it cannot see — a foreground page whose rows never arrived.
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _askedThrough = new(StringComparer.Ordinal);

    int AskedThrough(string showUri) => _askedThrough.TryGetValue(showUri, out int n) ? n : 0;

    void MarkAskedThrough(string showUri, int through)
        => _askedThrough.AddOrUpdate(showUri, through, (_, current) => current > through ? current : through);

    // ── joins ──
    // Every library set reads in ADD order, newest first (the Spotify collection default); unknown timestamps (0) sink
    // to the end.
    IReadOnlyList<T> JoinSet<T>(string setId, Func<string, T?> get) where T : class
    {
        var items = SortedByAddedDesc(_store.SavedItems(setId));
        var list = new List<T>(items.Count);
        for (int i = 0; i < items.Count; i++) { var v = get(items[i].Uri); if (v is not null) list.Add(v); }   // inner join: skip not-yet-hydrated
        return list;
    }

    static List<SavedItem> SortedByAddedDesc(IReadOnlyList<SavedItem> items)
    {
        var list = new List<SavedItem>(items);
        list.Sort((a, b) => b.AddedAtMs.CompareTo(a.AddedAtMs));
        return list;
    }

    IReadOnlyList<Track> JoinMembership(string playlistUri) => JoinMembership(playlistUri, _store.Membership(playlistUri));

    IReadOnlyList<Track> JoinMembership(string playlistUri, IReadOnlyList<PlaylistMember> members)
    {
        var list = new List<Track>(members.Count);
        for (int i = 0; i < members.Count; i++)
        {
            var m = members[i];
            // An EPISODE is a playable too: a playlist holding one used to lose the row entirely (GetTrack only), which
            // broke its count, its mosaic and its play context. EpisodeAsTrack is the projection (design §1.5).
            var t = _store.GetTrack(m.ItemUri) ?? EpisodeAsTrack.From(_store.GetEpisode(m.ItemUri));
            if (t is null) continue;   // offline-first inner join: a not-yet-hydrated member has no row until it lands
            DateTimeOffset? at = m.AddedAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(m.AddedAt) : null;
            list.Add(t with { AddedAt = at, AddedBy = m.AddedBy, ContextUid = m.ItemId, Chart = m.Chart });   // stamp membership facts (+ per-row uid + chart rank) onto the read-model copy
        }
        return list;
    }

    PlaylistSummary SummaryOf(string uri)
    {
        var h = _store.GetPlaylist(uri);
        int count = _store.Membership(uri).Count;
        var tiles = h?.Cover is null ? MosaicTilesOf(uri) : null;   // no custom cover → a 2×2 mosaic (or single) from the tracks
        Image? cover = h?.Cover ?? MosaicCover(tiles);
        // Editability flags (feed the "Copy to playlist" picker): a playlist is editable when the user can edit items or
        // owns it; the h-is-null branch defaults both to false (PlaylistSummary defaults).
        bool canEdit = h is not null && (h.Capabilities.CanEditItems || h.Capabilities.IsOwner);
        bool isOwner = h is not null && h.Capabilities.IsOwner;
        return h is null
            ? new PlaylistSummary(uri, uri, "", count, cover, tiles)
            : new PlaylistSummary(uri, h.Name, OwnerDisplayName(uri, h, collectionDependency: true), count > 0 ? count : h.TrackCount, cover, tiles, CanEdit: canEdit, IsOwner: isOwner);
    }

    // Up to 4 DISTINCT album covers from the playlist's resident tracks — the mosaic source for a cover-less playlist.
    // Derived read-through (NOT memoized on the header), so it recomputes when the tracklist changes.
    string OwnerDisplayName(string playlistUri, Playlist header, bool collectionDependency)
    {
        var owner = OverlayOwner(playlistUri, header, collectionDependency);
        return owner?.Name ?? header.Owner?.Name ?? header.OwnerName;
    }

    /// <summary>The resolved owner for a playlist header: the store's own <c>Owner</c> row when one has landed, the
    /// header's embedded (usually id-only) owner otherwise — and an ASK for the row when it has not, in the background
    /// mode so nothing on screen waits for a byline. The ledger dedupes the repeat asks a re-render produces.</summary>
    Owner? OverlayOwner(string playlistUri, Playlist header, bool collectionDependency, bool requestMissing = true)
    {
        var raw = RawOwnerId(header);
        if (raw.Length == 0) return header.Owner;
        if (_store.GetOwner(raw) is { } resolved) return resolved;
        if (requestMissing) EnsureOwners(new[] { raw });
        return header.Owner;
    }

    static string RawOwnerId(Playlist header)
        => header.Owner?.Id is { Length: > 0 } id ? id : header.OwnerName;

    void PrefetchPlaylistUsers(string playlistUri, Playlist header, IReadOnlyList<PlaylistMember> members)
    {
        var ids = new List<string>(1 + members.Count);
        var owner = RawOwnerId(header);
        if (owner.Length > 0) ids.Add(owner);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < members.Count; i++)
        {
            var id = members[i].AddedBy;
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
            ids.Add(id);
        }
        EnsureOwners(ids);
    }

    /// <summary>THE one door for owner identities (design 2.3, UserHydration): canonicalize, drop what is already
    /// resident, and ask the façade in the background. Fire-and-forget by contract — a byline is never worth blocking a
    /// page on, and the resolved rows arrive as an ordinary store change.</summary>
    void EnsureOwners(IReadOnlyList<string> rawIds)
    {
        if (rawIds.Count == 0) return;
        List<string>? uris = null;
        for (int i = 0; i < rawIds.Count; i++)
        {
            if (UserProfileIds.Normalize(rawIds[i]) is not { } canonical) continue;
            if (_store.GetOwner(canonical) is not null) continue;
            (uris ??= new List<string>(rawIds.Count)).Add(canonical);
        }
        if (uris is null) return;
        _ = _hydration.EnsureManyAsync(uris, HydrationLevel.Identity,
            new HydrationOptions(HydrationMode.Background, Surface: TraitSurface.UserProfiles));
    }

    IReadOnlyList<Owner>? BuildCollaborators(Playlist header, Owner? resolvedOwner, IReadOnlyList<PlaylistMember> members)
    {
        var result = new List<Owner>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rawOwner = RawOwnerId(header);
        var owner = resolvedOwner ?? header.Owner;
        if (owner is not null) Add(owner);
        else if (rawOwner.Length > 0) Add(new Owner(ProfileId(rawOwner), header.OwnerName, null));

        for (int i = 0; i < members.Count; i++)
        {
            var raw = members[i].AddedBy;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var profile = _store.GetOwner(raw);
            Add(new Owner(profile?.Id ?? ProfileId(raw), profile?.Name ?? raw, profile?.Avatar));
        }

        return result.Count > 0 ? result : header.Collaborators;

        void Add(Owner value)
        {
            var key = UserProfileIds.Normalize(value.Id) ?? value.Id;
            if (!seen.Add(key)) return;
            result.Add(value);
        }
    }

    static string ProfileId(string raw)
        => UserProfileIds.BareId(UserProfileIds.Normalize(raw) ?? raw);

    internal IReadOnlyList<string>? MosaicTilesOf(string uri)
    {
        var members = _store.Membership(uri);
        if (members.Count == 0) return null;
        var urls = new List<string>(4);
        var seen = new HashSet<string>();
        for (int i = 0; i < members.Count && urls.Count < 4; i++)
        {
            var t = _store.GetTrack(members[i].ItemUri);
            if (t?.Image?.Url is not { Length: > 0 } u) continue;
            if (!seen.Add(t.Album?.Uri ?? u)) continue;   // dedupe by album so a single-album playlist isn't 4× the same art
            urls.Add(u);
        }
        return urls.Count > 0 ? urls : null;
    }

    // ── change fan-out → CollectionsChanged ──
    void OnStoreChange(StoreChange c)
    {
        // Stamp the search corpus stale (see CorpusFor). Suppressed while THIS thread is inside a library search: the
        // walk's own hot-tier promotions come back through here and must not invalidate the corpus they are reading.
        if (!s_searchHydrating) Interlocked.Increment(ref _corpusGen);
        if (c.IsBulk) { foreach (var k in AllKinds) _collections.OnNext(k); return; }
        if (c.Kind is { } explicitKind) { _collections.OnNext(explicitKind); return; }
        if (KindOfUri(c.Uri) is { } kind) _collections.OnNext(kind);
    }

    static readonly CollectionKind[] AllKinds =
        { CollectionKind.Albums, CollectionKind.Artists, CollectionKind.Liked, CollectionKind.Shows, CollectionKind.Playlists };

    static CollectionKind? KindOfUri(string uri) => EntityUri.KindOf(uri) switch
    {
        EntityKind.Album => CollectionKind.Albums,
        EntityKind.Artist => CollectionKind.Artists,
        EntityKind.Show or EntityKind.Episode => CollectionKind.Shows,
        EntityKind.Playlist => CollectionKind.Playlists,
        // An OWNER landing changes what a playlist row says (the byline, the avatar) and nothing else, so it
        // invalidates the playlist collection. This is what replaced OnProfileChanged's user-to-playlists dependency
        // map plus its store.Bump: the row simply re-reads GetOwner when the grid re-renders.
        EntityKind.User => CollectionKind.Playlists,
        // A PRE-SAVE (spotify:prerelease:) deliberately falls through to null: no library page lists pre-saves, so there
        // is no collection to invalidate and no fan-out worth waking. The heart re-skins through LibraryBridge's
        // per-URI signal, which the optimistic toggle drives directly.
        _ => uri == "rootlist" ? CollectionKind.Playlists : null,
    };

    static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    sealed class ChangeObserver(StoreLibrarySource owner) : IObserver<StoreChange>
    {
        public void OnNext(StoreChange c) => owner.OnStoreChange(c);
        public void OnCompleted() { }
        public void OnError(Exception e) { }
    }

    public void Dispose() => _sub.Dispose();
}
