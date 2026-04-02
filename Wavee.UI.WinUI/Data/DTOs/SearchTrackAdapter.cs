using System;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Adapts a SearchResultItem (track type) to ITrackItem for use in TrackItem control.
/// </summary>
public sealed class SearchTrackAdapter : ITrackItem
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private readonly SearchResultItem _data;

    public SearchTrackAdapter(SearchResultItem data)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
    }

    public string Id => _data.Uri.Replace("spotify:track:", "");
    public string Uri => _data.Uri;
    public string Title => _data.Name;
    public string ArtistName => _data.ArtistNames != null ? string.Join(", ", _data.ArtistNames) : "";
    public string ArtistId => ""; // Not available in search results
    public string AlbumName => _data.AlbumName ?? "";
    public string AlbumId => ""; // Not available in search results
    public string? ImageUrl => _data.ImageUrl;
    public TimeSpan Duration => TimeSpan.FromMilliseconds(_data.DurationMs);
    public bool IsExplicit => false; // Not available in search results
    public string DurationFormatted
    {
        get
        {
            var d = Duration;
            return d.TotalHours >= 1
                ? d.ToString(@"h\:mm\:ss")
                : d.ToString(@"m\:ss");
        }
    }
    public int OriginalIndex => 0;
    public bool IsLoaded => true;
    public bool IsLiked { get; set; }
}
