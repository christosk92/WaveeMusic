using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// End-to-end paging-walk coverage for the browseSection "Show all" axis (style of ShowEpisodePagingTests): a fake
// IBrowseService serves a scripted sequence of pages and WalkAsync below drives them through the EXACT same
// arithmetic HomeSectionPage.LoadMoreAsync / BrowsePage.LoadMoreAsync use — HomeSectionPaging.BrowseSectionNextOffset
// for the cursor, HomeSectionPaging.CanAdvance + a dedup-progress check for termination — with no FluentGpu engine
// dependency (no Component, no Signal, no Element) anywhere in this file.
//
// THE regression this pins: Weekly Song Charts (totalCount 74) used to stop at 20 items ("El Salvador") because
// nothing ever asked for offset 20. Walking the real captured sequence (20/20/20/14) must land at all 74.
public class BrowseSectionPagingWalkTests
{
    sealed class FakeBrowseService : IBrowseService
    {
        readonly IReadOnlyDictionary<int, BrowseSection?> _pagesByOffset;
        public readonly List<int> RequestedOffsets = new();
        public int? HoldOffset;
        public TaskCompletionSource<BrowseSection?>? Hold;

        public FakeBrowseService(IReadOnlyDictionary<int, BrowseSection?> pagesByOffset) => _pagesByOffset = pagesByOffset;

        public Task<IReadOnlyList<BrowseCategory>> GetCategoriesAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<BrowsePageModel?> GetPageAsync(string pageUri, int sectionOffset = 0, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<BrowseSection?> GetSectionAsync(string sectionUri, int offset, CancellationToken ct = default)
        {
            RequestedOffsets.Add(offset);
            if (Hold is not null && offset == HoldOffset)
                return Hold.Task;
            return Task.FromResult(_pagesByOffset.TryGetValue(offset, out var page) ? page : null);
        }
    }

    static BrowseCard[] Cards(int start, int count) =>
        Enumerable.Range(start, count).Select(i => new BrowseCard("spotify:playlist:p" + i, "Track " + i, null, null)).ToArray();

    sealed record WalkResult(IReadOnlyList<HomeCard> Cards, bool Exhausted);

    // Production fold: HomeSectionPage.WalkChartsAsync / BrowseSectionWalk.Begin + Fold.
    static async Task<WalkResult> WalkAsync(IBrowseService svc, string sectionUri, HomeSection? seed = null,
                                           Action<IReadOnlyList<HomeCard>>? onPublished = null)
    {
        var start = BrowseSectionWalk.Begin(seed);
        HomeSection current;
        int offset;
        if (start.FetchFirst)
        {
            current = new HomeSection(sectionUri, "Weekly", null, Array.Empty<HomeCard>(), 0, 0);
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
            var page = await svc.GetSectionAsync(sectionUri, offset);
            if (page is null || page.Cards.Count == 0) return new WalkResult(current.Cards, true);
            var step = BrowseSectionWalk.Fold(current, offset, page);
            current = step.Section;
            if (step.Exhausted) return new WalkResult(current.Cards, true);
            offset = step.NextOffset!.Value;
        }
    }

    // THE regression: the exact captured sequence (20/20/20/14, totalCount 74) must accumulate all 74 cards and walk
    // the cursor 0 → 20 → 40 → 60 before exhausting on the explicit terminator.
    [Fact]
    public async Task Walk_TheCapturedWeeklyChartsSequence_Accumulates74CardsAcrossFourPages()
    {
        var svc = new FakeBrowseService(new Dictionary<int, BrowseSection?>
        {
            [0] = new("spotify:section:weekly", "Weekly Song Charts", BrowseSectionKind.Shelf, Cards(0, 20), [], 74, 20),
            [20] = new("spotify:section:weekly", "Weekly Song Charts", BrowseSectionKind.Shelf, Cards(20, 20), [], 74, 40),
            [40] = new("spotify:section:weekly", "Weekly Song Charts", BrowseSectionKind.Shelf, Cards(40, 20), [], 74, 60),
            [60] = new("spotify:section:weekly", "Weekly Song Charts", BrowseSectionKind.Shelf, Cards(60, 14), [], 74, BrowseSection.PagingComplete),
        });

        var result = await WalkAsync(svc, "spotify:section:weekly");

        Assert.Equal(74, result.Cards.Count);
        Assert.True(result.Exhausted);
        Assert.Equal(new[] { 0, 20, 40, 60 }, svc.RequestedOffsets);
        // …and every card exactly once: a page boundary that double-counted would show up here as duplicates.
        Assert.Equal(74, result.Cards.Select(c => c.Uri).Distinct().Count());
    }

    [Fact]
    public async Task Walk_FromASeededFirstPage_AsksOffset20AndReaches74()
    {
        var pages = WeeklyPages();
        var svc = new FakeBrowseService(pages);
        var seed = HomeBrowseCards.Section(pages[0]!, null);

        var result = await WalkAsync(svc, "spotify:section:weekly", seed);

        Assert.Equal(74, result.Cards.Count);
        Assert.True(result.Exhausted);
        Assert.Equal(new[] { 20, 40, 60 }, svc.RequestedOffsets);
    }

    [Fact]
    public void Begin_WeeklySeedWithCards_PublishesSeedAndAsksOffset20()
    {
        var seed = HomeBrowseCards.Section(WeeklyPages()[0]!, null);
        var start = BrowseSectionWalk.Begin(seed);
        Assert.True(start.Publish);
        Assert.False(start.FetchFirst);
        Assert.False(start.Exhausted);
        Assert.Equal(20, start.Offset);
        Assert.Equal(20, start.Current!.Cards.Count);
    }

    [Fact]
    public void Begin_EmptyHasMoreSeed_FetchesOffset0AndDoesNotPublish()
    {
        var seed = new HomeSection("spotify:section:weekly", "Weekly Song Charts", null, Array.Empty<HomeCard>(), 74, 0);
        var start = BrowseSectionWalk.Begin(seed);
        Assert.False(start.Publish);
        Assert.True(start.FetchFirst);
        Assert.Equal(0, start.Offset);
        Assert.Null(start.Current);
    }

    [Fact]
    public void Begin_CompleteFeaturedSeed_PublishesAndAsksOffset4()
    {
        // A non-empty seed is never complete in Begin — HomeSection dropped the browse cursor. Featured still
        // publishes the 4 cards, then the walk asks offset 4 once and Fold / empty / PagingComplete stop it.
        var cards = Cards(0, 4).Select(c => HomeBrowseCards.Card(c)).ToArray();
        var seed = new HomeSection("spotify:section:featured", "Featured Charts", null, cards, 4, 4);
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
        var seedCards = Cards(0, 4).Select(c => HomeBrowseCards.Card(c)).ToArray();
        var seed = new HomeSection("spotify:section:featured", "Featured Charts", null, seedCards, 4, 4);
        var svc = new FakeBrowseService(new Dictionary<int, BrowseSection?>
        {
            [4] = new("spotify:section:featured", "Featured Charts", BrowseSectionKind.Shelf, [], [], 4,
                BrowseSection.PagingComplete),
        });

        var result = await WalkAsync(svc, "spotify:section:featured", seed);

        Assert.Equal(4, result.Cards.Count);
        Assert.True(result.Exhausted);
        Assert.Equal(new[] { 4 }, svc.RequestedOffsets);
    }

    [Fact]
    public void Begin_UnderReportedTotal_StillAsksOffset20()
    {
        // Live offset-0 often looks like 20 of 20 once HomeBrowseCards.Section drops nextOffset. HasMore(seed)
        // is then false; Begin must still ask offset 20 or Weekly freezes at El Salvador.
        var seed = new HomeSection("spotify:section:weekly", "Weekly Song Charts", null,
            Cards(0, 20).Select(c => HomeBrowseCards.Card(c)).ToArray(), 20, 20);
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
        var pages = WeeklyPages();
        var svc = new FakeBrowseService(pages);
        var seed = new HomeSection(pages[0]!.Uri, pages[0]!.Title, null,
            pages[0]!.Cards.Select(c => HomeBrowseCards.Card(c)).ToArray(), 20, 20);

        var result = await WalkAsync(svc, "spotify:section:weekly", seed);

        Assert.Equal(74, result.Cards.Count);
        Assert.True(result.Exhausted);
        Assert.Equal(new[] { 20, 40, 60 }, svc.RequestedOffsets);
    }

    // THE UI handoff: offset 20 is in-flight, the published section must already be the 20 seed cards. WalkChartsAsync
    // used to skip SetReady(seed) on this path, so Weekly Song Charts stayed blank until (or unless) page 2 landed.
    [Fact]
    public async Task Walk_FromASeededFirstPage_PublishesSeedBeforeOffset20Returns()
    {
        var pages = WeeklyPages();
        var svc = new FakeBrowseService(pages)
        {
            HoldOffset = 20,
            Hold = new TaskCompletionSource<BrowseSection?>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var seed = HomeBrowseCards.Section(pages[0]!, null);
        IReadOnlyList<HomeCard>? published = null;
        var walk = WalkAsync(svc, "spotify:section:weekly", seed, cards => published = cards);

        // Begin publishes synchronously, then the walk awaits offset 20 — the seed must already be the visible section.
        Assert.NotNull(published);
        Assert.Equal(20, published.Count);
        Assert.Equal(new[] { 20 }, svc.RequestedOffsets);

        svc.Hold!.SetResult(pages[20]);
        // Remaining pages (40, 60) are immediate so the walk can finish.
        var result = await walk;
        Assert.Equal(74, result.Cards.Count);
        Assert.True(result.Exhausted);
    }

    static Dictionary<int, BrowseSection?> WeeklyPages() => new()
    {
        [0] = new("spotify:section:weekly", "Weekly Song Charts", BrowseSectionKind.Shelf, Cards(0, 20), [], 74, 20),
        [20] = new("spotify:section:weekly", "Weekly Song Charts", BrowseSectionKind.Shelf, Cards(20, 20), [], 74, 40),
        [40] = new("spotify:section:weekly", "Weekly Song Charts", BrowseSectionKind.Shelf, Cards(40, 20), [], 74, 60),
        [60] = new("spotify:section:weekly", "Weekly Song Charts", BrowseSectionKind.Shelf, Cards(60, 14), [], 74, BrowseSection.PagingComplete),
    };

    // Pathological terminator #1: a "complete" answer of nextOffset: 0 must not be honoured as a cursor (0 is not
    // > the requested offset), even on the very first page — CanAdvance(0, 0) is false, so the walk stops after one
    // request instead of re-asking offset 0 forever.
    [Fact]
    public async Task Walk_NextOffsetZero_LatchesExhaustedInsteadOfLoopingOnPageOne()
    {
        var svc = new FakeBrowseService(new Dictionary<int, BrowseSection?>
        {
            [0] = new("spotify:section:s", "Section", BrowseSectionKind.Shelf, Cards(0, 6), [], 6, 0),
        });

        var result = await WalkAsync(svc, "spotify:section:s");

        Assert.Equal(6, result.Cards.Count);
        Assert.True(result.Exhausted);
        Assert.Single(svc.RequestedOffsets);   // never re-requested offset 0
    }

    // Pathological terminator #2: the second page's cursor looks perfectly healthy (a forward-advancing value), but
    // every item it returned was already seen — the dedup-progress check must stop the walk anyway, or a server that
    // repeats a page forever would spin this loop forever too.
    [Fact]
    public async Task Walk_AnAllDuplicatePage_StaysLatched_EvenWithAHealthyLookingCursor()
    {
        var repeated = Cards(0, 2);
        var svc = new FakeBrowseService(new Dictionary<int, BrowseSection?>
        {
            [0] = new("spotify:section:s", "Section", BrowseSectionKind.Shelf, repeated, [], 40, 2),
            [2] = new("spotify:section:s", "Section", BrowseSectionKind.Shelf, repeated, [], 40, 4),   // same 2 URIs again
        });

        var result = await WalkAsync(svc, "spotify:section:s");

        Assert.Equal(2, result.Cards.Count);        // the duplicates never landed a second time
        Assert.True(result.Exhausted);
        Assert.Equal(new[] { 0, 2 }, svc.RequestedOffsets);   // asked once more, then stopped — not forever
    }

    // An explicit PagingComplete terminator wins even when Total still claims more than what has landed — the same
    // guarantee HomeSectionPagingTests pins for BrowseSectionNextOffset directly, exercised here end to end through
    // the walk loop.
    [Fact]
    public async Task Walk_ExplicitTerminator_StopsEvenThoughTotalClaimsMore()
    {
        var svc = new FakeBrowseService(new Dictionary<int, BrowseSection?>
        {
            [0] = new("spotify:section:s", "Section", BrowseSectionKind.Shelf, Cards(0, 20), [], 74, BrowseSection.PagingComplete),
        });

        var result = await WalkAsync(svc, "spotify:section:s");

        Assert.Equal(20, result.Cards.Count);
        Assert.True(result.Exhausted);
        Assert.Single(svc.RequestedOffsets);
    }

    // ── The walk's PROGRESS reading ──────────────────────────────────────────────────────────────────────────────
    // Same premise as Begin's: totalCount under-reports, and HomeBrowseCards.Section floors it at the card count. So a
    // total that does not EXCEED what we hold must never read as a finished walk — that is what painted a full bar on
    // the 20-card Weekly seed while offsets 20/40/60 were still to come.
    [Fact]
    public void WalkFraction_UntrustworthyTotal_IsNeverComplete()
    {
        var seed = new HomeSection("spotify:section:weekly", "Weekly", null,
            Cards(0, 20).Select(c => HomeBrowseCards.Card(c)).ToArray(), 20, 20);

        float frac = HomeSectionPaging.WalkFraction(seed, 20);

        Assert.True(frac < 1f, "an under-reported total must not report a finished walk");
        Assert.Equal(0.5f, frac, 3);                         // 20 held / (20 + one assumed page)
    }

    [Fact]
    public void WalkFraction_TrustworthyTotal_IsTheRealRatio()
    {
        var mid = new HomeSection("spotify:section:weekly", "Weekly", null,
            Cards(0, 40).Select(c => HomeBrowseCards.Card(c)).ToArray(), 74, 40);

        Assert.Equal(40f / 74f, HomeSectionPaging.WalkFraction(mid, 20), 3);
    }

    // A section with nothing in it yet reads 0, not NaN — the bar mounts before the first page lands.
    [Fact]
    public void WalkFraction_EmptySection_IsZero()
    {
        var empty = new HomeSection("spotify:section:weekly", "Weekly", null, Array.Empty<HomeCard>(), 0, 0);

        Assert.Equal(0f, HomeSectionPaging.WalkFraction(empty, 20));
    }
}
