// Ported from BetterLyrics by Zhe Fang

using System.Collections.Generic;
using System.Linq;

namespace Wavee.UI.WinUI.Controls.Lyrics.Models;

public class LyricsLine : BaseLyrics
{
    public List<BaseLyrics> PrimarySyllables { get; set; } = [];

    public string PrimaryText { get; set; } = "";
    public string SecondaryText { get; set; } = "";
    public string TertiaryText { get; set; } = "";

    public new string Text => PrimaryText;
    public new int StartIndex = 0;

    public bool IsPrimaryHasRealSyllableInfo { get; set; } = false;
}
