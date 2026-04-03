using System;

namespace Wavee.Controls.Lyrics.Enums
{
    public enum DockPlacement
    {
        Top,
        Bottom
    }

    public static class DockPlacementExtensions
    {
        public static WindowPixelSampleMode ToWindowPixelSampleMode(this DockPlacement placement)
        {
            return placement switch
            {
                DockPlacement.Top => WindowPixelSampleMode.BelowWindow,
                DockPlacement.Bottom => WindowPixelSampleMode.AboveWindow,
                _ => throw new ArgumentOutOfRangeException(nameof(placement), placement, null)
            };
        }
    }
}
