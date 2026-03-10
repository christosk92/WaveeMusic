using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.UI.WinUI.Data.Enums;

namespace Wavee.UI.WinUI.Converters;

/// <summary>
/// Converts IsPlaying boolean to Play/Pause glyph.
/// </summary>
public sealed class BoolToPlayPauseGlyphConverter : IValueConverter
{
    // E768 = Play, E769 = Pause
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is bool isPlaying && isPlaying ? "\uE769" : "\uE768";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts volume level (0-100) to appropriate volume icon glyph.
/// </summary>
public sealed class VolumeToGlyphConverter : IValueConverter
{
    // E74F = Muted, E993 = Low, E994 = Medium, E995/E767 = High
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not double volume)
            return "\uE767"; // Default high volume

        return volume switch
        {
            0 => "\uE74F",      // Muted
            < 33 => "\uE993",   // Low
            < 66 => "\uE994",   // Medium
            _ => "\uE767"       // High
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts RepeatMode enum to appropriate repeat icon glyph.
/// </summary>
public sealed class RepeatModeToGlyphConverter : IValueConverter
{
    // E8EE = RepeatAll, E8ED = RepeatOne
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not RepeatMode mode)
            return "\uE8EE";

        return mode switch
        {
            RepeatMode.Track => "\uE8ED",    // Repeat One/Track
            _ => "\uE8EE"                     // Repeat All/Context or Off (same icon, different state)
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts milliseconds to formatted time string (mm:ss or h:mm:ss).
/// </summary>
public sealed class MillisecondsToTimeStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not double ms)
            return "0:00";

        var timeSpan = TimeSpan.FromMilliseconds(ms);

        if (timeSpan.TotalHours >= 1)
            return timeSpan.ToString(@"h\:mm\:ss");

        return timeSpan.ToString(@"m\:ss");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts RepeatMode to checked state for toggle button.
/// </summary>
public sealed class RepeatModeToCheckedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not RepeatMode mode)
            return false;

        return mode != RepeatMode.Off;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts RepeatMode to Symbol for repeat button icon.
/// </summary>
public sealed class RepeatModeToSymbolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not RepeatMode mode)
            return Microsoft.UI.Xaml.Controls.Symbol.RepeatAll;

        return mode switch
        {
            RepeatMode.Track => Microsoft.UI.Xaml.Controls.Symbol.RepeatOne,
            _ => Microsoft.UI.Xaml.Controls.Symbol.RepeatAll
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a string URL to an ImageSource for x:Bind compatibility.
/// Returns null for null/empty strings to avoid "parameter is incorrect" errors.
/// Uses an O(1) LRU cache to prevent garbage collection of BitmapImage instances.
/// </summary>
public sealed class StringToImageSourceConverter : IValueConverter
{
    private readonly record struct ImageCacheKey(string Uri, int DecodeSize);
    private readonly record struct TimestampedImage(BitmapImage Image, DateTimeOffset LastAccessed);

    // O(1) LRU cache using LinkedList + Dictionary with node references
    // - LinkedList maintains LRU order (front = most recent, back = oldest)
    // - Dictionary maps (URI, DecodeSize) -> LinkedListNode for O(1) lookup and removal
    private static readonly LinkedList<KeyValuePair<ImageCacheKey, TimestampedImage>> _lruList = new();
    private static readonly Dictionary<ImageCacheKey, LinkedListNode<KeyValuePair<ImageCacheKey, TimestampedImage>>> _cache = new();
    private static readonly object _cacheLock = new();
    private const int MaxCacheSize = 100;

    /// <summary>
    /// Current number of entries in the image cache.
    /// </summary>
    public static int CacheCount
    {
        get { lock (_cacheLock) { return _cache.Count; } }
    }

    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string uri || string.IsNullOrWhiteSpace(uri))
            return null;

        var decodeSize = int.TryParse(parameter?.ToString(), out var parsed) ? parsed : 0;
        var cacheKey = new ImageCacheKey(uri, decodeSize);

        try
        {
            lock (_cacheLock)
            {
                // Cache hit: O(1) lookup, O(1) promote to front
                if (_cache.TryGetValue(cacheKey, out var node))
                {
                    _lruList.Remove(node);      // O(1) - removes by node reference
                    _lruList.AddFirst(node);    // O(1)
                    node.Value = new KeyValuePair<ImageCacheKey, TimestampedImage>(
                        cacheKey, node.Value.Value with { LastAccessed = DateTimeOffset.UtcNow });
                    return node.Value.Value.Image;
                }

                // Cache miss: create new BitmapImage
                var bitmapImage = new BitmapImage();
                if (decodeSize > 0)
                {
                    bitmapImage.DecodePixelWidth = decodeSize;
                    bitmapImage.DecodePixelHeight = decodeSize;
                }

                bitmapImage.UriSource = new Uri(uri);

                // Add to front of LRU list: O(1)
                var stamped = new TimestampedImage(bitmapImage, DateTimeOffset.UtcNow);
                var newNode = _lruList.AddFirst(new KeyValuePair<ImageCacheKey, TimestampedImage>(cacheKey, stamped));
                _cache[cacheKey] = newNode;

                // Evict oldest if over capacity: O(1)
                if (_cache.Count > MaxCacheSize && _lruList.Last != null)
                {
                    var oldest = _lruList.Last;
                    _cache.Remove(oldest.Value.Key);
                    _lruList.RemoveLast();
                }

                return bitmapImage;
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Removes image cache entries not accessed within the specified time window.
    /// Called by the background cache cleanup service.
    /// </summary>
    public static int CleanupStaleEntries(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var removed = 0;

        lock (_cacheLock)
        {
            var node = _lruList.Last;
            while (node != null)
            {
                var prev = node.Previous;
                if (node.Value.Value.LastAccessed < cutoff)
                {
                    _cache.Remove(node.Value.Key);
                    _lruList.Remove(node);
                    removed++;
                }
                else
                {
                    break;
                }
                node = prev;
            }
        }

        return removed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
