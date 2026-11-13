// ── Themes/Common/Rules/CommonMiniPlayerRules.cs — the mini player's fixed geometry and keys (#197) ─────────────────────
//
// Theme numbers (button boxes, radii, paddings, type) live in each theme's MiniPlayerStyle; these are the shape-independent
// constants of the Common template, read off the approved prototype (scratchpad miniplayer/index.html): node keys, the window
// buttons, the strip overlay's fade run, the filling form's foot and hover layers, the tall column's insets, the lyric windows, the
// options panel.

using FluentGpu.Foundation;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Common;

internal static class CommonMiniPlayerRules
{
    public const string RootKey = "mini.root", FaceKey = "mini.face", ChromeKey = "mini.chrome", TransportKey = "mini.transport",
        SeekKey = "mini.seek", HoverKey = "mini.hover", RestKey = "mini.rest", MetaKey = "mini.meta", PlayKey = "mini.play",
        SaveKey = "mini.save", DeviceKey = "mini.device", OptionsKey = "mini.options", BackKey = "mini.back",
        EdgeKey = "mini.edge", HudKey = "mini.hud", LyricsKey = "mini.lyrics", TickerKey = "mini.ticker", BleedKey = "mini.bleed",
        VolumeKey = "mini.volume", ShapeKey = "mini.shape", TitleKey = "mini.title", ScrimKey = "mini.scrim", TimeKey = "mini.time";

    // ── text ──
    /// <summary>Text line gaps, the seek row's gap and height, a time's least width (its digits grow inward from the column's edge).</summary>
    public const float LineGap = 1f, SeekGap = 8f, SeekRowH = 22f, TimeMinW = 28f;
    /// <summary>The right time is a toggle: its hit reaches the 40-DIP floor with this slop above and below the row; in the wide card the
    /// reach moves up by TimeLiftWide (the title block over it is no target, the transport under it sits close on a short card).</summary>
    public const float TimeSlop = 9f, TimeLiftWide = 5f;
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
    /// <summary>The strip's hover controls: the fade run into the solid plate (DIP), the plate's lead, the plate's end padding.</summary>
    public const float StripFade = 44f, StripFadeLead = 48f, StripOverlayEnd = 8f;
    /// <summary>How many of the strip's six hover buttons (save, previous, play, next, device, options) its plate holds at a width: all six
    /// where they fit, else device dropped first, then save (the heart is drawn at rest; options, the panel's door, always stays).
    /// Never a button cut by the window's edge.</summary>
    public static int StripButtons(float width, float box) => Math.Clamp((int)((width - StripFadeLead - StripOverlayEnd) / box), 0, 6);
    /// <summary>The strip thumb grows with the strip's height (inset this much above and below) up to this side.</summary>
    public const float StripThumbInset = 8f, StripThumbMax = 96f;
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
    /// <summary>The wide card's column gap, the transport row's bleed into the padding.</summary>
    public const float WideColGap = 4f, CtrlBleedX = 6f, CtrlBleedBottom = 4f;
    /// <summary>How far a control row's leading heart bleeds into the padding: a plain glyph by the row's bleed (its box is empty air), a
    /// ringed one until its ring meets the text's edge, a plated one not at all (a plate left of the title's edge reads as misaligned).</summary>
    public static float HeartBleed(MiniHeart heart, float bleed) => heart switch
    {
        MiniHeart.Plate => 0f,
        MiniHeart.Ring => MathF.Min(bleed, BadgeRing),
        _ => bleed,
    };

    // ── the window buttons (options, back to Wavee: top right, on hover) ──
    /// <summary>The 32-DIP window buttons stand this far apart, so each keeps a 40-DIP reach with its slop, and this far from the corner.</summary>
    public const float ChromeGap = 8f, ChromeInset = 6f;
    /// <summary>Over imagery each stands on its own dark glass plate.</summary>
    public static readonly ColorF ChromeOnDark = new(0f, 0f, 0f, 0.55f);
    /// <summary>How far from the window's right edge the wide card's title keeps clear of the two buttons, at rest too (hovering never
    /// re-trims it): the pair, its inset and a little air.</summary>
    public static float ChromeReserve(float box) => ChromeInset + 2f * box + ChromeGap + ChromeAir;
    public const float ChromeAir = 4f;

    // ── the filling form (square, the wide card's Lyrics face) ──
    /// <summary>The resting title row (inset, foot inset), the object faces' and Classic cover's foot band.</summary>
    public const float SquareRestInset = 14f, SquareRestFoot = 13f, SquareObjectFoot = 62f, SquareCoverFoot = 66f;
    /// <summary>The air between an object face's tile and the band its title stands on.</summary>
    public const float SquareObjectGap = 8f;
    /// <summary>The hover controls' foot inset and the gap between the transport row and the seek row under it (the right time's 40-DIP
    /// reach clears the device button); the risen title keeps this much air over the controls and under the window buttons.</summary>
    public const float SquareHoverFoot = 10f, SquareHoverRowGap = 6f, SquareTitleAir = 6f;
    /// <summary>The deeper foot scrim under the hover controls: its alpha at the foot (the shade's colour over imagery) and how far above the
    /// title it starts; the short top scrim under the window buttons over imagery: its height and alpha.</summary>
    public const float HoverScrimAlpha = 0.86f, HoverScrimLead = 28f, TopScrimH = 64f, TopScrimAlpha = 0.42f;
    /// <summary>The resting shade starts at this fraction of the height (its angle: top to bottom).</summary>
    public const float ShadeFrom = 0.45f, ShadeAngle = 90f;
    /// <summary>The ground's corner glow runs from the top right toward the bottom left and is gone this far along.</summary>
    public const float GlowAngle = 135f, GlowEnd = 0.62f;
    /// <summary>The Zune cover bleed's inset (it starts left of the window).</summary>
    public const float BleedX = -16f, BleedY = 4f, BleedAlpha = 0.92f;

    // ── tall ──
    /// <summary>The tall column's padding and gap; the volume row's gap.</summary>
    public const float TallPadTop = 14f, TallPadFoot = 12f, TallGap = 10f, TallPanelGap = 6f;
    /// <summary>What the tall column's fixed rows take (title block, seek, transport, gaps and padding), with and without the volume row: the
    /// art takes the rest.</summary>
    public const float TallFixed = 196f, TallFixedRoomy = 222f;
    /// <summary>The immersive scrim's stops: dark at the top (under the window buttons), clear through the middle, dark under the panel.</summary>
    public const float ScrimTopAlpha = 0.38f, ScrimTopEnd = 0.18f, ScrimClearEnd = 0.36f, ScrimFootFrom = 0.62f, ScrimFootAlpha = 0.82f;
    /// <summary>Classic's tinted band behind the column's top (a share of the height).</summary>
    public const float WaveBandShare = 0.62f;
    /// <summary>The volume row: its height, the percent's width.</summary>
    public const float VolumeRowH = 24f, VolumePctW = 26f;

    // ── lyrics ──
    /// <summary>Where the active line is held in each window (a share of its height) and the window's top and foot fades (shares).</summary>
    public const float LyricAnchorSquare = 0.42f, LyricAnchorTall = 0.4f, LyricFadeTop = 0.22f, LyricFadeFoot = 0.28f;
    /// <summary>The lyric windows' insets: the square's (top, foot) and the tall column's top (clear of the window buttons; its foot is the
    /// column's own rows).</summary>
    public const float SquareLyricTop = 14f, SquareLyricFoot = 64f, TallLyricTop = 44f;
    /// <summary>A lyric window's least soft foot (within a marked line's own padding): what shows of the next line, or of a line taller
    /// than the window, fades out and is never cut.</summary>
    public const float LyricFadeMin = 4f;
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

    // ── the options panel ──
    /// <summary>The panel's key and the shortcut its "Back to Wavee" row shows.</summary>
    public const string PanelKey = "mini.panel", BackAccelerator = "Ctrl+Alt+M";
    /// <summary>The panel: its width, top and foot padding, the inset of its heads, tiles and rows, the gap under a head and under a
    /// section, a row's height (a 44-DIP target) and gap, the Back row's inset from the card.</summary>
    public const float PanelW = 320f, PanelPadTop = 14f, PanelPadFoot = 4f, PanelInset = 14f, PanelHeadGap = 6f, PanelSectionGap = 14f,
        PanelRowH = 44f, PanelRowGap = 10f, PanelBackInset = 4f;
    /// <summary>The tiles: the gap between them, a shape tile's and a face tile's height, the chosen ring, the padding, the gap between a
    /// picture and its name; four faces to a row.</summary>
    public const float TileGap = 4f, ShapeTileMinH = 64f, FaceTileMinH = 40f, TileRing = 1.5f, TilePadY = 6f, TileTextGap = 3f;
    public const int FaceCols = 4;
    /// <summary>A shape tile's picture box and stroke; each shape is drawn at the size it opens at (360, 392, 288 and 288 wide) over one
    /// divisor, so the four read to scale.</summary>
    public const float ShapePicW = 40f, ShapePicH = 34f, ShapeStroke = 1.5f, ShapeCorner = 2f, ShapeMinH = 5f, ShapeDivW = 12f, ShapeDivH = 16f;
    public const float StripTileW = 360f, WideTileW = 392f, SquareTileW = 288f, TallTileW = 288f, StripRatio = 6f, WideRatio = 2.4f, TallRatio = 0.5625f;
    /// <summary>A hairline (the panel's rules, WinUI's thumb ring).</summary>
    public const float RuleW = 1f;
}
