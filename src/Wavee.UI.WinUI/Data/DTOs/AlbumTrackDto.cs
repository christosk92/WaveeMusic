using System;
using System.Collections.Generic;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Represents a track within an album.
/// </summary>
public sealed record AlbumTrackDto : ITrackItem
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public required string Id { get; init; }
    public required string Uri { get; init; }
    public required string Title { get; init; }
    public required string ArtistName { get; init; }
    public required string ArtistId { get; init; }
    public required string AlbumName { get; init; }
    public required string AlbumId { get; init; }
    public string? ImageUrl { get; init; }
    public TimeSpan Duration { get; init; }
    public bool IsExplicit { get; init; }
    /// <summary>
    /// True when the track has at least one Canvas video association
    /// (track.associationsV3.videoAssociations.totalCount > 0). Drives a small
    /// film-strip glyph next to the title in the album track grid.
    /// </summary>
    public bool HasCanvas { get; init; }

    private bool _hasLinkedLocalVideo;
    /// <summary>
    /// True when the track's audio URI has a local music-video file linked to
    /// it (via the right-click "Link Spotify track…" flow). Populated by
    /// <see cref="IMusicVideoMetadataService.ApplyAvailabilityToAsync"/> after
    /// the album loads. Fires PropertyChanged on both itself and
    /// <see cref="HasVideo"/> so <c>TrackItem</c>'s badge updates live.
    /// </summary>
    public bool HasLinkedLocalVideo
    {
        get => _hasLinkedLocalVideo;
        set
        {
            if (_hasLinkedLocalVideo == value) return;
            _hasLinkedLocalVideo = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasLinkedLocalVideo)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasVideo)));
        }
    }

    public bool HasVideo => HasCanvas || _hasLinkedLocalVideo;
    public int TrackNumber { get; init; }
    public int DiscNumber { get; init; }
    public bool IsPlayable { get; init; } = true;
    public int OriginalIndex { get; init; }
    public bool IsLoaded => true;
    public bool IsLiked { get; set; }
    public long PlayCount { get; init; }

    /// <summary>
    /// Per-track artists with URIs preserved. Empty for cached payloads written
    /// before this field existed; TrackItem falls back to <c>ArtistName</c> +
    /// <c>ArtistId</c> in that case.
    /// </summary>
    public IReadOnlyList<TrackArtistRef> Artists { get; init; } = Array.Empty<TrackArtistRef>();

    /// <summary>
    /// Duration formatted as "m:ss" or "h:mm:ss".
    /// </summary>
    public string DurationFormatted => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    /// <summary>
    /// Formatted play count (e.g., "1.2B", "500M", "100K").
    /// </summary>
    public string PlayCountFormatted => PlayCount switch
    {
        0 => "",
        >= 1_000_000_000 => $"{PlayCount / 1_000_000_000.0:0.#}B",
        >= 1_000_000 => $"{PlayCount / 1_000_000.0:0.#}M",
        >= 1_000 => $"{PlayCount / 1_000.0:0.#}K",
        _ => PlayCount.ToString("N0")
    };
}
