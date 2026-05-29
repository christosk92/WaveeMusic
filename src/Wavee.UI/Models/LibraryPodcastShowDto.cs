using System;
using Wavee.UI.Localization;

namespace Wavee.UI.Models;

/// <summary>
/// Represents a podcast/show in the user's library, either followed directly
/// or inferred from saved/listen-later episodes.
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial record LibraryPodcastShowDto
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
                return SavedEpisodeCount == 1
                    ? LocalizationHook.GetString("Count_SavedEpisode_One")
                    : LocalizationHook.Format("Count_SavedEpisode_Many", SavedEpisodeCount);

            if (IsRecentlyPlayed)
                return EpisodeCount == 1
                    ? LocalizationHook.GetString("Count_RecentEpisode_One")
                    : LocalizationHook.Format("Count_RecentEpisode_Many", EpisodeCount);

            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(Publisher))
                parts.Add(Publisher!);
            if (SavedEpisodeCount > 0)
                parts.Add(SavedEpisodeCount == 1
                    ? LocalizationHook.GetString("Count_SavedEpisode_One")
                    : LocalizationHook.Format("Count_SavedEpisode_Many", SavedEpisodeCount));
            else if (EpisodeCount > 0)
                parts.Add(EpisodeCount == 1
                    ? LocalizationHook.GetString("Count_Episode_One")
                    : LocalizationHook.Format("Count_Episode_Many", EpisodeCount));
            if (IsFollowed)
                parts.Add(LocalizationHook.GetString("Podcast_FollowedLabel"));

            return parts.Count == 0 ? LocalizationHook.GetString("Podcast_Placeholder") : string.Join(" - ", parts);
        }
    }

    public string AddedAtFormatted => FormatRelativeDate(AddedAt);

    public DateTime SortDate => LastEpisodeAddedAt ?? AddedAt;

    private static string FormatRelativeDate(DateTime date)
    {
        if (date == default)
            return "";

        var diff = DateTime.Now - date;
        if (diff.TotalDays < 1) return LocalizationHook.GetString("Relative_Today");
        if (diff.TotalDays < 2) return LocalizationHook.GetString("Relative_Yesterday");
        if (diff.TotalDays < 7) return LocalizationHook.Format("Relative_DaysAgo", (int)diff.TotalDays);
        if (diff.TotalDays < 30) return LocalizationHook.Format("Relative_WeeksAgo", (int)(diff.TotalDays / 7));
        if (diff.TotalDays < 365) return LocalizationHook.Format("Relative_MonthsAgo", (int)(diff.TotalDays / 30));
        return date.ToString("MMM d, yyyy");
    }
}