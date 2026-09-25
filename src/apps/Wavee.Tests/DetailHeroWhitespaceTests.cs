// ── Wavee.Tests/DetailHeroWhitespaceTests.cs — the header hero never reserves height its content will not use ──────
//
// The dead-whitespace bug: `Detail.VerticalLayout.IdentityMinHeightFor` used to force the row-flow identity column
// up to the ARTWORK's edge (issue #78), and a "fill block" grew whichever metadata-ish row happened to be last to
// absorb the difference. That did not remove the surplus between a tall cover and a short column of text — it only
// moved it INSIDE the column, as a blank band between the metadata line (or a bare accent rule, when nothing else
// was reserved) and the action row. This file pins the fix: the identity column's height always follows its own
// content (`Detail.VerticalLayout.IdentityHeightFor`/`IdentityChrome`), never a fixed constant borrowed from the
// artwork, and a slot with no content (no description, no metadata line) contributes exactly zero — not a
// transparent placeholder — in BOTH the side-by-side (row) and vertically-stacked hero variants.
//
// Engine-free: only `Wavee.Detail.VerticalLayout`, no FluentGpu Element ever renders here (CLAUDE.md "no source-text
// tests" — the decision lives in a pure class, and this is that class's contract).

using Xunit;
using VerticalLayout = Wavee.Detail.VerticalLayout;

namespace Wavee.Tests;

public class DetailHeroWhitespaceTests
{
    const float LadderMin = 240f, LadderMax = 1400f;

    // ── the column is never forced past its own content, at any width, in either flow ──────────────────────────────

    [Fact]
    public void IdentityMinHeight_IsAlwaysZero()
    {
        for (float w = LadderMin; w <= LadderMax; w += 4f)
        {
            Assert.Equal(0f, VerticalLayout.IdentityMinHeightFor(w, rowFlow: true));
            Assert.Equal(0f, VerticalLayout.IdentityMinHeightFor(w, rowFlow: false));
        }
    }

    // ── a hero with no description reserves nothing for one ─────────────────────────────────────────────────────────

    /// <summary>The description left the identity column for the band beneath the whole hero row (both flows): the
    /// column's own height no longer changes with or without one — it costs the column EXACTLY zero — and the whole
    /// cost lands on the band, as exactly <c>DescriptionBandHeight(rowFlow, true)</c>, never more (a leftover
    /// reserved band) and never less (a clipped row).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Hero_WithNoDescription_ReservesExactlyZeroForIt(bool rowFlow)
    {
        const float w = 520f;
        var plan = VerticalLayout.TitleTypeFor(w, rowFlow, "Random Access Memories",
            eyebrow: true, attribution: true, meta: true);

        // The identity column: IdentityHeightFor no longer has a description term at all, so there is nothing to
        // compare — the column's height is the same whether the page has a description or not.
        float identity = VerticalLayout.IdentityHeightFor(plan, rowFlow, eyebrow: true, attribution: true, meta: true);
        Assert.True(identity > 0f);

        // No forced MinHeight steps in to backfill the space a missing description leaves.
        Assert.Equal(0f, VerticalLayout.IdentityMinHeightFor(w, rowFlow));

        // The band the skeleton/pre-measure fallback reserves (the PESSIMISTIC null-title plan) matches
        // HeroBandHeight's own published shape exactly (pad + max(art, identity) in row flow, or the identity's
        // full weight in stacked) plus exactly the description BAND — never a fixed extra slice bolted on for
        // "a description that might show up".
        var pessimisticPlan = VerticalLayout.TitleTypeFor(w, rowFlow, title: null,
            eyebrow: true, attribution: true, meta: true);
        float identityP = VerticalLayout.IdentityHeightFor(pessimisticPlan, rowFlow,
            eyebrow: true, attribution: true, meta: true);
        float art = VerticalLayout.ArtworkFor(w, rowFlow);
        float toolbar = VerticalLayout.ExpandedToolbarTopPad + VerticalLayout.ToolbarRowHeight + VerticalLayout.ExpandedToolbarBottomPad;
        float hero = rowFlow ? MathF.Max(art, identityP) : art + VerticalLayout.HeroGapFor(w, rowFlow) + identityP;
        float expectedWithout = VerticalLayout.HeroPadFor(w, rowFlow) + hero + VerticalLayout.HeroBottomPad + toolbar;
        float expectedWith = expectedWithout + VerticalLayout.DescriptionBandHeight(rowFlow, description: true);
        Assert.Equal(expectedWith, VerticalLayout.HeroBandHeight(w, rowFlow, true, true, true, description: true));
        Assert.Equal(expectedWithout, VerticalLayout.HeroBandHeight(w, rowFlow, true, true, true, description: false));

        // Stated once, directly: the whole delta IS the named band height, nothing else.
        Assert.Equal(VerticalLayout.DescriptionBandHeight(rowFlow, description: true),
            VerticalLayout.HeroBandHeight(w, rowFlow, true, true, true, description: true)
            - VerticalLayout.HeroBandHeight(w, rowFlow, true, true, true, description: false));
    }

    // ── a hero with no metadata line reserves nothing for one ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Hero_WithNoMetadataLine_ReservesExactlyZeroForIt(bool rowFlow)
    {
        const float w = 520f;
        var planWithMeta = VerticalLayout.TitleTypeFor(w, rowFlow, "Random Access Memories",
            eyebrow: true, attribution: true, meta: true);
        var planNoMeta = VerticalLayout.TitleTypeFor(w, rowFlow, "Random Access Memories",
            eyebrow: true, attribution: true, meta: false);

        float withMeta = VerticalLayout.IdentityHeightFor(planWithMeta, rowFlow,
            eyebrow: true, attribution: true, meta: true);
        float withoutMeta = VerticalLayout.IdentityHeightFor(planNoMeta, rowFlow,
            eyebrow: true, attribution: true, meta: false);

        // The title plan may itself change (row flow's height budget opens up without the meta row), so isolate
        // the metadata row's own cost through IdentityChrome, which holds the title constant.
        var (chromeWith, blocksWith) = VerticalLayout.IdentityChrome(eyebrow: true, attribution: true, meta: true,
            pulse: false, rowFlow: rowFlow, chart: false);
        var (chromeWithout, blocksWithout) = VerticalLayout.IdentityChrome(eyebrow: true, attribution: true, meta: false,
            pulse: false, rowFlow: rowFlow, chart: false);
        Assert.Equal(VerticalLayout.MetaRowHeight, chromeWith - chromeWithout);
        Assert.Equal(1, blocksWith - blocksWithout);

        // No forced MinHeight backfills the gap a missing metadata line leaves either.
        Assert.Equal(0f, VerticalLayout.IdentityMinHeightFor(w, rowFlow));
        Assert.True(withoutMeta <= withMeta);
    }

    // ── the stacked variant's reserved height always matches what it paints ─────────────────────────────────────────

    /// <summary>Stacked was never forced past its content (<c>IdentityMinHeightFor</c> was already 0 there), so the
    /// band is exactly artwork + gap + the identity's OWN natural sum at every width — never max(art, identity) (that
    /// is the row-flow shape), and never inflated by a MinHeight the render never applies. This is the number the
    /// loaded hero's real, measured `expanded` box must equal once painted.</summary>
    [Fact]
    public void StackedHero_ReservedHeightIsArtworkPlusGapPlusNaturalIdentity_NeverMore()
    {
        const bool rowFlow = false;
        for (float w = LadderMin; w <= LadderMax; w += 4f)
        {
            float bw = VerticalLayout.BucketW(w);
            Assert.Equal(0f, VerticalLayout.IdentityMinHeightFor(bw, rowFlow));

            var plan = VerticalLayout.TitleTypeFor(bw, rowFlow, title: null,
                eyebrow: true, attribution: true, meta: true, pulse: false, chart: false);
            float art = VerticalLayout.ArtworkFor(bw, rowFlow);
            float gap = VerticalLayout.HeroGapFor(bw, rowFlow);
            float identity = VerticalLayout.IdentityHeightFor(plan, rowFlow,
                eyebrow: true, attribution: true, meta: true);
            float pad = VerticalLayout.HeroPadFor(bw, rowFlow);

            float expected = pad + (art + gap + identity) + VerticalLayout.HeroBottomPad
                            + VerticalLayout.ExpandedToolbarTopPad + VerticalLayout.ToolbarRowHeight
                            + VerticalLayout.ExpandedToolbarBottomPad;
            float actual = VerticalLayout.HeroBandHeight(bw, rowFlow, plan,
                eyebrow: true, attribution: true, meta: true, description: false);
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>Dropping the description in the stacked arm shrinks the reserved band by exactly the description's
    /// own cost (its lines plus the one gap boundary its block adds) — the same zero-surplus contract row flow
    /// gets, proved independently for the column layout.</summary>
    [Fact]
    public void StackedHero_WithNoDescription_ReservesExactlyZeroForIt()
    {
        const bool rowFlow = false;
        const float w = 360f;
        var plan = VerticalLayout.TitleTypeFor(w, rowFlow, "Discovery",
            eyebrow: true, attribution: true, meta: true);

        float bandWith = VerticalLayout.HeroBandHeight(w, rowFlow, plan, true, true, true, description: true);
        float bandWithout = VerticalLayout.HeroBandHeight(w, rowFlow, plan, true, true, true, description: false);
        Assert.Equal(VerticalLayout.DescriptionBandHeight(rowFlow, description: true), bandWith - bandWithout);
    }
}
