using System;
using Microsoft.UI.Xaml.Data;

namespace Wavee.UI.WinUI.Converters;

/// <summary>
/// Returns true if value is null, false otherwise.
/// Useful for IsIndeterminate binding when Progress is null.
/// </summary>
public sealed class NullToBoolConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isNull = value is null;
        return Invert ? !isNull : isNull;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
