using System;
using Microsoft.UI.Xaml.Data;

namespace Wavee.UI.WinUI.Converters;

/// <summary>
/// Converts true to false and false to true.
/// </summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is bool b ? !b : false;
}
