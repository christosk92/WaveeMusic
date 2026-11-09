// ── Themes/Classic/Structure/Rules/ClassicMiniRules.cs — the mini player's numbers in Classic (#197) ─────────────────────────
//
// From the approved prototype (scratchpad miniplayer/index.html, .tk[data-theme="classic"]): a warm wash tinted from the accent (the
// album's, with "Colour from album art": on by default, as in the app), 14-DIP card and flyout corners, 8-DIP art (10 on the strip thumb),
// round buttons, the heart on an album-tinted plate, the wave edge on the square's cover and behind the tall column, a 3-DIP track under a
// fill that ripples while the music moves, three slow sine waves for the visualizer, the active lyric line on a tinted plate.

using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

internal static class ClassicMiniRules
{
    public const int ArtColourSlot = 0;
    public const float Radius = 14f, ArtCorner = 8f, ThumbCorner = 10f, ButtonCorner = 20f, PlayCorner = 24f, ChromeCorner = 18f, BadgeCorner = 9f;
    public const float ButtonBox = 40f, ButtonGlyph = 16f, PlayBox = 44f, PlayBigBox = 48f, PlayGlyph = 18f, ChromeBox = 32f, ChromeGlyph = 16f;
    public const float Pad = 14f, Gap = 14f, StripPadStart = 10f, StripThumb = 40f, StripGap = 11f, Badge = 18f;
    /// <summary>The wash: the window's ground toward the accent by this much (light, dark); the accent plates.</summary>
    public const float WashLight = 0.09f, WashDark = 0.11f, TintAlpha = 0.15f, Tint2Alpha = 0.28f;
    public const float VeilAlpha = 0.52f, ShadeAlpha = 0.72f, OnDarkArtistAlpha = 0.85f;
    public const float HoverInMs = 120f, HoverOutDelayMs = 600f;

    public static float TitleSize(MiniShape shape) => shape switch { MiniShape.Strip => 14f, MiniShape.Wide => 15f, _ => 16f };
    public const float TitleLeading = 1.35f, MetaSize = 12f, MetaLeading = 17f, LabelSize = 13f, SmallSize = 11.5f, SmallLeading = 15f, HeadingSize = 14f;

    // ── the seek rail: a 3-DIP track, the fill a rippling line while playing (period 18, 10 tall, 2.2 stroke), a 3-DIP bar while resting ──
    public const float Track = 3f, TrackCorner = 1.5f, TrackAlpha = 0.18f, Thumb = 12f;
    public const float WaveH = 10f, WavePeriod = 18f, WaveAmp = 2.4f, WaveStroke = 2.2f, WaveMs = 1100f;
    /// <summary>The ripple's step rate: it drifts 16 DIP a second (24 device px at 150 %), so 25 steps move it about a pixel each, where a
    /// step per display frame (120 Hz) moved it a fifth of one (mini player power pass, 2026-11-09).</summary>
    public const float WaveHz = 25f;

    // ── the visualizer: three slow sine waves (accent, white, accent faint) drifting at 2.6, 3.7 and 5.1 s per period ──
    public const float VizPeriod = 140f, VizStroke = 2.5f, VizAmp = 0.45f;
    /// <summary>The three waves' common step rate: their band swell follows the analyser's ~25 Hz clock, and one shared cadence keeps the
    /// three drifts on the same turns instead of three interleaved ones (mini player power pass, 2026-11-09).</summary>
    public const float VizHz = 30f;
    public const float Viz0Top = 0.29f, Viz0H = 0.42f, Viz0Ms = 2600f;
    public const float Viz1Top = 0.33f, Viz1H = 0.34f, Viz1Ms = 3700f, Viz1Alpha = 0.55f;
    public const float Viz2Top = 0.24f, Viz2H = 0.52f, Viz2Ms = 5100f, Viz2Alpha = 0.35f;
    /// <summary>A wave's swing follows its third of the bands, never below this share.</summary>
    public const float VizMin = 0.35f;

    // ── lyrics: the ticker's 3-DIP accent bar; a column line on a 10-DIP rounded plate, the active plate tinted ──
    public const float TickerBar = 3f, LyricPad = 10f, LyricCorner = 10f, LyricLeading = 1.28f, LyricSpace = 0.28f, LyricGap = 2f;
    public const float TickerNow = 15f, TickerNowLeading = 20f, TickerNext = 12f, TickerNextLeading = 17f;
    public static float LyricSize(MiniShape shape) => shape switch { MiniShape.Square => 19f, MiniShape.Tall => 21f, _ => 13.5f };
    public const float InactiveOnDarkAlpha = 0.62f, ActiveOnDarkPlate = 0.16f;
}
