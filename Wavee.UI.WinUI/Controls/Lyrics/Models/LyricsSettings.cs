// Simplified settings ported from BetterLyrics — adapted for Wavee's side panel

namespace Wavee.UI.WinUI.Controls.Lyrics.Models;

/// <summary>
/// Effect settings for lyrics rendering (ported from BetterLyrics LyricsEffectSettings).
/// </summary>
public class LyricsEffectSettings
{
    public WordByWordEffectMode WordByWordEffectMode { get; set; } = WordByWordEffectMode.Auto;

    public bool IsLyricsBlurEffectEnabled { get; set; } = true;
    public bool IsLyricsFadeOutEffectEnabled { get; set; } = true;
    public bool IsLyricsOutOfSightEffectEnabled { get; set; } = true;

    public bool IsLyricsGlowEffectEnabled { get; set; } = true;
    public LyricsEffectScope LyricsGlowEffectScope { get; set; } = LyricsEffectScope.LongDurationSyllable;
    public int LyricsGlowEffectLongSyllableDuration { get; set; } = 700;
    public bool IsLyricsGlowEffectAmountAutoAdjust { get; set; } = true;
    public int LyricsGlowEffectAmount { get; set; } = 8;

    public bool IsLyricsScaleEffectEnabled { get; set; } = true;
    public int LyricsScaleEffectLongSyllableDuration { get; set; } = 700;
    public bool IsLyricsScaleEffectAmountAutoAdjust { get; set; } = true;
    public int LyricsScaleEffectAmount { get; set; } = 115;

    public bool IsLyricsFloatAnimationEnabled { get; set; } = true;
    public bool IsLyricsFloatAnimationAmountAutoAdjust { get; set; } = true;
    public int LyricsFloatAnimationAmount { get; set; } = 8;
    public int LyricsFloatAnimationDuration { get; set; } = 450;

    public EasingType LyricsScrollEasingType { get; set; } = EasingType.Quad;
    public EaseMode LyricsScrollEasingMode { get; set; } = EaseMode.Out;
    public int LyricsScrollDuration { get; set; } = 500;
    public int LyricsScrollTopDuration { get; set; } = 500;
    public int LyricsScrollBottomDuration { get; set; } = 500;
    public int LyricsScrollTopDelay { get; set; } = 0;
    public int LyricsScrollBottomDelay { get; set; } = 0;

    public bool IsFanLyricsEnabled { get; set; } = false;
    public int FanLyricsAngle { get; set; } = 30;

    public bool Is3DLyricsEnabled { get; set; } = false;

    public bool IsLyricsBrethingEffectEnabled { get; set; } = false;
    public int LyricsBreathingIntensity { get; set; } = 80;
}

/// <summary>
/// Style settings for lyrics rendering (ported from BetterLyrics LyricsStyleSettings).
/// </summary>
public class LyricsStyleSettings
{
    public bool IsDynamicLyricsFontSize { get; set; } = true;
    public int PhoneticLyricsFontSize { get; set; } = 12;
    public int OriginalLyricsFontSize { get; set; } = 26;
    public int TranslatedLyricsFontSize { get; set; } = 12;

    public int PhoneticLyricsOpacity { get; set; } = 60;
    public int PlayedOriginalLyricsOpacity { get; set; } = 100;
    public int UnplayedOriginalLyricsOpacity { get; set; } = 70;
    public int TranslatedLyricsOpacity { get; set; } = 60;

    public TextAlignmentType LyricsAlignmentType { get; set; } = TextAlignmentType.Left;
    public int LyricsFontStrokeWidth { get; set; } = 0;
    public LyricsFontWeight LyricsFontWeight { get; set; } = LyricsFontWeight.Bold;
    public double LyricsLineSpacingFactor { get; set; } = 0.5;
    public string LyricsCJKFontFamily { get; set; } = "Microsoft YaHei";
    public string LyricsWesternFontFamily { get; set; } = "Segoe UI Variable Display";
    public int PlayingLineTopOffset { get; set; } = 38; // % of canvas height
}
