using System;
using System.Collections.Generic;
using System.Globalization;
using Wavee.UI.Localization;

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
            parts.Add(LocalizeReleaseType(releaseType!));

        if (year is int y && y > 0)
            parts.Add(y.ToString(CultureInfo.InvariantCulture));

        if (itemCount is int c && c > 0)
            parts.Add(c == 1
                ? LocalizationHook.GetString(SingularKey(noun))
                : LocalizationHook.Format(PluralKey(noun), c));

        return parts.Count == 0 ? string.Empty : string.Join(Sep, parts);
    }

    private static string SingularKey(CountNoun noun) => noun switch
    {
        CountNoun.Song    => "Count_Song_One",
        CountNoun.Track   => "Count_Track_One",
        CountNoun.Episode => "Count_Episode_One",
        _                 => "Count_Song_One",
    };

    private static string PluralKey(CountNoun noun) => noun switch
    {
        CountNoun.Song    => "Count_Song_Many",
        CountNoun.Track   => "Count_Track_Many",
        CountNoun.Episode => "Count_Episode_Many",
        _                 => "Count_Song_Many",
    };

    private static string LocalizeReleaseType(string type) => type.ToLowerInvariant() switch
    {
        "single"      => LocalizationHook.GetString("ReleaseType_Single"),
        "ep"          => LocalizationHook.GetString("ReleaseType_EP"),
        "album"       => LocalizationHook.GetString("ReleaseType_Album"),
        "compilation" => LocalizationHook.GetString("ReleaseType_Compilation"),
        _             => TitleCase(type),
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