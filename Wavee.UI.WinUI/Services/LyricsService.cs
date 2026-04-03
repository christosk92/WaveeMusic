using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;
using Lyricify.Lyrics.Searchers.Helpers;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http.Lyrics;
using Wavee.Core.Session;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers.Lyrics;
using ControlsLyricsData = Wavee.Controls.Lyrics.Models.Lyrics.LyricsData;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Searches multiple lyrics providers in parallel (QQ Music, Kugou, Netease, LRCLIB),
/// preferring syllable-synced results over line-synced. Falls back to Spotify line-level lyrics.
/// </summary>
public sealed class LyricsService : ILyricsService
{
    private readonly ISession _session;
    private readonly LrcLibClient _lrcLibClient = new();
    private readonly ILogger? _logger;

    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(10);
    private static readonly Regex CollapseWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex PunctuationRegex = new(@"[\p{P}\p{S}]", RegexOptions.Compiled);

    public LyricsService(ISession session, ILogger<LyricsService>? logger = null)
    {
        _session = session;
        _logger = logger;
    }

    public async Task<ControlsLyricsData?> GetLyricsForTrackAsync(
        string trackId, string? title, string? artist,
        double durationMs, string? imageUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(title))
            return null;

        var trackMeta = new TrackMultiArtistMetadata
        {
            Title = title,
            Artist = artist,
            DurationMs = (int)durationMs,
        };

        // Search all providers in parallel (including Spotify for reference/fallback)
        var tasks = new List<Task<ProviderResult?>>
        {
            SearchProviderAsync("QQMusic", () => SearchQQMusicAsync(trackMeta, ct), ct),
            SearchProviderAsync("Kugou", () => SearchKugouAsync(trackMeta, ct), ct),
            SearchProviderAsync("Netease", () => SearchNeteaseAsync(trackMeta, ct), ct),
            SearchProviderAsync("LRCLIB", () => SearchLrcLibAsync(title, artist, durationMs, ct), ct),
            SearchProviderAsync("Spotify", () => SearchSpotifyAsync(trackId, imageUrl, ct), ct),
        };

        var results = await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();

        var completed = results.Where(r => r?.Data != null).ToList();

        // Spotify result (clean, used as reference for trimming)
        var spotifyResult = completed.FirstOrDefault(r => r!.Provider == "Spotify");

        // Best non-Spotify result: prefer syllable-synced, then most lines
        var best = completed
            .Where(r => r!.Provider != "Spotify")
            .OrderByDescending(r => r!.Data!.LyricsLines.Any(l => l.IsPrimaryHasRealSyllableInfo) ? 1 : 0)
            .ThenByDescending(r => r!.Data!.LyricsLines.Count)
            .FirstOrDefault();

        if (best?.Data != null)
        {
            TrimMetadataHeaders(best.Data, spotifyResult?.Data);
            _logger?.LogDebug("Lyrics from {Provider} for \"{Title}\" ({Lines} lines, syllable={Syllable})",
                best.Provider, title, best.Data.LyricsLines.Count,
                best.Data.LyricsLines.Any(l => l.IsPrimaryHasRealSyllableInfo));
            return best.Data;
        }

        // No non-Spotify result — use Spotify directly (already clean)
        return spotifyResult?.Data;
    }

    // ── Provider wrappers ──

    private async Task<ProviderResult?> SearchProviderAsync(
        string name, Func<Task<ProviderResult?>> search, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProviderTimeout);

            return await search();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger?.LogDebug("{Provider} timed out", name);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "{Provider} search failed", name);
            return null;
        }
    }

    // ── QQ Music (QRC syllable format) ──

    private static async Task<ProviderResult?> SearchQQMusicAsync(
        ITrackMetadata track, CancellationToken ct)
    {
        var result = await SearchHelper.Search(track,
            Lyricify.Lyrics.Searchers.Searchers.QQMusic,
            CompareHelper.MatchType.NoMatch);
        ct.ThrowIfCancellationRequested();

        if (result is not QQMusicSearchResult qqResult)
            return null;

        var response = await ProviderHelper.QQMusicApi.GetLyricsAsync(qqResult.Id);
        ct.ThrowIfCancellationRequested();

        var raw = response?.Lyrics;
        var parsed = LyricsContentParser.Parse(raw);
        return parsed != null ? new ProviderResult("QQMusic", parsed) : null;
    }

    // ── Kugou (KRC syllable format) ──

    private static async Task<ProviderResult?> SearchKugouAsync(
        ITrackMetadata track, CancellationToken ct)
    {
        var result = await SearchHelper.Search(track,
            Lyricify.Lyrics.Searchers.Searchers.Kugou,
            CompareHelper.MatchType.NoMatch);
        ct.ThrowIfCancellationRequested();

        if (result is not KugouSearchResult kugouResult)
            return null;

        var response = await ProviderHelper.KugouApi.GetSearchLyrics(hash: kugouResult.Hash);
        ct.ThrowIfCancellationRequested();

        var candidate = response?.Candidates.FirstOrDefault();
        if (candidate == null)
            return null;

        var raw = await Lyricify.Lyrics.Decrypter.Krc.Helper.GetLyricsAsync(
            candidate.Id, candidate.AccessKey);
        ct.ThrowIfCancellationRequested();

        var parsed = LyricsContentParser.Parse(raw);
        return parsed != null ? new ProviderResult("Kugou", parsed) : null;
    }

    // ── Netease (LRC format) ──

    private static async Task<ProviderResult?> SearchNeteaseAsync(
        ITrackMetadata track, CancellationToken ct)
    {
        var result = await SearchHelper.Search(track,
            Lyricify.Lyrics.Searchers.Searchers.Netease,
            CompareHelper.MatchType.NoMatch);
        ct.ThrowIfCancellationRequested();

        if (result is not NeteaseSearchResult neteaseResult)
            return null;

        var response = await ProviderHelper.NeteaseApi.GetLyric(neteaseResult.Id);
        ct.ThrowIfCancellationRequested();

        var raw = response?.Lrc?.Lyric;
        var parsed = LyricsContentParser.Parse(raw);
        return parsed != null ? new ProviderResult("Netease", parsed) : null;
    }

    // ── LRCLIB (LRC / enhanced LRC) ──

    private async Task<ProviderResult?> SearchLrcLibAsync(
        string? title, string? artist, double durationMs, CancellationToken ct)
    {
        var (lrcResponse, wordTimings) = await _lrcLibClient.GetLyricsAsync(
            title, artist, null, durationMs, ct);

        if (lrcResponse?.Lyrics?.Lines is not { Count: > 0 })
            return null;

        // If LRCLIB returned word timings, build syllable data manually
        if (wordTimings is { Count: > 0 })
        {
            var lines = new List<Wavee.Controls.Lyrics.Models.Lyrics.LyricsLine>(
                lrcResponse.Lyrics.Lines.Count);

            for (int i = 0; i < lrcResponse.Lyrics.Lines.Count; i++)
            {
                var apiLine = lrcResponse.Lyrics.Lines[i];
                var startMs = (int)apiLine.StartTimeMilliseconds;

                int? endMs = int.TryParse(apiLine.EndTimeMs, out var e) && e > 0 ? e : null;
                if ((endMs is null or 0) && i + 1 < lrcResponse.Lyrics.Lines.Count)
                    endMs = (int)lrcResponse.Lyrics.Lines[i + 1].StartTimeMilliseconds;

                var line = new Wavee.Controls.Lyrics.Models.Lyrics.LyricsLine
                {
                    StartMs = startMs,
                    EndMs = endMs,
                    PrimaryText = apiLine.Words,
                };

                if (wordTimings.TryGetValue(i, out var wt) && wt.Count > 0)
                {
                    int charIndex = 0;
                    line.IsPrimaryHasRealSyllableInfo = true;
                    line.PrimarySyllables = wt.Select(w =>
                    {
                        var syl = new Wavee.Controls.Lyrics.Models.Lyrics.BaseLyrics
                        {
                            StartMs = (int)w.StartMs,
                            EndMs = (int)w.EndMs,
                            Text = w.Text,
                            StartIndex = charIndex,
                        };
                        charIndex += w.Text.Length;
                        return syl;
                    }).ToList();
                }
                else
                {
                    line.PrimarySyllables =
                    [
                        new Wavee.Controls.Lyrics.Models.Lyrics.BaseLyrics
                        {
                            StartMs = startMs, EndMs = endMs,
                            Text = apiLine.Words, StartIndex = 0,
                        }
                    ];
                }

                lines.Add(line);
            }

            return new ProviderResult("LRCLIB", new ControlsLyricsData
            {
                LyricsLines = lines,
                LanguageCode = lrcResponse.Lyrics.Language,
            });
        }

        // No word timings — try parsing the raw synced lyrics text if available
        // (LrcLibClient may return raw LRC text that our parser can handle)
        var rawLrc = BuildRawLrc(lrcResponse.Lyrics);
        var parsed = LyricsContentParser.Parse(rawLrc);
        if (parsed != null)
            return new ProviderResult("LRCLIB", parsed);

        // Last resort: build line-level from API response
        return new ProviderResult("LRCLIB", ConvertApiLineLyrics(lrcResponse.Lyrics));
    }

    // ── Spotify (parallel provider) ──

    private async Task<ProviderResult?> SearchSpotifyAsync(
        string trackId, string? imageUrl, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(imageUrl))
        {
            _logger?.LogDebug("Spotify lyrics skipped for {TrackId}: album art URL missing", trackId);
            return null;
        }

        var response = await _session.SpClient.GetLyricsAsync(trackId, imageUrl, ct);
        if (response?.Lyrics?.Lines is not { Count: > 0 })
            return null;

        return new ProviderResult("Spotify", ConvertApiLineLyrics(response.Lyrics));
    }

    // ── Metadata header trimming ──

    /// <summary>
    /// Removes metadata header lines (title, credits) from the beginning of lyrics.
    /// Uses Spotify lyrics as reference when available; otherwise skips trimming.
    /// </summary>
    private void TrimMetadataHeaders(
        ControlsLyricsData data,
        ControlsLyricsData? spotifyRef)
    {
        if (data.LyricsLines.Count == 0) return;

        int trimCount = 0;
        var strategy = "spotify_unmatched";

        // Spotify-only strategy: match against first few Spotify lyric lines
        var spotifyAnchors = GetSpotifyAnchorCandidates(spotifyRef, maxCandidates: 3);
        if (spotifyAnchors.Count == 0)
        {
            _logger?.LogDebug("Metadata trim skipped: Spotify anchors unavailable");
            return;
        }

        const int MaxSearchLines = 24;
        const int MaxTrimFromSpotifyAnchor = 12;
        var searchLineCount = Math.Min(data.LyricsLines.Count, MaxSearchLines);

        for (int i = 0; i < searchLineCount; i++)
        {
            var normalizedLine = NormalizeForMatch(data.LyricsLines[i].PrimaryText);
            if (string.IsNullOrEmpty(normalizedLine))
                continue;

            if (!MatchesAnyAnchor(normalizedLine, spotifyAnchors))
                continue;

            if (i > 0 && i <= MaxTrimFromSpotifyAnchor)
            {
                trimCount = i;
                strategy = "spotify_anchor";
            }

            break;
        }

        if (trimCount > 0)
            data.LyricsLines.RemoveRange(0, trimCount);

        _logger?.LogDebug("Metadata trim result: strategy={Strategy}, removed={Removed}, remaining={Remaining}",
            strategy, trimCount, data.LyricsLines.Count);
    }

    /// <summary>
    /// Word-overlap scoring: checks if enough words in the line appear in any anchor.
    /// Handles different line breaks ("5th of November" vs "fifth of november when i walked you home")
    /// and minor wording differences.
    /// </summary>
    private static bool MatchesAnyAnchor(string normalizedLine, List<string> anchors)
    {
        var lineWords = normalizedLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (lineWords.Length == 0) return false;

        foreach (var anchor in anchors)
        {
            var anchorWords = new HashSet<string>(anchor.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            int matchCount = 0;
            foreach (var w in lineWords)
            {
                if (anchorWords.Contains(w))
                    matchCount++;
            }

            if (matchCount == 0) continue;

            double ratio = (double)matchCount / lineWords.Length;

            // Short lines (1-2 words): all words must match and at least one must be substantive (>= 4 chars)
            if (lineWords.Length <= 2)
            {
                if (matchCount == lineWords.Length && lineWords.Any(w => w.Length >= 4))
                    return true;
            }
            else
            {
                // Longer lines: at least 2 matching words and >= 50% overlap
                if (matchCount >= 2 && ratio >= 0.5)
                    return true;
            }
        }

        return false;
    }

    private static string NormalizeForMatch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var normalized = text.Trim().ToLowerInvariant()
            .Replace('’', '\'')
            .Replace('‘', '\'')
            .Replace('＇', '\'')
            .Replace('“', '"')
            .Replace('”', '"');

        normalized = PunctuationRegex.Replace(normalized, " ");
        normalized = CollapseWhitespaceRegex.Replace(normalized, " ").Trim();
        return normalized;
    }

    private static List<string> GetSpotifyAnchorCandidates(ControlsLyricsData? spotifyRef, int maxCandidates)
    {
        if (spotifyRef?.LyricsLines is not { Count: > 0 } spotifyLines)
            return [];

        var candidates = new List<string>(maxCandidates);
        foreach (var line in spotifyLines)
        {
            if (IsSkippableSpotifyLine(line.PrimaryText))
                continue;

            var normalized = NormalizeForMatch(line.PrimaryText);
            if (string.IsNullOrEmpty(normalized))
                continue;

            if (candidates.Contains(normalized))
                continue;

            candidates.Add(normalized);
            if (candidates.Count >= maxCandidates)
                break;
        }

        return candidates;
    }

    private static bool IsSkippableSpotifyLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var trimmed = text.Trim();
        if (trimmed == "♪")
            return true;

        const string decorativeChars = "♪♫♬♩♭♯·•・";
        return trimmed.All(ch => char.IsWhiteSpace(ch) || decorativeChars.Contains(ch));
    }

    // ── Helpers ──

    private static string BuildRawLrc(Wavee.Core.Http.Lyrics.LyricsData apiData)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in apiData.Lines)
        {
            var ts = TimeSpan.FromMilliseconds(line.StartTimeMilliseconds);
            sb.AppendLine($"[{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}]{line.Words}");
        }
        return sb.ToString();
    }

    private static ControlsLyricsData ConvertApiLineLyrics(Wavee.Core.Http.Lyrics.LyricsData apiData)
    {
        var lines = new List<Wavee.Controls.Lyrics.Models.Lyrics.LyricsLine>(apiData.Lines.Count);

        for (int i = 0; i < apiData.Lines.Count; i++)
        {
            var apiLine = apiData.Lines[i];
            var startMs = (int)apiLine.StartTimeMilliseconds;

            int? endMs = int.TryParse(apiLine.EndTimeMs, out var e) && e > 0 ? e : null;
            if ((endMs is null or 0) && i + 1 < apiData.Lines.Count)
                endMs = (int)apiData.Lines[i + 1].StartTimeMilliseconds;

            lines.Add(new Wavee.Controls.Lyrics.Models.Lyrics.LyricsLine
            {
                StartMs = startMs,
                EndMs = endMs,
                PrimaryText = apiLine.Words,
                IsPrimaryHasRealSyllableInfo = false,
                PrimarySyllables =
                [
                    new Wavee.Controls.Lyrics.Models.Lyrics.BaseLyrics
                    {
                        StartMs = startMs, EndMs = endMs,
                        Text = apiLine.Words, StartIndex = 0,
                    }
                ],
            });
        }

        return new ControlsLyricsData
        {
            LyricsLines = lines,
            LanguageCode = !string.IsNullOrEmpty(apiData.Language) ? apiData.Language : null,
        };
    }

    private sealed record ProviderResult(string Provider, ControlsLyricsData? Data);
}
