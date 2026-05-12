using System;
using System.ComponentModel;
using Wavee.Local;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Wraps a <see cref="LocalTrackRow"/> as an <see cref="ITrackItem"/> so local
/// tracks render through the existing <c>TrackDataGrid</c> + <c>TrackItem</c>
/// pipeline. Visual parity with Spotify track rows is free because the data
/// grid is bound against the interface, not a Spotify-specific DTO.
///
/// <para><see cref="ITrackItem.IsLocal"/> auto-derives from the URI prefix —
/// no manual flag needed.</para>
/// </summary>
public sealed class LocalTrackItemAdapter : ITrackItem
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public LocalTrackItemAdapter(LocalTrackRow row, int originalIndex)
    {
        Row = row ?? throw new ArgumentNullException(nameof(row));
        OriginalIndex = originalIndex;
    }

    public LocalTrackRow Row { get; }

    /// <summary>ITrackItem.Id — bare hash from <c>wavee:local:track:&lt;hash&gt;</c>.</summary>
    public string Id
    {
        get
        {
            var u = Row.TrackUri;
            var i = u.LastIndexOf(':');
            return i >= 0 && i < u.Length - 1 ? u[(i + 1)..] : u;
        }
    }

    public string Uri => Row.TrackUri;
    public string Title => Row.Title ?? System.IO.Path.GetFileNameWithoutExtension(Row.FilePath);
    public string ArtistName => Row.Artist ?? Row.AlbumArtist ?? "Unknown Artist";
    public string ArtistId => Row.ArtistUri ?? string.Empty;
    public string AlbumName => Row.Album ?? "Unknown Album";
    public string AlbumId => Row.AlbumUri ?? string.Empty;
    public string? ImageUrl => Row.ArtworkUri;
    public TimeSpan Duration => TimeSpan.FromMilliseconds(Row.DurationMs);
    public bool IsExplicit => false;
    public int OriginalIndex { get; }
    public bool IsLoaded => true;
    public bool HasVideo => Row.IsVideo;

    private bool _isLiked;
    public bool IsLiked
    {
        get => _isLiked;
        set
        {
            if (_isLiked == value) return;
            _isLiked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLiked)));
        }
    }

    public string DurationFormatted => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");
}
