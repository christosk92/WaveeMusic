using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The element trees behind the Liked Songs cover treatments — a static factory layer (<c>HomeFoldTile</c>'s),
/// deliberately NOT a component: no hooks, no state, nothing to remount. <see cref="LikedCoverArt"/> owns every hook
/// this feature has, and hands the ambient loops in as <see cref="Loops"/> sinks.
///
/// <para><b>ONE AUTHORING CANVAS.</b> Every number in this file is a DIP inside a <see cref="DesignSize"/>-square
/// canvas — the prototype's own 304 tile — and the whole composition is then dropped into a
/// <see cref="Scaled"/> wrapper at <c>size / 304</c>. That is exactly what the prototype does for its picker
/// miniatures (<c>.sw .thumb .cover { transform: scale(0.25) }</c>), and it buys three things the alternative
/// (re-deriving every proportion from <c>size</c>) does not:</para>
/// <list type="number">
/// <item>the looping <c>Keyframe[]</c>s can be <c>static readonly</c> — their amplitudes are canvas DIPs, so ONE array
/// is correct at every cover size, which is the engine's requirement (keyframes are stored by reference);</item>
/// <item>a rail-width drag re-scales a composited transform instead of re-laying-out 36 tiles;</item>
/// <item>a 76-DIP picker miniature is provably the SAME composition as the 304 cover behind the flyout, not a second
/// drawing of it that can drift.</item>
/// </list>
///
/// <para><b>The clip frame is OUTSIDE and UNROTATED</b> (Wall and Marquee both rotate their content): <c>ClipToBounds</c>
/// on or under a rotated node clips to the AXIS-ALIGNED bounding box, which would cut the corners off the cover. So the
/// frame rounds and clips, and rotation happens strictly inside it.</para>
///
/// <para><b>Ink on imagery is <c>WaveeOnMedia</c>, never a theme token</b> — a cover can be any luminance, and
/// <c>Tok.TextPrimary</c> over a pale sleeve in light theme is invisible (the rule <c>PlaylistInlineEdit.CoverOverlay</c>
/// states).</para></summary>
static class LikedCoverTreatments
{
    /// <summary>The authoring canvas edge — the prototype's cover tile.</summary>
    public const float DesignSize = 304f;

    /// <summary>Below this the "Liked Songs" chip's TEXT stops being text: at the 140-DIP vertical header the canvas
    /// scale is 0.46, which renders a 12.5 DIP label at under 6. The grid alone still reads as the collection, so the
    /// chip is dropped rather than painted as a smudge. The badge (a glyph in a circle, no type) survives further
    /// down, to <see cref="BadgeMinSize"/>.</summary>
    public const float ChromeMinSize = 180f;
    /// <summary>The floor for the heart badge — also the floor for treatments at all (<see cref="LikedCoverArt"/>
    /// collapses below it), so this is "any full treatment shows its badge".</summary>
    public const float BadgeMinSize = 140f;

    // Decode buckets, shared with the rest of the app so a treatment cell, the Home shelf card and the rail hero all
    // resolve to ONE cached texture per cover: 64 (wall cells), 128 (3x3 / 4x4 / marquee tiles — HomeFoldTile's
    // bucket), 256 (the Feature hero and the Stack fan — MediaCard's ShelfDecodePx, already warm from Home).
    const int WallDecodePx = 64, GridDecodePx = 128, HeroDecodePx = 256;

    /// <summary>The ambient-loop sinks a treatment may ask for — three, because Marquee's densified geometry runs three
    /// bands and each band's TranslateX track is keyed to its OWN row node. <see cref="LikedCoverArt"/> passes
    /// node-handle captures that its own <c>UseLayoutEffect</c> then drives; the picker's miniatures pass
    /// <c>default</c>, which is what makes a flyout of nine live thumbnails cost no timeline rows at all. A struct of
    /// three delegate FIELDS rather than a collection: the sinks are allocated once per component instance, so passing
    /// them costs a copy and never a heap object.</summary>
    public readonly record struct Loops(Action<NodeHandle>? A, Action<NodeHandle>? B, Action<NodeHandle>? C);

    /// <summary>Build a treatment. <paramref name="tileKey"/> is the caller's content key for
    /// <paramref name="tiles"/> (equality for the leaf components, which cannot compare a fresh array).
    /// <paramref name="style"/> must already be <c>LikedCoverRules.Effective</c> — this method composes, it does not
    /// decide; <see cref="LikedCoverStyle.Stock"/> (and an empty tile list, defensively) returns the bundled PNG
    /// through <see cref="LikedSongsArtwork.Cover"/> and is never re-implemented here.</summary>
    public static Element Build(LikedCoverStyle style, IReadOnlyList<string> tiles, string tileKey, int trackCount,
                                float size, float radius, bool mini, string? morphKey, in Loops loops = default)
    {
        if (style == LikedCoverStyle.Stock || tiles.Count == 0)
            return LikedSongsArtwork.Cover(size, radius, morphKey);

        bool chrome = !mini && size >= ChromeMinSize;
        bool badge = !mini && size >= BadgeMinSize;

        Element design = style switch
        {
            LikedCoverStyle.Lens => Lens(tiles, mini),
            LikedCoverStyle.Wall => Wall(tiles, mini, chrome, loops),
            LikedCoverStyle.Rainbow => Rainbow(tiles, tileKey, chrome),
            LikedCoverStyle.Marquee => Marquee(tiles, mini, badge, loops),
            LikedCoverStyle.Feature => Feature(tiles, chrome),
            LikedCoverStyle.Mosaic => Mosaic(tiles, chrome),
            LikedCoverStyle.Tone => Tone(tiles, trackCount, mini),
            LikedCoverStyle.Stack => Stack(tiles, badge),
            // Stock returned above; this arm exists only because a switch over an enum has to be total.
            _ => Mosaic(tiles, chrome),
        };

        return new BoxEl
        {
            ZStack = true, Width = size, Height = size, Shrink = 0f,
            ClipToBounds = true, Corners = CornerRadius4.All(radius),
            // The composed cover occupies the exact slot the stock PNG's MorphId held, so a Home-card -> rail Hero fly
            // still has a participant on both ends.
            MorphId = morphKey,
            // KEYED BY STYLE so a treatment swap REMOUNTS the canvas rather than being position-matched onto the
            // previous one. Without it the reconciler would reuse the outgoing style's nodes for the incoming style's
            // — which is fine for pixels but wrong for the ambient loops, whose slab rows are keyed to a node handle
            // that would then never re-realize (a Wall's drift row left ticking on what is now a Marquee band).
            Children = [Scaled(size, design) with { Key = "liked-style:" + style }],
        };
    }

    // ── the canvas ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The 304-canvas → cover-size wrapper. Top-left origin so the canvas and the frame share (0,0) and the
    /// scale is pure magnification, never a re-centring.</summary>
    static Element Scaled(float size, Element canvas) => new BoxEl
    {
        Width = DesignSize, Height = DesignSize, Shrink = 0f, ZStack = true, HitTestVisible = false,
        ScaleX = size / DesignSize, ScaleY = size / DesignSize,
        TransformOriginX = 0f, TransformOriginY = 0f,
        Children = [canvas],
    };

    /// <summary>A canvas-filling ZStack — the root every treatment returns.</summary>
    static BoxEl Canvas(params Element[] kids) => new()
    {
        ZStack = true, Width = DesignSize, Height = DesignSize, HitTestVisible = false, Children = kids,
    };

    // ── shared pieces ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One artwork cell. The image is COVER-fit inside a sized, clipping box (<c>Surfaces.Artwork</c>'s exact
    /// shape) because <c>ImageEl.DecodePx</c> is ignored the moment <c>Width</c> is explicit — the wrapper is what lets
    /// a cell hold its layout size while still decoding at a shared bucket. The placeholder is the cover's own graded
    /// tint, so a still-decoding cell is that record's colour and never a grey hole (E5).</summary>
    internal static Element Cell(string? url, float edge, float cornerRadius, int decodePx)
    {
        string? n = ImageSource.Normalize(url);
        return new BoxEl
        {
            Width = edge, Height = edge, Shrink = 0f, ClipToBounds = true,
            Corners = cornerRadius > 0f ? CornerRadius4.All(cornerRadius) : default,
            HitTestVisible = false,
            Children = [Image(n ?? "", ImageFit.Cover, 1f, decodePx, cornerRadius, Surfaces.PlaceholderFor(n))],
        };
    }

    /// <summary>An N-column uniform grid of already-built cells at an explicit extent (a <c>GridEl</c> inside a ZStack
    /// has no width to distribute otherwise).</summary>
    internal static Element Grid(int columns, Element[] cells, float cellEdge, float gap)
    {
        int rows = (cells.Length + columns - 1) / columns;
        return UniformGrid(columns, gap, cellEdge, cells) with
        {
            Width = columns * cellEdge + (columns - 1) * gap,
            Height = rows * cellEdge + (rows - 1) * gap,
        };
    }

    static Element TileGrid(IReadOnlyList<string> tiles, int columns, int cells, float cellEdge, float gap,
                            int decodePx, float cornerRadius = 0f)
    {
        var filled = LikedCoverRules.FillCells(tiles, cells);
        var kids = new Element[filled.Length];
        for (int i = 0; i < filled.Length; i++) kids[i] = Cell(filled[i], cellEdge, cornerRadius, decodePx);
        return Grid(columns, kids, cellEdge, gap);
    }

    /// <summary>The prototype's <c>.scrim</c>: nothing for the top 55 %, then down to black — so a chip or a title
    /// laid over the foot of a grid always has a ground.</summary>
    static Element Scrim(float bottomAlpha) => new BoxEl
    {
        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
        Gradient = GradientDown(
            new GradientStop(0f, ColorF.FromRgba(0, 0, 0) with { A = 0f }),
            new GradientStop(0.55f, ColorF.FromRgba(0, 0, 0) with { A = 0f }),
            new GradientStop(1f, ColorF.FromRgba(0, 0, 0) with { A = bottomAlpha })),
    };

    /// <summary>The prototype's <c>.chip-heart</c>: an on-media scrim capsule naming the collection, bottom-left.</summary>
    static Element NameChip() => new BoxEl
    {
        AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start,
        Margin = new Edges4(14f, 0f, 0f, 14f),
        Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f, Shrink = 0f,
        Height = 34f, Padding = new Edges4(10f, 0f, 12f, 0f),
        Corners = Radii.FullAll, Fill = WaveeOnMedia.ScrimRest,
        BorderWidth = 1f, BorderColor = WaveeOnMedia.Stroke, HitTestVisible = false,
        Children =
        [
            Icon(Icons.HeartFill, 16f, WaveeOnMedia.Ink),
            new TextEl(Loc.Get(Strings.Detail.LikedSongs))
            {
                Size = 12.5f, LineHeight = 16f, Weight = 600, Color = WaveeOnMedia.Ink,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        ],
    };

    /// <summary>The prototype's <c>.badge</c>: a solid white disc with the heart, in whichever bottom corner the
    /// treatment's motion leaves free. The glyph's near-black is a constant rather than a palette derivation — the
    /// badge sits on top of a palette GROUND, and tinting it from the same swatch is exactly how it would disappear
    /// into that ground on a dark-graded cover.</summary>
    static Element HeartBadge(bool right, ShadowSpec? shadow = null) => new BoxEl
    {
        AlignSelf = FlexAlign.End, JustifySelf = right ? FlexAlign.End : FlexAlign.Start,
        Margin = right ? new Edges4(0f, 0f, 14f, 14f) : new Edges4(14f, 0f, 0f, 14f),
        Width = 40f, Height = 40f, Shrink = 0f,
        Corners = Radii.FullAll, Fill = ColorF.FromRgba(255, 255, 255),
        Shadow = shadow ?? Elevation.Card, HitTestVisible = false,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [Icon(Icons.HeartFill, 20f, ColorF.FromRgba(0x1B, 0x1B, 0x20))],
    };

    /// <summary>The badge's cast when it sits on ART rather than on open ground. Deliberately NOT
    /// <c>Elevation.CardHover</c> or any other token: those are theme-split, and in light theme the card shadow is a
    /// 4-DIP 10 % whisper that a white disc on a bright sleeve simply loses. Artwork has no theme, so the shadow that
    /// separates a disc from it must not either — the same rule the file's header states for ink on imagery.</summary>
    static readonly ShadowSpec BadgeOnArtShadow =
        new(Blur: 14f, OffsetY: 3f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x59));

    // ── lens ──────────────────────────────────────────────────────────────────────────────────────────────────────

    // `.cv-lens .win-h { mask: <heart> center / 76% 76% }`. Those two numbers are the whole trick: the window is a
    // heart at 76 % of the tile, centred, and the mosaic INSIDE it is the FULL-canvas mosaic pushed back by exactly
    // the inset that centring leaves. Ground and window are then the same 304 composition at the same origin, so the
    // crisp copy lines up with the blurred one to the pixel and the heart reads as a hole cut in the blur — not as a
    // second, smaller picture of the same records.
    //
    // The 76 % is the prototype's number transferred LITERALLY, exactly as Tone transfers its 62 %. Worth knowing
    // when tuning it: the app's own heart (LikedHeart) inks 16 of its 24 view-box units, where the prototype's
    // Material heart inks ~20 — so the same percentage yields a window about a seventh smaller than the HTML's. That
    // is a consequence of choosing the heart the save button three inches below uses, not an oversight; the knob is
    // this constant.
    const float LensHeartEdge = 0.76f * DesignSize;              // 231.04
    const float LensInset = (DesignSize - LensHeartEdge) / 2f;   // 36.48 — MUST equal the centring inset above

    // `.cv-lens .ground { filter: blur(22px) saturate(1.25) brightness(.62) }`.
    const float LensGroundSigma = 22f;
    const float LensGroundSaturation = 1.25f;
    // brightness(.62) EXACTLY rather than approximately: ColorOverlay is a source-over composite inside the image
    // shader, and source-over black at alpha 0.38 leaves 0.62 * src. The renderer has no multiply blend; against
    // black it does not need one.
    static readonly ColorF LensGroundDim = ColorF.FromRgba(0, 0, 0) with { A = 0.38f };
    // `.cv-lens .win-h .mosaic { filter: saturate(1.12) contrast(1.04) }` — the saturation transfers to the image
    // shader; there is no contrast control there, and 1.04 is not worth an offscreen layer, so it is dropped.
    const float LensWindowSaturation = 1.12f;

    // `.cv-lens .rim path { stroke-width:.35 }`, authored in the SAME 24-unit view box the heart is and drawn on a
    // node that is the SAME 231.04 square the window is — so the prototype's number transfers literally.
    const float LensRimWidth = 0.35f;

    /// <summary>Lens — the heart as a WINDOW: the nine newest likes blurred, dimmed and palette-veiled as the ground,
    /// the identical mosaic crisp inside a heart-shaped clip, a thin rim over the boundary and the prototype's sheen
    /// closing it. The default treatment, and the one the whole feature was prototyped around.
    ///
    /// <para>The clip is the engine's tier-3 stencil path clip (<c>BoxEl.ClipPath</c>), which is a HARD EDGE by
    /// design — the mask keeps or discards a pixel, it does not feather one — so the silhouette carries no
    /// anti-aliasing of its own. The rim stroke is therefore not decoration ON that edge, it is what DRESSES it, and
    /// that is why it is a SIBLING ABOVE the window rather than a child of it: a stroke inside the clip would be cut
    /// in half by the very boundary it exists to cover.</para>
    ///
    /// <para>The ZStack order below is E7's requirement, not an accident. A <c>BakedBlur</c> derivative is
    /// asynchronous — the sharp tile paints first and the blur lands a beat later — so the ground must sit UNDER the
    /// crisp window, where that arrival is a fade behind a fixed shape. The other order would flash the whole cover
    /// sharp and then smear it, which is the wrong way round and reads as a bug.</para></summary>
    static Element Lens(IReadOnlyList<string> tiles, bool mini)
    {
        const float cell = DesignSize / 3f;
        var filled = LikedCoverRules.FillCells(tiles, 9);

        return Canvas(
            // A miniature skips the BAKE and keeps the DIM: nine derived images per thumbnail buys a blur that is four
            // pixels wide at a 0.25 canvas scale, and the flyout's whole cost story is that it adds no residency the
            // cover behind it was not already paying. Dimmed-but-sharp still reads as ground-under-window.
            LensMosaic(filled, cell, dim: true, blur: !mini),
            LikedCoverLeaves.LensVeil(At(tiles, 0), At(tiles, 1)),
            new BoxEl
            {
                AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
                Width = LensHeartEdge, Height = LensHeartEdge, Shrink = 0f, ZStack = true, HitTestVisible = false,
                ClipToBounds = true,
                ClipPath = LikedHeart.Data, ClipPathRule = FillRule.NonZero,
                ClipPathViewBoxW = LikedHeart.ViewBox, ClipPathViewBoxH = LikedHeart.ViewBox,
                Children =
                [
                    // The positioner is its own node (Wall's and Marquee's split, for a plainer reason): the clip node
                    // owns the silhouette and must stay at the centred rect the geometry is fitted into, so the
                    // canvas-restoring translate belongs one level down.
                    new BoxEl
                    {
                        OffsetX = -LensInset, OffsetY = -LensInset,
                        Width = DesignSize, Height = DesignSize, Shrink = 0f, HitTestVisible = false,
                        Children = [LensMosaic(filled, cell, dim: false, blur: false)],
                    },
                ],
            },
            new BoxEl
            {
                AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
                Width = LensHeartEdge, Height = LensHeartEdge, Shrink = 0f, HitTestVisible = false,
                Children =
                [
                    LikedHeart.Rim(LensHeartEdge, ColorF.FromRgba(255, 255, 255) with { A = 0.55f }, LensRimWidth),
                ],
            },
            LensSheen());
    }

    /// <summary>Lens's 3x3. The same nine cells at the same geometry in BOTH copies — they differ only in the
    /// per-image shader state each tile carries — so ground and window cannot drift apart.</summary>
    static Element LensMosaic(string[] filled, float cell, bool dim, bool blur)
    {
        var kids = new Element[filled.Length];
        for (int i = 0; i < filled.Length; i++) kids[i] = LensCell(filled[i], cell, dim, blur);
        return Grid(3, kids, cell, 0f);
    }

    /// <summary>One Lens tile. Deliberately not <see cref="Cell"/>: the two copies differ only in per-image shader
    /// state, and threading four more parameters through the shared cell would put Lens's vocabulary into every other
    /// treatment's call site.
    ///
    /// <para>ENGINE ADAPTATION, stated rather than hidden: the prototype blurs the ground as ONE layer, so its tiles
    /// bleed into each other. <c>BakedBlur</c> is a per-image derivative, so each tile is blurred within its own
    /// bounds and the cell boundaries stay faintly legible as colour transitions. That is the trade for a PERSISTENT
    /// derived image — a scene blur layer over the whole mosaic would re-blur nine textures every frame the cover is
    /// visible, on a page that also scrolls a 10k list.</para>
    ///
    /// <para>The sigma is a CANVAS DIP like every other number in this file, and the canvas is a composited scale — so
    /// ONE bake at ONE sigma is correct at every cover size, exactly as the static keyframe arrays are.</para></summary>
    static Element LensCell(string? url, float edge, bool dim, bool blur)
    {
        string? n = ImageSource.Normalize(url);
        var img = Image(n ?? "", ImageFit.Cover, 1f, GridDecodePx, 0f, Surfaces.PlaceholderFor(n)) with
        {
            Saturation = dim ? LensGroundSaturation : LensWindowSaturation,
            ColorOverlay = dim ? LensGroundDim : ColorF.Transparent,
            BakedBlur = blur ? new BakedBlurSpec(LensGroundSigma) : null,
        };
        return new BoxEl
        {
            Width = edge, Height = edge, Shrink = 0f, ClipToBounds = true, HitTestVisible = false,
            Children = [img],
        };
    }

    /// <summary>The prototype's <c>.sheen</c>: a white lift across the top, nothing through the middle, a black seat
    /// at the foot. It is the last layer, over the rim, because it is the light on the whole object.</summary>
    static Element LensSheen() => new BoxEl
    {
        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
        Gradient = GradientDown(
            new GradientStop(0f, ColorF.FromRgba(255, 255, 255) with { A = 0.10f }),
            new GradientStop(0.45f, ColorF.FromRgba(255, 255, 255) with { A = 0f }),
            new GradientStop(1f, ColorF.FromRgba(0, 0, 0) with { A = 0.18f })),
    };

    // ── flat grids ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Mosaic — the nine newest likes, edge to edge (the prototype's <c>.mosaic</c> has no gap), a bottom
    /// scrim and the name chip. The Apple Music / YouTube Music default, and the least ambiguous of the nine.</summary>
    static Element Mosaic(IReadOnlyList<string> tiles, bool chrome)
    {
        const float cell = DesignSize / 3f;
        var kids = new List<Element>(3) { TileGrid(tiles, 3, 9, cell, 0f, GridDecodePx), Scrim(0.55f) };
        if (chrome) kids.Add(NameChip());
        return Canvas(kids.ToArray());
    }

    /// <summary>Feature — the newest like at 2x2 with the six before it following (the prototype's
    /// <c>grid-area: 1/1/3/3</c>). Composed as explicit rows rather than a grid span: the hero cell needs the 256
    /// bucket while the followers stay at 128, and one grid of uniform cells cannot say that.</summary>
    static Element Feature(IReadOnlyList<string> tiles, bool chrome)
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
                Cell(filled[0], hero, 0f, HeroDecodePx),
                new BoxEl
                {
                    Direction = 1, Gap = gap, Shrink = 0f, HitTestVisible = false,
                    Children = [Cell(filled[1], cell, 0f, GridDecodePx), Cell(filled[2], cell, 0f, GridDecodePx)],
                },
            ],
        };
        Element bottom = new BoxEl
        {
            Direction = 0, Gap = gap, Shrink = 0f, HitTestVisible = false,
            Children =
            [
                Cell(filled[3], cell, 0f, GridDecodePx), Cell(filled[4], cell, 0f, GridDecodePx),
                Cell(filled[5], cell, 0f, GridDecodePx),
            ],
        };

        var kids = new List<Element>(3)
        {
            new BoxEl
            {
                Direction = 1, Gap = gap, Width = DesignSize, Height = DesignSize, HitTestVisible = false,
                Children = [top, bottom],
            },
            Scrim(0.5f),
        };
        if (chrome) kids.Add(NameChip());
        return Canvas(kids.ToArray());
    }

    /// <summary>Rainbow — sixteen covers sorted by dominant hue into a serpentine sweep. The GRID is a leaf
    /// (<see cref="LikedRainbowGrid"/>) because the order is a function of the gradings, which arrive after the first
    /// paint; everything else here is the plate under it and the chip over it.</summary>
    static Element Rainbow(IReadOnlyList<string> tiles, string tileKey, bool chrome)
    {
        const float gap = 2f;
        const float cell = (DesignSize - 3f * gap) / LikedCoverRules.RainbowColumns;   // 74.5
        var kids = new List<Element>(3)
        {
            new BoxEl { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false, Fill = PlateDark },
            LikedCoverLeaves.RainbowGrid(tileKey, LikedCoverRules.FillCells(tiles, LikedCoverRules.MaxTiles),
                                         cell, gap, GridDecodePx),
        };
        if (chrome) kids.Add(NameChip());
        return Canvas(kids.ToArray());
    }

    /// <summary>The prototype's <c>#131318</c> — the plate the two full-bleed grids sit on, so a gap or a
    /// still-decoding row never shows the page through the cover.</summary>
    static readonly ColorF PlateDark = ColorF.FromRgba(0x13, 0x13, 0x18);

    // ── motion treatments ─────────────────────────────────────────────────────────────────────────────────────────

    // Wall's grid rectangle, verbatim from `.cv-wall .wallgrid { left:-34%; top:-42%; width:170% }`.
    const float WallLeft = -0.34f * DesignSize, WallTop = -0.42f * DesignSize, WallSpan = 1.70f * DesignSize;
    const float WallGap = 7f;
    const float WallDriftDip = -46f;   // `@keyframes wallDrift { to { translateY(-46px) } }`
    const float WallLoopMs = 92_000f;  // the CSS 46s alternate, unrolled into one 92s there-and-back loop

    static readonly Keyframe[] s_wallDrift = [new(0f, 0f), new(0.5f, WallDriftDip), new(1f, 0f)];

    /// <summary>Reduced motion as a VALUE, not a branch (the canon rule): the loop is still declared, still owns its
    /// slab row, still quiesces under a parked page — its amplitude is simply zero. Nothing about the authored tree
    /// changes when the OS setting flips, which is the property a structural <c>if</c> would destroy.</summary>
    static readonly Keyframe[] s_still = [new(0f, 0f), new(1f, 0f)];

    /// <summary>Wall — the collection as a tilted record wall under a very slow drift.
    ///
    /// <para>ENGINE ADAPTATION: the prototype tilts with <c>perspective(760px) rotateX(16deg)</c>. The renderer's
    /// transform block is 2D affine (offset / scale / rotation) with no perspective, so the X tilt is DROPPED and the
    /// -11 degree roll plus the drift carry the read. The alternative — faking perspective by scaling each row — would
    /// make the grid a hand-tuned trapezoid that no longer survives a resize.</para></summary>
    static Element Wall(IReadOnlyList<string> tiles, bool mini, bool chrome, in Loops loops)
    {
        // A miniature caps at 4x4 rather than 6x6: sixteen cells at a 0.25 canvas scale is already past the point
        // where another row adds information, and nine thumbnails' worth of cells is the flyout's whole cost story.
        int columns = mini ? 4 : 6;
        int cells = columns * columns;
        float cell = (WallSpan - (columns - 1) * WallGap) / columns;

        var kids = new Element[cells];
        for (int i = 0; i < cells; i++)
            kids[i] = Cell(tiles[LikedCoverRules.WallCellIndex(i, tiles.Count)], cell, 3f, WallDecodePx);

        // TWO nodes, and the split is required, not tidiness: the drift writes AnimChannel.TranslateY, and the anim
        // fold reseeds a node's translate from its composited transform — so a static OffsetY on the SAME node would be
        // overwritten the first time the track ticks. The positioner owns the static placement; the inner node owns the
        // roll (a different channel, untouched by the fold) and the animated translate.
        Element drifting = new BoxEl
        {
            Direction = 1, Shrink = 0f, HitTestVisible = false,
            Rotation = -11f, TransformOriginX = 0.5f, TransformOriginY = 0.5f,
            OnRealized = loops.A,
            Children = [Grid(columns, kids, cell, WallGap)],
        };

        return Canvas(
            new BoxEl { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false, Fill = PlateDark },
            new BoxEl
            {
                OffsetX = WallLeft, OffsetY = WallTop, Width = WallSpan, Shrink = 0f, HitTestVisible = false,
                Children = [drifting],
            },
            // The prototype's two-layer vignette: a radial that darkens away from (50 %, 42 %), then a vertical pass
            // that seats the top and foot. Both are four stops or fewer (the recorder's cap).
            new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                Gradient = new GradientSpec(GradientShape.Radial, 0f,
                [
                    new GradientStop(0.46f, VignetteInk with { A = 0f }),
                    new GradientStop(1f, VignetteInk with { A = 0.62f }),
                ])
                { RadialCenter = new Point2(0.5f, 0.42f), RadialRadius = new Point2(1.2f, 1.2f) },
            },
            new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                Gradient = GradientDown(
                    new GradientStop(0f, VignetteInk with { A = 0.28f }),
                    new GradientStop(0.32f, VignetteInk with { A = 0f }),
                    new GradientStop(0.62f, VignetteInk with { A = 0f }),
                    new GradientStop(1f, VignetteInk with { A = 0.50f })),
            },
            chrome ? NameChip() : Nothing);
    }

    static readonly ColorF VignetteInk = ColorF.FromRgba(0x0A, 0x0A, 0x0E);

    // ── marquee ────────────────────────────────────────────────────────────────────────────────────────────────────

    // The prototype's `.band img { width:92px; gap:7px }` at 92 left the palette ground reading as two lonely stripes
    // over a mostly empty square. The treatment is meant to be the collection CROSSING the tile, with the ground as the
    // seam between bands — so the tile grew to 104 and a THIRD band was added. Both moves at once, because either alone
    // misses: bigger tiles in two bands only thickens two stripes, and a third band of 92s tiles the square with a
    // pinstripe pattern that reads as texture rather than as records.
    const float MarqueeTile = 104f, MarqueeGap = 7f;
    const int MarqueeRun = 8;
    const float MarqueeRepeat = MarqueeRun * (MarqueeTile + MarqueeGap);   // 888
    const float MarqueeLeft = -0.60f * DesignSize;                         // `.band { left:-60% }`  = -182.4
    // `.band { width:230% }`. The width is NOT there to size the strip — the tiles are `flex:none` and overflow it by
    // design — it is there to place the PIVOT: `transform-origin` defaults to the band box's own centre, so the CSS
    // rotates about (left + 230%/2, top + tile/2) = (167.2, top + 52), which is 55 % across the tile. Reproducing that
    // box verbatim is what makes the rotation land where the prototype's does.
    const float MarqueeBandWidth = 2.30f * DesignSize;                     // 699.2

    // BAND PLACEMENT, derived rather than eyeballed — this is the number that decides whether the cover reads as a
    // crossing or as stripes. The bands are parallel -16 degree strips, so what has to be uniform is their
    // PERPENDICULAR pitch, not their vertical one. With n = (sin 16, cos 16) = (0.2756, 0.9613) the unit normal, a
    // vertical step of dTop moves a band 0.9613 * dTop perpendicular; asking for tile + gap = 111 of perpendicular
    // pitch (touching strips, one 7-DIP ground seam between them, no overlap) gives dTop = 111 / 0.9613 = 115.5.
    //
    // The first top then centres the three-band block on the square. Perpendicular coordinate s(p) = n . p runs
    // s = 0 at (0,0) to s = 376.0 at (304,304); three 104 strips plus two 7 seams span 326, leaving 50.0 to split as
    // two equal corner slivers of 25.0 — which is exactly the ground the prototype shows at the top-left and the
    // bottom-right, and no more. Solving s(band 1 centre) = 25.0 + 52 gives top = -19.8, rounded to -20.
    const float MarqueeBandStep = 115.5f;
    const float MarqueeTop1 = -20f;
    const float MarqueeTop2 = MarqueeTop1 + MarqueeBandStep;               // 95.5
    const float MarqueeTop3 = MarqueeTop2 + MarqueeBandStep;               // 211

    // Seamless BECAUSE the travel is exactly one repetition: at the loop's wrap the strip is pixel-identical to its
    // start, so there is no seam to hide and no need for the CSS's fractional -33.4 % of a 230 %-wide band. That
    // identity only holds now that the translate runs INSIDE the rotated frame (see Band): a translate applied in the
    // band's parent space is a horizontal slide across a -16 degree strip, which walks the band off its own axis and
    // leaves the wrap a visible jump. It ALSO requires the band's CONTENT to repeat with period MarqueeRun, which is
    // why the tile index below wraps at MarqueeRun before it wraps at tiles.Count (see Band).
    static readonly Keyframe[] s_bandLeft = [new(0f, 0f, Easing.Linear), new(1f, -MarqueeRepeat, Easing.Linear)];
    static readonly Keyframe[] s_bandRight = [new(0f, -MarqueeRepeat, Easing.Linear), new(1f, 0f, Easing.Linear)];
    // Three durations, none a multiple of another, so the three bands never settle into a fixed relative phase: 60 and
    // 66 (the two that travel the same way) realign only every 660 s, and 74 shares no factor with either beyond 2.
    // Phase-locked bands would read as one rigid sheet sliding, which is the failure this treatment exists to avoid.
    const float MarqueeMsA = 60_000f, MarqueeMsB = 74_000f, MarqueeMsC = 66_000f;

    /// <summary>Marquee — three diagonal strips of the collection crossing the tile in alternating directions
    /// (left / right / left) over the palette ground. The Zune diagonal the app's cards already speak.</summary>
    static Element Marquee(IReadOnlyList<string> tiles, bool mini, bool badge, in Loops loops)
    {
        // Two repetitions live; a miniature shows ONE — and one is the floor, not a taste call. The band is placed from
        // the pivot box's left edge, so a run shorter than a repetition stops before the tile's far corner and the band
        // would peter out inside frame (see the coverage note in Band). A miniature is static, so one repetition is also
        // all it can ever need. Two is what the moving bands need and no more: 3 x 16 = 48 cells is the same cell count
        // the two-band, three-repetition shape cost, so a denser cover is not a more expensive one.
        int per = mini ? MarqueeRun : MarqueeRun * 2;

        Element Band(float top, Action<NodeHandle>? sink, int seed)
        {
            var kids = new Element[per];
            for (int i = 0; i < per; i++)
                // `i % MarqueeRun` BEFORE the tiles wrap: the loop translates by exactly MarqueeRun pitches, so the
                // strip is only pixel-identical at the wrap if position i and position i + MarqueeRun hold the SAME
                // cover. Indexing straight into a 16-long tile list would give the row a period of 16 against a travel
                // of 8 and put a visible content jump on every loop.
                kids[i] = Cell(tiles[(seed + i % MarqueeRun) % tiles.Count], MarqueeTile, 4f, GridDecodePx);
            // TWO nodes, and which one carries WHICH transform is the whole of this treatment's correctness — it is
            // CSS's `transform: rotate(-16deg) translateX(...)` read right to left.
            //
            // OUTER owns the band BOX and the rotation: the 230 %-wide, 104-tall rect at (-182.4, top), pivoting on its
            // own centre. INNER is the tile row, and owns nothing but the TranslateX track — so the animated translate
            // is composed INSIDE the rotated frame and the band slides along its own -16 degree axis. (The previous
            // shape put the rotation on the row itself. A row of tiles is thousands wide, so `origin 50 %` pivoted it
            // ~1000 DIP to the RIGHT of the tile and swung the visible run clean out of frame; and the track, applied
            // in the unrotated parent, slid it horizontally rather than along the strip.)
            //
            // The split is also what keeps the anim fold honest, exactly as Wall's does: the fold reseeds a node's
            // translate from its composited transform, so a static OffsetX on the node the TranslateX track drives
            // would be overwritten on the first tick. Here the static placement is the OUTER node's and the track's
            // node has no authored offset at all.
            //
            // ── COVERAGE, at design size, for all THREE bands and at BOTH ends of the travel ──
            // u = (cos -16, sin -16) = (0.9613, -0.2756) is the band axis; the pivot is P = (167.2, top + 52); d is the
            // coordinate along u measured from P. The row is 16 * 111 - 7 = 1769 wide and starts at the box's left
            // edge, which is 349.6 in front of the pivot, so the row spans d in [-349.6, +1419.4] at rest and the
            // TranslateX track shifts that whole interval by T in [-888, 0].
            //
            // What the row must cover is the projection of the 304 square's four corners onto u, per band —
            // d(corner) = 0.9613 * (x - 167.2) - 0.2756 * (y - P.y):
            //   band 1 (top -20,   P.y  32.0):  corners -151.9 / +140.3 / -235.7 / +56.5  =>  d in [-235.7, +140.3]
            //   band 2 (top 95.5,  P.y 147.5):  corners -120.1 / +172.2 / -203.9 / +88.4  =>  d in [-203.9, +172.2]
            //   band 3 (top 211,   P.y 263.0):  corners  -88.2 / +204.0 / -172.0 / +120.2 =>  d in [-172.0, +204.0]
            // so the union the row must span is d in [-235.7, +204.0].
            //   worst LEFT edge  (T = 0):     -349.6 <= -235.7, margin 113.9 — one whole tile spare.
            //   worst RIGHT edge (T = -888):  -349.6 + 1769 - 888 = +531.4 >= +204.0, margin 327.4 — three tiles spare.
            // Both hold for either direction, since s_bandLeft and s_bandRight traverse the SAME T interval and differ
            // only in which end they start at. MarqueeLeft therefore needs no adjustment for the denser geometry, and
            // a static miniature (one repetition, W = 881, T = 0) spans [-349.6, +531.4] and satisfies both bounds too.
            //
            // PERPENDICULAR (the read the eye actually gets): with n = (0.2756, 0.9613), band k occupies
            // s in [s_k - 52, s_k + 52] where s_k = 46.09 + 0.9613 * (top + 52) — that is [24.9, 128.9], [135.9, 239.9]
            // and [246.9, 350.9]. The seams between them are 7.0 and 7.0 (the authored ground gap, no overlap), and the
            // square's own s runs [0, 376.0] — so the ONLY ground left is a 24.9 sliver at the top-left corner and a
            // 25.1 sliver at the bottom-right. Read vertically: at the tile's left edge the three bands cover
            // y in [25.9, 134.1], [141.4, 249.6], [256.9, 365.1]; at its right edge [-61.3, 46.9], [54.2, 162.4],
            // [169.7, 277.9]. Both corners on the other diagonal are inside art, which is the crossing this treatment
            // is for.
            return new BoxEl
            {
                OffsetX = MarqueeLeft, OffsetY = top,
                Width = MarqueeBandWidth, Height = MarqueeTile, Shrink = 0f, HitTestVisible = false,
                Rotation = -16f, TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Gap = MarqueeGap, Shrink = 0f, HitTestVisible = false,
                        OnRealized = sink, Children = kids,
                    },
                ],
            };
        }

        return Canvas(
            LikedCoverLeaves.PaletteGround(At(tiles, 0), At(tiles, 1), At(tiles, 0), LikedGroundKind.Marquee),
            // Seeds 0 / 2 / 5, and the SMALL strides are the point rather than a compromise. Two bands start on the
            // same cover exactly when the tile count divides their seed difference, and Marquee's live range of counts
            // is [MinTiles(Marquee) = 6, MaxTiles = 16] — so the safe differences are the ones below 6, and 2 / 3 / 5
            // (the three pairwise gaps here) are all of them at once. A wider, more "spread" stride like 0 / 5 / 10
            // reads better on a 16-tile library and then collapses bands 1 and 3 onto the same cover at exactly 10.
            Canvas(Band(MarqueeTop1, loops.A, 0), Band(MarqueeTop2, loops.B, 2), Band(MarqueeTop3, loops.C, 5)),
            // The badge now lands on band-3 ART rather than on the old empty bottom-left ground, so it carries its own
            // media shadow instead of the card token: a fixed, theme-independent cast, for the same reason the ink over
            // imagery is WaveeOnMedia and never Tok — the thing behind it is a cover, not a surface.
            badge ? HeartBadge(right: false, shadow: BadgeOnArtShadow) : Nothing);
    }

    /// <summary>Tone — no art tiles at all: a multi-radial gradient graded from the newest likes, the heart as frosted
    /// glass, and the collection size. The ground is a leaf (<see cref="LikedToneGround"/>); the heart is real vector
    /// geometry (<see cref="LikedHeart"/>) rather than a glyph, because at 62 % of the cover a font atlas entry would
    /// have to be rasterized at ~190 DIP for one shape.</summary>
    static Element Tone(IReadOnlyList<string> tiles, int trackCount, bool mini)
    {
        const float heart = 0.62f * DesignSize;
        var kids = new List<Element>(4)
        {
            LikedCoverLeaves.ToneGround(tiles),
            new BoxEl
            {
                AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center, ZStack = true,
                Width = heart, Height = heart, HitTestVisible = false,
                Children =
                [
                    // ENGINE ADAPTATION: PathEl carries no Shadow (that is a BoxEl decoration), so the prototype's
                    // `drop-shadow(0 10px 24px …)` under the glass heart is absent. The rim below is what keeps the
                    // shape's edge legible where the gradient behind it happens to be pale.
                    LikedHeart.Fill(heart, ColorF.FromRgba(255, 255, 255) with { A = 0.92f }),
                    LikedHeart.Rim(heart, ColorF.FromRgba(255, 255, 255) with { A = 0.60f }),
                ],
            },
        };
        // The count is a FACT, so it is only stated when there is one — and never in a miniature, where a four-digit
        // run at a quarter scale is two pixels of noise.
        if (!mini && trackCount > 0)
            kids.Add(new TextEl(trackCount.ToString(System.Globalization.CultureInfo.CurrentCulture))
            {
                AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.End,
                Margin = new Edges4(0f, 0f, 14f, 12f),
                Size = 12f, LineHeight = 16f, Weight = 600,
                Color = ColorF.FromRgba(255, 255, 255) with { A = 0.85f },
            });
        return Canvas(kids.ToArray());
    }

    // Stack's rest poses, verbatim from `.cv-stack .fan img:nth-child(n)`. Index 0 paints FIRST (furthest back) and
    // carries the OLDEST of the five, so the newest like ends up on top of the fan.
    static readonly (float Rot, float Dx, float Dy)[] s_fanRest =
    [
        (-22f, -26f, 6f), (-11f, -12f, -2f), (0f, 0f, -6f), (11f, 12f, -2f), (22f, 26f, 6f),
    ];
    // `.cover:hover` poses minus the rest poses — the engine's MotionTarget contract is DELTAS on the authored pose.
    static readonly (float Rot, float Dx, float Dy)[] s_fanHover =
    [
        (-5f, -6f, 2f), (-2f, -3f, -1f), (0f, 0f, -4f), (2f, 3f, -1f), (5f, 6f, 2f),
    ];
    const float FanCover = 150f, FanLeft = 77f, FanTop = 66f;

    /// <summary>Stack — the last five likes fanned in the hub-card language Home already uses. The pose model is
    /// <c>HomeFoldTile.Create</c>'s verbatim, including its reduced-motion story: <c>MotionTok.ControlNormal</c>
    /// already carries the policy, so there is no <c>if</c> here either.</summary>
    static Element Stack(IReadOnlyList<string> tiles, bool badge)
    {
        var fan = new List<Element>(5);
        for (int i = 0; i < s_fanRest.Length; i++)
        {
            // Oldest first: slot i shows tiles[4 - i] so tiles[0] (the newest like) paints last and sits on top.
            string url = tiles[(s_fanRest.Length - 1 - i) % tiles.Count];
            var rest = s_fanRest[i];
            var hover = s_fanHover[i];
            fan.Add(new BoxEl
            {
                Width = FanCover, Height = FanCover, Shrink = 0f,
                OffsetX = FanLeft + rest.Dx, OffsetY = FanTop + rest.Dy, Rotation = rest.Rot,
                // `transform-origin: 50% 120%` — the fan pivots below the cards, which is what makes it open like a
                // hand of cards rather than a pinwheel.
                TransformOriginX = 0.5f, TransformOriginY = 1.2f,
                WhileHover = new MotionTarget { OffsetX = hover.Dx, OffsetY = hover.Dy, Rotation = hover.Rot },
                Transition = MotionTok.ControlNormal,
                HitTestVisible = false,
                Shadow = Elevation.Card, ClipToBounds = true, Corners = CornerRadius4.All(6f),
                BorderWidth = 1f, BorderColor = WaveeOnMedia.Stroke,
                Children = [Image(ImageSource.Normalize(url) ?? "", ImageFit.Cover, 1f, HeroDecodePx, 6f,
                                  Surfaces.PlaceholderFor(ImageSource.Normalize(url)))],
            });
        }

        return Canvas(
            LikedCoverLeaves.PaletteGround(At(tiles, 0), At(tiles, 1), At(tiles, 0), LikedGroundKind.Stack),
            Canvas(fan.ToArray()),
            badge ? HeartBadge(right: true) : Nothing);
    }

    // ── loop wiring (called by LikedCoverArt's layout effect, never from here) ─────────────────────────────────────

    /// <summary>Seed the ambient loops a style owns onto their realized nodes. Centralised here so the amplitudes,
    /// durations and the reduced-motion value choice live beside the geometry they belong to; the component supplies
    /// the scheduler, the scene (for the liveness check) and the realized handles.</summary>
    public static void SeedLoops(LikedCoverStyle style, AnimEngine anim, NodeHandle a, NodeHandle b, NodeHandle c)
    {
        bool still = Motion.ReducedMotion;
        switch (style)
        {
            case LikedCoverStyle.Wall when !a.IsNull:
                anim.Keyframes(a, AnimChannel.TranslateY, still ? s_still : s_wallDrift, WallLoopMs, loop: true);
                break;
            case LikedCoverStyle.Marquee:
                // Alternating directions, three distinct durations. Reduced motion stays a VALUE here (s_still), so all
                // three rows keep their slab rows and simply carry zero amplitude rather than leaving the tree.
                if (!a.IsNull) anim.Keyframes(a, AnimChannel.TranslateX, still ? s_still : s_bandLeft, MarqueeMsA, loop: true);
                if (!b.IsNull) anim.Keyframes(b, AnimChannel.TranslateX, still ? s_still : s_bandRight, MarqueeMsB, loop: true);
                if (!c.IsNull) anim.Keyframes(c, AnimChannel.TranslateX, still ? s_still : s_bandLeft, MarqueeMsC, loop: true);
                break;
        }
    }

    /// <summary>True when <paramref name="style"/> owns ambient loops at all — the component skips its handle captures
    /// and its scheduler poke entirely for the seven that do not.</summary>
    public static bool HasLoops(LikedCoverStyle style)
        => style is LikedCoverStyle.Wall or LikedCoverStyle.Marquee;

    // ── tiny helpers ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A zero-extent, hit-transparent placeholder, so a conditional chip/badge is an EMPTY child rather than a
    /// varying child COUNT — the ZStack's other children keep their positions across a size change.</summary>
    static readonly Element Nothing = new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };

    static string? At(IReadOnlyList<string> tiles, int i) => tiles.Count == 0 ? null : tiles[i % tiles.Count];
}
