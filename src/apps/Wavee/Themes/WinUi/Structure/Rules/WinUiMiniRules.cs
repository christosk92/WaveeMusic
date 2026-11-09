// ── Themes/WinUi/Structure/Rules/WinUiMiniRules.cs — the mini player's numbers in WinUI (#197) ─────────────────────────
//
// From the approved prototype (scratchpad miniplayer/index.html, .tk[data-theme="winui"]): 8-DIP card, 4-DIP art, 5-DIP button plates,
// round play disc, transport and save at 40 DIP or more, the hover window buttons at 32 like caption buttons; the 4-DIP seek track with the
// ringed thumb and its accent dot; rounded accent bars for the visualizer; the active lyric line marked by a 2-DIP accent bar.

using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

internal static class WinUiMiniRules
{
    public const int ArtColourSlot = 1;
    public const float Radius = 8f, ArtCorner = 4f, ThumbCorner = 4f, ButtonCorner = 5f, ChromeCorner = 7f, BadgeCorner = 9f, PlayCorner = 24f;
    public const float ButtonBox = 40f, ButtonGlyph = 16f, PlayBox = 44f, PlayBigBox = 48f, PlayGlyph = 18f, ChromeBox = 32f, ChromeGlyph = 16f;
    public const float Pad = 14f, Gap = 14f, StripPadStart = 10f, StripThumb = 40f, StripGap = 11f, Badge = 18f;
    /// <summary>The window's ground over its acrylic, the accent plates.</summary>
    public const float GroundAlpha = 0.8f, TintAlpha = 0.15f, Tint2Alpha = 0.28f;
    public const float VeilAlpha = 0.52f, ShadeAlpha = 0.72f;
    public const float HoverInMs = 120f, HoverOutDelayMs = 600f;
    /// <summary>The wide card shows its third line (album, or "Playing on") from this art-box height: below it the title, the artist, the seek
    /// row and the transport fill the column.</summary>
    public const float WideDetailFrom = 132f;

    /// <summary>The title per shape (14 strip, 15 wide, 16 square and tall), the lines under it, a label and the flyout heading.</summary>
    public static float TitleSize(MiniShape shape) => shape switch { MiniShape.Strip => 14f, MiniShape.Wide => 15f, _ => 16f };
    public const ushort TitleWeight = 600;
    public const float TitleLeading = 1.35f, MetaSize = 12f, MetaLeading = 17f, LabelSize = 13f, SmallSize = 11.5f, SmallLeading = 15f, HeadingSize = 14f;

    // ── the seek rail ──
    public const float Track = 4f, TrackCorner = 2f, TrackAlpha = 0.24f, Thumb = 14f, ThumbDot = 7f;

    // ── the visualizer: rounded accent bars, lit toward white at the top ──
    public const float VizInsetX = 0.09f, VizInsetY = 0.16f, VizGap = 0.035f, VizMin = 0.1f, VizTopCorner = 3f, VizFootCorner = 1f;
    public const float VizTopAlpha = 0.85f, VizAlpha = 0.95f;

    // ── lyrics: the strip ticker (its 2-DIP accent bar and two lines); the windows' type (the app's own lyrics view) per shape ──
    public const float TickerBar = 2f;
    public const float TickerNow = 13.5f, TickerNowLeading = 19f, TickerNext = 12f, TickerNextLeading = 17f;
    public static float LyricSize(MiniShape shape) => shape switch { MiniShape.Square => 19f, MiniShape.Tall => 21f, _ => 13.5f };
    /// <summary>The lyrics view's type in the mini window: the sizes above over the side pane's own (the view scales its rows, its
    /// rhythm and its wipe by it).</summary>
    public static float LyricScale(MiniShape shape) => LyricSize(shape) / WinUiPaneLyricsRules.LineSize;
}
