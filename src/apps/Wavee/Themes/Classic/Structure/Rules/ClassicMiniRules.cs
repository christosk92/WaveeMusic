// ── Themes/Classic/Structure/Rules/ClassicMiniRules.cs — the mini player's numbers in Classic (#197) ─────────────────────────
//
// From the approved prototype (scratchpad miniplayer/index.html, .tk[data-theme="classic"]): a warm wash tinted from the accent (the
// album's, with "Colour from album art": on by default, as in the app), 14-DIP card and flyout corners, 8-DIP art (10 on the strip thumb),
// round buttons, the heart on an album-tinted plate, a swell behind the tall column's object faces, a 3-DIP track under a
// fill that ripples while the music moves, three slow sine waves for the visualizer, the active lyric line on a tinted plate.

using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

internal static class ClassicMiniRules
{
    public const int ArtColourSlot = 0;
    public const float Radius = 14f, ArtCorner = 8f, ThumbCorner = 10f, ButtonCorner = 20f, PlayCorner = 24f, ChromeCorner = 16f, BadgeCorner = 9f;
    public const float ButtonBox = 40f, ButtonGlyph = 16f, PlayBox = 44f, PlayBigBox = 48f, PlayGlyph = 18f, ChromeBox = 32f, ChromeGlyph = 16f;
    public const float Pad = 14f, Gap = 14f, StripPadStart = 10f, StripThumb = 40f, StripGap = 11f, Badge = 18f;
    /// <summary>The wash: the window's ground toward the accent by this much (light, dark); the accent plates.</summary>
    public const float WashLight = 0.09f, WashDark = 0.11f, TintAlpha = 0.15f, Tint2Alpha = 0.28f;
    public const float ShadeAlpha = 0.72f, OnDarkArtistAlpha = 0.85f;
    public const float HoverInMs = 120f, HoverOutDelayMs = 600f;
    /// <summary>The wide card shows its third line (album, or "Playing on") from this art-box height: below it the title, the artist, the seek
    /// row and the transport fill the column.</summary>
    public const float WideDetailFrom = 132f;

    public static float TitleSize(MiniShape shape) => shape switch { MiniShape.Strip => 14f, MiniShape.Wide => 15f, _ => 16f };
    public const float TitleLeading = 1.35f, MetaSize = 12f, MetaLeading = 17f, LabelSize = 13f, SmallSize = 11.5f, SmallLeading = 15f, HeadingSize = 14f;

    // ── the seek rail: the played part a calm wave alone, the unplayed part the plain 3-DIP track after a gap, a rounded pill head between ──
    public const float Track = 3f, TrackCorner = 1.5f, TrackAlpha = 0.18f, Thumb = 12f;
    /// <summary>The wave: its box, its peak (3 DIP peak to peak), its wavelength, two samples a DIP (one a DIP stair-stepped the slopes at 100 %); the gaps before and after the head.</summary>
    public const float WaveH = 10f, WaveAmp = 1.5f, WavePeriod = 18f, SampleStep = 0.5f, WaveGap = 3.5f, TrackGap = 3f;
    /// <summary>The drift: one wavelength per WaveMs, in WaveTickMs steps (about 15 a second); the play / pause ease and its frame-pace step.</summary>
    public const float WaveMs = 1100f, WaveTickMs = 66f, WaveStep = WavePeriod * WaveTickMs / WaveMs, EaseMs = 250f, EaseTickMs = 16f;
    /// <summary>The head: a 4 x 12 rounded pill, a quarter taller while the controls show, eased over HeadMs.</summary>
    public const float HeadW = 4f, HeadH = 12f, HeadHotScale = 1.25f, HeadMs = 150f;

    // ── the visualizer: three slow sine waves (accent, white, accent faint) drifting at 2.6, 3.7 and 5.1 s per period ──
    /// <summary>The wavelength (DIP), the stroke and the swing (a share of a wave's band). The waves centre on the box's optical centre
    /// (<see cref="VizCentre"/>, a little above the middle), each in a band of the box's height.</summary>
    public const float VizPeriod = 150f, VizStroke = 3.5f, VizAmp = 0.45f, VizCentre = 0.46f;
    public const float Viz0H = 0.5f, Viz0Ms = 2600f;
    public const float Viz1H = 0.38f, Viz1Ms = 3700f, Viz1Alpha = 0.6f;
    public const float Viz2H = 0.66f, Viz2Ms = 5100f, Viz2Alpha = 0.38f;
    /// <summary>A wave's swing follows its third of the bands, never below this share: its resting swell, visible while the music is quiet or paused.</summary>
    public const float VizMin = 0.55f;

    // ── lyrics: the strip ticker's 3-DIP accent bar and its two lines; the windows' type (the app's own lyrics view) per shape ──
    public const float TickerBar = 3f;
    public const float TickerNow = 15f, TickerNowLeading = 20f, TickerNext = 12f, TickerNextLeading = 17f;
    /// <summary>The lyrics windows' type follows the window's width (a share of it, between a floor and a ceiling), so a line holds four or five
    /// words in the narrowest column and the type stops growing where a wide window would only make lines longer.</summary>
    public const float LyricWidthShare = 0.074f, LyricMin = 15f, LyricMax = 22f;
    public static float LyricSize(float width) => Math.Clamp(width * LyricWidthShare, LyricMin, LyricMax);
    /// <summary>The lyrics view's type in the mini window: the size above over the side pane's own (the view scales its rows, its
    /// rhythm and its wipe by it).</summary>
    public static float LyricScale(MiniShape shape, float width) => LyricSize(width) / Wavee.Lyrics.Surface.Timed(false).FontSize;
}
