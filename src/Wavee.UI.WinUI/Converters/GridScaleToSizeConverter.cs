using System;
using System.Globalization;
using Microsoft.UI.Xaml.Data;

namespace Wavee.UI.WinUI.Converters;

/// <summary>
/// Multiplies a `double` scale value by a base-size `string` parameter to
/// drive `UniformGridLayout.MinItemWidth` / `MinItemHeight` from a single
/// VM-bound scale slider. Mirrors the behaviour the Library album grid uses
/// to derive its cell dimensions from `AlbumsLibraryViewModel.GridScale`.
/// </summary>
public sealed class GridScaleToSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var scale = value is double d ? d : 1.0;
        if (parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var baseSize))
            return baseSize * scale;
        return value ?? 0d;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
