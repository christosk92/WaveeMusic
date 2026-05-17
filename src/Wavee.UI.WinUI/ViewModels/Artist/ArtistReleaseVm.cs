using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class ArtistReleaseVm : ObservableObject
{
    public string Id { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public string Type { get; init; } // ALBUM, SINGLE, COMPILATION
    public string? ImageUrl { get; init; }
    public DateTimeOffset ReleaseDate { get; init; }
    public int TrackCount { get; init; }
    public string? Label { get; init; }
    public int Year { get; init; }

    /// <summary>
    /// Discography-card subtitle: "Oct 10, 2025 · 10 tracks" — full release
    /// date plus track count, matching the prototype. Falls back to year-only
    /// when the date hasn't been resolved (e.g. legacy releases with only a
    /// year), and to track count alone if even the year is missing.
    /// </summary>
    public string SubtitleDetail
    {
        get
        {
            var datePart = ReleaseDate.Year > 1
                ? ReleaseDate.ToString("MMM d, yyyy", CultureInfo.CurrentCulture)
                : (Year > 0 ? Year.ToString(CultureInfo.CurrentCulture) : null);
            var tracksPart = TrackCount > 0
                ? $"{TrackCount} track{(TrackCount == 1 ? "" : "s")}"
                : null;
            return (datePart, tracksPart) switch
            {
                ({ } d, { } t) => $"{d} · {t}",
                ({ } d, null) => d,
                (null, { } t) => t,
                _ => string.Empty,
            };
        }
    }

    [ObservableProperty]
    private string? _colorHex;
}
