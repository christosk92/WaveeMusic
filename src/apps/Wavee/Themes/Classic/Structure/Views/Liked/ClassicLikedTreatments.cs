// ── Themes/Classic/Structure/Views/Liked/ClassicLikedTreatments.cs — the nine cover treatments and the heart (themes plan 6.1, T9c) ──
//
// From `Entities\User.Cover.cs` (`LikedTreatments`, `LikedHeart`): a static factory layer, no hooks and no state; the cover view
// (`ClassicLikedCover`) owns every hook.
//
// ONE AUTHORING CANVAS. Every number is a DIP inside a 304-square canvas (the prototype's own tile) dropped into a `Scaled` wrapper at
// `size / 304`. The looping keyframe arrays can therefore be `static readonly` (the engine stores keyframes BY REFERENCE, so one array
// must be right at every size), a rail drag re-scales one composited transform instead of re-laying-out 36 tiles, and a 76-DIP picker
// miniature is provably the SAME composition as the cover behind the flyout.
//
// THE CLIP FRAME IS OUTSIDE AND UNROTATED (Wall and Marquee rotate their content): ClipToBounds on or under a rotated node clips to
// the axis-aligned box, which would cut the cover's corners.
//
// INK ON IMAGERY IS `R.OnMedia`, never a theme token: a Rainbow cover is every luminance at once (P21: the scrims and inks are the
// on-media ladder; the plates and washes, being artwork, are `ClassicLikedRules`).

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Render;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

internal static class ClassicLikedHeart
{
    /// <summary>The authored view-box edge; stroke widths below are in VIEW-BOX units.</summary>
    public const float ViewBox = 24f;

    // ThemedIconData's "HeartFill" base layer, character for character — the string IS the interning key.
    const string Contour =
        "M12 20 C12 20 4 14.5 4 8.75 C4 6 6.1 4 8.5 4 C10.1 4 11.4 4.95 12 6.3 C12.6 4.95 13.9 4 15.5 4 "
      + "C17.9 4 20 6 20 8.75 C20 14.5 12 20 12 20 Z";

    /// <summary>Interned ONCE through the shared table (one content epoch forever — the tessellation cache stays
    /// warm; a per-render parse would re-tessellate every frame).</summary>
    public static readonly PathData Data = Intern();

    static PathData Intern()
    {
        int id = PathGeometryTable.Shared.Register(Contour, ViewBox, ViewBox, FillRule.NonZero);
        PathGeometryTable.Shared.TryGet(id, out var data);
        return data;
    }

    public static PathEl Fill(float edge, ColorF fill) => new()
    {
        Width = edge, Height = edge, ViewBoxW = ViewBox, ViewBoxH = ViewBox,
        Geometry = Data, Fill = fill, Rule = FillRule.NonZero,
    };

    public static PathEl Rim(float edge, ColorF stroke, float width = 0.3f) => new()
    {
        Width = edge, Height = edge, ViewBoxW = ViewBox, ViewBoxH = ViewBox,
        Geometry = Data, StrokeColor = stroke, Stroke = new StrokeStyle(width, LineCap.Round, LineJoin.Round),
    };
}

internal static class ClassicLikedTreatments
{
    public const float DesignSize = 304f;
    /// <summary>Below this the name chip's TEXT stops being text (0.46 scale at 140 renders 12.5 DIP under 6).</summary>
    public const float ChromeMinSize = 180f;
    /// <summary>The badge floor — also the treatment floor itself (the site ladder collapses below it).</summary>
    public const float BadgeMinSize = 140f;

    // Decode buckets shared with the rest of the app so a cell, a Home card and the rail hero hit ONE texture.
    const int WallDecodePx = 64, GridDecodePx = 128, HeroDecodePx = 256;

    /// <summary>The ambient-loop sinks (three: Marquee's bands each key their own TranslateX row). Default for a
    /// miniature, which is what makes nine live thumbnails cost no timeline rows.</summary>
    public readonly record struct Loops(Action<NodeHandle>? A, Action<NodeHandle>? B, Action<NodeHandle>? C);

    /// <summary>Build a treatment. <paramref name="style"/> must already be effective; Stock (and, defensively, no
    /// tiles) returns the bundled PNG.</summary>
    public static Element Build(LikedCoverStyle style, string[] tiles, string tileKey, int trackCount,
                                float size, float radius, bool mini, string? morphKey, in Loops loops = default)
    {
        if (style == LikedCoverStyle.Stock || tiles.Length == 0) return ClassicLikedLeaves.StockCover(size, radius, morphKey);

        bool chrome = !mini && size >= ChromeMinSize;
        bool badge = !mini && size >= BadgeMinSize;
        Element design = style switch
        {
            LikedCoverStyle.Lens => Lens(tiles, mini),
            LikedCoverStyle.Wall => Wall(tiles, mini, chrome, loops),
            LikedCoverStyle.Rainbow => Rainbow(tiles, tileKey, chrome),
            LikedCoverStyle.Marquee => MarqueeBands(tiles, mini, badge, loops),
            LikedCoverStyle.Feature => Feature(tiles, chrome),
            LikedCoverStyle.Tone => Tone(tiles, trackCount, mini),
            LikedCoverStyle.Stack => StackFan(tiles, badge),
            _ => MosaicGrid(tiles, chrome),
        };
        return new BoxEl
        {
            ZStack = true, Width = size, Height = size, Shrink = 0f, ClipToBounds = true,
            Corners = CornerRadius4.All(radius),
            // The composed cover takes the exact slot the stock PNG's MorphId held (a Home card → rail fly keeps a
            // participant on both ends).
            MorphId = morphKey,
            // KEYED BY STYLE: a swap REMOUNTS the canvas, so a Wall drift row can never keep ticking on what is now a
            // Marquee band (M-3).
            Children = [Scaled(size, design) with { Key = "liked-style:" + style }],
        };
    }

    // ── the canvas ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>304-canvas → cover size, top-left origin: pure magnification, never a re-centring.</summary>
    static Element Scaled(float size, Element canvas) => new BoxEl
    {
        Width = DesignSize, Height = DesignSize, Shrink = 0f, ZStack = true, HitTestVisible = false,
        ScaleX = size / DesignSize, ScaleY = size / DesignSize, TransformOriginX = 0f, TransformOriginY = 0f,
        Children = [canvas],
    };

    static BoxEl CanvasOf(params Element[] kids) => new()
    {
        ZStack = true, Width = DesignSize, Height = DesignSize, HitTestVisible = false, Children = kids,
    };

    // ── shared pieces ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One artwork cell: COVER-fit inside a sized clipping box (DecodePx is ignored once Width is explicit —
    /// the wrapper is what lets a cell decode at a shared bucket). The placeholder is the cover's own LIVE tint, so a
    /// still-decoding cell is that record's colour and never a grey hole (E5).</summary>
    public static Element ArtCell(string? url, float edge, float corners, int decodePx) => new BoxEl
    {
        Width = edge, Height = edge, Shrink = 0f, ClipToBounds = true, HitTestVisible = false,
        Corners = corners > 0f ? CornerRadius4.All(corners) : default,
        Children = [ArtImage(url, decodePx, corners)],
    };

    static ImageEl ArtImage(string? url, int decodePx, float corners)
        => Image(url ?? "", ImageFit.Cover, 1f, decodePx, corners, placeholder: (ColorF?)null)
            with { Placeholder = Design.WatchedPlaceholder(url) };

    /// <summary>An N-column uniform grid at an explicit extent (a grid inside a ZStack has no width to distribute).</summary>
    public static Element TileGridOf(int columns, Element[] cells, float cellEdge, float gap)
    {
        int rows = (cells.Length + columns - 1) / columns;
        return UniformGrid(columns, gap, cellEdge, cells) with
        {
            Width = columns * cellEdge + (columns - 1) * gap,
            Height = rows * cellEdge + (rows - 1) * gap,
        };
    }

    static Element FilledGrid(string[] tiles, int columns, int cells, float cellEdge, float gap, int decodePx)
    {
        var filled = LikedCoverRules.FillCells(tiles, cells);
        var kids = new Element[filled.Length];
        for (int i = 0; i < filled.Length; i++) kids[i] = ArtCell(filled[i], cellEdge, 0f, decodePx);
        return TileGridOf(columns, kids, cellEdge, gap);
    }

    // P21: the inks and scrims over artwork are the on-media ladder (theme-invariant: Classic's ink is white and its scrim black in
    // both themes), read where the element is built, never cached at type load.
    static ColorF Black => R.OnMedia.ScrimRest with { A = 1f };
    static ColorF White => R.OnMedia.Ink;

    static Element Fill(GradientSpec gradient) => new BoxEl
    {
        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false, Gradient = gradient,
    };

    /// <summary>The prototype's <c>.scrim</c>: nothing for the top 55 %, then down to black.</summary>
    static Element Scrim(float bottomAlpha) => Fill(GradientDown(
        new GradientStop(0f, Black with { A = 0f }),
        new GradientStop(0.55f, Black with { A = 0f }),
        new GradientStop(1f, Black with { A = bottomAlpha })));

    /// <summary>The on-media name chip, bottom-left: ♥ 16 + "Liked Songs" 12.5/16/600.</summary>
    static Element NameChip() => new BoxEl
    {
        AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start, Margin = new Edges4(14f, 0f, 0f, 14f),
        Direction = 0, AlignItems = FlexAlign.Center, Gap = ClassicLikedRules.NameChipGap, Shrink = 0f,
        Height = ClassicLikedRules.NameChipHeight, Padding = new Edges4(10f, 0f, 12f, 0f),
        Corners = Radii.FullAll, Fill = R.OnMedia.ScrimRest,
        BorderWidth = 1f, BorderColor = R.OnMedia.Stroke, HitTestVisible = false,
        Children =
        [
            Icon(Icons.HeartFill, 16f, R.OnMedia.Ink),
            R.Type.DenseTitle(Loc.Get(Strings.Detail.LikedSongs)) with
            {
                Color = R.OnMedia.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        ],
    };

    /// <summary>The white badge disc with the heart. The glyph's near-black is a constant: the badge sits on a palette
    /// GROUND, and tinting it from the same swatch is how it would vanish into it.</summary>
    static Element HeartBadge(bool right, ShadowSpec? shadow = null) => new BoxEl
    {
        AlignSelf = FlexAlign.End, JustifySelf = right ? FlexAlign.End : FlexAlign.Start,
        Margin = right ? new Edges4(0f, 0f, 14f, 14f) : new Edges4(14f, 0f, 0f, 14f),
        Width = ClassicLikedRules.BadgeEdge, Height = ClassicLikedRules.BadgeEdge, Shrink = 0f, Corners = Radii.FullAll, Fill = ClassicLikedRules.BadgeDisc,
        Shadow = shadow ?? Elevation.Card, HitTestVisible = false,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [Icon(Icons.HeartFill, ClassicLikedRules.BadgeGlyphEdge, ClassicLikedRules.BadgeGlyph)],
    };

    /// <summary>The badge's cast on ART: fixed, theme-independent (a theme-split token is a 10 % whisper in light
    /// theme that a white disc on a bright sleeve loses — artwork has no theme).</summary>
    static readonly ShadowSpec BadgeOnArtShadow = new(Blur: 14f, OffsetY: 3f, OffsetX: 0f, Color: ClassicLikedRules.BadgeCast);

    /// <summary>A zero-extent, hit-transparent child, so a conditional chip/badge is an EMPTY child rather than a
    /// varying child COUNT — the ZStack's other children keep their positions across a size change.</summary>
    static readonly Element Nothing = new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };

    static string? At(string[] tiles, int i) => tiles.Length == 0 ? null : tiles[i % tiles.Length];

    // ── lens (W5) ───────────────────────────────────────────────────────────────────────────────────────────────

    // `mask: <heart> center / 76% 76%`: the window is a heart at 76 % of the tile, centred, and the mosaic INSIDE it
    // is the FULL-canvas mosaic pushed back by exactly the centring inset — ground and window are the same 304
    // composition at the same origin, so the crisp copy aligns with the blurred one to the pixel.
    const float LensHeartEdge = 0.76f * DesignSize;              // 231.04
    const float LensInset = (DesignSize - LensHeartEdge) / 2f;   // 36.48
    const float LensGroundSigma = 22f;
    const float LensGroundSaturation = 1.25f;
    // brightness(.62) EXACTLY: source-over black at 0.38 leaves 0.62 · src.
    static ColorF LensGroundDim => Black with { A = 0.38f };
    const float LensWindowSaturation = 1.12f;
    const float LensRimWidth = 0.35f;

    /// <summary>Lens — the heart as a WINDOW. ZStack order is E7: the async BakedBlur ground sits UNDER the crisp
    /// window (its arrival is a fade behind a fixed shape). The path clip is a HARD edge, so the rim is a SIBLING
    /// ABOVE the clip — a stroke inside would be halved by the boundary it exists to dress.</summary>
    static Element Lens(string[] tiles, bool mini)
    {
        const float cell = DesignSize / 3f;
        var filled = LikedCoverRules.FillCells(tiles, 9);
        return CanvasOf(
            // A miniature skips the BAKE and keeps the DIM (nine derived images per thumbnail buys a 4-px blur).
            LensMosaic(filled, cell, dim: true, blur: !mini),
            ClassicLikedLeaves.LensVeil(At(tiles, 0), At(tiles, 1)),
            new BoxEl
            {
                AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
                Width = LensHeartEdge, Height = LensHeartEdge, Shrink = 0f, ZStack = true, HitTestVisible = false,
                ClipToBounds = true, ClipPath = ClassicLikedHeart.Data, ClipPathRule = FillRule.NonZero,
                ClipPathViewBoxW = ClassicLikedHeart.ViewBox, ClipPathViewBoxH = ClassicLikedHeart.ViewBox,
                Children =
                [
                    // The positioner is its own node: the clip node stays at the centred rect the geometry fits.
                    new BoxEl
                    {
                        OffsetX = -LensInset, OffsetY = -LensInset, Width = DesignSize, Height = DesignSize,
                        Shrink = 0f, HitTestVisible = false,
                        Children = [LensMosaic(filled, cell, dim: false, blur: false)],
                    },
                ],
            },
            new BoxEl
            {
                AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
                Width = LensHeartEdge, Height = LensHeartEdge, Shrink = 0f, HitTestVisible = false,
                Children = [ClassicLikedHeart.Rim(LensHeartEdge, White with { A = 0.55f }, LensRimWidth)],
            },
            // The sheen is the light on the whole object — last, over the rim.
            Fill(GradientDown(
                new GradientStop(0f, White with { A = 0.10f }),
                new GradientStop(0.45f, White with { A = 0f }),
                new GradientStop(1f, Black with { A = 0.18f }))));
    }

    /// <summary>Lens's 3×3 — the same nine cells at the same geometry in both copies; they differ only in the
    /// per-image shader state. ENGINE ADAPTATION: BakedBlur is per image, so the ground keeps faint cell boundaries
    /// where the CSS blur bleeds (§4.4 #5).</summary>
    static Element LensMosaic(string[] filled, float cell, bool dim, bool blur)
    {
        var kids = new Element[filled.Length];
        for (int i = 0; i < filled.Length; i++)
        {
            var img = ArtImage(filled[i], GridDecodePx, 0f) with
            {
                Saturation = dim ? LensGroundSaturation : LensWindowSaturation,
                ColorOverlay = dim ? LensGroundDim : ColorF.Transparent,
                BakedBlur = blur ? new BakedBlurSpec(LensGroundSigma) : null,
            };
            kids[i] = new BoxEl { Width = cell, Height = cell, Shrink = 0f, ClipToBounds = true, HitTestVisible = false, Children = [img] };
        }
        return TileGridOf(3, kids, cell, 0f);
    }

    // ── flat grids (W9, W10, W7) ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Mosaic — the nine newest likes edge to edge, a .55 scrim and the name chip.</summary>
    static Element MosaicGrid(string[] tiles, bool chrome)
    {
        const float cell = DesignSize / 3f;
        return CanvasOf(FilledGrid(tiles, 3, 9, cell, 0f, GridDecodePx), Scrim(0.55f), chrome ? NameChip() : Nothing);
    }

    /// <summary>Feature — the newest like at 2×2 (202, the 256 bucket) with six followers at 100 (128), gap 2. Explicit
    /// rows, not a grid span: one uniform grid cannot give the hero its own decode bucket.</summary>
    static Element Feature(string[] tiles, bool chrome)
    {
        const float gap = 2f;
        const float cell = (DesignSize - 2f * gap) / 3f;      // 100
        const float hero = cell * 2f + gap;                   // 202
        var filled = LikedCoverRules.FillCells(tiles, 7);
        Element top = new BoxEl
        {
            Direction = 0, Gap = gap, Shrink = 0f, HitTestVisible = false,
            Children =
            [
                ArtCell(filled[0], hero, 0f, HeroDecodePx),
                new BoxEl
                {
                    Direction = 1, Gap = gap, Shrink = 0f, HitTestVisible = false,
                    Children = [ArtCell(filled[1], cell, 0f, GridDecodePx), ArtCell(filled[2], cell, 0f, GridDecodePx)],
                },
            ],
        };
        Element bottom = new BoxEl
        {
            Direction = 0, Gap = gap, Shrink = 0f, HitTestVisible = false,
            Children = [ArtCell(filled[3], cell, 0f, GridDecodePx), ArtCell(filled[4], cell, 0f, GridDecodePx), ArtCell(filled[5], cell, 0f, GridDecodePx)],
        };
        return CanvasOf(
            new BoxEl { Direction = 1, Gap = gap, Width = DesignSize, Height = DesignSize, HitTestVisible = false, Children = [top, bottom] },
            Scrim(0.5f),
            chrome ? NameChip() : Nothing);
    }

    /// <summary>Rainbow — sixteen covers in a hue serpentine. The GRID is a leaf: its ORDER is a function of the
    /// gradings, which arrive after the first paint.</summary>
    static Element Rainbow(string[] tiles, string tileKey, bool chrome)
    {
        const float gap = 2f;
        const float cell = (DesignSize - 3f * gap) / LikedCoverRules.RainbowColumns;   // 74.5
        return CanvasOf(
            new BoxEl { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false, Fill = ClassicLikedRules.PlateDark },
            ClassicLikedLeaves.RainbowGrid(tileKey, LikedCoverRules.FillCells(tiles, LikedCoverRules.MaxTiles), cell, gap, GridDecodePx),
            chrome ? NameChip() : Nothing);
    }

    // ── wall (W6) ───────────────────────────────────────────────────────────────────────────────────────────────

    // `.wallgrid { left:-34%; top:-42%; width:170% }`.
    const float WallLeft = -0.34f * DesignSize, WallTop = -0.42f * DesignSize, WallSpan = 1.70f * DesignSize;
    const float WallGap = 7f;
    const float WallDriftDip = -46f;
    const float WallLoopMs = 92_000f;   // the CSS 46 s alternate, unrolled into one there-and-back loop

    static readonly Keyframe[] s_wallDrift = [new(0f, 0f), new(0.5f, WallDriftDip), new(1f, 0f)];

    /// <summary>Reduced motion as a VALUE (the canon rule): the loop is still declared and still owns its slab row; its
    /// amplitude is zero. A structural <c>if</c> would change what is authored.</summary>
    static readonly Keyframe[] s_still = [new(0f, 0f), new(1f, 0f)];

    /// <summary>Wall — a tilted record wall under a very slow drift. ENGINE ADAPTATION: no perspective, so the
    /// rotateX(16°) is dropped and the −11° roll + drift carry the read (§4.4 #4). TWO nodes, required: the drift writes
    /// TranslateY and the anim fold reseeds a node's translate from its composited transform, so the static offset
    /// lives on the positioner and the roll + track on the inner node.</summary>
    static Element Wall(string[] tiles, bool mini, bool chrome, in Loops loops)
    {
        int columns = mini ? 4 : 6;   // a miniature caps at 4×4
        int cells = columns * columns;
        float cell = (WallSpan - (columns - 1) * WallGap) / columns;
        var kids = new Element[cells];
        for (int i = 0; i < cells; i++) kids[i] = ArtCell(tiles[LikedCoverRules.WallCellIndex(i, tiles.Length)], cell, 3f, WallDecodePx);

        Element drifting = new BoxEl
        {
            Direction = 1, Shrink = 0f, HitTestVisible = false,
            Rotation = -11f, TransformOriginX = 0.5f, TransformOriginY = 0.5f,
            OnRealized = loops.A,
            Children = [TileGridOf(columns, kids, cell, WallGap)],
        };
        return CanvasOf(
            new BoxEl { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false, Fill = ClassicLikedRules.PlateDark },
            new BoxEl { OffsetX = WallLeft, OffsetY = WallTop, Width = WallSpan, Shrink = 0f, HitTestVisible = false, Children = [drifting] },
            // The two-layer vignette: a radial low-point at (50 %, 42 %), then a vertical pass seating top and foot.
            Fill(new GradientSpec(GradientShape.Radial, 0f,
            [
                new GradientStop(0.46f, ClassicLikedRules.VignetteInk with { A = 0f }),
                new GradientStop(1f, ClassicLikedRules.VignetteInk with { A = 0.62f }),
            ])
            { RadialCenter = new Point2(0.5f, 0.42f), RadialRadius = new Point2(1.2f, 1.2f) }),
            Fill(GradientDown(
                new GradientStop(0f, ClassicLikedRules.VignetteInk with { A = 0.28f }),
                new GradientStop(0.32f, ClassicLikedRules.VignetteInk with { A = 0f }),
                new GradientStop(0.62f, ClassicLikedRules.VignetteInk with { A = 0f }),
                new GradientStop(1f, ClassicLikedRules.VignetteInk with { A = 0.50f }))),
            chrome ? NameChip() : Nothing);
    }

    // ── marquee (W8) ────────────────────────────────────────────────────────────────────────────────────────────

    // Tile 104 and a THIRD band (not the prototype's 92 × 2): the collection CROSSING the tile with the ground as the
    // seam between bands; either change alone reads as stripes.
    const float MarqueeTile = 104f, MarqueeGap = 7f;
    const int MarqueeRun = 8;
    const float MarqueeRepeat = MarqueeRun * (MarqueeTile + MarqueeGap);   // 888
    const float MarqueeLeft = -0.60f * DesignSize;                         // -182.4
    // `.band { width:230% }` places the PIVOT (the band box's own centre), not the strip's extent.
    const float MarqueeBandWidth = 2.30f * DesignSize;                     // 699.2
    // Perpendicular pitch tile + gap = 111 → vertical step 111 / cos 16° = 115.5; the first top centres the
    // three-band block (two 25-DIP corner slivers of ground and no more).
    const float MarqueeBandStep = 115.5f;
    const float MarqueeTop1 = -20f;
    const float MarqueeTop2 = MarqueeTop1 + MarqueeBandStep;               // 95.5
    const float MarqueeTop3 = MarqueeTop2 + MarqueeBandStep;               // 211

    // Seamless BECAUSE the travel is exactly one repetition and the translate runs INSIDE the rotated frame.
    static readonly Keyframe[] s_bandLeft = [new(0f, 0f, Easing.Linear), new(1f, -MarqueeRepeat, Easing.Linear)];
    static readonly Keyframe[] s_bandRight = [new(0f, -MarqueeRepeat, Easing.Linear), new(1f, 0f, Easing.Linear)];
    // None a multiple of another: 60 and 66 (same direction) realign only every 660 s — phase-locked bands read as
    // one rigid sheet sliding.
    const float MarqueeMsA = 60_000f, MarqueeMsB = 74_000f, MarqueeMsC = 66_000f;

    /// <summary>Marquee — three diagonal strips crossing the tile in alternating directions over the palette ground.
    /// Coverage at design size holds at both ends of the travel (0.2.9's derivation: the row spans d ∈ [−349.6,
    /// +1419.4] around the pivot, the square needs [−235.7, +204.0]).</summary>
    static Element MarqueeBands(string[] tiles, bool mini, bool badge, in Loops loops)
    {
        int per = mini ? MarqueeRun : MarqueeRun * 2;   // one repetition is the floor; two is what the moving band needs

        Element Band(float top, Action<NodeHandle>? sink, int seed)
        {
            var kids = new Element[per];
            // `i % MarqueeRun` BEFORE the tile wrap: position i and i + 8 must hold the SAME cover or the wrap jumps.
            for (int i = 0; i < per; i++) kids[i] = ArtCell(tiles[(seed + i % MarqueeRun) % tiles.Length], MarqueeTile, 4f, GridDecodePx);
            // OUTER: the band box + the rotation (pivot = its centre). INNER: the row + the TranslateX track only, so
            // the translate composes inside the rotated frame and the fold never overwrites a static offset.
            return new BoxEl
            {
                OffsetX = MarqueeLeft, OffsetY = top, Width = MarqueeBandWidth, Height = MarqueeTile, Shrink = 0f,
                HitTestVisible = false, Rotation = -16f, TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                Children = [new BoxEl { Direction = 0, Gap = MarqueeGap, Shrink = 0f, HitTestVisible = false, OnRealized = sink, Children = kids }],
            };
        }

        return CanvasOf(
            ClassicLikedLeaves.PaletteGround(At(tiles, 0), At(tiles, 1), At(tiles, 0), ClassicLikedGroundKind.Marquee),
            // Seeds 0 / 2 / 5: pairwise gaps 2 / 3 / 5 are all below MinTiles(Marquee) = 6, so no two bands ever start
            // on the same cover anywhere in the live tile range.
            CanvasOf(Band(MarqueeTop1, loops.A, 0), Band(MarqueeTop2, loops.B, 2), Band(MarqueeTop3, loops.C, 5)),
            badge ? HeartBadge(right: false, shadow: BadgeOnArtShadow) : Nothing);
    }

    // ── tone (W11) ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Tone — no art tiles: the graded multi-radial ground, the heart as frosted glass at 62 %, and the
    /// collection size. ENGINE ADAPTATION: PathEl has no shadow, so the drop-shadow under the heart is absent and the
    /// rim keeps its edge legible (§4.4 #7).</summary>
    static Element Tone(string[] tiles, int trackCount, bool mini)
    {
        const float heart = 0.62f * DesignSize;
        return CanvasOf(
            ClassicLikedLeaves.ToneGround(tiles),
            new BoxEl
            {
                AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center, ZStack = true,
                Width = heart, Height = heart, HitTestVisible = false,
                Children = [ClassicLikedHeart.Fill(heart, White with { A = 0.92f }), ClassicLikedHeart.Rim(heart, White with { A = 0.60f })],
            },
            // A FACT, so stated only when there is one — never in a miniature. CurrentCulture VERBATIM (0.2.9's exact
            // call; note the "G" format carries no group separator — ch 07 item 73's "1 166" claim is reported).
            !mini && trackCount > 0
                ? new TextEl(trackCount.ToString(CultureInfo.CurrentCulture))
                {
                    AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.End, Margin = new Edges4(0f, 0f, 14f, 12f),
                    Size = ClassicLikedRules.ToneCountSize, LineHeight = ClassicLikedRules.ToneCountLine, Weight = 600, Color = White with { A = 0.85f },
                }
                : Nothing);
    }

    // ── stack (W12) ─────────────────────────────────────────────────────────────────────────────────────────────

    // Rest poses verbatim; index 0 paints FIRST and carries the OLDEST of the five, so the newest like is on top.
    static readonly (float Rot, float Dx, float Dy)[] s_fanRest =
        [(-22f, -26f, 6f), (-11f, -12f, -2f), (0f, 0f, -6f), (11f, 12f, -2f), (22f, 26f, 6f)];
    // `.cover:hover` minus the rest pose — MotionTarget is a DELTA on the authored pose.
    static readonly (float Rot, float Dx, float Dy)[] s_fanHover =
        [(-5f, -6f, 2f), (-2f, -3f, -1f), (0f, 0f, -4f), (2f, 3f, -1f), (5f, 6f, 2f)];
    const float FanCover = 150f, FanLeft = 77f, FanTop = 66f;

    /// <summary>Stack — the last five likes fanned (origin 50 % / 120 %, so it opens like a hand of cards). The token
    /// carries the reduced-motion policy — no <c>if</c> (M-11).</summary>
    static Element StackFan(string[] tiles, bool badge)
    {
        var fan = new Element[s_fanRest.Length];
        for (int i = 0; i < fan.Length; i++)
        {
            string url = tiles[(fan.Length - 1 - i) % tiles.Length];
            var rest = s_fanRest[i];
            var hover = s_fanHover[i];
            fan[i] = new BoxEl
            {
                Width = FanCover, Height = FanCover, Shrink = 0f,
                OffsetX = FanLeft + rest.Dx, OffsetY = FanTop + rest.Dy, Rotation = rest.Rot,
                TransformOriginX = 0.5f, TransformOriginY = 1.2f,
                WhileHover = new MotionTarget { OffsetX = hover.Dx, OffsetY = hover.Dy, Rotation = hover.Rot },
                Transition = MotionTok.ControlNormal,
                HitTestVisible = false, Shadow = Elevation.Card, ClipToBounds = true, Corners = CornerRadius4.All(6f),
                BorderWidth = 1f, BorderColor = R.OnMedia.Stroke,
                Children = [ArtImage(url, HeroDecodePx, 6f)],
            };
        }
        return CanvasOf(
            ClassicLikedLeaves.PaletteGround(At(tiles, 0), At(tiles, 1), At(tiles, 0), ClassicLikedGroundKind.Stack),
            CanvasOf(fan),
            badge ? HeartBadge(right: true) : Nothing);
    }

    // ── loop wiring (called by LikedCoverArt's layout effect) ───────────────────────────────────────────────────

    public static bool HasLoops(LikedCoverStyle style) => style is LikedCoverStyle.Wall or LikedCoverStyle.Marquee;

    /// <summary>Seed the ambient loops a style owns onto their realized nodes. Durations, amplitudes and the
    /// reduced-motion VALUE live beside the geometry they belong to.</summary>
    public static void SeedLoops(LikedCoverStyle style, AnimEngine anim, NodeHandle a, NodeHandle b, NodeHandle c)
    {
        bool still = Design.Reduced;
        switch (style)
        {
            case LikedCoverStyle.Wall when !a.IsNull:
                anim.Keyframes(a, AnimChannel.TranslateY, still ? s_still : s_wallDrift, WallLoopMs, loop: true);
                break;
            case LikedCoverStyle.Marquee:
                if (!a.IsNull) anim.Keyframes(a, AnimChannel.TranslateX, still ? s_still : s_bandLeft, MarqueeMsA, loop: true);
                if (!b.IsNull) anim.Keyframes(b, AnimChannel.TranslateX, still ? s_still : s_bandRight, MarqueeMsB, loop: true);
                if (!c.IsNull) anim.Keyframes(c, AnimChannel.TranslateX, still ? s_still : s_bandLeft, MarqueeMsC, loop: true);
                break;
        }
    }
}
