using System;
using System.Collections.Generic;
using System.ComponentModel;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Represents a saved podcast episode in the user's library.
/// </summary>
public sealed record LibraryEpisodeDto : ITrackItem
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Id { get; init; }
    public required string Uri { get; init; }
    public required string Title { get; init; }
    public required string ArtistName { get; init; }
    public required string ArtistId { get; init; }
    public required string AlbumName { get; init; }
    public required string AlbumId { get; init; }
    public string? ImageUrl { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset? ReleaseDate { get; init; }
    public string? ShareUrl { get; init; }
    public string? PreviewUrl { get; init; }
    public IReadOnlyList<string> MediaTypes { get; init; } = [];
    public TimeSpan Duration { get; init; }
    public DateTime AddedAt { get; init; }
    public bool IsExplicit { get; init; }
    public bool IsPlayable { get; init; } = true;
    public TimeSpan PlayedPosition { get; private set; }
    public string? PlayedState { get; private set; }
    public int OriginalIndex { get; init; }
    public bool IsLoaded => true;
    public bool IsLiked { get; set; } = true;
    public bool HasPlaybackProgressError =>
        string.Equals(PlayedState, PodcastEpisodeProgressDto.ErrorState, StringComparison.Ordinal);

    public double? PlaybackProgress
    {
        get
        {
            if (HasPlaybackProgressError)
                return null;

            if (string.Equals(PlayedState, "COMPLETED", StringComparison.OrdinalIgnoreCase))
                return 1d;

            if (Duration.TotalMilliseconds <= 0)
                return null;

            return Math.Clamp(PlayedPosition.TotalMilliseconds / Duration.TotalMilliseconds, 0d, 1d);
        }
    }

    public string PlaybackProgressText
    {
        get
        {
            if (HasPlaybackProgressError)
                return "Progress unavailable";

            var progress = PlaybackProgress;
            if (progress is null)
                return "";

            if (string.Equals(PlayedState, "COMPLETED", StringComparison.OrdinalIgnoreCase) || progress >= 0.995)
                return "Played";

            if (progress <= 0.001)
                return "Unplayed";

            return $"{Math.Round(progress.Value * 100):0}%";
        }
    }

    public string DurationFormatted => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    public string AddedAtFormatted => FormatRelativeDate(AddedAt);

    public void ApplyPlaybackProgress(TimeSpan playedPosition, string? playedState)
    {
        PlayedPosition = playedPosition < TimeSpan.Zero ? TimeSpan.Zero : playedPosition;
        PlayedState = NormalizePlayedState(PlayedPosition, Duration, playedState);
        OnPropertyChanged(nameof(PlayedPosition));
        OnPropertyChanged(nameof(PlayedState));
        OnPropertyChanged(nameof(PlaybackProgress));
        OnPropertyChanged(nameof(PlaybackProgressText));
        OnPropertyChanged(nameof(HasPlaybackProgressError));
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string? NormalizePlayedState(TimeSpan playedPosition, TimeSpan duration, string? playedState)
    {
        if (string.Equals(playedState, PodcastEpisodeProgressDto.ErrorState, StringComparison.Ordinal))
            return playedState;

        if (string.Equals(playedState, "COMPLETED", StringComparison.OrdinalIgnoreCase))
            return "COMPLETED";

        if (duration.TotalMilliseconds > 0)
        {
            var remaining = duration - playedPosition;
            if (playedPosition.TotalMilliseconds > 0 &&
                (remaining <= TimeSpan.FromSeconds(30) ||
                 playedPosition.TotalMilliseconds / duration.TotalMilliseconds >= 0.995d))
            {
                return "COMPLETED";
            }
        }

        if (playedPosition <= TimeSpan.Zero)
            return "NOT_STARTED";

        return string.IsNullOrWhiteSpace(playedState) ? "IN_PROGRESS" : playedState;
    }

    private static string FormatRelativeDate(DateTime date)
    {
        var diff = DateTime.Now - date;
        if (diff.TotalDays < 1) return "Today";
        if (diff.TotalDays < 2) return "Yesterday";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} days ago";
        if (diff.TotalDays < 30) return $"{(int)(diff.TotalDays / 7)} weeks ago";
        if (diff.TotalDays < 365) return $"{(int)(diff.TotalDays / 30)} months ago";
        return date.ToString("MMM d, yyyy");
    }
}
