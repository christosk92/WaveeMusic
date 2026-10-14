// ── Themes/Common/Rules/CommonDiagnosticsRules.cs — the numbers of the Common diagnostics pages (themes plan 6.5, T11c) ─────────────
//
// From `Screens\Diagnostics.UI.cs` (the shared chrome: header, frame, card, row, chip, status), `Diagnostics.Capture.UI.cs`, `Diagnostics.Tiles.cs`,
// `Diagnostics.Scroll.cs` and `Diagnostics.Evidence.cs`. Every literal look of `CommonDiagnostics*.cs` is here (WVTH003 applies to Common like a
// structure).

namespace Wavee.Themes.Common;

/// <summary>The diagnostics pages' geometry.</summary>
internal static class CommonDiagnosticsRules
{
    /// <summary>The scroll column's widest extent.</summary>
    public const float PageMaxWidth = 1000f;

    /// <summary>The label column of a Runtime-style row and of a Connect-style (wide) row.</summary>
    public const float RuntimeLabelWidth = 132f, ConnectLabelWidth = 160f;

    /// <summary>The page header's glyph.</summary>
    public const float HeaderGlyphSize = 22f;

    /// <summary>The gap between a page's cards.</summary>
    public const float PageGap = 12f;

    /// <summary>A card's gap and its inner padding.</summary>
    public const float CardGap = 6f, CardPadding = 12f;

    /// <summary>A twelve-point line (a title, a label, a value) and an eleven-point mono time.</summary>
    public const float TextSize = 12f, MonoSize = 11f;

    /// <summary>A status block's glyph and its inner column gap.</summary>
    public const float StatusGlyphSize = 18f, StatusGap = 4f;

    /// <summary>The margin above a separator rule, by <c>DiagGap</c>.</summary>
    public const float GapHairline = 2f, GapTight = 4f, GapNormal = 6f, GapWide = 8f;

    /// <summary>The margin below a separator rule.</summary>
    public const float SeparatorBelow = 6f;

    /// <summary>A link over its caption: the gap and the room above.</summary>
    public const float LinkGap = 2f, LinkTop = 6f;

    /// <summary>A presence chip's padding.</summary>
    public const float ChipSideX = 6f, ChipSideY = 1f;

    /// <summary>The wire check's text boxes and result columns.</summary>
    public const float WireBoxWidth = 460f, WireOpWidth = 190f, WireStatusWidth = 60f, WireBytesWidth = 70f, WireTableGap = 6f;

    /// <summary>A capture anomaly's kind column.</summary>
    public const float AnomalyKindWidth = 120f;

    /// <summary>A capture root's time column; the expanded tree's time column, indent and depth step.</summary>
    public const float RootTimeWidth = 92f, TreeTimeWidth = 82f, TreeInsetLeft = 24f, TreeInsetTop = 2f, TreeInsetBottom = 8f, TreeEmptyBottom = 6f, TreeDepthStep = 16f, TreeGap = 2f;

    /// <summary>The scroll probe's profile combo.</summary>
    public const float ProfileComboWidth = 220f;

    /// <summary>The tiles card's pass table: kind and target columns, the reasons block's gap.</summary>
    public const float PassKindWidth = 120f, PassTargetWidth = 100f, ReasonsGap = 2f;

    /// <summary>The evidence card's columns: the pixel label, the x and y boxes, the tag box; the pixel table's columns.</summary>
    public const float PixelLabelWidth = 48f, PixelBoxWidth = 80f, TagBoxWidth = 200f;

    /// <summary>The evidence pixel table: index, kind, alpha and feather, tile, raster columns.</summary>
    public const float HitIndexWidth = 28f, HitKindWidth = 64f, HitAlphaWidth = 44f, HitTileWidth = 56f, HitRasterWidth = 72f;
}
