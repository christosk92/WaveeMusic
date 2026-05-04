using System;

namespace Wavee.Controls.Lyrics.Core
{
    [Flags]
    public enum DirtyFlags
    {
        None           = 0,
        Layout         = 1 << 0,
        Palette        = 1 << 1,
        MouseScrolling = 1 << 2,
        PlayingLine    = 1 << 3,
    }
}
