using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Converters;

/// <summary>
/// Visible when <see cref="PlaylistViewModel.LayoutMode"/> equals
/// <see cref="PlaylistLayoutMode.Banner"/>; <see cref="Visibility.Collapsed"/>
/// otherwise. Drives the visibility of the hero banner row in
/// <c>PlaylistPage.xaml</c>.
/// </summary>
public sealed class PlaylistLayoutModeBannerVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is PlaylistLayoutMode m && m == PlaylistLayoutMode.Banner
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Visible when <see cref="PlaylistViewModel.LayoutMode"/> equals
/// <see cref="PlaylistLayoutMode.Cover"/>; <see cref="Visibility.Collapsed"/>
/// otherwise. Drives the visibility of the square-cover Grid + the
/// left-column title block in <c>PlaylistPage.xaml</c>.
/// </summary>
public sealed class PlaylistLayoutModeCoverVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is PlaylistLayoutMode m && m == PlaylistLayoutMode.Cover
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
