// ── Wavee.Tests/DetailVerticalLayoutTests.cs — the unified detail hero's pure arithmetic, walked as a width ladder ──
//
// Ported from _old/Wavee.Tests/DetailVerticalLayoutTests.cs onto `Detail.VerticalLayout` (Entities/Detail.cs). Every
// assertion is 0.2.9's; only the call shape changed. Added in 0.3: the chapter's worked title plans as test vectors
// (ch 03 W6/W7/W8 and §0.8) and the Classic header's clip inset (ch 03 §3, parity item 63).
//
// The defect class is a hero that behaves differently at two widths a few DIP apart, so the properties that matter
// during a resize — monotonicity, floors that hold, a flow that does not chatter on its seam — are shown by walking the
// ladder, not by spot-checking three widths.

using Xunit;
using VerticalLayout = Wavee.Detail.VerticalLayout;

namespace Wavee.Tests;

public class DetailVerticalLayoutTests
{
    // 1-DIP steps from an ultra-narrow snap layout to a maximised window with the Hero page layout forced on.
    const float LadderMin = 240f, LadderMax = 1400f;

    [Fact]
    public void PageLayoutConstants_MirrorPersistedSettingValues()
    {
        Assert.Equal(0, VerticalLayout.PageAuto);
        Assert.Equal(1, VerticalLayout.PageHero);
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
        => Assert.Equal(expected, VerticalLayout.RowFlow(w));

    [Fact]
    public void RowFlow_Unmeasured_UsesTheFallbackWidth()
    {
        Assert.Equal(VerticalLayout.RowFlow(VerticalLayout.FallbackW), VerticalLayout.RowFlow(0f));
        Assert.Equal(VerticalLayout.RowFlow(VerticalLayout.FallbackW),
                     VerticalLayout.RowFlow(-1f, current: false, initialized: true));
    }

    /// <summary>Entering row flow needs 424; leaving it needs a further 24-DIP drop (to 400).</summary>
    [Fact]
    public void RowFlow_UsesResizeHysteresis()
    {
        Assert.False(VerticalLayout.RowFlow(423f, current: false, initialized: true));
        Assert.True(VerticalLayout.RowFlow(424f, current: false, initialized: true));
        Assert.True(VerticalLayout.RowFlow(423f, current: true, initialized: true));
        Assert.True(VerticalLayout.RowFlow(VerticalLayout.RowFlowLeaveW, current: true, initialized: true));
        Assert.False(VerticalLayout.RowFlow(VerticalLayout.RowFlowLeaveW - 1f, current: true, initialized: true));
    }

    /// <summary>Before the first measure the seed is a construction default, so the first real width is taken outright.</summary>
    [Fact]
    public void RowFlow_FirstMeasureIgnoresTheSeed()
    {
        Assert.True(VerticalLayout.RowFlow(700f, current: false, initialized: false));
        Assert.False(VerticalLayout.RowFlow(360f, current: true, initialized: false));
    }

    /// <summary>Walk the whole ladder with hysteresis armed, both directions: the flow flips at most once per sweep.</summary>
    [Fact]
    public void RowFlow_WalksTheLadderWithoutChattering()
    {
        Assert.Equal(1, FlipsWhileWalking(LadderMin, LadderMax));
        Assert.Equal(1, FlipsWhileWalking(LadderMax, LadderMin));

        static int FlipsWhileWalking(float from, float to)
        {
            float step = from < to ? 1f : -1f;
            bool flow = VerticalLayout.RowFlow(from);
            int flips = 0;
            for (float w = from; from < to ? w <= to : w >= to; w += step)
            {
                bool next = VerticalLayout.RowFlow(w, flow, initialized: true);
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
        => Assert.Equal(expected, VerticalLayout.ArtworkFor(w, rowFlow: false));

    [Theory]
    [InlineData(400f, 144f)]    // RowFlowLeaveW: 0.44 × (400 − 48 − 24) = 144.32 → 144, exactly the floor
    [InlineData(424f, 155f)]    // RowFlowEnterW: 0.44 × 352 = 154.88 → 155
    [InlineData(469f, 175f)]    // 0.44 × 397 = 174.7 → 175
    [InlineData(540f, 206f)]    // 0.44 × 468 = 205.9 → 206
    [InlineData(617f, 240f)]    // 0.44 × 545 = 239.8 → 240, the cap crossing
    [InlineData(1400f, 240f)]
    public void ArtworkFor_RowFlow_StaysInsideTheBand(float w, float expected)
    {
        float art = VerticalLayout.ArtworkFor(w, rowFlow: true);
        Assert.InRange(art, VerticalLayout.RowArtMin, VerticalLayout.RowArtMax);
        Assert.Equal(expected, art);
    }

    /// <summary>At ANY width the cover stays a hero, never a list thumbnail.</summary>
    [Fact]
    public void ArtworkFor_NeverFallsBelowTheHeroFloor()
    {
        for (float w = LadderMin; w <= LadderMax; w += 1f)
        {
            Assert.True(VerticalLayout.ArtworkFor(w, rowFlow: false) >= VerticalLayout.ArtMin);
            Assert.True(VerticalLayout.ArtworkFor(w, rowFlow: true) >= VerticalLayout.ArtMin);
        }
        Assert.True(VerticalLayout.ArtworkFor(40f, rowFlow: false) >= VerticalLayout.ArtMin);
        Assert.True(VerticalLayout.ArtworkFor(0f, rowFlow: false) >= VerticalLayout.ArtMin);
    }

    /// <summary>Widening never SHRINKS the artwork.</summary>
    [Fact]
    public void ArtworkFor_IsMonotoneInWidth()
    {
        foreach (bool row in new[] { false, true })
        {
            float prev = VerticalLayout.ArtworkFor(LadderMin, row);
            for (float w = LadderMin; w <= LadderMax; w += 1f)
            {
                float art = VerticalLayout.ArtworkFor(w, row);
                Assert.True(art >= prev, $"artwork shrank at w={w} (rowFlow={row}): {prev} → {art}");
                prev = art;
            }
        }
    }

    /// <summary>Whole DIPs only — a fractional edge would churn the cover key and the decode bucket.</summary>
    [Fact]
    public void ArtworkFor_IsAlwaysAWholeDip()
    {
        for (float w = LadderMin; w <= LadderMax; w += 1f)
        {
            Assert.Equal(MathF.Round(VerticalLayout.ArtworkFor(w, false)), VerticalLayout.ArtworkFor(w, false));
            Assert.Equal(MathF.Round(VerticalLayout.ArtworkFor(w, true)), VerticalLayout.ArtworkFor(w, true));
        }
    }

    // ── the identity column ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The copy column stays inside its bounds and, in ROW flow, fits beside the artwork and the gap.</summary>
    [Fact]
    public void ContentWidth_StaysInsideItsBoundsAndFitsBesideTheArtwork()
    {
        for (float w = LadderMin; w <= LadderMax; w += 1f)
        {
            foreach (bool row in new[] { false, true })
            {
                float c = VerticalLayout.ContentWidthFor(w, row);
                Assert.InRange(c, VerticalLayout.ContentWMin, VerticalLayout.ContentWMax);
            }

            if (w < VerticalLayout.RowFlowLeaveW) continue;
            float inner = w - 2f * VerticalLayout.HeroPadFor(w, rowFlow: true);
            float used = VerticalLayout.ArtworkFor(w, true) + VerticalLayout.HeroGapFor(w, rowFlow: true)
                       + VerticalLayout.ContentWidthFor(w, true);
            Assert.True(used <= inner + 0.01f, $"row hero overflows at w={w}: {used} > {inner}");
        }
    }

    /// <summary>The tight pad/gap pair only ever applies to the STACKED arm below NarrowPadW.</summary>
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
        Assert.Equal(pad, VerticalLayout.HeroPadFor(w, rowFlow));
        Assert.Equal(gap, VerticalLayout.HeroGapFor(w, rowFlow));
    }

    [Fact]
    public void HeroPad_NeverStepsInsideTheRowArm()
    {
        for (float w = LadderMin; w <= LadderMax; w += 1f)
        {
            Assert.Equal(VerticalLayout.HeroPad, VerticalLayout.HeroPadFor(w, rowFlow: true));
            Assert.Equal(VerticalLayout.HeroGap, VerticalLayout.HeroGapFor(w, rowFlow: true));
        }
    }

    [Fact]
    public void CompactRowArm_FitsEveryPartInsideTheColumn()
    {
        for (float w = VerticalLayout.RowFlowLeaveW; w <= LadderMax; w += 1f)
        {
            float pad = VerticalLayout.HeroPadFor(w, rowFlow: true);
            float gap = VerticalLayout.HeroGapFor(w, rowFlow: true);
            float art = VerticalLayout.ArtworkFor(w, rowFlow: true);
            float body = VerticalLayout.ContentWidthFor(w, rowFlow: true);
            float used = 2f * pad + gap + art + body;
            Assert.True(used <= w + 0.01f, $"row arm overflows at w={w}: {used} > {w}");
        }
    }

    /// <summary>Everywhere the row arm can occur, it reserves less band than the same content stacked (issue #80).</summary>
    [Fact]
    public void RowFlow_IsAlwaysShorterThanStackedWhereItCanOccur()
    {
        for (float w = VerticalLayout.RowFlowEnterW; w <= LadderMax; w += 1f)
        {
            float row = VerticalLayout.HeroBandHeight(w, true, true, true, true, true);
            float stacked = VerticalLayout.HeroBandHeight(w, false, true, true, true, true);
            Assert.True(row < stacked, $"row flow band {row} is not shorter than stacked {stacked} at w={w}");
        }
    }

    // ── the title WIDTH cap ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The title's measure is never narrower than the body column's and stays inside its own wider cap (#79).</summary>
    [Fact]
    public void TitleWidth_IsTheCopyColumnUnderAHeadlineCap()
    {
        Assert.Equal(640f, VerticalLayout.ContentWidthFor(1200f, rowFlow: true));
        Assert.Equal(888f, VerticalLayout.TitleWidthFor(1200f, rowFlow: true));
        Assert.Equal(VerticalLayout.TitleWMax, VerticalLayout.TitleWidthFor(1600f, rowFlow: true));

        for (float w = LadderMin; w <= LadderMax; w += 1f)
            foreach (bool row in new[] { false, true })
            {
                float body = VerticalLayout.ContentWidthFor(w, row);
                float title = VerticalLayout.TitleWidthFor(w, row);
                Assert.InRange(title, VerticalLayout.ContentWMin, VerticalLayout.TitleWMax);
                Assert.True(title >= body, $"title width {title} < body width {body} at w={w} (row={row})");
            }
    }

    // ── the EDITABLE title's run measure (#92) ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(572f, 544f)]
    [InlineData(20f, 0f)]
    [InlineData(28f, 0f)]
    [InlineData(0f, 0f)]
    public void EditableTitleMeasure_IsTheWrapWidthLessThePencilSlot(float wrapWidth, float expected)
        => Assert.Equal(expected, VerticalLayout.EditableTitleMeasure(wrapWidth));

    [Theory]
    [InlineData(400f, true)]     // 400 − 48 − 24 − 144 = 184
    [InlineData(424f, true)]     // 424 − 48 − 24 − 155 = 197
    [InlineData(820f, true)]     // 820 − 48 − 24 − 240 = 508
    [InlineData(1200f, true)]    // 888
    [InlineData(1600f, true)]    // TitleWMax binds: 1000
    [InlineData(240f, false)]    // stacked, narrow pad: 208
    [InlineData(600f, false)]    // stacked: 552
    public void EditableTitleMeasure_PlusPencilSlot_FillsTheWrapWidthExactly(float colW, bool rowFlow)
    {
        float wrap = VerticalLayout.TitleWidthFor(colW, rowFlow);
        float run = VerticalLayout.EditableTitleMeasure(wrap);
        Assert.True(run > 0f, $"run measure {run} collapsed at colW={colW} row={rowFlow}");
        Assert.Equal(wrap, run + VerticalLayout.EditableTitlePencilW + VerticalLayout.EditableTitlePencilGap);
    }

    // ── the title TYPE PLAN ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Widening never SHRINKS a short title (its width-fit is never binding, so this walks the height-budget
    /// and fluid-cap path the plan actually lives on).</summary>
    [Fact]
    public void TitleSize_IsMonotoneInWidth()
    {
        const string title = "Discovery";
        float prev = VerticalLayout.TitleTypeFor(320f, VerticalLayout.RowFlow(320f), title,
            eyebrow: false, attribution: false, meta: false).Size;
        for (float w = 320f; w <= 1600f; w += 1f)
        {
            bool row = VerticalLayout.RowFlow(w);
            float size = VerticalLayout.TitleTypeFor(w, row, title, eyebrow: false, attribution: false, meta: false).Size;
            Assert.True(size >= prev - 0.01f, $"title size dropped at w={w}: {prev} -> {size}");
            prev = size;
        }
    }

    /// <summary>A longer title (a longer PREFIX of one string) never earns a larger size at the same width.</summary>
    [Theory]
    [InlineData(500f)]
    [InlineData(900f)]
    [InlineData(1200f)]
    public void TitleSize_IsNonIncreasingInTitleLength(float colW)
    {
        const string longest = "Can This Love Be Translated From The Original Language Without Losing Its Meaning";
        bool row = VerticalLayout.RowFlow(colW);
        float prevSize = VerticalLayout.TitleTypeFor(colW, row, longest[..1],
            eyebrow: false, attribution: false, meta: false).Size;
        for (int n = 2; n <= longest.Length; n++)
        {
            float size = VerticalLayout.TitleTypeFor(colW, row, longest[..n],
                eyebrow: false, attribution: false, meta: false).Size;
            Assert.True(size <= prevSize + 0.01f, $"size grew from {prevSize} to {size} at length {n} (colW={colW})");
            prevSize = size;
        }
    }

    /// <summary>"Pony" spends the COVER'S HEIGHT: one line whose block is within a snap step of the budget.</summary>
    [Fact]
    public void TitlePlan_ShortTitleFillsTheCoverHeight()
    {
        const float colW = 1200f;
        var plan = VerticalLayout.TitleTypeFor(colW, rowFlow: true, "Pony",
            eyebrow: true, attribution: true, meta: true);
        float budget = VerticalLayout.TitleHeightBudgetFor(colW, rowFlow: true,
            eyebrow: true, attribution: true, meta: true);

        Assert.Equal(1, plan.Lines);
        Assert.True(MathF.Abs(plan.BlockHeight - budget) <= VerticalLayout.TitleSnapStep(plan.Size),
            $"block height {plan.BlockHeight} is not within a snap step of the budget {budget}");
    }

    /// <summary>A title too long for any comfortable one-line size takes a SECOND line, and still clears 40.</summary>
    [Fact]
    public void TitlePlan_LongTitleTakesTwoLinesRatherThanEllipsis()
    {
        const string longTitle = "Can This Love Be Translated? (Soundtrack from the Netflix Series)";
        var plan = VerticalLayout.TitleTypeFor(1200f, rowFlow: true, longTitle,
            eyebrow: false, attribution: false, meta: false);

        Assert.Equal(2, plan.Lines);
        Assert.True(plan.Size >= 40f, $"size {plan.Size} is below the old Title rung");
    }

    [Fact]
    public void TitlePlan_PrefersOneLineWhenItBuysSize()
    {
        var plan = VerticalLayout.TitleTypeFor(1200f, rowFlow: true, "To Pimp a Butterfly",
            eyebrow: false, attribution: false, meta: false);
        Assert.Equal(1, plan.Lines);
    }

    /// <summary>Every plan over a width × title grid leaves auto-fit ARMED: MinSize strictly below Size, never below the floor.</summary>
    [Fact]
    public void TitlePlan_MinSizeAlwaysArmsAutoFit()
    {
        string?[] titles = ["Pony", "To Pimp a Butterfly",
            "Can This Love Be Translated? (Soundtrack from the Netflix Series)", "X", "", null];
        for (float w = LadderMin; w <= LadderMax; w += 40f)
            foreach (bool row in new[] { false, true })
                foreach (string? title in titles)
                {
                    var plan = VerticalLayout.TitleTypeFor(w, row, title,
                        eyebrow: false, attribution: false, meta: false);
                    Assert.True(plan.MinSize < plan.Size,
                        $"MinSize {plan.MinSize} does not arm auto-fit against Size {plan.Size} at w={w} row={row} title={title}");
                    Assert.True(plan.MinSize >= VerticalLayout.TitleMinSizeFloor,
                        $"MinSize {plan.MinSize} fell below the floor at w={w} row={row} title={title}");
                }
    }

    /// <summary>The identity reservation for the chosen plan never runs past the cover by more than a snap step's slop.</summary>
    [Fact]
    public void TitlePlan_NeverOverflowsTheCover()
    {
        string?[] titles = ["Pony", "To Pimp a Butterfly",
            "Can This Love Be Translated? (Soundtrack from the Netflix Series)", "X", ""];
        for (float w = LadderMin; w <= LadderMax; w += 5f)
            foreach (string? title in titles)
            {
                var plan = VerticalLayout.TitleTypeFor(w, rowFlow: true, title,
                    eyebrow: true, attribution: true, meta: true);
                float h = VerticalLayout.IdentityHeightFor(plan, rowFlow: true,
                    eyebrow: true, attribution: true, meta: true, description: false);
                float art = VerticalLayout.ArtworkFor(w, rowFlow: true);
                Assert.True(h <= art + 8f, $"identity {h} overflows the cover {art} by more than a snap step at w={w} title={title}");
            }
    }

    /// <summary>ch 03's worked plans, reproduced: W6 (520, row flow, eyebrow + attribution + meta), W7 (380 bucketed to
    /// 384, stacked), W8 (1100, Hero layout) and §0.8's "Pony" at a 1000-DIP column.</summary>
    [Theory]
    [InlineData(520f, true, "Random Access Memories", 32f, 43f, 2)]
    [InlineData(520f, true, null, 44f, 59f, 1)]
    [InlineData(384f, false, "Random Access Memories", 30f, 40f, 2)]
    [InlineData(1100f, true, "Discover Weekly", 96f, 128f, 1)]
    [InlineData(1000f, true, "Pony", 88f, 117f, 1)]
    public void TitlePlan_ReproducesTheChapterWorkedExamples(float colW, bool rowFlow, string? title,
                                                             float size, float lineHeight, int lines)
    {
        var plan = VerticalLayout.TitleTypeFor(colW, rowFlow, title, eyebrow: true, attribution: true, meta: true);
        Assert.Equal(size, plan.Size);
        Assert.Equal(lineHeight, plan.LineHeight);
        Assert.Equal(lines, plan.Lines);
        Assert.True(plan.MinSize < plan.Size);
    }

    [Fact]
    public void TitlePlan_W6_SolvesAgainstItsOwnMeasureAndBudget()
    {
        // 520 row flow: art 197, title measure 251, height budget 197 − 92 − 5·4 = 85, cap ≈ 42.7.
        Assert.Equal(197f, VerticalLayout.ArtworkFor(520f, rowFlow: true));
        Assert.Equal(251f, VerticalLayout.TitleWidthFor(520f, rowFlow: true));
        Assert.Equal(85f, VerticalLayout.TitleHeightBudgetFor(520f, rowFlow: true, eyebrow: true, attribution: true, meta: true));
        Assert.Equal(384f, VerticalLayout.BucketW(380f));   // W7: banker's rounding of 47.5 → 48
    }

    [Fact]
    public void SnapTitleSize_IsIdempotentAndOnGrid()
    {
        for (float s = 0f; s <= 200f; s += 0.5f)
        {
            float snapped = VerticalLayout.SnapTitleSize(s);
            Assert.Equal(snapped, VerticalLayout.SnapTitleSize(snapped));
            float step = VerticalLayout.TitleSnapStep(snapped);
            float onGrid = MathF.Round(snapped / step) * step;
            Assert.True(MathF.Abs(onGrid - snapped) < 0.01f, $"{snapped} is not on its own {step}-grid");
        }
    }

    /// <summary>Growing needs TWO snap steps, shrinking ONE, and a cleared delta lands exactly on the target.</summary>
    [Fact]
    public void StableTitleSize_IsAsymmetricAndConverges()
    {
        const float current = 40f;   // TitleSnapStep(40) == 4
        Assert.Equal(48f, VerticalLayout.StableTitleSize(target: 48f, current, initialized: true));
        Assert.Equal(current, VerticalLayout.StableTitleSize(target: 46f, current, initialized: true));
        Assert.Equal(36f, VerticalLayout.StableTitleSize(target: 36f, current, initialized: true));
        Assert.Equal(current, VerticalLayout.StableTitleSize(target: 38f, current, initialized: true));
        Assert.Equal(current, VerticalLayout.StableTitleSize(target: current, current, initialized: true));

        Assert.Equal(40f, VerticalLayout.StableTitleSize(target: 40f, current: 20f, initialized: false));
        Assert.Equal(40f, VerticalLayout.StableTitleSize(target: 40f, current: 10f, initialized: true));
    }

    [Fact]
    public void FluidTitleCap_HitsItsLocks()
    {
        Assert.Equal(28f, VerticalLayout.FluidTitleCapFor(360f));
        Assert.Equal(96f, VerticalLayout.FluidTitleCapFor(1100f));
        Assert.Equal(28f, VerticalLayout.FluidTitleCapFor(240f));
        Assert.Equal(96f, VerticalLayout.FluidTitleCapFor(1600f));

        float prev = VerticalLayout.FluidTitleCapFor(LadderMin);
        for (float w = LadderMin; w <= LadderMax; w += 1f)
        {
            float cap = VerticalLayout.FluidTitleCapFor(w);
            Assert.True(cap >= prev, $"fluid cap dropped at w={w}");
            prev = cap;
        }
    }

    [Fact]
    public void DescriptionMaxLines_IsShorterBesideTheArtwork()
    {
        Assert.Equal(3, VerticalLayout.DescriptionMaxLines(rowFlow: true));
        Assert.Equal(4, VerticalLayout.DescriptionMaxLines(rowFlow: false));
    }

    // ── the collapse ladder ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StickyGeometry_UsesCompactIdentityPlusChromeInset()
    {
        Assert.Equal(56f, VerticalLayout.CompactIdentityHeight);
        Assert.Equal(37f, VerticalLayout.ChromeExtent());
        Assert.Equal(85f, VerticalLayout.ChromeExtent(contentFilterExtent: 48f));
        Assert.Equal(93f, VerticalLayout.StickyClipInset());
        Assert.Equal(141f, VerticalLayout.StickyClipInset(contentFilterExtent: 48f));
    }

    /// <summary>The middle term reads the table's REAL header height: under the Classic skin (32) the rows are cut at
    /// 89, not the Modern 93 — no 4-DIP dead band above the first visible row (ch 03 parity item 63).</summary>
    [Fact]
    public void StickyGeometry_ReadsTheTablesRealHeaderHeight()
    {
        Assert.Equal(33f, VerticalLayout.ChromeExtent(0f, headerHeight: 32f));
        Assert.Equal(89f, VerticalLayout.StickyClipInset(0f, headerHeight: 32f));
        Assert.Equal(137f, VerticalLayout.StickyClipInset(48f, headerHeight: 32f));
        Assert.Equal(VerticalLayout.StickyClipInset(), VerticalLayout.StickyClipInset(0f, VerticalLayout.ChromeHeaderHeight));
    }

    /// <summary>The trailing shelves' own clip line is the same 56-DIP band the hero collapses to, and (by
    /// construction) exactly <c>ChromeExtent</c> shorter than the rows' own clip — the gap the chrome itself fills in
    /// <c>Track.Table.cs</c>'s <c>TrailingBody</c> (<c>tableBlock</c> = chrome + rows; <c>trailBlock</c> clipped at
    /// <c>TrailingClipInset</c> just below it).</summary>
    [Fact]
    public void TrailingClipInset_IsChromeExtentBelowTheRowsClip()
    {
        Assert.Equal(VerticalLayout.CompactIdentityHeight, VerticalLayout.TrailingClipInset);
        Assert.Equal(VerticalLayout.ChromeExtent(), VerticalLayout.StickyClipInset() - VerticalLayout.TrailingClipInset);
        Assert.Equal(VerticalLayout.ChromeExtent(contentFilterExtent: 48f),
            VerticalLayout.StickyClipInset(contentFilterExtent: 48f) - VerticalLayout.TrailingClipInset);
        Assert.Equal(VerticalLayout.ChromeExtent(0f, headerHeight: 32f),
            VerticalLayout.StickyClipInset(0f, headerHeight: 32f) - VerticalLayout.TrailingClipInset);
    }

    [Fact]
    public void VerticalViewport_MapsEveryLiveTrackToExpandableSlot()
    {
        const int visibleTracks = 4;
        Assert.Equal(VerticalItemRole.Hero, VerticalLayout.ItemRole(0, visibleTracks));
        Assert.Equal(VerticalItemRole.Chrome, VerticalLayout.ItemRole(1, visibleTracks));
        for (int i = 2; i < 2 + visibleTracks; i++)
            Assert.Equal(VerticalItemRole.ExpandableTrack, VerticalLayout.ItemRole(i, visibleTracks));
        Assert.Equal(VerticalItemRole.Empty, VerticalLayout.ItemRole(2 + visibleTracks, visibleTracks));
    }

    [Theory]
    [InlineData(260f, 204f)]
    [InlineData(56f, 1f)]
    [InlineData(20f, 1f)]
    public void CollapseDistance_EndsAtCompactIdentity(float expanded, float expected)
        => Assert.Equal(expected, VerticalLayout.CollapseDistance(expanded));

    /// <summary>The band's reveal is the LAST 44 DIP of the collapse, overlapping the hero's 96-DIP fade.</summary>
    [Theory]
    [InlineData(204f, 108f, 160f)]
    [InlineData(568f, 472f, 524f)]
    [InlineData(40f, 0f, 0f)]
    public void ScrollHandoff_UsesLateOverlappingWindows(float collapse, float expandedStart, float compactStart)
    {
        Assert.Equal(expandedStart, VerticalLayout.ExpandedFadeStart(collapse));
        Assert.Equal(compactStart, VerticalLayout.CompactRevealStart(collapse));
        Assert.True(compactStart < collapse);
        Assert.True(expandedStart <= compactStart);
    }

    [Theory]
    [InlineData(200f)]
    [InlineData(320f)]
    [InlineData(420f)]
    [InlineData(560f)]
    public void RevealWindow_OverlapsTheHeroFadeAtEveryHeroHeight(float heroHeight)
    {
        float collapse = VerticalLayout.CollapseDistance(heroHeight);
        float fade = VerticalLayout.ExpandedFadeStart(collapse);
        float reveal = VerticalLayout.CompactRevealStart(collapse);
        Assert.True(reveal > fade, $"band reveal starts before the hero fades at h={heroHeight}");
        Assert.True(reveal < collapse);
        Assert.Equal(VerticalLayout.CompactRevealBand, collapse - reveal);
    }

    [Theory]
    [InlineData(96f, 256)]
    [InlineData(128f, 256)]
    [InlineData(129f, 512)]
    [InlineData(280f, 512)]
    [InlineData(289f, 1024)]
    public void ArtworkDecodePx_UsesStableBuckets(float size, int expected)
        => Assert.Equal(expected, VerticalLayout.ArtworkDecodePx(size));

    [Theory]
    [InlineData(0f, 112f)]
    [InlineData(64f, 112f)]
    [InlineData(420f, 420f)]
    public void BackdropBand_FloorsBeforeTheHeroIsMeasured(float heroHeight, float expected)
        => Assert.Equal(expected, VerticalLayout.BackdropBandFor(heroHeight));

    [Theory]
    [InlineData(0f)]
    [InlineData(-4f)]
    [InlineData(7f)]
    [InlineData(1000f)]
    public void BucketW_SnapsToEightDipAndNeverReturnsZero(float w)
    {
        float b = VerticalLayout.BucketW(w);
        Assert.True(b > 0f);
        Assert.Equal(0f, b % 8f);
    }
}
