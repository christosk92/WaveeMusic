using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.UI.WinUI.Helpers;

namespace Wavee.UI.WinUI.Converters;

/// <summary>
/// Converts Spotify image URIs (spotify:image:, spotify:mosaic:, https://) to BitmapImage.
/// </summary>
public sealed class SpotifyImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string uri)
        {
            var url = SpotifyImageHelper.ToHttpsUrl(uri);
            if (!string.IsNullOrEmpty(url))
                return new BitmapImage(new Uri(url));
        }
        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
