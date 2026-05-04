using System;
using Microsoft.UI.Xaml.Data;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Converters;

/// <summary>
/// Converts a <see cref="Microsoft.UI.Xaml.Controls.Slider"/>'s <c>double</c> value
/// (0-3, snapped to ticks) into a human-readable <see cref="CachingProfile"/>
/// display name for the slider's drag tooltip.
///
/// <para>
/// Wired up via <c>ThumbToolTipValueConverter</c> on the slider. The reverse
/// direction is not used by the slider tooltip but is implemented for completeness.
/// </para>
/// </summary>
public sealed class CachingProfileTooltipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is double d)
        {
            var index = Math.Clamp((int)Math.Round(d), 0, 3);
            return CachingProfilePresets.GetDisplayName((CachingProfile)index);
        }
        return CachingProfilePresets.GetDisplayName(CachingProfile.Medium);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
