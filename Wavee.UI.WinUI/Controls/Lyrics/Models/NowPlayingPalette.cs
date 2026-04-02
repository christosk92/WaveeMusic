// Ported from BetterLyrics by Zhe Fang

using Microsoft.UI.Xaml;
using Windows.UI;

namespace Wavee.UI.WinUI.Controls.Lyrics.Models;

public struct NowPlayingPalette
{
    public Color SpectrumColor;
    public Color NonCurrentLineFillColor;
    public Color PlayedCurrentLineFillColor;
    public Color UnplayedCurrentLineFillColor;
    public Color PlayedTextStrokeColor;
    public Color UnplayedTextStrokeColor;
    public Color UnderlayColor;
    public Color AccentColor1;
    public Color AccentColor2;
    public Color AccentColor3;
    public Color AccentColor4;
    public ElementTheme ThemeType;

    public static NowPlayingPalette Default => new()
    {
        SpectrumColor = Color.FromArgb(255, 255, 255, 255),
        NonCurrentLineFillColor = Color.FromArgb(200, 255, 255, 255),
        PlayedCurrentLineFillColor = Color.FromArgb(255, 255, 255, 255),
        UnplayedCurrentLineFillColor = Color.FromArgb(150, 255, 255, 255),
        PlayedTextStrokeColor = Color.FromArgb(0, 0, 0, 0),
        UnplayedTextStrokeColor = Color.FromArgb(0, 0, 0, 0),
        UnderlayColor = Color.FromArgb(0, 0, 0, 0),
        AccentColor1 = Color.FromArgb(255, 100, 150, 255),
        AccentColor2 = Color.FromArgb(255, 150, 100, 255),
        AccentColor3 = Color.FromArgb(255, 100, 200, 200),
        AccentColor4 = Color.FromArgb(255, 200, 150, 100),
        ThemeType = ElementTheme.Dark,
    };
}
