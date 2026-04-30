using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers.Playback;

namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Lightweight <see cref="ITrackItem"/> adapter that reads from the currently-playing
/// track in <see cref="IPlaybackStateService"/>. Used to pass the now-playing track
/// to <see cref="Controls.Track.TrackContextMenu"/> without duplicating menu logic.
/// </summary>
public sealed class NowPlayingTrackAdapter : ITrackItem
{
    private readonly IPlaybackStateService _ps;

    public NowPlayingTrackAdapter(IPlaybackStateService playbackState)
    {
        _ps = playbackState;
    }

    public string Id => _ps.CurrentTrackId ?? "";
    public string Uri => string.IsNullOrEmpty(_ps.CurrentTrackId)
        ? ""
        : (_ps.CurrentTrackId.StartsWith("spotify:", StringComparison.Ordinal)
            ? _ps.CurrentTrackId
            : $"spotify:track:{_ps.CurrentTrackId}");
    public string Title => _ps.CurrentTrackTitle ?? "";
    public string ArtistName => _ps.CurrentArtistName ?? "";
    public string ArtistId => _ps.CurrentArtistId ?? "";
    public string AlbumName => ""; // Not available from playback state
    public string AlbumId => _ps.CurrentAlbumId ?? "";
    public string? ImageUrl => _ps.CurrentAlbumArt;
    public TimeSpan Duration => TimeSpan.FromMilliseconds(_ps.Duration);
    public bool IsExplicit => false;
    public string DurationFormatted => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");
    public int OriginalIndex => 0;
    public bool IsLoaded => !string.IsNullOrEmpty(_ps.CurrentTrackId);

    public bool IsLiked
    {
        get
        {
            var uri = PlaybackSaveTargetResolver.GetTrackUri(_ps);
            var likeService = Ioc.Default.GetService<ITrackLikeService>();
            return !string.IsNullOrEmpty(uri)
                && likeService?.IsSaved(SavedItemType.Track, uri) == true;
        }
        set { }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
