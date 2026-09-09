using FluentGpu.Controls;
using Xunit;

namespace Wavee.Tests;

// PagedShelf's two CARD-MOUNT decisions (FluentGpu.Controls/ShelfProbeMath.cs, source-included).
//
// The defect they exist to stop is a measured one. The artist magazine used to carry eight `measured: true`
// PagedShelves (music videos, playlists, concerts, merch, gallery, appears-on, fans-also-like/related) plus the
// chart. Those shelves now reserve via cardHeight; the probe path remains for any caller that still opts in. A measured shelf
// learns its strip height by mounting a bounded sample of REAL cards and measuring the tallest — and every one of
// those cards is a full ShelfCard + LazyNowPlayingOverlay + CoverShimmer + tooltip subtree. Mounting all eight
// shelves' whole samples in the ONE frame the artist's `extras` publication creates them put ~190 card subtrees into a
// single reconcile: the render census caught frames of 13–24 ms attributing 3.5–13 ms and 3–4.7 MB to
// `PagedShelfCore` alone, with ShelfCard×46…100 and LazyNowPlayingOverlay×46…100 realized in that frame.
//
// Both fixes are policy, so both are pinned here as VALUES rather than as a screenshot or a frame time:
//   • ShelfProbeMath — the sample is taken in chunk-sized passes across frames, and the END STATE must be identical
//     to the eager pass it replaces (same cells, same max, same lock).
//   • ShelfViewportBand — a shelf far below the page's scroll window mounts no cards at all, and a gate that cannot
//     see where it is never withholds anything.
//
// Not tested here: PagedShelfCore itself. It needs a mounted scene, a scroll kernel and a layout pass to probe at all;
// this assembly deliberately does not reference FluentGpu.Controls (only pure files are source-included), and faking a
// SceneStore would pin the fake. The control consults these two classes for the whole decision.
public class ShelfMountBudgetTests
{
    // ── the sample size: bounded, count-driven ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Target_IsTheItemCountUntilTheSampleCap()
    {
        Assert.Equal(0, ShelfProbeMath.Target(0));
        Assert.Equal(0, ShelfProbeMath.Target(-3));
        Assert.Equal(1, ShelfProbeMath.Target(1));
        Assert.Equal(16, ShelfProbeMath.Target(16));
        Assert.Equal(ShelfProbeMath.SampleCap, ShelfProbeMath.Target(ShelfProbeMath.SampleCap));
    }

    [Fact]
    public void Target_NeverRealizesAnUnboundedCatalog()
        => Assert.Equal(ShelfProbeMath.SampleCap, ShelfProbeMath.Target(100_000));

    // ── the per-pass budget ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FirstSample_IsOneChunk()
        => Assert.Equal(ShelfProbeMath.Chunk, ShelfProbeMath.FirstSample(ShelfProbeMath.SampleCap));

    [Fact]
    public void FirstSample_NeverExceedsTheTarget()
    {
        // The common measured shelf is a handful of cards: it must complete in its FIRST pass, so a small shelf costs
        // exactly what it cost before the progression existed — one mount pass, no continuation, no extra frame.
        Assert.Equal(2, ShelfProbeMath.FirstSample(2));
        Assert.True(ShelfProbeMath.IsComplete(ShelfProbeMath.FirstSample(2), 2));
        Assert.Equal(0, ShelfProbeMath.FirstSample(0));
    }

    [Fact]
    public void NextSample_AdvancesByOneChunkAndSaturates()
    {
        int target = ShelfProbeMath.Target(16);
        int mounted = ShelfProbeMath.FirstSample(target);
        Assert.Equal(4, mounted);
        mounted = ShelfProbeMath.NextSample(mounted, target);
        Assert.Equal(8, mounted);
        mounted = ShelfProbeMath.NextSample(mounted, target);
        Assert.Equal(12, mounted);
        mounted = ShelfProbeMath.NextSample(mounted, target);
        Assert.Equal(16, mounted);
        // Saturated: a continuation that fires again cannot walk past the sample.
        Assert.Equal(16, ShelfProbeMath.NextSample(mounted, target));
    }

    [Fact]
    public void NextSample_IsMonotonic_SoAContinuationNeverUnmountsAMeasuredCell()
    {
        // Shrinking the prefix would unmount a keyed cell mid-progression and throw away its measurement — the running
        // max would then be taken over fewer cards than the eager pass measured.
        int target = ShelfProbeMath.Target(20);
        for (int mounted = 0; mounted <= target; mounted++)
            Assert.True(ShelfProbeMath.NextSample(mounted, target) >= mounted);
    }

    [Fact]
    public void NextSample_FromNothing_StartsAFreshProgression()
    {
        int target = ShelfProbeMath.Target(20);
        Assert.Equal(ShelfProbeMath.FirstSample(target), ShelfProbeMath.NextSample(0, target));
        Assert.Equal(ShelfProbeMath.FirstSample(target), ShelfProbeMath.NextSample(-5, target));
    }

    [Fact]
    public void EmptyShelf_NeverProbes()
    {
        Assert.Equal(0, ShelfProbeMath.NextSample(0, 0));
        Assert.Equal(0, ShelfProbeMath.Passes(0));
        // "Nothing to sample" must read as COMPLETE, or the control would keep a pass in flight forever on an empty
        // shelf and never drop its (nonexistent) cells.
        Assert.True(ShelfProbeMath.IsComplete(0, 0));
    }

    // ── the progression terminates, and lands on exactly the eager sample ────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(500)]
    public void Progression_EndsAtTheEagerSample_InTheAdvertisedNumberOfPasses(int count)
    {
        int target = ShelfProbeMath.Target(count);
        int mounted = ShelfProbeMath.FirstSample(target);
        int passes = 1;
        while (!ShelfProbeMath.IsComplete(mounted, target))
        {
            mounted = ShelfProbeMath.NextSample(mounted, target);
            passes++;
            Assert.True(passes <= ShelfProbeMath.SampleCap, "the progression must terminate");
        }
        // The END STATE is what makes the chunking free: the completed progression measured the SAME cells an eager
        // whole-sample pass would have measured, so the locked height is unchanged — only the frames differ.
        Assert.Equal(target, mounted);
        Assert.Equal(ShelfProbeMath.Passes(count), passes);
    }

    [Fact]
    public void Passes_BoundsTheWholeArtistPageProbeCost()
    {
        // Eight measured shelves × a 16-item cap: the whole page's sample is spread over at most `Passes` frames, and
        // no single frame mounts more than Chunk cards per shelf. That product is the budget the census needed.
        Assert.Equal(4, ShelfProbeMath.Passes(16));
        Assert.Equal(6, ShelfProbeMath.Passes(ShelfProbeMath.SampleCap));
        Assert.Equal(1, ShelfProbeMath.Passes(ShelfProbeMath.Chunk));
    }

    [Fact]
    public void IsComplete_OnlyOnceTheWholeSampleIsCovered()
    {
        Assert.False(ShelfProbeMath.IsComplete(4, 16));
        Assert.False(ShelfProbeMath.IsComplete(15, 16));
        Assert.True(ShelfProbeMath.IsComplete(16, 16));
        Assert.True(ShelfProbeMath.IsComplete(17, 16));   // saturated prefix still reads complete
    }

    // ── the viewport band ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Band_IsAWholeViewportOnEachSide()
    {
        Assert.Equal(900f, ShelfViewportBand.Lookahead(900f));
        // …with a floor, so a docked/compact window still looks a useful distance ahead.
        Assert.Equal(ShelfViewportBand.MinLookahead, ShelfViewportBand.Lookahead(120f));
        Assert.Equal(ShelfViewportBand.MinLookahead, ShelfViewportBand.Lookahead(0f));
        Assert.Equal(ShelfViewportBand.MinLookahead, ShelfViewportBand.Lookahead(float.NaN));
    }

    [Fact]
    public void ShelfInTheViewport_MountsItsCards()
    {
        // scroll window [0, 800); the shelf occupies [100, 400).
        Assert.True(ShelfViewportBand.Intersects(100f, 300f, 0f, 800f, ShelfViewportBand.Lookahead(800f)));
    }

    [Fact]
    public void ShelfJustBelowTheFold_MountsItsCards()
    {
        // One viewport of lookahead: a shelf at 1200 with the window at [0, 800) is inside the band, so the user can
        // never scroll into an empty band.
        Assert.True(ShelfViewportBand.Intersects(1200f, 300f, 0f, 800f, ShelfViewportBand.Lookahead(800f)));
    }

    [Fact]
    public void ShelfFarBelowTheFold_MountsNothing()
    {
        // 4000 DIP down with an 800 DIP viewport: past the window (800) + the lookahead (800). THIS is the artist
        // page's eight shelves at page open — every one of them below the discography grids.
        Assert.False(ShelfViewportBand.Intersects(4000f, 300f, 0f, 800f, ShelfViewportBand.Lookahead(800f)));
    }

    [Fact]
    public void ShortContentHeight_DoesNotLatchABelowFoldShelf()
    {
        // First layout often reports contentH == viewportH because later sections have not reserved yet.
        // The gate must still withhold a shelf whose own reserved box is past the band — latching on
        // "the page appears to fit" was the artist first-content 9-shelf flush.
        Assert.False(ShelfViewportBand.ShouldLatch(4000f, 300f, 0f, 800f));
        Assert.True(ShelfViewportBand.ShouldLatch(100f, 300f, 0f, 800f));
    }

    [Fact]
    public void ShelfFarAboveTheScrollWindow_MountsNothing()
    {
        // Scrolled to 6000: a shelf at [100, 400) is a whole page behind the window and behind the band.
        Assert.False(ShelfViewportBand.Intersects(100f, 300f, 6000f, 800f, ShelfViewportBand.Lookahead(800f)));
    }

    [Fact]
    public void ScrollingDown_BringsTheShelvesInOneAtATime()
    {
        // The stagger the gate buys for free: at any one offset only the shelves near the window are mounted, so the
        // per-frame card mass is a shelf or two, never the page.
        float[] tops = [0f, 900f, 1800f, 2700f, 3600f, 4500f, 5400f, 6300f];
        int MountedAt(float offset)
        {
            int n = 0;
            foreach (float top in tops)
                if (ShelfViewportBand.Intersects(top, 300f, offset, 800f, ShelfViewportBand.Lookahead(800f))) n++;
            return n;
        }
        Assert.True(MountedAt(0f) <= 3);
        Assert.True(MountedAt(3000f) <= 4);
        // …and every shelf is reachable: each one is in band at some offset on the way down.
        foreach (float top in tops)
            Assert.True(ShelfViewportBand.Intersects(top, 300f, top, 800f, ShelfViewportBand.Lookahead(800f)));
    }

    [Fact]
    public void UnknownGeometry_NeverWithholdsContent()
    {
        // A gate that cannot see where it is must answer TRUE — a blank shelf is a defect, a mounted one is only cost.
        Assert.True(ShelfViewportBand.Intersects(4000f, 300f, 0f, 0f, 800f));            // viewport not measured yet
        Assert.True(ShelfViewportBand.Intersects(4000f, 300f, 0f, 1f, 800f));            // degenerate viewport
        Assert.True(ShelfViewportBand.Intersects(4000f, 300f, 0f, float.NaN, 800f));     // NaN viewport
        Assert.True(ShelfViewportBand.Intersects(float.NaN, 300f, 0f, 800f, 800f));      // unresolved position
        Assert.True(ShelfViewportBand.Intersects(4000f, 300f, float.NaN, 800f, 800f));   // unresolved offset
    }

    [Fact]
    public void UnmeasuredHeight_IsTreatedAsAPointNotAsNaN()
    {
        // A shelf whose height has not landed yet is a zero-height box at its top, NOT an unanswerable comparison:
        // NaN arithmetic would make every test false and gate the shelf off forever.
        Assert.True(ShelfViewportBand.Intersects(100f, float.NaN, 0f, 800f, 800f));
        Assert.False(ShelfViewportBand.Intersects(4000f, float.NaN, 0f, 800f, 800f));
        Assert.True(ShelfViewportBand.Intersects(100f, -50f, 0f, 800f, 800f));
    }

    [Fact]
    public void NegativeOrNonFiniteLookahead_DegradesToNoBandNotToNaN()
    {
        // The window itself still counts: a shelf inside [offset, offset+viewportH) mounts even with no band at all.
        Assert.True(ShelfViewportBand.Intersects(100f, 300f, 0f, 800f, -100f));
        Assert.False(ShelfViewportBand.Intersects(2000f, 300f, 0f, 800f, -100f));
        Assert.True(ShelfViewportBand.Intersects(100f, 300f, 0f, 800f, float.NaN));
    }

    [Fact]
    public void ShelfStraddlingTheBandEdge_Mounts()
    {
        // Its BOTTOM reaches the band, so it mounts: the test is an intersection, not a containment.
        float lookahead = ShelfViewportBand.Lookahead(800f);
        Assert.True(ShelfViewportBand.Intersects(-700f, 300f, 0f, 800f, lookahead));   // bottom at -400, band top -800
        Assert.False(ShelfViewportBand.Intersects(-1400f, 300f, 0f, 800f, lookahead)); // bottom at -1100, past it
    }
}
