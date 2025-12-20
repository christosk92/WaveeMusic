using System;
using Microsoft.UI.Xaml.Data;

namespace Wavee.WinUI.Converters;

/// <summary>
/// Converter that negates a boolean value.
/// </summary>
public sealed class BoolNegationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is bool boolValue && !boolValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        return value is bool boolValue && !boolValue;
    }
}

/// <summary>
/// Converter that returns true if value is not null.
/// </summary>
public sealed class NullToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value != null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
