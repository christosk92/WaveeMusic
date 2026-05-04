using System;
using Microsoft.UI.Xaml.Data;

namespace Wavee.UI.WinUI.Converters;

public sealed class BoolToGridListGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        // Grid view = true → show list icon (switch to list)
        // List view = false → show grid icon (switch to grid)
        return value is true ? "\uE8FD" : "\uF0E2";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
