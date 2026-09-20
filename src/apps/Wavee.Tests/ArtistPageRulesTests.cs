// ── Wavee.Tests/ArtistPageRulesTests.cs — the artist page's own pure decisions (ArtistSections) ───────────────────────
//
// New in 0.3 (Entities/Artist.UI.cs §3): each rule was a branch inside 0.2.9's ArtistPage.Body / TopBand / Biography, and
// each is pinned against the 0.2.9 behaviour ch 08 records (§1.1, W12, W18, W19, W23, the breakpoint table), plus the
// resolved-metrics wash forms that fix ch 08 §9's inconsistency #1.

using Xunit;

namespace Wavee.Tests;

public class ArtistPageRulesTests
{
    static ArtistPageFacts All(bool related = true) => new(
        Popular: true, Pick: true, Upcoming: true, LatestRelease: true, Albums: true, Singles: true, Compilations: true,
        AppearsOn: true, Tour: true, MusicVideos: true, Playlists: true, Concerts: true, Merch: true, Gallery: true,
        Related: related, Fans: true);

    static ArtistSection[] PlanOf(in ArtistPageFacts f)
    {
        var buffer = new ArtistSection[ArtistSections.Count];
        int n = ArtistSections.Plan(in f, buffer);
        return buffer[..n];
    }

    /// <summary>The fixed order of ArtistPage.cs:234-269, with the upcoming band between Top tracks and Latest release.</summary>
    [Fact]
    public void Plan_EverySection_InTheFixedOrder()
    {
        Assert.Equal(new[]
        {
            ArtistSection.Popular, ArtistSection.Upcoming, ArtistSection.LatestRelease, ArtistSection.Albums,
            ArtistSection.Singles, ArtistSection.Compilations, ArtistSection.AppearsOn, ArtistSection.Tour,
            ArtistSection.MusicVideos, ArtistSection.Playlists, ArtistSection.Concerts, ArtistSection.Merch,
            ArtistSection.Biography, ArtistSection.Gallery, ArtistSection.Related,
        }, PlanOf(All()));
    }

    /// <summary>W24: there is no empty artist — the minimum page is the biography alone.</summary>
    [Fact]
    public void Plan_NothingKnown_IsTheBiographyAlone()
        => Assert.Equal(new[] { ArtistSection.Biography }, PlanOf(default));

    /// <summary>"related" and "fans" are alternatives: Related wins, Fans only when Related is empty.</summary>
    [Fact]
    public void Plan_FansOnlyWhenRelatedIsEmpty()
    {
        var withRelated = PlanOf(All(related: true));
        Assert.Contains(ArtistSection.Related, withRelated);
        Assert.DoesNotContain(ArtistSection.Fans, withRelated);

        var fallback = PlanOf(All(related: false));
        Assert.DoesNotContain(ArtistSection.Related, fallback);
        Assert.Equal(ArtistSection.Fans, fallback[^1]);
        Assert.NotEqual(ArtistSections.Key(ArtistSection.Related), ArtistSections.Key(ArtistSection.Fans));
    }

    /// <summary>W12 / ch 08 §9 #9: the pick owns the rail, so an upcoming release takes its own band — but only beside a
    /// Top-tracks band; with no pick the upcoming card IS the rail and no band is planned.</summary>
    [Fact]
    public void UpcomingBand_OnlyWhenThePickOwnsTheRailBesideTopTracks()
    {
        Assert.True(ArtistSections.UpcomingBand(pick: true, popular: true, upcoming: true));
        Assert.False(ArtistSections.UpcomingBand(pick: false, popular: true, upcoming: true));
        Assert.False(ArtistSections.UpcomingBand(pick: true, popular: false, upcoming: true));
        Assert.False(ArtistSections.UpcomingBand(pick: true, popular: true, upcoming: false));

        Assert.True(ArtistSections.UpcomingInRail(pick: false, upcoming: true));
        Assert.False(ArtistSections.UpcomingInRail(pick: true, upcoming: true));
        Assert.DoesNotContain(ArtistSection.Upcoming, PlanOf(All() with { Pick = false }));
    }

    /// <summary>ch 08 §0 #10 / parity 20: only destinations join the pivot — never the upcoming band, Latest release,
    /// the tour banner, or Popular (the band title scrolls back to it; there is no Overview tab).</summary>
    [Fact]
    public void Pivot_HoldsDestinationsOnly()
    {
        Assert.False(ArtistSections.IsDestination(ArtistSection.Upcoming));
        Assert.False(ArtistSections.IsDestination(ArtistSection.LatestRelease));
        Assert.False(ArtistSections.IsDestination(ArtistSection.Tour));
        Assert.False(ArtistSections.IsDestination(ArtistSection.Popular));
        int destinations = 0;
        foreach (var s in PlanOf(All())) if (ArtistSections.IsDestination(s)) destinations++;
        Assert.Equal(11, destinations);
    }

    /// <summary>TopTracks.cs:17-27: wide at 760, held down to 736 once wide, re-entered only at 760.</summary>
    [Fact]
    public void TopBandWide_HoldsTwentyFourDipOnceWide()
    {
        Assert.True(ArtistSections.TopBandWide(760f, wasWide: false));
        Assert.False(ArtistSections.TopBandWide(759f, wasWide: false));
        Assert.True(ArtistSections.TopBandWide(736f, wasWide: true));
        Assert.False(ArtistSections.TopBandWide(735f, wasWide: true));
        Assert.True(ArtistSections.PreMeasureWidth >= ArtistSections.TopBandWideW);   // the first frame starts WIDE (parity 90)
    }

    /// <summary>ch 08 audit #4: a ≤ 5-track chart is ONE column at any width; never more than two.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 2)]
    [InlineData(10, 2)]
    [InlineData(50, 2)]
    public void ChartColumns_ClampByRowCount(int total, int expected)
        => Assert.Equal(expected, ArtistSections.ChartColumns(total));

    /// <summary>W18: the tiles keep their order and a zero stat drops its WHOLE tile.</summary>
    [Fact]
    public void FactTiles_DropZeroStats_KeepTheOrder()
    {
        var tiles = new ArtistFact[6];
        int n = ArtistSections.FactTiles(monthly: 20, followers: 0, albums: 6, singles: 0, concerts: 7, related: 12, tiles);
        Assert.Equal(4, n);
        Assert.Equal(
            new[] { ArtistFactKind.Monthly, ArtistFactKind.Albums, ArtistFactKind.Concerts, ArtistFactKind.Related },
            new[] { tiles[0].Kind, tiles[1].Kind, tiles[2].Kind, tiles[3].Kind });
        Assert.Equal(0, ArtistSections.FactTiles(0, 0, 0, 0, 0, 0, tiles));
    }

    /// <summary>ch 08 GAP 19: the followed artists in order, the page artist excluded, at most twelve.</summary>
    [Fact]
    public void Fans_ExcludeThePageArtist_CapAtTwelve()
    {
        int[] followed = [0, 5, 7, 5, 3, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19];
        var into = new int[ArtistSections.FansCap];
        int n = ArtistSections.Fans(followed, self: 5, into);
        Assert.Equal(12, n);
        Assert.Equal(7, into[0]);
        Assert.DoesNotContain(5, into[..n]);
        Assert.DoesNotContain(0, into[..n]);
    }

    /// <summary>W23 + 0.3's Retry: failed only when the overview is unknown, nobody is still asking, and its transport
    /// reported a failure.</summary>
    [Fact]
    public void PageFailed_OnlyWhenTheOverviewAskEndedInFailure()
    {
        Assert.True(ArtistSections.PageFailed(overviewKnown: false, overviewPending: false, EdgeState.Failed));
        Assert.False(ArtistSections.PageFailed(overviewKnown: false, overviewPending: true, EdgeState.Failed));
        Assert.False(ArtistSections.PageFailed(overviewKnown: true, overviewPending: false, EdgeState.Failed));
        Assert.False(ArtistSections.PageFailed(overviewKnown: false, overviewPending: false, EdgeState.Unknown));
    }

    /// <summary>TopTracks.cs:172-180: the live countdown only inside two weeks, and never for a release already out.</summary>
    [Fact]
    public void ShowsCountdown_InsideTwoWeeksOnly()
    {
        const long now = 1_800_000_000;
        Assert.True(ArtistSections.ShowsCountdown(now + 3600, now));
        Assert.True(ArtistSections.ShowsCountdown(now + ArtistSections.CountdownWindowSeconds, now));
        Assert.False(ArtistSections.ShowsCountdown(now + ArtistSections.CountdownWindowSeconds + 1, now));
        Assert.False(ArtistSections.ShowsCountdown(now, now));
        Assert.False(ArtistSections.ShowsCountdown(now - 60, now));
    }

    /// <summary>ch 08 §9 inconsistency #1: the wash sized from the RESOLVED metrics follows the hero's own hysteretic
    /// tier — at 870 a page that was Wide keeps the 440 hero AND the 440 + 96 wash, where the width-only form drops to
    /// Medium's 384 + 96.</summary>
    [Fact]
    public void BlendWash_FromResolvedMetrics_FollowsTheHysteresis()
    {
        var held = ArtistHeroLayout.For(870f, ArtistHeroTier.Wide);
        Assert.Equal(ArtistHeroTier.Wide, held.Tier);
        Assert.Equal(ArtistHeroLayout.WideHeight + ArtistHeroLayout.ContentBlendTail, ArtistHeroLayout.BlendBackdropHeightFor(in held));
        Assert.Equal(ArtistHeroLayout.MediumHeight + ArtistHeroLayout.ContentBlendTail, ArtistHeroLayout.BlendBackdropHeightFor(870f));

        // Outside every hysteresis band the two forms agree.
        var wide = ArtistHeroLayout.For(1200f, ArtistHeroTier.Wide);
        Assert.Equal(ArtistHeroLayout.BlendBackdropHeightFor(1200f), ArtistHeroLayout.BlendBackdropHeightFor(in wide));
        Assert.Equal(ArtistHeroLayout.BlendBoundaryFor(1200f), ArtistHeroLayout.BlendBoundaryFor(in wide), 4);
        Assert.Equal(ArtistHeroLayout.PageGutterFor(1200f), wide.Gutter);
    }
}
