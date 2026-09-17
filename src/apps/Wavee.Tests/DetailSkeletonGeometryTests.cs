// ── Wavee.Tests/DetailSkeletonGeometryTests.cs — D49: the loading band IS the loaded band ─────────────────────────
//
// Ported from _old/Wavee.Tests/DetailSkeletonGeometryTests.cs onto `Detail.VerticalLayout` / `Detail.Skeleton`
// (Entities/Detail.cs). Every 0.2.9 assertion is kept; only the call shape changed. Added in 0.3:
//   · W28 — the CHART caption's presence flag. 0.2.9's hero emitted `hero-chart` but the band arithmetic had no flag for
//     it, so a chart playlist's caption (+ one gap) arrived outside the reserved band and shoved the toolbar, the
//     column header and every row. The flag reserves it exactly like the daylist pulse.
//   · the skeleton's bar geometry (fractions, the 32-DIP floor, the last-line-short rule), now a named rule.
//
// The defect D49 fixed: in the hero system the hero and chrome are PREFIX ITEMS of the virtualized list, so while the
// model was pending they did not exist, and several hundred DIP materialised above the rows when content landed. The
// fix is arithmetic, not a second design: the skeleton reserves `HeroBandHeight`, the same function the loaded hero's
// pre-measure collapse binds use.

using Xunit;
using Skeleton = Wavee.Detail.Skeleton;
using VerticalLayout = Wavee.Detail.VerticalLayout;

namespace Wavee.Tests;

public class DetailSkeletonGeometryTests
{
    const float LadderMin = 240f, LadderMax = 1400f;

    // ── the two-column rail's skeleton plan (the rows it reserves mirror Detail.RailColumn) ───────────────────────

    [Fact]
    public void RailPlan_Playlist_OwnerRowTitleMetaCtaAndBlurb()
    {
        var plan = Skeleton.RailPlanFor(DetailKind.Playlist, BadgeStyle.OwnerRow, heart: true, descriptionMaxLines: 6);
        Assert.False(plan.Eyebrow);
        Assert.True(plan.Owner);
        Assert.False(plan.Artists);
        Assert.True(plan.Meta);
        Assert.Equal(Skeleton.RailTitleLines, plan.TitleLines);
        Assert.Equal(3, plan.Fabs);                       // heart · Share · ⋯
        Assert.Equal(Skeleton.RailDescriptionLines, plan.DescriptionLines);
    }

    [Fact]
    public void RailPlan_Album_EyebrowArtistsNoMetaNoBlurbNoMore()
    {
        var plan = Skeleton.RailPlanFor(DetailKind.Album, BadgeStyle.TypeYear, heart: true, descriptionMaxLines: 6);
        Assert.True(plan.Eyebrow);
        Assert.False(plan.Owner);
        Assert.True(plan.Artists);
        Assert.False(plan.Meta);
        Assert.Equal(2, plan.Fabs);                       // heart · Share — an album's rail has no ⋯ (ch 05 parity 15)
        Assert.Equal(0, plan.DescriptionLines);
    }

    [Fact]
    public void RailPlan_Show_StatesItsPublisherLineAndBlurb()
    {
        var plan = Skeleton.RailPlanFor(DetailKind.Show, BadgeStyle.TypeYear, heart: true, descriptionMaxLines: 3);
        Assert.True(plan.Meta);
        Assert.Equal(3, plan.DescriptionLines);
    }

    [Fact]
    public void RailPlan_Liked_NoHeartNoBlurb()
    {
        var plan = Skeleton.RailPlanFor(DetailKind.Liked, BadgeStyle.None, heart: false, descriptionMaxLines: 6);
        Assert.False(plan.Owner);
        Assert.True(plan.Meta);
        Assert.Equal(2, plan.Fabs);                       // Share · ⋯
        Assert.Equal(0, plan.DescriptionLines);
    }

    [Fact]
    public void RailPlan_BlurbClampsToTheRailsOwnLines()
    {
        Assert.Equal(2, Skeleton.RailPlanFor(DetailKind.Playlist, BadgeStyle.OwnerRow, heart: true, descriptionMaxLines: 2).DescriptionLines);
        Assert.Equal(0, Skeleton.RailPlanFor(DetailKind.Playlist, BadgeStyle.OwnerRow, heart: true, descriptionMaxLines: 0).DescriptionLines);
        Assert.Equal(0, Skeleton.RailPlanFor(DetailKind.Playlist, BadgeStyle.OwnerRow, heart: true, descriptionMaxLines: -1).DescriptionLines);
    }

    // The hero emit predicates, as the two real pages present them.
    const bool Album = true;            // eyebrow "ALBUM · 2019", billed artists, meta line, no blurb
    const bool Playlist = true;         // owner row, meta line, description

    // ── the band's arithmetic ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The band is the padded composition PLUS the toolbar row — never less than the artwork it holds.</summary>
    [Fact]
    public void HeroBand_AlwaysClearsTheArtworkPlusPaddingPlusToolbar()
    {
        for (float w = LadderMin; w <= LadderMax; w += 1f)
            foreach (bool rowFlow in new[] { false, true })
            {
                float band = VerticalLayout.HeroBandHeight(w, rowFlow, true, true, true, true);
                float floor = VerticalLayout.HeroPadFor(w, rowFlow)
                            + VerticalLayout.ArtworkFor(w, rowFlow)
                            + VerticalLayout.HeroBottomPad
                            + VerticalLayout.ExpandedToolbarTopPad
                            + VerticalLayout.ToolbarRowHeight
                            + VerticalLayout.ExpandedToolbarBottomPad;
                Assert.True(band >= floor, $"band {band} < artwork floor {floor} at w={w} (rowFlow={rowFlow})");
            }
    }

    /// <summary>The band is exactly the sum of the parts it declares (the pessimistic null-title plan's).</summary>
    [Theory]
    [InlineData(360f, false)]
    [InlineData(400f, false)]
    [InlineData(539f, false)]
    [InlineData(540f, true)]
    [InlineData(700f, true)]
    [InlineData(1200f, true)]
    public void HeroBand_IsExactlyTheCompositionItDeclares(float w, bool rowFlow)
    {
        var plan = VerticalLayout.TitleTypeFor(w, rowFlow, title: null, true, true, true);
        float identity = VerticalLayout.IdentityHeightFor(plan, rowFlow, true, true, true, true);
        float art = VerticalLayout.ArtworkFor(w, rowFlow);
        float hero = rowFlow ? MathF.Max(art, identity) : art + VerticalLayout.HeroGapFor(w, rowFlow) + identity;
        float expected = VerticalLayout.HeroPadFor(w, rowFlow) + hero + VerticalLayout.HeroBottomPad
                       + VerticalLayout.ExpandedToolbarTopPad
                       + VerticalLayout.ToolbarRowHeight
                       + VerticalLayout.ExpandedToolbarBottomPad;
        Assert.Equal(expected, VerticalLayout.HeroBandHeight(w, rowFlow, true, true, true, true));
    }

    /// <summary>The identity column reserves the blocks the hero will emit — no more, no less. Title, rule and action
    /// row are unconditional; every optional block (eyebrow, attribution, meta, description, pulse, and W28's chart)
    /// costs exactly its row plus one inter-block gap.</summary>
    [Theory]
    [InlineData(700f, true)]
    [InlineData(400f, false)]
    public void IdentityHeight_ChargesOnlyForTheBlocksTheHeroEmits(float w, bool rowFlow)
    {
        // A fresh PESSIMISTIC plan per flag combination (the budget's chrome sum moves with the flags, even though the
        // null-title SIZE does not at these widths — the fluid cap binds, not the height budget).
        var barePlan = VerticalLayout.TitleTypeFor(w, rowFlow, title: null, false, false, false);
        float bare = VerticalLayout.IdentityHeightFor(barePlan, rowFlow, false, false, false, false);
        Assert.Equal(
            barePlan.BlockHeight
            + VerticalLayout.AccentRuleRowHeight
            + VerticalLayout.ActionRowHeight
            + 2f * VerticalLayout.IdentityGap,
            bare);

        var eyebrowPlan = VerticalLayout.TitleTypeFor(w, rowFlow, title: null, true, false, false);
        Assert.Equal(bare + VerticalLayout.EyebrowRowHeight + VerticalLayout.IdentityGap,
            VerticalLayout.IdentityHeightFor(eyebrowPlan, rowFlow, true, false, false, false));

        var attributionPlan = VerticalLayout.TitleTypeFor(w, rowFlow, title: null, false, true, false);
        Assert.Equal(bare + VerticalLayout.AttributionRowHeight + VerticalLayout.IdentityGap,
            VerticalLayout.IdentityHeightFor(attributionPlan, rowFlow, false, true, false, false));

        var metaPlan = VerticalLayout.TitleTypeFor(w, rowFlow, title: null, false, false, true);
        Assert.Equal(bare + VerticalLayout.MetaRowHeight + VerticalLayout.IdentityGap,
            VerticalLayout.IdentityHeightFor(metaPlan, rowFlow, false, false, true, false));

        // Description never moves the plan (the title's budget excludes it by design), so it reuses barePlan.
        Assert.Equal(
            bare + VerticalLayout.DescriptionMaxLines(rowFlow) * VerticalLayout.DescriptionLineHeight
                 + VerticalLayout.IdentityGap,
            VerticalLayout.IdentityHeightFor(barePlan, rowFlow, false, false, false, true));

        var pulsePlan = VerticalLayout.TitleTypeFor(w, rowFlow, title: null, false, false, false, pulse: true);
        Assert.Equal(bare + VerticalLayout.PulseRowHeight + VerticalLayout.IdentityGap,
            VerticalLayout.IdentityHeightFor(pulsePlan, rowFlow, false, false, false, false, pulse: true));

        // W28: the chart caption is a block like every other — its row plus one gap.
        var chartPlan = VerticalLayout.TitleTypeFor(w, rowFlow, title: null, false, false, false, chart: true);
        Assert.Equal(bare + VerticalLayout.ChartRowHeight + VerticalLayout.IdentityGap,
            VerticalLayout.IdentityHeightFor(chartPlan, rowFlow, false, false, false, false, chart: true));
    }

    /// <summary>W28 end to end: a chart playlist's reserved band IS the composition with the caption in it — the band
    /// the skeleton and the pre-measure binds reserve now matches what a chart playlist composes, so nothing shoves when
    /// the model lands. Stacked, where nothing absorbs it, the band grows by exactly the row plus one gap. In row flow
    /// the caption is paid out of the title's height budget first (the plan may shrink the title to keep the column on
    /// the cover), so there the claim is the exact composition, not a fixed delta.</summary>
    [Theory]
    [InlineData(400f, false)]
    [InlineData(380f, false)]
    [InlineData(520f, true)]
    [InlineData(1100f, true)]
    public void W28_TheChartCaptionIsReservedInTheBand(float w, bool rowFlow)
    {
        Assert.Equal(16f, VerticalLayout.ChartRowHeight);

        // The reserved band is exactly the composition WITH the caption (the pessimistic null-title plan, chart flag on).
        var plan = VerticalLayout.TitleTypeFor(w, rowFlow, title: null, true, true, true, chart: true);
        float identity = VerticalLayout.IdentityHeightFor(plan, rowFlow, true, true, true, true, chart: true);
        float art = VerticalLayout.ArtworkFor(w, rowFlow);
        float hero = rowFlow ? MathF.Max(art, identity) : art + VerticalLayout.HeroGapFor(w, rowFlow) + identity;
        float expected = VerticalLayout.HeroPadFor(w, rowFlow) + hero + VerticalLayout.HeroBottomPad
                       + VerticalLayout.ExpandedToolbarTopPad + VerticalLayout.ToolbarRowHeight
                       + VerticalLayout.ExpandedToolbarBottomPad;
        Assert.Equal(expected, VerticalLayout.HeroBandHeight(w, rowFlow, true, true, true, true, chart: true));

        // For the SAME title plan, the caption costs exactly its row plus one gap.
        Assert.Equal(VerticalLayout.ChartRowHeight + VerticalLayout.IdentityGap,
            VerticalLayout.IdentityHeightFor(plan, rowFlow, true, true, true, true, chart: true)
            - VerticalLayout.IdentityHeightFor(plan, rowFlow, true, true, true, true, chart: false));

        float plainBand = VerticalLayout.HeroBandHeight(w, rowFlow, true, true, true, true, pulse: false, chart: false);
        float chartBand = VerticalLayout.HeroBandHeight(w, rowFlow, true, true, true, true, pulse: false, chart: true);
        if (!rowFlow)
            Assert.Equal(VerticalLayout.ChartRowHeight + VerticalLayout.IdentityGap, chartBand - plainBand);
        else
            Assert.Equal(
                MathF.Max(0f, VerticalLayout.TitleHeightBudgetFor(w, rowFlow, true, true, true)
                              - VerticalLayout.ChartRowHeight - VerticalLayout.IdentityGap),
                VerticalLayout.TitleHeightBudgetFor(w, rowFlow, true, true, true, chart: true));

        var (plainChrome, plainBlocks) = VerticalLayout.IdentityChrome(true, true, true, false, false, rowFlow);
        var (chartChrome, chartBlocks) = VerticalLayout.IdentityChrome(true, true, true, false, false, rowFlow, chart: true);
        Assert.Equal(plainChrome + VerticalLayout.ChartRowHeight, chartChrome);
        Assert.Equal(plainBlocks + 1, chartBlocks);
    }

    /// <summary>The ALL-flags case, so a new hero row cannot join the column without its own flag: the chrome is the sum
    /// of every row, over nine blocks (the title + eight).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdentityChrome_EnumeratesEveryHeroBlock(bool rowFlow)
    {
        var (h, blocks) = VerticalLayout.IdentityChrome(eyebrow: true, attribution: true, meta: true, pulse: true,
                                                         description: true, rowFlow, chart: true);
        Assert.Equal(VerticalLayout.EyebrowRowHeight + VerticalLayout.AccentRuleRowHeight + VerticalLayout.AttributionRowHeight
                     + VerticalLayout.MetaRowHeight + VerticalLayout.PulseRowHeight + VerticalLayout.ChartRowHeight
                     + VerticalLayout.ActionRowHeight
                     + VerticalLayout.DescriptionMaxLines(rowFlow) * VerticalLayout.DescriptionLineHeight, h);
        Assert.Equal(9, blocks);
    }

    /// <summary>An album and a playlist both reserve a real band at every width — never one the 56-DIP band could not
    /// collapse into.</summary>
    [Fact]
    public void HeroBand_IsCollapsibleAtEveryWidthForBothPageKinds()
    {
        for (float w = LadderMin; w <= LadderMax; w += 1f)
        {
            bool rowFlow = VerticalLayout.RowFlow(w);
            float album = VerticalLayout.HeroBandHeight(w, rowFlow, Album, true, true, false);
            float playlist = VerticalLayout.HeroBandHeight(w, rowFlow, Playlist, true, true, true);
            foreach (float band in new[] { album, playlist })
            {
                Assert.True(band > VerticalLayout.CompactIdentityHeight,
                    $"band {band} cannot collapse into the 56-DIP context band at w={w}");
                Assert.True(VerticalLayout.CollapseDistance(band) > VerticalLayout.CompactRevealBand,
                    $"collapse distance leaves no reveal window at w={w}");
            }
            Assert.True(playlist >= VerticalLayout.HeroBandHeight(w, rowFlow, Playlist, true, true, false));
        }
    }

    [Fact]
    public void HeroBand_UnmeasuredUsesTheFallbackColumn()
    {
        bool rowFlow = VerticalLayout.RowFlow(VerticalLayout.FallbackW);
        Assert.Equal(
            VerticalLayout.HeroBandHeight(VerticalLayout.FallbackW, rowFlow, true, true, true, true),
            VerticalLayout.HeroBandHeight(0f, rowFlow, true, true, true, true));
    }

    // ── issue #78: the identity fill block redistributes slack WITHOUT growing the band ─────────────────────────────

    /// <summary>A bare identity is SHORTER than the artwork at a real row-flow width; the row arm's MinHeight closes that
    /// gap as max(natural, art) — exactly HeroBandHeight's cross size, so the fix adds NO height.</summary>
    [Fact]
    public void IdentityFill_RedistributesInsideTheColumnAndAddsNothingToTheBand()
    {
        const float w = 424f;
        const bool rowFlow = true;
        float art = VerticalLayout.ArtworkFor(w, rowFlow);
        var plan = VerticalLayout.TitleTypeFor(w, rowFlow, title: null, false, false, false);
        float identity = VerticalLayout.IdentityHeightFor(plan, rowFlow, false, false, false, false);
        Assert.True(identity < art, $"fixture assumption broken: identity {identity} is not shorter than art {art}");

        float minH = VerticalLayout.IdentityMinHeightFor(w, rowFlow);
        Assert.Equal(art, minH);

        float occupied = MathF.Max(identity, minH);
        Assert.Equal(art, occupied);

        float band = VerticalLayout.HeroBandHeight(w, rowFlow, false, false, false, false);
        float expectedBand = VerticalLayout.HeroPadFor(w, rowFlow) + occupied + VerticalLayout.HeroBottomPad
                            + VerticalLayout.ExpandedToolbarTopPad + VerticalLayout.ToolbarRowHeight
                            + VerticalLayout.ExpandedToolbarBottomPad;
        Assert.Equal(expectedBand, band);

        Assert.Equal(0f, VerticalLayout.IdentityMinHeightFor(w, rowFlow: false));
        for (float sw = LadderMin; sw <= LadderMax; sw += 1f)
            Assert.Equal(0f, VerticalLayout.IdentityMinHeightFor(sw, rowFlow: false));
    }

    // ── the toolbar reservation (issue #78/#79/#80 parity item D) ────────────────────────────────────────────────────

    /// <summary>The band charges the command bar's BOX (44), never the pill row inside it (32).</summary>
    [Fact]
    public void HeroBand_ReservesTheCommandBarSurfaceNotItsPills()
    {
        Assert.Equal(44f, VerticalLayout.ToolbarRowHeight);
        Assert.Equal(32f, VerticalLayout.ToolbarPillHeight);
        Assert.True(VerticalLayout.ToolbarPillHeight < VerticalLayout.ToolbarRowHeight);

        for (float w = LadderMin; w <= LadderMax; w += 1f)
            foreach (bool rowFlow in new[] { false, true })
            {
                float art = VerticalLayout.ArtworkFor(w, rowFlow);
                var plan = VerticalLayout.TitleTypeFor(w, rowFlow, title: null, true, true, true);
                float identity = VerticalLayout.IdentityHeightFor(plan, rowFlow, true, true, true, true);
                float hero = rowFlow
                    ? MathF.Max(art, identity)
                    : art + VerticalLayout.HeroGapFor(w, rowFlow) + identity;
                float pillOnlyBand = VerticalLayout.HeroPadFor(w, rowFlow) + hero + VerticalLayout.HeroBottomPad
                                    + VerticalLayout.ExpandedToolbarTopPad + VerticalLayout.ToolbarPillHeight
                                    + VerticalLayout.ExpandedToolbarBottomPad;
                float band = VerticalLayout.HeroBandHeight(w, rowFlow, true, true, true, true);
                Assert.Equal(VerticalLayout.ToolbarRowHeight - VerticalLayout.ToolbarPillHeight, band - pillOnlyBand);
            }
    }

    // ── the skeleton's bar geometry ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every bar is max(32, round(measure · fraction)) — the fractions are the hero's own run lengths.</summary>
    [Theory]
    [InlineData(251f, Skeleton.EyebrowFraction, 80f)]        // 80.32 → 80
    [InlineData(251f, Skeleton.AttributionFraction, 100f)]   // 100.4 → 100
    [InlineData(251f, Skeleton.MetaFraction, 156f)]          // 155.62 → 156
    [InlineData(251f, Skeleton.PulseFraction, 88f)]          // 87.85 → 88
    [InlineData(251f, Skeleton.TitleLastLineFraction, 171f)] // 170.68 → 171
    [InlineData(80f, Skeleton.EyebrowFraction, 32f)]         // 25.6 → floored at 32
    [InlineData(0f, Skeleton.MetaFraction, 32f)]
    public void BarWidth_IsARoundedFractionWithAThirtyTwoDipFloor(float measure, float fraction, float expected)
        => Assert.Equal(expected, Skeleton.BarWidth(measure, fraction));

    [Fact]
    public void SkeletonConstants_AreTheHerosOwnShapes()
    {
        Assert.Equal(0.32f, Skeleton.EyebrowFraction);
        Assert.Equal(0.40f, Skeleton.AttributionFraction);
        Assert.Equal(0.62f, Skeleton.MetaFraction);
        Assert.Equal(0.35f, Skeleton.PulseFraction);
        Assert.Equal(0.68f, Skeleton.TitleLastLineFraction);
        Assert.Equal(0.55f, Skeleton.DescriptionLastLineFraction);
        Assert.Equal(32f, Skeleton.BarFloor);
        Assert.Equal(4f, Skeleton.BarRadius);
        Assert.Equal(20f, Skeleton.RuleWidth);
        Assert.Equal(2f, Skeleton.RuleHeight);
        Assert.Equal(1f, Skeleton.RuleRadius);
    }

    /// <summary>N runs at the measure, the last one short; a zero-line block still draws one run.</summary>
    [Fact]
    public void LineWidth_RunsAtTheMeasureAndShortensOnlyTheLastLine()
    {
        const float measure = 352f;
        Assert.Equal(measure, Skeleton.LineWidth(measure, 0, 4, Skeleton.DescriptionLastLineFraction));
        Assert.Equal(measure, Skeleton.LineWidth(measure, 2, 4, Skeleton.DescriptionLastLineFraction));
        Assert.Equal(Skeleton.BarWidth(measure, Skeleton.DescriptionLastLineFraction),
                     Skeleton.LineWidth(measure, 3, 4, Skeleton.DescriptionLastLineFraction));
        Assert.Equal(1, Skeleton.LineCount(0));
        Assert.Equal(Skeleton.BarWidth(measure, Skeleton.TitleLastLineFraction),
                     Skeleton.LineWidth(measure, 0, 0, Skeleton.TitleLastLineFraction));
    }
}
