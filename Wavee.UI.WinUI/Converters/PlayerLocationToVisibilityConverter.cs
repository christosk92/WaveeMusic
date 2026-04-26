using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Wavee.UI.WinUI.Data.Enums;

namespace Wavee.UI.WinUI.Converters;

/// <summary>
/// Maps a <see cref="PlayerLocation"/> to <see cref="Visibility"/>. The target
/// location is supplied via <see cref="TargetLocation"/> — the converter returns
/// Visible when the bound value matches the target, Collapsed otherwise.
///
/// Two converter instances are typically declared in resources:
/// one with TargetLocation=Bottom (drives PlayerBar visibility), one with
/// TargetLocation=Sidebar (drives SidebarPlayerWidget visibility).
/// </summary>
public sealed class PlayerLocationToVisibilityConverter : IValueConverter
{
    public PlayerLocation TargetLocation { get; set; } = PlayerLocation.Bottom;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not PlayerLocation location) return Visibility.Collapsed;
        return location == TargetLocation ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
