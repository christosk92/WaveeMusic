using System;
using System.Collections.Generic;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Represents a track within a playlist.
/// </summary>
public sealed record PlaylistTrackDto : ITrackItem
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
    /// <summary>
    /// When the track was added to the playlist, or <c>null</c> when the playlist's
    /// API payload did not include an added-at timestamp (editorial / radio playlists
    /// typically omit it). Pages hide the Date Added column when no track carries a
    /// non-null value.
    /// </summary>
    public DateTime? AddedAt { get; init; }
    public string? AddedBy { get; init; }

    /// <summary>
    /// Resolved display name of <see cref="AddedBy"/>. Populated lazily by
    /// <c>PlaylistViewModel</c> on collaborative playlists; null until resolved
    /// (or for non-collab playlists where the badge is suppressed). Setter fires
    /// <see cref="PropertyChanged"/> so any already-realized row cell that binds
    /// this property updates without re-templating.
    /// </summary>
    private string? _addedByDisplayName;
    public string? AddedByDisplayName
    {
        get => _addedByDisplayName;
        set
        {
            if (_addedByDisplayName == value) return;
            _addedByDisplayName = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(AddedByDisplayName)));
        }
    }

    /// <summary>
    /// Resolved profile avatar URL of <see cref="AddedBy"/>. Same lifecycle as
    /// <see cref="AddedByDisplayName"/>.
    /// </summary>
    private string? _addedByAvatarUrl;
    public string? AddedByAvatarUrl
    {
        get => _addedByAvatarUrl;
        set
        {
            if (_addedByAvatarUrl == value) return;
            _addedByAvatarUrl = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(AddedByAvatarUrl)));
        }
    }

    public bool IsExplicit { get; init; }
    public int OriginalIndex { get; init; }
    public bool IsLoaded => true;
    public bool IsLiked { get; set; }

    /// <summary>
    /// Stable per-track uid derived from the playlist's binary <c>itemId</c>
    /// (lower-case hex). Sent as <c>track.uid</c> in published PlayerState so
    /// remote clients can issue skip-to-uid commands unambiguously.
    /// </summary>
    public string? Uid { get; init; }

    /// <summary>
    /// Per-track format attributes passed through from the playlist API —
    /// recommender decorations (e.g. <c>item-score</c>, <c>decision_id</c>,
    /// <c>core:list_uid</c>, the <c>PROBABLY_IN_*</c> signals). Empty for
    /// user-authored playlists.
    /// </summary>
    public IReadOnlyDictionary<string, string>? FormatAttributes { get; init; }

    /// <summary>
    /// Duration formatted as "m:ss" or "h:mm:ss".
    /// </summary>
    public string DurationFormatted => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    /// <summary>
    /// AddedAt formatted as relative time or date; empty when no timestamp is available.
    /// </summary>
    public string AddedAtFormatted
    {
        get
        {
            if (AddedAt is not DateTime when) return "";
            var diff = DateTime.Now - when;
            if (diff.TotalMinutes < 1) return "Just now";
            if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes} min ago";
            if (diff.TotalDays < 1) return $"{(int)diff.TotalHours} hr ago";
            if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} days ago";
            return when.ToString("MMM d, yyyy");
        }
    }
}
