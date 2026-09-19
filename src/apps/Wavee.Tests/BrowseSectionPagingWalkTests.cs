// ── Wavee.Tests/BrowseSectionPagingWalkTests.cs — the Charts drill walk end to end (Wave 5, owner P; 0.2.9 port) ─────
//
// A fake section source serves a scripted sequence of pages and `WalkAsync` below drives them through the EXACT fold
// the Charts drill uses: `BrowseSectionWalk.Begin` for the seed, then per page the view AFTER the page landed on the row
// (0.3's `ReplacePage`: the row's raw cursor becomes `offset + items this page`, its `NextOffset` the page's cursor) and
// `BrowseSectionWalk.Fold(before, after, requestedOffset)` for the verdict. No engine anywhere in this file.
//
// THE regression this pins: Weekly Song Charts (totalCount 74) used to stop at 20 items ("El Salvador") because nothing
// ever asked for offset 20. Walking the captured sequence (20/20/20/14) must land at all 74.
//
// 0.2.9's `BrowseSection.PagingComplete` is `SectionPaging.Complete`; its `HomeBrowseCards.Section(page, null)` seed is
// the view a landed first page reads as (cards, `max(total, cards)`, raw = cards).

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class BrowseSectionPagingWalkTests
{
    const string WeeklyUri = "spotify:section:weekly";

    /// <summary>One browseSection answer, as the walk sees it.</summary>
    sealed record Page(IReadOnlyList<HomeCard> Cards, int Total, int NextOffset);

    sealed class FakeSectionSource(IReadOnlyDictionary<int, Page?> pagesByOffset)
    {
        public readonly List<int> RequestedOffsets = new();
        public int? HoldOffset;
        public TaskCompletionSource<Page?>? Hold;

        public Task<Page?> GetSectionAsync(int offset)
        {
            RequestedOffsets.Add(offset);
            if (Hold is not null && offset == HoldOffset) return Hold.Task;
            return Task.FromResult(pagesByOffset.TryGetValue(offset, out var page) ? page : null);
        }
    }

    readonly HomeCard[] _cards;

    public BrowseSectionPagingWalkTests()
    {
        TestScope.Fresh();
        var specs = new HomeFixtures.Spec[74];
        for (int i = 0; i < specs.Length; i++) specs[i] = HomeFixtures.Playlist("spotify:playlist:walk-p" + i, "Track " + i);
        _cards = HomeFixtures.Cards("Weekly Song Charts", SectionKind.BrowseShelf, specs).ToArray();
        Assert.Equal(74, _cards.Length);
    }

    HomeCard[] Cards(int start, int count) => _cards.AsSpan(start, count).ToArray();

    static HomeSectionView Empty(string uri, string title) => new(Table.None, uri, title, null, Array.Empty<HomeCard>(), 0, 0);

    /// <summary>The seed a landed first page reads as (0.2.9 <c>HomeBrowseCards.Section</c>).</summary>
    static HomeSectionView Seed(Page page, string uri = WeeklyUri, string title = "Weekly Song Charts")
        => new(Table.None, uri, title, null, page.Cards, Math.Max(page.Total, page.Cards.Count), page.Cards.Count);

    /// <summary>The row's view once the page at <paramref name="offset"/> landed: cards appended with the dedupe, the raw
    /// cursor at offset + page items, the page's own cursor.</summary>
    static HomeSectionView Land(HomeSectionView before, int offset, Page page)
        => HomeSectionPaging.Append(before, page.Cards, page.Total) with
        {
            RawItemCount = offset + page.Cards.Count,
            NextOffset = page.NextOffset,
        };

    sealed record WalkResult(IReadOnlyList<HomeCard> Cards, bool Exhausted);

    static async Task<WalkResult> WalkAsync(FakeSectionSource svc, string uri, HomeSectionView? seed = null,
        Action<IReadOnlyList<HomeCard>>? onPublished = null)
    {
        var start = BrowseSectionWalk.Begin(seed);
        HomeSectionView current;
        int offset;
        if (start.FetchFirst)
        {
            current = Empty(uri, "Weekly");
            offset = 0;
        }
        else
        {
            current = start.Current!;
            offset = start.Offset;
            if (start.Publish) onPublished?.Invoke(current.Cards);
            if (start.Exhausted) return new WalkResult(current.Cards, true);
        }
        while (true)
        {
            var page = await svc.GetSectionAsync(offset);
            if (page is null || page.Cards.Count == 0) return new WalkResult(current.Cards, true);
            var step = BrowseSectionWalk.Fold(current, Land(current, offset, page), offset);
            current = step.Section;
            if (step.Exhausted) return new WalkResult(current.Cards, true);
            offset = step.NextOffset;
        }
    }

    Dictionary<int, Page?> WeeklyPages() => new()
    {
        [0] = new(Cards(0, 20), 74, 20),
        [20] = new(Cards(20, 20), 74, 40),
        [40] = new(Cards(40, 20), 74, 60),
        [60] = new(Cards(60, 14), 74, SectionPaging.Complete),
    };

    [Fact]
    public async Task Walk_TheCapturedWeeklyChartsSequence_Accumulates74CardsAcrossFourPages()
    {
        var svc = new FakeSectionSource(WeeklyPages());

        var result = await WalkAsync(svc, WeeklyUri);

        Assert.Equal(74, result.Cards.Count);
        Assert.True(result.Exhausted);
        Assert.Equal(new[] { 0, 20, 40, 60 }, svc.RequestedOffsets);
        Assert.Equal(74, result.Cards.Select(c => c.DedupeKey).Distinct().Count());
    }

    [Fact]
    public async Task Walk_FromASeededFirstPage_AsksOffset20AndReaches74()
    {
        var pages = WeeklyPages();
        var svc = new FakeSectionSource(pages);

        var result = await WalkAsync(svc, WeeklyUri, Seed(pages[0]!));

        Assert.Equal(74, result.Cards.Count);
        Assert.True(result.Exhausted);
        Assert.Equal(new[] { 20, 40, 60 }, svc.RequestedOffsets);
    }

    [Fact]
    public void Begin_WeeklySeedWithCards_PublishesSeedAndAsksOffset20()
    {
        var start = BrowseSectionWalk.Begin(Seed(WeeklyPages()[0]!));
        Assert.True(start.Publish);
        Assert.False(start.FetchFirst);
        Assert.False(start.Exhausted);
        Assert.Equal(20, start.Offset);
        Assert.Equal(20, start.Current!.Cards.Count);
    }

    [Fact]
    public void Begin_NoSeed_FetchesOffset0()
    {
        var start = BrowseSectionWalk.Begin(null);
        Assert.True(start.FetchFirst);
        Assert.False(start.Publish);
        Assert.Equal(0, start.Offset);
        Assert.Null(start.Current);
    }

    [Fact]
    public void Begin_EmptyHasMoreSeed_FetchesOffset0AndDoesNotPublish()
    {
        var seed = new HomeSectionView(Table.None, WeeklyUri, "Weekly Song Charts", null, Array.Empty<HomeCard>(), 74, 0);
        var start = BrowseSectionWalk.Begin(seed);
        Assert.False(start.Publish);
        Assert.True(start.FetchFirst);
        Assert.Equal(0, start.Offset);
        Assert.Null(start.Current);
    }

    [Fact]
    public void Begin_EmptyFinishedSeed_PublishesItAndStops()
    {
        var seed = new HomeSectionView(Table.None, WeeklyUri, "Weekly Song Charts", null, Array.Empty<HomeCard>(), 0, 0);
        var start = BrowseSectionWalk.Begin(seed);
        Assert.True(start.Publish);
        Assert.True(start.Exhausted);
        Assert.False(start.FetchFirst);
        Assert.Same(seed, start.Current);
    }

    [Fact]
    public void Begin_CompleteFeaturedSeed_PublishesAndAsksOffset4()
    {
        // A non-empty seed is never complete in Begin: Featured still publishes its 4 cards, then asks offset 4 once.
        var seed = new HomeSectionView(Table.None, "spotify:section:featured", "Featured Charts", null, Cards(0, 4), 4, 4);
        var start = BrowseSectionWalk.Begin(seed);
        Assert.True(start.Publish);
        Assert.False(start.Exhausted);
        Assert.False(start.FetchFirst);
        Assert.Equal(4, start.Offset);
        Assert.Equal(4, start.Current!.Cards.Count);
    }

    [Fact]
    public async Task Walk_FromAFeaturedSeed_AsksOffset4OnceAndStops()
    {
        var seed = new HomeSectionView(Table.None, "spotify:section:featured", "Featured Charts", null, Cards(0, 4), 4, 4);
        var svc = new FakeSectionSource(new Dictionary<int, Page?>
        {
            [4] = new(Array.Empty<HomeCard>(), 4, SectionPaging.Complete),
        });

        var result = await WalkAsync(svc, "spotify:section:featured", seed);

        Assert.Equal(4, result.Cards.Count);
        Assert.True(result.Exhausted);
        Assert.Equal(new[] { 4 }, svc.RequestedOffsets);
    }

    [Fact]
    public void Begin_UnderReportedTotal_StillAsksOffset20()
    {
        var seed = new HomeSectionView(Table.None, WeeklyUri, "Weekly Song Charts", null, Cards(0, 20), 20, 20);
        var start = BrowseSectionWalk.Begin(seed);
        Assert.True(start.Publish);
        Assert.False(start.Exhausted);
        Assert.False(start.FetchFirst);
        Assert.Equal(20, start.Offset);
        Assert.Equal(20, start.Current!.Cards.Count);
    }

    [Fact]
    public async Task Walk_FromASeededFirstPage_UnderReportedTotal_AsksOffset20AndReaches74()
    {
        var svc = new FakeSectionSource(WeeklyPages());
        var seed = new HomeSectionView(Table.None, WeeklyUri, "Weekly Song Charts", null, Cards(0, 20), 20, 20);

        var result = await WalkAsync(svc, WeeklyUri, seed);

        Assert.Equal(74, result.Cards.Count);
        Assert.True(result.Exhausted);
        Assert.Equal(new[] { 20, 40, 60 }, svc.RequestedOffsets);
    }

    [Fact]
    public async Task Walk_FromASeededFirstPage_PublishesSeedBeforeOffset20Returns()
    {
        var pages = WeeklyPages();
        var svc = new FakeSectionSource(pages)
        {
            HoldOffset = 20,
            Hold = new TaskCompletionSource<Page?>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        IReadOnlyList<HomeCard>? published = null;
        var walk = WalkAsync(svc, WeeklyUri, Seed(pages[0]!), cards => published = cards);

        Assert.NotNull(published);
        Assert.Equal(20, published!.Count);
        Assert.Equal(new[] { 20 }, svc.RequestedOffsets);

        svc.Hold!.SetResult(pages[20]);
        var result = await walk;
        Assert.Equal(74, result.Cards.Count);
        Assert.True(result.Exhausted);
    }

    [Fact]
    public async Task Walk_NextOffsetZero_LatchesExhaustedInsteadOfLoopingOnPageOne()
    {
        var svc = new FakeSectionSource(new Dictionary<int, Page?> { [0] = new(Cards(0, 6), 6, 0) });

        var result = await WalkAsync(svc, "spotify:section:s");

        Assert.Equal(6, result.Cards.Count);
        Assert.True(result.Exhausted);
        Assert.Single(svc.RequestedOffsets);
    }

    [Fact]
    public async Task Walk_AnAllDuplicatePage_StaysLatched_EvenWithAHealthyLookingCursor()
    {
        var repeated = Cards(0, 2);
        var svc = new FakeSectionSource(new Dictionary<int, Page?>
        {
            [0] = new(repeated, 40, 2),
            [2] = new(repeated, 40, 4),   // the same two entities again
        });

        var result = await WalkAsync(svc, "spotify:section:s");

        Assert.Equal(2, result.Cards.Count);
        Assert.True(result.Exhausted);
        Assert.Equal(new[] { 0, 2 }, svc.RequestedOffsets);
    }

    [Fact]
    public async Task Walk_ExplicitTerminator_StopsEvenThoughTotalClaimsMore()
    {
        var svc = new FakeSectionSource(new Dictionary<int, Page?> { [0] = new(Cards(0, 20), 74, SectionPaging.Complete) });

        var result = await WalkAsync(svc, "spotify:section:s");

        Assert.Equal(20, result.Cards.Count);
        Assert.True(result.Exhausted);
        Assert.Single(svc.RequestedOffsets);
    }

    [Fact]
    public async Task Walk_NoCursorAtAll_SynthesizesOffsetPlusCountVersusTotal()
    {
        // A page with no pagingInfo is the ONE case that falls back to offset + count vs the total.
        var svc = new FakeSectionSource(new Dictionary<int, Page?>
        {
            [0] = new(Cards(0, 10), 15, SectionPaging.NoCursor),
            [10] = new(Cards(10, 5), 15, SectionPaging.NoCursor),
        });

        var result = await WalkAsync(svc, "spotify:section:s");

        Assert.Equal(15, result.Cards.Count);
        Assert.True(result.Exhausted);
        Assert.Equal(new[] { 0, 10 }, svc.RequestedOffsets);
    }

    [Fact]
    public void Fold_AnEmptyLanding_IsExhaustedWithTheTerminator()
    {
        var before = Seed(WeeklyPages()[0]!);
        var step = BrowseSectionWalk.Fold(before, before, requestedOffset: 20);
        Assert.True(step.Exhausted);
        Assert.Equal(SectionPaging.Complete, step.NextOffset);
    }

    // ── the walk's PROGRESS reading ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WalkFraction_UntrustworthyTotal_IsNeverComplete()
    {
        var seed = new HomeSectionView(Table.None, WeeklyUri, "Weekly", null, Cards(0, 20), 20, 20);

        float frac = HomeSectionPaging.WalkFraction(seed, 20);

        Assert.True(frac < 1f, "an under-reported total must not report a finished walk");
        Assert.Equal(0.5f, frac, 3);
    }

    [Fact]
    public void WalkFraction_TrustworthyTotal_IsTheRealRatio()
    {
        var mid = new HomeSectionView(Table.None, WeeklyUri, "Weekly", null, Cards(0, 40), 74, 40);
        Assert.Equal(40f / 74f, HomeSectionPaging.WalkFraction(mid, 20), 3);
    }

    [Fact]
    public void WalkFraction_EmptySection_IsZero()
        => Assert.Equal(0f, HomeSectionPaging.WalkFraction(Empty(WeeklyUri, "Weekly"), 20));
}
