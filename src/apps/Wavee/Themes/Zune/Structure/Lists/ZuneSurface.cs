// ── Themes/Zune/Structure/Lists/ZuneSurface.cs — Zune's media surface (themes plan 6.3; P5 Z3) ─────────────────────────────────────
//
// `Controls.Surface` (the one card, tile and row of the app) as Zune draws it (Z-Home quickplay, Z-Library-Albums, Z-Search, Z-Browse):
// TILES ARE SQUARE (radius 0: `R.Shape.Tile`, the plate and the shell), covers carry the Cover radius (2), people stay round. The plate is a
// flat wash, never a stroke or a lift: a card plate and a list row answer hover with the 5 % ink wash and press with the pressed wash; a
// tile rests on the panel fill and washes on hover; the tile that relates to playback keeps no hairline (Zune says it with the accent
// elsewhere). The selected skin is the 2-DIP accent border over the 12 % accent wash. Titles in the CardTitle role (15/20), the meta in
// the TrackMeta role (13/18).
//
// WHAT STAYED CORE. The hot fold, what mounts when (the lazy chrome, the now-playing overlay), the ownership of click / focus / tab stop,
// drag, drop, the menu, the title tooltip, the seed face and the geometry constants (`SurfaceGeometry`, `SurfaceParts`): the seed IS the
// live surface's measured extent, so every number the extents also read is the core's, unchanged.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

namespace Wavee.Themes.Zune;

/// <summary>Zune's style for the media surface.</summary>
internal static class ZuneSurface
{
    /// <summary>The style Zune registers under <c>StyleKeys.Surface</c>. Stateless.</summary>
    public static readonly SurfaceStyle Style = new(Shell, Selected, StackBody, RowBody, Labels, RowText);

    /// <summary>The plate of a kind: (rest fill, hover fill, pressed fill, rest opacity, dash). Zune's plates are washes, never strokes, except
    /// the Outline kind's dashed frame (an "add" tile).</summary>
    static (ColorF Fill, ColorF Hover, ColorF Pressed, float RestOpacity, bool Dashed) PlateOf(PlateKind kind)
    {
        var r = PlateRules.Of(kind);
        return r.HasRootFill
            ? (R.Layer.FloatingPane, ColorContrast.Over(R.Layer.RowHover, R.Layer.FloatingPane), ColorContrast.Over(R.Layer.RowPressed, R.Layer.FloatingPane), 1f, false)
            : (R.Layer.RowHover, R.Layer.RowHover, R.Layer.RowPressed, 0f, r.Dashed);
    }

    /// <summary>The shell: a ZStack of [plate, content], square, the role / tab stop / cursor the ownership rule decides; the host adds the
    /// handlers. The plate is a non-hit-testable reveal that follows the shell's hover and press.</summary>
    static BoxEl Shell(Element content, in SurfaceShape s, in SurfaceOwnership mode, string uri)
    {
        var plate = PlateOf(s.Plate);
        var corners = R.Shape.All(ShapeRole.Tile);
        // A shelf card's plate is a wash over its ART (the stack body draws it, A5): the shell keeps no whole-card plate unless the kind rests on a fill.
        bool artWash = IsShelfBody(content) && !PlateRules.Of(s.Plate).HasRootFill;
        Element[] kids = artWash ? [content] : [PlateBox(in plate, corners), content];
        return new BoxEl
        {
            ZStack = true, Grow = s.IsRow ? 0f : 1f, MinWidth = 0f,
            Width = s.Labels ? float.NaN : s.MinHeight,
            Corners = corners,
            BorderWidth = plate.Dashed ? 1f : 0f, BorderColor = plate.Dashed ? Tok.StrokeControlSecondary : ColorF.Transparent,
            BorderDashOn = plate.Dashed ? ZuneListRules.SurfaceDash : 0f, BorderDashOff = plate.Dashed ? ZuneListRules.SurfaceDash : 0f,
            Role = mode.Role, Focusable = mode.OwnsFocus,
            FocusVisualMargin = s.IsRow ? Design.FocusInsetRow : Design.FocusInsetBordered,
            Cursor = SurfaceRules.Cursor(in mode),
            Children = kids,
        };
    }

    /// <summary>The plate reveal: a non-hit-testable box whose opacity and fill follow the shell's hover and press.</summary>
    static BoxEl PlateBox(in (ColorF Fill, ColorF Hover, ColorF Pressed, float RestOpacity, bool Dashed) plate, CornerRadius4 corners) => new()
    {
        Grow = 1f, HitTestVisible = false, Corners = corners,
        Opacity = plate.RestOpacity, HoverOpacity = 1f,
        HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
        PressDurationMs = MotionTok.ControlFaster.DurationMs, PressEasing = MotionTok.ControlFaster.Easing,
        Fill = plate.Fill, HoverFill = plate.Hover, PressedFill = plate.Pressed,
    };

    // The host wraps the body in one non-keyed box (`within`); a shelf stack body is that box's only child and carries ShelfBodyKey.
    static bool IsShelfBody(Element content)
        => content.Is(out BoxEl box) && box.Children is { Length: 1 } kids && kids[0].Is(out BoxEl body) && body.Key == ZuneListRules.ShelfBodyKey;

    /// <summary>The selected skin: the 2-DIP accent border (<paramref name="accent"/> read now; null is the style's accent) over the accent wash.
    /// A static border (no shape selects a tile, so no static ↔ bound flip on a reused node).</summary>
    static BoxEl Selected(BoxEl shell, Func<ColorF>? accent)
        => shell with
        {
            BorderColor = accent is null ? R.Ink.AccentSelection : accent(),
            BorderWidth = ZuneListRules.SurfaceSelectedBorder, Fill = R.Layer.SelectedRest,
        };

    /// <summary>The STACK body: the cover stack (art · [overlay] · [corner "..."]) over the label block, at the shelf's inner width or the grid's
    /// fluid fill (the same geometry `SurfaceGeometry` sums for the seed).</summary>
    static Element StackBody(Controls.CardData d, in SurfaceShape s, float width, IReadSignal<float>? liveWidth, bool overlay, bool chrome,
                             bool hasMenu, Action? onPlay, Element? labels = null)
    {
        bool circular = Controls.CoverShape.IsCircular(d.Circular, d.CoverAspect);
        Element? fab = overlay
            ? Controls.NowPlayingOverlay(d.Uri, onPlay, s.Fab, centred: true, atRest: s.Play == PlayReveal.Always)
            : null;
        Element? corner = chrome && SurfaceRules.ShowsMenuCorner(in s, hasMenu, d.ShowMenu) ? Controls.MoreCorner() : null;
        float coverRadius = circular ? Radii.Full : R.Shape.Cover;

        if (float.IsNaN(width))
        {
            Element fill = d.CoverOverride ?? Controls.ArtworkFill(d.CoverUrl, coverRadius, aspect: d.CoverAspect);
            Element[] cover = fab is null ? [fill] : corner is null ? [fill, fab.Value] : [fill, fab.Value, corner.Value];
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

        // A SHELF card (A5): no plate padding, so the art is the card's whole width and stands at the heading's x; the caption 6 under it, the
        // hover a wash over the art alone. The slack under it keeps the card exactly as tall as the core's estimate and seed face.
        // Every size is BOUND to the host's live card width (audit B3bc): a width change re-lays the card out and re-renders nothing. The art
        // and its plate fill the cover box (layers of no size), so a circle is the full radius; the label block is width-free (its column sizes it).
        var (coverW, coverH) = SurfaceParts.ShelfCover(width, liveWidth, 0f, d.CoverAspect);
        bool square = d.CoverAspect == 1f;
        float radius = circular ? Radii.Full : R.Shape.Cover;
        Element art = d.CoverOverride
            ?? Controls.Artwork(d.CoverUrl, float.NaN, float.NaN, radius,
                                decodePx: square ? Controls.ShelfDecodePx : Controls.WideDecodePx);
        if (!PlateRules.Of(s.Plate).HasRootFill)
        {
            var plate = PlateOf(s.Plate);
            art = new BoxEl
            {
                ZStack = true, Shrink = 0f,
                Children = [art, PlateBox(in plate, CornerRadius4.All(radius)) with { Grow = 0f }],
            };
        }
        float aspect = d.CoverAspect;
        // A square cover's slack is the same at every width (2): static, so a derived skeleton keeps it. Only a wide cover's rounding moves it.
        Prop<float> slack = liveWidth is null || aspect == 1f
            ? ZuneListRules.ShelfSlack(width, aspect)
            : Prop.Of(() => ZuneListRules.ShelfSlack(liveWidth.Value, aspect));
        return new BoxEl
        {
            Direction = 1, Grow = 1f, Key = ZuneListRules.ShelfBodyKey,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Gap = ZuneListRules.ShelfCaptionGap,
                    Children = [Controls.CardCover(art, coverW, coverH, aspect, circular, fab, corner), labels ?? Labels(d, in s, float.NaN)],
                },
                new BoxEl { Height = slack, Shrink = 0f, HitTestVisible = false },
            ],
        };
    }

    /// <summary>The ROW body: the art square · the text column · the trailing cluster, the "..." as a hover overlay over the row's end or, on a
    /// lane shape, its own reserved lane. A label-less shape (a rail tile) is the art alone, centred in its floor square.</summary>
    static Element RowBody(Controls.CardData d, in SurfaceShape s, bool overlay, bool chrome, bool hasMenu, Action? onPlay, Element? text = null)
    {
        float edge = s.ArtEdge;
        Element art = d.CoverOverride ?? Controls.Artwork(d.CoverUrl, edge, edge, d.Circular ? edge / 2f : R.Shape.Cover);
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

        Element? trailing = d.Trailing is null ? null : SurfaceParts.Action(d.Trailing.Value);
        bool lane = SurfaceRules.MenuReservesWidth(in s, hasMenu, d.ShowMenu);
        if (lane)
            trailing = trailing is null ? MenuLane(chrome) : new BoxEl
            {
                Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, Shrink = 0f,
                Children = [trailing.Value, MenuLane(chrome)],
            };
        Element column = (text ?? RowText(d, in s)).Value;
        var flow = new BoxEl
        {
            Direction = 0, Gap = s.ArtGap, AlignItems = FlexAlign.Center, Grow = 1f,
            MinHeight = s.MinHeight, MinWidth = 0f,
            // A floor under art + 2 x pad (Search's 52 / 56 rows of Z-Search-Small / -Compact) trims the pad to fit it; every other row keeps 8.
            Padding = new Edges4(s.PadX, RowPadY(edge, s.MinHeight), s.PadX, RowPadY(edge, s.MinHeight)),
            Children = trailing is null ? [artBox, column] : [artBox, column, trailing.Value],
        };
        if (lane || !SurfaceRules.ShowsMenuTrailing(in s, hasMenu, d.ShowMenu)) return flow;
        return new BoxEl
        {
            ZStack = true, Grow = 1f, MinWidth = 0f, MinHeight = s.MinHeight,
            Children = chrome ? [flow, MenuOverlay()] : [flow],
        };
    }

    static float RowPadY(float edge, float floor)
        => float.IsNaN(floor) ? SurfaceGeometry.RowPad : Math.Clamp((floor - edge) / 2f, 0f, SurfaceGeometry.RowPad);

    // The trailing "..." overlay over the row's end: an opaque square chip that is itself a hover reveal.
    static Element MenuOverlay() => new BoxEl
    {
        HitTestPassThrough = true, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
        Padding = Edges4.All(SurfaceGeometry.RowPad),
        Children =
        [
            SurfaceParts.Action(new BoxEl
            {
                Width = Controls.IconButtonSize, Height = Controls.IconButtonSize, Shrink = 0f,
                Fill = Prop.Of(static () => R.Layer.FloatingChrome),
                Opacity = 0f, HoverOpacity = 1f,
                HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
                Children = [ToolTip.WrapStable(s_trailingMore, Loc.Get(Strings.Common.More)).Skeletonized(false)],
            }),
        ],
    };

    // The reserved "..." lane: the same button and reveal in a cell of its own after the trailing cluster.
    static Element MenuLane(bool chrome) => SurfaceParts.Action(new BoxEl
    {
        Width = Controls.IconButtonSize, Height = Controls.IconButtonSize, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Opacity = 0f, HoverOpacity = 1f,
        HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
        Children = chrome ? [ToolTip.WrapStable(s_trailingMore, Loc.Get(Strings.Common.More)).Skeletonized(false)] : [],
    });

    /// <summary>MOUNT-STABLE (one static delegate), which <see cref="ToolTip.WrapStable"/> requires.</summary>
    static readonly Func<Element> s_trailingMore = static () => Controls.MoreButton(null, requestsContext: true, restOpacity: 0f);

    /// <summary>A stack's label block: the CardTitle role capped at the shape's lines (the search-highlight arm at the same budget), the
    /// subtitle, a tertiary meta line or the inline caption. A circular card centres the block under its circle.</summary>
    static Element Labels(Controls.CardData d, in SurfaceShape s, float inner)
    {
        int lines = Math.Max(1, s.TitleLines);
        bool circular = Controls.CoverShape.IsCircular(d.Circular, d.CoverAspect);
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
            if (d.Subtitle is not null) kids.Add(d.Subtitle.Value);
            if (d.Meta is { Length: > 0 } meta)
                kids.Add(R.Type.TrackMeta(meta) with { Color = Tok.TextTertiary, Width = inner, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
        }
        return new BoxEl
        {
            Direction = 1, Gap = SurfaceGeometry.CardLabelGap, MinWidth = 0f,
            AlignItems = circular ? FlexAlign.Center : FlexAlign.Start,
            Children = kids.ToArray(),
        };
    }

    /// <summary>A row's text column: [eyebrow] · the title (the core's row rung, capped at the shape's lines) · [subtitle] · [meta row] ·
    /// [below]. Null for a label-less shape.</summary>
    static Element? RowText(Controls.CardData d, in SurfaceShape s)
    {
        if (!s.Labels) return null;
        int lines = Math.Max(1, s.TitleLines);
        TextEl title = (ZuneListRules.IsRailRow(in s) ? R.Type.TrackTitle(d.Title) : SurfaceParts.RowTitleStyle(in s, d.Title)) with
        {
            MaxLines = lines, Wrap = lines > 1 ? TextWrap.Wrap : TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        var kids = new List<Element>(5);
        if (d.Eyebrow is not null) kids.Add(d.Eyebrow.Value);
        kids.Add(Title(d, title, lines, centred: false));
        if (d.Subtitle is not null) kids.Add(d.Subtitle.Value);
        if (d.MetaRow is not null) kids.Add(d.MetaRow.Value);
        if (d.Below is not null) kids.Add(d.Below.Value);
        return new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = s.IsLargeRow && !ZuneListRules.IsRailRow(in s) ? Spacing.S : SurfaceGeometry.CardLabelGap,
            Children = kids.ToArray(),
        };
    }

    static Element Title(Controls.CardData d, TextEl style, int lines, bool centred)
    {
        var (start, length) = d.Highlight;
        Element? run = length > 0
            ? Controls.SearchHighlight(d.Title, start, length, style.Size, style.ResolvedWeight, Tok.TextPrimary, lines)
            : null;
        return Controls.TrimmedTitle(style, d.Title, run, centred);
    }

    // The inline caption: the second line and the meta as ONE secondary paragraph (the meta a tertiary " · meta" tail), capped at `lines`.
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
