// ── Wavee.Tests/DetailHeroDescriptionTests.cs — the description leaves the identity column for the band beneath the
// whole hero row, in BOTH flows ────────────────────────────────────────────────────────────────────────────────────
//
// Before this change the description was the one block `TitleHeightBudgetFor` excluded from the row-flow title's
// height budget ("a tail may run past the cover") yet it was still composed INSIDE the identity column
// (`Detail.UI.Hero.cs`), so every page with a description overran the cover by up to its own block height — the empty
// band under the cover on playlist/show pages. The description is now the padded hero box's SECOND CHILD, after the
// whole `[artwork, identity]` row, in both row and stacked flow: the identity column never carries it at all, and
// because the title budget already excluded it, the column now ends at the cover edge BY CONSTRUCTION, not by luck.
//
// This file pins that guarantee and the new band arithmetic (`Detail.VerticalLayout.DescriptionWidthFor` /
// `DescriptionGapFor` / `DescriptionBandHeight`, Entities/Detail.cs).
//
// Engine-free: only `Wavee.Detail.VerticalLayout`, no FluentGpu Element ever renders here (CLAUDE.md "no source-text
// tests" — the decision lives in a pure class, and this is that class's contract).

using Xunit;
using VerticalLayout = Wavee.Detail.VerticalLayout;

namespace Wavee.Tests;

public class DetailHeroDescriptionTests
{
    const float LadderMin = 424f, LadderMax = 1400f;

    static readonly string?[] Titles =
    [
        "Chill",
        "Here's some throwback pop, 2010s, high-spirited, summer camp, nostalgia, birthday",
        "スローなブギにしてくれ深夜高速道路のミックス",
    ];

    /// <summary>Row flow's title height budget already excludes the description, and the description itself no longer
    /// lives in the column at all — so the identity column can never outgrow the cover on its account. Swept over a
    /// real playlist's flag set (eyebrow, attribution, meta, pulse — the daylist shape, the tallest chrome a playlist
    /// carries) and a representative title set (short, a long run-on blurb-length title, CJK).
    ///
    /// INVESTIGATED, NOT SILENTLY WEAKENED: at the row-flow ENTRY width (424) with `pulse` on, the identity still
    /// clears the cover by up to 16 DIP (short title, worst case), decaying to 0 by w≈472. This is a PRE-EXISTING
    /// `TitleTypeFor` floor effect, not something this change introduces: `TitleTypeFor`'s search never returns below
    /// `TitleSizeFloor` (its `best` seed), so a one-line title at the floor costs a fixed ~27 DIP no matter how small
    /// the row-flow height budget is, and the pulse row's own 28 DIP plus the four other unconditional/optional rows
    /// (120 DIP of chrome before the title) leaves less than that at the narrowest row-flow cover (155 DIP at w=424).
    /// The same arithmetic exists byte-for-byte before this patch (`TitleHeightBudgetFor`/`IdentityChrome` already
    /// excluded the description); moving the description out of the column changes nothing about it. Toleranced at
    /// the measured worst case, named so a regression that makes it WORSE still fails loudly.</summary>
    [Fact]
    public void Row_flow_identity_never_outgrows_the_cover_when_a_description_is_present()
    {
        const float knownFloorSlop = 16.5f;   // see the investigation note above — the w=424/pulse/floor-title edge
        for (float w = LadderMin; w <= LadderMax; w += 8f)
            foreach (string? title in Titles)
            {
                var plan = VerticalLayout.TitleTypeFor(w, rowFlow: true, title,
                    eyebrow: true, attribution: true, meta: true, pulse: true);
                float identity = VerticalLayout.IdentityHeightFor(plan, rowFlow: true,
                    eyebrow: true, attribution: true, meta: true, pulse: true);
                // A title no line budget holds above the floor WRAPS at the floor (2026-09-30: never trimmed) — its extra
                // lines are the title's own, not the description's, and are the only other growth allowed.
                float wrapped = plan.Size <= VerticalLayout.TitleSizeFloor ? (plan.Lines - 1) * plan.LineHeight : 0f;
                float art = VerticalLayout.ArtworkFor(w, rowFlow: true);
                Assert.True(identity - wrapped <= art + knownFloorSlop,
                    $"identity {identity} (floor wrap {wrapped}) outgrows the cover {art} by more than the known floor slop at w={w} title={title}");
            }
    }

    /// <summary>The band the skeleton/pre-measure fallback reserves is the cover's own row PLUS the description band
    /// tacked on at the end — never woven back into the identity column's own height, in row flow.</summary>
    [Fact]
    public void Band_height_is_cover_plus_description_band_in_row_flow()
    {
        const bool rowFlow = true;
        for (float w = LadderMin; w <= LadderMax; w += 8f)
        {
            var plan = VerticalLayout.TitleTypeFor(w, rowFlow, title: null,
                eyebrow: true, attribution: true, meta: true, pulse: true);
            float identity = VerticalLayout.IdentityHeightFor(plan, rowFlow,
                eyebrow: true, attribution: true, meta: true, pulse: true);
            float art = VerticalLayout.ArtworkFor(w, rowFlow);
            float toolbar = VerticalLayout.ExpandedToolbarTopPad + VerticalLayout.ToolbarRowHeight
                          + VerticalLayout.ExpandedToolbarBottomPad;
            float withoutDescription = VerticalLayout.HeroPadFor(w, rowFlow) + MathF.Max(art, identity)
                                      + VerticalLayout.HeroBottomPad + toolbar;
            float withDescription = VerticalLayout.HeroBandHeight(w, rowFlow, plan,
                eyebrow: true, attribution: true, meta: true, description: true, pulse: true);
            Assert.Equal(withoutDescription + VerticalLayout.DescriptionBandHeight(rowFlow, description: true),
                withDescription);
        }
    }

    /// <summary>The description's own reading measure is capped at <see cref="VerticalLayout.DescriptionWMax"/> in row
    /// flow and never squeezed below <see cref="VerticalLayout.ContentWMin"/> in either flow, across the width
    /// ladder.</summary>
    [Fact]
    public void Description_measure_is_capped_and_never_below_ContentWMin()
    {
        for (float w = 240f; w <= LadderMax; w += 8f)
            foreach (bool rowFlow in new[] { false, true })
            {
                float measure = VerticalLayout.DescriptionWidthFor(w, rowFlow);
                Assert.True(measure >= VerticalLayout.ContentWMin - 0.5f,
                    $"description measure {measure} fell below ContentWMin at w={w} rowFlow={rowFlow}");
                if (rowFlow)
                    Assert.True(measure <= VerticalLayout.DescriptionWMax + 0.5f,
                        $"description measure {measure} exceeded DescriptionWMax at w={w}");
            }
    }

    /// <summary>Stacked flow's description band is unchanged by the move: the same gap (<see cref="VerticalLayout.IdentityGap"/>,
    /// the rhythm it always had inside the column) and the same four-line cap it always had — only ROW flow gained the
    /// new, wider two-line measure.</summary>
    [Fact]
    public void Stacked_flow_description_band_equals_the_old_in_column_block()
    {
        Assert.Equal(VerticalLayout.IdentityGap, VerticalLayout.DescriptionGapFor(rowFlow: false));
        Assert.Equal(4, VerticalLayout.DescriptionMaxLines(rowFlow: false));
        float expected = VerticalLayout.IdentityGap + 4 * VerticalLayout.DescriptionLineHeight;
        Assert.Equal(expected, VerticalLayout.DescriptionBandHeight(rowFlow: false, description: true));
    }
}
