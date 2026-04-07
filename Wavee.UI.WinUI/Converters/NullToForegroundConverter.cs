using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Converters;

/// <summary>
/// Returns White brush when the value is non-null/non-empty (e.g., header image present),
/// otherwise returns the theme-appropriate text foreground.
/// </summary>
public sealed class NullToForegroundConverter : IValueConverter
{
    private static readonly SolidColorBrush WhiteBrush = new(Microsoft.UI.Colors.White);

    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        var hasValue = value != null && (value is not string s || !string.IsNullOrEmpty(s));

        if (hasValue)
            return WhiteBrush;

        // Fall back to theme-aware text foreground
        if (Application.Current.Resources.TryGetValue("TextFillColorPrimaryBrush", out var brush))
            return brush;

        return new SolidColorBrush(Microsoft.UI.Colors.Black);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
