using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.ViewModels;

public sealed class ArtistTopTrackVm : ITrackItem
{
    public required string Id { get; init; }
    public int Index { get; set; }
    public string? Uri { get; init; }
    public string? AlbumImageUrl { get; init; }
    public string? AlbumUri { get; init; }
    public long PlayCountRaw { get; init; }
    public bool IsPlayable { get; init; }
    /// <summary>
    /// True when the Spotify track itself ships a Canvas video (set at row
    /// build time from the API payload).
    /// </summary>
    public bool HasCanvasVideo { get; init; }

    private bool _hasLinkedLocalVideo;
    /// <summary>
    /// True when the audio URI has a linked local music-video file. Populated
    /// asynchronously by <see cref="Wavee.UI.WinUI.Services.IMusicVideoMetadataService.ApplyAvailabilityToAsync"/>
    /// after top tracks load. Fires PropertyChanged on both itself and
    /// <see cref="HasVideo"/> so <c>TrackItem</c>'s badge updates live.
    /// </summary>
    public bool HasLinkedLocalVideo
    {
        get => _hasLinkedLocalVideo;
        set
        {
            if (_hasLinkedLocalVideo == value) return;
            _hasLinkedLocalVideo = value;
            OnPropertyChanged(nameof(HasLinkedLocalVideo));
            OnPropertyChanged(nameof(HasVideo));
        }
    }

    public bool HasVideo => HasCanvasVideo || _hasLinkedLocalVideo;

    // -- ITrackItem implementation --
    string ITrackItem.Uri => Uri ?? SpotifyUriHelper.ToUri(SpotifyEntityKind.Track, Id);
    string ITrackItem.Title => Title ?? "";
    // ArtistName feeds the artist column in TrackItem. Play count is shown
    // in its own column via the explicit PlayCountText binding, so this no
    // longer hijacks the field to spell out the play count.
    string ITrackItem.ArtistName => ArtistNames ?? "";
    string ITrackItem.ArtistId => "";
    string ITrackItem.AlbumName => AlbumName ?? "";
    string ITrackItem.AlbumId => AlbumUri ?? "";
    string? ITrackItem.ImageUrl => AlbumImageUrl;
    TimeSpan ITrackItem.Duration => Duration;
    bool ITrackItem.IsExplicit => IsExplicit;
    string ITrackItem.DurationFormatted => DurationFormatted;
    int ITrackItem.OriginalIndex => Index;
    bool ITrackItem.IsLoaded => true;
    bool ITrackItem.HasVideo => HasVideo;

    // -- Public properties --
    public string? Title { get; init; }
    public string? AlbumName { get; init; }
    public string? ArtistNames { get; init; }
    public TimeSpan Duration { get; init; }
    public bool IsExplicit { get; init; }

    public string PlayCountFormatted => PlayCountRaw.ToString("N0");

    public string DurationFormatted =>
        Duration.TotalHours >= 1
            ? Duration.ToString(@"h\:mm\:ss")
            : Duration.ToString(@"m\:ss");

    private bool _isLiked;
    public bool IsLiked
    {
        get => _isLiked;
        set => SetField(ref _isLiked, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
