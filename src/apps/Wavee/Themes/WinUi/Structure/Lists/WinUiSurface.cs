// ── Themes/WinUi/Structure/Lists/WinUiSurface.cs — WinUI's media surface (themes plan 6.2; P6 W3) ────────────────────────────
//
// From `Platform/Surface.Parts.cs` (the picture of `Controls.Surface`, the ONE card, tile and row of the app): the shell (a plate that
// is a non-hit-testable REVEAL carrying its kind's hover and press fills, the stroke, the role, the tab stop and the cursor the
// ownership rule decides), the selected skin, the STACK body (the cover over the label block: a shelf's inner-width square or the
// grid's fluid `ArtworkFill`) and the ROW body (art, text column, trailing cluster and the "..." as an overlay or a reserved lane),
// the label block with its trim tooltip, the inline caption and the plate table (`SurfacePlate`, "THE fill table": each `PlateKind`'s
// fills and stroke). `StyleKeys.Surface` carries the templates; the core host (`Controls.SurfaceHost`, Platform\Surface.Host.cs) keeps
// everything that is not a picture: the hot fold (hover, focus-within, the slot's focus bit), what is mounted when (the lazy chrome
// and the now-playing overlay), the ownership of click, focus and the roving tab stop, drag, drop, the menu, the title tooltip and
// every handler as a trampoline into the newest props.
//
// WHAT STAYED CORE. The seed face, the shelf gutter, the drag-arm barrier, a card's inner width and the row title's rung
// (`SurfaceParts`): host-free callers (a skeleton source, a page's placeholder, `Controls.Surface`'s `SkeletonOverride`) build them
// with no registry, and the seed IS the live surface's measured extent in bones, which the geometry constants
// (`SurfaceGeometry`) pin for both. Every number a template reads that the extents also read is `SurfaceGeometry`'s.
//
// Numbers are `Rules\WinUiSurfaceLayout.cs` (WVTH003 allows no literal look here).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>THE plate table: each <see cref="PlateKind"/>'s fills (theme-live <c>Tok.*</c> reads, like
/// <c>Interaction.*</c>) and its root stroke. Its structure is <see cref="PlateRules.Of"/>'s — which parts a kind has is
/// decided there and pinned by tests; the colours are decided here.
/// <list type="table">
/// <item>CardPlate — a subtle reveal (<c>FillSubtleSecondary</c> → <c>FillSubtleTertiary</c> pressed), no stroke: the
/// card plate verbatim, the recorder's own hover lightening included.</item>
/// <item>ListRow — the same subtle ramp, its hover leg pinned to <c>FillSubtleSecondary</c>: the list-row ramp a row has
/// always had (<c>Interaction.ListRow</c>'s legs).</item>
/// <item>Tile — the opaque card fill at REST (<c>RestOpacity</c> 1), replaced by <c>FillControlSecondary</c> on hover and
/// <c>FillControlTertiary</c> pressed (<c>Interaction.Tile</c>'s legs — a replacing ramp, so the rest fill lives on the
/// plate, not the root), under a 1-DIP card hairline that the shell binds to accent@0.5 while the surface relates to
/// playback.</item>
/// <item>TileStill — the Tile's fills and hairline, but the hairline never follows playback.</item>
/// <item>Outline — the ListRow ramp under a 1-DIP dashed (3/3) <c>StrokeControlSecondary</c> frame.</item>
/// </list></summary>
public readonly record struct SurfacePlate(ColorF Fill, ColorF HoverFill, ColorF PressedFill, float RestOpacity,
                                           float StrokeWidth, ColorF Stroke, float Dash, bool FollowsPlayback)
{
    public static SurfacePlate For(PlateKind kind)
    {
        var r = PlateRules.Of(kind);
        // HasRootFill is the one opaque kind (the tile): its ramp REPLACES the rest fill, so all three legs ride the plate.
        return new SurfacePlate(
            Fill: r.HasRootFill ? Tok.FillCardDefault : Tok.FillSubtleSecondary,
            HoverFill: r.HasRootFill ? Tok.FillControlSecondary
                : kind == PlateKind.CardPlate ? ColorF.Transparent : Tok.FillSubtleSecondary,
            PressedFill: r.HasRootFill ? Tok.FillControlTertiary : Tok.FillSubtleTertiary,
            RestOpacity: r.HasRootFill ? 1f : 0f,
            StrokeWidth: r.HasStroke ? 1f : 0f,
            Stroke: !r.HasStroke ? ColorF.Transparent : r.Dashed ? Tok.StrokeControlSecondary : Tok.StrokeCardDefault,
            Dash: r.Dashed ? 3f : 0f,
            FollowsPlayback: r.StrokeFollowsPlayback);
    }
}

/// <summary>WinUI's style for the media surface.</summary>
internal static class WinUiSurface
{
    /// <summary>The style WinUI registers under <c>StyleKeys.Surface</c>. Stateless: every host and a test share it.</summary>
    public static readonly SurfaceStyle Style = new(Shell, Selected, StackBody, RowBody, Labels, RowText);

    /// <summary>The ONE shell: a ZStack of [plate, <paramref name="content"/>]. Corners and focus margin come from the
    /// shape (the card radius + <c>FocusInsetBordered</c> on a stack, the control radius + <c>FocusInsetRow</c> on a
    /// row), the plate and stroke from <see cref="SurfacePlate.For"/>, the role, the tab stop and the cursor from the
    /// ownership rule (<see cref="SurfaceRules"/>) — never from a caller. The host adds the handlers with <c>with</c>.
    /// <para>The plate is a non-hit-testable REVEAL (<c>HoverOpacity</c>): it follows its container's hover AND press
    /// across the interaction boundary, and its explicit <c>PressedFill</c> opts it into the recorder's press cross-fade
    /// (an inherited press never darkens a fill on its own). 83 ms both ways — the ControlFaster rung.</para></summary>
    static BoxEl Shell(Element content, in SurfaceShape s, in SurfaceOwnership mode, string uri)
    {
        var plate = SurfacePlate.For(s.Plate);
        float r = s.IsRow ? Radii.Control : Radii.Card;
        // The tile's hairline reads the playback relation in its OWN bind: a track edge re-tints this one node (paint
        // only) and re-renders nothing.
        Prop<ColorF> stroke = plate.FollowsPlayback
            ? Prop.Of(() => Controls.RelatesNow(uri) ? Tok.AccentDefault with { A = 0.5f } : Tok.StrokeCardDefault)
            : plate.Stroke;
        return new BoxEl
        {
            ZStack = true, Grow = s.IsRow ? 0f : 1f, MinWidth = 0f,
            // A label-less tile is its own square (the rail); every other shell takes its cell's width.
            Width = s.Labels ? float.NaN : s.MinHeight,
            Corners = CornerRadius4.All(r),
            BorderWidth = plate.StrokeWidth, BorderColor = stroke,
            BorderDashOn = plate.Dash, BorderDashOff = plate.Dash,
            Role = mode.Role, Focusable = mode.OwnsFocus,
            FocusVisualMargin = s.IsRow ? Design.FocusInsetRow : Design.FocusInsetBordered,
            Cursor = SurfaceRules.Cursor(in mode),
            Children =
            [
                new BoxEl
                {
                    Grow = 1f, HitTestVisible = false, Corners = CornerRadius4.All(r),
                    Opacity = plate.RestOpacity, HoverOpacity = 1f,
                    HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
                    PressDurationMs = MotionTok.ControlFaster.DurationMs, PressEasing = MotionTok.ControlFaster.Easing,
                    Fill = plate.Fill, HoverFill = plate.HoverFill, PressedFill = plate.PressedFill,
                },
                content,
            ],
        };
    }

    /// <summary>The selected skin: the 2-DIP accent border (<paramref name="accent"/>, read now — the host calls this in
    /// its render, so a palette landing repaints the one selected surface; null = the theme accent) over the brighter
    /// card fill, on the shell ROOT (Controls.Art.cs header, rule 1). A STATIC border: the tile kind's hairline is a bind,
    /// and no shape selects a tile (a static↔bound flip on one channel of a reused node is what <c>BindContract</c>
    /// reports).</summary>
    static BoxEl Selected(BoxEl shell, Func<ColorF>? accent)
        => shell with { BorderColor = (accent ?? s_themeAccent)(), BorderWidth = WinUiSurfaceLayout.SelectedBorder, Fill = Tok.FillCardDefault };

    static readonly Func<ColorF> s_themeAccent = static () => Tok.AccentDefault;

    /// <summary>The STACK body: the cover stack (art · [overlay] · [corner "…"]) over the label block.
    /// <list type="bullet">
    /// <item>SHELF (<paramref name="width"/> known): the cover is the inner width square (or <c>CoverAspect</c> wide, from
    /// <see cref="SurfaceGeometry.CoverHeight"/>, at the 512 wide decode), 8 padding all round and 8 cover → labels —
    /// exactly the constants <see cref="SurfaceGeometry.ShelfHeight(float, float, int, bool)"/> sums.</item>
    /// <item>GRID (<paramref name="width"/> NaN): a fluid <c>ArtworkFill</c> cover — a square card leaves the box's aspect
    /// OFF (the fill image carries its own 1:1), a wide one sizes the whole cover stack, overlay and corner included, to
    /// its ratio — over 8/8/8/12 padding (<see cref="SurfaceGeometry.GridCardChromeFor"/>).</item>
    /// </list>
    /// <paramref name="overlay"/> mounts the now-playing overlay (hot, relating, or a video at rest); the corner "…" mounts
    /// with <paramref name="chrome"/> when the shape and the data both have one (<see cref="SurfaceRules.ShowsMenuCorner"/>).
    /// <paramref name="labels"/> is the host's cached label block (built here when null).</summary>
    static Element StackBody(Controls.CardData d, in SurfaceShape s, float width, bool overlay, bool chrome, bool hasMenu,
                                    Action? onPlay, Element? labels = null)
    {
        // A circle only on a square cover (`CoverShape`): the radius, the clip and the labels all read this one bit.
        bool circular = Controls.CoverShape.IsCircular(d.Circular, d.CoverAspect);
        Element? fab = overlay
            ? Controls.NowPlayingOverlay(d.Uri, onPlay, s.Fab, centred: true, atRest: s.Play == PlayReveal.Always)
            : null;
        Element? corner = chrome && SurfaceRules.ShowsMenuCorner(in s, hasMenu, d.ShowMenu) ? Controls.MoreCorner() : null;

        if (float.IsNaN(width))
        {
            Element fill = d.CoverOverride
                ?? Controls.ArtworkFill(d.CoverUrl, circular ? Radii.Full : Radii.Card, aspect: d.CoverAspect);
            // The overlay is mounted whenever the corner is (chrome ⊂ overlay), so three arms cover every case.
            Element[] cover = fab is null ? [fill] : corner is null ? [fill, fab] : [fill, fab, corner];
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.S, Grow = 1f,
                Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.M),
                Children =
                [
                    new BoxEl
                    {
                        ZStack = true, ClipToBounds = !circular, Children = cover,
                        AspectRatio = d.CoverAspect == 1f ? float.NaN : d.CoverAspect,
                    },
                    labels ?? Labels(d, in s, float.NaN),
                ],
            };
        }

        float inner = SurfaceParts.InnerOf(width);
        // A square card keeps its 256 decode and its edge; a wide tile takes the derived height and the 512 decode.
        bool square = d.CoverAspect == 1f;
        float coverH = SurfaceGeometry.CoverHeight(inner, d.CoverAspect);
        Element art = d.CoverOverride
            ?? Controls.Artwork(d.CoverUrl, inner, coverH, circular ? inner / 2f : Radii.Card,
                                decodePx: square ? Controls.ShelfDecodePx : Controls.WideDecodePx);
        return new BoxEl
        {
            Direction = 1, Gap = SurfaceGeometry.ShelfPlatePad, Grow = 1f,
            Padding = Edges4.All(SurfaceGeometry.ShelfPlatePad),
            Children = [Controls.CardCover(art, inner, coverH, circular, fab, corner), labels ?? Labels(d, in s, inner)],
        };
    }

    /// <summary>The ROW body: the art square (<see cref="SurfaceShape.ArtEdge"/>, the overlay's FAB at
    /// <see cref="SurfaceShape.Fab"/>) · the text column (<see cref="RowText"/>) · the trailing cluster (the data's
    /// <c>Trailing</c>; the "…" is a hover overlay over the row's end that reserves no width, or — on a
    /// <see cref="MenuPlacement.TrailingLane"/> shape — its own reserved lane after the cluster,
    /// <see cref="SurfaceRules.MenuReservesWidth"/>) — 12 apart, centred, 8 padding, over the shape's floor. A label-less
    /// shape (the rail tile) is the art alone, centred in its floor square.
    /// <paramref name="text"/> is the host's cached text column (built here when null).</summary>
    static Element RowBody(Controls.CardData d, in SurfaceShape s, bool overlay, bool chrome, bool hasMenu, Action? onPlay,
                                  Element? text = null)
    {
        float edge = s.ArtEdge;
        Element art = d.CoverOverride ?? Controls.Artwork(d.CoverUrl, edge, edge, d.Circular ? edge / 2f : Radii.Control);
        var artBox = new BoxEl
        {
            ZStack = true, Width = edge, Height = edge, Shrink = 0f, ClipToBounds = !d.Circular,
            Children = overlay
                ? [art, Controls.NowPlayingOverlay(d.Uri, onPlay, s.Fab, centred: true, atRest: s.Play == PlayReveal.Always)]
                : [art],
        };
        if (!s.Labels)
            return new BoxEl
            {
                Grow = 1f, Width = s.MinHeight, Height = s.MinHeight,
                Padding = Edges4.All(MathF.Max(0f, (s.MinHeight - edge) / 2f)),
                Children = [artBox],
            };

        Element? trailing = d.Trailing is null ? null : SurfaceParts.Action(d.Trailing);
        // A LANE shape's "…" is the cluster's last cell, laid out at rest whether or not the row is hot (stable per row),
        // so it sits BESIDE the Follow pill / heart instead of over it and the hot edge reflows nothing.
        bool lane = SurfaceRules.MenuReservesWidth(in s, hasMenu, d.ShowMenu);
        if (lane)
            trailing = trailing is null ? MenuLane(chrome) : new BoxEl
            {
                Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, Shrink = 0f,
                Children = [trailing, MenuLane(chrome)],
            };
        Element column = text ?? RowText(d, in s)!;
        var flow = new BoxEl
        {
            Direction = 0, Gap = SurfaceGeometry.RowGap, AlignItems = FlexAlign.Center, Grow = 1f,
            MinHeight = s.MinHeight, MinWidth = 0f,
            Padding = Edges4.All(SurfaceGeometry.RowPad),
            Children = trailing is null ? [artBox, column] : [artBox, column, trailing],
        };
        // Otherwise the "…" is an OVERLAY, not a lane: it takes no width at rest or on hover, so the title never
        // re-ellipsises under the pointer and a narrow row keeps its text. It is mounted lazily with the chrome. The ZStack
        // wrapper exists whenever the row CAN show the "…" (stable per row), so hot only adds the overlay child.
        if (lane || !SurfaceRules.ShowsMenuTrailing(in s, hasMenu, d.ShowMenu)) return flow;
        return new BoxEl
        {
            ZStack = true, Grow = 1f, MinWidth = 0f, MinHeight = s.MinHeight,
            Children = chrome ? [flow, MenuOverlay()] : [flow],
        };
    }

    // The trailing "…" overlay: pinned to the row's end (inside the row padding), centred, over an OPAQUE chip that is
    // itself a hover reveal — so on hover it covers the end of the trailing lane (or the text) instead of competing with it.
    // The button is the standard "…" at rest opacity 0 and carries no handler: it re-enters the context funnel and opens
    // the menu the host attached to the shell. The container passes hits through; only the chip takes them.
    static Element MenuOverlay() => new BoxEl
    {
        HitTestPassThrough = true, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
        Padding = Edges4.All(SurfaceGeometry.RowPad),
        Children =
        [
            SurfaceParts.Action(new BoxEl
            {
                Width = Controls.IconButtonSize, Height = Controls.IconButtonSize, Shrink = 0f,
                Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillControlSolid,
                Opacity = 0f, HoverOpacity = 1f,
                HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
                Children = [ToolTip.WrapStable(s_trailingMore, Loc.Get(Strings.Common.More)).Skeletonized(false)],
            }),
        ],
    };

    // The reserved "…" lane (a TrailingLane shape): the same standard "…" and the same hover reveal as the overlay, but in
    // a 32 cell of its own after the trailing cluster — so no opaque chip (there is nothing under it to hide). The cell is
    // laid out at rest; the button mounts into it with the chrome. Action() makes it a drag-arm barrier like every other
    // trailing control.
    static Element MenuLane(bool chrome) => SurfaceParts.Action(new BoxEl
    {
        Width = Controls.IconButtonSize, Height = Controls.IconButtonSize, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Opacity = 0f, HoverOpacity = 1f,
        HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
        Children = chrome ? [ToolTip.WrapStable(s_trailingMore, Loc.Get(Strings.Common.More)).Skeletonized(false)] : [],
    });

    /// <summary>MOUNT-STABLE (one static delegate for the process), which <see cref="ToolTip.WrapStable"/> requires: the
    /// trailing "…" has no per-row data at all.</summary>
    static readonly Func<Element> s_trailingMore = static () => Controls.MoreButton(null, requestsContext: true, restOpacity: 0f);

    /// <summary>A stack's label block. The title is capped at the shape's <see cref="SurfaceShape.TitleLines"/> (wrapping
    /// when it allows more than one) and sits inside the trim tooltip; the search-highlight arm gets the SAME budget as
    /// the plain title, so a filter narrowing a grid does not reflow a card from two lines to one. A <c>Meta</c> adds ONE
    /// tertiary caption line under the subtitle (2 gap + 16 — the <c>metaLine</c> the shelf extent budgets); an inline
    /// <c>Caption</c> instead carries the meta as a tertiary tail on its own <c>CaptionLines</c>, so no separate line
    /// exists. A circular card centres the block under its circle. <paramref name="inner"/> is the shelf's inner width
    /// (NaN on a fluid grid card).</summary>
    static Element Labels(Controls.CardData d, in SurfaceShape s, float inner)
    {
        int lines = Math.Max(1, s.TitleLines);
        bool circular = Controls.CoverShape.IsCircular(d.Circular, d.CoverAspect);
        // A circular card's title is a CONTENT-SIZED run capped at the slot (MaxWidth), so the block's centring can place
        // it under the circle — a fixed Width made it a full-width, left-set line over a centred subtitle.
        TextEl title = R.Type.CardTitle(d.Title) with
        {
            Width = circular ? float.NaN : inner, MaxWidth = circular ? inner : float.NaN,
            MaxLines = lines, Wrap = lines > 1 ? TextWrap.Wrap : TextWrap.NoWrap,
            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        var kids = new List<Element>(3) { Title(d, title, lines, centred: circular) };
        if (d.Caption is not null)
        {
            if (InlineCaption(d.Caption, d.Meta, d.CaptionLines, inner) is { } caption) kids.Add(caption);
        }
        else
        {
            if (d.Subtitle is not null) kids.Add(d.Subtitle);
            if (d.Meta is { Length: > 0 } meta)
                kids.Add(R.Type.TrackMeta(meta) with
                    { Color = Tok.TextTertiary, Width = inner, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
        }
        return new BoxEl
        {
            Direction = 1, Gap = SurfaceGeometry.CardLabelGap, MinWidth = 0f,
            AlignItems = circular ? FlexAlign.Center : FlexAlign.Start,
            Children = kids.ToArray(),
        };
    }

    /// <summary>A row's text column: [eyebrow] · the title (trim tooltip; the page-hero rung on a large row, the track
    /// rung otherwise, capped at <see cref="SurfaceShape.TitleLines"/>) · [subtitle] · [meta row] · [below], growing into
    /// the row from a zero basis. Null for a label-less shape.</summary>
    static Element? RowText(Controls.CardData d, in SurfaceShape s)
    {
        if (!s.Labels) return null;
        bool large = s.IsLargeRow;
        int lines = Math.Max(1, s.TitleLines);
        TextEl title = SurfaceParts.RowTitleStyle(in s, d.Title) with
        {
            MaxLines = lines, Wrap = lines > 1 ? TextWrap.Wrap : TextWrap.NoWrap,
            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        var kids = new List<Element>(5);
        if (d.Eyebrow is not null) kids.Add(d.Eyebrow);
        kids.Add(Title(d, title, lines, centred: false));
        if (d.Subtitle is not null) kids.Add(d.Subtitle);
        if (d.MetaRow is not null) kids.Add(d.MetaRow);
        if (d.Below is not null) kids.Add(d.Below);
        return new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = large ? Spacing.S : SurfaceGeometry.CardLabelGap,
            Children = kids.ToArray(),
        };
    }

    // The title inside the trim tooltip (`Controls.TrimmedTitle`, the one wrapper): the plain run, or the search-highlight
    // arm at the SAME line budget (the tooltip measures with the plain run's style either way). On a circular card the run
    // is centred under the circle as a content-sized run instead of ellipsising at the slot's width.
    static Element Title(Controls.CardData d, TextEl style, int lines, bool centred)
    {
        var (start, length) = d.Highlight;
        Element? run = length > 0
            ? Controls.SearchHighlight(d.Title, start, length, style.Size, style.ResolvedWeight, Tok.TextPrimary, lines)
            : null;
        return Controls.TrimmedTitle(style, d.Title, run, centred);
    }

    // The inline caption: the second line and the meta as ONE secondary 12/16 paragraph (the meta a tertiary " · meta"
    // tail), capped at `lines` — exactly `captionLines` caption lines of the shelf extent, never a separate meta line.
    // Null when there is nothing to say.
    static Element? InlineCaption(string caption, string? meta, int lines, float inner)
    {
        bool hasMeta = meta is { Length: > 0 };
        if (caption.Length == 0 && !hasMeta) return null;
        if (lines < 1) lines = 1;
        TextSpan[] spans = caption.Length == 0 ? [new TextSpan(meta!, Color: Tok.TextTertiary)]
            : hasMeta ? [new TextSpan(caption), new TextSpan(" · " + meta, Color: Tok.TextTertiary)]
            : [new TextSpan(caption)];
        var style = R.Type.TrackMeta("");
        return new SpanTextEl(spans)
        {
            Size = style.Size, LineHeight = style.LineHeight, Color = Tok.TextSecondary,
            Wrap = lines > 1 ? TextWrap.Wrap : TextWrap.NoWrap, MaxLines = lines, Trim = TextTrim.CharacterEllipsis,
            Width = inner, MinWidth = 0f,
        };
    }
}
