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

    /// <summary>Dropping the description costs the identity EXACTLY the description block's own height — never more
    /// (a leftover reserved band) and never less (a clipped row) — in both flows.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Hero_WithNoDescription_ReservesExactlyZeroForIt(bool rowFlow)
    {
        const float w = 520f;
        var plan = VerticalLayout.TitleTypeFor(w, rowFlow, "Random Access Memories",
            eyebrow: true, attribution: true, meta: true);

        float withDescription = VerticalLayout.IdentityHeightFor(plan, rowFlow,
            eyebrow: true, attribution: true, meta: true, description: true);
        float withoutDescription = VerticalLayout.IdentityHeightFor(plan, rowFlow,
            eyebrow: true, attribution: true, meta: true, description: false);

        // The description's own lines, PLUS the one extra gap boundary its block adds between it and the actions
        // row above — no more, no less.
        float descriptionCost = VerticalLayout.DescriptionMaxLines(rowFlow) * VerticalLayout.DescriptionLineHeight
                               + VerticalLayout.IdentityGap;
        Assert.Equal(descriptionCost, withDescription - withoutDescription);

        // No forced MinHeight steps in to backfill the gap the missing description leaves.
        Assert.Equal(0f, VerticalLayout.IdentityMinHeightFor(w, rowFlow));

        // The band the skeleton/pre-measure fallback reserves (the PESSIMISTIC null-title plan) matches
        // HeroBandHeight's own published shape exactly (pad + max(art, identity) in row flow, or the identity's
        // full weight in stacked) — never a fixed extra slice bolted on for "a description that might show up".
        var pessimisticPlan = VerticalLayout.TitleTypeFor(w, rowFlow, title: null,
            eyebrow: true, attribution: true, meta: true);
        float identityWith = VerticalLayout.IdentityHeightFor(pessimisticPlan, rowFlow,
            eyebrow: true, attribution: true, meta: true, description: true);
        float identityWithout = VerticalLayout.IdentityHeightFor(pessimisticPlan, rowFlow,
            eyebrow: true, attribution: true, meta: true, description: false);
        float art = VerticalLayout.ArtworkFor(w, rowFlow);
        float toolbar = VerticalLayout.ExpandedToolbarTopPad + VerticalLayout.ToolbarRowHeight + VerticalLayout.ExpandedToolbarBottomPad;
        float expectedWith = VerticalLayout.HeroPadFor(w, rowFlow)
            + (rowFlow ? MathF.Max(art, identityWith) : art + VerticalLayout.HeroGapFor(w, rowFlow) + identityWith)
            + VerticalLayout.HeroBottomPad + toolbar;
        float expectedWithout = VerticalLayout.HeroPadFor(w, rowFlow)
            + (rowFlow ? MathF.Max(art, identityWithout) : art + VerticalLayout.HeroGapFor(w, rowFlow) + identityWithout)
            + VerticalLayout.HeroBottomPad + toolbar;
        Assert.Equal(expectedWith, VerticalLayout.HeroBandHeight(w, rowFlow, true, true, true, description: true));
        Assert.Equal(expectedWithout, VerticalLayout.HeroBandHeight(w, rowFlow, true, true, true, description: false));
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
            eyebrow: true, attribution: true, meta: true, description: false);
        float withoutMeta = VerticalLayout.IdentityHeightFor(planNoMeta, rowFlow,
            eyebrow: true, attribution: true, meta: false, description: false);

        // The title plan may itself change (row flow's height budget opens up without the meta row), so isolate
        // the metadata row's own cost through IdentityChrome, which holds the title constant.
        var (chromeWith, blocksWith) = VerticalLayout.IdentityChrome(eyebrow: true, attribution: true, meta: true,
            pulse: false, description: false, rowFlow: rowFlow, chart: false);
        var (chromeWithout, blocksWithout) = VerticalLayout.IdentityChrome(eyebrow: true, attribution: true, meta: false,
            pulse: false, description: false, rowFlow: rowFlow, chart: false);
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
                eyebrow: true, attribution: true, meta: true, description: false);
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
        float descriptionCost = VerticalLayout.DescriptionMaxLines(rowFlow) * VerticalLayout.DescriptionLineHeight
                               + VerticalLayout.IdentityGap;
        Assert.Equal(descriptionCost, bandWith - bandWithout);
    }
}
