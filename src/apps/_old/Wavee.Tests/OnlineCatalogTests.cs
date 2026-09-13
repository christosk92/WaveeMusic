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

    // Offline is a DECLINE, not a failure: there is no catalog to ask, so the answer is empty and the omnibar says
    // "No results found". A catalog that exists and cannot answer is the other case (LiveCatalog_SuggestRichFailure_*).
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
        Assert.Null(await OfflineOnlineCatalog.Instance.GetHomeAsync(null, CancellationToken.None));
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

    // The omnibar's rich suggestions are LOUD like search: a failure propagates so the popup can say "Couldn't load
    // suggestions" with a retry, instead of the "No results found" that a silent Empty used to produce for every
    // transport hiccup (OmnibarSuggestQuery.Fail is the consumer).
    [Fact]
    public async Task LiveCatalog_SuggestRichFailure_Propagates()
    {
        var online = new FakeCatalog { Suggest = _ => throw new InvalidOperationException("down") };
        using var src = Source(new InMemoryStore(), online);

        await Assert.ThrowsAsync<InvalidOperationException>(() => src.SuggestRichAsync("bl"));
    }

    // The plain-completion shape has no popup to speak through (LiveSessionHost's startup probe is its one caller):
    // it still degrades to "no suggestions".
    [Fact]
    public async Task LiveCatalog_PlainSuggestFailure_DegradesToEmpty()
    {
        var online = new FakeCatalog { Suggest = _ => throw new InvalidOperationException("down") };
        using var src = Source(new InMemoryStore(), online);

        Assert.Empty(await src.SuggestAsync("bl"));
    }

    // …and on the wire, PathfinderClient is where "no body" becomes a typed failure. The optional read (QueryAsync)
    // keeps answering null for the callers whose null branch is a legitimate skip; the required read the catalog's
    // search + suggest paths use throws a PathfinderRequestException that names the operation and the status — so a
    // 2xx document with nothing in it (an ANSWER) can never be confused with a 503 (a FAILURE).
    [Fact]
    public async Task PathfinderClient_NonSuccessStatus_IsATypedFailure_NotAnEmptyAnswer()
    {
        var pf = new PathfinderClient(new StatusExchange(503));

        var ex = await Assert.ThrowsAsync<PathfinderRequestException>(
            () => pf.QueryOrThrowAsync(PathfinderOps.SearchSuggestions, PathfinderOps.SearchSuggestionsHash, null));

        Assert.Equal(PathfinderOps.SearchSuggestions, ex.Operation);
        Assert.Equal(503, ex.HttpStatus);
        Assert.Null(await pf.QueryAsync(PathfinderOps.SearchSuggestions, PathfinderOps.SearchSuggestionsHash, null));
    }

    [Fact]
    public async Task PathfinderClient_TransportException_IsATypedFailure_WithTheCause()
    {
        var pf = new PathfinderClient(new StatusExchange(new System.Net.Http.HttpRequestException("socket reset")));

        var ex = await Assert.ThrowsAsync<PathfinderRequestException>(
            () => pf.QueryOrThrowAsync(PathfinderOps.SearchSuggestions, PathfinderOps.SearchSuggestionsHash, null));

        Assert.Null(ex.HttpStatus);
        Assert.IsType<System.Net.Http.HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task PathfinderClient_EmptyDocument_IsAnAnswer()
    {
        var pf = new PathfinderClient(new StatusExchange(200, "{\"data\":{}}"));

        using var doc = await pf.QueryOrThrowAsync(PathfinderOps.SearchSuggestions, PathfinderOps.SearchSuggestionsHash, null);

        Assert.True(doc.RootElement.TryGetProperty("data", out _));
    }

    [Fact]
    public async Task PathfinderClient_Cancellation_IsNeitherAnAnswerNorAFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var pf = new PathfinderClient(new StatusExchange(200, "{}"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pf.QueryOrThrowAsync(PathfinderOps.SearchSuggestions, PathfinderOps.SearchSuggestionsHash, null, ct: cts.Token));
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
        var home = await src.GetHomeAsync(null);

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

        Assert.Equal(3, Shelves(await src.GetHomeAsync(null)).Length);
    }

    [Fact]
    public async Task GetHome_LiveModulesLanded_DoesNotAppendASecondLibraryTail()
    {
        var live = new HomeGroup(HomeGroupKind.MixBand, "Made for you",
            new[] { new HomeCard("spotify:playlist:mix", "Daily Mix 1", null, null, HomeCardKind.Playlist) });
        using var src = Source(LibraryStore(), new FakeCatalog { Home = () => new LiveHomeResult(new[] { live }, null) });

        var home = await src.GetHomeAsync(null);

        Assert.Empty(Shelves(home));
        Assert.Contains(home.Groups, g => g.Kind == HomeGroupKind.MixBand);
    }

    // ── Home: a FACET is a different document ───────────────────────────────────────────────────────────────────────
    // The facet arrives as a request PARAMETER, so the source knows a read is faceted and can act on it. It acts on it
    // twice: the personal quick matrix is the unfiltered feed's first module and is not re-injected under a chip (that
    // is why "Music" used to look like "All" at the fold), and a faceted read that cannot be answered LIVE fails loud
    // instead of degrading to the library shelves — shelves under "Podcasts" are a lie, not a fallback.
    [Fact]
    public async Task GetHome_Faceted_DoesNotPrependJumpBackIn()
    {
        var live = new HomeGroup(HomeGroupKind.MixBand, "Made for you",
            new[] { new HomeCard("spotify:playlist:mix", "Daily Mix 1", null, null, HomeCardKind.Playlist) });
        var online = new FakeCatalog { Home = () => new LiveHomeResult(new[] { live }, null, Facet: "music-chip") };
        using var src = Source(LibraryStore(), online);

        var home = await src.GetHomeAsync("music-chip");

        Assert.Equal("music-chip", online.LastFacet);       // the source was TOLD, not left to guess
        Assert.DoesNotContain(home.Groups, g => g.Kind == HomeGroupKind.QuickGrid);
        Assert.Empty(Shelves(home));
        Assert.Contains(home.Groups, g => g.Kind == HomeGroupKind.MixBand);
    }

    [Fact]
    public async Task GetHome_Unfaceted_StillPrependsJumpBackIn()
    {
        var live = new HomeGroup(HomeGroupKind.MixBand, "Made for you",
            new[] { new HomeCard("spotify:playlist:mix", "Daily Mix 1", null, null, HomeCardKind.Playlist) });
        var online = new FakeCatalog { Home = () => new LiveHomeResult(new[] { live }, null) };
        using var src = Source(LibraryStore(), online);

        var home = await src.GetHomeAsync(null);

        Assert.Null(online.LastFacet);
        Assert.Equal(HomeGroupKind.QuickGrid, home.Groups[0].Kind);
    }

    [Fact]
    public async Task GetHome_FacetedLiveFailure_Throws()
    {
        using var src = Source(LibraryStore(),
            new FakeCatalog { Home = () => throw new InvalidOperationException("pathfinder is down") });

        await Assert.ThrowsAsync<InvalidOperationException>(() => src.GetHomeAsync("music-chip"));
    }

    // …and "offline" is the same absence by a quieter route: there is no locally computable "Podcasts" either.
    [Fact]
    public async Task GetHome_FacetedOffline_Throws()
    {
        using var src = Source(LibraryStore(), OfflineOnlineCatalog.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => src.GetHomeAsync("podcasts-chip"));
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

        Assert.Same(chips, (await src.GetHomeAsync(null)).Chips);
        online.Home = () => new LiveHomeResult(new[] { group }, null);      // the faceted follow-up carries none
        Assert.Same(chips, (await src.GetHomeAsync(null)).Chips);
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

        Assert.Same(chips, (await src.GetHomeAsync(null)).Chips);
        seam.Reset();                                        // logout
        var offline = await src.GetHomeAsync(null);
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

    static SpotifyOnlineCatalog Catalog(Wire wire, IStore store, IEntityHydrator hydrator)
    {
        var client = new PathfinderClient(wire.Exchange);
        var resource = new PathfinderResource(client, static () =>
            new SessionContext("me", "US", "premium", "en", Tier.Premium, false));
        return new SpotifyOnlineCatalog(client, resource, store, hydrator,
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
            static () => new HomeModuleTitles("", "", "", "", "", "", "", "", "", ""),
            static (_, _) => Task.CompletedTask, static (_, _) => Task.FromResult<byte[]?>(null));

        await Assert.ThrowsAsync<PathfinderRequestException>(
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

    // Home rides the DESKTOP integration with the real local zone, and the facet is a REQUEST PARAMETER: the caller
    // names the document it wants, the variable goes on the wire, and the ANSWER carries the facet back so a late reply
    // can be matched against the current chip selection. PathfinderResource keys its TTL cache on the request body, so
    // a facet switch is a distinct cache entry rather than a stale hit.
    [Fact]
    public async Task SpotifyHome_FacetIsARequestParameter()
    {
        var store = new InMemoryStore();
        var wire = new Wire(HomeResponse);
        using var catalog = Catalog(wire, store, new RecordingHydrator(store));

        var first = await catalog.GetHomeAsync(null);
        Assert.NotNull(first);
        Assert.Equal("Good evening", first!.Greeting);
        Assert.Equal("", first.Facet);                     // null asked for the unfiltered feed; the answer says so
        Assert.Equal(PathfinderOps.Home, wire.Op(0));
        var vars = wire.Vars(0);
        Assert.Equal("INTEGRATION_DESKTOP", vars.GetProperty("homeEndUserIntegration").GetString());
        Assert.Equal("", vars.GetProperty("facet").GetString());
        Assert.Equal(SpotifyTimeZone.LocalIana, vars.GetProperty("timeZone").GetString());

        await catalog.GetHomeAsync(null);
        Assert.Equal(1, wire.Calls);                       // same body ⇒ the resource's TTL cache answers

        var faceted = await catalog.GetHomeAsync("podcasts-following-chip");
        Assert.Equal(2, wire.Calls);                       // a facet switch is a DIFFERENT key, not a stale hit
        Assert.Equal("podcasts-following-chip", wire.Vars(1).GetProperty("facet").GetString());
        Assert.Equal("podcasts-following-chip", faceted!.Facet);
    }

    // ── fakes ───────────────────────────────────────────────────────────────────────────────────────────────────────

    // One scripted answer for every request: a status (+ body), or a transport exception.
    sealed class StatusExchange : IHttpExchange
    {
        readonly int _status;
        readonly string _body;
        readonly Exception? _throw;

        public StatusExchange(int status, string body = "") { _status = status; _body = body; }
        public StatusExchange(Exception ex) { _throw = ex; _body = ""; }

        public Task<HttpResp> SendAsync(HttpReq req, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_throw is not null) throw _throw;
            return Task.FromResult(new HttpResp(_status,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), Encoding.UTF8.GetBytes(_body)));
        }
    }

    sealed class FakeCatalog : IOnlineCatalog
    {
        public Func<string, SearchFacet, int, int, SearchResults?>? Search { get; set; }
        public Func<string, SearchSuggestions>? Suggest { get; set; }
        public Func<LiveHomeResult?>? Home { get; set; }
        public int SearchCalls { get; private set; }
        public int SuggestCalls { get; private set; }
        public int HomeCalls { get; private set; }
        /// <summary>The facet of the last Home read — null until asked, and null again for an unfiltered read.</summary>
        public string? LastFacet { get; private set; }

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

        public Task<LiveHomeResult?> GetHomeAsync(string? facet, CancellationToken ct = default)
        {
            HomeCalls++;
            LastFacet = facet;
            return Task.FromResult(Home is null ? null : Home());
        }
    }
}
