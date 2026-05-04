using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Controls.Lyrics.Models.Lyrics;

namespace Wavee.UI.Services;

internal sealed record LyricsSelectionCandidate(
    string Provider,
    LyricsData Data,
    double ContentScore,
    int PreferenceBonus,
    bool IsTimingReliable = true,
    string? TimingReason = null);

internal sealed record LyricsSelectionDecision(
    string Provider,
    LyricsData Data,
    string Reason);

internal static class LyricsProviderSelector
{
    public static LyricsSelectionDecision? SelectBestExternalResult(
        IEnumerable<LyricsSelectionCandidate> candidates)
    {
        LyricsSelectionCandidate? bestNonMusixmatch = null;
        double bestNonMusixmatchScore = double.MinValue;
        LyricsSelectionCandidate? musixmatchFallback = null;

        foreach (var candidate in candidates)
        {
            if (!candidate.IsTimingReliable)
                continue;

            if (candidate.Provider == "Musixmatch")
            {
                musixmatchFallback ??= candidate;
                continue;
            }

            bool hasSyllable = candidate.Data.LyricsLines.Any(l => l.IsPrimaryHasRealSyllableInfo);
            double composite = candidate.ContentScore * 100
                + (hasSyllable ? 50 : 0)
                + candidate.PreferenceBonus
                + Math.Min(candidate.Data.LyricsLines.Count, 20) * 0.1;

            if (composite > bestNonMusixmatchScore)
            {
                bestNonMusixmatchScore = composite;
                bestNonMusixmatch = candidate;
            }
        }

        if (bestNonMusixmatch != null)
        {
            bool hasSyllable = bestNonMusixmatch.Data.LyricsLines.Any(l => l.IsPrimaryHasRealSyllableInfo);
            return new LyricsSelectionDecision(
                bestNonMusixmatch.Provider,
                bestNonMusixmatch.Data,
                BuildReason(
                    "Content match",
                    bestNonMusixmatch.ContentScore,
                    hasSyllable,
                    bestNonMusixmatch.PreferenceBonus,
                    bestNonMusixmatch.Data.LyricsLines.Count,
                    bestNonMusixmatch.TimingReason));
        }

        if (musixmatchFallback != null)
        {
            bool hasSyllable = musixmatchFallback.Data.LyricsLines.Any(l => l.IsPrimaryHasRealSyllableInfo);
            return new LyricsSelectionDecision(
                musixmatchFallback.Provider,
                musixmatchFallback.Data,
                BuildReason(
                    "Fallback-only source - content match",
                    musixmatchFallback.ContentScore,
                    hasSyllable,
                    musixmatchFallback.PreferenceBonus,
                    musixmatchFallback.Data.LyricsLines.Count,
                    musixmatchFallback.TimingReason));
        }

        return null;
    }

    private static string BuildReason(
        string prefix,
        double contentScore,
        bool hasSyllable,
        int preferenceBonus,
        int lineCount,
        string? timingReason)
    {
        var reason = $"{prefix}: {contentScore:P0}, syllable: {hasSyllable}, pref: +{preferenceBonus}, lines: {lineCount}";
        if (!string.IsNullOrWhiteSpace(timingReason))
            reason += $", timing: {timingReason}";
        return reason;
    }
}
