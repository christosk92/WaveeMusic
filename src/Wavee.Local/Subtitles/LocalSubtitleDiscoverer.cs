using System.Text.RegularExpressions;

namespace Wavee.Local.Subtitles;

/// <summary>
/// Finds external subtitle files associated with a video on disk. Handles:
/// <list type="bullet">
///   <item>Sibling file: <c>Movie.srt</c>, <c>Movie.en.srt</c>, <c>Movie.eng.srt</c></item>
///   <item>Sibling <c>Subs/</c> folder: flat or per-episode-subdir (RARBG layout)</item>
///   <item>Language detected from suffix (ISO 639-1/639-2 code, full name, parent-dir name)</item>
///   <item>Flags (<c>.forced.</c>, <c>.sdh.</c>, <c>.cc.</c>) parsed from filename</item>
/// </list>
///
/// <para>Pure-function module: each method takes a virtual filesystem
/// (<see cref="IFileSystem"/>) so unit tests don't need real disk IO.</para>
/// </summary>
public static class LocalSubtitleDiscoverer
{
    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx",
    };

    /// <summary>
    /// Discovers all external subtitle files for the given video.
    /// </summary>
    public static IReadOnlyList<DiscoveredSubtitle> Discover(string videoPath, IFileSystem? fs = null)
    {
        fs ??= DefaultFileSystem.Instance;
        var results = new List<DiscoveredSubtitle>();

        var dir = Path.GetDirectoryName(videoPath);
        if (string.IsNullOrEmpty(dir) || !fs.DirectoryExists(dir))
            return results;

        var baseName = Path.GetFileNameWithoutExtension(videoPath);

        // 1) Sibling files in same dir: <base>.[lang.]ext
        foreach (var file in fs.EnumerateFiles(dir))
        {
            if (!SubtitleExtensions.Contains(Path.GetExtension(file))) continue;
            var stem = Path.GetFileNameWithoutExtension(file);
            if (MatchesBase(stem, baseName, out var langSuffix, out var flags))
            {
                results.Add(new DiscoveredSubtitle(
                    Path: Path.Combine(dir, file),
                    Language: DetectLanguage(langSuffix, parentDirName: null),
                    Forced: flags.Forced,
                    Sdh: flags.Sdh));
            }
        }

        // 2) Sibling Subs/ folder in same dir.
        var subsDir = Path.Combine(dir, "Subs");
        if (fs.DirectoryExists(subsDir))
        {
            CollectFromSubsDir(subsDir, baseName, fs, results);
        }

        // 3) Parent dir's Subs/ folder (RARBG season-folder layout):
        //    parent dir name == baseName's series-level stem.
        var parentDir = Path.GetDirectoryName(dir);
        if (!string.IsNullOrEmpty(parentDir) && fs.DirectoryExists(parentDir))
        {
            var parentSubsDir = Path.Combine(parentDir, "Subs");
            if (fs.DirectoryExists(parentSubsDir))
            {
                CollectFromSubsDir(parentSubsDir, baseName, fs, results);
            }
        }

        return results;
    }

    private static void CollectFromSubsDir(
        string subsDir,
        string baseName,
        IFileSystem fs,
        List<DiscoveredSubtitle> results)
    {
        // Flat layout: Subs/<base>.lang.ext
        foreach (var file in fs.EnumerateFiles(subsDir))
        {
            if (!SubtitleExtensions.Contains(Path.GetExtension(file))) continue;
            var stem = Path.GetFileNameWithoutExtension(file);
            if (MatchesBase(stem, baseName, out var langSuffix, out var flags))
            {
                results.Add(new DiscoveredSubtitle(
                    Path: Path.Combine(subsDir, file),
                    Language: DetectLanguage(langSuffix, parentDirName: null),
                    Forced: flags.Forced,
                    Sdh: flags.Sdh));
            }
        }

        // Per-episode subdir: Subs/<base>/<lang>.srt, Subs/<base>/2_English.srt
        foreach (var subDir in fs.EnumerateDirectories(subsDir))
        {
            // Subdir name should start with the base file name (with junk allowed after).
            if (!subDir.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)) continue;
            var fullSubDir = Path.Combine(subsDir, subDir);
            foreach (var file in fs.EnumerateFiles(fullSubDir))
            {
                if (!SubtitleExtensions.Contains(Path.GetExtension(file))) continue;
                var stem = Path.GetFileNameWithoutExtension(file);
                results.Add(new DiscoveredSubtitle(
                    Path: Path.Combine(fullSubDir, file),
                    Language: DetectLanguage(stem, parentDirName: subDir),
                    Forced: stem.Contains(".forced.", StringComparison.OrdinalIgnoreCase)
                            || stem.Contains("_forced", StringComparison.OrdinalIgnoreCase),
                    Sdh: stem.Contains(".sdh.", StringComparison.OrdinalIgnoreCase)
                         || stem.Contains(".cc.", StringComparison.OrdinalIgnoreCase)
                         || stem.Contains(".hi.", StringComparison.OrdinalIgnoreCase)));
            }
        }
    }

    private static bool MatchesBase(string stem, string baseName, out string langSuffix, out SubtitleFlags flags)
    {
        langSuffix = string.Empty;
        flags = new SubtitleFlags();

        if (!stem.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (stem.Length == baseName.Length)
            return true; // bare <base>.srt

        // Suffix after baseName must start with '.' or other separator
        var suffix = stem.Substring(baseName.Length);
        if (suffix[0] != '.' && suffix[0] != '_' && suffix[0] != '-')
            return false; // e.g. baseName "Movie" doesn't match "MovieTwo.srt"

        suffix = suffix.TrimStart('.', '_', '-');

        // Parse flags
        flags.Forced = Regex.IsMatch(suffix, @"\b(forced)\b", RegexOptions.IgnoreCase);
        flags.Sdh = Regex.IsMatch(suffix, @"\b(sdh|cc|hi)\b", RegexOptions.IgnoreCase);

        // Language code/name = the first token before any flag
        var tokens = suffix.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        langSuffix = tokens.Length > 0 ? tokens[0] : string.Empty;

        return true;
    }

    /// <summary>
    /// Parse a single subtitle file path into a <see cref="DiscoveredSubtitle"/>
    /// using the same language / forced / SDH heuristics the scanner uses for
    /// sibling-file discovery. Used by the drag-drop path on the player surface
    /// — a user-dropped <c>Movie.eng.forced.srt</c> gets the same metadata
    /// shape as if the scanner had picked it up.
    /// </summary>
    public static DiscoveredSubtitle ParseFromPath(string subtitlePath)
    {
        var stem = Path.GetFileNameWithoutExtension(subtitlePath);

        // Try the full stem as the language suffix (handles "filename.en.srt"
        // when the user drops a file whose stem is already an ISO code, and
        // also passes the bare filename through DetectLanguage which tolerates
        // both code + name + parent dir name).
        var lang = DetectLanguage(stem, parentDirName: Path.GetFileName(Path.GetDirectoryName(subtitlePath)));

        var forced = Regex.IsMatch(stem, @"\b(forced)\b", RegexOptions.IgnoreCase);
        var sdh = Regex.IsMatch(stem, @"\b(sdh|cc|hi)\b", RegexOptions.IgnoreCase);

        return new DiscoveredSubtitle(
            Path: subtitlePath,
            Language: lang,
            Forced: forced,
            Sdh: sdh);
    }

    /// <summary>
    /// Resolves language by priority:
    ///   ISO-639 code suffix → language-name suffix → parent-dir name → null.
    /// </summary>
    public static string? DetectLanguage(string suffix, string? parentDirName)
    {
        if (!string.IsNullOrWhiteSpace(suffix))
        {
            var code = LanguageCodes.NormalizeToIso639_1(suffix);
            if (code is not null) return code;

            var fromName = LanguageCodes.FromLanguageName(suffix);
            if (fromName is not null) return fromName;
        }

        if (!string.IsNullOrWhiteSpace(parentDirName))
        {
            var fromName = LanguageCodes.FromLanguageName(parentDirName);
            if (fromName is not null) return fromName;
        }

        return null;
    }

    private struct SubtitleFlags
    {
        public bool Forced;
        public bool Sdh;
    }
}

/// <summary>Single discovered subtitle entry.</summary>
public sealed record DiscoveredSubtitle(
    string Path,
    string? Language,
    bool Forced,
    bool Sdh);

/// <summary>
/// Filesystem abstraction so subtitle-discovery tests can run against a
/// virtual fixture instead of real disk.
/// </summary>
public interface IFileSystem
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    IEnumerable<string> EnumerateFiles(string dir);
    IEnumerable<string> EnumerateDirectories(string dir);
}

internal sealed class DefaultFileSystem : IFileSystem
{
    public static readonly DefaultFileSystem Instance = new();
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool FileExists(string path) => File.Exists(path);
    public IEnumerable<string> EnumerateFiles(string dir) =>
        Directory.EnumerateFiles(dir).Select(Path.GetFileName)!;
    public IEnumerable<string> EnumerateDirectories(string dir) =>
        Directory.EnumerateDirectories(dir).Select(Path.GetFileName)!;
}

/// <summary>
/// Minimal ISO-639-1/639-2 mapping for languages commonly seen in
/// subtitle filenames. Not exhaustive — covers the top ~40 languages by
/// content volume.
/// </summary>
internal static class LanguageCodes
{
    private static readonly Dictionary<string, string> NameToIso = new(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "en", ["eng"] = "en",
        ["spanish"] = "es", ["esp"] = "es", ["spa"] = "es",
        ["french"] = "fr", ["fre"] = "fr", ["fra"] = "fr",
        ["german"] = "de", ["ger"] = "de", ["deu"] = "de",
        ["italian"] = "it", ["ita"] = "it",
        ["portuguese"] = "pt", ["por"] = "pt",
        ["dutch"] = "nl", ["nld"] = "nl",
        ["russian"] = "ru", ["rus"] = "ru",
        ["japanese"] = "ja", ["jpn"] = "ja",
        ["korean"] = "ko", ["kor"] = "ko",
        ["chinese"] = "zh", ["chi"] = "zh", ["zho"] = "zh",
        ["arabic"] = "ar", ["ara"] = "ar",
        ["hindi"] = "hi", ["hin"] = "hi",
        ["turkish"] = "tr", ["tur"] = "tr",
        ["polish"] = "pl", ["pol"] = "pl",
        ["swedish"] = "sv", ["swe"] = "sv",
        ["norwegian"] = "no", ["nor"] = "no",
        ["danish"] = "da", ["dan"] = "da",
        ["finnish"] = "fi", ["fin"] = "fi",
        ["greek"] = "el", ["gre"] = "el",
        ["hebrew"] = "he", ["heb"] = "he",
        ["thai"] = "th", ["tha"] = "th",
        ["vietnamese"] = "vi", ["vie"] = "vi",
        ["indonesian"] = "id", ["ind"] = "id",
        ["czech"] = "cs", ["cze"] = "cs",
        ["hungarian"] = "hu", ["hun"] = "hu",
        ["romanian"] = "ro", ["rum"] = "ro",
        ["bulgarian"] = "bg", ["bul"] = "bg",
        ["ukrainian"] = "uk", ["ukr"] = "uk",
        ["malay"] = "ms", ["may"] = "ms",
        ["bangla"] = "bn", ["bengali"] = "bn", ["ben"] = "bn",
    };

    public static string? NormalizeToIso639_1(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        // 2-char codes that match Iso639-1 directly
        if (code.Length == 2 && IsoLetters(code)) return code.ToLowerInvariant();
        // 3-char Iso639-2/T or -2/B codes — look up in our map
        if (code.Length == 3 && IsoLetters(code) && NameToIso.TryGetValue(code, out var two)) return two;
        return null;
    }

    public static string? FromLanguageName(string name)
    {
        return NameToIso.TryGetValue(name.Trim(), out var iso) ? iso : null;
    }

    private static bool IsoLetters(string s)
    {
        foreach (var c in s)
            if (!char.IsLetter(c)) return false;
        return true;
    }
}
