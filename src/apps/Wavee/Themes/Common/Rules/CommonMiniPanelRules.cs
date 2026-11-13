// ── Themes/Common/Rules/CommonMiniPanelRules.cs — the mini player's options panel: keys and geometry (#197) ───────────────
//
// The ⋯ panel (CommonMiniPlayer.Flyouts.cs) is a compact card beside a window that can be 232 DIP wide: a 4 x 2 grid of face pictures,
// one row of shape pictograms and two switches. Its width is what four 58-DIP tiles and their gaps need inside the card's inset. The
// theme decides the type (its small voice), the corners (its art corner, capped small) and the switch; these are the fixed numbers.

using FluentGpu.Foundation;

namespace Wavee.Themes.Common;

internal static class CommonMiniPanelRules
{
    /// <summary>The panel's key; a face tile is <c>mini.panel.face:&lt;index&gt;</c>, a shape tile <c>mini.panel.shape:&lt;Shape&gt;</c>, the
    /// switches <c>mini.panel.ontop</c> and <c>mini.panel.artcolour</c>.</summary>
    public const string PanelKey = "mini.panel", FaceTileKey = "mini.panel.face:", ShapeTileKey = "mini.panel.shape:",
        OnTopKey = "mini.panel.ontop", ArtColourKey = "mini.panel.artcolour",
        // A deck face's picture inside its tile: the inspector's lint allows the deck's shrunken label type under this key.
        DeckPictureKey = "mini.panel.deck:";

    /// <summary>The card: its inset (sides), top and foot padding, the gap under a section head and under a section, a switch row's height (a
    /// 44-DIP target) and the gap between its label and the switch.</summary>
    public const float PanelInset = 10f, PanelPadTop = 12f, PanelPadFoot = 4f, HeadGap = 6f, SectionGap = 12f, RowH = 44f, RowGap = 10f;
    /// <summary>The panel's width: four tiles and three gaps inside the inset (4 x 58 + 3 x 4 + 2 x 10).</summary>
    public const float PanelW = FaceCols * TileW + (FaceCols - 1) * TileGap + 2f * PanelInset;
    /// <summary>A switch in the album's accent: its hover and pressed fills are the accent at these alphas (the Fluent accent ladder's
    /// secondary and tertiary).</summary>
    public const float SwitchHoverAlpha = 0.9f, SwitchPressedAlpha = 0.8f;
    /// <summary>The hairline between the pictures and the switches.</summary>
    public const float RuleW = 1f;

    /// <summary>A tile: its width, the gap between tiles (both ways), its padding above the ring and below the name, the gap between the ring
    /// and the name; four to a row.</summary>
    public const float TileW = 58f, TileGap = 4f, TilePadTop = 0f, TilePadFoot = 4f, TileTextGap = 3f;
    public const int FaceCols = 4;
    /// <summary>The picture: its side, the ring around it (the chosen tile's, in the accent) and the air between ring and picture. The
    /// ring box is the tile's width.</summary>
    public const float PictureSide = 52f, Ring = 2f, RingGap = 1f;
    /// <summary>A picture's corner is the theme's art corner, never more than this (a small tile reads as a tile, not a pill).</summary>
    public const float PictureCornerMax = 6f;
    /// <summary>A tile's hover plate rounds this much beyond the picture's corner (none where the theme is square).</summary>
    public const float PlateCornerExtra = 3f;

    // ── face pictures ──
    /// <summary>Lyrics: the cover under a dark tint, three line bars (their inset from the left, widths as shares of the picture, height, gap and the dim
    /// lines' alpha).</summary>
    public static readonly ColorF LyricVeil = new(0f, 0f, 0f, 0.58f);
    public const float LyricInset = 8f, LyricBarH = 4f, LyricBarGap = 5f, LyricBar0 = 0.5f, LyricBar1 = 0.7f, LyricBar2 = 0.4f, LyricDimAlpha = 0.45f;
    /// <summary>Canvas: a small plate in the bottom-right corner with the video glyph (its side, glyph, inset).</summary>
    public const float CanvasMark = 18f, CanvasGlyph = 10f, CanvasInset = 4f;
    public static readonly ColorF CanvasPlate = new(0f, 0f, 0f, 0.6f);
    /// <summary>Visualizer: five accent bars standing on the foot (heights as shares of the picture), their width and gap.</summary>
    public static readonly float[] VizBars = [0.34f, 0.62f, 0.86f, 0.5f, 0.26f];
    public const float VizBarW = 5f, VizBarGap = 3f, VizFoot = 9f;

    // ── shape pictograms ──
    /// <summary>A shape tile's picture box (as wide as a face picture, shorter), the outline's stroke and corner. Each shape is drawn at
    /// the size it opens at over ONE divisor, so the four read to scale against each other.</summary>
    public const float ShapePicH = 38f, ShapeStroke = 1.5f, ShapeCorner = 2f, ShapeMinH = 4f, ShapeDiv = 14.5f;
    public const float StripW = 360f, WideW = 392f, SquareW = 288f, TallW = 288f, StripRatio = 6f, WideRatio = 2.4f, TallRatio = 0.5625f;
}
