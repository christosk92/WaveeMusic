using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wavee.Core.Http.Lyrics;

/// <summary>
/// Client for the AMLL-TTML-DB — a GitHub-hosted database of syllable-synced TTML lyrics.
/// Downloads a JSONL index, searches by title/artist, then fetches the matching TTML file.
/// </summary>
public sealed class AmllTtmlDbClient
{
    private const string BaseUrl = "https://raw.githubusercontent.com/amll-dev/amll-ttml-db/refs/heads/main";
    private const string IndexUrl = BaseUrl + "/metadata/raw-lyrics-index.jsonl";
    private const string RawLyricsPrefix = BaseUrl + "/raw-lyrics/";
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromHours(24);

    private static readonly HttpClient HttpClient = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "Wavee/1.0" } }
    };

    private static readonly SemaphoreSlim DownloadLock = new(1, 1);
    private static readonly Regex PunctuationRegex = new(@"[\p{P}\p{S}]", RegexOptions.Compiled);
    private static readonly Regex CollapseWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static string CacheDir => Path.Combine(Path.GetTempPath(), "wavee");
    private static string IndexPath => Path.Combine(CacheDir, "amll-ttml-index.jsonl");
    private static string TimestampPath => Path.Combine(CacheDir, "amll-ttml-index-ts.txt");

    /// <summary>
    /// Searches the AMLL-TTML-DB index for a matching track and returns the TTML file path, or null.
    /// </summary>
    public async Task<string?> SearchAsync(string title, string artist, CancellationToken ct)
    {
        var indexPath = await EnsureIndexCachedAsync(ct);
        if (indexPath == null || !File.Exists(indexPath))
            return null;

        var queryTitle = NormalizeForMatch(title);
        var queryArtist = NormalizeForMatch(artist);
        if (string.IsNullOrEmpty(queryTitle))
            return null;

        string? bestFile = null;
        int bestScore = 0;

        using var reader = new StreamReader(indexPath);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length < 10) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (!root.TryGetProperty("metadata", out var metadata)) continue;
                if (!root.TryGetProperty("rawLyricFile", out var rawFile)) continue;

                string? indexTitle = null;
                string? indexArtist = null;

                foreach (var entry in metadata.EnumerateArray())
                {
                    if (entry.GetArrayLength() != 2) continue;
                    var key = entry[0].GetString();
                    var values = entry[1];

                    if (key == "musicName" && values.GetArrayLength() > 0)
                        indexTitle = values[0].GetString();
                    else if (key == "artists" && values.GetArrayLength() > 0)
                    {
                        var parts = new List<string>();
                        foreach (var v in values.EnumerateArray())
                        {
                            var s = v.GetString();
                            if (!string.IsNullOrEmpty(s)) parts.Add(s);
                        }
                        indexArtist = string.Join(" ", parts);
                    }
                }

                var normTitle = NormalizeForMatch(indexTitle);
                var normArtist = NormalizeForMatch(indexArtist);

                if (string.IsNullOrEmpty(normTitle)) continue;

                // Title must match exactly (normalized)
                if (normTitle != queryTitle) continue;

                // Score artist match
                int score = 1; // title matched
                if (!string.IsNullOrEmpty(queryArtist) && !string.IsNullOrEmpty(normArtist))
                {
                    if (normArtist == queryArtist)
                        score = 10; // exact artist match
                    else if (normArtist.Contains(queryArtist, StringComparison.Ordinal)
                             || queryArtist.Contains(normArtist, StringComparison.Ordinal))
                        score = 5; // partial artist match
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestFile = rawFile.GetString();
                    if (score >= 10) break; // exact match, stop searching
                }
            }
            catch (JsonException)
            {
                // Malformed line, skip
            }
        }

        return bestFile;
    }

    /// <summary>
    /// Fetches the TTML content from GitHub given the rawLyricFile path.
    /// </summary>
    public async Task<string?> FetchTtmlAsync(string rawLyricFile, CancellationToken ct)
    {
        var url = RawLyricsPrefix + rawLyricFile;
        var response = await HttpClient.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Ensures the JSONL index is cached locally, downloading if stale or missing.
    /// </summary>
    private static async Task<string?> EnsureIndexCachedAsync(CancellationToken ct)
    {
        // Quick check without lock
        if (IsIndexFresh())
            return IndexPath;

        await DownloadLock.WaitAsync(ct);
        try
        {
            // Re-check after acquiring lock
            if (IsIndexFresh())
                return IndexPath;

            Directory.CreateDirectory(CacheDir);

            using var response = await HttpClient.GetAsync(IndexUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return File.Exists(IndexPath) ? IndexPath : null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var fs = new FileStream(IndexPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await stream.CopyToAsync(fs, ct);

            await File.WriteAllTextAsync(TimestampPath,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ct);

            return IndexPath;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Download failed — use stale cache if available
            return File.Exists(IndexPath) ? IndexPath : null;
        }
        finally
        {
            DownloadLock.Release();
        }
    }

    private static bool IsIndexFresh()
    {
        if (!File.Exists(IndexPath) || !File.Exists(TimestampPath))
            return false;

        try
        {
            var tsText = File.ReadAllText(TimestampPath);
            if (long.TryParse(tsText, out var ts))
            {
                var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts;
                return age < (long)CacheExpiry.TotalSeconds;
            }
        }
        catch { }

        return false;
    }

    private static string NormalizeForMatch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var normalized = text.Trim().ToLowerInvariant()
            .Replace('\u2019', '\'')  // '
            .Replace('\u2018', '\'')  // '
            .Replace('\uFF07', '\'')  // ＇
            .Replace('\u201C', '"')   // "
            .Replace('\u201D', '"');  // "

        normalized = PunctuationRegex.Replace(normalized, " ");
        normalized = CollapseWhitespaceRegex.Replace(normalized, " ").Trim();
        return normalized;
    }
}
