using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.Data.Models;
using Windows.UI;

namespace Wavee.UI.WinUI.Converters;

public sealed partial class ActivityOutcomeBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var outcome = value is ActivityOutcome typed ? typed : ActivityOutcome.None;
        return outcome switch
        {
            ActivityOutcome.Positive => ResourceBrush("SystemFillColorSuccessBrush", Color.FromArgb(255, 16, 124, 65)),
            ActivityOutcome.Negative => ResourceBrush("SystemFillColorCriticalBrush", Color.FromArgb(255, 196, 43, 28)),
            ActivityOutcome.Undo => ResourceBrush("AccentFillColorDefaultBrush", Color.FromArgb(255, 0, 120, 212)),
            _ => ResourceBrush("TextFillColorTertiaryBrush", Color.FromArgb(255, 120, 120, 120))
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    private static Brush ResourceBrush(string key, Color fallback)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Brush brush)
            return brush;

        return new SolidColorBrush(fallback);
    }
}
