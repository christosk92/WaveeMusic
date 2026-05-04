using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Converters;

public sealed class BoolToBackgroundConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var boolValue = value is bool b && b;
        if (Invert)
            boolValue = !boolValue;

        // Return subtle background for active state, transparent for inactive
        return boolValue
            ? Application.Current.Resources["SubtleFillColorSecondaryBrush"] as Brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
