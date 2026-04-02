// Ported from BetterLyrics by Zhe Fang

using System;

namespace Wavee.UI.WinUI.Controls.Lyrics.Models;

public class BaseRenderLyrics : BaseLyrics
{
    public bool IsPlayingLastFrame { get; set; } = false;

    public BaseRenderLyrics(BaseLyrics baseLyrics)
    {
        Text = baseLyrics.Text;
        StartMs = baseLyrics.StartMs;
        EndMs = baseLyrics.EndMs;
        StartIndex = baseLyrics.StartIndex;
    }

    public bool GetIsPlaying(double currentMs) => StartMs <= currentMs && currentMs < (EndMs ?? int.MaxValue);
    public double GetPlayProgress(double currentMs) => Math.Clamp((currentMs - StartMs) / DurationMs, 0, 1);
}
