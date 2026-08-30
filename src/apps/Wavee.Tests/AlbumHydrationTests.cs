using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Hydration;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The album ladder (docs/plans/wavee/hydration-facade-design.md §2.3). These pin the POLICY the old
// LiveSessionHost.EnsureAlbumAsync encoded as early-outs, plus the two things the rung split added: the caller's rung
// decides how much is awaited, and the whole BATCH shares one repair call and one trait POST.
//
// Absorbs the intent of DiscographyPaginationTests.AlbumGate_* (a named V4 tracklist opens; a gid-only one does not)
// now that the gate is HydrationLevels.Of(Album) rather than StoreLibrarySource.IsAlbumOpenReady.
public class AlbumHydrationTests
{
    const string AlbumUri = "spotify:album:a1";

    static Track Row(string id, string title = "T", string albumUri = AlbumUri) =>
        new(id, "spotify:track:" + id, title, Array.Empty<ArtistRef>(), new AlbumRef("a1", albumUri, "A1"),
            180_000, false, null);

    static Album Tracklist(params Track[] rows) =>
        new("a1", AlbumUri, "A1", null, Array.Empty<ArtistRef>(), 2020, rows.Length, rows,
            Hydration: AlbumHydrationLevel.Tracks);

    static AlbumHydration Ladder(LadderHarness h) => new(h.Store, h.Envelopes);

    // ── Open ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Open_NamedV4Tracklist_NeedsNoRepairAndNoGetAlbum()
    {
        // The V4-first policy: a named tracklist IS the openable album. getAlbum on this path was the round trip the
        // whole rework exists to remove.
        using var h = new LadderHarness();
        h.Store.UpsertAlbum(Tracklist(Row("t1"), Row("t2")));

        await Ladder(h).ContinueAsync(LadderHarness.Batch(AlbumUri), HydrationLevel.Open, default, h.Ctx, CancellationToken.None);

        Assert.Empty(h.Hydrator.Batches);
        Assert.Empty(h.Envelopes.AlbumCalls);
        Assert.Equal(HydrationLevel.Open, HydrationLevels.Of(h.Store.GetAlbum(AlbumUri)));
    }

    [Fact]
    public async Task Open_UnnamedRows_OneIdentityRepairThenTheTracklistIsRebuilt()
    {
        // AlbumV4's disc rows are gid-only for tracks the album entity carried no names for. The repair writes TRACK
        // entities; the album's embedded list is a denormalized copy, so it only heals if it is REBUILT from them.
        using var h = new LadderHarness();
        h.Store.UpsertAlbum(Tracklist(Row("t1"), Row("t2", title: "")));
        h.Hydrator.OnEnsureMany = uris => { foreach (var u in uris) h.Store.UpsertTrack(Row(EntityUri.IdOf(u), "Two")); };

        await Ladder(h).ContinueAsync(LadderHarness.Batch(AlbumUri), HydrationLevel.Open, default, h.Ctx, CancellationToken.None);

        var batch = Assert.Single(h.Hydrator.Batches);
        Assert.Equal(HydrationLevel.Identity, batch.Level);
        Assert.Equal(["spotify:track:t2"], batch.Uris);          // only the unnamed row is asked for
        Assert.Equal("Two", h.Store.GetAlbum(AlbumUri)!.Tracks![1].Title);
        Assert.Empty(h.Envelopes.AlbumCalls);                    // …and the repair is what made getAlbum unnecessary
        Assert.Equal(HydrationLevel.Open, HydrationLevels.Of(h.Store.GetAlbum(AlbumUri)));
    }

    [Fact]
    public async Task Open_TheRepairIsOneBatchForTheWholeAlbumWave()
    {
        // The per-album shape (one SyncAll per album) is what made opening a discography shelf fire N round trips.
        using var h = new LadderHarness();
        h.Store.UpsertAlbum(Tracklist(Row("t1", title: "")));
        h.Store.UpsertAlbum(new Album("a2", "spotify:album:a2", "A2", null, Array.Empty<ArtistRef>(), 2021, 1,
            new[] { Row("t9", "", "spotify:album:a2") }, Hydration: AlbumHydrationLevel.Tracks));

        await Ladder(h).ContinueAsync(LadderHarness.Batch(AlbumUri, "spotify:album:a2"), HydrationLevel.Open, default,
            h.Ctx, CancellationToken.None);

        var batch = Assert.Single(h.Hydrator.Batches);
        Assert.Equal(["spotify:track:t1", "spotify:track:t9"], batch.Uris);
    }

    [Fact]
    public async Task Open_V4Empty_FallsBackToGetAlbumAndWritesArtistsTracksAlbum()
    {
        // The ONLY surviving getAlbum-on-open case. The write order is load-bearing: the tracklist is fanned out as
        // ENTITIES before the album write, because CachedStore.PersistAlbum strips Tracks.
        using var h = new LadderHarness();
        h.Store.UpsertAlbum(new Album("a1", AlbumUri, "A1", null, Array.Empty<ArtistRef>(), 2020, 0));
        h.Envelopes.OnAlbum = _ => new Album("a1", AlbumUri, "A1", null, Array.Empty<ArtistRef>(), 2020, 1,
            new[] { Row("t1", "One") }, ArtistsDetailed: new[] { new Artist("ar1", "spotify:artist:ar1", "Ar1", null) },
            Copyright: "© 2020", Hydration: AlbumHydrationLevel.Full);

        await Ladder(h).ContinueAsync(LadderHarness.Batch(AlbumUri), HydrationLevel.Open, default, h.Ctx, CancellationToken.None);

        Assert.Equal([AlbumUri], h.Envelopes.AlbumCalls);
        Assert.Equal("Ar1", h.Store.GetArtist("spotify:artist:ar1")?.Name);
        Assert.Equal("One", h.Store.GetTrack("spotify:track:t1")?.Title);
        Assert.Equal(HydrationLevel.Full, HydrationLevels.Of(h.Store.GetAlbum(AlbumUri)));
    }

    [Fact]
    public async Task Open_RowFacetsGoOnThePump_NotTheCriticalPath()
    {
        // An Open ask still wants video/adornment facets for its rows — just not before first paint. (Rich is the rung
        // that awaits them; see below.)
        using var h = new LadderHarness();
        h.Store.UpsertAlbum(Tracklist(Row("t1")));
        using var release = new ManualResetEventSlim(false);
        h.Traits.OnEnsure = _ => release.Wait(TimeSpan.FromSeconds(10));

        // The ladder returns while the (deliberately stuck) trait pass is still running on the pump. If it were awaited,
        // this would sit behind the gate and time out — which is exactly the regression worth catching.
        await Ladder(h).ContinueAsync(LadderHarness.Batch(AlbumUri), HydrationLevel.Open, default, h.Ctx, CancellationToken.None)
                       .WaitAsync(TimeSpan.FromSeconds(2));

        release.Set();
        await h.DrainAsync();
        var call = Assert.Single(h.Traits.Calls);
        // PlayCount rides the Open post-step too: kind 185 has no retry surface, so the Open rung is its one shot.
        Assert.Equal(TraitSet.RowBundle | TraitSet.PlayCount, call.Traits);
        Assert.Equal(TraitSurface.AlbumOpen, call.Surface);
        Assert.Equal(["spotify:track:t1"], call.Uris);
    }

    // ── Rich ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rich_AwaitsOneTraitPassCarryingRowBundlePlayCountAndPublishing()
    {
        // ONE door replaces plays + publishing + FillAlbumAdornments + the second 185 read, and it is AWAITED: the ©/℗
        // line and the top-track star are first-paint content. The ALBUM uri rides the same list (183 is album-only).
        using var h = new LadderHarness();
        h.Store.UpsertAlbum(Tracklist(Row("t1"), Row("t2")));

        await Ladder(h).ContinueAsync(LadderHarness.Batch(AlbumUri), HydrationLevel.Rich, default, h.Ctx, CancellationToken.None);

        var call = Assert.Single(h.Traits.Calls);
        Assert.Equal(TraitSet.RowBundle | TraitSet.PlayCount | TraitSet.Publishing, call.Traits);
        Assert.Equal(TraitSurface.AlbumOpen, call.Surface);
        Assert.Equal([AlbumUri, "spotify:track:t1", "spotify:track:t2"], call.Uris);
        Assert.Empty(h.Envelopes.AlbumCalls);          // Rich is still V4 + traits — no Pathfinder round trip
    }

    /// <summary>The awaited trait pass has to reach the ALBUM PAGE, not just the store's track plane.
    ///
    /// <para>Kind 185 (and 222/6) is projected onto the shared <c>Track</c> rows, while an album's <c>Tracks</c> is a
    /// DENORMALIZED copy that <c>DetailPage.MapAlbum</c> → <c>DetailTracks.TrackAt/TopTrack</c> read verbatim. Without
    /// the re-join, blocking the album open on Rich bought nothing visible: every Plays cell painted "—" and no track
    /// got the star until the below-the-fold getAlbum landed its own tracklist a round trip later. Before the façade
    /// the counts arrived inside that envelope's tracklist, which is why nothing used to have to re-join them.</para></summary>
    [Fact]
    public async Task Rich_FoldsTheTraitPassBackIntoTheAlbumsOwnTracklist()
    {
        using var h = new LadderHarness();
        h.Store.UpsertAlbum(Tracklist(Row("t1"), Row("t2")));
        // What the pipeline does: write the facets onto the shared ROWS.
        h.Traits.OnEnsure = uris =>
        {
            foreach (var uri in uris)
            {
                if (h.Store.GetTrack(uri) is not { } row) continue;
                h.Store.UpsertTrack(row with { PlayCount = 4_200, TempoBpm = 128 });
            }
        };
        h.Store.UpsertTrack(Row("t1"));
        h.Store.UpsertTrack(Row("t2"));

        await Ladder(h).ContinueAsync(LadderHarness.Batch(AlbumUri), HydrationLevel.Rich, default, h.Ctx, CancellationToken.None);

        var tracks = h.Store.GetAlbum(AlbumUri)!.Tracks!;
        Assert.All(tracks, t => Assert.Equal(4_200, t.PlayCount));   // the Plays column and the star
        Assert.All(tracks, t => Assert.Equal(128, t.TempoBpm));      // and the rest of the row bundle
    }

    [Fact]
    public async Task Rich_TraitFailure_IsBestEffort()
    {
        // A mapper throw must never turn a renderable album into a failed open.
        using var h = new LadderHarness();
        h.Store.UpsertAlbum(Tracklist(Row("t1")));
        h.Traits.Throw = true;

        await Ladder(h).ContinueAsync(LadderHarness.Batch(AlbumUri), HydrationLevel.Rich, default, h.Ctx, CancellationToken.None);

        Assert.Equal(HydrationLevel.Open, HydrationLevels.Of(h.Store.GetAlbum(AlbumUri)));
    }

    // ── Full ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Full_FetchesTheEnvelopeOnceAndReachesFull()
    {
        using var h = new LadderHarness();
        h.Store.UpsertAlbum(Tracklist(Row("t1")));
        h.Envelopes.OnAlbum = _ => Tracklist(Row("t1", "One")) with
        {
            Label = "Label", Copyright = "© 2020", Hydration = AlbumHydrationLevel.Full,
        };

        await Ladder(h).ContinueAsync(LadderHarness.Batch(AlbumUri), HydrationLevel.Full, default, h.Ctx, CancellationToken.None);

        Assert.Equal([AlbumUri], h.Envelopes.AlbumCalls);
        Assert.Equal(HydrationLevel.Full, HydrationLevels.Of(h.Store.GetAlbum(AlbumUri)));
    }

    [Fact]
    public async Task Full_AfterAV4EmptyFallback_DoesNotFetchTheEnvelopeTwice()
    {
        // The fallback and the Full upgrade are the SAME envelope; asking for Full on a V4-empty album must cost one
        // getAlbum, not two.
        using var h = new LadderHarness();
        h.Store.UpsertAlbum(new Album("a1", AlbumUri, "A1", null, Array.Empty<ArtistRef>(), 2020, 0));
        h.Envelopes.OnAlbum = _ => Tracklist(Row("t1", "One")) with
        {
            Copyright = "© 2020", Hydration = AlbumHydrationLevel.Full,
        };

        await Ladder(h).ContinueAsync(LadderHarness.Batch(AlbumUri), HydrationLevel.Full, default, h.Ctx, CancellationToken.None);

        Assert.Single(h.Envelopes.AlbumCalls);
    }

    [Fact]
    public async Task EnvelopeFailure_IsBestEffort_AndLeavesTheAlbumWhereItWas()
    {
        using var h = new LadderHarness();
        h.Store.UpsertAlbum(new Album("a1", AlbumUri, "A1", null, Array.Empty<ArtistRef>(), 2020, 0));
        h.Envelopes.Throw = true;

        await Ladder(h).ContinueAsync(LadderHarness.Batch(AlbumUri), HydrationLevel.Open, default, h.Ctx, CancellationToken.None);

        Assert.Equal(HydrationLevel.Identity, HydrationLevels.Of(h.Store.GetAlbum(AlbumUri)));
    }

    [Fact]
    public async Task RepairFailure_FallsThroughToGetAlbum()
    {
        // Exactly what the old procedure did: a failed TrackV4 batch is the getAlbum fallback's trigger. The fallback
        // envelope here is the SAME LENGTH as the (blank) resident row, so FetchEnvelopeAsync's adopt-gate withholds
        // the wholesale tracklist replacement (§2a) — the named row lands instead through the ordinary re-join, which
        // for an Open-only ask is the row-facets POST STEP on the pump, not something ContinueAsync itself awaits.
        using var h = new LadderHarness();
        h.Store.UpsertAlbum(Tracklist(Row("t1", title: "")));
        h.Hydrator.Throw = true;
        h.Envelopes.OnAlbum = _ => Tracklist(Row("t1", "One"));

        await Ladder(h).ContinueAsync(LadderHarness.Batch(AlbumUri), HydrationLevel.Open, default, h.Ctx, CancellationToken.None);
        await h.DrainAsync();

        Assert.Single(h.Envelopes.AlbumCalls);
        Assert.Equal(HydrationLevel.Open, HydrationLevels.Of(h.Store.GetAlbum(AlbumUri)));
    }

    /// <summary>getAlbum PINS limit=50 (PathfinderEnvelopeFetch.AlbumAsync:28) — its tracklist is a WINDOW onto the
    /// release, not necessarily the whole thing. A resident tracklist that is already at least as long as the window
    /// must survive the fetch intact: <see cref="AlbumHydration.RebuildTracklists"/> re-joins field-level facets, it
    /// cannot heal a list whose rows are simply GONE.</summary>
    [Fact]
    public async Task Full_EnvelopeWithFiftyRows_DoesNotTruncateAHundredRowAlbum()
    {
        using var h = new LadderHarness();
        var rows = new Track[100];
        for (int i = 0; i < 100; i++) rows[i] = Row($"t{i}");
        h.Store.UpsertAlbum(Tracklist(rows));                     // a 100-row, fully-named (Open) tracklist
        h.Envelopes.OnAlbum = _ => Tracklist(rows[..50]) with { Hydration = AlbumHydrationLevel.Full };

        await Ladder(h).ContinueAsync(LadderHarness.Batch(AlbumUri), HydrationLevel.Full, default, h.Ctx, CancellationToken.None);

        Assert.Equal([AlbumUri], h.Envelopes.AlbumCalls);          // the envelope really was asked for…
        Assert.Equal(100, h.Store.GetAlbum(AlbumUri)!.Tracks!.Count);   // …but it did not shrink the resident list
    }

    /// <summary>A same-length getAlbum response that regresses to blank/zeroed rows must not blank what
    /// <c>StoreEntityMerge.Track</c> already protects (Title/DurationMs/PlayCount never go backwards). The adopt-gate
    /// leaves the album's OWN Tracks alone on a same-length response, and the Full re-join then reads the TRACK
    /// plane's merge answer back — which already kept the richer values — so nothing regresses either way.</summary>
    [Fact]
    public async Task Full_EnvelopeRowsBlank_TracklistKeepsRepairedNames()
    {
        using var h = new LadderHarness();
        var t1 = Row("t1", title: "One") with { PlayCount = 111 };
        var t2 = Row("t2", title: "Two") with { PlayCount = 222 };
        h.Store.UpsertTrack(t1);
        h.Store.UpsertTrack(t2);
        h.Store.UpsertAlbum(Tracklist(t1, t2));
        var before = HydrationLevels.Of(h.Store.GetAlbum(AlbumUri));
        h.Envelopes.OnAlbum = _ => Tracklist(
                Row("t1", title: "") with { DurationMs = 0 },
                Row("t2", title: "") with { DurationMs = 0 })
            with { Hydration = AlbumHydrationLevel.Full };

        await Ladder(h).ContinueAsync(LadderHarness.Batch(AlbumUri), HydrationLevel.Full, default, h.Ctx, CancellationToken.None);

        var tracks = h.Store.GetAlbum(AlbumUri)!.Tracks!;
        Assert.Equal("One", tracks[0].Title);
        Assert.Equal("Two", tracks[1].Title);
        Assert.Equal(180_000, tracks[0].DurationMs);
        Assert.Equal(180_000, tracks[1].DurationMs);
        Assert.Equal(111, tracks[0].PlayCount);
        Assert.Equal(222, tracks[1].PlayCount);
        Assert.True(HydrationLevels.Of(h.Store.GetAlbum(AlbumUri)) >= before);
    }

    /// <summary>The V4-empty case: there is nothing resident to protect, so the adopt-gate must not withhold the
    /// envelope's own tracklist just because the comparison is against an empty list.</summary>
    [Fact]
    public async Task Full_EnvelopeOnEmptyAlbum_AdoptsTracks()
    {
        using var h = new LadderHarness();
        h.Store.UpsertAlbum(new Album("a1", AlbumUri, "A1", null, Array.Empty<ArtistRef>(), 2020, 0));
        h.Envelopes.OnAlbum = _ => Tracklist(Row("t1", "One"), Row("t2", "Two")) with { Hydration = AlbumHydrationLevel.Full };

        await Ladder(h).ContinueAsync(LadderHarness.Batch(AlbumUri), HydrationLevel.Full, default, h.Ctx, CancellationToken.None);

        var tracks = h.Store.GetAlbum(AlbumUri)!.Tracks;
        Assert.Equal(2, tracks?.Count);
        Assert.Equal(HydrationLevel.Full, HydrationLevels.Of(h.Store.GetAlbum(AlbumUri)));
    }

    [Fact]
    public void ExtraCatalogKinds_IsEmptyInP1()
    {
        // 183 is fused into the step-0 POST only once a PublishingProjector exists to project it (P2). Fusing earlier
        // would fetch a payload nothing reads — the trait pipeline's Publishing arm asks for it in the meantime.
        using var h = new LadderHarness();
        var into = new List<(string Uri, int Kind)>();
        var uri = EntityUri.Parse(AlbumUri);

        Ladder(h).ExtraCatalogKinds(in uri, HydrationLevel.Rich, into);

        Assert.Empty(into);
    }
}
