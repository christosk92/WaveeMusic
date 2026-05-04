using System;
using Microsoft.UI.Xaml.Data;

namespace Wavee.UI.WinUI.Converters;

/// <summary>
/// Converts double? to double for ProgressBar.Value binding.
/// Returns 0.0 when null.
/// </summary>
public sealed class NullableDoubleToDoubleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is double d ? d : 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
