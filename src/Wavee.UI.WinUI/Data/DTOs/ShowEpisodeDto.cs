using System;
using System.Linq;

namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// One episode row inside the Show detail page. Combines the Pathfinder
/// metadata (title, description, duration, cover) with the local
/// EpisodeCacheEntry's playback progress so the row can render its played-state
/// pill without an extra fetch.
/// </summary>
public sealed class ShowEpisodeDto
{
    private const long NearEndThresholdMs = 90_000;

    public string Uri { get; init; } = "";
    public string Title { get; init; } = "";
    public string? DescriptionPreview { get; init; }
    public string? CoverArtUrl { get; init; }
    public long DurationMs { get; init; }
    public DateTimeOffset? ReleaseDate { get; init; }

    /// <summary>NOT_STARTED / IN_PROGRESS / COMPLETED — mirrors the strings the rest of the codebase uses.</summary>
    public string PlayedState { get; init; } = "NOT_STARTED";

    public long PlayedPositionMs { get; init; }
    public bool IsExplicit { get; init; }
    public bool IsVideo { get; init; }
    public bool IsPlayable { get; init; } = true;

    /// <summary>Parent show URI (<c>spotify:show:{id}</c>), populated when the episode
    /// protobuf carries show metadata. Null on rows materialised purely from progress
    /// snapshots — the show page doesn't need it (the page already knows the show)
    /// but the episode detail page uses it to seed the breadcrumb without a refetch.</summary>
    public string? ShowUri { get; init; }
    public string? ShowName { get; init; }
    public string? ShowImageUrl { get; init; }

    /// <summary>Full episode description after HTML strip (no first-paragraph trim).
    /// Falls back to <see cref="DescriptionPreview"/> on episode detail surfaces that
    /// want the long form.</summary>
    public string? FullDescription { get; init; }

    /// <summary>Raw provider description HTML. Detail surfaces pass this through
    /// <c>HtmlTextBlock</c> so links and paragraph breaks survive.</summary>
    public string? DescriptionHtml { get; init; }

    /// <summary>1-indexed chronological position over the show's full episode list, computed
    /// once by <c>ShowViewModel</c> after sorting. 0 means unset (DTO not yet placed in
    /// the show context). Settable, not init-only, because the VM assigns it after the
    /// service hands the list back.</summary>
    public int EpisodeNumber { get; set; }
    public string EpisodeNumberTag => EpisodeNumber > 0 ? $"#{EpisodeNumber}" : "";

    /// <summary>0..1 progress fraction. 0 for unstarted, 1 for completed.</summary>
    public double Progress
    {
        get
        {
            if (PlayedState == "COMPLETED") return 1.0;
            if (PlayedState != "IN_PROGRESS" || DurationMs <= 0) return 0.0;
            return Math.Clamp((double)PlayedPositionMs / DurationMs, 0.0, 1.0);
        }
    }

    public bool IsInProgress => string.Equals(PlayedState, "IN_PROGRESS", StringComparison.Ordinal);
    public bool IsCompleted => string.Equals(PlayedState, "COMPLETED", StringComparison.Ordinal);
    public bool HasProgress => IsInProgress && Progress > 0 && Progress < 1;
    public string ListenActionText => IsInProgress ? "Resume" : IsCompleted ? "Replay" : "Play";
    public string StatusText => IsInProgress ? "In progress" : IsCompleted ? "Played" : "New";

    /// <summary>
    /// "Played" / "12 min left" / "Apr 30 · 2 hr 47 min" depending on state.
    /// Built once in the service so each row binds a string instead of running
    /// formatting in XAML converters per virtualised container.
    /// </summary>
    public string MetaLine { get; init; } = "";

    /// <summary>"Apr 30, 2026" — pre-formatted from ReleaseDate.</summary>
    public string DateText { get; init; } = "";

    /// <summary>"2 hr 47 min" / "47 min" / "12 min left" — pre-formatted.</summary>
    public string DurationOrRemainingText { get; init; } = "";

    public ShowEpisodeDto WithPlaybackProgress(long playedPositionMs, string? playedState)
    {
        var normalizedState = NormalizePlayedState(playedPositionMs, playedState);
        var normalizedPosition = Math.Max(0, playedPositionMs);
        if (DurationMs > 0)
            normalizedPosition = Math.Min(normalizedPosition, DurationMs);

        var durationOrRemaining = normalizedState switch
        {
            "COMPLETED" => "Played",
            "IN_PROGRESS" when DurationMs > 0 && normalizedPosition > 0
                => $"{FormatDuration(Math.Max(0, DurationMs - normalizedPosition))} left",
            _ => FormatDuration(DurationMs),
        };

        var metaLine = string.Join(" | ", new[] { DateText, durationOrRemaining }
            .Where(static s => !string.IsNullOrEmpty(s)));

        return new ShowEpisodeDto
        {
            Uri = Uri,
            Title = Title,
            DescriptionPreview = DescriptionPreview,
            CoverArtUrl = CoverArtUrl,
            DurationMs = DurationMs,
            ReleaseDate = ReleaseDate,
            PlayedState = normalizedState,
            PlayedPositionMs = normalizedPosition,
            IsExplicit = IsExplicit,
            IsVideo = IsVideo,
            IsPlayable = IsPlayable,
            EpisodeNumber = EpisodeNumber,
            MetaLine = metaLine,
            DateText = DateText,
            DurationOrRemainingText = durationOrRemaining,
            ShowUri = ShowUri,
            ShowName = ShowName,
            ShowImageUrl = ShowImageUrl,
            FullDescription = FullDescription,
            DescriptionHtml = DescriptionHtml,
        };
    }

    private string NormalizePlayedState(long playedPositionMs, string? playedState)
    {
        if (string.Equals(playedState, "COMPLETED", StringComparison.OrdinalIgnoreCase))
            return "COMPLETED";

        var normalizedPosition = Math.Max(0, playedPositionMs);
        if (DurationMs > 0 && normalizedPosition > 0 && DurationMs - normalizedPosition <= NearEndThresholdMs)
            return "COMPLETED";

        if (string.Equals(playedState, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase))
            return "IN_PROGRESS";

        if (normalizedPosition > 0)
            return "IN_PROGRESS";

        return "NOT_STARTED";
    }

    private static string FormatDuration(long durationMs)
    {
        if (durationMs <= 0) return "";
        var ts = TimeSpan.FromMilliseconds(durationMs);
        if (ts.TotalHours >= 1)
        {
            var hr = (int)ts.TotalHours;
            var min = ts.Minutes;
            return min > 0 ? $"{hr} hr {min} min" : $"{hr} hr";
        }

        var totalMin = Math.Max(1, (int)Math.Round(ts.TotalMinutes));
        return $"{totalMin} min";
    }
}
