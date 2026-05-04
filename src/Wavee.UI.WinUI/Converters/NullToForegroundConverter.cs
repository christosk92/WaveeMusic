using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Converters;

/// <summary>
/// Returns the theme-appropriate text foreground regardless of whether the source
/// value is set. The hero scrim is theme-aware (black in Dark, white in Light), so
/// the overlay text only needs to contrast with the current theme — the same brush
/// that works against the page background works against the scrim.
/// </summary>
public sealed class NullToForegroundConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (Application.Current.Resources.TryGetValue("TextFillColorPrimaryBrush", out var brush))
            return brush;

        return new SolidColorBrush(Microsoft.UI.Colors.Black);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
