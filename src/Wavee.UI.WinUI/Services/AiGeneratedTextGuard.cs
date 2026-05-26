using System;

namespace Wavee.UI.WinUI.Services;

internal static class AiGeneratedTextGuard
{
    public const string NoArtistSummary = "NO_ARTIST_SUMMARY";
    public const string NoAlbumSummary = "NO_ALBUM_SUMMARY";

    public static bool IsInvalidGeneratedText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = Normalize(text);

        if (string.Equals(normalized, Normalize(NoArtistSummary), StringComparison.Ordinal)
            || string.Equals(normalized, Normalize(NoAlbumSummary), StringComparison.Ordinal))
        {
            return true;
        }

        if (ContainsAny(
                normalized,
                "music_grounding",
                "spotify_biography",
                "album_facts",
                "popular_tracks",
                "web_results",
                "wikipedia data",
                "prompt is inadequate",
                "no actual evidence provided",
                "no evidence provided",
                "no training information",
                "please provide relevant information",
                "please provide spotify biography",
                "unable to fulfill",
                "unable to fulfil",
                "unable to comply",
                "cannot fulfill",
                "cannot fulfil",
                "as an ai"))
        {
            return true;
        }

        return ContainsAll(normalized, "unfortunately", "unable")
               || ContainsAll(normalized, "instructions", "unable")
               || ContainsAll(normalized, "prompt", "inadequate")
               || ContainsAll(normalized, "no", "evidence", "provided")
               || ContainsAll(normalized, "please provide", "information")
               || ContainsAll(normalized, "i am unable", "request")
               || ContainsAll(normalized, "i cannot", "request")
               || ContainsAll(normalized, "i can't", "request");
    }

    public static bool IsInvalidGeneratedTextInProgress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = Normalize(text);
        return IsSentinelPrefix(normalized, NoArtistSummary)
               || IsSentinelPrefix(normalized, NoAlbumSummary)
               || IsInvalidGeneratedText(text);
    }

    private static string Normalize(string value)
    {
        var collapsed = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Trim()
            .ToLowerInvariant();

        while (collapsed.Contains("  ", StringComparison.Ordinal))
            collapsed = collapsed.Replace("  ", " ", StringComparison.Ordinal);

        return collapsed;
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool ContainsAll(string text, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (!text.Contains(needle, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool IsSentinelPrefix(string normalizedText, string sentinel)
    {
        var normalizedSentinel = Normalize(sentinel);
        return normalizedSentinel.StartsWith(normalizedText, StringComparison.Ordinal)
               || normalizedText.StartsWith(normalizedSentinel, StringComparison.Ordinal);
    }
}
