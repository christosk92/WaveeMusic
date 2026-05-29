using System;
using System.Globalization;

namespace Wavee.UI.Models;

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial record TimelineHoverPreviewItem(
    string Title,
    string? Subtitle,
    long StartMilliseconds,
    long StopMilliseconds)
{
    public string TimeRange
    {
        get
        {
            if (StopMilliseconds > StartMilliseconds)
                return $"{FormatTime(StartMilliseconds)} - {FormatTime(StopMilliseconds)}";

            return FormatTime(StartMilliseconds);
        }
    }

    private static string FormatTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
