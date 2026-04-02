// Ported from BetterLyrics by Zhe Fang

using Microsoft.Graphics.Canvas.Text;
using Wavee.UI.WinUI.Controls.Lyrics.Helpers;

namespace Wavee.UI.WinUI.Controls.Lyrics.Extensions;

public static class CanvasTextLayoutExtensions
{
    public static void SetFontFamilyForText(this CanvasTextLayout? canvasTextLayout, string? text, string cjk, string latin)
    {
        if (canvasTextLayout == null || text == null) return;

        for (int i = 0; i < text.Length; i++)
        {
            canvasTextLayout.SetFontFamily(i, 1, LanguageHelper.IsCJK(text[i]) ? cjk : latin);
        }
    }
}
