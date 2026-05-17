using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Helpers;

namespace Wavee.UI.WinUI.Converters;

/// <summary>
/// Converts a Spotify image URI (spotify:image:, spotify:mosaic:, https://) to a
/// <see cref="BitmapImage"/> for consumers that require an <see cref="Microsoft.UI.Xaml.Media.ImageSource"/>
/// — chiefly <c>PersonPicture.ProfilePicture</c> and <c>ImageBrush.ImageSource</c>.
///
/// <para>
/// This is the <em>uncached</em> survival path. Cached image surfaces live in
/// <see cref="Services.ImageCacheService"/> and render via
/// <see cref="Controls.Imaging.CompositionImage"/>. Use that wherever you can.
/// This converter creates a fresh <see cref="BitmapImage"/> per call with a
/// down-sampled <c>DecodePixelWidth</c> — appropriate for single-image surfaces
/// (page heroes, avatars) but wasteful inside virtualized lists.
/// </para>
///
/// <para>
/// Pass <c>ConverterParameter</c> as an int string to override the decode size
/// (default 200 px).
/// </para>
/// </summary>
public sealed class SpotifyImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string uri) return null;
        var url = SpotifyImageHelper.ToHttpsUrl(uri);
        if (string.IsNullOrEmpty(url)) return null;

        var decodeSize = parameter is string s && int.TryParse(s, out var d) ? d : 200;

        return new BitmapImage(new Uri(url))
        {
            DecodePixelWidth = decodeSize,
            DecodePixelType = DecodePixelType.Logical,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
