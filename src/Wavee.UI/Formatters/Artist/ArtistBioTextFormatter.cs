using System;
using Wavee.UI.Helpers;

namespace Wavee.UI.Formatters.Artist;

/// <summary>
/// Hero-card biography one-liner derivation extracted from ArtistViewModel.
/// All inputs and outputs are primitives — fully testable from Wavee.UI.Tests
/// without a fixture artist.
/// </summary>
internal static class ArtistBioTextFormatter
{
    /// <summary>
    /// Length cap before the trailing ellipsis kicks in. Default matches the
    /// historical inline value (147 chars + "..." = 150 visible).
    /// </summary>
    public const int DefaultMaxLength = 150;

    /// <summary>
    /// Builds the hero-card bio one-liner. Prefers <paramref name="biography"/>'s
    /// first sentence, falls back to <paramref name="summary"/>'s. Strips HTML,
    /// decodes entities, removes the "<artist> is/are/was/were" prefix, strips
    /// "A/An/The" articles, fixes sentence casing, and truncates to <paramref name="maxLength"/>.
    /// Returns empty string when both sources are blank.
    /// </summary>
    public static string BuildHeroBioLine(string? biography, string? summary, string? artistName, int maxLength = DefaultMaxLength)
    {
        var source = FirstSentenceOrText(biography) ?? FirstSentenceOrText(summary);
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;

        // SpotifyHtmlHelper.StripHtml handles tag-stripping AND HTML-entity
        // decoding; we just need to collapse br-derived newlines back to spaces
        // for the one-liner shape.
        var stripped = SpotifyHtmlHelper.StripHtml(source) ?? string.Empty;
        var line = stripped.Replace('\r', ' ').Replace('\n', ' ').Trim();

        if (!string.IsNullOrWhiteSpace(artistName))
            line = StripLeadingArtistSubject(line, artistName!);

        line = StripLeadingArticle(line);
        line = EnsureSentenceCasing(line);

        if (maxLength > 3 && line.Length > maxLength)
        {
            // Truncate to (maxLength - 3) for the ellipsis, then trim trailing
            // whitespace so we don't get " ...".
            line = line.Substring(0, maxLength - 3).TrimEnd() + "...";
        }
        return line;
    }

    /// <summary>
    /// Returns the first sentence (up to and including the first ". " boundary)
    /// or the whole input when no sentence break is found. CR/LF collapsed to
    /// spaces; null/blank → null.
    /// </summary>
    public static string? FirstSentenceOrText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = text!.Replace('\r', ' ').Replace('\n', ' ').Trim();
        var end = normalized.IndexOf(". ", StringComparison.Ordinal);
        return end > 0 ? normalized.Substring(0, end + 1) : normalized;
    }

    /// <summary>
    /// Removes "<artistName> is/are/was/were " from the front of <paramref name="line"/>
    /// so the bio reads as a sentence fragment about the artist rather than
    /// repeating the artist's name. Case-insensitive. Returns the input
    /// unchanged when no prefix matches.
    /// </summary>
    public static string StripLeadingArtistSubject(string line, string artistName)
    {
        var prefixes = new[]
        {
            $"{artistName} is ",
            $"{artistName} are ",
            $"{artistName} was ",
            $"{artistName} were "
        };
        foreach (var prefix in prefixes)
        {
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return line.Substring(prefix.Length).TrimStart();
        }
        return line;
    }

    /// <summary>
    /// Strips a leading "A ", "An ", or "The " article (case-insensitive).
    /// </summary>
    public static string StripLeadingArticle(string line)
    {
        if (line.StartsWith("a ", StringComparison.OrdinalIgnoreCase)) return line[2..].TrimStart();
        if (line.StartsWith("an ", StringComparison.OrdinalIgnoreCase)) return line[3..].TrimStart();
        if (line.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) return line[4..].TrimStart();
        return line;
    }

    /// <summary>
    /// Capitalizes the first character (invariant) and appends a period when
    /// the line doesn't already end in <c>.</c>, <c>!</c>, or <c>?</c>.
    /// Returns empty string for null/whitespace input.
    /// </summary>
    public static string EnsureSentenceCasing(string line)
    {
        line = (line ?? string.Empty).Trim();
        if (line.Length == 0) return string.Empty;
        line = char.ToUpperInvariant(line[0]) + line[1..];
        return line.EndsWith(".", StringComparison.Ordinal)
            || line.EndsWith("!", StringComparison.Ordinal)
            || line.EndsWith("?", StringComparison.Ordinal)
            ? line
            : line + ".";
    }
}
