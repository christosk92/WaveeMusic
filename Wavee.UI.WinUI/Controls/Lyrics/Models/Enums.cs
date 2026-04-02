// Ported from BetterLyrics by Zhe Fang

using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using System;
using Windows.UI.Text;

namespace Wavee.UI.WinUI.Controls.Lyrics.Models;

public enum EasingType
{
    Linear, SmoothStep, Sine, Quad, Cubic, Quart, Quint, Expo, Circle, Back, Elastic, Bounce,
}

public enum EaseMode { In, Out, InOut }

public enum WordByWordEffectMode { Auto, Never, Always }

public enum LyricsEffectScope { LongDurationSyllable, LineStartToCurrentChar }

public enum LyricsFontWeight
{
    Thin, ExtraLight, Light, SemiLight, Normal, Medium, SemiBold, Bold, ExtraBold, Black, ExtraBlack,
}

public enum TextAlignmentType { Left, Center, Right }

public static class LyricsFontWeightExtensions
{
    public static FontWeight ToFontWeight(this LyricsFontWeight weight) => weight switch
    {
        LyricsFontWeight.Thin => FontWeights.Thin,
        LyricsFontWeight.ExtraLight => FontWeights.ExtraLight,
        LyricsFontWeight.Light => FontWeights.Light,
        LyricsFontWeight.SemiLight => FontWeights.SemiLight,
        LyricsFontWeight.Normal => FontWeights.Normal,
        LyricsFontWeight.Medium => FontWeights.Medium,
        LyricsFontWeight.SemiBold => FontWeights.SemiBold,
        LyricsFontWeight.Bold => FontWeights.Bold,
        LyricsFontWeight.ExtraBold => FontWeights.ExtraBold,
        LyricsFontWeight.Black => FontWeights.Black,
        LyricsFontWeight.ExtraBlack => FontWeights.ExtraBlack,
        _ => throw new ArgumentOutOfRangeException(nameof(weight), weight, null),
    };
}

public static class TextAlignmentTypeExtensions
{
    public static HorizontalAlignment ToHorizontalAlignment(this TextAlignmentType t) => t switch
    {
        TextAlignmentType.Left => HorizontalAlignment.Left,
        TextAlignmentType.Center => HorizontalAlignment.Center,
        TextAlignmentType.Right => HorizontalAlignment.Right,
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    public static CanvasHorizontalAlignment ToCanvasHorizontalAlignment(this TextAlignmentType t) => t switch
    {
        TextAlignmentType.Left => CanvasHorizontalAlignment.Left,
        TextAlignmentType.Center => CanvasHorizontalAlignment.Center,
        TextAlignmentType.Right => CanvasHorizontalAlignment.Right,
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };
}
