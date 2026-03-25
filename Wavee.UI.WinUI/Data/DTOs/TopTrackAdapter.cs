using System;
using System.Linq;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.DTOs;

public sealed class TopTrackAdapter : ITrackItem
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private readonly TopTrackData _data;

    public TopTrackAdapter(TopTrackData data, int index)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        OriginalIndex = index;
    }

    public string Id => _data.Uri ?? "";
    public string Title => _data.Name ?? "";
    public string ArtistName => _data.DisplayArtistName;
    public string ArtistId => _data.Artists?.Items?.FirstOrDefault()?.Uri?.Replace("spotify:artist:", "") ?? "";
    public string AlbumName => _data.AlbumOfTrack?.Name ?? "";
    public string AlbumId => _data.AlbumOfTrack?.Uri?.Replace("spotify:album:", "") ?? "";
    public string? ImageUrl => _data.DisplayAlbumArtUrl;
    public TimeSpan Duration => TimeSpan.FromMilliseconds(_data.Duration?.TotalMilliseconds ?? 0);
    public bool IsExplicit => _data.ContentRating?.Label == "EXPLICIT";
    public string DurationFormatted => _data.DisplayDuration;
    public int OriginalIndex { get; }
    public bool IsLoaded => true;
}
