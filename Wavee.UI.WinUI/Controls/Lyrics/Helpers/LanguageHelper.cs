// Simplified from BetterLyrics — only IsCJK needed for font selection

namespace Wavee.UI.WinUI.Controls.Lyrics.Helpers;

public static class LanguageHelper
{
    public static bool IsCJK(char ch)
    {
        // CJK Unified Ideographs
        if (ch >= 0x4E00 && ch <= 0x9FFF) return true;
        // CJK Unified Ideographs Extension A
        if (ch >= 0x3400 && ch <= 0x4DBF) return true;
        // CJK Compatibility Ideographs
        if (ch >= 0xF900 && ch <= 0xFAFF) return true;
        // Hiragana
        if (ch >= 0x3040 && ch <= 0x309F) return true;
        // Katakana
        if (ch >= 0x30A0 && ch <= 0x30FF) return true;
        // Hangul Syllables
        if (ch >= 0xAC00 && ch <= 0xD7AF) return true;
        // Hangul Jamo
        if (ch >= 0x1100 && ch <= 0x11FF) return true;
        // CJK Symbols and Punctuation
        if (ch >= 0x3000 && ch <= 0x303F) return true;
        // Fullwidth Forms
        if (ch >= 0xFF00 && ch <= 0xFFEF) return true;
        // Bopomofo
        if (ch >= 0x3100 && ch <= 0x312F) return true;

        return false;
    }

    public static bool IsCJK(string text)
    {
        foreach (var ch in text)
        {
            if (IsCJK(ch)) return true;
        }
        return false;
    }
}
