// Ported from BetterLyrics by Zhe Fang

using System.Numerics;

namespace Wavee.UI.WinUI.Controls.Lyrics.Extensions;

public static class Vector2Extensions
{
    public static Vector2 AddX(this Vector2 v, float x) => new(v.X + x, v.Y);
    public static Vector2 AddY(this Vector2 v, float y) => new(v.X, v.Y + y);
}
