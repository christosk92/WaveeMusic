using System.Collections.Generic;

namespace Wavee.Controls.Lyrics.Models.Lyrics
{
    public class RenderLyricsSyllable : BaseRenderLyrics
    {
        public List<RenderLyricsChar> ChildrenRenderLyricsChars { get; set; } = [];

        public RenderLyricsSyllable(BaseLyrics lyricsSyllable) : base(lyricsSyllable) { }
    }
}
