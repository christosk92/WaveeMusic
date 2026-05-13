using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Wavee.UI.Services;

/// <summary>One chapter cue inside a video file.</summary>
public sealed record EpisodeChapter(string? Title, long StartTimeMs);

/// <summary>
/// Decides when the local-TV "Up Next" overlay should appear.
/// </summary>
internal static class LocalCreditsDetector
{
    /// <summary>How long before the file's natural end the card surfaces when no chapter marker is found.</summary>
    public const long FallbackPreEndMs = 30_000;

    /// <summary>Episodes shorter than this do not get a meaningful tail.</summary>
    public const long ShortEpisodeThresholdMs = 60_000;

    private const long AmbiguousCreditsMinStartMs = 10 * 60_000;
    private const double AmbiguousCreditsMinProgress = 0.70;

    private static readonly Regex ExplicitEndCreditsTitleRegex = new(
        @"^\s*(end|closing|final)\s*credits?\s*$|^\s*outro\s*$|^\s*end\s*card\s*$|^\s*ending\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AmbiguousCreditsTitleRegex = new(
        @"^\s*credits?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns the position in milliseconds at which the Up Next overlay
    /// should appear, or <c>null</c> when the duration is not usable.
    /// </summary>
    public static long? GetTriggerMs(IReadOnlyList<EpisodeChapter>? chapters, long durationMs)
    {
        if (durationMs <= 0) return null;

        if (chapters is { Count: > 0 })
        {
            for (var i = 0; i < chapters.Count; i++)
            {
                var chapter = chapters[i];
                if (string.IsNullOrWhiteSpace(chapter.Title)) continue;
                if (!IsValidChapterStart(chapter.StartTimeMs, durationMs)) continue;

                if (ExplicitEndCreditsTitleRegex.IsMatch(chapter.Title))
                    return chapter.StartTimeMs;

                if (AmbiguousCreditsTitleRegex.IsMatch(chapter.Title)
                    && IsPlausiblyEndCredits(chapter.StartTimeMs, durationMs))
                {
                    return chapter.StartTimeMs;
                }
            }
        }

        if (durationMs <= ShortEpisodeThresholdMs)
            return 0;

        return durationMs - FallbackPreEndMs;
    }

    private static bool IsValidChapterStart(long startTimeMs, long durationMs)
        => startTimeMs >= 0 && startTimeMs < durationMs;

    private static bool IsPlausiblyEndCredits(long startTimeMs, long durationMs)
    {
        // Many TV files label the opening title sequence simply "Credits".
        // Treat that bare label as end credits only when it is both late in
        // the runtime and not in the cold-open/intro portion.
        return startTimeMs >= AmbiguousCreditsMinStartMs
               && startTimeMs >= durationMs * AmbiguousCreditsMinProgress;
    }
}
