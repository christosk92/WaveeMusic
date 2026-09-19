using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Hydration;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The artist ladder (docs/plans/wavee/hydration-facade-design.md §2.3): three transports that used to be three services
// with three freshness gates, now three RUNGS in a fixed order — Open assembles the discography, Rich lands the
// overview, Full extends the chart and fills its counts.
//
// Absorbs the behaviour the old suites expressed: MetadataSourceTests' assemble intent, ArtistStatsPlayCountTests'
// stats-only-write + counts-on-the-shared-row properties, and ArtistPopularTracksTests.Ensure_* (merge keeps the seed
// head, the countless top-up needs no GET, a failure keeps the seed).
public class ArtistHydrationTests
{
    const string ArtistUri = "spotify:artist:ar1";

    static Album Card(string id, string name, int year = 2020) =>
        new(id, "spotify:album:" + id, name, null, Array.Empty<ArtistRef>(), year, 0);

    static Track Chart(string id, long plays = 0) =>
        new(id, "spotify:track:" + id, "T" + id, Array.Empty<ArtistRef>(), new AlbumRef("", "", ""),
            200_000, false, null, PlayCount: plays);

    /// <summary>An artist whose discography is already assembled — the Open rung, so a Rich/Full test starts where it
    /// means to.</summary>
    static Artist Assembled(IReadOnlyList<Track>? topTracks = null, DateTimeOffset fetchedAt = default,
                            DateTimeOffset chartFetchedAt = default) =>
        new("ar1", ArtistUri, "Ar1", null, TopAlbums: new[] { Card("al1", "Al1") }, AlbumsTotal: 1,
            TopTracks: topTracks, LatestRelease: topTracks is null ? null : Card("al1", "Al1"),
            // THREE stamps, three questions. FetchedAt is the max-of clock persistence and every other SWR reader use;
            // OverviewFetchedAt is the overview transport's own clock (the Rich age gate); ChartFetchedAt is the chart
            // transport's (the Full one). A helper that set one for all of them would make every freshness assertion
            // below test the wrong field. ChartFetchedAt defaults to NEVER, which is what a row written before the
            // chart step existed reads back as — such a row re-fetches once, by design.
            FetchedAt: fetchedAt, OverviewFetchedAt: fetchedAt, ChartFetchedAt: chartFetchedAt);

    /// <summary>Seed the artist AND make its named discography cards RESIDENT — the ladder's stub test is "named AND
    /// resident", because a name without a card is exactly the un-hydrated stub it exists to fetch.</summary>
    static void Seed(LadderHarness h, Artist artist)
    {
        h.Store.UpsertArtist(artist);
        if (artist.TopAlbums is { } cards)
            for (int i = 0; i < cards.Count; i++)
                if (cards[i].Name.Length > 0) h.Store.UpsertAlbum(cards[i]);
    }

    static ArtistHydration Ladder(LadderHarness h) => new(h.Store, h.Envelopes, h.Chart);

    static Task Run(LadderHarness h, HydrationLevel level)
        => Ladder(h).ContinueAsync(LadderHarness.Batch(ArtistUri), level, default, h.Ctx, CancellationToken.None);

    // ── Open ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Open_HydratesTheOwnDiscographyStubsThenAssembles()
    {
        // ArtistV4 carries the discography as gid-only stubs; AlbumV4 turns each into a resident card; Assemble folds
        // the cards back onto the artist row. Only then is the facet total covered by what we hold — i.e. Open.
        using var h = new LadderHarness();
        h.Store.UpsertArtist(new Artist("ar1", ArtistUri, "Ar1", null, TopAlbums: new[] { Card("al1", "") }, AlbumsTotal: 1));
        h.Hydrator.OnEnsureMany = uris => { foreach (var u in uris) h.Store.UpsertAlbum(Card(EntityUri.IdOf(u), "Al1")); };

        await Run(h, HydrationLevel.Open);

        var batch = Assert.Single(h.Hydrator.Batches);
        Assert.Equal(HydrationLevel.Identity, batch.Level);
        Assert.Equal(["spotify:album:al1"], batch.Uris);
        Assert.Equal("Al1", h.Store.GetArtist(ArtistUri)!.TopAlbums![0].Name);
        Assert.Equal(HydrationLevel.Open, HydrationLevels.Of(h.Store.GetArtist(ArtistUri)));
        Assert.Empty(h.Envelopes.OverviewCalls);       // Open is 100% V4 — the Library pane must never pay for stats
    }

    [Fact]
    public async Task Open_LeavesAppearsOnAlone_RichCapsItAtTheShelfSlice()
    {
        // The appears-on set can be thousands; only the Rich shelf slice is ever bulk-hydrated.
        var appears = Enumerable.Range(0, Wavee.Backend.Metadata.ArtistDiscography.AppearsOnHydrateCap + 5).Select(i => Card("ap" + i, "")).ToArray();
        using var open = new LadderHarness();
        Seed(open, Assembled() with { AppearsOn = appears });

        await Run(open, HydrationLevel.Open);
        Assert.Empty(open.Hydrator.Batches);           // own stubs are already resident-named; appears-on is not asked

        using var rich = new LadderHarness();
        Seed(rich, Assembled() with { AppearsOn = appears });

        await Run(rich, HydrationLevel.Rich);
        Assert.Equal(Wavee.Backend.Metadata.ArtistDiscography.AppearsOnHydrateCap, Assert.Single(rich.Hydrator.Batches).Uris.Count);
    }

    // ── Rich ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rich_OneOverviewCall_WrittenStatsOnly()
    {
        // The neutralized fields are the whole point: the overview carries only the FIRST ~10 releases per facet and
        // MergeAlbumCards treats a non-null incoming list as authoritative, so a raw upsert clobbers a full ArtistV4
        // discography down to that first page. Totals go to 0 = unknown for the same reason.
        using var h = new LadderHarness();
        Seed(h, Assembled());
        h.Envelopes.OnOverview = _ => new Artist("ar1", ArtistUri, "Ar1", null,
            TopAlbums: new[] { Card("al9", "Overview page 1") }, MonthlyListeners: 1000,
            TopTracks: new[] { Chart("t1", 20491) }, AlbumsTotal: 99, LatestRelease: Card("al1", "Al1"));

        await Run(h, HydrationLevel.Rich);

        Assert.Equal([ArtistUri], h.Envelopes.OverviewCalls);
        var artist = h.Store.GetArtist(ArtistUri)!;
        Assert.Equal(["spotify:album:al1"], artist.TopAlbums!.Select(a => a.Uri));   // V4 discography survived
        Assert.Equal(1, artist.AlbumsTotal);                                          // …and so did its total
        Assert.Equal(1000, artist.MonthlyListeners);                                  // the stats DID land
        Assert.Equal(20491, artist.TopTracks![0].PlayCount);
        // The count is ALSO on the shared track plane — what every later merge and the cold persist read.
        Assert.Equal(20491, h.Store.GetTrack("spotify:track:t1")?.PlayCount);
        Assert.Equal(HydrationLevel.Rich, HydrationLevels.Of(artist));
    }

    [Fact]
    public async Task Rich_FreshStamp_SkipsTheOverviewEntirely()
    {
        using var h = new LadderHarness();
        Seed(h, Assembled(new[] { Chart("t1", 5) }, DateTimeOffset.UtcNow));

        await Run(h, HydrationLevel.Rich);

        Assert.Empty(h.Envelopes.OverviewCalls);
    }

    [Fact]
    public async Task Rich_FreshFetchedAtWithNoOverviewStamp_StillRefetches()
    {
        // The stamps are not interchangeable. An artist whose FetchedAt was bumped by the CHART step (or by any writer
        // that carried one) while its overview last landed a fortnight ago is stale — gating on FetchedAt, as this once
        // did, showed a week-old releases column and a week-old play counts for a whole TTL.
        using var h = new LadderHarness();
        Seed(h, Assembled(new[] { Chart("t1", 5) }) with
        {
            FetchedAt = DateTimeOffset.UtcNow,
            OverviewFetchedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(13),
        });
        h.Envelopes.OnOverview = _ => Assembled(new[] { Chart("t1", 7) });

        await Run(h, HydrationLevel.Rich);

        Assert.Single(h.Envelopes.OverviewCalls);
    }

    [Fact]
    public async Task Rich_OverviewWrite_StampsBothClocks()
    {
        using var h = new LadderHarness();
        Seed(h, Assembled());
        h.Envelopes.OnOverview = _ => Assembled(new[] { Chart("t1", 5) });

        await Run(h, HydrationLevel.Rich);

        var artist = h.Store.GetArtist(ArtistUri)!;
        Assert.True(DateTimeOffset.UtcNow - artist.OverviewFetchedAt < TimeSpan.FromMinutes(1));
        Assert.True(DateTimeOffset.UtcNow - artist.FetchedAt < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Rich_StaleStamp_RefetchesEvenThoughTheArtistIsAlreadyRich()
    {
        // Presence alone is not freshness: an artist whose overview landed a fortnight ago IS Rich and IS stale.
        using var h = new LadderHarness();
        Seed(h, Assembled(new[] { Chart("t1", 5) }, DateTimeOffset.UtcNow - TimeSpan.FromHours(13)));
        h.Envelopes.OnOverview = _ => Assembled(new[] { Chart("t1", 7) });

        await Run(h, HydrationLevel.Rich);

        Assert.Single(h.Envelopes.OverviewCalls);
    }

    [Fact]
    public async Task Rich_OverviewFailure_KeepsWhatWasResident()
    {
        using var h = new LadderHarness();
        Seed(h, Assembled());
        h.Envelopes.Throw = true;

        await Run(h, HydrationLevel.Rich);

        Assert.Equal(HydrationLevel.Open, HydrationLevels.Of(h.Store.GetArtist(ArtistUri)));
    }

    // ── Full ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Full_ChartGetThenIdentityBatchThenMerge_SeedHeadKeepsItsCounts()
    {
        // Step two: the extension endpoint carries uris ONLY. If its copy of a shared uri won, the top rows would
        // silently lose their "N plays" subline.
        using var h = new LadderHarness();
        Seed(h, Assembled(new[] { Chart("a", 900), Chart("b", 800) }, DateTimeOffset.UtcNow));
        h.Chart.OnUris = _ => ["spotify:track:a", "spotify:track:b", "spotify:track:c", "spotify:track:d"];
        h.Hydrator.OnEnsureMany = uris => { foreach (var u in uris) h.Store.UpsertTrack(Chart(EntityUri.IdOf(u))); };

        await Run(h, HydrationLevel.Full);

        Assert.Single(h.Chart.Calls);
        var batch = Assert.Single(h.Hydrator.Batches);
        Assert.Equal(HydrationLevel.Identity, batch.Level);
        Assert.Equal(4, batch.Uris.Count);
        Assert.Equal([900, 800, 0, 0], h.Store.GetArtist(ArtistUri)!.TopTracks!.Select(t => t.PlayCount));
    }

    [Fact]
    public async Task Full_AwaitsTheChartTraitPass_ThenReadsTheCountsOffTheRowsItWrote()
    {
        // Step three, through the ONE door: the pipeline writes kind 185 onto the shared ROWS, and the chart (a
        // projection) picks them back up. A row that already carries a positive count is never overwritten.
        using var h = new LadderHarness();
        Seed(h, Assembled(new[] { Chart("a", 900), Chart("b", 800) }, DateTimeOffset.UtcNow));
        h.Chart.OnUris = _ => ["spotify:track:a", "spotify:track:b", "spotify:track:c", "spotify:track:d"];
        h.Hydrator.OnEnsureMany = uris => { foreach (var u in uris) h.Store.UpsertTrack(Chart(EntityUri.IdOf(u))); };
        h.Traits.OnEnsure = _ =>
        {
            h.Store.UpsertTrack(Chart("c", 300));
            h.Store.UpsertTrack(Chart("d", 200));
        };

        await Run(h, HydrationLevel.Full);

        var call = Assert.Single(h.Traits.Calls);
        Assert.Equal(TraitSet.RowBundle | TraitSet.PlayCount, call.Traits);
        Assert.Equal(TraitSurface.ArtistPopular, call.Surface);
        Assert.Equal(4, call.Uris.Count);
        Assert.Equal([900, 800, 300, 200], h.Store.GetArtist(ArtistUri)!.TopTracks!.Select(t => t.PlayCount));
    }

    [Fact]
    public async Task Full_AlreadyExtendedAndFreshButCountless_TopsUpWithoutTheGet()
    {
        // A chart that was FETCHED (so the chart clock is stamped) but whose rows have no counts — what a build before
        // step three left behind. It must gain its counts without re-running the extension GET or the hydrate, and
        // once every row is counted, the next ask costs nothing at all.
        var extended = Enumerable.Range(0, ArtistPopularTracks.OverviewSeedCap + 2)
            .Select(i => Chart("t" + i, i < 2 ? 100 : 0)).ToArray();
        using var h = new LadderHarness();
        Seed(h, Assembled(extended, DateTimeOffset.UtcNow, chartFetchedAt: DateTimeOffset.UtcNow));
        h.Traits.OnEnsure = uris => { foreach (var u in uris) h.Store.UpsertTrack(Chart(EntityUri.IdOf(u), 42)); };

        await Run(h, HydrationLevel.Full);

        Assert.Empty(h.Chart.Calls);
        Assert.Empty(h.Hydrator.Batches);
        Assert.Single(h.Traits.Calls);
        Assert.All(h.Store.GetArtist(ArtistUri)!.TopTracks!, t => Assert.True(t.PlayCount > 0));
        Assert.Equal([100, 100], h.Store.GetArtist(ArtistUri)!.TopTracks!.Take(2).Select(t => t.PlayCount));

        await Run(h, HydrationLevel.Full);
        Assert.Single(h.Traits.Calls);                 // fully served from the store — not even a trait ask
    }

    // THE re-GET regression (finding 9). HydrationLevels.Of(Artist) only calls an artist Full when the chart is LONGER
    // than the overview seed cap, so an artist whose real chart is a handful of tracks can never reach Full — the old
    // gate ("Full AND the overview stamp is fresh") could therefore never be true for them, and the spclient chart GET
    // re-fired on every ask past the 10-minute exhausted seal, forever. The chart's OWN stamp is what says "we asked,
    // and this short list is the answer".
    [Fact]
    public async Task Full_ShortChart_IsNotReFetched_EvenThoughItNeverReachesTheFullRung()
    {
        using var h = new LadderHarness();
        Seed(h, Assembled(new[] { Chart("a", 900) }, DateTimeOffset.UtcNow));
        h.Chart.OnUris = _ => ["spotify:track:a", "spotify:track:b"];   // a genuinely short chart: 2 rows
        h.Hydrator.OnEnsureMany = uris => { foreach (var u in uris) h.Store.UpsertTrack(Chart(EntityUri.IdOf(u), 10)); };

        await Run(h, HydrationLevel.Full);
        Assert.Single(h.Chart.Calls);
        // Presence still says Rich — 2 rows is under the seed cap — and that is CORRECT and not the freshness question.
        Assert.Equal(HydrationLevel.Rich, HydrationLevels.Of(h.Store.GetArtist(ArtistUri)));
        Assert.NotEqual(default, h.Store.GetArtist(ArtistUri)!.ChartFetchedAt);

        await Run(h, HydrationLevel.Full);
        await Run(h, HydrationLevel.Full);
        Assert.Single(h.Chart.Calls);   // …still ONE GET, however often the exhausted seal lets the ladder re-run
    }

    // An EMPTY answer is an answer too — "this artist has no extended chart" must be remembered, or the artists that
    // produce it are exactly the ones that re-ask forever.
    [Fact]
    public async Task Full_EmptyChart_IsRemembered_AndNotReAsked()
    {
        using var h = new LadderHarness();
        Seed(h, Assembled(new[] { Chart("a", 900) }, DateTimeOffset.UtcNow));

        await Run(h, HydrationLevel.Full);
        await Run(h, HydrationLevel.Full);

        Assert.Single(h.Chart.Calls);
        Assert.NotEqual(default, h.Store.GetArtist(ArtistUri)!.ChartFetchedAt);
    }

    // …and a STALE chart stamp re-asks: the freshness half of the same gate.
    [Fact]
    public async Task Full_StaleChartStamp_ReFetches()
    {
        using var h = new LadderHarness();
        Seed(h, Assembled(new[] { Chart("a", 900) }, DateTimeOffset.UtcNow,
                          chartFetchedAt: DateTimeOffset.UtcNow - TimeSpan.FromHours(13)));   // > ArtistRichTtl (12 h)

        await Run(h, HydrationLevel.Full);

        Assert.Single(h.Chart.Calls);
    }

    // A chart write must not raise the OVERVIEW clock (and vice versa) — the whole reason these are two fields.
    [Fact]
    public async Task Full_ChartStamp_DoesNotDisturbTheOverviewStamp()
    {
        var overviewAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(3);
        using var h = new LadderHarness();
        Seed(h, Assembled(new[] { Chart("a", 900) }, overviewAt));

        await Run(h, HydrationLevel.Full);

        var artist = h.Store.GetArtist(ArtistUri)!;
        Assert.Equal(overviewAt, artist.OverviewFetchedAt);
        Assert.True(DateTimeOffset.UtcNow - artist.ChartFetchedAt < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Full_ChartFailure_KeepsTheOverviewSeed()
    {
        using var h = new LadderHarness();
        Seed(h, Assembled(new[] { Chart("a", 900) }, DateTimeOffset.UtcNow));
        h.Chart.Throw = true;

        await Run(h, HydrationLevel.Full);

        Assert.Equal(["spotify:track:a"], h.Store.GetArtist(ArtistUri)!.TopTracks!.Select(t => t.Uri));
        Assert.Empty(h.Traits.Calls);
        // …and a THROW is not an answer: the chart clock stays unstamped so the next ask really does retry.
        Assert.Equal(default, h.Store.GetArtist(ArtistUri)!.ChartFetchedAt);
    }

    // The same empty answer, from the other side: it costs no hydrate and no trait pass.
    [Fact]
    public async Task Full_EmptyChart_LeavesTheSeedAndAsksForNothingElse()
    {
        using var h = new LadderHarness();
        Seed(h, Assembled(new[] { Chart("a", 900) }, DateTimeOffset.UtcNow));

        await Run(h, HydrationLevel.Full);

        Assert.Single(h.Chart.Calls);
        Assert.Empty(h.Hydrator.Batches);
        Assert.Empty(h.Traits.Calls);
    }

    [Fact]
    public async Task Identity_RunsNoContinuation()
    {
        // Identity IS step 0 (ArtistV4) and nothing else — a chip or a queue label must cost no second transport.
        using var h = new LadderHarness();
        h.Store.UpsertArtist(new Artist("ar1", ArtistUri, "Ar1", null, TopAlbums: new[] { Card("al1", "") }, AlbumsTotal: 1));

        await Run(h, HydrationLevel.Identity);

        Assert.Empty(h.Hydrator.Batches);
        Assert.Empty(h.Envelopes.OverviewCalls);
        Assert.Empty(h.Chart.Calls);
    }

    /// <summary>A Rich pass must not collapse a chart a Full pass already extended.
    ///
    /// <para>The overview carries only the ~10-track seed and <c>StoreEntityMerge.Artist</c> takes a non-empty incoming
    /// <c>TopTracks</c> as authoritative. Within ONE pass that is fine (Full runs Rich first, so the chart step
    /// follows), but Rich and Full are two ledger keys and therefore two passes: <c>ArtistPage</c> asks Rich while its
    /// <c>ArtistPopular</c> child asks Full, and the Rich pass's overview write landing after the Full pass's chart
    /// write is exactly the "stats rewrote TopTracks under the chart's feet" bug this ladder exists to end. The seed's
    /// head — and its play counts — must still WIN; only the extended tail is folded back.</para></summary>
    [Fact]
    public async Task Rich_DoesNotCollapseAnExtendedChartBackToTheSeed()
    {
        using var h = new LadderHarness();
        // What a Full pass left behind: 12 rows, i.e. more than the overview seed cap.
        var extended = Enumerable.Range(0, 12).Select(i => Chart("x" + i, 100 + i)).ToArray();
        Seed(h, Assembled(extended));
        // …and a stale stamp, so the overview really re-runs.
        h.Store.UpsertArtist(h.Store.GetArtist(ArtistUri)! with
        {
            OverviewFetchedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(13),
            FetchedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(13),
        });
        h.Envelopes.OnOverview = _ => new Artist("ar1", ArtistUri, "Ar1", null, MonthlyListeners: 1000,
            TopTracks: new[] { Chart("x0", 999), Chart("fresh", 500) }, LatestRelease: Card("al1", "Al1"));

        await Run(h, HydrationLevel.Rich);

        var chart = h.Store.GetArtist(ArtistUri)!.TopTracks!;
        // The FRESH seed leads and keeps its counts…
        Assert.Equal(["spotify:track:x0", "spotify:track:fresh"], chart.Take(2).Select(t => t.Uri));
        Assert.Equal(999, chart[0].PlayCount);
        // …and the extended tail survived instead of being truncated to the two-row seed.
        Assert.Equal(13, chart.Count);
        Assert.Contains(chart, t => t.Uri == "spotify:track:x11");
        // Which is what keeps the rung where the Full pass put it, so the chart step is not re-run for nothing.
        Assert.Equal(HydrationLevel.Full, HydrationLevels.Of(h.Store.GetArtist(ArtistUri)));
    }
}
