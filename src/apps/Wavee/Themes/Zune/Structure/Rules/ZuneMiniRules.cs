// ── Themes/Zune/Structure/Rules/ZuneMiniRules.cs — the mini player's numbers in Zune (#197) ─────────────────────────
//
// From the approved prototype (scratchpad miniplayer/index.html, .tk[data-theme="zune"]): square card, art, chrome, flip pill and badge
// (0 radius), round transport and an outlined round heart (the prototype's --br 50 %), the pink accent, thin lowercase type that bleeds off
// the edge under a fade (owner rule: never shrunk, never cut), a 2-DIP seek line with a small thumb in the ink, a thin centred waveform in
// pink and white, the active lyric line big and pink.

using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

internal static class ZuneMiniRules
{
    public const int ArtColourSlot = 2;
    public const float Radius = 0f, ArtCorner = 0f, ThumbCorner = 0f, ChromeCorner = 0f, BadgeCorner = 0f;
    /// <summary>Round transport and heart (the prototype's --br: 50 %).</summary>
    public const float ButtonCorner = 20f, PlayCorner = 24f;
    public const float ButtonBox = 40f, ButtonGlyph = 16f, PlayBox = 44f, PlayBigBox = 48f, PlayGlyph = 18f, ChromeBox = 32f, ChromeGlyph = 16f;
    public const float Pad = 14f, Gap = 14f, StripPadStart = 10f, StripThumb = 40f, StripGap = 11f, Badge = 18f;
    public const float TintAlpha = 0.15f, Tint2Alpha = 0.28f, VeilAlpha = 0.52f, ShadeAlpha = 0.72f;
    public const float HoverInMs = 120f, HoverOutDelayMs = 600f;
    /// <summary>The wide card shows its third line (album, or "Playing on") from this art-box height: below it the title, the artist, the seek
    /// row and the transport fill the column.</summary>
    public const float WideDetailFrom = 142f;

    /// <summary>The title's display size per shape (strip 23, wide 27, square 28, tall 30): large and light, never shrunk to fit; it bleeds
    /// past the column to the window's edge under a fade instead (owner rule). The fade's run.</summary>
    public static float TitleSize(MiniShape shape) => shape switch { MiniShape.Strip => 23f, MiniShape.Square => 28f, MiniShape.Tall => 30f, _ => 27f };
    public const float TitleLeading = 1.15f, TitleFade = 44f;
    public const float MetaSize = 12f, MetaLeading = 17f, LabelSize = 13f, SmallSize = 11.5f, SmallLeading = 15f, HeadingSize = 24f, HeadingLeading = 30f;
    public const ushort ArtistWeight = 600;

    /// <summary>The artist across the square's cover: 28 % of the window, between 56 and 110 DIP.</summary>
    public static float BleedSize(float width) => Math.Clamp(width * 0.28f, 56f, 110f);
    public const float BleedTracking = -20f;

    // ── the seek rail ──
    public const float Track = 2f, TrackAlphaLight = 0.16f, TrackAlphaDark = 0.22f, Thumb = 10f;

    // ── the visualizer: thin bars growing both ways from the box's middle line, every third white ──
    /// <summary>The waveform's insets (shares of the box) and a bar's width (DIP).</summary>
    public const float VizInsetX = 0.08f, VizInsetY = 0.1f, VizBar = 2f;
    public const int VizWhiteEvery = 3;
    /// <summary>The resting shape (a bar's span with its band silent, as a share of the full span): a swell rising from the ends to the middle, so a
    /// pause draws a calm lens of light, not a row of ticks.</summary>
    public const float VizRestLow = 0.05f, VizRestSwell = 0.22f;
    /// <summary>Bar <paramref name="i"/> of <paramref name="n"/> at rest (see <see cref="VizRestLow"/>).</summary>
    public static float VizRest(int i, int n) => VizRestLow + VizRestSwell * MathF.Sin(MathF.PI * (i + 0.5f) / Math.Max(1, n));

    // ── lyrics ──
    public const float TickerNow = 20f, TickerNowLeading = 25f, TickerNext = 13f, TickerNextLeading = 17f, TickerBleed = 12f;
    /// <summary>Zune's lyric type is display type: per shape, never made smaller to fit a narrow window (a long line wraps instead).</summary>
    public static float LyricSize(MiniShape shape) => shape switch { MiniShape.Square => 22.4f, MiniShape.Tall => 24.8f, _ => 16f };
    /// <summary>The lyrics view's type in the mini window: the sizes above over the side pane's own (the view scales its rows, its
    /// rhythm and its wipe by it). The width does not move it.</summary>
    public static float LyricScale(MiniShape shape, float width) => LyricSize(shape) / Wavee.Lyrics.Surface.Timed(false).FontSize;
    public const float OnDarkLift = 0.45f;
}
