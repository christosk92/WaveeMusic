using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Wavee.UI.WinUI.Helpers.Application;

/// <summary>
/// Small helpers for keeping caption-button colors in sync with the app theme on
/// any window that uses <c>ExtendsContentIntoTitleBar = true</c>. Without this,
/// the caption glyphs follow the *system* theme — so a dark app on a light
/// system gets white-on-white close buttons and vice versa.
/// </summary>
internal static class TitleBarHelper
{
    /// <summary>
    /// Apply theme-neutral transparent backgrounds with subtle hover/pressed tints.
    /// Call once after <c>ExtendsContentIntoTitleBar = true</c>.
    /// </summary>
    public static void ApplyTransparentButtonBackground(AppWindow appWindow)
    {
        var titleBar = appWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(20, 128, 128, 128);
        titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(40, 128, 128, 128);
    }

    /// <summary>
    /// Drive caption-glyph color from the app's <see cref="ElementTheme"/>.
    /// </summary>
    public static void ApplyCaptionButtonColors(AppWindow appWindow, ElementTheme theme)
    {
        var isDark = theme == ElementTheme.Dark;
        var foreground = isDark ? Colors.White : Windows.UI.Color.FromArgb(255, 0x1A, 0x1A, 0x1A);
        var inactive = isDark
            ? Windows.UI.Color.FromArgb(255, 0x9A, 0x9A, 0x9A)
            : Windows.UI.Color.FromArgb(255, 0x80, 0x80, 0x80);

        var titleBar = appWindow.TitleBar;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = inactive;
    }
}
