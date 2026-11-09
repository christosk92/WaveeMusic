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

    // ── the seek rail: the played part a calm wave alone, the unplayed part the plain 3-DIP track after a gap, a rounded pill head between ──
    public const float Track = 3f, TrackCorner = 1.5f, TrackAlpha = 0.18f, Thumb = 12f;
    /// <summary>The wave: its box, its peak (3 DIP peak to peak), its wavelength, one sample a DIP; the gaps before and after the head.</summary>
    public const float WaveH = 10f, WaveAmp = 1.5f, WavePeriod = 18f, SampleStep = 1f, WaveGap = 2f, TrackGap = 3f;
    /// <summary>The drift: one wavelength per WaveMs, in WaveTickMs steps (about 15 a second); the play / pause ease and its frame-pace step.</summary>
    public const float WaveMs = 1100f, WaveTickMs = 66f, WaveStep = WavePeriod * WaveTickMs / WaveMs, EaseMs = 250f, EaseTickMs = 16f;
    /// <summary>The head: a 4 x 12 rounded pill, a quarter taller while the controls show, eased over HeadMs.</summary>
    public const float HeadW = 4f, HeadH = 12f, HeadHotScale = 1.25f, HeadMs = 150f;
    /// <summary>The strip's edge while the controls show: an 8-DIP rail at the window's foot, a 2-DIP peak to peak swing and an 8-DIP head, clear of
    /// the thumb's badge and the hover plate.</summary>
    public const float EdgeH = 8f, EdgeWaveAmp = 1f, EdgeHeadH = 8f;

    // ── the visualizer: three slow sine waves (accent, white, accent faint) drifting at 2.6, 3.7 and 5.1 s per period ──
    public const float VizPeriod = 140f, VizStroke = 2.5f, VizAmp = 0.45f;
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
