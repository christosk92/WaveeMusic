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
    [InlineData(539f, false)]
    [InlineData(540f, true)]
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
    /// 540; leaving it needs a further 24-DIP drop — the same asymmetry the page-mode ladder uses.</summary>
    [Fact]
    public void RowFlow_UsesResizeHysteresis()
    {
        Assert.False(DetailVerticalLayout.RowFlow(539f, current: false, initialized: true));
        Assert.True(DetailVerticalLayout.RowFlow(540f, current: false, initialized: true));
        // …and once beside, it holds through the dip band.
        Assert.True(DetailVerticalLayout.RowFlow(539f, current: true, initialized: true));
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
    [InlineData(540f, 200f)]    // 0.34 × (540 − 48 − 24) = 159 → the 200 floor
    [InlineData(700f, 214f)]    // 0.34 × 628 = 213.5 → 214, inside the band
    [InlineData(820f, 240f)]    // 0.34 × 748 = 254.3 → the 240 ceiling
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
            float inner = w - 2f * DetailVerticalLayout.HeroPadFor(w);
            float used = DetailVerticalLayout.ArtworkFor(w, true) + DetailVerticalLayout.HeroGapFor(w)
                       + DetailVerticalLayout.ContentWidthFor(w, true);
            Assert.True(used <= inner + 0.01f, $"row hero overflows at w={w}: {used} > {inner}");
        }
    }

    [Theory]
    [InlineData(240f, 16f, 16f)]
    [InlineData(419f, 16f, 16f)]
    [InlineData(420f, 24f, 24f)]
    [InlineData(900f, 24f, 24f)]
    public void Padding_TightensOnlyAtPhoneWidth(float w, float pad, float gap)
    {
        Assert.Equal(pad, DetailVerticalLayout.HeroPadFor(w));
        Assert.Equal(gap, DetailVerticalLayout.HeroGapFor(w));
    }

    // ── the title RUNG ───────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(240f, 20f, 28f)]
    [InlineData(359f, 20f, 28f)]
    [InlineData(360f, 28f, 36f)]
    [InlineData(639f, 28f, 36f)]
    [InlineData(640f, 40f, 52f)]
    [InlineData(1400f, 40f, 52f)]
    public void TitleRung_IsSteppedAndCarriesItsLineHeight(float w, float size, float lineHeight)
    {
        Assert.Equal(size, DetailVerticalLayout.TitleSizeFor(w));
        Assert.Equal(lineHeight, DetailVerticalLayout.TitleLineHeightFor(size));
    }

    /// <summary>THREE sizes across the whole ladder, and every one of them a rung of the engine ramp with its paired
    /// line height. The composition this replaced sized the title from the TITLE'S LENGTH across four off-ramp values
    /// (42/34/28/24), so no two albums opened at the same typographic weight.</summary>
    [Fact]
    public void TitleRung_HasExactlyThreeValuesAcrossTheLadder()
    {
        var seen = new SortedSet<float>();
        for (float w = LadderMin; w <= LadderMax; w += 1f) seen.Add(DetailVerticalLayout.TitleSizeFor(w));
        Assert.Equal(new[] { 20f, 28f, 40f }, seen);

        // Each is a ramp pair (Subtitle 20/28 · Title 28/36 · TitleLarge 40/52) — never a bare size.
        Assert.Equal(28f, DetailVerticalLayout.TitleLineHeightFor(20f));
        Assert.Equal(36f, DetailVerticalLayout.TitleLineHeightFor(28f));
        Assert.Equal(52f, DetailVerticalLayout.TitleLineHeightFor(40f));
    }

    /// <summary>Monotone: a wider page never gets a SMALLER title.</summary>
    [Fact]
    public void TitleRung_IsMonotoneInWidth()
    {
        float prev = DetailVerticalLayout.TitleSizeFor(LadderMin);
        for (float w = LadderMin; w <= LadderMax; w += 1f)
        {
            float size = DetailVerticalLayout.TitleSizeFor(w);
            Assert.True(size >= prev, $"title rung dropped at w={w}");
            prev = size;
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
