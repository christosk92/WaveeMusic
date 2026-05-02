using System;

namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// One episode row inside the Show detail page. Combines the Pathfinder
/// metadata (title, description, duration, cover) with the local
/// EpisodeCacheEntry's playback progress so the row can render its played-state
/// pill without an extra fetch.
/// </summary>
public sealed class ShowEpisodeDto
{
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
}
