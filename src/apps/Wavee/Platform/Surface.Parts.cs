// ── Platform/Surface.Parts.cs ──────────────────────────────────────────────────────────────────────────────────────
// The media surface's TREE: the one shell (plate + ownership + cursor), the stack and row bodies, the label block with
// its trim tooltip, the selected skin, the shelf gutter, the seed face, the drag-arm barrier, and the one fill table.
//
// Role: UI
// Owner: L
// Wave: 0 (shared media surface)
// Budget: 450 lines
// Spec: docs/plans/wavee/shared-media-surface-implementation.md §2.3–§2.5
//
// Static element builders with no state: the ONE host (Surface.Host.cs) composes them, and a hook-free slot class (the
// sidebar's) or a (B) surface may call them directly. Every geometry constant is `SurfaceGeometry`'s (Surface.Rules.cs),
// so the estimators and this renderer agree to the DIP; every fill is `SurfacePlate.For`'s, so no caller paints its own
// hover. The look is the shelf column and gutter, the fluid AspectRatio grid cover and the 64-floor row, stated once.

using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

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

/// <summary>The media surface's static element builders (plan §2.3). Nothing here owns state or reads a signal outside a
/// bind thunk; the host decides what is mounted and passes it in.</summary>
public static class SurfaceParts
{
    // ══ 1. THE SHELL ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The ONE shell: a ZStack of [plate, <paramref name="content"/>]. Corners and focus margin come from the
    /// shape (the card radius + <c>FocusInsetBordered</c> on a stack, the control radius + <c>FocusInsetRow</c> on a
    /// row), the plate and stroke from <see cref="SurfacePlate.For"/>, the role, the tab stop and the cursor from the
    /// ownership rule (<see cref="SurfaceRules"/>) — never from a caller. The host adds the handlers with <c>with</c>.
    /// <para>The plate is a non-hit-testable REVEAL (<c>HoverOpacity</c>): it follows its container's hover AND press
    /// across the interaction boundary, and its explicit <c>PressedFill</c> opts it into the recorder's press cross-fade
    /// (an inherited press never darkens a fill on its own). 83 ms both ways — the ControlFaster rung.</para></summary>
    public static BoxEl Shell(Element content, in SurfaceShape s, in SurfaceOwnership mode, string uri)
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

    /// <summary>The selected skin: the 2-DIP accent border (<paramref name="accent"/>, read now — the host calls this in
    /// its render, so a palette landing repaints the one selected surface; null = the theme accent) over the brighter
    /// card fill, on the shell ROOT (Controls.Art.cs header, rule 1). A STATIC border: the tile kind's hairline is a bind,
    /// and no shape selects a tile (a static↔bound flip on one channel of a reused node is what <c>BindContract</c>
    /// reports).</summary>
    public static BoxEl Selected(BoxEl shell, Func<ColorF>? accent)
        => shell with { BorderColor = (accent ?? s_themeAccent)(), BorderWidth = 2f, Fill = Tok.FillCardDefault };

    static readonly Func<ColorF> s_themeAccent = static () => Tok.AccentDefault;

    /// <summary>A drag-arm BARRIER around <paramref name="el"/>: <c>DragController.TryArm</c>'s upward walk stops here, so a
    /// press on a control inside a row's trailing cluster (a Follow toggle, a heart, the "…") never arms the row's drag.
    /// The control itself still owns its click by the dispatcher's nearest-owner rule.</summary>
    public static BoxEl Action(Element el) => new()
    {
        BlocksDragArm = true, Shrink = 0f, AlignItems = FlexAlign.Center,
        Children = [el],
    };

    // ══ 2. THE BODIES ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A shelf card's inner width (the card less its 2 × 8 plate padding); NaN for a fluid grid card.</summary>
    public static float InnerOf(float width) => float.IsNaN(width) ? float.NaN : width - 2f * SurfaceGeometry.ShelfPlatePad;

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
    public static Element StackBody(Controls.CardData d, in SurfaceShape s, float width, bool overlay, bool chrome, bool hasMenu,
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

        float inner = InnerOf(width);
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
    public static Element RowBody(Controls.CardData d, in SurfaceShape s, bool overlay, bool chrome, bool hasMenu, Action? onPlay,
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

        Element? trailing = d.Trailing is null ? null : Action(d.Trailing);
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
            Action(new BoxEl
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
    static Element MenuLane(bool chrome) => Action(new BoxEl
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

    // ══ 3. THE LABELS ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A stack's label block. The title is capped at the shape's <see cref="SurfaceShape.TitleLines"/> (wrapping
    /// when it allows more than one) and sits inside the trim tooltip; the search-highlight arm gets the SAME budget as
    /// the plain title, so a filter narrowing a grid does not reflow a card from two lines to one. A <c>Meta</c> adds ONE
    /// tertiary caption line under the subtitle (2 gap + 16 — the <c>metaLine</c> the shelf extent budgets); an inline
    /// <c>Caption</c> instead carries the meta as a tertiary tail on its own <c>CaptionLines</c>, so no separate line
    /// exists. A circular card centres the block under its circle. <paramref name="inner"/> is the shelf's inner width
    /// (NaN on a fluid grid card).</summary>
    public static Element Labels(Controls.CardData d, in SurfaceShape s, float inner)
    {
        int lines = Math.Max(1, s.TitleLines);
        bool circular = Controls.CoverShape.IsCircular(d.Circular, d.CoverAspect);
        // A circular card's title is a CONTENT-SIZED run capped at the slot (MaxWidth), so the block's centring can place
        // it under the circle — a fixed Width made it a full-width, left-set line over a centred subtitle.
        TextEl title = Design.Type.CardTitle(d.Title) with
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
                kids.Add(Design.Type.TrackMeta(meta) with
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
    public static Element? RowText(Controls.CardData d, in SurfaceShape s)
    {
        if (!s.Labels) return null;
        bool large = s.IsLargeRow;
        int lines = Math.Max(1, s.TitleLines);
        TextEl title = RowTitleStyle(in s, d.Title) with
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

    static TextEl RowTitleStyle(in SurfaceShape s, string text)
        => s.IsLargeRow ? Design.Type.PageHero(text) : Design.Type.TrackTitle(text);

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
        var style = Design.Type.TrackMeta("");
        return new SpanTextEl(spans)
        {
            Size = style.Size, LineHeight = style.LineHeight, Color = Tok.TextSecondary,
            Wrap = lines > 1 ? TextWrap.Wrap : TextWrap.NoWrap, MaxLines = lines, Trim = TextTrim.CharacterEllipsis,
            Width = inner, MinWidth = 0f,
        };
    }

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
        float inner = InnerOf(width);
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
            Padding = shelf ? Edges4.All(SurfaceGeometry.ShelfPlatePad) : new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.M),
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
