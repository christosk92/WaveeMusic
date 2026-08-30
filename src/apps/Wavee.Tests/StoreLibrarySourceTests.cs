using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Backend.Library;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The bridge: a catalog source that reads the persistent Store (membership sets × shared entities), joining at read.
public class StoreLibrarySourceTests
{
    static Track Trk(string id) => new(id, "spotify:track:" + id, "T" + id, [], new AlbumRef("", "", ""), 1000, false, null);

    // Every read on this source now goes through THE hydration facade (design 3): it ensures a rung, then reads the
    // store. A unit test's backend is genuinely offline, so the default inner is the real OfflineEntityHydrator --
    // store-only, never networks, never throws. A test that cares about WHAT was asked for passes a RecordingHydrator.
    static SwitchableEntityHydrator Offline(IStore store) => new(new Wavee.Backend.Hydration.OfflineEntityHydrator(store));
    static SwitchableEntityHydrator Recording(RecordingHydrator rec) => new(rec);

    [Fact]
    public async Task GetAlbums_JoinsSavedSetWithStore_SkippingUnhydrated()
    {
        var store = new InMemoryStore();
        store.UpsertAlbum(new Album("a1", "spotify:album:a1", "Album1", null, [], 2020, 1));
        store.SetSaved("albums", "spotify:album:a1", true, SyncState.Confirmed);
        store.SetSaved("albums", "spotify:album:missing", true, SyncState.Confirmed);   // not hydrated → skipped
        var src = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);
        Assert.Equal("Album1", Assert.Single(await src.GetAlbumsAsync()).Name);
    }

    [Fact]
    public async Task GetLikedSongs_JoinsSavedTracks()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("t1"));
        store.SetSaved("liked", "spotify:track:t1", true, SyncState.Confirmed);
        var src = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);
        Assert.Equal("Tt1", Assert.Single(await src.GetLikedSongsAsync()).Title);
    }

    // Replaces GetLikedSongs_FiresTheVideoDetectHook_WithTheJoinedUris + _EmptyCollection_DoesNotFireTheDetectHook.
    // Liked is a COLLECTION, so the ask is addressed by its uri, not derived from whatever the join happened to
    // resolve: one background Open on spotify:collection:tracks replaces the two fire-and-forget hooks (paged member
    // hydrate + video/adornment detect) this read used to fan out. CollectionHydration pages the members and asks for
    // the LikedSongs trait bundle itself (design 2.3).
    [Fact]
    public async Task GetLikedSongs_AsksTheFacadeForTheCollectionRung_InTheBackground()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("t1"));
        store.SetSaved("liked", "spotify:track:t1", true, SyncState.Confirmed);
        var rec = new RecordingHydrator(store);
        var src = new StoreLibrarySource(store, Recording(rec), OfflineOnlineCatalog.Instance);

        Assert.Single(await src.GetLikedSongsAsync());

        var (uris, level, surface) = Assert.Single(rec.Batches);
        Assert.Equal("spotify:collection:tracks", Assert.Single(uris));
        Assert.Equal(HydrationLevel.Open, level);
        Assert.Equal(TraitSurface.LikedSongs, surface);
        Assert.Equal(HydrationMode.Background, Assert.Single(rec.Options).Mode);   // a read never blocks on its members
    }

    // The regression this addresses-by-uri shape fixes: an EMPTY cache used to hydrate nothing, because the old hooks
    // were fed the joined (i.e. already-resident) rows. The collection ask does not depend on the join at all.
    [Fact]
    public async Task GetLikedSongs_EmptyCollection_StillAsksForTheCollectionRung()
    {
        var store = new InMemoryStore();
        var rec = new RecordingHydrator(store);
        var src = new StoreLibrarySource(store, Recording(rec), OfflineOnlineCatalog.Instance);
        Assert.Empty(await src.GetLikedSongsAsync());
        Assert.Equal("spotify:collection:tracks", Assert.Single(Assert.Single(rec.Batches).Uris));
    }

    [Fact]
    public async Task NoneReads_ReturnResidentDetailModels_WithoutAnyHydrationAsk()
    {
        const string playlistUri = "spotify:playlist:p";
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("t1"));
        store.UpsertPlaylist(new Playlist("p", playlistUri, "Resident playlist", null, "Me", null, 1));
        store.SetMembership(playlistUri,
            [new PlaylistMember("i1", "spotify:track:t1", null, 0)], null);
        store.SetSaved("liked", "spotify:track:t1", true, SyncState.Confirmed);
        store.UpsertAlbum(new Album("a1", "spotify:album:a1", "Resident album", null, [], 2020, 1));
        store.UpsertShow(new Show("s1", "spotify:show:s1", "Resident show", "Publisher", null));
        var rec = new RecordingHydrator(store);
        var src = new StoreLibrarySource(store, Recording(rec), OfflineOnlineCatalog.Instance);

        Assert.Equal("Resident playlist", (await src.GetPlaylistAsync(playlistUri, HydrationLevel.None))!.Name);
        Assert.Equal("Tt1", Assert.Single(await src.GetLikedSongsAsync(HydrationLevel.None)).Title);
        Assert.Equal("Resident album", (await src.GetAlbumAsync("spotify:album:a1", HydrationLevel.None))!.Name);
        Assert.Equal("Resident show", (await src.GetShowAsync("spotify:show:s1", HydrationLevel.None))!.Name);

        Assert.Empty(rec.Batches);
        Assert.Empty(rec.TraitCalls);
    }

    [Fact]
    public async Task GetShows_JoinsSavedShows()
    {
        var store = new InMemoryStore();
        store.UpsertShow(new Show("s1", "spotify:show:s1", "Show1", "Pub", null));
        store.SetSaved("shows", "spotify:show:s1", true, SyncState.Confirmed);
        var src = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);
        Assert.Equal("Show1", Assert.Single(await src.GetShowsAsync()).Name);
    }

    // ── the same background collection ask Liked already had (StoreLibrarySource.cs 4a) ──────────────────────────────
    // GetLikedSongsAsync fires a background Open on its own collection uri so members that never got named through
    // any OTHER surface still hydrate; GetAlbumsAsync/GetArtistsAsync/GetShowsAsync now do the same, on their own
    // saved-set uris (CollectionHydration.SetOf).
    [Fact]
    public async Task GetAlbumsAsync_AsksCollectionAlbumsSetInBackground()
    {
        var store = new InMemoryStore();
        store.UpsertAlbum(new Album("a1", "spotify:album:a1", "Album1", null, [], 2020, 1));
        store.SetSaved("albums", "spotify:album:a1", true, SyncState.Confirmed);
        var rec = new RecordingHydrator(store);
        var src = new StoreLibrarySource(store, Recording(rec), OfflineOnlineCatalog.Instance);

        Assert.Single(await src.GetAlbumsAsync());

        var (uris, level, _) = Assert.Single(rec.Batches);
        Assert.Equal("spotify:collection:albums", Assert.Single(uris));
        Assert.Equal(HydrationLevel.Open, level);
        Assert.Equal(HydrationMode.Background, Assert.Single(rec.Options).Mode);   // a read never blocks on its members
    }

    [Fact]
    public async Task GetArtistsAsync_AsksCollectionArtistsSetInBackground()
    {
        var store = new InMemoryStore();
        store.UpsertArtist(new Artist("ar1", "spotify:artist:ar1", "Artist1", null));
        store.SetSaved("artists", "spotify:artist:ar1", true, SyncState.Confirmed);
        var rec = new RecordingHydrator(store);
        var src = new StoreLibrarySource(store, Recording(rec), OfflineOnlineCatalog.Instance);

        Assert.Single(await src.GetArtistsAsync());

        var (uris, level, _) = Assert.Single(rec.Batches);
        Assert.Equal("spotify:collection:artists", Assert.Single(uris));
        Assert.Equal(HydrationLevel.Open, level);
        Assert.Equal(HydrationMode.Background, Assert.Single(rec.Options).Mode);
    }

    [Fact]
    public async Task GetShowsAsync_AsksCollectionShowsSetInBackground()
    {
        var store = new InMemoryStore();
        store.UpsertShow(new Show("s1", "spotify:show:s1", "Show1", "Pub", null));
        store.SetSaved("shows", "spotify:show:s1", true, SyncState.Confirmed);
        var rec = new RecordingHydrator(store);
        var src = new StoreLibrarySource(store, Recording(rec), OfflineOnlineCatalog.Instance);

        Assert.Single(await src.GetShowsAsync());

        var (uris, level, _) = Assert.Single(rec.Batches);
        Assert.Equal("spotify:collection:shows", Assert.Single(uris));
        Assert.Equal(HydrationLevel.Open, level);
        Assert.Equal(HydrationMode.Background, Assert.Single(rec.Options).Mode);
    }

    [Fact]
    public async Task GetPlaylist_JoinsMembershipWithTracks_AndStampsMembershipAddedAt()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("t1"));
        store.UpsertTrack(Trk("t2"));
        store.UpsertPlaylist(new Playlist("p", "spotify:playlist:p", "My Mix", null, "Me", null, 0));
        store.SetMembership("spotify:playlist:p", new[]
        {
            new PlaylistMember("i1", "spotify:track:t1", "alice", 1_700_000_000_000),
            new PlaylistMember("i2", "spotify:track:t2", null, 0),
        }, null);
        var src = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);
        var pl = await src.GetPlaylistAsync("spotify:playlist:p");

        Assert.NotNull(pl);
        Assert.Equal(2, pl!.Tracks!.Count);
        Assert.Equal("Tt1", pl.Tracks[0].Title);
        Assert.Equal("alice", pl.Tracks[0].AddedBy);     // added_by comes from the membership row, not the shared entity
        Assert.NotNull(pl.Tracks[0].AddedAt);
        Assert.Null(pl.Tracks[1].AddedAt);               // added_at 0 → unknown → null
        Assert.Equal(2, pl.TrackCount);
    }

    // A KNOWN baseline (including a valid EMPTY one, a freshly-created playlist) must not read as "missing" and pull a
    // blocking first fetch over the top of optimistic local edits. OpenPolicy is where that lives now: baseline =>
    // background revalidate; no baseline => blocking Open.
    [Fact]
    public async Task GetPlaylist_KnownEmptyBaseline_RevalidatesInTheBackground_NeverBlocking()
    {
        const string uri = "spotify:playlist:empty";
        var store = new InMemoryStore();
        store.UpsertPlaylist(new Playlist("empty", uri, "New playlist", null, "Me", null, 0));
        store.SetMembership(uri, Array.Empty<PlaylistMember>(), null);
        var rec = new RecordingHydrator(store);
        var src = new StoreLibrarySource(store, Recording(rec), OfflineOnlineCatalog.Instance);

        var playlist = await src.GetPlaylistAsync(uri);

        Assert.NotNull(playlist);
        Assert.Empty(playlist!.Tracks!);
        // Index the PLAYLIST ask: the same read also asks the User ladder for the header's owner (P4-C), which is a
        // different ladder on a different uri and says nothing about the playlist's open plan.
        int i = PlaylistAsk(rec, uri);
        Assert.Equal(HydrationMode.Background, rec.Options[i].Mode);
        Assert.True(rec.Options[i].Revalidate);
        Assert.Equal(HydrationLevel.Open, rec.Batches[i].Level);
    }

    /// <summary>The index of the batch that asked for <paramref name="uri"/> — exactly one, and it is not the owner
    /// ask that rides along with every playlist read.</summary>
    static int PlaylistAsk(RecordingHydrator rec, string uri)
    {
        int found = -1;
        for (int i = 0; i < rec.Batches.Count; i++)
            if (rec.Batches[i].Uris.Count == 1 && rec.Batches[i].Uris[0] == uri)
            {
                Assert.Equal(-1, found);   // one ask per open, never two
                found = i;
            }
        Assert.True(found >= 0, "the playlist itself was never asked for");
        return found;
    }

    // …and the inverse: with NO membership baseline there is nothing to paint, so the open blocks on Open.
    [Fact]
    public async Task GetPlaylist_NoBaseline_BlocksOnOpen()
    {
        const string uri = "spotify:playlist:cold";
        var store = new InMemoryStore();
        store.UpsertPlaylist(new Playlist("cold", uri, "Cold", null, "Me", null, 0));
        var rec = new RecordingHydrator(store);
        var src = new StoreLibrarySource(store, Recording(rec), OfflineOnlineCatalog.Instance);

        _ = await src.GetPlaylistAsync(uri);

        int i = PlaylistAsk(rec, uri);
        Assert.Equal(HydrationMode.Blocking, rec.Options[i].Mode);
        Assert.Equal(HydrationLevel.Open, rec.Batches[i].Level);
    }

    [Fact]
    public async Task GetPlaylist_WarmBaseline_ServesFromStoreWithoutRefetch()
    {
        const string uri = "spotify:playlist:warm";
        var store = new InMemoryStore();
        store.UpsertPlaylist(new Playlist("warm", uri, "Warm mix", null, "Spotify", new Image("https://img/cover"), 0));
        store.SetMembership(uri, System.Array.Empty<PlaylistMember>(), null);
        var src = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);

        using var read = new CancellationTokenSource();
        read.Cancel();
        // Even with an ALREADY-CANCELLED read token, a warm playlist is served from the store and never refetched.
        // (The old palette-hydration hook this test also covered is gone: cover colours are image-keyed in
        // CoverColorPlane and resolved by the art slot itself, so a playlist read no longer schedules colour work.)
        Assert.NotNull(await src.GetPlaylistAsync(uri, ct: read.Token));
    }

    [Fact]
    public async Task GetPlaylist_StampsContextUidFromItemId_WithoutPollutingSharedStore()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("t1"));
        store.UpsertPlaylist(new Playlist("p", "spotify:playlist:p", "My Mix", null, "Me", null, 0));
        store.SetMembership("spotify:playlist:p", new[] { new PlaylistMember("rowuid-1", "spotify:track:t1", null, 0) }, null);
        var src = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);
        var pl = await src.GetPlaylistAsync("spotify:playlist:p");

        Assert.Equal("rowuid-1", pl!.Tracks![0].ContextUid);          // per-row uid stamped from PlaylistMember.ItemId
        Assert.Null(store.GetTrack("spotify:track:t1")!.ContextUid);  // the SHARED stored entity is untouched (read-model only)
    }

    [Fact]
    public async Task GetPlaylist_StampsChartEntryFromMembership_WithoutPollutingSharedStore()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("t1"));
        store.UpsertPlaylist(new Playlist("p", "spotify:playlist:p", "Top Songs - Argentina", null, "spotify", null, 0));
        var chart = new ChartEntry(ChartEntryStatus.Up, 3, 4, 41545);
        store.SetMembership("spotify:playlist:p", new[] { new PlaylistMember("rowuid-1", "spotify:track:t1", null, 0, chart) }, null);
        var src = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);
        var pl = await src.GetPlaylistAsync("spotify:playlist:p");

        Assert.Equal(chart, pl!.Tracks![0].Chart);                    // per-row chart facts stamped from PlaylistMember.Chart
        Assert.Null(store.GetTrack("spotify:track:t1")!.Chart);       // the SHARED stored entity is untouched (read-model only)
    }

    [Fact]
    public async Task GetPlaylist_OverlaysResolvedOwnerAndCollaborators_WithoutChangingTrackAddedBy()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("t1"));
        store.UpsertTrack(Trk("t2"));
        store.UpsertTrack(Trk("t3"));
        store.UpsertPlaylist(new Playlist("p", "spotify:playlist:p", "My Mix", null, "owner_raw", null, 0,
            Capabilities: new PlaylistCapabilities(false, false, false, true, false)));
        store.SetMembership("spotify:playlist:p", new[]
        {
            new PlaylistMember("i1", "spotify:track:t1", "owner_raw", 1),
            new PlaylistMember("i2", "spotify:track:t2", "friend_raw", 2),
            new PlaylistMember("i3", "spotify:track:t3", "friend_raw", 3),
        }, null);
        store.UpsertOwner(new Owner("owner_raw", "Owner Display", new Image("https://img/owner")));
        store.UpsertOwner(new Owner("friend_raw", "Friend Display", new Image("https://img/friend")));
        var src = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);

        var pl = await src.GetPlaylistAsync("spotify:playlist:p");

        Assert.NotNull(pl);
        Assert.Equal("Owner Display", pl!.OwnerName);
        Assert.Equal("Owner Display", pl.Owner!.Name);
        Assert.Equal("https://img/owner", pl.Owner.Avatar!.Url);
        Assert.Equal(new[] { "Owner Display", "Friend Display" }, pl.Collaborators!.Select(o => o.Name).ToArray());
        Assert.Equal(new[] { "owner_raw", "friend_raw" }, pl.Collaborators.Select(o => o.Id).ToArray());
        Assert.Equal("owner_raw", pl.Tracks![0].AddedBy);
        Assert.Equal("friend_raw", pl.Tracks[1].AddedBy);
    }

    // The ASK half of the same contract: an owner with no resident row is requested through THE façade (the User
    // ladder, Identity, background), and an owner that is already resident costs no request at all. This is what
    // replaced IUserProfileService.Prefetch — one door, one ledger, no service-private in-flight map.
    [Fact]
    public async Task GetPlaylist_AsksTheFacadeForUnresolvedOwners_AndNotForResidentOnes()
    {
        const string uri = "spotify:playlist:owners";
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("t1"));
        store.UpsertTrack(Trk("t2"));
        store.UpsertPlaylist(new Playlist("p", uri, "My Mix", null, "owner_raw", null, 0));
        store.SetMembership(uri, new[]
        {
            new PlaylistMember("i1", "spotify:track:t1", "owner_raw", 1),
            new PlaylistMember("i2", "spotify:track:t2", "friend_raw", 2),
        }, null);
        store.UpsertOwner(new Owner("friend_raw", "Friend Display", null));   // already resident → never asked for
        var rec = new RecordingHydrator(store);
        var src = new StoreLibrarySource(store, Recording(rec), OfflineOnlineCatalog.Instance);

        _ = await src.GetPlaylistAsync(uri);

        var userAsks = rec.Batches
            .Where(b => b.Uris.Count > 0 && b.Uris[0].StartsWith("spotify:user:", System.StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(userAsks);
        Assert.All(userAsks, b => Assert.Equal(HydrationLevel.Identity, b.Level));
        Assert.All(userAsks, b => Assert.Equal(TraitSurface.UserProfiles, b.Surface));
        var askedUris = userAsks.SelectMany(b => b.Uris).ToHashSet();
        Assert.Contains("spotify:user:owner_raw", askedUris);
        Assert.DoesNotContain("spotify:user:friend_raw", askedUris);
        // …and never blocking: a byline is not worth holding a page open for.
        Assert.All(rec.Options.Where((_, i) => rec.Batches[i].Uris.Count > 0
                                               && rec.Batches[i].Uris[0].StartsWith("spotify:user:", System.StringComparison.Ordinal)),
                   o => Assert.Equal(HydrationMode.Background, o.Mode));
    }

    // The HeaderOf owner seed (Owner id+name = username, avatar null) renders the name immediately, and the resolved
    // profile still WINS — OverlayOwner returns Get(raw) ?? header.Owner, so the null-avatar seed never clobbers it.
    [Fact]
    public async Task GetPlaylist_SeededOwnerChip_IsOverriddenByResolvedProfile_NotClobbered()
    {
        const string uri = "spotify:playlist:seeded";
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("t1"));
        store.UpsertPlaylist(new Playlist("p", uri, "My Mix", null, "owner_raw", null, 0,
            Owner: new Owner("owner_raw", "owner_raw", null)));   // exactly what HeaderOf now seeds
        store.SetMembership(uri, new[] { new PlaylistMember("i1", "spotify:track:t1", "owner_raw", 1) }, null);
        // The overlay is the STORE now: an Owner row landing (UserHydration's write) is what upgrades the byline.
        var src = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);

        // before any profile resolves: the seed renders its name; avatar still null.
        var seeded = await src.GetPlaylistAsync(uri);
        Assert.NotNull(seeded!.Owner);
        Assert.Equal("owner_raw", seeded.Owner!.Name);
        Assert.Null(seeded.Owner.Avatar);

        // once the profile resolves, the overlay WINS — the seed does not clobber the resolved display name + avatar.
        store.UpsertOwner(new Owner("owner_raw", "Owner Display", new Image("https://img/owner")));
        var resolved = await src.GetPlaylistAsync(uri);
        Assert.Equal("Owner Display", resolved!.Owner!.Name);
        Assert.Equal("https://img/owner", resolved.Owner.Avatar!.Url);
        Assert.Equal("Owner Display", resolved.OwnerName);
    }

    [Fact]
    public async Task GetPlaylists_OverlaysResolvedOwnerName_ForSidebarAndHomeSummaries()
    {
        var store = new InMemoryStore();
        store.UpsertPlaylist(new Playlist("p1", "spotify:playlist:p1", "One", null, "owner_raw", null, 0));
        store.SetRootlist(new[] { new RootlistEntry(0, 0, "spotify:playlist:p1", null, 0) });
        store.UpsertOwner(new Owner("owner_raw", "Owner Display", null));
        var src = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);

        var pls = await src.GetPlaylistsAsync();

        Assert.Equal("Owner Display", Assert.Single(pls).OwnerName);
    }

    // A landed owner reaches the UI through the ORDINARY store change — and the read source writes NOTHING to make that
    // happen. The deleted shape was: subscribe to IUserProfileService.Changed, look the user up in a user→playlists
    // dependency map, and store.Bump() every playlist that referenced it — a read path writing to the store to fake a
    // change notification for data the store did not hold. What is asserted now is both halves: the owner's own uri is
    // the change, it invalidates the playlist collection, and NO playlist uri is bumped by this source.
    [Fact]
    public async Task ResolvedOwner_InvalidatesThePlaylistCollection_AndTheSourceBumpsNoPlaylist()
    {
        var store = new InMemoryStore();
        const string uri = "spotify:playlist:p";
        store.UpsertTrack(Trk("t1"));
        store.UpsertPlaylist(new Playlist("p", uri, "My Mix", null, "owner_raw", null, 0));
        store.SetMembership(uri, new[] { new PlaylistMember("i1", "spotify:track:t1", "friend_raw", 1) }, null);
        store.SetRootlist(new[] { new RootlistEntry(0, 0, uri, null, 0) });
        var src = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);
        _ = await src.GetPlaylistAsync(uri);
        _ = await src.GetPlaylistsAsync();

        int playlistBumps = 0;
        var ownerBumps = new List<string>();
        CollectionKind? collection = null;
        using var storeSub = store.Changes.Subscribe(new StoreObs(c =>
        {
            if (c.Uri == uri) playlistBumps++;
            if (c.Uri.StartsWith("spotify:user:", System.StringComparison.Ordinal)) ownerBumps.Add(c.Uri);
        }));
        using var collectionSub = src.CollectionsChanged.Subscribe(new Obs(k => collection = k));

        store.UpsertOwner(new Owner("friend_raw", "Friend Display", null));
        store.UpsertOwner(new Owner("owner_raw", "Owner Display", null));

        Assert.Equal(new[] { "spotify:user:friend_raw", "spotify:user:owner_raw" }, ownerBumps.ToArray());
        Assert.Equal(0, playlistBumps);                          // the read source is not a writer
        Assert.Equal(CollectionKind.Playlists, collection);      // …but the grid still learns to re-read
        // …and the re-read now sees the resolved name.
        Assert.Equal("Owner Display", (await src.GetPlaylistAsync(uri))!.OwnerName);
    }

    // Replaces GetArtist_MissingOrStubDiscography_Fetches_HydratedDoesNot. The "is this artist cold?" gate this source
    // used to own (no TopAlbums / stub names / facet totals > held) is now HydrationLevels.Of(Artist) -- pinned by
    // HydrationLevelsTests -- and the "don't re-run when fresh" half is the ledger's, pinned by HydrationLedgerTests.
    // What is left HERE, and all this source still owes, is: ask for the rung the caller named, THEN read the store.
    [Fact]
    public async Task GetArtist_AsksForTheCallersRung_ThenReadsTheStore()
    {
        const string uri = "spotify:artist:ar";
        var store = new InMemoryStore();
        var rec = new RecordingHydrator(store)
        {
            OnEnsureMany = _ => store.UpsertArtist(new Artist("ar", uri, "Billie", null,
                new[] { new Album("al", "spotify:album:al", "HIT ME HARD AND SOFT", null, Array.Empty<ArtistRef>(), 2024, 10) })),
        };
        var src = new StoreLibrarySource(store, Recording(rec), OfflineOnlineCatalog.Instance);

        var rich = await src.GetArtistAsync(uri, HydrationLevel.Rich);

        Assert.Equal("HIT ME HARD AND SOFT", rich!.TopAlbums![0].Name);
        var (uris, level, surface) = Assert.Single(rec.Batches);
        Assert.Equal(uri, Assert.Single(uris));
        Assert.Equal(HydrationLevel.Rich, level);
        Assert.Equal(TraitSurface.None, surface);   // an artist's traits ride its chart step, not its page open
    }

    // TopTracks is a denormalized snapshot ArtistHydration's chart pass writes on its own ~12h cadence
    // (StoreLibrarySource.cs 4b); a Track row healed by ANY other surface in between (TrackV4 identity, traits) would
    // otherwise not reach ArtistPopular until that stamp expires. GetArtistAsync now re-joins each TopTracks row
    // against the store at read — same shape as AlbumHydration.RebuildTracklists — and never writes the healed copy
    // back (the chart pass still owns the stored shape).
    [Fact]
    public async Task GetArtistAsync_TopTracksReJoinedFromStore()
    {
        const string artistUri = "spotify:artist:ar1";
        var store = new InMemoryStore();
        var staleTop = new Track("t1", "spotify:track:t1", "", [], new AlbumRef("", "", ""), 1000, false, null);
        store.UpsertArtist(new Artist("ar1", artistUri, "Artist1", null, TopTracks: new[] { staleTop }));
        store.UpsertTrack(Trk("t1"));   // healed elsewhere: same uri, a named row
        var src = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);

        var artist = await src.GetArtistAsync(artistUri, HydrationLevel.None);

        Assert.Equal("Tt1", artist!.TopTracks![0].Title);                          // re-joined from the store row
        Assert.Equal("", store.GetArtist(artistUri)!.TopTracks![0].Title);         // the store's own record is untouched
    }

    // The album read carries the AlbumOpen surface -- that tag is what picks the trait bundle the album rung awaits
    // (RowBundle | PlayCount | Publishing), so it is part of the contract, not a log field.
    [Fact]
    public async Task GetAlbum_AsksForTheCallersRung_WithTheAlbumOpenSurface()
    {
        const string uri = "spotify:album:a1";
        var store = new InMemoryStore();
        var rec = new RecordingHydrator(store);
        var src = new StoreLibrarySource(store, Recording(rec), OfflineOnlineCatalog.Instance);

        _ = await src.GetAlbumAsync(uri, HydrationLevel.Full);

        var (uris, level, surface) = Assert.Single(rec.Batches);
        Assert.Equal(uri, Assert.Single(uris));
        Assert.Equal(HydrationLevel.Full, level);
        Assert.Equal(TraitSurface.AlbumOpen, surface);
    }

    // Fix 3: an album with no ©/℗ facet at all can never reach Rich (HydrationLevels.Of), so it seals Exhausted for a
    // day (HydrationPolicy.ExhaustedAlbumRichTtl) — and that seal skips AlbumHydration.ContinueAsync as a whole, its
    // OWN PlayCount/RowBundle trait step included, for the same day. GetAlbumAsync's companion trait ask keeps that
    // bundle alive on a RE-open regardless of the seal — gated to "already open, still short of Rich" so it never
    // duplicates the ladder's own ask on the FIRST (cold) open (see the sibling test below).
    [Fact]
    public async Task GetAlbum_ReOpen_FiresATraitAsk_WhenTheAlbumIsStuckBelowRich()
    {
        const string uri = "spotify:album:a1";
        var store = new InMemoryStore();
        // Already at Open from an earlier pass, but no ©/℗ facet at all — a real ledger would have sealed Rich
        // Exhausted for this album a day ago, and RecordingHydrator's no-op ensure stands in for that skip.
        store.UpsertAlbum(new Album("a1", uri, "A1", null, [], 2020, 1, [Trk("t1")], Hydration: AlbumHydrationLevel.Tracks));
        var rec = new RecordingHydrator(store);
        var src = new StoreLibrarySource(store, Recording(rec), OfflineOnlineCatalog.Instance);

        _ = await src.GetAlbumAsync(uri, HydrationLevel.Rich);

        var call = Assert.Single(rec.TraitCalls);
        Assert.Equal(TraitSurface.AlbumOpen, call.Surface);
        Assert.Contains(uri, call.Uris);
        Assert.Contains("spotify:track:t1", call.Uris);
    }

    [Fact]
    public async Task GetAlbum_ColdOpen_DoesNotDuplicateTheLaddersOwnTraitAsk()
    {
        // Nothing resident yet — this IS the ladder's first-ever pass over the album, which is what
        // AlbumHydrationTests.Rich_AwaitsOneTraitPassCarryingRowBundlePlayCountAndPublishing already covers. The
        // companion ask must not ALSO fire just because the (fake) ensure made no progress, or a cold Rich open would
        // cost two trait passes instead of one (HydrationWasteTests.AlbumOpenCold_IsOneCataloguePost_OneTraitPass_...).
        const string uri = "spotify:album:a1";
        var store = new InMemoryStore();
        store.UpsertAlbum(new Album("a1", uri, "A1", null, [], 2020, 1, [Trk("t1")]));   // Hydration: Summary — not yet Open
        var rec = new RecordingHydrator(store);
        var src = new StoreLibrarySource(store, Recording(rec), OfflineOnlineCatalog.Instance);

        _ = await src.GetAlbumAsync(uri, HydrationLevel.Rich);

        Assert.Empty(rec.TraitCalls);
    }

    [Fact]
    public async Task GetAlbum_NoneRead_NeverFiresTheCompanionTraitAsk()
    {
        // The None (pure resident, no-I/O) read must not turn into a network ask of its own — DetailPage.RefreshAsync
        // relies on that to repaint off a store signal without closing a refresh loop.
        const string uri = "spotify:album:a1";
        var store = new InMemoryStore();
        store.UpsertAlbum(new Album("a1", uri, "A1", null, [], 2020, 1, [Trk("t1")], Hydration: AlbumHydrationLevel.Tracks));
        var rec = new RecordingHydrator(store);
        var src = new StoreLibrarySource(store, Recording(rec), OfflineOnlineCatalog.Instance);

        _ = await src.GetAlbumAsync(uri, HydrationLevel.None);

        Assert.Empty(rec.Batches);
        Assert.Empty(rec.TraitCalls);
    }

    // Fix 4: AlbumHydration.RebuildTracklists only re-joins Album.Tracks from INSIDE its own Rich/Open post-steps, so
    // a play count/tempo that lands on the shared Track row from ANY other surface (a playlist trait pass, this
    // class's own Fix-3 companion ask) stayed invisible on the album page until that ladder step ran again — which
    // the Rich-exhausted seal can delay a full day. GetAlbumAsync now re-joins Album.Tracks against the store at
    // READ, the same shape as GetArtistAsync's TopTracks re-join.
    [Fact]
    public async Task GetAlbumAsync_TracksReJoinedFromStore()
    {
        const string uri = "spotify:album:a1";
        var store = new InMemoryStore();
        var staleTrack = new Track("t1", "spotify:track:t1", "T1", [], new AlbumRef("a1", uri, "A1"), 180_000, false, null);
        store.UpsertAlbum(new Album("a1", uri, "A1", null, [], 2020, 1, [staleTrack]));
        // A play count landed on the shared Track row from ANOTHER surface (a playlist trait pass) in the meantime —
        // the album's OWN denormalized Tracks snapshot above never saw it.
        store.UpsertTrack(staleTrack with { PlayCount = 4_200, TempoBpm = 128 });
        var src = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);

        var album = await src.GetAlbumAsync(uri, HydrationLevel.None);

        Assert.Equal(4_200, album!.Tracks![0].PlayCount);                    // re-joined from the store row
        Assert.Equal(128, album.Tracks![0].TempoBpm);
        Assert.Equal(0, store.GetAlbum(uri)!.Tracks![0].PlayCount);          // the store's own denormalized copy is untouched
    }

    // The PLAY path never waits on Rich: an ordered, named tracklist is Open, and that is all a context needs.
    [Fact]
    public async Task StreamTracks_AsksOnlyForOpen()
    {
        const string uri = "spotify:playlist:p";
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("t1"));
        store.SetMembership(uri, new[] { new PlaylistMember("i1", "spotify:track:t1", null, 0) }, null);
        var rec = new RecordingHydrator(store);
        var src = new StoreLibrarySource(store, Recording(rec), OfflineOnlineCatalog.Instance);

        await foreach (var _ in src.StreamTracksAsync(uri)) { }

        Assert.Equal(HydrationLevel.Open, Assert.Single(rec.Batches).Level);
    }

    // An EPISODE is a playable: a playlist holding one used to drop the row entirely (the join asked GetTrack alone),
    // which broke its count, its mosaic and its play context. EpisodeAsTrack is the projection (design 1.5).
    [Fact]
    public async Task GetPlaylist_JoinsEpisodeMembers_AsPlayableRows()
    {
        const string uri = "spotify:playlist:mixed";
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("t1"));
        store.UpsertEpisode(new Episode("e1", "spotify:episode:e1", "Ep One", "The Show",
            new Image("https://img/ep"), 1_800_000, System.DateTimeOffset.UnixEpoch));
        store.UpsertPlaylist(new Playlist("mixed", uri, "Mixed", null, "Me", null, 0));
        store.SetMembership(uri, new[]
        {
            new PlaylistMember("i1", "spotify:track:t1", null, 0),
            new PlaylistMember("i2", "spotify:episode:e1", "alice", 1_700_000_000_000),
        }, null);
        var src = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);

        var pl = await src.GetPlaylistAsync(uri);

        Assert.Equal(2, pl!.Tracks!.Count);
        Assert.Equal("Ep One", pl.Tracks[1].Title);
        Assert.Equal("The Show", pl.Tracks[1].Album.Name);
        Assert.Equal("alice", pl.Tracks[1].AddedBy);    // membership facts are stamped on the projection too
        Assert.Equal("i2", pl.Tracks[1].ContextUid);
        Assert.Equal(2, pl.TrackCount);
    }

    [Fact]
    public async Task GetPlaylist_ReturnsNull_ForUnknown()
    {
        var store = new InMemoryStore();
        var src = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);
        Assert.Null(await src.GetPlaylistAsync("spotify:playlist:nope"));
    }

    [Fact]
    public async Task GetPlaylists_FromRootlist_WithMembershipCount()
    {
        var store = new InMemoryStore();
        store.UpsertPlaylist(new Playlist("p1", "spotify:playlist:p1", "One", null, "Me", null, 0));
        store.SetRootlist(new[]
        {
            new RootlistEntry(0, 1, "spotify:start-group:g:F", "F", 0),   // a folder marker — not a playlist row
            new RootlistEntry(1, 0, "spotify:playlist:p1", null, 1),
        });
        store.SetMembership("spotify:playlist:p1", new[] { new PlaylistMember("i", "spotify:track:x", null, 0) }, null);
        var src = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);
        var pls = await src.GetPlaylistsAsync();
        Assert.Equal("One", Assert.Single(pls).Name);
        Assert.Equal(1, pls[0].TrackCount);
    }

    [Fact]
    public async Task GetStats_CountsEachSet()
    {
        var store = new InMemoryStore();
        store.SetSaved("albums", "spotify:album:a", true, SyncState.Confirmed);
        store.SetSaved("artists", "spotify:artist:b", true, SyncState.Confirmed);
        store.SetSaved("liked", "spotify:track:c", true, SyncState.Confirmed);
        store.SetSaved("shows", "spotify:show:d", true, SyncState.Confirmed);
        var src = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);
        var st = await src.GetStatsAsync();
        Assert.Equal(1, st.Albums);
        Assert.Equal(1, st.Artists);
        Assert.Equal(1, st.LikedSongs);
        Assert.Equal(1, st.Podcasts);
    }

    [Fact]
    public void CollectionsChanged_FiresForTheSetKind_OnStoreBump()
    {
        var store = new InMemoryStore();
        var src = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);
        CollectionKind? seen = null;
        using var sub = src.CollectionsChanged.Subscribe(new Obs(k => seen = k));
        store.SetSaved("albums", "spotify:album:a", true, SyncState.Confirmed);   // bumps spotify:album:a
        Assert.Equal(CollectionKind.Albums, seen);
    }

    sealed class Obs(System.Action<CollectionKind> onNext) : System.IObserver<CollectionKind>
    {
        public void OnNext(CollectionKind v) => onNext(v);
        public void OnCompleted() { }
        public void OnError(System.Exception e) { }
    }

    sealed class StoreObs(System.Action<StoreChange> onNext) : System.IObserver<StoreChange>
    {
        public void OnNext(StoreChange v) => onNext(v);
        public void OnCompleted() { }
        public void OnError(System.Exception e) { }
    }

    // ── the collections default order: added-date DESC (newest first), the add time stamped on the read-model row ─────
    [Fact]
    public async Task GetLikedSongs_OrdersByAddedDesc_AndStampsAddedAt()
    {
        var store = new InMemoryStore();
        foreach (var id in new[] { "a", "b", "c", "z" }) store.UpsertTrack(Trk(id));
        store.SetSaved("liked", "spotify:track:a", true, SyncState.Confirmed, 1_000_000);
        store.SetSaved("liked", "spotify:track:b", true, SyncState.Confirmed, 3_000_000);   // newest
        store.SetSaved("liked", "spotify:track:c", true, SyncState.Confirmed, 2_000_000);
        store.SetSaved("liked", "spotify:track:z", true, SyncState.Confirmed);              // no timestamp → sinks last
        var src = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);

        var liked = await src.GetLikedSongsAsync();
        Assert.Equal(new[] { "spotify:track:b", "spotify:track:c", "spotify:track:a", "spotify:track:z" },
            liked.Select(t => t.Uri).ToArray());
        Assert.Equal(System.DateTimeOffset.FromUnixTimeMilliseconds(3_000_000), liked[0].AddedAt);
        Assert.Null(liked[3].AddedAt);
    }

    // added_at semantics at the store: 0 preserves, non-zero refines silently (no extra change signal), survives unlike→relike reset.
    [Fact]
    public void SetSaved_AddedAt_PreservesOnZero_RefinesSilently()
    {
        var store = new InMemoryStore();
        var col = new ChangeCollector();
        store.SetSaved("liked", "spotify:track:x", true, SyncState.Pending, 5_000);   // optimistic like stamps local now
        using var sub = store.Changes.Subscribe(col);
        store.SetSaved("liked", "spotify:track:x", true, SyncState.Confirmed);        // ack: 0 → timestamp preserved
        Assert.Equal(5_000, Assert.Single(store.SavedItems("liked")).AddedAtMs);
        int after = col.All.Count;
        store.SetSaved("liked", "spotify:track:x", true, SyncState.Confirmed, 7_000); // server echo refines → silent
        Assert.Equal(7_000, Assert.Single(store.SavedItems("liked")).AddedAtMs);
        Assert.Equal(after, col.All.Count);                                           // no extra change signal
    }
}
