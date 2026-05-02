using System;

namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Represents a podcast/show in the user's library, either followed directly
/// or inferred from saved/listen-later episodes.
/// </summary>
public sealed record LibraryPodcastShowDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public string? Description { get; init; }
    public string? ImageUrl { get; init; }
    public int EpisodeCount { get; init; }
    public int SavedEpisodeCount { get; init; }
    public DateTime AddedAt { get; init; }
    public DateTime? LastEpisodeAddedAt { get; init; }
    public bool IsFollowed { get; init; }
    public bool IsAllPodcasts { get; init; }
    public bool IsRecentlyPlayed { get; init; }

    public bool HasSavedEpisodes => SavedEpisodeCount > 0;

    public string PlaceholderGlyph => IsRecentlyPlayed ? "\uE81C" : "\uEC05";

    public string Metadata
    {
        get
        {
            if (IsAllPodcasts)
                return SavedEpisodeCount == 1 ? "1 saved episode" : $"{SavedEpisodeCount} saved episodes";

            if (IsRecentlyPlayed)
                return EpisodeCount == 1 ? "1 recent episode" : $"{EpisodeCount} recent episodes";

            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(Publisher))
                parts.Add(Publisher!);
            if (SavedEpisodeCount > 0)
                parts.Add(SavedEpisodeCount == 1 ? "1 saved episode" : $"{SavedEpisodeCount} saved episodes");
            else if (EpisodeCount > 0)
                parts.Add(EpisodeCount == 1 ? "1 episode" : $"{EpisodeCount} episodes");
            if (IsFollowed)
                parts.Add("Followed");

            return parts.Count == 0 ? "Podcast" : string.Join(" - ", parts);
        }
    }

    public string AddedAtFormatted => FormatRelativeDate(AddedAt);

    public DateTime SortDate => LastEpisodeAddedAt ?? AddedAt;

    private static string FormatRelativeDate(DateTime date)
    {
        if (date == default)
            return "";

        var diff = DateTime.Now - date;
        if (diff.TotalDays < 1) return "Today";
        if (diff.TotalDays < 2) return "Yesterday";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} days ago";
        if (diff.TotalDays < 30) return $"{(int)(diff.TotalDays / 7)} weeks ago";
        if (diff.TotalDays < 365) return $"{(int)(diff.TotalDays / 30)} months ago";
        return date.ToString("MMM d, yyyy");
    }
}
