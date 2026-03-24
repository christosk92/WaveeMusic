using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Wavee.UI.WinUI.Converters;

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isNotNull = value != null
            && !(value is int i && i == 0)
            && !(value is long l && l == 0)
            && !(value is string s && string.IsNullOrEmpty(s));
         if (Invert)
            isNotNull = !isNotNull;

        return isNotNull ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
