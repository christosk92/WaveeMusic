using System;
using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// D49 — the detail page's LOADING geometry must be the loaded page's geometry.
///
/// <para>The defect: a detail page opened as a stack of shimmer ROWS at y=0. In the vertical / hero-system arm (narrow
/// windows, and every width once "Track page layout = Hero" is on) the hero and the list chrome are persistent PREFIX
/// ITEMS of the virtualized list, so they sit INSIDE the <c>Skel.Region</c> boundary and did not exist while the model
/// was Pending; when content landed, several hundred DIP of hero + toolbar + column header materialised above the rows
/// and shoved the whole list down the page.</para>
///
/// <para>The fix is arithmetic, not a second design: <c>DetailSkeleton.VerticalHeroBand</c> composes the same parts at
/// the same sizes from <see cref="DetailVerticalLayout"/>, and the band's HEIGHT is one pure function
/// (<see cref="DetailVerticalLayout.HeroBandHeight"/>) with two consumers — the skeleton and the loaded hero's own
/// pre-measure fallback. This file pins that arithmetic (a re-introduced literal is exactly how the 420/320 constants
/// drifted three compositions behind the hero).</para>
/// </summary>
public class DetailSkeletonGeometryTests
{
    const float LadderMin = 240f, LadderMax = 1400f;

    // The four hero emit predicates, as the two real pages present them.
    const bool Album = true;            // eyebrow "ALBUM · 2019", billed artists, meta line, no blurb
    const bool Playlist = true;         // owner row, meta line, description

    // ── the band's arithmetic ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The band is the padded composition PLUS the toolbar row — never less than the artwork it has to hold.
    /// A band shorter than its own cover is the failure mode the 420/320 constants actually shipped: at 400 DIP the
    /// stacked artwork alone is 280 and the padding another 32.</summary>
    [Fact]
    public void HeroBand_AlwaysClearsTheArtworkPlusPaddingPlusToolbar()
    {
        for (float w = LadderMin; w <= LadderMax; w += 1f)
            foreach (bool rowFlow in new[] { false, true })
            {
                float band = DetailVerticalLayout.HeroBandHeight(w, rowFlow, true, true, true, true);
                float floor = DetailVerticalLayout.HeroPadFor(w, rowFlow)
                            + DetailVerticalLayout.ArtworkFor(w, rowFlow)
                            + DetailVerticalLayout.HeroBottomPad
                            + DetailVerticalLayout.ExpandedToolbarTopPad
                            + DetailVerticalLayout.ToolbarRowHeight
                            + DetailVerticalLayout.ExpandedToolbarBottomPad;
                Assert.True(band >= floor, $"band {band} < artwork floor {floor} at w={w} (rowFlow={rowFlow})");
            }
    }

    /// <summary>The band is the sum of the parts it declares — the same sum the skeleton's boxes lay out. Stacked adds
    /// artwork + gap + identity; row flow bottom-aligns them, so it is the taller of the two.</summary>
    [Theory]
    [InlineData(360f, false)]
    [InlineData(400f, false)]
    [InlineData(539f, false)]
    [InlineData(540f, true)]
    [InlineData(700f, true)]
    [InlineData(1200f, true)]
    public void HeroBand_IsExactlyTheCompositionItDeclares(float w, bool rowFlow)
    {
        // The PESSIMISTIC (title: null) plan — the same one HeroBandHeight(colW, rowFlow, eyebrow, attribution, meta,
        // description, pulse)'s pre-measure overload builds internally — so this hand-rolled sum stays byte-for-byte
        // what that overload (called below) actually computes.
        var plan = DetailVerticalLayout.TitleTypeFor(w, rowFlow, title: null, true, true, true);
        float identity = DetailVerticalLayout.IdentityHeightFor(plan, rowFlow, true, true, true, true);
        float art = DetailVerticalLayout.ArtworkFor(w, rowFlow);
        float hero = rowFlow ? MathF.Max(art, identity) : art + DetailVerticalLayout.HeroGapFor(w, rowFlow) + identity;
        float expected = DetailVerticalLayout.HeroPadFor(w, rowFlow) + hero + DetailVerticalLayout.HeroBottomPad
                       + DetailVerticalLayout.ExpandedToolbarTopPad
                       + DetailVerticalLayout.ToolbarRowHeight
                       + DetailVerticalLayout.ExpandedToolbarBottomPad;
        Assert.Equal(expected, DetailVerticalLayout.HeroBandHeight(w, rowFlow, true, true, true, true));
    }

    /// <summary>The identity column reserves the blocks the hero will actually emit — no more, no less. Title, accent
    /// rule and action row are unconditional; the optional four each cost exactly their row plus one inter-block gap.</summary>
    [Theory]
    [InlineData(700f, true)]
    [InlineData(400f, false)]
    public void IdentityHeight_ChargesOnlyForTheBlocksTheHeroEmits(float w, bool rowFlow)
    {
        // A PESSIMISTIC (title: null) plan per flag combination — TitleHeightBudgetFor's own chrome sum changes with
        // eyebrow/attribution/meta/pulse, so the plan built for a combination is not always identical to the "bare"
        // plan even though (verified below, and in DetailVerticalLayoutTests) the SIZE the null-title plan resolves
        // to does not actually move as a result at any width/flow this ladder produces — the fluid cap or the (huge,
        // starved) width-fit term is what is actually binding, not the height budget, for an absent title. Building a
        // fresh plan per call — rather than reusing "bare" everywhere — is what keeps this test honest about that
        // rather than assuming it.
        var barePlan = DetailVerticalLayout.TitleTypeFor(w, rowFlow, title: null, false, false, false);
        float bare = DetailVerticalLayout.IdentityHeightFor(barePlan, rowFlow, false, false, false, false);
        Assert.Equal(
            barePlan.BlockHeight
            + DetailVerticalLayout.AccentRuleRowHeight
            + DetailVerticalLayout.ActionRowHeight
            + 2f * DetailVerticalLayout.IdentityGap,
            bare);

        var eyebrowPlan = DetailVerticalLayout.TitleTypeFor(w, rowFlow, title: null, true, false, false);
        Assert.Equal(bare + DetailVerticalLayout.EyebrowRowHeight + DetailVerticalLayout.IdentityGap,
            DetailVerticalLayout.IdentityHeightFor(eyebrowPlan, rowFlow, true, false, false, false));

        var attributionPlan = DetailVerticalLayout.TitleTypeFor(w, rowFlow, title: null, false, true, false);
        Assert.Equal(bare + DetailVerticalLayout.AttributionRowHeight + DetailVerticalLayout.IdentityGap,
            DetailVerticalLayout.IdentityHeightFor(attributionPlan, rowFlow, false, true, false, false));

        var metaPlan = DetailVerticalLayout.TitleTypeFor(w, rowFlow, title: null, false, false, true);
        Assert.Equal(bare + DetailVerticalLayout.MetaRowHeight + DetailVerticalLayout.IdentityGap,
            DetailVerticalLayout.IdentityHeightFor(metaPlan, rowFlow, false, false, true, false));

        // Description never moves the plan — TitleHeightBudgetFor always calls IdentityChrome with description: false
        // (see that method's doc: the description is a tail deliberately excluded from the title's height budget) —
        // so it reuses barePlan rather than a plan built with a description flag that does not exist on that method.
        Assert.Equal(
            bare + DetailVerticalLayout.DescriptionMaxLines(rowFlow) * DetailVerticalLayout.DescriptionLineHeight
                 + DetailVerticalLayout.IdentityGap,
            DetailVerticalLayout.IdentityHeightFor(barePlan, rowFlow, false, false, false, true));

        // The daylist flip-countdown row costs exactly its row plus one gap, like every other optional block.
        var pulsePlan = DetailVerticalLayout.TitleTypeFor(w, rowFlow, title: null, false, false, false, pulse: true);
        Assert.Equal(bare + DetailVerticalLayout.PulseRowHeight + DetailVerticalLayout.IdentityGap,
            DetailVerticalLayout.IdentityHeightFor(pulsePlan, rowFlow, false, false, false, false, pulse: true));
    }

    /// <summary>An album (eyebrow + billed artists + meta, no blurb) and a playlist (owner + meta + description) both
    /// reserve a real band at every width the page can be — never a hairline, never something the 56-DIP context band
    /// could not collapse into.</summary>
    [Fact]
    public void HeroBand_IsCollapsibleAtEveryWidthForBothPageKinds()
    {
        for (float w = LadderMin; w <= LadderMax; w += 1f)
        {
            bool rowFlow = DetailVerticalLayout.RowFlow(w);
            float album = DetailVerticalLayout.HeroBandHeight(w, rowFlow, Album, true, true, false);
            float playlist = DetailVerticalLayout.HeroBandHeight(w, rowFlow, Playlist, true, true, true);
            foreach (float band in new[] { album, playlist })
            {
                Assert.True(band > DetailVerticalLayout.CompactIdentityHeight,
                    $"band {band} cannot collapse into the 56-DIP context band at w={w}");
                Assert.True(DetailVerticalLayout.CollapseDistance(band) > DetailVerticalLayout.CompactRevealBand,
                    $"collapse distance leaves no reveal window at w={w}");
            }
            // A description costs the page height; it can never make the hero SHORTER.
            Assert.True(playlist >= DetailVerticalLayout.HeroBandHeight(w, rowFlow, Playlist, true, true, false));
        }
    }

    /// <summary>Unmeasured width falls back to the same nominal column every other resolver in this file uses, so the
    /// first frame after a cold navigation reserves a plausible band rather than a degenerate one.</summary>
    [Fact]
    public void HeroBand_UnmeasuredUsesTheFallbackColumn()
    {
        bool rowFlow = DetailVerticalLayout.RowFlow(DetailVerticalLayout.FallbackW);
        Assert.Equal(
            DetailVerticalLayout.HeroBandHeight(DetailVerticalLayout.FallbackW, rowFlow, true, true, true, true),
            DetailVerticalLayout.HeroBandHeight(0f, rowFlow, true, true, true, true));
    }

    // ── issue #78: the identity fill block redistributes slack WITHOUT growing the band ─────────────────────────────

    /// <summary>The load-bearing claim of issue #78's fix: a bare identity (title + rule + action row only — no
    /// attribution/meta/description) is SHORTER than the artwork beside it at a real row-flow width. The row arm's
    /// MinHeight (<see cref="DetailVerticalLayout.IdentityMinHeightFor"/>) closes that gap by making the identity
    /// column occupy exactly <c>max(natural, art)</c> — which is byte-for-byte what
    /// <see cref="DetailVerticalLayout.HeroBandHeight"/> already computes for the row's cross size, so the fix adds NO
    /// height to the band. The surplus it opens is redistributed INSIDE the identity column, by the last block's
    /// Grow = 1 (<c>DetailVerticalHero.Build</c>'s fill-block), which this pure-arithmetic file cannot see — the DSL
    /// element tree lives outside it — but whose non-effect on the band this test pins.</summary>
    [Fact]
    public void IdentityFill_RedistributesInsideTheColumnAndAddsNothingToTheBand()
    {
        const float w = 424f;
        const bool rowFlow = true;
        float art = DetailVerticalLayout.ArtworkFor(w, rowFlow);
        var plan = DetailVerticalLayout.TitleTypeFor(w, rowFlow, title: null, false, false, false);
        float identity = DetailVerticalLayout.IdentityHeightFor(plan, rowFlow, false, false, false, false);
        Assert.True(identity < art, $"fixture assumption broken: identity {identity} is not shorter than art {art}");

        float minH = DetailVerticalLayout.IdentityMinHeightFor(w, rowFlow);
        Assert.Equal(art, minH);

        // The occupied height once MinHeight applies is max(identity, minH) = art — exactly what HeroBandHeight's own
        // max(art, identity) already resolves to, so the band is unchanged by the fix.
        float occupied = MathF.Max(identity, minH);
        Assert.Equal(art, occupied);

        float band = DetailVerticalLayout.HeroBandHeight(w, rowFlow, false, false, false, false);
        float expectedBand = DetailVerticalLayout.HeroPadFor(w, rowFlow) + occupied + DetailVerticalLayout.HeroBottomPad
                            + DetailVerticalLayout.ExpandedToolbarTopPad + DetailVerticalLayout.ToolbarRowHeight
                            + DetailVerticalLayout.ExpandedToolbarBottomPad;
        Assert.Equal(expectedBand, band);

        // Stacked flow has no cross-axis mismatch to close (the artwork sits ABOVE the identity, not beside it), so
        // its MinHeight is always 0 and can never inflate the stacked band.
        Assert.Equal(0f, DetailVerticalLayout.IdentityMinHeightFor(w, rowFlow: false));
        for (float sw = LadderMin; sw <= LadderMax; sw += 1f)
            Assert.Equal(0f, DetailVerticalLayout.IdentityMinHeightFor(sw, rowFlow: false));
    }

    // ── issue #78/#79/#80's skeleton/live parity item (D): the toolbar reservation ───────────────────────────────────

    /// <summary>The band charges the real <c>CommandBarSurface</c> BOX (44 DIP), never the pill row it draws inside
    /// (32 DIP) — the exact 12-DIP under-reserve the old flat <c>ToolbarRowHeight = 32</c> constant shipped. Swapping
    /// one constant for the other in the same formula must change the band by exactly their difference, at every
    /// width and flow — the structural guarantee that replaces the comment <c>DetailTracks.CommandBarSurface</c> used
    /// to rely on.</summary>
    [Fact]
    public void HeroBand_ReservesTheCommandBarSurfaceNotItsPills()
    {
        Assert.Equal(44f, DetailVerticalLayout.ToolbarRowHeight);
        Assert.Equal(32f, DetailVerticalLayout.ToolbarPillHeight);
        Assert.True(DetailVerticalLayout.ToolbarPillHeight < DetailVerticalLayout.ToolbarRowHeight);

        for (float w = LadderMin; w <= LadderMax; w += 1f)
            foreach (bool rowFlow in new[] { false, true })
            {
                float art = DetailVerticalLayout.ArtworkFor(w, rowFlow);
                var plan = DetailVerticalLayout.TitleTypeFor(w, rowFlow, title: null, true, true, true);
                float identity = DetailVerticalLayout.IdentityHeightFor(plan, rowFlow, true, true, true, true);
                float hero = rowFlow
                    ? MathF.Max(art, identity)
                    : art + DetailVerticalLayout.HeroGapFor(w, rowFlow) + identity;
                float pillOnlyBand = DetailVerticalLayout.HeroPadFor(w, rowFlow) + hero + DetailVerticalLayout.HeroBottomPad
                                    + DetailVerticalLayout.ExpandedToolbarTopPad + DetailVerticalLayout.ToolbarPillHeight
                                    + DetailVerticalLayout.ExpandedToolbarBottomPad;
                float band = DetailVerticalLayout.HeroBandHeight(w, rowFlow, true, true, true, true);
                Assert.Equal(DetailVerticalLayout.ToolbarRowHeight - DetailVerticalLayout.ToolbarPillHeight,
                    band - pillOnlyBand);
            }
    }
}
