using System;
using System.Collections.Generic;
using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The UNIFIED detail hero's pure arithmetic, driven as a width LADDER.
///
/// <para>Modelled on <c>MergedChromeLayoutTests</c> / <c>ContextBandLayoutTests</c> for the same reason they are: the
/// defect class here is a hero that behaves differently at two widths a few DIP apart, and the properties that
/// actually matter during a resize drag — monotonicity, floors that hold, a flow that does not chatter on its own
/// seam — can only be shown by walking the ladder, not by spot-checking three widths.</para>
///
/// <para>What is NOT here any more, deliberately: the <c>DetailHeroOrientation</c> enum and its three-variant ladder.
/// The hero has ONE composition; width chooses sizes and one flow axis, never a different design.</para>
/// </summary>
public class DetailVerticalLayoutTests
{
    // The ladder every walk below uses: 1-DIP steps through the whole band a detail page can actually be, from an
    // ultra-narrow snap layout to a maximised window with the Hero page layout forced on.
    const float LadderMin = 240f, LadderMax = 1400f;

    [Fact]
    public void PageLayoutConstants_MirrorPersistedSettingValues()
    {
        Assert.Equal(0, DetailVerticalLayout.PageAuto);
        Assert.Equal(1, DetailVerticalLayout.PageHero);
    }

    // ── the ONE breakpoint: stacked ↔ row flow ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(240f, false)]
    [InlineData(400f, false)]
    [InlineData(423f, false)]
    [InlineData(424f, true)]
    [InlineData(820f, true)]
    [InlineData(1400f, true)]
    public void RowFlow_TurnsOnAtOneWidth(float w, bool expected)
        => Assert.Equal(expected, DetailVerticalLayout.RowFlow(w));

    [Fact]
    public void RowFlow_Unmeasured_UsesTheFallbackWidth()
    {
        Assert.Equal(DetailVerticalLayout.RowFlow(DetailVerticalLayout.FallbackW),
                     DetailVerticalLayout.RowFlow(0f));
        Assert.Equal(DetailVerticalLayout.RowFlow(DetailVerticalLayout.FallbackW),
                     DetailVerticalLayout.RowFlow(-1f, current: false, initialized: true));
    }

    /// <summary>A resize grip parked ON the seam must not flip the composition every frame. Entering row flow needs
    /// 424; leaving it needs a further 24-DIP drop (to 400) — the same asymmetry the page-mode ladder uses.</summary>
    [Fact]
    public void RowFlow_UsesResizeHysteresis()
    {
        Assert.False(DetailVerticalLayout.RowFlow(423f, current: false, initialized: true));
        Assert.True(DetailVerticalLayout.RowFlow(424f, current: false, initialized: true));
        // …and once beside, it holds through the dip band.
        Assert.True(DetailVerticalLayout.RowFlow(423f, current: true, initialized: true));
        Assert.True(DetailVerticalLayout.RowFlow(DetailVerticalLayout.RowFlowLeaveW, current: true, initialized: true));
        Assert.False(DetailVerticalLayout.RowFlow(DetailVerticalLayout.RowFlowLeaveW - 1f, current: true, initialized: true));
    }

    /// <summary>Before the first measure there is nothing to be hysteretic about — the seed is a construction default,
    /// not a flow the visitor has seen — so the first real width is taken outright.</summary>
    [Fact]
    public void RowFlow_FirstMeasureIgnoresTheSeed()
    {
        Assert.True(DetailVerticalLayout.RowFlow(700f, current: false, initialized: false));
        Assert.False(DetailVerticalLayout.RowFlow(360f, current: true, initialized: false));
    }

    /// <summary>Walk the whole ladder with hysteresis armed, in BOTH directions: the flow must flip at most once per
    /// sweep. Two flips in one direction is a ladder that oscillates.</summary>
    [Fact]
    public void RowFlow_WalksTheLadderWithoutChattering()
    {
        Assert.Equal(1, FlipsWhileWalking(LadderMin, LadderMax));
        Assert.Equal(1, FlipsWhileWalking(LadderMax, LadderMin));

        static int FlipsWhileWalking(float from, float to)
        {
            float step = from < to ? 1f : -1f;
            bool flow = DetailVerticalLayout.RowFlow(from);
            int flips = 0;
            for (float w = from; from < to ? w <= to : w >= to; w += step)
            {
                bool next = DetailVerticalLayout.RowFlow(w, flow, initialized: true);
                if (next != flow) flips++;
                flow = next;
            }
            return flips;
        }
    }

    // ── artwork ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(240f, 208f)]    // narrow pad (16) → the cover fills the column
    [InlineData(340f, 280f)]    // …until the 280 cap
    [InlineData(420f, 280f)]
    [InlineData(539f, 280f)]
    public void ArtworkFor_Stacked_FillsTheColumnUpToTheCap(float w, float expected)
        => Assert.Equal(expected, DetailVerticalLayout.ArtworkFor(w, rowFlow: false));

    [Theory]
    [InlineData(400f, 144f)]    // RowFlowLeaveW: 0.44 × (400 − 48 − 24) = 144.32 → 144, exactly the floor
    [InlineData(424f, 155f)]    // RowFlowEnterW: 0.44 × 352 = 154.88 → 155
    [InlineData(469f, 175f)]    // the reported compact case: 0.44 × 397 = 174.7 → 175
    [InlineData(540f, 206f)]    // 0.44 × 468 = 205.9 → 206 (was 200 under the old 0.34 fraction)
    [InlineData(617f, 240f)]    // 0.44 × 545 = 239.8 → 240, the new cap crossing (was 820 under the old fraction)
    [InlineData(1400f, 240f)]
    public void ArtworkFor_RowFlow_StaysInsideTheBand(float w, float expected)
    {
        float art = DetailVerticalLayout.ArtworkFor(w, rowFlow: true);
        Assert.InRange(art, DetailVerticalLayout.RowArtMin, DetailVerticalLayout.RowArtMax);
        Assert.Equal(expected, art);
    }

    /// <summary>The floor is the whole point of a floor: at ANY width the cover stays a hero, never a list thumbnail.
    /// The old ladder stepped to 96 and then 64, which is the row-thumbnail size — the cover stopped being the page's
    /// subject exactly where the page had least else to say.</summary>
    [Fact]
    public void ArtworkFor_NeverFallsBelowTheHeroFloor()
    {
        for (float w = LadderMin; w <= LadderMax; w += 1f)
        {
            Assert.True(DetailVerticalLayout.ArtworkFor(w, rowFlow: false) >= DetailVerticalLayout.ArtMin);
            Assert.True(DetailVerticalLayout.ArtworkFor(w, rowFlow: true) >= DetailVerticalLayout.ArtMin);
        }
        // …including below the ladder, where the column is degenerate.
        Assert.True(DetailVerticalLayout.ArtworkFor(40f, rowFlow: false) >= DetailVerticalLayout.ArtMin);
        Assert.True(DetailVerticalLayout.ArtworkFor(0f, rowFlow: false) >= DetailVerticalLayout.ArtMin);
    }

    /// <summary>Widening never SHRINKS the artwork. A cover that got smaller as the window grew is the single most
    /// visible way a continuous size function can be wrong.</summary>
    [Fact]
    public void ArtworkFor_IsMonotoneInWidth()
    {
        foreach (bool row in new[] { false, true })
        {
            float prev = DetailVerticalLayout.ArtworkFor(LadderMin, row);
            for (float w = LadderMin; w <= LadderMax; w += 1f)
            {
                float art = DetailVerticalLayout.ArtworkFor(w, row);
                Assert.True(art >= prev, $"artwork shrank at w={w} (rowFlow={row}): {prev} → {art}");
                prev = art;
            }
        }
    }

    /// <summary>Whole DIPs only. A fractional edge would churn the cover component's key (which folds the size in) and
    /// the decode bucket on every sub-pixel resize frame.</summary>
    [Fact]
    public void ArtworkFor_IsAlwaysAWholeDip()
    {
        for (float w = LadderMin; w <= LadderMax; w += 1f)
        {
            Assert.Equal(MathF.Round(DetailVerticalLayout.ArtworkFor(w, false)), DetailVerticalLayout.ArtworkFor(w, false));
            Assert.Equal(MathF.Round(DetailVerticalLayout.ArtworkFor(w, true)), DetailVerticalLayout.ArtworkFor(w, true));
        }
    }

    // ── the identity column ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The copy column never falls below its floor, never exceeds the 640 cap, and in ROW flow always leaves
    /// room for the artwork and the gap beside it (the geometry the two arms have to agree on).</summary>
    [Fact]
    public void ContentWidth_StaysInsideItsBoundsAndFitsBesideTheArtwork()
    {
        for (float w = LadderMin; w <= LadderMax; w += 1f)
        {
            foreach (bool row in new[] { false, true })
            {
                float c = DetailVerticalLayout.ContentWidthFor(w, row);
                Assert.InRange(c, DetailVerticalLayout.ContentWMin, DetailVerticalLayout.ContentWMax);
            }

            // Row flow only, and only where it can actually occur (hysteresis floor and up): art + gap + copy must fit
            // the padded column. This is the geometry the reflow depends on — the copy column is what gives.
            if (w < DetailVerticalLayout.RowFlowLeaveW) continue;
            float inner = w - 2f * DetailVerticalLayout.HeroPadFor(w, rowFlow: true);
            float used = DetailVerticalLayout.ArtworkFor(w, true) + DetailVerticalLayout.HeroGapFor(w, rowFlow: true)
                       + DetailVerticalLayout.ContentWidthFor(w, true);
            Assert.True(used <= inner + 0.01f, $"row hero overflows at w={w}: {used} > {inner}");
        }
    }

    /// <summary>The tight pad/gap pair only ever applies to the STACKED arm below <c>NarrowPadW</c>. The row arm always
    /// takes the full 24/24 pair, at every width — load-bearing for <see cref="ArtworkFor_IsMonotoneInWidth"/>: without
    /// this split, <c>NarrowPadW</c> = 420 would subtract 40 DIP of inner width at one column width crossing into the
    /// row arm's low floor, producing a cover that SHRINKS as the window widens.</summary>
    [Theory]
    [InlineData(240f, false, 16f, 16f)]
    [InlineData(419f, false, 16f, 16f)]
    [InlineData(420f, false, 24f, 24f)]
    [InlineData(900f, false, 24f, 24f)]
    [InlineData(240f, true, 24f, 24f)]
    [InlineData(419f, true, 24f, 24f)]
    [InlineData(420f, true, 24f, 24f)]
    [InlineData(900f, true, 24f, 24f)]
    public void Padding_TightensOnlyAtPhoneWidth(float w, bool rowFlow, float pad, float gap)
    {
        Assert.Equal(pad, DetailVerticalLayout.HeroPadFor(w, rowFlow));
        Assert.Equal(gap, DetailVerticalLayout.HeroGapFor(w, rowFlow));
    }

    /// <summary>The invariant <see cref="Padding_TightensOnlyAtPhoneWidth"/> pins by example: the row arm's pad/gap
    /// never steps anywhere across the whole ladder, stacked or not — it is flatly 24/24.</summary>
    [Fact]
    public void HeroPad_NeverStepsInsideTheRowArm()
    {
        for (float w = LadderMin; w <= LadderMax; w += 1f)
        {
            Assert.Equal(DetailVerticalLayout.HeroPad, DetailVerticalLayout.HeroPadFor(w, rowFlow: true));
            Assert.Equal(DetailVerticalLayout.HeroGap, DetailVerticalLayout.HeroGapFor(w, rowFlow: true));
        }
    }

    /// <summary>At every width the row arm can actually be in (from the hysteresis floor up), the padded artwork + gap
    /// + copy column together never exceed the column — the geometry the compact row arm (issue #80) depends on.</summary>
    [Fact]
    public void CompactRowArm_FitsEveryPartInsideTheColumn()
    {
        for (float w = DetailVerticalLayout.RowFlowLeaveW; w <= LadderMax; w += 1f)
        {
            float pad = DetailVerticalLayout.HeroPadFor(w, rowFlow: true);
            float gap = DetailVerticalLayout.HeroGapFor(w, rowFlow: true);
            float art = DetailVerticalLayout.ArtworkFor(w, rowFlow: true);
            float body = DetailVerticalLayout.ContentWidthFor(w, rowFlow: true);
            float used = 2f * pad + gap + art + body;
            Assert.True(used <= w + 0.01f, $"row arm overflows at w={w}: {used} > {w}");
        }
    }

    /// <summary>The whole point of dropping the breakpoint to 424 (issue #80): everywhere the row arm can occur, it
    /// reserves less vertical band than the same content would stacked — otherwise lowering the breakpoint would not
    /// actually save any height.</summary>
    [Fact]
    public void RowFlow_IsAlwaysShorterThanStackedWhereItCanOccur()
    {
        for (float w = DetailVerticalLayout.RowFlowEnterW; w <= LadderMax; w += 1f)
        {
            float row = DetailVerticalLayout.HeroBandHeight(w, true, true, true, true, true);
            float stacked = DetailVerticalLayout.HeroBandHeight(w, false, true, true, true, true);
            Assert.True(row < stacked, $"row flow band {row} is not shorter than stacked {stacked} at w={w}");
        }
    }

    // ── the title WIDTH cap (unchanged contract; the SIZE is now a measured type plan, below) ───────────────────────

    /// <summary>The title's own measure is never narrower than the body column's, and stays inside its own (wider)
    /// cap — the fix for issue #79's 248-DIP-empty-at-1200 defect: the title used to measure against the 640 body cap
    /// too.</summary>
    [Fact]
    public void TitleWidth_IsTheCopyColumnUnderAHeadlineCap()
    {
        // At 1200 the body copy stops at its 640 readability cap while the headline takes the whole copy column
        // (1200 − 48 pad − 24 gap − 240 cover = 888), which is the 248 DIP that used to sit empty to its right.
        Assert.Equal(640f, DetailVerticalLayout.ContentWidthFor(1200f, rowFlow: true));
        Assert.Equal(888f, DetailVerticalLayout.TitleWidthFor(1200f, rowFlow: true));

        // ...and the headline cap does still bind, just further out: past ~1090 of copy column.
        Assert.Equal(DetailVerticalLayout.TitleWMax, DetailVerticalLayout.TitleWidthFor(1600f, rowFlow: true));

        for (float w = LadderMin; w <= LadderMax; w += 1f)
            foreach (bool row in new[] { false, true })
            {
                float body = DetailVerticalLayout.ContentWidthFor(w, row);
                float title = DetailVerticalLayout.TitleWidthFor(w, row);
                Assert.InRange(title, DetailVerticalLayout.ContentWMin, DetailVerticalLayout.TitleWMax);
                Assert.True(title >= body, $"title width {title} < body width {body} at w={w} (row={row})");
            }
    }

    // ── the title TYPE PLAN ──────────────────────────────────────────────────────────────────────────────────────
    //
    // Replaces the old four-rung width ladder (TitleSizeFor/TitleLineHeightFor/TitleMinSizeFor, all deleted): the
    // rungs' authored line heights were silently discarded by the engine's natural line box for every rung above 20
    // (DetailVerticalLayout.NaturalLineRatio's doc explains the measurement), and the ladder had no notion of the
    // cover's own height, so a short title always opened a page with an empty band under it. The plan below is a
    // measured algorithm: it spends the cover's unclaimed height as the title's budget, and defers to the engine's
    // own auto-fit (MinSize) to correct the estimate against real shaped metrics.

    /// <summary>Widening never SHRINKS the title. Walked with a SHORT, real album title ("Discovery") specifically
    /// because a short title's own width-fit term is nowhere near binding at any width on this ladder, so the walk
    /// exercises the height-budget/fluid-cap path the plan actually lives on — a long title's WIDTH-fit term can (by
    /// design; see <see cref="DetailVerticalLayout.TitleHeightBudgetFor"/>'s doc on why row flow's narrower title
    /// measure is deliberate) become the binding term exactly at the stacked→row flow seam, where the title's own box
    /// narrows even though the cover's height budget grows — that specific, bounded non-monotonicity belongs to a
    /// long title's WIDTH constraint, not to this size ladder being unstable.</summary>
    [Fact]
    public void TitleSize_IsMonotoneInWidth()
    {
        const string title = "Discovery";
        float prev = DetailVerticalLayout.TitleTypeFor(320f, DetailVerticalLayout.RowFlow(320f), title,
            eyebrow: false, attribution: false, meta: false).Size;
        for (float w = 320f; w <= 1600f; w += 1f)
        {
            bool row = DetailVerticalLayout.RowFlow(w);
            float size = DetailVerticalLayout.TitleTypeFor(w, row, title, eyebrow: false, attribution: false, meta: false).Size;
            Assert.True(size >= prev - 0.01f, $"title size dropped at w={w}: {prev} -> {size}");
            prev = size;
        }
    }

    /// <summary>A longer title never earns a LARGER size at the same column width — walked as increasing PREFIXES of
    /// one fixed long string (rather than unrelated strings of different lengths), so the only thing changing between
    /// assertions is length itself.</summary>
    [Theory]
    [InlineData(500f)]
    [InlineData(900f)]
    [InlineData(1200f)]
    public void TitleSize_IsNonIncreasingInTitleLength(float colW)
    {
        const string longest = "Can This Love Be Translated From The Original Language Without Losing Its Meaning";
        bool row = DetailVerticalLayout.RowFlow(colW);
        float prevSize = DetailVerticalLayout.TitleTypeFor(colW, row, longest[..1],
            eyebrow: false, attribution: false, meta: false).Size;
        for (int n = 2; n <= longest.Length; n++)
        {
            float size = DetailVerticalLayout.TitleTypeFor(colW, row, longest[..n],
                eyebrow: false, attribution: false, meta: false).Size;
            Assert.True(size <= prevSize + 0.01f, $"size grew from {prevSize} to {size} at length {n} (colW={colW})");
            prevSize = size;
        }
    }

    /// <summary>The whole point of the type plan (research point 3): "Pony" cannot fill an 820-DIP MEASURE without an
    /// absurd point size, so the empty band under it is closed by spending the COVER'S HEIGHT instead. At 1200 DIP
    /// with a full identity column (eyebrow + attribution + meta), the height budget resolves to exactly one line at
    /// the fluid cap's own natural line box — "Pony" stays on ONE line and its block spends (within a snap step) the
    /// whole budget, rather than leaving dozens of DIP of dead space above the action row the way the old flat 40-DIP
    /// rung did.</summary>
    [Fact]
    public void TitlePlan_ShortTitleFillsTheCoverHeight()
    {
        const float colW = 1200f;
        var plan = DetailVerticalLayout.TitleTypeFor(colW, rowFlow: true, "Pony",
            eyebrow: true, attribution: true, meta: true);
        float budget = DetailVerticalLayout.TitleHeightBudgetFor(colW, rowFlow: true,
            eyebrow: true, attribution: true, meta: true);

        Assert.Equal(1, plan.Lines);
        Assert.True(MathF.Abs(plan.BlockHeight - budget) <= DetailVerticalLayout.TitleSnapStep(plan.Size),
            $"block height {plan.BlockHeight} is not within a snap step of the budget {budget}");
    }

    /// <summary>A title too long to read comfortably at ANY one-line size in the available width takes a SECOND line
    /// rather than shrinking indefinitely (research point 3: the height budget is what makes a second line worth
    /// taking — it only wins when splitting actually buys a strictly larger size). The chosen size still clears the
    /// old ladder's own Title rung (40): two-line is a size WIN here, not a last resort.</summary>
    [Fact]
    public void TitlePlan_LongTitleTakesTwoLinesRatherThanEllipsis()
    {
        const string longTitle = "Can This Love Be Translated? (Soundtrack from the Netflix Series)";
        var plan = DetailVerticalLayout.TitleTypeFor(1200f, rowFlow: true, longTitle,
            eyebrow: false, attribution: false, meta: false);

        Assert.Equal(2, plan.Lines);
        Assert.True(plan.Size >= 40f, $"size {plan.Size} is below the old Title rung");
    }

    /// <summary>The mirror case: a title short enough that ONE line already reaches the fluid cap stays on one line —
    /// splitting it would not buy any more size, so <see cref="DetailVerticalLayout.TitleTypeFor(float,float,float,float,float)"/>'s
    /// "only switch lines if it strictly wins" rule keeps it put.</summary>
    [Fact]
    public void TitlePlan_PrefersOneLineWhenItBuysSize()
    {
        var plan = DetailVerticalLayout.TitleTypeFor(1200f, rowFlow: true, "To Pimp a Butterfly",
            eyebrow: false, attribution: false, meta: false);
        Assert.Equal(1, plan.Lines);
    }

    /// <summary>Every plan the algorithm can produce, over a width × title-length grid, leaves auto-fit ARMED: a
    /// non-empty shrink window strictly below the chosen size, and never below the absolute floor. A plan that failed
    /// this would silently disable the engine's auto-fit (its four preconditions include MinSize &gt; 0 and
    /// MinSize &lt; Size), leaving a long title to overflow or ellipsize instead of shrinking to fit.</summary>
    [Fact]
    public void TitlePlan_MinSizeAlwaysArmsAutoFit()
    {
        string?[] titles = ["Pony", "To Pimp a Butterfly",
            "Can This Love Be Translated? (Soundtrack from the Netflix Series)", "X", "", null];
        for (float w = LadderMin; w <= LadderMax; w += 40f)
            foreach (bool row in new[] { false, true })
                foreach (string? title in titles)
                {
                    var plan = DetailVerticalLayout.TitleTypeFor(w, row, title,
                        eyebrow: false, attribution: false, meta: false);
                    Assert.True(plan.MinSize < plan.Size,
                        $"MinSize {plan.MinSize} does not arm auto-fit against Size {plan.Size} at w={w} row={row} title={title}");
                    Assert.True(plan.MinSize >= DetailVerticalLayout.TitleMinSizeFloor,
                        $"MinSize {plan.MinSize} fell below the floor at w={w} row={row} title={title}");
                }
    }

    /// <summary>The identity column's reservation for the CHOSEN plan never runs past the cover's own edge by more
    /// than a bounded quantization slop: <see cref="DetailVerticalLayout.SnapTitleSize"/> can round the raw
    /// height-fit target UP to the nearest grid point (by design — see its doc on keeping the <c>TextMeasureCache</c>
    /// a hit), and a two-line plan pays that rounding twice (once per line), so the true bound is a couple of DIP
    /// past the naive "+1" a perfectly continuous size would allow — verified empirically over a wide
    /// title/width/chrome sweep to be well inside one snap step's worth of slack, never unbounded.</summary>
    [Fact]
    public void TitlePlan_NeverOverflowsTheCover()
    {
        string?[] titles = ["Pony", "To Pimp a Butterfly",
            "Can This Love Be Translated? (Soundtrack from the Netflix Series)", "X", ""];
        for (float w = LadderMin; w <= LadderMax; w += 5f)
            foreach (string? title in titles)
            {
                var plan = DetailVerticalLayout.TitleTypeFor(w, rowFlow: true, title,
                    eyebrow: true, attribution: true, meta: true);
                float h = DetailVerticalLayout.IdentityHeightFor(plan, rowFlow: true,
                    eyebrow: true, attribution: true, meta: true, description: false);
                float art = DetailVerticalLayout.ArtworkFor(w, rowFlow: true);
                Assert.True(h <= art + 8f, $"identity {h} overflows the cover {art} by more than a snap step at w={w} title={title}");
            }
    }

    /// <summary>Snapping is a projection onto the size grid: applying it twice is the same as applying it once, and
    /// the result always divides evenly by its OWN step (re-derived from the snapped value, not the input's step —
    /// see the method's doc on why a boundary-crossing value must not keep its old grid).</summary>
    [Fact]
    public void SnapTitleSize_IsIdempotentAndOnGrid()
    {
        for (float s = 0f; s <= 200f; s += 0.5f)
        {
            float snapped = DetailVerticalLayout.SnapTitleSize(s);
            Assert.Equal(snapped, DetailVerticalLayout.SnapTitleSize(snapped));
            float step = DetailVerticalLayout.TitleSnapStep(snapped);
            float onGrid = MathF.Round(snapped / step) * step;
            Assert.True(MathF.Abs(onGrid - snapped) < 0.01f, $"{snapped} is not on its own {step}-grid");
        }
    }

    /// <summary>Growing needs the target to clear TWO snap steps before <c>StableTitleSize</c> commits; shrinking
    /// needs only ONE — the same asymmetry <see cref="RowFlow(float,bool,bool)"/> uses for the flow seam, here pinned
    /// at a size (40, step 4) where the arithmetic is easy to read by inspection. Once a delta clears its threshold
    /// the size lands EXACTLY on the target in the one call (no multi-frame creep), which is the "converges" half of
    /// the claim: hysteresis only ever costs one frame of latency, never an asymptotic approach.</summary>
    [Fact]
    public void StableTitleSize_IsAsymmetricAndConverges()
    {
        const float current = 40f;   // TitleSnapStep(40) == 4
        Assert.Equal(48f, DetailVerticalLayout.StableTitleSize(target: 48f, current, initialized: true));   // +8 (2×step): moves
        Assert.Equal(current, DetailVerticalLayout.StableTitleSize(target: 46f, current, initialized: true)); // +6: held
        Assert.Equal(36f, DetailVerticalLayout.StableTitleSize(target: 36f, current, initialized: true));   // -4 (1×step): moves
        Assert.Equal(current, DetailVerticalLayout.StableTitleSize(target: 38f, current, initialized: true)); // -2: held
        Assert.Equal(current, DetailVerticalLayout.StableTitleSize(target: current, current, initialized: true)); // no-op

        // Nothing to be hysteretic about yet: a fresh mount, or a "current" that has fallen below the floor (never a
        // real committed size), takes the target outright.
        Assert.Equal(40f, DetailVerticalLayout.StableTitleSize(target: 40f, current: 20f, initialized: false));
        Assert.Equal(40f, DetailVerticalLayout.StableTitleSize(target: 40f, current: 10f, initialized: true));
    }

    /// <summary>The fluid cap's two anchors, pinned by example, plus the whole ladder walked for monotonicity — the
    /// cap must never let a wider window offer a SMALLER ceiling.</summary>
    [Fact]
    public void FluidTitleCap_HitsItsLocks()
    {
        Assert.Equal(28f, DetailVerticalLayout.FluidTitleCapFor(360f));
        Assert.Equal(96f, DetailVerticalLayout.FluidTitleCapFor(1100f));
        Assert.Equal(28f, DetailVerticalLayout.FluidTitleCapFor(240f));   // clamped flat below the low lock
        Assert.Equal(96f, DetailVerticalLayout.FluidTitleCapFor(1600f)); // clamped flat above the high lock

        float prev = DetailVerticalLayout.FluidTitleCapFor(LadderMin);
        for (float w = LadderMin; w <= LadderMax; w += 1f)
        {
            float cap = DetailVerticalLayout.FluidTitleCapFor(w);
            Assert.True(cap >= prev, $"fluid cap dropped at w={w}");
            prev = cap;
        }
    }

    [Fact]
    public void DescriptionMaxLines_IsShorterBesideTheArtwork()
    {
        Assert.Equal(3, DetailVerticalLayout.DescriptionMaxLines(rowFlow: true));
        Assert.Equal(4, DetailVerticalLayout.DescriptionMaxLines(rowFlow: false));
    }

    // ── the collapse ladder (unchanged contract, re-pinned) ──────────────────────────────────────────────────────

    [Fact]
    public void StickyGeometry_UsesCompactIdentityPlusChromeInset()
    {
        Assert.Equal(56f, DetailVerticalLayout.CompactIdentityHeight);
        Assert.Equal(37f, DetailVerticalLayout.ChromeExtent());
        Assert.Equal(85f, DetailVerticalLayout.ChromeExtent(contentFilterExtent: 48f));
        Assert.Equal(93f, DetailVerticalLayout.StickyClipInset());
        Assert.Equal(141f, DetailVerticalLayout.StickyClipInset(contentFilterExtent: 48f));
    }

    [Fact]
    public void VerticalViewport_MapsEveryLiveTrackToExpandableSlot()
    {
        const int visibleTracks = 4;
        Assert.Equal(DetailVerticalItemRole.Hero, DetailVerticalLayout.ItemRole(0, visibleTracks));
        Assert.Equal(DetailVerticalItemRole.Chrome, DetailVerticalLayout.ItemRole(1, visibleTracks));
        for (int i = 2; i < 2 + visibleTracks; i++)
            Assert.Equal(DetailVerticalItemRole.ExpandableTrack,
                DetailVerticalLayout.ItemRole(i, visibleTracks));
        Assert.Equal(DetailVerticalItemRole.Empty,
            DetailVerticalLayout.ItemRole(2 + visibleTracks, visibleTracks));
    }

    [Theory]
    [InlineData(260f, 204f)]
    [InlineData(56f, 1f)]
    [InlineData(20f, 1f)]
    public void CollapseDistance_EndsAtCompactIdentity(float expanded, float expected)
        => Assert.Equal(expected, DetailVerticalLayout.CollapseDistance(expanded));

    /// <summary>The hero dissolves into the band over overlapping windows, and the band's reveal is the LAST 44 DIP of
    /// the collapse — the timing constant the artist page shares through <c>ArtistHeroLayout.CompactRevealStart</c>.
    /// This is what has to keep lining up now that the hero's measured height changed shape.</summary>
    [Theory]
    [InlineData(204f, 108f, 160f)]
    [InlineData(568f, 472f, 524f)]
    [InlineData(40f, 0f, 0f)]
    public void ScrollHandoff_UsesLateOverlappingWindows(float collapse, float expandedStart, float compactStart)
    {
        Assert.Equal(expandedStart, DetailVerticalLayout.ExpandedFadeStart(collapse));
        Assert.Equal(compactStart, DetailVerticalLayout.CompactRevealStart(collapse));
        // Compact identity starts before the expanded presentation reaches zero, so there is no dead visual interval.
        Assert.True(compactStart < collapse);
        Assert.True(expandedStart <= compactStart);
    }

    /// <summary>Every hero the ladder can produce is tall enough that the band's 44-DIP reveal window still opens
    /// AFTER the hero has started fading — i.e. the two ramps overlap at every real hero height, which is what makes
    /// the handoff reversible instead of a snap. The heights are the real composition's floors: the artwork edge plus
    /// the identity block plus the toolbar row.</summary>
    [Theory]
    [InlineData(200f)]
    [InlineData(320f)]
    [InlineData(420f)]
    [InlineData(560f)]
    public void RevealWindow_OverlapsTheHeroFadeAtEveryHeroHeight(float heroHeight)
    {
        float collapse = DetailVerticalLayout.CollapseDistance(heroHeight);
        float fade = DetailVerticalLayout.ExpandedFadeStart(collapse);
        float reveal = DetailVerticalLayout.CompactRevealStart(collapse);
        Assert.True(reveal > fade, $"band reveal starts before the hero fades at h={heroHeight}");
        Assert.True(reveal < collapse);
        Assert.Equal(DetailVerticalLayout.CompactRevealBand, collapse - reveal);
    }

    [Theory]
    [InlineData(96f, 256)]
    [InlineData(128f, 256)]
    [InlineData(129f, 512)]
    [InlineData(280f, 512)]
    [InlineData(289f, 1024)]
    public void ArtworkDecodePx_UsesStableBuckets(float size, int expected)
        => Assert.Equal(expected, DetailVerticalLayout.ArtworkDecodePx(size));

    /// <summary>The blurred background extension's band never collapses to a hairline before the hero is measured,
    /// and it is exactly the hero once it is.</summary>
    [Theory]
    [InlineData(0f, 112f)]
    [InlineData(64f, 112f)]
    [InlineData(420f, 420f)]
    public void BackdropBand_FloorsBeforeTheHeroIsMeasured(float heroHeight, float expected)
        => Assert.Equal(expected, DetailVerticalLayout.BackdropBandFor(heroHeight));

    [Theory]
    [InlineData(0f)]
    [InlineData(-4f)]
    [InlineData(7f)]
    [InlineData(1000f)]
    public void BucketW_SnapsToEightDipAndNeverReturnsZero(float w)
    {
        float b = DetailVerticalLayout.BucketW(w);
        Assert.True(b > 0f);
        Assert.Equal(0f, b % 8f);
    }
}
