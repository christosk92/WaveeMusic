// ── Entities/User.Cover.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the nine cover treatments, the four palette leaves, the heart geometry, the cover component, the picker + flyout
//
// Role: UI
// Owner: O
// Wave: 5
// Budget: 1700 lines
// Spec: ch 07 §0 items 1-6, 11, 15; W5-W15; §3-§6 cover rows; §9 cover notes. Ported from 0.2.9
//       `Features/Detail/LikedCoverTreatments.cs`, `LikedCoverLeaves.cs`, `LikedHeart.cs`, `LikedCoverArt.cs`,
//       `LikedCoverPicker.cs`, `Components/LikedSongsArtwork.cs`. Contract §2.3 (LikedCover / LikedArtwork /
//       LikedChipArt / LikedToneAnchorUrl).
//
// ONE AUTHORING CANVAS. Every number in §3 is a DIP inside a 304-square canvas — the prototype's own tile — dropped into
// a `Scaled` wrapper at `size / 304`. The looping keyframe arrays can therefore be `static readonly` (the engine stores
// keyframes BY REFERENCE, so one array must be right at every size), a rail drag re-scales one composited transform
// instead of re-laying-out 36 tiles, and a 76-DIP picker miniature is provably the SAME composition as the cover behind
// the flyout.
//
// THE CLIP FRAME IS OUTSIDE AND UNROTATED (Wall and Marquee rotate their content): ClipToBounds on or under a rotated
// node clips to the axis-aligned box, which would cut the cover's corners.
//
// INK ON IMAGERY IS `Design.OnMedia`, never a theme token — a Rainbow cover is every luminance at once.
//
// THE LEAF DISCIPLINE (§4). A grading landing for one of sixteen covers repaints ONE node: only the four leaves read
// `Palette.Watch`; the cover component and the treatments never do. Every leaf paints its neutral fallback immediately
// and upgrades in place (E6) — nothing waits on a colour.
//
// 0.3 DATA: the tiles are the newest-first `Edges.Liked` prefix through `LikedCoverRules.Tiles(ReadOnlySpan<Track>)`
// (dedupe by album slot + image id). The persisted style is `Prefs.Appearance.LikedCover` (the epoch is the recompute
// trigger; the VALUE is carried in the snapshot — the 0.2.9 epoch caveat). Tone's count is stated only once the liked
// edge is Complete (a partial count on the cover is a lie, ch 07 §7).

using System.Globalization;
using System.Runtime.InteropServices;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Render;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct User
{
    // ══ 1. THE CONTRACT SURFACE (§2.3) ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>The dynamic Liked Songs cover. <paramref name="picker"/> mounts the "Cover style" pill + flyout (the rail
    /// cover, the vertical hero); false for strips, cards, chips and rows. GEOMETRY FREEZES AT MOUNT, so all three are in
    /// the Key and a rail drag REMOUNTS (ch 07 §0.15). The two arms key under DIFFERENT prefixes so a page switching arms
    /// never position-matches one onto the other.</summary>
    public static Element LikedCover(float size, float radius, bool picker, string? morphKey = null)
        => picker
            ? Embed.Comp(() => new LikedCoverPicker(size, radius, morphKey)) with
            {
                Key = "liked-cover-pick:" + (int)size + ":" + (int)radius + ":" + (morphKey ?? ""),
            }
            : Embed.Comp(() => new LikedCoverArt(size, radius, morphKey)) with
            {
                Key = "liked-cover:" + (int)size + ":" + (int)radius + ":" + (morphKey ?? ""),
            };

    /// <summary>The card / sidebar / menu-header funnel (0.2.9 <c>LikedSongsArtwork.Fitted</c>): a square slot gets the
    /// cover verbatim (its own site ladder: a treatment ≥ 140, the flat 2×2 mosaic below, the stock PNG below that); a
    /// letterbox slot composes at its LONGER edge and centre-crops, as every other cover in a non-square frame does.</summary>
    public static Element LikedArtwork(float width, float height, float radius)
    {
        if (LikedCoverRules.IsSquare(width, height)) return LikedCover(width, radius, picker: false);
        float side = LikedCoverRules.FitSide(width, height);
        return new BoxEl
        {
            Width = width, Height = height, Shrink = 0f, ClipToBounds = true, Corners = CornerRadius4.All(radius),
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            // The composed square carries NO corners of its own — the frame rounds the slot; rounding both would notch
            // the crop.
            Children = [LikedCover(side, 0f, picker: false)],
        };
    }

    /// <summary><c>Drag.LikedChipArt</c>: the 40-DIP art box's cover — below the treatment floor, so the flat 2×2 mosaic
    /// of the newest likes (or the stock PNG). A live component element: build it ONCE at install, never per frame.</summary>
    public static Element LikedChipArt() => LikedCover(ChipArtEdge, Radii.Control, picker: false);

    const float ChipArtEdge = 40f;

    /// <summary>ch 07 §0.12: the page tone keys to the NEWEST tile when the persisted style composes a treatment, else
    /// null. Reads the liked edge and the tracks — call inside a render that subscribes <c>Edges.Liked.Changed</c>.</summary>
    public static string? LikedToneAnchorUrl()
    {
        var requested = LikedCoverRules.FromSetting(Prefs.Appearance.LikedCover(LikedCoverRules.StyleCount));
        if (requested == LikedCoverStyle.Stock) return null;
        int n = FillLikedTiles(s_anchorScratch);
        return n > 0 && LikedCoverRules.Effective(requested, n) != LikedCoverStyle.Stock ? s_anchorScratch[0] : null;
    }

    static readonly string[] s_anchorScratch = new string[LikedCoverRules.MaxTiles];

    /// <summary>The newest-first distinct liked cover urls, written into <paramref name="into"/>. UI thread.</summary>
    static int FillLikedTiles(string[] into)
    {
        var scope = Entities.Current;
        int me = scope.MeSlot;
        if (me <= Table.None) return 0;
        return LikedCoverRules.Tiles(MemoryMarshal.Cast<int, Track>(scope.Edges.Liked.Targets(me)), into);
    }

    /// <summary>The bundled Spotify stock cover — correct offline, never a render-time request. No reveal transition: a
    /// fade on the DEGRADE target is what reads as a flash (G8, M-23).</summary>
    static Element StockLikedCover(float size, float radius, string? morphKey)
        => Image(s_stockCoverPath, size, size, radius, Design.ArtworkPlaceholder, transition: ImageTransition.None)
            with { MorphId = morphKey };

    static readonly string s_stockCoverPath = Path.Combine(AppContext.BaseDirectory, "assets", "covers", "liked-songs-300.png");

    // ══ 2. THE SNAPSHOT (one equality-gated view of everything a cover depends on) ═══════════════════════════════════

    /// <summary>Equality over the scalars and <see cref="TileKey"/>, never over <see cref="Tiles"/>: an array compares by
    /// reference, so default record equality would report "changed" on every recompute and defeat the gate (ch 07 §9).</summary>
    sealed record LikedSnapshot(LikedCoverStyle Requested, LikedCoverStyle Effective, bool WantsArt,
                                string[] Tiles, string TileKey, int TrackCount)
    {
        public static readonly LikedSnapshot Stock = new(LikedCoverStyle.Stock, LikedCoverStyle.Stock, false, [], "", 0);

        public bool Equals(LikedSnapshot? other)
            => other is not null && Requested == other.Requested && Effective == other.Effective
               && WantsArt == other.WantsArt && TrackCount == other.TrackCount
               && string.Equals(TileKey, other.TileKey, StringComparison.Ordinal);

        public override int GetHashCode() => HashCode.Combine(Requested, Effective, WantsArt, TrackCount, TileKey);
    }

    /// <summary>Read the snapshot, SUBSCRIBING the calling memo to the scope, the appearance epoch, the liked edge and the
    /// track rows. <paramref name="previous"/>'s tile array is reused when the content key did not move, so a like that
    /// does not change the newest sixteen covers allocates one string and repaints nothing.</summary>
    static LikedSnapshot ReadLikedSnapshot(LikedSnapshot? previous, string[] scratch)
    {
        _ = Entities.ScopeEpoch.Value;
        var requested = LikedCoverRules.FromSetting(Prefs.Appearance.LikedCover(LikedCoverRules.StyleCount));
        // "Would this style compose art AT ALL, given an unlimited library?" — the cost gate (E4). Only Stock says no.
        bool wantsArt = LikedCoverRules.Effective(requested, int.MaxValue) != LikedCoverStyle.Stock;
        if (!wantsArt) return new LikedSnapshot(requested, LikedCoverStyle.Stock, false, [], "", 0);

        var scope = Entities.Current;
        var liked = scope.Edges.Liked;
        _ = liked.Changed.Value;
        _ = scope.Tracks.Changed.Value;
        var (tiles, key) = KeyedTiles(scratch, previous);
        int n = tiles.Length;
        int me = scope.MeSlot;
        int count = me > Table.None && liked.State(me) == EdgeState.Complete ? liked.Count(me) : 0;
        return new LikedSnapshot(requested, LikedCoverRules.Effective(requested, n), true, tiles, key, count);
    }

    /// <summary>The current tiles and their content key (unit-separator joined). Reuses <paramref name="previous"/>'s
    /// array when the key did not move. Subscribes nothing itself.</summary>
    static (string[] Tiles, string Key) KeyedTiles(string[] scratch, LikedSnapshot? previous)
    {
        const char TileKeySeparator = (char)0x1F;
        int n = FillLikedTiles(scratch);
        string key = n == 0 ? "" : string.Join(TileKeySeparator, scratch, 0, n);
        string[] tiles = previous is not null && string.Equals(previous.TileKey, key, StringComparison.Ordinal)
            ? previous.Tiles
            : scratch.AsSpan(0, n).ToArray();
        return (tiles, key);
    }

    /// <summary>The cover's demand: the liked list once per scope when nobody answered (a failed ask waits for its Retry),
    /// then the identity bundle of the newest rows the tile scan can reach. Idempotent — auto-tracked effects re-run it as
    /// pages land.</summary>
    static void DemandLikedCover()
    {
        _ = Entities.ScopeEpoch.Value;
        var scope = Entities.Current;
        int me = scope.MeSlot;
        if (me <= Table.None) return;
        var liked = scope.Edges.Liked;
        _ = liked.Changed.Value;
        if (liked.State(me) == EdgeState.Unknown)
        {
            if (!liked.IsFailed(me)) Entities.EnsureEdge(FetchEdge.Liked, me);
            return;
        }
        var slots = liked.Targets(me);
        int take = Math.Min(slots.Length, CoverRowWindow);
        if (take > 0) Entities.Ensure(MemoryMarshal.Cast<int, Track>(slots[..take]), TrackFields.Image | TrackFields.Album);
    }

    /// <summary>How deep the tile scan may need to look: sixteen distinct albums are usually inside the newest ~50 likes;
    /// 96 bounds a pathological run of one album without making the cover a whole-library fetch.</summary>
    const int CoverRowWindow = 96;

    // ══ 3. THE HEART (vector geometry — the app's own `Icons.HeartFill` contour, ch 07 §0.11) ═══════════════════════

    static class LikedHeart
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

    // ══ 4. THE TREATMENTS (a static factory layer — no hooks, no state; LikedCoverArt owns every hook) ═══════════════

    static class LikedTreatments
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
            if (style == LikedCoverStyle.Stock || tiles.Length == 0) return StockLikedCover(size, radius, morphKey);

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

        static readonly ColorF Black = ColorF.FromRgba(0, 0, 0), White = ColorF.FromRgba(255, 255, 255);

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
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f, Shrink = 0f,
            Height = 34f, Padding = new Edges4(10f, 0f, 12f, 0f),
            Corners = Radii.FullAll, Fill = Design.OnMedia.ScrimRest,
            BorderWidth = 1f, BorderColor = Design.OnMedia.Stroke, HitTestVisible = false,
            Children =
            [
                Icon(Icons.HeartFill, 16f, Design.OnMedia.Ink),
                Design.Type.DenseTitle(Loc.Get(Strings.Detail.LikedSongs)) with
                {
                    Color = Design.OnMedia.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };

        /// <summary>The white badge disc with the heart. The glyph's near-black is a constant: the badge sits on a palette
        /// GROUND, and tinting it from the same swatch is how it would vanish into it.</summary>
        static Element HeartBadge(bool right, ShadowSpec? shadow = null) => new BoxEl
        {
            AlignSelf = FlexAlign.End, JustifySelf = right ? FlexAlign.End : FlexAlign.Start,
            Margin = right ? new Edges4(0f, 0f, 14f, 14f) : new Edges4(14f, 0f, 0f, 14f),
            Width = 40f, Height = 40f, Shrink = 0f, Corners = Radii.FullAll, Fill = White,
            Shadow = shadow ?? Elevation.Card, HitTestVisible = false,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = [Icon(Icons.HeartFill, 20f, ColorF.FromRgba(0x1B, 0x1B, 0x20))],
        };

        /// <summary>The badge's cast on ART: fixed, theme-independent (a theme-split token is a 10 % whisper in light
        /// theme that a white disc on a bright sleeve loses — artwork has no theme).</summary>
        static readonly ShadowSpec BadgeOnArtShadow = new(Blur: 14f, OffsetY: 3f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x59));

        /// <summary>The plate the two full-bleed grids sit on (#131318).</summary>
        static readonly ColorF PlateDark = ColorF.FromRgba(0x13, 0x13, 0x18);

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
        static readonly ColorF LensGroundDim = Black with { A = 0.38f };
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
                LensVeil(At(tiles, 0), At(tiles, 1)),
                new BoxEl
                {
                    AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
                    Width = LensHeartEdge, Height = LensHeartEdge, Shrink = 0f, ZStack = true, HitTestVisible = false,
                    ClipToBounds = true, ClipPath = LikedHeart.Data, ClipPathRule = FillRule.NonZero,
                    ClipPathViewBoxW = LikedHeart.ViewBox, ClipPathViewBoxH = LikedHeart.ViewBox,
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
                    Children = [LikedHeart.Rim(LensHeartEdge, White with { A = 0.55f }, LensRimWidth)],
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
                new BoxEl { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false, Fill = PlateDark },
                RainbowGrid(tileKey, LikedCoverRules.FillCells(tiles, LikedCoverRules.MaxTiles), cell, gap, GridDecodePx),
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

        static readonly ColorF VignetteInk = ColorF.FromRgba(0x0A, 0x0A, 0x0E);

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
                new BoxEl { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false, Fill = PlateDark },
                new BoxEl { OffsetX = WallLeft, OffsetY = WallTop, Width = WallSpan, Shrink = 0f, HitTestVisible = false, Children = [drifting] },
                // The two-layer vignette: a radial low-point at (50 %, 42 %), then a vertical pass seating top and foot.
                Fill(new GradientSpec(GradientShape.Radial, 0f,
                [
                    new GradientStop(0.46f, VignetteInk with { A = 0f }),
                    new GradientStop(1f, VignetteInk with { A = 0.62f }),
                ])
                { RadialCenter = new Point2(0.5f, 0.42f), RadialRadius = new Point2(1.2f, 1.2f) }),
                Fill(GradientDown(
                    new GradientStop(0f, VignetteInk with { A = 0.28f }),
                    new GradientStop(0.32f, VignetteInk with { A = 0f }),
                    new GradientStop(0.62f, VignetteInk with { A = 0f }),
                    new GradientStop(1f, VignetteInk with { A = 0.50f }))),
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
                PaletteGround(At(tiles, 0), At(tiles, 1), At(tiles, 0), LikedGroundKind.Marquee),
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
                ToneGround(tiles),
                new BoxEl
                {
                    AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center, ZStack = true,
                    Width = heart, Height = heart, HitTestVisible = false,
                    Children = [LikedHeart.Fill(heart, White with { A = 0.92f }), LikedHeart.Rim(heart, White with { A = 0.60f })],
                },
                // A FACT, so stated only when there is one — never in a miniature. CurrentCulture VERBATIM (0.2.9's exact
                // call; note the "G" format carries no group separator — ch 07 item 73's "1 166" claim is reported).
                !mini && trackCount > 0
                    ? new TextEl(trackCount.ToString(CultureInfo.CurrentCulture))
                    {
                        AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.End, Margin = new Edges4(0f, 0f, 14f, 12f),
                        Size = 12f, LineHeight = 16f, Weight = 600, Color = White with { A = 0.85f },
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
                    BorderWidth = 1f, BorderColor = Design.OnMedia.Stroke,
                    Children = [ArtImage(url, HeroDecodePx, 6f)],
                };
            }
            return CanvasOf(
                PaletteGround(At(tiles, 0), At(tiles, 1), At(tiles, 0), LikedGroundKind.Stack),
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

    // ══ 5. THE PALETTE LEAVES (the ONLY nodes that read `Palette.Watch`) ═════════════════════════════════════════════

    enum LikedGroundKind : byte { Marquee, Stack }

    static Element PaletteGround(string? urlA, string? urlB, string? urlBase, LikedGroundKind kind)
        => Embed.Comp(new GroundProps(urlA, urlB, urlBase, kind), static () => new LikedPaletteGround()) with { Key = "liked-ground:" + kind };

    static Element LensVeil(string? urlA, string? urlB)
        => Embed.Comp(new VeilProps(urlA, urlB), static () => new LikedLensVeil()) with { Key = "liked-lens-veil" };

    /// <summary>Tone's five spots CYCLE the tiles: one liked cover is a legal monochrome tone, four empty spots a hole.</summary>
    static Element ToneGround(string[] tiles)
        => Embed.Comp(new ToneProps(Cycle(tiles, 0), Cycle(tiles, 1), Cycle(tiles, 2), Cycle(tiles, 3), Cycle(tiles, 4)),
                      static () => new LikedToneGround()) with { Key = "liked-tone" };

    static Element RainbowGrid(string tileKey, string[] cells, float edge, float gap, int decodePx)
        => Embed.Comp(new RainbowProps(tileKey, cells, edge, gap, decodePx), static () => new LikedRainbowGrid()) with { Key = "liked-rainbow" };

    static string? Cycle(string[] tiles, int i) => tiles.Length == 0 ? null : tiles[i % tiles.Length];

    /// <summary>A cover's graded scheme — the DARK half in BOTH themes (a cover treatment is imagery carrying on-media
    /// ink over a dark plate in light theme too), falling back to whichever half exists.</summary>
    static bool TryDarkScheme(string? url, out Scheme scheme)
    {
        scheme = default;
        if (string.IsNullOrEmpty(url)) return false;
        return (Palette.TryScheme(url, lightTheme: false, out scheme) || Palette.TryScheme(url, lightTheme: true, out scheme))
               && !scheme.IsEmpty;
    }

    /// <summary>The chroma a cover contributes to a ground: <c>Vivid(Accent(scheme))</c>.</summary>
    static ColorF? LikedChroma(string? url)
        => TryDarkScheme(url, out var s) ? Design.Palette.Vivid(Design.Palette.Accent(in s)) : null;

    /// <summary>The ground plate's tint — the tinted-dark background role, so the plate stays a plate.</summary>
    static ColorF? LikedBaseTint(string? url)
        => TryDarkScheme(url, out var s) ? Design.Palette.TintedDark(in s) : null;

    /// <summary>Subscribe the calling leaf to a cover's grading.</summary>
    static void WatchCover(string? url)
    {
        if (url is { Length: > 0 }) _ = Palette.Watch(url).Value;
    }

    sealed record GroundProps(string? UrlA, string? UrlB, string? UrlBase, LikedGroundKind Kind);
    sealed record VeilProps(string? UrlA, string? UrlB);
    sealed record ToneProps(string? U0, string? U1, string? U2, string? U3, string? U4);

    /// <summary>The cells are an ARRAY (reference equality), so equality is declared over the caller's content key and
    /// the scalar geometry — a fresh-but-equal re-push must not re-render sixteen Watch reads (ch 07 §9).</summary>
    sealed record RainbowProps(string TileKey, string[] Cells, float Edge, float Gap, int DecodePx)
    {
        public bool Equals(RainbowProps? other)
            => other is not null && string.Equals(TileKey, other.TileKey, StringComparison.Ordinal)
               && Edge.Equals(other.Edge) && Gap.Equals(other.Gap) && DecodePx == other.DecodePx;

        public override int GetHashCode() => HashCode.Combine(TileKey, Edge, Gap, DecodePx);
    }

    /// <summary>Marquee's / Stack's ground: two radial washes over a tinted near-black plate — three nodes, because the
    /// engine has one gradient per node (and four stops per gradient).</summary>
    sealed class LikedPaletteGround : Component
    {
        static readonly ColorF MarqueeBase = ColorF.FromRgba(0x17, 0x17, 0x1C);
        static readonly ColorF StackBase = ColorF.FromRgba(0x1B, 0x1B, 0x20);

        public override Element Render()
        {
            var p = UseProps<GroundProps>();
            WatchCover(p.UrlA); WatchCover(p.UrlB); WatchCover(p.UrlBase);

            bool marquee = p.Kind == LikedGroundKind.Marquee;
            ColorF plate = marquee ? MarqueeBase : StackBase;
            // Ungraded ⇒ the plate alone: a dark plate immediately, gaining chroma when the palette answers (E6).
            if (LikedBaseTint(p.UrlBase) is { } tint) plate = ColorF.Lerp(plate, tint, marquee ? 0.40f : 0.45f);

            Element a = LikedChroma(p.UrlA) is { } ca
                ? Wash(ca, marquee ? 0.88f : 0.85f, marquee ? new Point2(0.15f, 0.12f) : new Point2(0.20f, 0.10f),
                       marquee ? new Point2(1.184f, 0.855f) : new Point2(1.250f, 0.855f), marquee ? 0.72f : 0.70f)
                : new BoxEl { HitTestVisible = false };
            Element b = LikedChroma(p.UrlB) is { } cb
                ? Wash(cb, marquee ? 0.82f : 0.80f, marquee ? new Point2(0.88f, 0.88f) : new Point2(0.90f, 0.90f),
                       marquee ? new Point2(1.250f, 0.987f) : new Point2(1.184f, 0.921f), marquee ? 0.72f : 0.70f)
                : new BoxEl { HitTestVisible = false };
            return new BoxEl
            {
                ZStack = true, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false, Fill = plate,
                Children = [a, b],
            };
        }

        static Element Wash(ColorF c, float alpha, Point2 centre, Point2 radius, float falloff) => new BoxEl
        {
            AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
            Gradient = new GradientSpec(GradientShape.Radial, 0f, [new GradientStop(0f, c with { A = alpha }), new GradientStop(falloff, c with { A = 0f })])
            { RadialCenter = centre, RadialRadius = radius },
        };
    }

    /// <summary>Lens's veil: <c>linear-gradient(160deg, t1 45%, t2 38%)</c> at opacity .55, folded into two alphas.
    /// ENGINE ADAPTATION: no multiply blend — source-over at the folded alphas over a ground already at 62 % (§4.4 #1).
    /// ONE grading is enough: stop 1 reuses stop 0's colour (item 72); only with neither graded is it the neutral.</summary>
    sealed class LikedLensVeil : Component
    {
        const float Angle = 70f;   // CSS 160° − 90°
        const float AlphaA = 0.45f * 0.55f, AlphaB = 0.38f * 0.55f;

        public override Element Render()
        {
            var p = UseProps<VeilProps>();
            WatchCover(p.UrlA); WatchCover(p.UrlB);
            ColorF a = LikedChroma(p.UrlA) ?? Design.Palette.PageToneNeutralDark;
            ColorF b = LikedChroma(p.UrlB) ?? a;
            return new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                Gradient = LinearGradient(Angle, new GradientStop(0f, a with { A = AlphaA }), new GradientStop(1f, b with { A = AlphaB })),
            };
        }
    }

    /// <summary>Tone's face: a plate graded from the newest cover, five radial spots, the closing vignette. ENGINE
    /// ADAPTATION: no additive blend (spots are source-over at α .55) and no film grain (§4.4 #2, #3). A spot exists only
    /// for a GRADED tile, so an ungraded cover is the plate + vignette alone.</summary>
    sealed class LikedToneGround : Component
    {
        static readonly Point2[] Centres = [new(0.18f, 0.20f), new(0.82f, 0.25f), new(0.25f, 0.85f), new(0.80f, 0.80f), new(0.50f, 0.50f)];
        static readonly float[] RadiusFactors = [0.9f, 0.8f, 0.85f, 0.75f, 0.6f];
        const float RadiusScale = 0.62f;
        const float SpotAlpha = 0.55f;
        static readonly Element Empty = new BoxEl { HitTestVisible = false };

        public override Element Render()
        {
            var p = UseProps<ToneProps>();
            Span<string?> urls = [p.U0, p.U1, p.U2, p.U3, p.U4];
            for (int i = 0; i < urls.Length; i++) WatchCover(urls[i]);

            // The base is the app's neutral page-tone dark — Tone's plate and the page ground under it are the same token.
            ColorF plate = LikedChroma(p.U0) is { } lead
                ? ColorF.Lerp(Design.Palette.PageToneNeutralDark, lead, 0.55f)
                : Design.Palette.PageToneNeutralDark;

            var kids = new Element[urls.Length + 1];
            for (int i = 0; i < urls.Length; i++)
            {
                float r = RadiusFactors[i] * RadiusScale;
                kids[i] = LikedChroma(urls[i]) is { } c
                    ? new BoxEl
                    {
                        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                        Gradient = new GradientSpec(GradientShape.Radial, 0f, [new GradientStop(0f, c with { A = SpotAlpha }), new GradientStop(1f, c with { A = 0f })])
                        { RadialCenter = Centres[i], RadialRadius = new Point2(r, r) },
                    }
                    : Empty;
            }
            kids[urls.Length] = new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                Gradient = GradientDown(
                    new GradientStop(0f, ColorF.FromRgba(255, 255, 255) with { A = 0.10f }),
                    new GradientStop(0.6f, ColorF.FromRgba(0, 0, 0) with { A = 0f }),
                    new GradientStop(1f, ColorF.FromRgba(0, 0, 0) with { A = 0.28f })),
            };
            return new BoxEl { ZStack = true, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false, Fill = plate, Children = kids };
        }
    }

    /// <summary>Rainbow's grid: a late grading does not just change a colour, it MOVES a tile into the hue ramp. Cells are
    /// positional, so a reorder swaps already-decoded textures rather than re-requesting them. The hue is read from
    /// <c>BackgroundTintedBase</c> — the chroma role, never <c>TextBrightAccent</c> (white on every dark half).</summary>
    sealed class LikedRainbowGrid : Component
    {
        float?[] _hues = [];

        public override Element Render()
        {
            var p = UseProps<RainbowProps>();
            var cells = p.Cells;
            for (int i = 0; i < cells.Length; i++) WatchCover(cells[i]);
            if (_hues.Length != cells.Length) _hues = new float?[cells.Length];
            for (int i = 0; i < cells.Length; i++)
                _hues[i] = TryDarkScheme(cells[i], out var s) ? LikedCoverRules.HueOf(s.BackgroundTintedBase) : null;

            int[] order = LikedCoverRules.RainbowOrder(_hues);
            var kids = new Element[order.Length];
            for (int i = 0; i < order.Length; i++) kids[i] = LikedTreatments.ArtCell(cells[order[i]], p.Edge, 0f, p.DecodePx);
            return LikedTreatments.TileGridOf(LikedCoverRules.RainbowColumns, kids, p.Edge, p.Gap);
        }
    }

    // ══ 6. THE COVER COMPONENT (the site ladder + the ambient loops) ═════════════════════════════════════════════════

    /// <summary>The ONLY component that owns the cover's hooks — declared unconditionally, in one order, on every path, so a
    /// hook behind "which style?" can never shift order the first time a user picks one.</summary>
    sealed class LikedCoverArt : Component
    {
        readonly float _size, _radius;
        readonly string? _morphKey;
        readonly string[] _scratch = new string[LikedCoverRules.MaxTiles];
        LikedSnapshot? _last;
        Memo<LikedSnapshot>? _snapshot;

        // The loop nodes as INSTANCE state: the sinks are allocated once per component, never per render.
        NodeHandle _loopA, _loopB, _loopC;
        readonly Action<NodeHandle> _sinkA, _sinkB, _sinkC;
        readonly Func<LikedSnapshot> _compute;
        readonly Action _demand, _seed;
        LikedCoverStyle _seedStyle;
        bool _hasLoops;

        public LikedCoverArt(float size, float radius, string? morphKey)
        {
            _size = size; _radius = radius; _morphKey = morphKey;
            _sinkA = h => _loopA = h;
            _sinkB = h => _loopB = h;
            _sinkC = h => _loopC = h;
            _compute = () => _last = ReadLikedSnapshot(_last, _scratch);
            _demand = () => { if (_snapshot!.Value.WantsArt) DemandLikedCover(); };
            // After realize (the handles are only valid then) and with the scene-liveness check. Loops quiesce by
            // themselves under a parked page; there is nothing to tear down on nav away.
            _seed = () =>
            {
                if (!_hasLoops || Context.Anim is not { } anim || Context.Scene is not { } scene) return;
                var a = !_loopA.IsNull && scene.IsLive(_loopA) ? _loopA : default;
                var b = !_loopB.IsNull && scene.IsLive(_loopB) ? _loopB : default;
                var c = !_loopC.IsNull && scene.IsLive(_loopC) ? _loopC : default;
                LikedTreatments.SeedLoops(_seedStyle, anim, a, b, c);
            };
        }

        public override Element Render()
        {
            _snapshot = UseComputed(_compute);
            var snap = _snapshot.Value;
            UseEffect(_demand);

            // The AND with the size must survive: a 76-DIP miniature must never allocate three timeline rows.
            _hasLoops = LikedTreatments.HasLoops(snap.Effective) && _size >= LikedTreatments.BadgeMinSize;
            _seedStyle = snap.Effective;
            // Deps carry the style, the art and the size: a swap or a tile change re-seeds, a repaint does not.
            UseLayoutEffect(_seed, DepKey.From(HashCode.Combine((int)snap.Effective, snap.TileKey, (int)_size, _hasLoops)));

            // The site ladder (LikedCoverRules.Site): a 48-DIP Wall is 36 unreadable specks with a loop nobody sees, so
            // below the floor the answer is the flat 2×2 mosaic; Tone keeps its gradient + heart.
            switch (LikedCoverRules.Site(snap.Effective, _size, snap.Tiles.Length, LikedTreatments.BadgeMinSize))
            {
                case LikedCoverSite.Stock:
                    return StockLikedCover(_size, _radius, _morphKey);
                case LikedCoverSite.MiniTone:
                    return LikedTreatments.Build(LikedCoverStyle.Tone, snap.Tiles, snap.TileKey, snap.TrackCount, _size, _radius, mini: true, _morphKey);
                case LikedCoverSite.MiniMosaic:
                    return Controls.Mosaic(snap.Tiles, _size, _size, _radius) with { MorphId = _morphKey };
                default:
                    return LikedTreatments.Build(snap.Effective, snap.Tiles, snap.TileKey, snap.TrackCount, _size, _radius, mini: false, _morphKey,
                        _hasLoops ? new LikedTreatments.Loops(_sinkA, _sinkB, _sinkC) : default);
            }
        }
    }

    // ══ 7. THE PICKER (W14) + THE FLYOUT (W15) ═══════════════════════════════════════════════════════════════════════

    /// <summary>The cover PLUS its "Cover style" affordance: hovering or focusing the cover reveals an on-media pill whose
    /// flyout offers the nine LIVE miniatures as one radio group. The pill is ALWAYS mounted; its reveal is a BOUND
    /// opacity (a compositor cross-fade on one node), so hovering the cover costs no reconcile.</summary>
    sealed class LikedCoverPicker : Component
    {
        readonly float _size, _radius;
        readonly string? _morphKey;
        readonly Signal<bool> _hovered = new(false), _focused = new(false), _open = new(false);
        NodeHandle _anchor;
        OverlayHandle? _handle;
        IOverlayService? _overlay;
        Element? _art;
        readonly Action<NodeHandle> _anchorSink;
        readonly Action _toggle, _exit;
        readonly Action<Point2> _hover;
        readonly Action<KeyEventArgs> _key;
        readonly Action<bool> _focus;
        readonly Prop<float> _pillOpacity;

        public LikedCoverPicker(float size, float radius, string? morphKey)
        {
            _size = size; _radius = radius; _morphKey = morphKey;
            _anchorSink = h => _anchor = h;
            _hover = _ => { if (!_hovered.Peek()) _hovered.Value = true; };
            _exit = () => { if (_hovered.Peek()) _hovered.Value = false; };
            _focus = f => { if (_focused.Peek() != f) _focused.Value = f; };
            // It stays lit while the flyout is up, so the affordance does not vanish as the pointer travels into it.
            _pillOpacity = Prop.Of(() => _hovered.Value || _focused.Value || _open.Value ? 1f : 0f);
            _toggle = Toggle;
            // Down / F4 open an anchored surface — the app's one keyboard contract (Space/Enter arrive as OnClick).
            _key = e => { if (e.KeyCode is Keys.Down or Keys.F4) { Toggle(); e.Handled = true; } };
        }

        void Toggle()
        {
            var overlay = _overlay;
            if (Controls.IsNullOverlay(overlay)) return;
            if (_handle is { IsOpen: true } already) { already.Close(); return; }
            // The chrome (acrylic + stroke + elevation + reveal) is the HOST's; FocusTrap keeps the radio roving inside;
            // ConstrainToRootBounds flips the flyout up near the window's foot.
            _handle = overlay.Open(
                () => _anchor,
                static () => Embed.Comp(static () => new LikedCoverStyleFlyout()),
                FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Flyout)
                { ConstrainToRootBounds = true });
            _open.Value = true;
            _handle.ClosedAction = () => { _handle = null; _open.Value = false; };
        }

        public override Element Render()
        {
            _overlay = UseContext(Overlay.Service);
            _art ??= LikedCover(_size, _radius, picker: false, _morphKey);
            return new BoxEl
            {
                ZStack = true, Width = _size, Height = _size, Shrink = 0f,
                // Hover is read on the WHOLE cover: the pill must be visible before the pointer can reach it.
                OnHoverMove = _hover, OnPointerExit = _exit,
                Children = [_art, Pill()],
            };
        }

        Element Pill() => new BoxEl
        {
            AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.End, Margin = new Edges4(0f, 0f, 10f, 10f),
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Shrink = 0f,
            Height = 30f, Padding = new Edges4(10f, 0f, 12f, 0f), Corners = Radii.FullAll,
            Fill = Design.OnMedia.ScrimRest, HoverFill = Design.OnMedia.ScrimHover, PressedFill = Design.OnMedia.ScrimPressed,
            BorderWidth = 1f, BorderColor = Design.OnMedia.Stroke,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            OnClick = _toggle, OnKeyDown = _key, OnRealized = _anchorSink,
            // FOCUS is part of the reveal: a keyboard user must never land on an invisible control (item 28).
            OnFocusChanged = _focus,
            Opacity = _pillOpacity, Transition = MotionTok.ControlNormal,
            Children =
            [
                Icon(Icons.Brush, 14f, Design.OnMedia.Ink),
                new TextEl(Loc.Get(Strings.Detail.LikedCover.Pill))
                {
                    Size = 12f, LineHeight = 16f, Weight = 600, Color = Design.OnMedia.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };
    }

    /// <summary>The flyout body: the eyebrow, a 3-wide column-major radio grid of LIVE miniatures (the real treatments in
    /// <c>mini</c> form — static, capped cell counts, texture-cache hits at the cover's own decode buckets), and the note.
    /// A component so it re-renders on the appearance bump and the checked card tracks the value that PERSISTED (E15).</summary>
    sealed class LikedCoverStyleFlyout : Component
    {
        const float MiniEdge = 76f;      // PickerCoverMini's 92 minus its 8-per-side resting inset
        const float MiniRadius = 5f;
        const int Columns = 3;
        static readonly Edges4 CardPad = new(16f, 15f, 16f, 17f);   // WinUI FlyoutContentPadding

        readonly string[] _scratch = new string[LikedCoverRules.MaxTiles];
        LikedSnapshot? _last;
        LikedSnapshot _snap = LikedSnapshot.Stock;
        readonly Func<LikedSnapshot> _compute;
        readonly Action _demand;
        readonly Func<int, bool, Element> _mini;
        readonly Action<int> _apply;

        public LikedCoverStyleFlyout()
        {
            // The picker reads the tiles even while the persisted style is Stock — every miniature needs the art.
            _compute = () =>
            {
                var s = ReadLikedSnapshot(_last, _scratch);
                if (!s.WantsArt)
                {
                    _ = Entities.Current.Edges.Liked.Changed.Value;
                    _ = Entities.Current.Tracks.Changed.Value;
                    var (tiles, key) = KeyedTiles(_scratch, _last);
                    s = s with { Tiles = tiles, TileKey = key };
                }
                return _last = s;
            };
            // E4: opening the picker is the SECOND thing that charges the liked-list warm.
            _demand = DemandLikedCover;
            _apply = Apply;
            _mini = Mini;
        }

        public override Element Render()
        {
            _snap = UseComputed(_compute).Value;
            UseEffect(_demand);
            var order = LikedCoverRules.PickerOrder;
            int selected = IndexOf(order, _snap.Requested);
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.M, Shrink = 0f, Padding = CardPad,
                // BORDER-box: exactly three cards, two gutters and the inset — no early wrap, no fourth-column slack (332).
                Width = Columns * Controls.PickerCoverMini.Width + (Columns - 1) * Spacing.M + CardPad.Left + CardPad.Right,
                Children =
                [
                    Design.Type.Eyebrow(Loc.Get(Strings.Detail.LikedCover.Title)) with { Color = Tok.TextPrimary },
                    Controls.PickerStrip(order.Length, selected, _mini, _apply, maxColumns: Columns),
                    new TextEl(Loc.Get(Strings.Detail.LikedCover.Note))
                    {
                        Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, Wrap = TextWrap.WrapWholeWords, MaxLines = 3,
                    },
                ],
            };
        }

        /// <summary>Idempotent and cheap, which it MUST be: the strip fires on every keyboard rove (selection follows focus),
        /// and roving IS the preview — the cover behind the flyout repaints on the bump.</summary>
        static void Apply(int i)
        {
            var order = LikedCoverRules.PickerOrder;
            if ((uint)i >= (uint)order.Length) return;
            Prefs.Appearance.Set(Platform.Keys.LikedCoverStyle, LikedCoverRules.ToSetting(order[i]));
        }

        /// <summary>Below its floor a style shows what picking it would paint TODAY — the stock cover — dimmed to .45, and
        /// stays SELECTABLE (the delivered E1 deviation: RadioButtons has no per-item enabled state, and an unfed choice
        /// lights up by itself when the library reaches the floor).</summary>
        Element Mini(int i, bool on)
        {
            var style = LikedCoverRules.PickerOrder[i];
            var snap = _snap;
            bool fed = snap.Tiles.Length >= LikedCoverRules.MinTiles(style);
            var effective = LikedCoverRules.Effective(style, snap.Tiles.Length);
            Element thumb = new BoxEl
            {
                Width = MiniEdge, Height = MiniEdge, Shrink = 0f, HitTestVisible = false, Opacity = fed ? 1f : 0.45f,
                // morphKey null: nine miniatures claiming the hero's shared-element id would hijack a Home → detail fly.
                Children = [LikedTreatments.Build(effective, snap.Tiles, snap.TileKey, snap.TrackCount, MiniEdge, MiniRadius, mini: true, morphKey: null)],
            };
            return Controls.PickerTitled(Controls.PickerCard(on, Controls.PickerCoverMini, thumb), Loc.Get(LikedCoverRules.NameKey(style)), on);
        }

        /// <summary>The persisted style's slot, or Stock's (a hand-edited value this build does not define reads as Stock).</summary>
        static int IndexOf(LikedCoverStyle[] order, LikedCoverStyle style)
        {
            int stock = 0;
            for (int i = 0; i < order.Length; i++)
            {
                if (order[i] == style) return i;
                if (order[i] == LikedCoverStyle.Stock) stock = i;
            }
            return stock;
        }
    }
}
