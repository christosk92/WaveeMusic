using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Converters;

/// <summary>
/// Returns a filled brush when the chip is selected, subtle brush when not.
/// </summary>
public sealed class ChipBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isSelected = value is bool b && b;
        var key = isSelected
            ? "AccentFillColorDefaultBrush"
            : "ControlFillColorSecondaryBrush";

        return Application.Current.Resources.TryGetValue(key, out var brush)
            ? (Brush)brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Returns white text when selected, default text color when not.
/// </summary>
public sealed class ChipForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isSelected = value is bool b && b;
        var key = isSelected
            ? "TextOnAccentFillColorPrimaryBrush"
            : "TextFillColorPrimaryBrush";

        return Application.Current.Resources.TryGetValue(key, out var brush)
            ? (Brush)brush
            : new SolidColorBrush(Microsoft.UI.Colors.White);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
