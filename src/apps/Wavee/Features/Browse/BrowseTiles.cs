using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Features.Browse;

/// <summary>One destination in the Browse directory, independent of where it came from — so Search's genre results
/// and a browse category render through the same cells (<see cref="BrowseTiles"/>) instead of two divergent link
/// grammars for the same "this is a page you can open" fact.</summary>
readonly record struct BrowseTileModel(string Title, string Uri, uint? Color, Image? Artwork, Action Open);

/// <summary>The Browse directory's FIVE cell densities — Word / Name / Link / Bar / Peek, one per band, cheapest to
/// most expressive. <see cref="BrowseDirectory"/>'s class doc-comment has the why; this file is only the how.
///
/// Every cell is a link (<c>Role = Hyperlink</c>, <c>Focusable</c>, <c>Cursor = Hand</c>, keyed by the destination's
/// own uri) — the one thing every density shares, mirroring the single row-link cell this file replaces.</summary>
static class BrowseTiles
{
    // ── The plate every bare-text density now sits on ─────────────────────────────────────
    // Word and Name used to be UNPLATED type, and Name was set in the very same alias as the band heading above it
    // (WaveeType.ModuleHeader → Ui.Subtitle) while Word was set LARGER than that heading — so "Top" and "Music" read as
    // one undifferentiated stack with nothing to say that half of it is pressable, and the hierarchy was inverted on
    // top of that. The fix is a PAIR: the band heading drops to the eyebrow rung (BrowseDirectory.BandLabel) and the
    // destinations take a real control plate. The grammar is ContentFilterChips.Chip's, verbatim, because that is
    // already this app's interactive pill — FillControlDefault → Secondary on hover, a stroke that goes accent on
    // hover, and the subtle hover/press scale that says "button" before a pointer ever lands.
    static Element Chip(BrowseTileModel m, float height, Element? lead, TextEl label)
    {
        var text = label with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
        return new BoxEl
        {
            Key = m.Uri,
            Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
            OnClick = m.Open,
            // Shrink 0 on a WRAPPING row: the row breaks to a new line rather than compressing every pill until its
            // label ellipsises (ContentFilterChips' rail makes the same call for the same reason).
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Height = height, Shrink = 0f, MinWidth = 0f,
            Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
            Corners = CornerRadius4.All(999f),
            Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault, HoverBorderColor = Tok.AccentDefault,
            HoverScale = WaveeMotion.ScaleSubtle.Hover,
            HoverDurationMs = WaveeMotion.Fast, HoverEasing = Easing.FluentDecelerate,
            PressScale = WaveeMotion.ScaleSubtle.Press,
            Children = lead is null ? [text] : [lead, text],
        };
    }

    // ── Top: the four destinations Spotify puts first — the tallest pill, and the only one at BodyStrong, so the row
    // still reads as the primary one without being set larger than the heading that names it. ─────────────
    public static Element Word(BrowseTileModel m) =>
        Chip(m, BrowseLayout.WordChipH, null, Ui.BodyStrong(m.Title));

    // ── For you: the same plate one rung down, with the colour pip that gives each destination the identity colour
    // its own detail page will carry. ──────────────────────────────────────────────────
    public static Element Name(BrowseTileModel m) =>
        Chip(m, BrowseLayout.NameChipH, Pip(m), Ui.Body(m.Title));

    // ── Genres (also Search's genre results — SearchGenreTiles.Grid): the pip again, next to Body secondary —
    // one rung below Name because a genre column runs ~25 deep and a module-rung name would read as a wall of
    // headlines. Padding/corner/HoverFill are KEPT from the row-link cell this replaces (not dropped for the plain
    // pip+text look): at 14px in a list this long, the padded hit target and its hover fill are what makes one row
    // easy to click without catching its neighbour — that affordance is the one thing this density cannot lose.
    public static Element Link(BrowseTileModel m) => new BoxEl
    {
        Key = m.Uri,
        Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand,
        FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
        OnClick = m.Open, MinWidth = 0f,
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.XS, 4f, Spacing.XS, 4f),
        Corners = CornerRadius4.All(Radii.Control),
        HoverFill = Tok.FillControlSecondary,
        Children =
        [
            Pip(m),
            Body(m.Title).Secondary() with
            {
                HoverColor = Tok.AccentTextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap,
                Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            },
        ],
    };

    // ── Mood & activity: the house WinUI card plate — HomeFoldTile's own Fill/stroke/elevation, the SAME plate the
    // Fold tile sits on — with the category colour demoted from being the surface to a corner radial wash ON the
    // surface, plus the left accent hairline this density always had. browse-cards-v2-mica.html's hybrid (variant
    // 4): variant 2's card grammar under the tick, never the old flat wash-IS-the-plate (variant 1 / today's ship).
    public static Element Bar(BrowseTileModel m)
    {
        ColorF seed = Accent(m);
        return new BoxEl
        {
            Key = m.Uri,
            Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
            OnClick = m.Open, MinWidth = 0f,
            ZStack = true, Height = BrowseLayout.BarHeight, ClipToBounds = true,
            // The house card plate (HomeFoldTile verbatim): FillCardDefault → FillCardSecondary on hover at the SAME
            // elevation — never a lift. This REPLACES the old "no explicit HoverFill" note: that note described the
            // engine's own auto-lighten default standing in for a hover state on a saturated WASH plate, which no
            // longer exists here — on a THEME card surface the house hover fill (the same swap every other card in
            // this app uses) is the correct, no-longer-improvised state.
            Corners = Radii.CardAll, Fill = Tok.FillCardDefault, HoverFill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Shadow = Elevation.Card,
            Children =
            [
                CornerWash(seed, BrowseLayout.BarHeight, centerY: 0.45f, radiusX: 0.65f, radiusY: 1.40f),
                Tick(seed, BrowseLayout.BarHeight),
                new BoxEl
                {
                    HitTestVisible = false, Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.Start,
                    AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                    Padding = Edges4.All(Spacing.S),
                    Children =
                    [
                        WaveeType.CardTitle(m.Title) with
                        {
                            // The plate is a theme surface now, not on-media art — OnMediaPrimary (hardcoded white)
                            // would break the light theme; TextPrimary is the card-surface ink every other card uses.
                            Color = Tok.TextPrimary, MaxLines = 2, Wrap = TextWrap.Wrap,
                            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                    ],
                },
            ],
        };
    }

    // ── More: the SAME house card plate as Bar (and HomeFoldTile) under the tilted hanging cover, kept byte-for-
    // byte — browse-cards-v2-mica.html's hybrid (variant 4) drops variant 2's plate/wash under Zune's own peek art
    // rather than replacing it. The long tail still earns the same card weight as Charts: an uncurated category is
    // not a lesser destination, just an unsorted one. ────────────────────────────────────────────────────────────
    public static Element Peek(BrowseTileModel m, float cardW)
    {
        ColorF seed = Accent(m);
        float copyMax = cardW > 0f ? cardW * BrowseLayout.PeekCopyFrac : float.NaN;
        var copy = new BoxEl
        {
            HitTestVisible = false, Direction = 1, Justify = FlexJustify.Start, MinWidth = 0f,
            Padding = Edges4.All(Spacing.M),
            MaxWidth = copyMax,
            Children =
            [
                WaveeType.ModuleHeader(m.Title) with
                {
                    // See Bar's own note: the plate is a theme surface now, so the title ink is TextPrimary, not the
                    // on-media white this density used while its plate was a full-bleed colour field.
                    Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2,
                    Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
            ],
        };

        var children = new List<Element>(4)
        {
            CornerWash(seed, BrowseLayout.MoreHeight, centerY: 0.28f, radiusX: 0.70f, radiusY: 1.30f),
            Tick(seed, BrowseLayout.MoreHeight),
            copy,
        };
        if (m.Artwork is not null) children.Add(PeekArt(m));

        return new BoxEl
        {
            Key = "browse-peek:" + m.Uri + ":" + cardW.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
            OnClick = m.Open,
            ZStack = true, Height = BrowseLayout.MoreHeight, MinWidth = 0f, ClipToBounds = true,
            Corners = Radii.CardAll, Fill = Tok.FillCardDefault, HoverFill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Shadow = Elevation.Card,
            Children = children.ToArray(),
        };
    }

    // The corner accent wash Bar/Peek's card plate carries — HomeFoldTile's own radial-GradientSpec technique
    // verbatim (a RAW, un-muted category colour anchored at the card's right edge, fading to transparent at 72% —
    // browse-cards-v2-mica.html's `.bar.v2::after` / `.peek-cell.v2 .wash`), just parameterised per density's own
    // anchor/spread. Alpha is carried on the OVERLAY NODE's Opacity/HoverOpacity (the reveal-leg pair every hover-
    // revealed decorative overlay in this app uses — MediaCard's hover plates, TrackRow's rest strip, etc. — not a
    // WhileHover MotionTarget delta: this node is a plain decoration, not a gesture-state pose), so the .20 rest /
    // .34 hover ramp costs nothing beyond the two fields and rides the house ~83ms hover cross-fade by default
    // (Reconciler's InteractionAnim.ControlFasterMs fallback — the same duration MotionTok.ControlFaster names).
    static Element CornerWash(ColorF seed, float height, float centerY, float radiusX, float radiusY) => new BoxEl
    {
        HitTestVisible = false, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
        Height = height, Opacity = 0.20f, HoverOpacity = 0.34f,
        Gradient = new GradientSpec(GradientShape.Radial, 0f,
        [
            new GradientStop(0f, seed with { A = 1f }),
            new GradientStop(0.72f, seed with { A = 0f }),
        ])
        {
            RadialCenter = new Point2(1.0f, centerY),
            RadialRadius = new Point2(radiusX, radiusY),
        },
    };

    // The hanging cover: bottom-right corner of the ZStack (AlignSelf = vertical, JustifySelf = horizontal — the
    // ZStack overlay-alignment contract), nudged past the corner with a negative margin so it reads as PEEKING out
    // of the frame rather than sitting flush inside it; ClipToBounds on the root trims the overflow to a clean edge,
    // the same "hang off the frame" cut HomeFoldTile's stacked covers use. No cardW is available here (Peek takes no
    // width parameter), so — unlike HomeModuleLayout.FoldRest's absolute per-card-width placement — this is corner
    // alignment plus a fixed offset rather than a computed one; it holds up at every column width BrowseLayout.
    // MoreColumns produces.
    static Element PeekArt(BrowseTileModel m)
    {
        const float hang = BrowseLayout.Peek * 0.3f;
        return new BoxEl
        {
            Width = BrowseLayout.Peek, Height = BrowseLayout.Peek,
            AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.End,
            Margin = new Edges4(0f, 0f, -hang, -hang),
            Rotation = -8f,
            // Rest-pose-relative (MotionTarget contract): these are DELTAS on the Rotation/Offset rest pose above,
            // never absolute values. The hover edge cascades down from the tile's own OnClick root (see
            // HomeFoldTile's class doc-comment for the same cascading-hover idiom this copies) even though this node
            // is HitTestVisible = false.
            WhileHover = new MotionTarget { OffsetX = -6f, OffsetY = 2f, Rotation = 3f },
            Transition = MotionTok.ControlNormal,
            HitTestVisible = false,
            Shadow = Elevation.Card, ClipToBounds = true,
            Corners = CornerRadius4.All(Radii.Control),
            Children = [Surfaces.Artwork(m.Artwork, SpotifyExportMapper.Hash(m.Uri),
                BrowseLayout.Peek, BrowseLayout.Peek, Radii.Control, decodePx: 128)],
        };
    }

    // The 8-DIP identity pip Name/Link share — a rounded square (prototype `.pip { border-radius: 2px }`), not a
    // circle: a disc next to a name reads as a bullet, a block reads as a colour swatch.
    static Element Pip(BrowseTileModel m) => new BoxEl
    {
        Width = BrowseLayout.Pip, Height = BrowseLayout.Pip, Corners = CornerRadius4.All(Spacing.XXS),
        Fill = Accent(m), HitTestVisible = false, Shrink = 0f,
    };

    // Full-height left hairline on Bar/Peek — a saturated RAW-colour accent against the card plate, distinct from
    // the CornerWash's own softer, edge-anchored read of the same colour, not a second fill.
    static Element Tick(ColorF seed, float height) => new BoxEl
    {
        Width = BrowseLayout.TickW, Height = height,
        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Start,
        Corners = CornerRadius4.All(Spacing.XXS), Fill = seed,
        HitTestVisible = false, Shrink = 0f,
    };

    // ONE accent resolution for every cell. 0/null is "this category carries no colour", never a request for a
    // transparent tile — WaveePalette.ToColor(0) would paint pure black, so the semantic accent is the fallback,
    // same rule HomeFoldTile's own wash and ChromeAccent use.
    static ColorF Accent(BrowseTileModel m) => m.Color is { } c ? WaveePalette.ToColor(c) : Tok.AccentDefault;

    public static Element LinkGrid(IReadOnlyList<BrowseTileModel> items, float width)
    {
        int cols = BrowseLayout.LinkColumns(width > 0f ? width : BrowseLayout.DirectoryFallbackWidth);
        var cells = new Element[items.Count];
        for (int i = 0; i < items.Count; i++) cells[i] = Link(items[i]);
        return BrowseLayout.StarGrid(cols, Spacing.M, Spacing.S, cells) with { Key = "browse-link-grid:" + cols };
    }

    /// <summary>One category → tile model — the routing decision <c>BrowsePage.ToTile</c> and
    /// <c>BrowseDirectory.ToModel</c> used to each carry as their own private copy. <paramref name="openCategory"/>/
    /// <paramref name="openFeature"/> both null means the caller has no navigation host yet (a null page
    /// <c>Model</c>): the tile still renders and still highlights, it just does nothing — the shared inert
    /// <see cref="ToModelNoop"/>.</summary>
    public static BrowseTileModel ToModel(BrowseCategory c, Action<string, string>? openCategory, Action<string>? openFeature) => new(
        c.Title, c.Uri, c.Color, c.Artwork,
        openCategory is null && openFeature is null
            ? ToModelNoop
            : () =>
            {
                // A client feature (Live Events) is NOT a browse page — it routes into the client's own surface.
                if (c.IsClientFeature) openFeature!(c.Uri);
                else openCategory!(c.Uri, c.Title);
            });

    static readonly Action ToModelNoop = static () => { };
}

/// <summary>Layout constants for <see cref="BrowseTiles"/> — sized here, next to the cells that read them, rather
/// than in <c>HomeModules.HomeModuleLayout</c> (a different concern owns that file).</summary>
static class BrowseLayout
{
    // ── THE masthead frame: one title Y for the directory, a category page, and a section drill ──
    /// Top inset of every Browse-family surface. No crumb band lives here any more — the trail rides IN the
    /// masthead's own title line (BrowseMasthead — Zune's breadcrumb-as-title) — this is now plain shared breathing
    /// room above the masthead, the one title Y every Browse-family surface shares.
    public const float FrameTop = Spacing.XXXL;
    /// Overlay masthead reserve (FrameTop + SurfaceDisplay line). Family pages pad this PLUS Spacing.L so the body
    /// stays put while the overlay fades — a live height would re-pad parked pages mid-exit.
    public const float MastheadReserve = BrowseMastheadMetrics.Reserve;
    /// The page gutter — Home's own (HomeSectionPage, RecentsPage use the same), so a drill never shifts the column.
    public const float FrameX = Spacing.PageWide;
    public static Edges4 Frame(float bottom) => new(FrameX, FrameTop, FrameX, bottom);
    /// First-frame width guess the directory's Responsive grids share before a real measure lands.
    public const float DirectoryFallbackWidth = 900f;

    /// <summary>The Top band's pill height — WinUI's 36 DIP "large" control rung, the tallest of the three link
    /// densities.</summary>
    public const float WordChipH = 36f;
    /// <summary>The For-you band's pill height — the standard 32 DIP control rung, one step under
    /// <see cref="WordChipH"/> so the two bands stay ordered without either being set larger than its own heading.
    /// </summary>
    public const float NameChipH = 32f;
    /// <summary>Gap between plated link cells. Bare words needed <c>Spacing.L</c> of air to separate; pills carry their
    /// own edges, so the same gap reads as a scattered row.</summary>
    public const float ChipGap = Spacing.S;

    /// <summary>Prototype <c>.sec .tick</c> — 3×14, the compact band label and the Bar/Peek left hairline share the width.</summary>
    public const float TickW = 3f;
    /// <summary>Prototype <c>.sec .tick { height:14px }</c> — band labels only; Bar/Peek hairlines stretch the tile.</summary>
    public const float TickH = 14f;
    /// <summary>Mood &amp; activity's bar height.</summary>
    public const float BarHeight = 52f;
    /// <summary>More's peek-card height.</summary>
    public const float MoreHeight = 88f;
    /// <summary>The peek card's hanging cover — square.</summary>
    public const float Peek = 80f;
    /// <summary>Prototype <c>.hub-tile .t { max-width:62% }</c>.</summary>
    public const float PeekCopyFrac = 0.62f;
    /// <summary>Name/Link's identity pip — 8 DIP rounded square.</summary>
    public const float Pip = 8f;

    // 240/120 give a 3-col target at ~720 and a 2-col step at ~380 — wide enough that the longest localised genre
    // name ("Folk & Acoustic", "Funk & Disco") plus the 8-DIP pip and its gap never wraps at the top tier. Fixed
    // bands rather than a continuous floor-divide (the SAME style HomeModuleLayout.Columns' container queries use,
    // not the old per-file width/min-column-width division the directory used before this file existed): a
    // floor-divide can flap between two column counts across a single pixel of resize at a boundary, where a fixed
    // set of bands cannot.
    /// <summary>3-col target for the Genres grid.</summary>
    public static int LinkColumns(float width) => width > 720f ? 3 : width > 380f ? 2 : 1;

    /// <summary>Prototype <c>.bars { minmax(132px, 1fr) }</c> — denser than Link's 3-col bands.</summary>
    public const float BarColMin = 132f;
    /// <summary>Column count for the Mood grid at <paramref name="width"/> — floor-fit.</summary>
    public static int BarColumns(float width) => Math.Max(1, (int)(width / BarColMin));

    // minmax(168px, …) — the same floor MediaCard's shelf cells use (HomeModuleLayout.ShelfCardMin): the More band's
    // peek cards read as CARDS, not a text column, so they want a wider floor than Link's 120.
    public const float MoreColMin = 168f;
    const int MoreColMax = 4;
    /// <summary>Column count for the More grid at <paramref name="width"/> — floor-fit, capped.</summary>
    public static int MoreColumns(float width) => Math.Clamp((int)(width / MoreColMin), 1, MoreColMax);

    /// <summary>A star grid, not a wrapped flex row — a row of Grow=1 cells reports a one-line MEASURE height
    /// because nothing divides the available width until Arrange. AlignSelf=Stretch backfills a 1-column band.</summary>
    public static Element StarGrid(int columns, float colGap, float rowGap, IReadOnlyList<Element> cells)
    {
        if (cells.Count == 0) return new BoxEl();
        var tracks = new TrackSize[Math.Max(1, columns)];
        for (int i = 0; i < tracks.Length; i++) tracks[i] = TrackSize.Star();
        return Grid(tracks, colGap, rowGap, float.NaN, [.. cells]) with { AlignSelf = FlexAlign.Stretch };
    }
}
