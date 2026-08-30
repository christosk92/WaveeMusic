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
                float floor = DetailVerticalLayout.HeroPadFor(w)
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
        float identity = DetailVerticalLayout.IdentityHeightFor(w, rowFlow, true, true, true, true);
        float art = DetailVerticalLayout.ArtworkFor(w, rowFlow);
        float hero = rowFlow ? MathF.Max(art, identity) : art + DetailVerticalLayout.HeroGapFor(w) + identity;
        float expected = DetailVerticalLayout.HeroPadFor(w) + hero + DetailVerticalLayout.HeroBottomPad
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
        float bare = DetailVerticalLayout.IdentityHeightFor(w, rowFlow, false, false, false, false);
        Assert.Equal(
            DetailVerticalLayout.TitleLineHeightFor(DetailVerticalLayout.TitleSizeFor(w)) * DetailVerticalLayout.TitleMaxLines
            + DetailVerticalLayout.AccentRuleRowHeight
            + DetailVerticalLayout.ActionRowHeight
            + 2f * DetailVerticalLayout.IdentityGap,
            bare);

        Assert.Equal(bare + DetailVerticalLayout.EyebrowRowHeight + DetailVerticalLayout.IdentityGap,
            DetailVerticalLayout.IdentityHeightFor(w, rowFlow, true, false, false, false));
        Assert.Equal(bare + DetailVerticalLayout.AttributionRowHeight + DetailVerticalLayout.IdentityGap,
            DetailVerticalLayout.IdentityHeightFor(w, rowFlow, false, true, false, false));
        Assert.Equal(bare + DetailVerticalLayout.MetaRowHeight + DetailVerticalLayout.IdentityGap,
            DetailVerticalLayout.IdentityHeightFor(w, rowFlow, false, false, true, false));
        Assert.Equal(
            bare + DetailVerticalLayout.DescriptionMaxLines(rowFlow) * DetailVerticalLayout.DescriptionLineHeight
                 + DetailVerticalLayout.IdentityGap,
            DetailVerticalLayout.IdentityHeightFor(w, rowFlow, false, false, false, true));
        // The daylist flip-countdown row costs exactly its row plus one gap, like every other optional block.
        Assert.Equal(bare + DetailVerticalLayout.PulseRowHeight + DetailVerticalLayout.IdentityGap,
            DetailVerticalLayout.IdentityHeightFor(w, rowFlow, false, false, false, false, pulse: true));
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
}
