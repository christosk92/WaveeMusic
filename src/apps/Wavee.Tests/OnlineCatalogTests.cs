using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Backend.Library;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.SpotifyLive;
using Wavee.SpotifyLive.Hydration;
using Xunit;

namespace Wavee.Tests;

// ── THE online-read seam (hydration façade design §2.7) ──────────────────────────────────────────────────────────────
// Search / suggest / suggest-rich / home used to be four nullable `Live*` hooks the live bootstrap poked onto
// StoreLibrarySource, so every read carried its own "is the session up?" branch and logging out left all four pointing
// at a dead session. They are now ONE IOnlineCatalog the source takes in its ctor: OfflineOnlineCatalog until go-live,
// SpotifyOnlineCatalog after it, back to offline on logout — one switch, symmetric.
//
// These cases pin BOTH halves: what an offline answer must make the source do (the behaviour the absent hooks used to
// produce, verbatim), and what the Spotify arm puts on the wire.
public class OnlineCatalogTests
{
    static SwitchableEntityHydrator Offline(IStore store) => new(new OfflineEntityHydrator(store));

    static StoreLibrarySource Source(IStore store, IOnlineCatalog online)
        => new(store, Offline(store), online);

    static Track Trk(string id, string title, string artist = "Someone")
        => new(id, "spotify:track:" + id, title, [new ArtistRef("ar1", "spotify:artist:ar1", artist)],
            new AlbumRef("al1", "spotify:album:al1", "Album"), 1000, false, null);

    // ── the offline answers ─────────────────────────────────────────────────────────────────────────────────────────

    // search → null ⇒ the caller uses ITS OWN index. That is the whole contract: OfflineOnlineCatalog does not "search
    // and find nothing", it declines, and StoreLibrarySource then scans the store exactly as it did with a null hook.
    [Fact]
    public async Task OfflineCatalog_Search_Declines_SoTheSourceScansItsStoreIndex()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("t1", "Blue Monday"));
        store.SetSaved("liked", "spotify:track:t1", true, SyncState.Confirmed);
        using var src = Source(store, OfflineOnlineCatalog.Instance);

        var results = await src.SearchAsync("blue");

        Assert.Equal("spotify:track:t1", Assert.Single(results.Tracks).Uri);
        Assert.Empty(results.Albums);
    }

    // …and the facet gate is unchanged: only All/Tracks read the offline track index.
    [Fact]
    public async Task OfflineCatalog_Search_NonTrackFacet_IsEmpty()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("t1", "Blue Monday"));
        using var src = Source(store, OfflineOnlineCatalog.Instance);

        Assert.Empty((await src.SearchAsync("blue", SearchFacet.Albums, 0, 30)).Tracks);
    }

    [Fact]
    public async Task OfflineCatalog_Suggestions_AreEmpty_InBothShapes()
    {
        using var src = Source(new InMemoryStore(), OfflineOnlineCatalog.Instance);

        Assert.Empty(await src.SuggestAsync("blue"));
        Assert.Same(SearchSuggestions.Empty, await src.SuggestRichAsync("blue"));
    }

    [Fact]
    public async Task OfflineCatalog_Home_IsNull_MeaningNoLiveFeed()
    {
        Assert.Null(await OfflineOnlineCatalog.Instance.GetHomeAsync(CancellationToken.None));
        Assert.Null(await OfflineOnlineCatalog.Instance.SearchAsync("q", SearchFacet.All, 0, 30, CancellationToken.None));
    }

    // ── the live answers, through the source ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LiveCatalog_Search_ShortCircuitsTheStoreIndex()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("t1", "Blue Monday"));          // resident, and NOT what the answer must contain
        var online = new FakeCatalog
        {
            Search = (_, _, _, _) => new SearchResults([Trk("t9", "Online Only")], [], [], []),
        };
        using var src = Source(store, online);

        var results = await src.SearchAsync("blue");

        Assert.Equal("spotify:track:t9", Assert.Single(results.Tracks).Uri);
        Assert.Equal(1, online.SearchCalls);
    }

    // A LIVE catalog that fails is LOUD. Silently degrading to the (much smaller) offline index is exactly the failure
    // mode that makes "search suddenly only finds my library" impossible to diagnose — so the seam's null means "no
    // online catalog" and nothing else.
    [Fact]
    public async Task LiveCatalog_SearchFailure_Propagates()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("t1", "Blue Monday"));
        var online = new FakeCatalog { Search = (_, _, _, _) => throw new InvalidOperationException("pathfinder is down") };
        using var src = Source(store, online);

        await Assert.ThrowsAsync<InvalidOperationException>(() => src.SearchAsync("blue"));
    }

    // Suggestions are keystroke chrome: a failure degrades to "no suggestions" rather than an error under the omnibar.
    [Fact]
    public async Task LiveCatalog_SuggestFailure_DegradesToEmpty()
    {
        var online = new FakeCatalog { Suggest = _ => throw new InvalidOperationException("down") };
        using var src = Source(new InMemoryStore(), online);

        Assert.Empty(await src.SuggestAsync("bl"));
        Assert.Empty((await src.SuggestRichAsync("bl")).Queries);
    }

    // …but a SUPERSEDED keystroke is not a failure: cancellation still propagates, so the caller can tell "you typed
    // again" apart from "the server said no".
    [Fact]
    public async Task LiveCatalog_SuggestCancellation_Propagates()
    {
        var online = new FakeCatalog { Suggest = _ => throw new OperationCanceledException() };
        using var src = Source(new InMemoryStore(), online);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => src.SuggestRichAsync("bl"));
    }

    [Fact]
    public async Task EmptyQuery_NeverReachesTheSeam()
    {
        var online = new FakeCatalog();
        using var src = Source(new InMemoryStore(), online);

        Assert.Empty((await src.SearchAsync("   ")).Tracks);
        Assert.Empty(await src.SuggestAsync("  "));
        Assert.Empty((await src.SuggestRichAsync("")).Queries);
        Assert.Equal(0, online.SearchCalls);
        Assert.Equal(0, online.SuggestCalls);
    }

    // ── Home: the degraded-session library tail (ported from StoreLibrarySourceTests.GetHome_*) ─────────────────────
    // Offline (the seam answers null) or on a live fetch that fails, Home has nothing but the nine quick-pick tiles
    // unless the synced library re-contributes its three shelves. Online they must NOT be appended — that second
    // library tail is what the section-owned Home replaced.
    static InMemoryStore LibraryStore()
    {
        var store = new InMemoryStore();
        store.UpsertPlaylist(new Playlist("p1", "spotify:playlist:p1", "One", null, "Me", null, 0));
        store.SetRootlist(new[] { new RootlistEntry(0, 0, "spotify:playlist:p1", null, 0) });
        store.UpsertAlbum(new Album("a1", "spotify:album:a1", "Album1", null, [], 2020, 1));
        store.SetSaved("albums", "spotify:album:a1", true, SyncState.Confirmed);
        store.UpsertArtist(new Artist("ar1", "spotify:artist:ar1", "Artist1", null));
        store.SetSaved("artists", "spotify:artist:ar1", true, SyncState.Confirmed);
        return store;
    }

    static HomeGroup[] Shelves(HomeContribution home)
        => home.Groups.Where(g => g.Kind == HomeGroupKind.Shelf).ToArray();

    [Fact]
    public async Task GetHome_Offline_ContributesTheThreeLibraryShelves()
    {
        using var src = Source(LibraryStore(), OfflineOnlineCatalog.Instance);
        var home = await src.GetHomeAsync();

        Assert.Contains(home.Groups, g => g.Kind == HomeGroupKind.QuickGrid);
        var shelves = Shelves(home);
        Assert.Equal(3, shelves.Length);
        Assert.Equal(
            new[]
            {
                FluentGpu.Localization.Loc.Get(Strings.Home.YourPlaylists),
                FluentGpu.Localization.Loc.Get(Strings.Home.YourAlbums),
                FluentGpu.Localization.Loc.Get(Strings.Home.YourArtists),
            },
            shelves.Select(s => s.Title!).ToArray());
        Assert.Equal("spotify:playlist:p1", Assert.Single(shelves[0].Cards).Uri);
        Assert.Equal(HomeCardKind.Playlist, shelves[0].Cards[0].Kind);
        Assert.Equal("spotify:album:a1", Assert.Single(shelves[1].Cards).Uri);
        Assert.Equal(HomeCardKind.Album, shelves[1].Cards[0].Kind);
        Assert.Equal("spotify:artist:ar1", Assert.Single(shelves[2].Cards).Uri);
        Assert.Equal(HomeCardKind.Artist, shelves[2].Cards[0].Kind);
    }

    [Fact]
    public async Task GetHome_LiveFetchThrows_StillContributesTheLibraryShelves()
    {
        using var src = Source(LibraryStore(), new FakeCatalog { Home = () => throw new InvalidOperationException("pathfinder is down") });

        Assert.Equal(3, Shelves(await src.GetHomeAsync()).Length);
    }

    [Fact]
    public async Task GetHome_LiveModulesLanded_DoesNotAppendASecondLibraryTail()
    {
        var live = new HomeGroup(HomeGroupKind.MixBand, "Made for you",
            new[] { new HomeCard("spotify:playlist:mix", "Daily Mix 1", null, null, HomeCardKind.Playlist) });
        using var src = Source(LibraryStore(), new FakeCatalog { Home = () => new LiveHomeResult(new[] { live }, null) });

        var home = await src.GetHomeAsync();

        Assert.Empty(Shelves(home));
        Assert.Contains(home.Groups, g => g.Kind == HomeGroupKind.MixBand);
    }

    // The chip pin. A FACETED home response does not always repeat homeChips, so the last non-empty set is remembered —
    // otherwise selecting a facet drops the row that produced the selection and the feed stays filtered with no way out.
    [Fact]
    public async Task GetHome_FacetedResponseWithoutChips_KeepsTheLastChipRow()
    {
        var chips = new[] { new HomeChip("music-chip", "Music", System.Array.Empty<HomeChip>()) };
        var group = new HomeGroup(HomeGroupKind.MixBand, "Made for you",
            new[] { new HomeCard("spotify:playlist:mix", "Daily Mix 1", null, null, HomeCardKind.Playlist) });
        var online = new FakeCatalog();
        online.Home = () => new LiveHomeResult(new[] { group }, chips);
        using var src = Source(LibraryStore(), online);

        Assert.Same(chips, (await src.GetHomeAsync()).Chips);
        online.Home = () => new LiveHomeResult(new[] { group }, null);      // the faceted follow-up carries none
        Assert.Same(chips, (await src.GetHomeAsync()).Chips);
    }

    // …and "no live Home" is NOT a chip-less live response: an offline feed has no facets to filter, so the pinned row
    // must not survive a logout. (The absent hook produced exactly this; the seam must too.)
    [Fact]
    public async Task GetHome_AfterGoingOffline_DropsThePinnedChipRow()
    {
        var chips = new[] { new HomeChip("music-chip", "Music", System.Array.Empty<HomeChip>()) };
        var group = new HomeGroup(HomeGroupKind.MixBand, "Made for you",
            new[] { new HomeCard("spotify:playlist:mix", "Daily Mix 1", null, null, HomeCardKind.Playlist) });
        var seam = new SwitchableOnlineCatalog(new FakeCatalog { Home = () => new LiveHomeResult(new[] { group }, chips) });
        using var src = Source(LibraryStore(), seam);

        Assert.Same(chips, (await src.GetHomeAsync()).Chips);
        seam.Reset();                                        // logout
        var offline = await src.GetHomeAsync();
        Assert.Null(offline.Chips);
        Assert.Equal(3, Shelves(offline).Length);            // …and the degraded shelves are back
    }

    // ── the switchable itself ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Switchable_StartsOffline_SwapsIn_AndResets()
    {
        var seam = new SwitchableOnlineCatalog();
        Assert.Same(OfflineOnlineCatalog.Instance, seam.Inner);
        Assert.Null(await seam.SearchAsync("q", SearchFacet.All, 0, 30));

        var live = new FakeCatalog { Search = (_, _, _, _) => new SearchResults([], [], [], []) };
        seam.SetInner(live);
        Assert.NotNull(await seam.SearchAsync("q", SearchFacet.All, 0, 30));

        seam.Reset();
        Assert.Same(OfflineOnlineCatalog.Instance, seam.Inner);
        Assert.Null(await seam.SearchAsync("q", SearchFacet.All, 0, 30));
    }

    [Fact]
    public void Switchable_RefusesANullInner()
    {
        var seam = new SwitchableOnlineCatalog();
        Assert.Throws<ArgumentNullException>(() => seam.SetInner(null!));
        Assert.Throws<ArgumentNullException>(() => new SwitchableOnlineCatalog(null!));
    }

    [Fact]
    public void StoreLibrarySource_RefusesANullSeam()
    {
        // Every seam this source reads through is REQUIRED (wiring-discipline): the hydration facade and the online
        // catalog. (The owner/added-by overlay is not a seam at all any more - it is IStore.GetOwner.)
        Assert.Throws<ArgumentNullException>(() =>
            new StoreLibrarySource(new InMemoryStore(), Offline(new InMemoryStore()), null!));
        Assert.Throws<ArgumentNullException>(() =>
            new StoreLibrarySource(new InMemoryStore(), null!, OfflineOnlineCatalog.Instance));
    }

    // ── the Spotify arm, on the wire ────────────────────────────────────────────────────────────────────────────────

    const string TracksResponse = """
    { "data": { "searchV2": { "tracksV2": { "totalCount": 42, "items": [
        { "item": { "data": {
            "uri": "spotify:track:t1", "name": "Blue Monday",
            "duration": { "totalMilliseconds": 450000 },
            "artists": { "items": [ { "uri": "spotify:artist:ar1", "profile": { "name": "New Order" } } ] },
            "albumOfTrack": { "uri": "spotify:album:al1", "name": "Substance", "coverArt": { "sources": [] } }
        } } }
    ] } } } }
    """;

    sealed class Wire
    {
        public List<JsonDocument> Bodies { get; } = new();
        public FakeExchange Exchange { get; }
        public int Calls => Exchange.Calls;

        public Wire(string response)
            => Exchange = new FakeExchange((req, _) =>
            {
                if (req.Body is { } body) Bodies.Add(JsonDocument.Parse(body));
                return new HttpResp(200, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    Encoding.UTF8.GetBytes(response));
            });

        public JsonElement Body(int i) => Bodies[i].RootElement;
        public string Op(int i) => Body(i).GetProperty("operationName").GetString()!;
        public JsonElement Vars(int i) => Body(i).GetProperty("variables");
    }

    static SpotifyOnlineCatalog Catalog(Wire wire, IStore store, IEntityHydrator hydrator, Func<string?>? facet = null)
    {
        var client = new PathfinderClient(wire.Exchange);
        var resource = new PathfinderResource(client, static () =>
            new SessionContext("me", "US", "premium", "en", Tier.Premium, false));
        return new SpotifyOnlineCatalog(client, resource, store, hydrator,
            facet ?? (static () => null),
            static () => new HomeModuleTitles("Jump back in", "Recents", "Made for you", "Top mixes", "Radio",
                "Up next", "Audiobooks", "Editor's picks", "Because you listened", "Podcasts"),
            static (_, _) => Task.CompletedTask,
            static (_, _) => Task.FromResult<byte[]?>(null));
    }

    // The per-facet op, its captured variable shape, AND the trait post-step. Online search rows are transient mapper
    // output (never store joins), so play-time correctness depends on those traits being warmed at read time.
    [Fact]
    public async Task SpotifySearch_TracksFacet_SendsSearchTracks_AndWarmsTheRowsTraits()
    {
        var store = new InMemoryStore();
        var hydrator = new RecordingHydrator(store);
        var wire = new Wire(TracksResponse);
        using var catalog = Catalog(wire, store, hydrator);

        var results = await catalog.SearchAsync("blue monday", SearchFacet.Tracks, 20, 10);

        Assert.Equal(1, wire.Calls);
        Assert.Equal(PathfinderOps.SearchTracks, wire.Op(0));
        var vars = wire.Vars(0);
        Assert.Equal("blue monday", vars.GetProperty("searchTerm").GetString());   // NOT "query" — the captured shape
        Assert.Equal(20, vars.GetProperty("offset").GetInt32());
        Assert.Equal(10, vars.GetProperty("limit").GetInt32());
        Assert.False(vars.GetProperty("includePreReleases").GetBoolean());          // true ONLY for audiobooks
        Assert.Equal("spotify:track:t1", Assert.Single(results!.Tracks).Uri);
        Assert.Equal(42, results.TracksTotal);

        var (uris, surface) = Assert.Single(hydrator.TraitCalls);
        Assert.Equal(new[] { "spotify:track:t1" }, uris.ToArray());
        Assert.Equal(TraitSurface.Search, surface);
    }

    // The "All" tab is a DIFFERENT operation with a DIFFERENT variable set, keyed on "query".
    [Fact]
    public async Task SpotifySearch_AllFacet_SendsTopResults_KeyedOnQuery()
    {
        var store = new InMemoryStore();
        var wire = new Wire(TracksResponse);
        using var catalog = Catalog(wire, store, new RecordingHydrator(store));

        await catalog.SearchAsync("blue", SearchFacet.All, 0, 30);

        Assert.Equal(PathfinderOps.SearchTopResults, wire.Op(0));
        var vars = wire.Vars(0);
        Assert.Equal("blue", vars.GetProperty("query").GetString());
        Assert.False(vars.TryGetProperty("searchTerm", out _));
        Assert.Equal(2, vars.GetProperty("sectionFilters").GetArrayLength());
        Assert.Equal(50, vars.GetProperty("numberOfTopResults").GetInt32());   // desktop pins 50, not the caller limit
        Assert.False(vars.GetProperty("includeAlbumPreReleases").GetBoolean());
    }

    // Audiobooks is the ONE facet whose op sends includePreReleases:true (wire-verified).
    [Fact]
    public async Task SpotifySearch_AudiobooksFacet_IsTheOnlyOneSendingIncludePreReleases()
    {
        var store = new InMemoryStore();
        var wire = new Wire(TracksResponse);
        using var catalog = Catalog(wire, store, new RecordingHydrator(store));

        await catalog.SearchAsync("dune", SearchFacet.Audiobooks, 0, 30);

        Assert.Equal(PathfinderOps.SearchAudiobooks, wire.Op(0));
        Assert.True(wire.Vars(0).GetProperty("includePreReleases").GetBoolean());
    }

    [Fact]
    public async Task SpotifySearch_GenresFacet_UsesSearchTerm()
    {
        var store = new InMemoryStore();
        var wire = new Wire("""{ "data": { "searchV2": { "genres": { "totalCount": 2, "items": [] } } } }""");
        using var catalog = Catalog(wire, store, new RecordingHydrator(store));

        await catalog.SearchAsync("sleep", SearchFacet.Genres, 0, 30);

        Assert.Equal(PathfinderOps.SearchGenres, wire.Op(0));
        var vars = wire.Vars(0);
        Assert.Equal("sleep", vars.GetProperty("searchTerm").GetString());
        Assert.False(vars.GetProperty("includeAlbumPreReleases").GetBoolean());
        Assert.Equal(20, vars.GetProperty("numberOfTopResults").GetInt32());
    }

    // A row-less answer warms nothing — no empty trait pass per keystroke.
    [Fact]
    public async Task SpotifySearch_NoTracks_WarmsNoTraits()
    {
        var store = new InMemoryStore();
        var hydrator = new RecordingHydrator(store);
        var wire = new Wire("""{ "data": { "searchV2": { } } }""");
        using var catalog = Catalog(wire, store, hydrator);

        await catalog.SearchAsync("nothing", SearchFacet.Tracks, 0, 30);

        Assert.Empty(hydrator.TraitCalls);
    }

    // A failed op THROWS rather than returning null: null is reserved for "no online catalog" (see the seam's contract).
    [Fact]
    public async Task SpotifySearch_TransportFailure_Throws()
    {
        var store = new InMemoryStore();
        var http = new FakeExchange((_, _) => new HttpResp(500,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), Array.Empty<byte>()));
        var client = new PathfinderClient(http);
        var resource = new PathfinderResource(client, static () =>
            new SessionContext("me", "US", "premium", "en", Tier.Premium, false));
        using var catalog = new SpotifyOnlineCatalog(client, resource, store, new RecordingHydrator(store),
            static () => null,
            static () => new HomeModuleTitles("", "", "", "", "", "", "", "", "", ""),
            static (_, _) => Task.CompletedTask, static (_, _) => Task.FromResult<byte[]?>(null));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => catalog.SearchAsync("q", SearchFacet.Tracks, 0, 30));
    }

    const string SuggestResponse = """
    { "data": { "searchV2": { "querySuggestions": { "items": [ { "suggestion": "blue monday" } ] } } } }
    """;

    // Both suggest shapes ride ONE searchSuggestions op — the plain shape is the rich one's Queries, never a second call.
    [Fact]
    public async Task SpotifySuggest_BothShapes_RideTheSameOp()
    {
        var store = new InMemoryStore();
        var wire = new Wire(SuggestResponse);
        using var catalog = Catalog(wire, store, new RecordingHydrator(store));

        await catalog.SuggestAsync("blue");
        await catalog.SuggestRichAsync("blue");

        Assert.Equal(2, wire.Calls);                            // one op per CALL (no cross-call cache on the client)
        Assert.Equal(PathfinderOps.SearchSuggestions, wire.Op(0));
        Assert.Equal(PathfinderOps.SearchSuggestions, wire.Op(1));
        Assert.Equal("blue", wire.Vars(0).GetProperty("query").GetString());
        Assert.False(wire.Vars(0).GetProperty("includeAlbumPreReleases").GetBoolean());
        Assert.Equal(30, wire.Vars(0).GetProperty("numberOfTopResults").GetInt32());
    }

    const string HomeResponse = """{ "data": { "home": { "greeting": { "transformedLabel": "Good evening" } } } }""";

    // Home rides the DESKTOP integration with the real local zone, and the facet is read at FETCH time — the chip row
    // writes it between reads, and PathfinderResource keys its TTL cache on the request body, so a facet switch must be
    // a distinct cache entry rather than a stale hit.
    [Fact]
    public async Task SpotifyHome_ReadsTheFacetAtFetchTime()
    {
        var store = new InMemoryStore();
        string? facet = null;
        var wire = new Wire(HomeResponse);
        using var catalog = Catalog(wire, store, new RecordingHydrator(store), () => facet);

        var first = await catalog.GetHomeAsync();
        Assert.NotNull(first);
        Assert.Equal("Good evening", first!.Greeting);
        Assert.Equal(PathfinderOps.Home, wire.Op(0));
        var vars = wire.Vars(0);
        Assert.Equal("INTEGRATION_DESKTOP", vars.GetProperty("homeEndUserIntegration").GetString());
        Assert.Equal("", vars.GetProperty("facet").GetString());
        Assert.Equal(SpotifyTimeZone.LocalIana, vars.GetProperty("timeZone").GetString());

        await catalog.GetHomeAsync();
        Assert.Equal(1, wire.Calls);                       // same body ⇒ the resource's TTL cache answers

        facet = "podcasts-following-chip";
        await catalog.GetHomeAsync();
        Assert.Equal(2, wire.Calls);                       // a facet switch is a DIFFERENT key, not a stale hit
        Assert.Equal("podcasts-following-chip", wire.Vars(1).GetProperty("facet").GetString());
    }

    // ── fakes ───────────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class FakeCatalog : IOnlineCatalog
    {
        public Func<string, SearchFacet, int, int, SearchResults?>? Search { get; set; }
        public Func<string, SearchSuggestions>? Suggest { get; set; }
        public Func<LiveHomeResult?>? Home { get; set; }
        public int SearchCalls { get; private set; }
        public int SuggestCalls { get; private set; }
        public int HomeCalls { get; private set; }

        public Task<SearchResults?> SearchAsync(string query, SearchFacet facet, int offset, int limit, CancellationToken ct = default)
        {
            SearchCalls++;
            return Task.FromResult(Search is null ? null : Search(query, facet, offset, limit));
        }

        public async Task<IReadOnlyList<string>> SuggestAsync(string query, CancellationToken ct = default)
            => (await SuggestRichAsync(query, ct).ConfigureAwait(false)).Queries;

        public Task<SearchSuggestions> SuggestRichAsync(string query, CancellationToken ct = default)
        {
            SuggestCalls++;
            return Task.FromResult(Suggest is null ? SearchSuggestions.Empty : Suggest(query));
        }

        public Task<LiveHomeResult?> GetHomeAsync(CancellationToken ct = default)
        {
            HomeCalls++;
            return Task.FromResult(Home is null ? null : Home());
        }
    }
}
