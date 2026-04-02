// Ported from BetterLyrics by Zhe Fang

using System.Collections.Generic;

namespace Wavee.UI.WinUI.Controls.Lyrics.Models;

public class RenderLyricsSyllable : BaseRenderLyrics
{
    public List<RenderLyricsChar> ChildrenRenderLyricsChars { get; set; } = [];

    public RenderLyricsSyllable(BaseLyrics lyricsSyllable) : base(lyricsSyllable) { }
}
