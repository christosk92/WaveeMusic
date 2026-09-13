using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Features.Detail;

namespace Wavee;

// ── THE DETAIL PAGE'S LOADING GEOMETRY ────────────────────────────────────────────────────────────────────────────
//
// D49. A detail page used to open as a stack of shimmer ROWS starting at the very top of the pane: in the vertical /
// hero-system arm (narrow windows, and EVERY width once the "Track page layout = Hero" preference is on) the hero and
// the list chrome are persistent PREFIX ITEMS of the virtualized list — items 0 and 1 — which means they live INSIDE
// the Skel.Region boundary and simply do not exist while the model is Pending. When the content landed, several
// hundred DIP of hero plus the toolbar plus the column header materialised above the rows and shoved the entire list
// down the page. (The two-column arm never had this: its rail is a sibling of the track list and its chrome is a
// sibling of the list body, so both are outside the boundary and were always reserved.)
//
// The fix is geometry, not a second design: this file composes the SAME parts, at the SAME sizes, from the SAME pure
// resolver the loaded hero uses (DetailVerticalLayout — artwork edge, identity measure, title rung, padding, gap,
// description cap). Nothing here picks a number of its own. The engine's SkeletonDeriver turns these declared boxes
// into shimmer bars, so the swap→real contract (Skel.Region + SkelReveal.FadeOnly) is untouched.
static class DetailSkeleton
{
    /// <summary>The vertical/hero arm's reserved band: the padded artwork + identity composition (stacked or
    /// side-by-side, whichever <paramref name="rowFlow"/> says), then the list toolbar row that rides under it.
    /// Structurally identical to <c>DetailVerticalHero.Build</c>'s expanded presentation, so the loaded hero fades
    /// into a slot that is already its size.
    ///
    /// <para><paramref name="colW"/> is the track column's measured width — the same value the hero itself derives
    /// from — and the four flags are the hero's own emit predicates for THIS model (see
    /// <c>TrackList.HeroHas*</c>), so an album's eyebrow and a playlist's owner row each reserve their real row.</para></summary>
    /// <param name="previewArt">When the model already carries a usable preview cover (the grid-click nav path always
    /// does), the caller's factory for the REAL hero artwork block — same <c>Surfaces.Artwork</c>, same url, same
    /// bucket the unmeasured loaded hero uses (<c>DetailVerticalLayout.ArtworkDecodePx(_, widthMeasured: false)</c>) —
    /// wrapped so the deriver leaves it unshimmered (see <c>TrackList.PreviewHeroArt</c>). Invoked with the exact
    /// artwork edge this method derives, so the caller never re-guesses it. Null (deep link, no preview cover yet)
    /// keeps the plain gray reserved box below — the pre-fix behaviour.</param>
    public static Element VerticalHeroBand(float colW, bool rowFlow, float compactLeft,
        bool eyebrow, bool attribution, bool meta, bool description, bool pulse = false,
        Func<float, Element>? previewArt = null)
    {
        float bw = DetailVerticalLayout.BucketW(colW);
        float pad = DetailVerticalLayout.HeroPadFor(bw, rowFlow);
        float gap = DetailVerticalLayout.HeroGapFor(bw, rowFlow);
        float art = DetailVerticalLayout.ArtworkFor(bw, rowFlow);
        float contentW = DetailVerticalLayout.ContentWidthFor(bw, rowFlow);
        // The PESSIMISTIC type plan — title: null — the same one DetailVerticalLayout.HeroBandHeight's pre-measure
        // overload builds internally: the skeleton has no text to shape, so it reserves the tallest single-line
        // title any real title at this width could plausibly need (a one-line title sized to the fluid cap), never a
        // guessed representative string. Building it explicitly here (rather than just calling the plan-less
        // HeroBandHeight overload below) is what lets the shimmer's OWN title bar mirror the plan's line count/height
        // and lets the identity gap mirror DetailVerticalHero's IdentityGapFor — "the shimmer matches the hero
        // block-for-block" needs the same plan on both sides, not just the same total height.
        var titlePlan = DetailVerticalLayout.TitleTypeFor(bw, rowFlow, title: null, eyebrow, attribution, meta, pulse);
        float identityGap = DetailVerticalLayout.IdentityGapFor(bw, rowFlow, titlePlan,
            eyebrow, attribution, meta, description, pulse);

        // The identity column, block for block, in DetailVerticalHero's order. The LAST block above the action row is
        // the same "fill block" the live hero grows — mirrored here so the loading band and the loaded band never
        // disagree about where the row arm's surplus goes (issue #78).
        var blocks = new List<Element>(7);
        if (eyebrow) blocks.Add(Bar(Fraction(contentW, 0.32f), DetailVerticalLayout.EyebrowRowHeight));
        blocks.Add(TitleBlock(titlePlan));
        blocks.Add(new BoxEl
        {
            // Surfaces.AccentRule: a 20×2 mark with a 2-DIP top margin, left-aligned under the title.
            Width = Surfaces.AccentRuleWidth, Height = Surfaces.AccentRuleHeight, AlignSelf = FlexAlign.Start,
            Margin = new Edges4(0f, Surfaces.AccentRuleGap, 0f, 0f),
            Corners = CornerRadius4.All(1f),
        });
        if (attribution) blocks.Add(Bar(Fraction(contentW, 0.4f), DetailVerticalLayout.AttributionRowHeight));
        if (meta) blocks.Add(Bar(Fraction(contentW, 0.62f), DetailVerticalLayout.MetaRowHeight));
        if (pulse) blocks.Add(Bar(Fraction(contentW, 0.35f), DetailVerticalLayout.PulseRowHeight));
        if (blocks.Count > 0 && blocks[^1] is BoxEl fillBlock) blocks[^1] = fillBlock with { Grow = 1f };
        blocks.Add(ActionRow());
        if (description) blocks.Add(DescriptionBlock(contentW, DetailVerticalLayout.DescriptionMaxLines(rowFlow)));

        Element identity = new BoxEl
        {
            Direction = 1, Gap = identityGap, AlignItems = FlexAlign.Stretch,
            Width = rowFlow ? float.NaN : contentW,
            Grow = rowFlow ? 1f : 0f, Basis = rowFlow ? 0f : float.NaN, MinWidth = 0f,
            MinHeight = DetailVerticalLayout.IdentityMinHeightFor(bw, rowFlow),
            Children = blocks.ToArray(),
        };

        Element artwork = previewArt?.Invoke(art) ?? new BoxEl
        {
            Width = art, Height = art, Shrink = 0f,
            Corners = CornerRadius4.All(Radii.Card),
        };

        // AlignItems = Start in BOTH arms — matching the live hero exactly (DetailVerticalHero.cs). The row arm used
        // to bottom-align (FlexAlign.End) here while the live hero used Start, so the shimmer laid its cover against a
        // different baseline than the thing it reserves for.
        Element hero = new BoxEl
        {
            Direction = rowFlow ? (byte)0 : (byte)1,
            Gap = gap,
            AlignItems = FlexAlign.Start,
            Children = [artwork, identity],
        };

        return new BoxEl
        {
            Key = "detail-skeleton:hero",
            Direction = 1,
            // The pure sum of everything above — a floor, never a cap, so a wrapped action row or a taller run can
            // still grow the band. It is the SAME number the loaded hero's collapse binds use before its first
            // measure, which is what makes "the skeleton reserved what the hero needs" a testable statement.
            MinHeight = DetailVerticalLayout.HeroBandHeight(colW, rowFlow, titlePlan, eyebrow, attribution, meta, description, pulse),
            Children =
            [
                new BoxEl
                {
                    Direction = 1,
                    Padding = new Edges4(pad, pad, pad, DetailVerticalLayout.HeroBottomPad),
                    Children = [hero],
                },
                new BoxEl
                {
                    Direction = 1,
                    Padding = new Edges4(compactLeft, DetailVerticalLayout.ExpandedToolbarTopPad,
                        compactLeft, DetailVerticalLayout.ExpandedToolbarBottomPad),
                    Children = [ToolbarRow()],
                },
            ],
        };
    }

    // The hero title reserves exactly the PLAN's own line count and line height (a pessimistic one-line plan for a
    // null title — see the plan's construction above) rather than a flat two-line cap: a plan that changes shape when
    // the real title lands is exactly the shove this file exists to prevent, so the shimmer has to commit to the same
    // line count the live hero would commit to for a title this width could plausibly show. Zero gap between the
    // lines — the run's own line height is the spacing.
    static Element TitleBlock(DetailVerticalLayout.TitleTypePlan plan) => new BoxEl
    {
        Direction = 1, Gap = 0f,
        Children = BuildLines(plan.WrapWidth, plan.LineHeight, plan.Lines, 0.68f),
    };

    static Element DescriptionBlock(float contentW, int lines) => new BoxEl
    {
        Direction = 1, Gap = 0f,
        Children = BuildLines(contentW, DetailVerticalLayout.DescriptionLineHeight, lines, 0.55f),
    };

    // N stacked runs at the measure's width, the last one short — the shape any wrapped paragraph actually has.
    static Element[] BuildLines(float contentW, float lineHeight, int count, float lastFraction)
    {
        var lines = new Element[Math.Max(1, count)];
        for (int i = 0; i < lines.Length; i++)
            lines[i] = Bar(i == lines.Length - 1 ? Fraction(contentW, lastFraction) : contentW, lineHeight);
        return lines;
    }

    // The accent Play capsule leads, then the quiet 32-DIP satellites — the hero's own action grammar (WaveeCta).
    static Element ActionRow() => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Margin = new Edges4(0f, Spacing.XS, 0f, 0f),
        Height = WaveeCta.PillHeight,
        Children =
        [
            new BoxEl { Width = 104f, Height = WaveeCta.PillHeight, Corners = CornerRadius4.All(WaveeCta.PillHeight / 2f) },
            Satellite(), Satellite(), Satellite(), Satellite(),
        ],
    };

    static Element Satellite() => new BoxEl
    {
        Width = WaveeCta.IconButtonSize, Height = WaveeCta.IconButtonSize, Shrink = 0f,
        Corners = Radii.ControlAll,
    };

    // The list command bar under the hero: the SAME padded surface box DetailTracks.CommandBarSurface reserves
    // (DetailVerticalLayout.ToolbarRowHeight, 44 — the box the band's arithmetic charges), with a group of pills at
    // control height (ToolbarPillHeight, 32 — what CommandBarSurface's own pills draw at) inset by its
    // ToolbarSurfacePadX/Y. Both sides read the same four constants, so the reserved band and the drawn shimmer can
    // never disagree about the toolbar's height again (issue #78/#79/#80 skeleton parity pass).
    static Element ToolbarRow() => new BoxEl
    {
        Direction = 1,
        Height = DetailVerticalLayout.ToolbarRowHeight,
        Padding = new Edges4(DetailVerticalLayout.ToolbarSurfacePadX, DetailVerticalLayout.ToolbarSurfacePadY,
            DetailVerticalLayout.ToolbarSurfacePadX, DetailVerticalLayout.ToolbarSurfacePadY),
        Children =
        [
            new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Grow = 1f, MinWidth = 0f,
                Children =
                [
                    Pill(72f), Pill(88f), Pill(64f),
                    new BoxEl { Grow = 1f, Height = 1f },
                    Pill(DetailTrackCommandBarLayout.SearchPreferred),
                ],
            },
        ],
    };

    static Element Pill(float w) => new BoxEl
    {
        Width = w, Height = DetailVerticalLayout.ToolbarPillHeight, Shrink = 0f,
        Corners = Radii.ControlAll,
    };

    static Element Bar(float w, float h) => new BoxEl
    {
        Width = w, Height = h, Corners = CornerRadius4.All(4f),
    };

    static float Fraction(float measure, float f) => MathF.Max(32f, MathF.Round(measure * f));
}
