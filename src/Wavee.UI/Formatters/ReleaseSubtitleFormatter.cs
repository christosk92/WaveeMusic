using System;
using System.Collections.Generic;
using System.Globalization;

namespace Wavee.UI.Formatters;

/// <summary>
/// Builds the dot-separated "Album · 2023 · 12 songs" subtitle string used
/// across artist / album / playlist surfaces. Centralizes the plural rules
/// (1 song / 12 songs, 1 track / 12 tracks) so every surface phrases counts
/// consistently. Replaces the per-VM concatenation duplicated 5 times in the
/// audit.
/// </summary>
internal static class ReleaseSubtitleFormatter
{
    /// <summary>Separator dot — U+00B7 MIDDLE DOT with surrounding spaces. Matches the existing XAML usage.</summary>
    private const string Sep = " · ";

    public enum CountNoun { Song, Track, Episode }

    /// <summary>
    /// "<paramref name="releaseType"/>{Sep}<paramref name="year"/>{Sep}{N} songs".
    /// Each segment is included only when non-null / non-empty / positive.
    /// Returns the empty string when every segment is absent.
    /// </summary>
    public static string Format(string? releaseType, int? year, int? itemCount, CountNoun noun = CountNoun.Song)
    {
        var parts = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(releaseType))
            parts.Add(TitleCase(releaseType!));

        if (year is int y && y > 0)
            parts.Add(y.ToString(CultureInfo.InvariantCulture));

        if (itemCount is int c && c > 0)
            parts.Add(c == 1 ? $"1 {Singular(noun)}" : $"{c} {Plural(noun)}");

        return parts.Count == 0 ? string.Empty : string.Join(Sep, parts);
    }

    private static string Singular(CountNoun noun) => noun switch
    {
        CountNoun.Song    => "song",
        CountNoun.Track   => "track",
        CountNoun.Episode => "episode",
        _                 => "song",
    };

    private static string Plural(CountNoun noun) => noun switch
    {
        CountNoun.Song    => "songs",
        CountNoun.Track   => "tracks",
        CountNoun.Episode => "episodes",
        _                 => "songs",
    };

    /// <summary>
    /// Title-cases lowercase release-type strings ("album" → "Album"). Spotify's
    /// release-type field comes in as lowercase from Pathfinder; XAML always
    /// renders it capitalized. Invariant culture so cross-locale builds stay
    /// stable.
    /// </summary>
    private static string TitleCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        // Cheap path: only the first character needs to change.
        if (char.IsUpper(s[0])) return s;
        return char.ToUpper(s[0], CultureInfo.InvariantCulture) + s[1..];
    }
}
