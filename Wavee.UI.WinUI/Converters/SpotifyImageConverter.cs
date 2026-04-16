using System;
using System.Threading;
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
    private static ImageCacheService? _cache;
    private static int _cacheLookupAttempted; // 0 = not yet, 1 = done

    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string uri) return null;
        var url = SpotifyImageHelper.ToHttpsUrl(uri);
        if (string.IsNullOrEmpty(url)) return null;

        var decodeSize = parameter is string s && int.TryParse(s, out var d) ? d : 200;

        // Resolve the cache once. If it's not yet registered (early startup),
        // remember that and avoid hitting Ioc on every binding evaluation.
        if (_cache == null && Interlocked.CompareExchange(ref _cacheLookupAttempted, 1, 0) == 0)
        {
            _cache = Ioc.Default.GetService<ImageCacheService>();
        }

        return _cache?.GetOrCreate(url, decodeSize)
            ?? new BitmapImage(new Uri(url)) { DecodePixelWidth = decodeSize, DecodePixelType = DecodePixelType.Logical };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
