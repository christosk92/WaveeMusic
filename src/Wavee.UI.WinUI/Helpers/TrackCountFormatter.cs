using System.Globalization;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Helpers;

/// <summary>
/// Canonical "N tracks" label used on home cards — singular/plural aware and
/// culture-formatted ("1 track", "1,234 tracks"). Centralises the
/// <c>Count_Track_One</c> / <c>Count_Track_Many</c> lookup so the album
/// metadata line (<see cref="AlbumPrefetcher"/>) and the playlist track-count
/// line share one implementation instead of re-rolling the singular/plural
/// dance per call site.
/// </summary>
public static class TrackCountFormatter
{
    public static string FormatTrackCount(int count) =>
        count == 1
            ? AppLocalization.GetString("Count_Track_One")
            : AppLocalization.Format("Count_Track_Many", count.ToString("N0", CultureInfo.CurrentCulture));
}
