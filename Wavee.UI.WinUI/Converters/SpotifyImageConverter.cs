using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Converters;

/// <summary>
/// Converts Spotify image URIs (spotify:image:, spotify:mosaic:, https://) to BitmapImage.
/// Uses ImageCacheService for LRU caching and DecodePixelWidth to avoid full-resolution decoding.
/// Pass ConverterParameter as an int string to override the default decode size (200px).
/// </summary>
public sealed class SpotifyImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string uri) return null;
        var url = SpotifyImageHelper.ToHttpsUrl(uri);
        if (string.IsNullOrEmpty(url)) return null;

        var decodeSize = parameter is string s && int.TryParse(s, out var d) ? d : 200;
        var cache = Ioc.Default.GetService<ImageCacheService>();
        return cache?.GetOrCreate(url, decodeSize) ?? new BitmapImage(new Uri(url)) { DecodePixelWidth = decodeSize, DecodePixelType = DecodePixelType.Logical };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
