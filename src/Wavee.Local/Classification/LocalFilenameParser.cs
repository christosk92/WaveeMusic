using System.Text.RegularExpressions;

namespace Wavee.Local.Classification;

/// <summary>
/// Extracts hints from local-file names: episode markers, year-in-parens,
/// artist-title patterns, release-group garbage, etc. Used by
/// <see cref="LocalContentClassifier"/> for kind detection and by the resolver
/// to produce display metadata when ATL tags are absent.
///
/// <para>Pure-function module — every method is static and side-effect-free,
/// trivially unit-testable.</para>
/// </summary>
public static class LocalFilenameParser
{
    // Match SxxExx, S01.E01, S1E1, Season 1 Episode 1, 1x01, etc.
    private static readonly Regex EpisodeRegex = new(
        @"(?ix)
          (?:^|[.\s_-])
          (?:
              s(?<s>\d{1,2})[.\s_-]?e(?<e>\d{1,3})
            | season[\s_-]?(?<s>\d{1,2})[\s_-]+(?:episode|ep)[\s_-]?(?<e>\d{1,3})
            | (?<s>\d{1,2})x(?<e>\d{1,3})
          )
          (?=[.\s_-]|$)
        ",
        RegexOptions.Compiled);

    // Year in parens or bare, surrounded by separators. Matches 1900–2099.
    private static readonly Regex YearRegex = new(
        @"(?<![\d])(?<y>(?:19|20)\d{2})(?![\d])",
        RegexOptions.Compiled);

    // Disc-folder convention: "CD1", "Disc 02", etc.
    private static readonly Regex DiscFolderRegex = new(
        @"^(?:CD|Disc)\s*0*(?<d>\d{1,2})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Track-number prefix: "01 - Title", "01_Title", "01. Title", "1. Title".
    private static readonly Regex TrackNumberPrefixRegex = new(
        @"^\s*(?<n>\d{1,2})\s*[.\-_]\s*",
        RegexOptions.Compiled);

    // Release-group / encoding markers that should be stripped from titles.
    private static readonly string[] ReleaseGroupMarkers =
    [
        // Resolutions
        "2160p", "1080p", "720p", "480p", "4K", "HD",
        // Source / format
        "BluRay", "Blu-Ray", "BDRip", "BRRip", "DVDRip", "WEB-DL", "WEBDL", "WEB-Rip",
        "WEBRip", "HDTV", "HDRip", "HDCAM", "CAMRip", "DVDScr", "DDP", "AAC",
        // Codecs
        "x264", "x265", "HEVC", "H264", "H265", "AVC", "VC1", "XviD", "DivX",
        // Audio
        "DTS", "DTS-HD", "DTS-X", "TrueHD", "AC3", "EAC3", "Atmos", "5.1", "7.1",
        // HDR
        "HDR", "HDR10", "DolbyVision", "DV", "10bit", "8bit",
        // Release-group suffixes (common torrent groups)
        "RARBG", "YIFY", "YTS", "FGT", "GalaxyTV", "GalaxyRG", "Vyndros",
        "EVO", "AMIABLE", "TGx", "JoFlix", "BONE",
        // Misc tags
        "PROPER", "REPACK", "INTERNAL", "LIMITED", "UNRATED", "EXTENDED",
        "DC", "DirectorsCut", "REMASTERED", "REMASTER",
    ];

    private static readonly Regex ReleaseGroupRegex = new(
        @"(?ix)[.\s_-]+(?:" + string.Join("|", ReleaseGroupMarkers.Select(Regex.Escape)) + @")\b",
        RegexOptions.Compiled);

    // Music-video signals — markers that suggest the video is a music video.
    private static readonly Regex MusicVideoSignalRegex = new(
        @"(?ix)
          \b(?:
              MV
            | M/V
            | Official\s+Video
            | Official\s+Music\s+Video
            | Music\s+Video
            | Lyric\s+Video
            | Audio
            | Performance
            | Live\s+at
            | Stone\s+Music
            | (?:slowed\s*\+?\s*reverb)
            | (?:Topic)
          )\b
        ",
        RegexOptions.Compiled);

    /// <summary>
    /// Tries to parse a TV-episode marker out of the filename.
    /// Returns true if a SxxExx-style match is found anywhere in the name.
    /// </summary>
    public static bool TryParseEpisode(string filename, out string seriesName, out int season, out int episode, out string? episodeTitle)
    {
        seriesName = string.Empty;
        season = 0;
        episode = 0;
        episodeTitle = null;

        var baseName = Path.GetFileNameWithoutExtension(filename);
        var m = EpisodeRegex.Match(baseName);
        if (!m.Success) return false;

        season = int.Parse(m.Groups["s"].Value);
        episode = int.Parse(m.Groups["e"].Value);

        // Everything before the SxxExx marker = series name (cleaned).
        var seriesRaw = baseName.Substring(0, m.Index);
        seriesName = CleanTitle(seriesRaw);

        // Everything after = episode title candidate (cleaned of release-group junk).
        var afterMarker = baseName.Substring(m.Index + m.Length);
        var afterClean = CleanTitle(StripReleaseGroupMarkers(afterMarker));
        if (!string.IsNullOrWhiteSpace(afterClean) && afterClean.Length > 1)
            episodeTitle = afterClean;

        return seriesName.Length > 0;
    }

    /// <summary>
    /// Tries to parse a movie title + year from the filename.
    /// Year-in-parens or bare 4-digit year is required to disambiguate from
    /// arbitrary file names.
    /// </summary>
    public static bool TryParseMovie(string filename, out string title, out int? year)
    {
        title = string.Empty;
        year = null;

        var baseName = Path.GetFileNameWithoutExtension(filename);

        // Don't accept an episode-shaped name as a movie.
        if (EpisodeRegex.IsMatch(baseName)) return false;

        var yMatch = YearRegex.Match(baseName);
        if (yMatch.Success)
        {
            year = int.Parse(yMatch.Groups["y"].Value);
            // Title = everything before the year token.
            var before = baseName.Substring(0, yMatch.Index);
            title = CleanTitle(StripReleaseGroupMarkers(before));
        }
        else
        {
            // No year found → strip release-group markers and use what's left.
            title = CleanTitle(StripReleaseGroupMarkers(baseName));
        }

        return title.Length > 0;
    }

    /// <summary>
    /// Tries to parse "Artist - Title" or "Artist - Title (Music Video Markers)"
    /// from the filename for a music-video.
    /// </summary>
    public static bool TryParseMusicVideo(string filename, out string? artist, out string title)
    {
        artist = null;
        title = string.Empty;

        var baseName = Path.GetFileNameWithoutExtension(filename);
        var stripped = StripReleaseGroupMarkers(baseName);
        var cleaned = CleanTitle(stripped);

        // Look for "Artist - Title" split. Take FIRST hyphen-with-spaces since
        // titles can contain dashes (e.g. "Artist - Title - 1080p").
        var splitIndex = IndexOfHyphenSeparator(cleaned);
        if (splitIndex > 0)
        {
            artist = cleaned.Substring(0, splitIndex).Trim();
            title = cleaned.Substring(splitIndex + 1).Trim();
            // Trim trailing parens like "(slowed + reverb)" only if very obvious noise.
            title = StripTrailingNoise(title);
            return artist.Length > 0 && title.Length > 0;
        }

        title = cleaned;
        return title.Length > 0;
    }

    /// <summary>
    /// Tries to parse a music track filename, accepting the common patterns:
    ///   "01 - Title.mp3", "01_Title.mp3", "01. Title.mp3"
    ///   "Artist - Title.mp3"
    ///   "Artist - Album - 01 - Title.mp3"
    ///   "Title.mp3"
    /// Folder hints are used when the filename alone is ambiguous.
    /// </summary>
    public static MusicTrackHints TryParseMusicTrack(string filename, string? parentFolder = null, string? grandparentFolder = null)
    {
        var baseName = Path.GetFileNameWithoutExtension(filename);
        int? trackNumber = null;
        int? discNumber = null;
        string? artist = null;
        string? album = null;
        string title;

        // Disc-folder convention: parent folder is "CD1" / "Disc 2", grandparent is album.
        if (parentFolder is not null && DiscFolderRegex.Match(parentFolder) is { Success: true } discM)
        {
            discNumber = int.Parse(discM.Groups["d"].Value);
            album = grandparentFolder?.Trim();
            // grandparent's parent (not passed in) would be artist; we leave artist null
            // unless caller passes the great-grandparent later.
        }
        else
        {
            // Standard `\Artist\Album\track.mp3` layout.
            album = parentFolder?.Trim();
            artist = grandparentFolder?.Trim();
        }

        // Strip leading "01 - " / "01_" / "1. " etc.
        var work = baseName;
        var trackM = TrackNumberPrefixRegex.Match(work);
        if (trackM.Success)
        {
            trackNumber = int.Parse(trackM.Groups["n"].Value);
            work = work.Substring(trackM.Length).Trim();
        }

        // Split on " - " separators inside the remaining filename:
        //   Title                       (just title)
        //   Artist - Title              (artist + title)
        //   Artist - Album - Title      (artist + album + title — album wins over folder)
        var parts = SplitByHyphen(work).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        switch (parts.Count)
        {
            case 0:
                title = work.Trim();
                break;
            case 1:
                title = parts[0];
                break;
            case 2:
                artist ??= parts[0];
                title = parts[1];
                break;
            default:
                artist ??= parts[0];
                album ??= parts[1];
                title = string.Join(" - ", parts.Skip(2));
                break;
        }

        return new MusicTrackHints(
            Artist: NullIfBlank(artist),
            Album: NullIfBlank(album),
            Title: NullIfBlank(title) ?? baseName,
            TrackNumber: trackNumber,
            DiscNumber: discNumber);
    }

    /// <summary>
    /// Strips known release-group / encoding markers from a string.
    /// Idempotent: running it twice produces the same output.
    /// </summary>
    public static string StripReleaseGroupMarkers(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        return ReleaseGroupRegex.Replace(input, "");
    }

    /// <summary>True if the filename contains a release-group / encoding marker
    /// (1080p, BluRay, x265, WEB-DL, HEVC, etc.). Strong signal for video content.</summary>
    public static bool HasReleaseGroupMarkers(string filename)
    {
        return ReleaseGroupRegex.IsMatch(filename);
    }

    /// <summary>True if the filename has music-video signal words
    /// (MV, Official Video, slowed+reverb, etc.).</summary>
    public static bool HasMusicVideoSignals(string filename)
    {
        return MusicVideoSignalRegex.IsMatch(filename);
    }

    private static string CleanTitle(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        // Replace dots / underscores with spaces; collapse whitespace.
        var work = s.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ');
        var collapsed = Regex.Replace(work, @"\s+", " ").Trim();
        // Strip wrapping brackets that were left dangling by the strip pass.
        collapsed = Regex.Replace(collapsed, @"^\s*[\[\(]\s*|\s*[\]\)]\s*$", "");
        return collapsed.Trim();
    }

    private static int IndexOfHyphenSeparator(string s)
    {
        // Find " - " preceded by a non-space char. Avoids matching at position 0
        // or after a trailing space.
        for (int i = 1; i < s.Length - 1; i++)
        {
            if (s[i] == '-' && s[i - 1] == ' ' && s[i + 1] == ' ')
                return i;
        }
        return -1;
    }

    private static IEnumerable<string> SplitByHyphen(string s)
    {
        var pieces = new List<string>();
        int start = 0;
        for (int i = 1; i < s.Length - 1; i++)
        {
            if (s[i] == '-' && s[i - 1] == ' ' && s[i + 1] == ' ')
            {
                pieces.Add(s.Substring(start, i - 1 - start));
                start = i + 2;
                i++; // skip past the space we just consumed
            }
        }
        if (start <= s.Length)
            pieces.Add(s.Substring(start));
        return pieces;
    }

    private static string StripTrailingNoise(string s)
    {
        // Remove " (Official Video)" / " [Music Video]" trailing tags only if
        // they look like a wrapped tag (no other content after).
        return Regex.Replace(s, @"\s*[\[\(][^\]\)]{0,40}[\]\)]\s*$", "").Trim();
    }

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>
/// Parsed hints for a music-track filename. Any field may be null if the
/// filename / folder didn't carry that signal.
/// </summary>
public sealed record MusicTrackHints(
    string? Artist,
    string? Album,
    string Title,
    int? TrackNumber,
    int? DiscNumber);
