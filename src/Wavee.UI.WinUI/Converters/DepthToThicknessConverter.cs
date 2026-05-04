using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Wavee.UI.WinUI.Converters;

/// <summary>
/// Converts an integer nesting depth into a left-only <see cref="Thickness"/>.
/// Used by the sidebar template so nested folder children render with progressive
/// indentation (depth 0 → 0 px, depth 1 → stride, depth 2 → 2 × stride…).
/// <para>
/// ConverterParameter accepts either a single number (stride only, e.g. <c>ConverterParameter=20</c>)
/// or a <c>stride,offset</c> pair (e.g. <c>ConverterParameter=20,10</c>) which yields
/// <c>depth × stride + offset</c>. Defaults: stride 16, offset 0.
/// </para>
/// </summary>
public sealed class DepthToThicknessConverter : IValueConverter
{
    private const double DefaultPixelsPerLevel = 16d;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var depth = value switch
        {
            int i => i,
            long l => (int)l,
            _ => 0
        };

        if (depth < 0) depth = 0;

        var pixelsPerLevel = DefaultPixelsPerLevel;
        var offset = 0d;

        if (parameter is string paramText && paramText.Length > 0)
        {
            var parts = paramText.Split(',');
            if (parts.Length >= 1 && double.TryParse(parts[0],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsedStride))
            {
                pixelsPerLevel = parsedStride;
            }
            if (parts.Length >= 2 && double.TryParse(parts[1],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsedOffset))
            {
                offset = parsedOffset;
            }
        }

        return new Thickness(depth * pixelsPerLevel + offset, 0, 0, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
