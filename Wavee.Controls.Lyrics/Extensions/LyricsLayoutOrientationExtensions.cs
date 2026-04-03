using Wavee.Controls.Lyrics.Enums;
using Microsoft.UI.Xaml.Controls;
using System;

namespace Wavee.Controls.Lyrics.Extensions
{
    public static class LyricsLayoutOrientationExtensions
    {
        extension(LyricsLayoutOrientation orientation)
        {
            public Orientation ToOrientation() => orientation switch
            {
                LyricsLayoutOrientation.Horizontal => Orientation.Horizontal,
                LyricsLayoutOrientation.Vertical => Orientation.Vertical,
                _ => throw new ArgumentOutOfRangeException(nameof(orientation)),
            };

            public Orientation ToOrientationInverse() => orientation switch
            {
                LyricsLayoutOrientation.Horizontal => Orientation.Vertical,
                LyricsLayoutOrientation.Vertical => Orientation.Horizontal,
                _ => throw new ArgumentOutOfRangeException(nameof(orientation)),
            };
        }
    }
}
