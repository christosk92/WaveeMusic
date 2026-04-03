using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Lyrics;

/// <summary>
/// Client for the LRCLIB public lyrics API (https://lrclib.net).
/// Returns word-level timing when enhanced LRC tags are available.
/// </summary>
public sealed class LrcLibClient
{
    private static readonly HttpClient HttpClient = new()
    {
        BaseAddress = new Uri("https://lrclib.net"),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Wavee/1.0 (https://github.com/user/wavee)" }
        }
    };

    /// <summary>
    /// Searches LRCLIB for lyrics matching the given track metadata.
    /// Returns a <see cref="LyricsResponse"/> compatible with the Spotify model, or null if no match found.
    /// </summary>
    public async Task<(LyricsResponse? Response, Dictionary<int, List<LrcWordTiming>>? WordTimings)> GetLyricsAsync(
        string? title, string? artist, string? album,
        double durationMs, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return (null, null);

        try
        {
            var query = $"/api/search?track_name={Uri.EscapeDataString(title)}";
            if (!string.IsNullOrWhiteSpace(artist))
                query += $"&artist_name={Uri.EscapeDataString(artist)}";
            if (!string.IsNullOrWhiteSpace(album))
                query += $"&album_name={Uri.EscapeDataString(album)}";

            var results = await HttpClient.GetFromJsonAsync<LrcLibSearchResult[]>(
                query, LrcLibJsonContext.Default.LrcLibSearchResultArray, ct);

            if (results is not { Length: > 0 })
                return (null, null);

            // Score results by artist/title similarity and pick the best match
            var match = PickBestResult(results, title, artist, durationMs, synced: true);
            if (match == null)
            {
                // Fall back to plain lyrics
                match = PickBestResult(results, title, artist, durationMs, synced: false);
                if (match == null) return (null, null);

                return (BuildUnsyncedResponse(match.PlainLyrics!), null);
            }

            // Parse the synced LRC content
            var parseResult = LrcParser.Parse(match.SyncedLyrics!);
            if (parseResult.Lines.Count == 0)
                return (null, null);

            var response = new LyricsResponse
            {
                Lyrics = new LyricsData
                {
                    SyncType = "LINE_SYNCED",
                    Lines = parseResult.Lines,
                    Provider = "LRCLIB",
                    ProviderDisplayName = "LRCLIB",
                    Language = "",
                },
                Colors = null,
                HasVocalRemoval = false
            };

            return (response, parseResult.HasWordTiming ? parseResult.WordTimings : null);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Scores and picks the best matching result, preferring artist+title match over first-found.
    /// </summary>
    private static LrcLibSearchResult? PickBestResult(
        LrcLibSearchResult[] results, string? title, string? artist, double durationMs, bool synced)
    {
        LrcLibSearchResult? best = null;
        int bestScore = -1;

        foreach (var r in results)
        {
            bool hasLyrics = synced
                ? !string.IsNullOrWhiteSpace(r.SyncedLyrics)
                : !string.IsNullOrWhiteSpace(r.PlainLyrics);
            if (!hasLyrics) continue;

            int score = 0;

            // Artist match is most important to avoid cross-artist confusion
            if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(r.ArtistName))
            {
                if (string.Equals(r.ArtistName, artist, StringComparison.OrdinalIgnoreCase))
                    score += 10;
                else if (r.ArtistName.Contains(artist, StringComparison.OrdinalIgnoreCase)
                         || artist.Contains(r.ArtistName, StringComparison.OrdinalIgnoreCase))
                    score += 5;
                // No match on artist: score stays 0
            }

            // Title match
            if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(r.TrackName))
            {
                if (string.Equals(r.TrackName, title, StringComparison.OrdinalIgnoreCase))
                    score += 4;
                else if (r.TrackName.Contains(title, StringComparison.OrdinalIgnoreCase)
                         || title.Contains(r.TrackName, StringComparison.OrdinalIgnoreCase))
                    score += 2;
            }

            // Duration proximity bonus (within 3 seconds)
            if (durationMs > 0 && r.Duration > 0)
            {
                var diff = Math.Abs(durationMs / 1000.0 - r.Duration);
                if (diff < 3) score += 3;
                else if (diff < 10) score += 1;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = r;
            }
        }

        return best;
    }

    private static LyricsResponse BuildUnsyncedResponse(string plainLyrics)
    {
        var lines = plainLyrics.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => new LyricsLine { Words = l.Trim() })
            .ToList();

        return new LyricsResponse
        {
            Lyrics = new LyricsData
            {
                SyncType = "UNSYNCED",
                Lines = lines,
                Provider = "LRCLIB",
                ProviderDisplayName = "LRCLIB",
            }
        };
    }
}

internal sealed record LrcLibSearchResult
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("trackName")]
    public string? TrackName { get; init; }

    [JsonPropertyName("artistName")]
    public string? ArtistName { get; init; }

    [JsonPropertyName("albumName")]
    public string? AlbumName { get; init; }

    [JsonPropertyName("duration")]
    public double Duration { get; init; }

    [JsonPropertyName("syncedLyrics")]
    public string? SyncedLyrics { get; init; }

    [JsonPropertyName("plainLyrics")]
    public string? PlainLyrics { get; init; }
}

[JsonSerializable(typeof(LrcLibSearchResult[]))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class LrcLibJsonContext : JsonSerializerContext;
