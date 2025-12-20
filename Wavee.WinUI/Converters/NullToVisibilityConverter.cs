using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Wavee.WinUI.Converters;

/// <summary>
/// Converts null values to Visibility.Collapsed, non-null to Visibility.Visible
/// </summary>
public partial class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value != null ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
