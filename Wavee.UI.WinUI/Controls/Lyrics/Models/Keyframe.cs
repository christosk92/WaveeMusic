// Ported from BetterLyrics by Zhe Fang

namespace Wavee.UI.WinUI.Controls.Lyrics.Models;

public struct Keyframe<T>
{
    public T Value { get; }
    public double Duration { get; }

    public Keyframe(T value, double durationSeconds)
    {
        Value = value;
        Duration = durationSeconds;
    }
}
