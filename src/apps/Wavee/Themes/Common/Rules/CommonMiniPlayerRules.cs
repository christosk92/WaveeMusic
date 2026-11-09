// ── Themes/Common/Rules/CommonMiniPlayerRules.cs — the mini player's fixed geometry and keys (#197) ─────────────────────
//
// Theme numbers (button boxes, radii, paddings, type) live in each theme's MiniPlayerStyle; these are the shape-independent
// constants of the Common template, read off the approved prototype (scratchpad miniplayer/index.html): node keys, the face-flip
// slide, the strip overlay's fade run, the square's and the tall column's insets, the lyric windows, the flyouts.

using FluentGpu.Foundation;

namespace Wavee.Themes.Common;

internal static class CommonMiniPlayerRules
{
    public const string RootKey = "mini.root", FaceKey = "mini.face", ChromeKey = "mini.chrome", TransportKey = "mini.transport",
        SeekKey = "mini.seek", HoverKey = "mini.hover", RestKey = "mini.rest", MetaKey = "mini.meta", PlayKey = "mini.play",
        SaveKey = "mini.save", DeviceKey = "mini.device", OptionsKey = "mini.options", BackKey = "mini.back", FlipKey = "mini.flip",
        EdgeKey = "mini.edge", HudKey = "mini.hud", LyricsKey = "mini.lyrics", TickerKey = "mini.ticker", BleedKey = "mini.bleed",
        LabelKey = "mini.label", VolumeKey = "mini.volume", ShapeKey = "mini.shape", OptionsBodyKey = "mini.options.body", DevicesBodyKey = "mini.devices.body";

    /// <summary>The face flip slides the new face in from this fraction of its box.</summary>
    public const float FlipSlide = 0.18f;

    // ── text ──
    /// <summary>Text line gaps, the seek row's gap and height, a time label's minimum width.</summary>
    public const float LineGap = 1f, SeekGap = 8f, SeekRowH = 22f, TimeMinW = 32f;
    /// <summary>On-media ink: white, the secondary line, the tertiary line, a hover plate and the track behind a rail.</summary>
    public static readonly ColorF OnMediaInk = new(1f, 1f, 1f, 1f);
    /// <summary>An accent mark over imagery is the accent lifted this far toward white.</summary>
    public const float OnDarkLift = 0.45f;
    public const float OnMediaSecondaryAlpha = 0.8f, OnMediaTertiaryAlpha = 0.5f, OnMediaPlateAlpha = 0.16f, OnMediaTrackAlpha = 0.32f;
    /// <summary>A disabled control is its ink at this alpha.</summary>
    public const float DisabledAlpha = 0.4f;
    /// <summary>The accent's own ink: dark on a light accent (relative luminance above this), white otherwise.</summary>
    public const float OnAccentLuma = 0.42f;
    public static readonly ColorF OnAccentDark = new(0.067f, 0.067f, 0.067f, 1f);
    /// <summary>The play disc's hover and press: the accent mixed toward white / black by these.</summary>
    public const float PlayHoverMix = 0.08f, PlayPressMix = 0.12f;

    // ── strip ──
    /// <summary>The strip's hover controls: the fade run into the solid plate (DIP), the plate's lead, the inset that leaves the edge line,
    /// the plate's end padding.</summary>
    public const float StripFade = 44f, StripFadeLead = 48f, StripOverlayFoot = 8f, StripOverlayEnd = 8f;
    /// <summary>The window's edge line: a hairline at rest, thicker while the controls show, inside a taller hit box.</summary>
    public const float EdgeRest = 2f, EdgeHot = 5f, EdgeHit = 10f;
    /// <summary>The resting saved heart's glyph.</summary>
    public const float SavedGlyph = 15f;
    /// <summary>The visualizer face behind a strip, at this opacity.</summary>
    public const float StripVizAlpha = 0.22f;
    /// <summary>The ticker's rolling lines: in from this far below.</summary>
    public const float TickerRise = 8f;
    /// <summary>The badge's offset past the thumb's corner and its ring (the window's ground around it); its glyph and eq bars.</summary>
    public const float BadgeOffset = 3f, BadgeRing = 2f, BadgeGlyph = 0.56f, EqBarW = 2f, EqBarGap = 1.5f, EqFoot = 5f;
    public const float EqBar0 = 0.5f, EqBar1 = 1f, EqBar2 = 0.7f, EqBarMax = 8f;

    // ── wide ──
    /// <summary>The wide card's column gap, the transport row's bleed into the padding, the chrome's inset from the corner.</summary>
    public const float WideColGap = 4f, CtrlBleedX = 6f, CtrlBleedBottom = 4f, ChromeInset = 6f, ChromePad = 2f, ChromeSep = 18f, ChromeSepW = 1f;
    /// <summary>The 32-DIP window buttons stand this far apart, so each keeps a 40-DIP reach with its slop.</summary>
    public const float ChromeGap = 8f;
    /// <summary>The flip pill on the wide art: its inset from the art's foot, its corner, its plate.</summary>
    public const float FlipFoot = 6f, FlipCorner = 16f;
    public static readonly ColorF FlipPlate = new(0f, 0f, 0f, 0.6f);
    /// <summary>The chrome pill over imagery: a dark glass.</summary>
    public static readonly ColorF ChromeOnDark = new(0f, 0f, 0f, 0.55f);
    /// <summary>The face name in the flip pill: its minimum width.</summary>
    public const float FaceNameMinW = 62f;

    // ── square ──
    /// <summary>The square's resting row (inset, foot inset), the object faces' and Classic cover's foot band, the hover layer's padding.</summary>
    public const float SquareRestInset = 14f, SquareRestFoot = 13f, SquareObjectFoot = 62f, SquareWaveFoot = 66f;
    public const float SquareHoverPadX = 10f, SquareHoverTop = 8f, SquareHoverFoot = 10f, SquareTopButton = 36f, SquareTopGap = 4f, SquareMidGap = 14f, SquareMidGlyph = 22f;
    /// <summary>The resting shade starts at this fraction of the height (its angle: top to bottom).</summary>
    public const float ShadeFrom = 0.45f, ShadeAngle = 90f;
    /// <summary>The ground's corner glow runs from the top right toward the bottom left and is gone this far along.</summary>
    public const float GlowAngle = 135f, GlowEnd = 0.62f;
    /// <summary>The Zune cover bleed's inset (it starts left of the window).</summary>
    public const float BleedX = -16f, BleedY = 4f, BleedAlpha = 0.92f;

    // ── tall ──
    /// <summary>The tall column's padding and gap; the transport's gap; the immersive panel's gap; the label's inset.</summary>
    public const float TallPadTop = 14f, TallPadFoot = 12f, TallGap = 10f, TallTransportGap = 10f, TallPanelGap = 6f, TallLabelX = 14f, TallLabelY = 12f;
    /// <summary>What the tall column's fixed rows take (title block, seek, transport, gaps and padding), with and without the volume row: the
    /// art takes the rest.</summary>
    public const float TallFixed = 196f, TallFixedRoomy = 222f;
    /// <summary>The immersive scrim's stops: dark at the top, clear through the middle, dark under the panel.</summary>
    public const float ScrimTopAlpha = 0.38f, ScrimTopEnd = 0.18f, ScrimClearEnd = 0.46f, ScrimFootFrom = 0.78f, ScrimFootAlpha = 0.82f;
    /// <summary>Classic's tinted band behind the column's top (a share of the height).</summary>
    public const float WaveBandShare = 0.62f;
    /// <summary>The volume row: its height, the percent's width.</summary>
    public const float VolumeRowH = 24f, VolumePctW = 26f;

    // ── lyrics ──
    /// <summary>Where the active line is held in each window (a share of its height) and the window's top and foot fades (shares).</summary>
    public const float LyricAnchorWide = 0.38f, LyricAnchorSquare = 0.42f, LyricAnchorTall = 0.4f, LyricFadeTop = 0.22f, LyricFadeFoot = 0.28f;
    /// <summary>The lyric windows' insets: the square's (top, foot) and the tall column's (top, foot), the start inset when the theme marks
    /// lines with a bar or plate.</summary>
    public const float SquareLyricTop = 14f, SquareLyricFoot = 64f, TallLyricTop = 16f, TallLyricFoot = 186f, LyricInsetMarked = 4f;
    /// <summary>The wide lyrics face's one-line head (title · artist) above its window.</summary>
    public const float WideLyricHead = 18f;
    /// <summary>Air the wide lyric window leaves to the column, so the time labels keep their reach clear of the transport row.</summary>
    public const float WideLyricSlack = 10f;
    /// <summary>The ticker's start bar gap.</summary>
    public const float TickerBarGap = 9f;

    // ── visualizer ──
    /// <summary>Bars per shape (the strip draws its own behind the row).</summary>
    public const int VizBarsStrip = 40, VizBarsWide = 14, VizBarsSquare = 22, VizBarsTall = 18;
    /// <summary>The visualizer's ground (the prototype's #0c0a10) and the accent glow rising from its foot.</summary>
    public static readonly ColorF VizGround = new(0.047f, 0.039f, 0.063f, 1f);
    public const float VizGlowFrom = 0.3f;

    // ── HUD ──
    /// <summary>The volume HUD: how long it stays, its plate, padding, gap, corner, glyph.</summary>
    public const float HudMs = 900f, HudPadX = 12f, HudPadY = 6f, HudGap = 8f, HudCorner = 16f, HudGlyph = 16f;
    public static readonly ColorF HudPlate = new(0f, 0f, 0f, 0.72f);

    // ── flyouts ──
    /// <summary>The flyouts' width, padding, row height (a 44-DIP target), tile gap, the shape tile's picture box and stroke.</summary>
    public const float FlyoutW = 284f, FlyoutPad = 6f, RowH = 44f, RowPadX = 8f, RowGap = 10f, TileGap = 4f, TileMinH = 44f, FaceTileMinH = 40f;
    public const float ShapePicW = 40f, ShapePicH = 34f, ShapeStroke = 1.5f, ShapeCorner = 2f, ShapeMinH = 5f, ShapeDivW = 12f, ShapeDivH = 16f;
    public const float TileRing = 1.5f, HeadPadX = 8f, HeadPadTop = 6f, HeadPadFoot = 4f, GroupPadTop = 8f, GroupPadFoot = 2f, NotePadFoot = 6f;
    public const float RuleW = 1f, FootPadTop = 8f, FootPadFoot = 4f, CheckW = 18f, TilePadY = 5f, TileTextGap = 3f;
    /// <summary>The shape tiles draw each shape at its default size (the prototype's 360, 392, 288, 288 widths).</summary>
    public const float StripTileW = 360f, WideTileW = 392f, SquareTileW = 288f, TallTileW = 288f, StripRatio = 6f, WideRatio = 2.4f, TallRatio = 0.5625f;
}
