// ── Platform/Surface.Parts.cs ──────────────────────────────────────────────────────────────────────────────────────
// The media surface's host-free GEOMETRY: the shelf gutter, the drag-arm barrier, a card's inner width, the row title's rung and
// the SEED face. The picture of a live surface (the shell and its plate, the stack and row bodies, the label block with its trim
// tooltip, the selected skin and the one fill table) is the structure's since T8c: `StyleKeys.Surface` / `SurfaceStyle`, Classic's
// `Themes\Classic\Structure\Lists\ClassicSurface.cs`. The seed stays here because host-free callers (a skeleton source, a page's
// placeholder, `Controls.Surface`'s `SkeletonOverride`) build it with no registry, and because it IS the live surface's measured
// extent in bones (`SurfaceGeometry`): the live body and the seed must agree to the DIP.
//
// Role: UI
// Owner: L
// Wave: 0 (shared media surface)
// Budget: 450 lines
// Spec: docs/plans/wavee/shared-media-surface-implementation.md §2.3–§2.5
//
// Static element builders with no state: the ONE host (Surface.Host.cs) composes them with the structure's templates, and a
// hook-free slot class (the sidebar's) or a (B) surface may call them directly. Every geometry constant is `SurfaceGeometry`'s
// (Surface.Rules.cs), so the estimators and the renderer agree to the DIP.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>The media surface's host-free element builders (plan §2.3): the gutter, the drag barrier and the seed face. Nothing here
/// owns state or reads a signal outside a bind thunk.</summary>
public static class SurfaceParts
{
    // ══ 1. THE GUTTER AND THE DRAG BARRIER ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The shelf's column around a shell: the card's fixed width, the 4-DIP gutter over the plate and none under
    /// it — exactly <see cref="SurfaceGeometry.ShelfGutterTop"/>/<see cref="SurfaceGeometry.ShelfGutterBottom"/>, which
    /// <see cref="SurfaceGeometry.ShelfHeight(float, float, int, bool)"/> sums. Nothing reserves a halo: the fill-only hover
    /// neither lifts nor casts one.</summary>
    public static BoxEl Gutter(Element shell, float width) => new()
    {
        Padding = new Edges4(0f, SurfaceGeometry.ShelfGutterTop, 0f, SurfaceGeometry.ShelfGutterBottom),
        Width = width, Shrink = 0f,
        Children = [shell],
    };

    /// <summary>A drag-arm BARRIER around <paramref name="el"/>: <c>DragController.TryArm</c>'s upward walk stops here, so a
    /// press on a control inside a row's trailing cluster (a Follow toggle, a heart, the "…") never arms the row's drag.
    /// The control itself still owns its click by the dispatcher's nearest-owner rule.</summary>
    public static BoxEl Action(Element el) => new()
    {
        BlocksDragArm = true, Shrink = 0f, AlignItems = FlexAlign.Center,
        Children = [el],
    };

    // ══ 2. A CARD'S INNER WIDTH ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A shelf card's inner width (the card less its 2 × 8 plate padding); NaN for a fluid grid card.</summary>
    public static float InnerOf(float width) => InnerOf(width, flush: false);

    /// <summary>A shelf card's inner width for a cover that is <paramref name="flush"/> with its cell (<see cref="SurfaceShape.Flush"/>: the whole width).</summary>
    public static float InnerOf(float width, bool flush)
        => float.IsNaN(width) ? float.NaN : width - 2f * SurfaceGeometry.PlateInsetX(flush);

    // ══ 3. THE ROW TITLE'S RUNG ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The row title's rung (the page-hero rung on a large row, the track rung otherwise): the seed's line box and the structure's
    /// live run read the same one, so a hydrating row never changes height.</summary>
    internal static TextEl RowTitleStyle(in SurfaceShape s, string text)
        => s.IsLargeRow ? R.Type.PageHero(text) : R.Type.TrackTitle(text);

    // ══ 4. THE SEED FACE ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The SEED face (<c>CardData.IsSeed</c>): the shape's own layout with bone leaves — the cover a
    /// <c>FillSubtleSecondary</c> block of the cover's geometry, each title line a 13-high bar (capped at 150) in the
    /// title's 20 line box, each caption line an 11 × 92 bar in its 16 box — disabled, no plate, no chrome, no handlers.
    /// Because every line keeps its line box, a seed is exactly as tall as the live surface it stands for
    /// (<see cref="SurfaceGeometry.ShelfHeight(float, float, int, bool)"/> / <see cref="SurfaceGeometry.GridCardChromeFor"/>),
    /// so a hydrating card never re-measures its row. <paramref name="height"/> pins it like <c>CardData.Height</c>;
    /// <paramref name="circular"/> (the data's <c>Circular</c>: an artist, a person) rounds the cover bone to a circle, so a
    /// round card hydrates from a round seed.</summary>
    public static Element Seed(in SurfaceShape s, float width, float height = float.NaN, bool circular = false)
    {
        BoxEl face = s.IsRow ? RowSeed(in s, circular) : StackSeed(in s, width, circular);
        if (!float.IsNaN(height)) face = face with { Height = height, Grow = 0f, Shrink = 0f };
        return s.IsRow || float.IsNaN(width) ? face : Gutter(face, width);
    }

    static BoxEl StackSeed(in SurfaceShape s, float width, bool circular)
    {
        bool shelf = !float.IsNaN(width);
        float inner = InnerOf(width, s.Flush);
        float padX = SurfaceGeometry.PlateInsetX(s.Flush);
        var corners = CornerRadius4.All(circular ? Radii.Full : Radii.Card);
        Element cover = shelf
            ? new BoxEl
            {
                Width = inner, Height = SurfaceGeometry.CoverHeight(inner, 1f),
                Corners = corners, Fill = Tok.FillSubtleSecondary,
            }
            : new BoxEl
            {
                AspectRatio = 1f, AlignSelf = FlexAlign.Stretch,
                Corners = corners, Fill = Tok.FillSubtleSecondary,
            };
        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinWidth = 0f, IsEnabled = false,
            Gap = shelf ? SurfaceGeometry.ShelfPlatePad : Spacing.S,
            Padding = shelf
                ? new Edges4(padX, SurfaceGeometry.ShelfPlatePad, padX, SurfaceGeometry.ShelfPlatePad)
                : new Edges4(s.Flush ? 0f : Spacing.S, Spacing.S, s.Flush ? 0f : Spacing.S, Spacing.M),
            Children = [cover, SeedLines(in s, SurfaceGeometry.CardTitleLineH, SurfaceGeometry.CardLabelGap)],
        };
    }

    static BoxEl RowSeed(in SurfaceShape s, bool circular)
    {
        float edge = s.ArtEdge;
        var bone = new BoxEl
        {
            Width = edge, Height = edge, Shrink = 0f, Corners = CornerRadius4.All(circular ? Radii.Full : Radii.Control),
            Fill = Tok.FillSubtleSecondary,
        };
        if (!s.Labels)
            return new BoxEl
            {
                Grow = 1f, Width = s.MinHeight, Height = s.MinHeight, IsEnabled = false,
                Padding = Edges4.All(MathF.Max(0f, (s.MinHeight - edge) / 2f)),
                Children = [bone],
            };
        float titleH = RowTitleStyle(in s, "").LineHeight;
        float gap = s.IsLargeRow ? Spacing.S : SurfaceGeometry.CardLabelGap;
        return new BoxEl
        {
            Direction = 0, Gap = SurfaceGeometry.RowGap, AlignItems = FlexAlign.Center, Grow = 1f, IsEnabled = false,
            MinHeight = s.MinHeight, MinWidth = 0f, Padding = Edges4.All(SurfaceGeometry.RowPad),
            Children = [bone, SeedLines(in s, float.IsNaN(titleH) ? SurfaceGeometry.CardTitleLineH : titleH, gap) with
                { Grow = 1f, Basis = 0f }],
        };
    }

    // The label bones: one line box per title line, per caption line and for the meta line, each holding its bar.
    static BoxEl SeedLines(in SurfaceShape s, float titleH, float gap)
    {
        int titles = Math.Max(1, s.TitleLines), captions = Math.Max(0, s.CaptionLines);
        var kids = new Element[titles + captions + (s.MetaLine ? 1 : 0)];
        int k = 0;
        for (int i = 0; i < titles; i++) kids[k++] = SeedLine(titleH, 13f, float.NaN);
        for (int i = 0; i < captions; i++) kids[k++] = SeedLine(SurfaceGeometry.CardCaptionLineH, 11f, 92f);
        if (s.MetaLine) kids[k] = SeedLine(SurfaceGeometry.CardCaptionLineH, 11f, 92f);
        return new BoxEl { Direction = 1, Gap = gap, MinWidth = 0f, Children = kids };
    }

    // A line box of the live run's height with a bone bar centred in it: a stretched (capped 150) bar for a title, a fixed
    // one for a caption.
    static Element SeedLine(float lineH, float barH, float barW) => new BoxEl
    {
        Direction = 1, Height = lineH, Justify = FlexJustify.Center, MinWidth = 0f,
        Children =
        [
            new BoxEl
            {
                Height = barH, Width = barW, AlignSelf = float.IsNaN(barW) ? FlexAlign.Stretch : FlexAlign.Start,
                MaxWidth = float.IsNaN(barW) ? 150f : float.NaN,
                Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary,
            },
        ],
    };
}
