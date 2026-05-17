using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.UI.Enums;
using Wavee.UI.Helpers;
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
/// Converts a string URL to an <see cref="Microsoft.UI.Xaml.Media.ImageSource"/>
/// for consumers that need one — <c>PersonPicture.ProfilePicture</c>,
/// <c>ImageBrush.ImageSource</c>. This is the <em>uncached</em> survival path;
/// cached image surfaces live in <see cref="Services.ImageCacheService"/>
/// and render via <see cref="Controls.Imaging.CompositionImage"/>.
///
/// <para>Pass ConverterParameter as an int string to set decode size (default 200 px).</para>
/// </summary>
public sealed class StringToImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string rawUri || string.IsNullOrWhiteSpace(rawUri))
            return null;

        var uri = SpotifyImageHelper.ToHttpsUrl(rawUri) ?? rawUri;
        var decodeSize = int.TryParse(parameter?.ToString(), out var parsed) ? parsed : 200;

        return new BitmapImage(new Uri(uri))
        {
            DecodePixelWidth = decodeSize,
            DecodePixelType = DecodePixelType.Logical
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
