using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Parsers;
using Lyricify.Lyrics.Searchers;
using Lyricify.Lyrics.Searchers.Helpers;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http.Lyrics;
using Wavee.Core.Session;
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.Services;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Helpers.Lyrics;
using ControlsLyricsData = Wavee.Controls.Lyrics.Models.Lyrics.LyricsData;
using ControlsLyricsLine = Wavee.Controls.Lyrics.Models.Lyrics.LyricsLine;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Searches multiple lyrics providers in parallel (QQ Music, Kugou, Netease, LRCLIB),
/// preferring syllable-synced results over line-synced. Falls back to Spotify line-level lyrics.
/// </summary>
public sealed class LyricsService : ILyricsService
{
    private readonly ISession _session;
    private readonly IMetadataDatabase? _db;
    private readonly ISettingsService? _settingsService;
    private readonly LrcLibClient _lrcLibClient = new();
    private readonly AmllTtmlDbClient _amllClient = new();
    private readonly Lyricify.Lyrics.Providers.Web.Musixmatch.Api _musixmatchApi = new();
    private readonly ILogger? _logger;

    // Bounded LRU of hot lyrics. Each ControlsLyricsData can be 50-200 KB (word/syllable-synced
    // entries are the fat ones), so an unbounded cache used to grow to tens of megabytes over
    // a browsing session. Capacity is supplied by the caching profile at DI construction time;
    // the default matches the Medium profile to preserve legacy behaviour when no profile is set.
    private readonly LyricsMemoryCache _memoryCache;

    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(10);
    private const string LyricsCacheVersion = "syllable-v3";
    private const string PreviousLyricsCacheVersion = "timing-v2";
    private static readonly Regex CollapseWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex PunctuationRegex = new(@"[\p{P}\p{S}]", RegexOptions.Compiled);

    public LyricsService(
        ISession session,
        IMetadataDatabase? db = null,
        ISettingsService? settingsService = null,
        ILogger<LyricsService>? logger = null,
        // 50 entries × ~75 KB avg (syllable-sync is the heavy case) ≈ 3.5 MB
        // ceiling. Previously 150 ≈ 11 MB. SQLite is the authoritative lyrics
        // store; the in-memory LRU just avoids re-parsing on track revisit.
        // 50 covers a typical listening session (queue + a few manual picks)
        // while keeping the ceiling small.
        int memoryCacheCapacity = 50)
    {
        _session = session;
        _db = db;
        _settingsService = settingsService;
        _logger = logger;
        _memoryCache = new LyricsMemoryCache(memoryCacheCapacity);
    }

    public async Task<(ControlsLyricsData? Lyrics, LyricsSearchDiagnostics Diagnostics)> GetLyricsForTrackAsync(
        string trackId, string? title, string? artist,
        double durationMs, string? imageUrl, CancellationToken ct = default)
    {
        // 1. Check in-memory cache
        if (_memoryCache.TryGetValue(trackId, out var cached))
        {
            if (!IsInstrumentalOnlyResult(cached.Data))
            {
                _logger?.LogDebug("Lyrics cache hit (memory) for {TrackId}", trackId);
                return (cached.Data, BuildCachedDiagnostics(trackId, title, artist, durationMs, cached.Provider));
            }

            // Evict stale instrumental-only cache entry
            _memoryCache.TryRemove(trackId, out _);
            _logger?.LogDebug("Evicted instrumental-only lyrics from memory cache for {TrackId}", trackId);
        }

        // 2. Check SQLite cache
        if (_db != null)
        {
            try
            {
                var cacheKey = BuildLyricsCacheKey(trackId);
                var dbResult = await _db.GetLyricsCacheAsync(cacheKey, ct);
                if (dbResult == null)
                {
                    var legacyKey = BuildLegacyLyricsCacheKey(trackId);
                    var legacyResult = await _db.GetLyricsCacheAsync(legacyKey, ct);
                    if (legacyResult != null)
                    {
                        dbResult = legacyResult;
                        cacheKey = legacyKey;
                    }
                }

                if (dbResult != null)
                {
                    var dto = JsonSerializer.Deserialize(dbResult.Value.JsonData, LyricsCacheJsonContext.Default.CachedLyricsDto);
                    if (dto != null)
                    {
                        var data = LyricsCacheConverter.FromDto(dto);
                        if (!IsInstrumentalOnlyResult(data))
                        {
                            var provider = dbResult.Value.Provider ?? "cached";
                            _memoryCache[trackId] = (data, provider);
                            _logger?.LogDebug("Lyrics cache hit (SQLite) for {TrackId}", trackId);
                            return (data, BuildCachedDiagnostics(trackId, title, artist, durationMs, provider));
                        }

                        // Evict stale instrumental-only entry from SQLite
                        _logger?.LogDebug("Evicted instrumental-only lyrics from SQLite cache for {TrackId}", trackId);
                        _ = _db.DeleteLyricsCacheAsync(cacheKey, CancellationToken.None);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to read lyrics from SQLite cache");
            }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var providerDiags = new List<ProviderDiagnostic>();

        if (string.IsNullOrEmpty(title))
            return (null, BuildDiagnostics(trackId, title, artist, durationMs, providerDiags, null, null, sw.Elapsed));

        var trackMeta = new TrackMultiArtistMetadata
        {
            Title = title,
            Artist = artist,
            DurationMs = (int)durationMs,
        };

        // Build priority/disabled maps from user preferences
        var prefs = _settingsService?.Settings.LyricsSourcePreferences;
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var priorityMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (prefs is { Count: > 0 })
        {
            for (int i = 0; i < prefs.Count; i++)
            {
                if (!prefs[i].IsEnabled) disabled.Add(prefs[i].Name);
                priorityMap[prefs[i].Name] = prefs.Count - i; // higher position = higher value
            }
        }

        // Search enabled providers in parallel (Spotify always on — ground truth)
        var tasks = new List<Task<ProviderResult?>>();
        var providerNames = new List<string>();

        void AddIfEnabled(string name, Func<Task<ProviderResult?>> search)
        {
            if (!disabled.Contains(name))
            {
                tasks.Add(SearchProviderAsync(name, search, ct));
                providerNames.Add(name);
            }
        }

        AddIfEnabled("QQMusic", () => SearchQQMusicAsync(trackMeta, ct));
        AddIfEnabled("Kugou", () => SearchKugouAsync(trackMeta, ct));
        AddIfEnabled("Netease", () => SearchNeteaseAsync(trackMeta, ct));
        AddIfEnabled("LRCLIB", () => SearchLrcLibAsync(title, artist, durationMs, ct));
        AddIfEnabled("Musixmatch", () => SearchMusixmatchAsync(title, artist, durationMs, ct));
        AddIfEnabled("AMLL-TTML-DB", () => SearchAmllTtmlDbAsync(title, artist, ct));

        // Musixmatch: always searched as syllable-sync fallback, even when user-disabled
        bool musixmatchDisabled = disabled.Contains("Musixmatch");
        if (musixmatchDisabled)
        {
            tasks.Add(SearchProviderAsync("Musixmatch", () => SearchMusixmatchAsync(title, artist, durationMs, ct), ct));
            providerNames.Add("Musixmatch");
        }

        // Spotify is always searched (ground truth for scoring)
        tasks.Add(SearchProviderAsync("Spotify", () => SearchSpotifyAsync(trackId, imageUrl, ct), ct));
        providerNames.Add("Spotify");

        var results = await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();

        // Build diagnostics for each provider
        for (int i = 0; i < results.Length; i++)
        {
            var r = results[i];
            providerDiags.Add(new ProviderDiagnostic
            {
                Name = providerNames[i],
                Status = r?.Data != null ? ProviderStatus.Success
                       : r?.Error != null ? (r.Error.Contains("timed out") ? ProviderStatus.Timeout : ProviderStatus.Error)
                       : ProviderStatus.NoResult,
                Error = r?.Error,
                LineCount = r?.Data?.LyricsLines.Count ?? 0,
                HasSyllableSync = r?.Data?.LyricsLines.Any(l => l.IsPrimaryHasRealSyllableInfo) ?? false,
                RawPreview = r?.Data?.LyricsLines.Count > 0
                    ? string.Join("\n", r!.Data!.LyricsLines.Take(5).Select(l => l.PrimaryText))
                    : null,
            });
        }

        var completed = results.Where(r => r?.Data != null).ToList();

        // Spotify result (always correct — fetched by track ID, used as ground truth)
        var spotifyResult = completed.FirstOrDefault(r => r!.Provider == "Spotify");
        var spotifyLines = spotifyResult?.Data?.LyricsLines;

        // Trim headers from external results before scoring against Spotify
        var external = completed.Where(r => r!.Provider != "Spotify").ToList();
        foreach (var ext in external)
            TrimMetadataHeaders(ext!.Data!, spotifyResult?.Data);

        // Score each external result against Spotify lyrics to verify correctness.
        // Musixmatch is intentionally excluded from normal ranking and is only used
        // as a last-resort fallback when no other external source survives validation.
        var selectionCandidates = new List<LyricsSelectionCandidate>();

        foreach (var ext in external)
        {
            var data = ext!.Data!;

            // Skip results that are just instrumental markers (e.g., Chinese "pure music" placeholders)
            if (IsInstrumentalOnlyResult(data))
            {
                _logger?.LogDebug("{Provider} rejected: instrumental-only marker", ext.Provider);
                continue;
            }

            var hasSyllableSync = data.LyricsLines.Any(l => l.IsPrimaryHasRealSyllableInfo);
            double contentScore = spotifyLines is { Count: > 0 }
                ? ScoreAgainstSpotify(data, spotifyLines)
                : 1.0; // No Spotify to compare — accept all

            // Spotify is useful as a text sanity check, but not as a timing
            // authority. Syllable providers often segment lyrics differently,
            // so use a lower text-overlap floor for them and preserve their
            // own timestamps.
            var minimumTextScore = hasSyllableSync ? 0.05 : 0.2;
            if (contentScore < minimumTextScore && spotifyLines is { Count: > 0 })
            {
                _logger?.LogDebug("{Provider} rejected: content score {Score:P0} vs Spotify", ext.Provider, contentScore);
                continue;
            }

            // Preference bonus: user's top-ranked source gets highest bonus (tiebreaker)
            int prefBonus = priorityMap.TryGetValue(ext.Provider!, out var p) ? p * 5 : 0;
            selectionCandidates.Add(new LyricsSelectionCandidate(
                ext.Provider!,
                data,
                contentScore,
                prefBonus,
                TimingReason: hasSyllableSync
                    ? "provider syllable timing preserved"
                    : null));
        }

        var selection = LyricsProviderSelector.SelectBestExternalResult(selectionCandidates);
        if (selection != null)
        {
            _logger?.LogDebug("Lyrics from {Provider} for \"{Title}\" ({Reason})",
                selection.Provider, title, selection.Reason);
            sw.Stop();
            CacheLyrics(trackId, selection.Data, selection.Provider);
            return (selection.Data, BuildDiagnostics(trackId, title, artist, durationMs, providerDiags, selection.Provider, selection.Reason, sw.Elapsed));
        }

        // No external result passed validation — use Spotify directly
        sw.Stop();
        var selectedProvider = spotifyResult?.Data != null ? "Spotify" : null;
        var selectionReason = spotifyResult?.Data != null ? "Fallback (no external provider matched Spotify)" : "No lyrics found";
        if (spotifyResult?.Data != null)
            CacheLyrics(trackId, spotifyResult.Data, "Spotify");
        return (spotifyResult?.Data, BuildDiagnostics(trackId, title, artist, durationMs, providerDiags, selectedProvider, selectionReason, sw.Elapsed));
    }

    /// <summary>
    /// Scores an external lyrics result against Spotify's (ground truth) lyrics.
    /// Compares normalized text of sampled lines. Returns 0.0–1.0 overlap ratio.
    /// </summary>
    private static double ScoreAgainstSpotify(
        ControlsLyricsData candidate,
        List<Wavee.Controls.Lyrics.Models.Lyrics.LyricsLine> spotifyLines)
    {
        // Build normalized set of Spotify lyric words (skip instrumental markers)
        var spotifyTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in spotifyLines)
        {
            var text = NormalizeForMatch(line.PrimaryText);
            if (text.Length > 0)
                spotifyTexts.Add(text);
        }

        if (spotifyTexts.Count == 0)
            return 1.0; // Spotify has no real text — can't compare, accept

        // Sample candidate lines (skip very short/empty) and check how many match Spotify
        int matched = 0;
        int sampled = 0;

        foreach (var line in candidate.LyricsLines)
        {
            var text = NormalizeForMatch(line.PrimaryText);
            if (text.Length < 3) continue; // skip empty/instrumental markers

            sampled++;

            // Check if this line (or close enough) exists in Spotify
            if (spotifyTexts.Contains(text))
            {
                matched++;
                continue;
            }

            // Fuzzy: check if any Spotify line contains this text or vice versa
            foreach (var st in spotifyTexts)
            {
                if (st.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || text.Contains(st, StringComparison.OrdinalIgnoreCase))
                {
                    matched++;
                    break;
                }
            }
        }

        return sampled == 0 ? 0.0 : (double)matched / sampled;
    }

    private static LyricsSearchDiagnostics BuildDiagnostics(
        string? trackId, string? title, string? artist, double durationMs,
        List<ProviderDiagnostic> providers, string? selectedProvider, string? reason, TimeSpan elapsed)
    {
        return new LyricsSearchDiagnostics
        {
            TrackId = trackId,
            QueryTitle = title,
            QueryArtist = artist,
            QueryDurationMs = durationMs,
            Providers = providers,
            SelectedProvider = selectedProvider,
            SelectionReason = reason,
            TotalSearchTime = elapsed,
        };
    }

    private static LyricsSearchDiagnostics BuildCachedDiagnostics(
        string? trackId, string? title, string? artist, double durationMs, string provider)
    {
        return new LyricsSearchDiagnostics
        {
            TrackId = trackId,
            QueryTitle = title,
            QueryArtist = artist,
            QueryDurationMs = durationMs,
            Providers = [],
            SelectedProvider = $"cached ({provider})",
            SelectionReason = "From cache",
            TotalSearchTime = TimeSpan.Zero,
        };
    }

    private static string BuildLyricsCacheKey(string trackId)
        => $"{BuildLegacyLyricsCacheKey(trackId)}#{LyricsCacheVersion}";

    private static string BuildLyricsCacheKey(string trackId, string version)
        => $"{BuildLegacyLyricsCacheKey(trackId)}#{version}";

    private static string BuildLegacyLyricsCacheKey(string trackId)
        => $"spotify:track:{trackId}";

    public async Task ClearCacheForTrackAsync(string trackId, CancellationToken ct = default)
    {
        _memoryCache.TryRemove(trackId, out _);

        if (_db != null)
        {
            await _db.DeleteLyricsCacheAsync(BuildLyricsCacheKey(trackId), ct);
            await _db.DeleteLyricsCacheAsync(BuildLyricsCacheKey(trackId, PreviousLyricsCacheVersion), ct);
            await _db.DeleteLyricsCacheAsync(BuildLegacyLyricsCacheKey(trackId), ct);
        }
    }

    private void CacheLyrics(string trackId, ControlsLyricsData data, string provider)
    {
        _memoryCache[trackId] = (data, provider);

        if (_db != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var dto = LyricsCacheConverter.ToDto(data);
                    var json = JsonSerializer.Serialize(dto, LyricsCacheJsonContext.Default.CachedLyricsDto);
                    bool hasSyllable = data.LyricsLines.Any(l => l.IsPrimaryHasRealSyllableInfo);
                    await _db.SetLyricsCacheAsync(BuildLyricsCacheKey(trackId), provider, json, hasSyllable);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to persist lyrics to SQLite");
                }
            });
        }
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
            return new ProviderResult(name, null, "timed out");
        }
        catch (OperationCanceledException)
        {
            // Outer ct cancelled (track changed mid-search). Return a cancelled
            // sentinel rather than rethrowing — the Task.WhenAll caller would
            // otherwise aggregate one OCE per in-flight provider, each a
            // first-chance throw in the debugger. Caller observes ct itself.
            return new ProviderResult(name, null, "cancelled");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "{Provider} search failed", name);
            return new ProviderResult(name, null, ex.Message);
        }
    }

    // ── QQ Music (QRC syllable format) ──

    private static async Task<ProviderResult?> SearchQQMusicAsync(
        ITrackMetadata track, CancellationToken ct)
    {
        var result = await SearchHelper.Search(track,
            Lyricify.Lyrics.Searchers.Searchers.QQMusic,
            CompareHelper.MatchType.Low);
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
            CompareHelper.MatchType.Low);
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
            CompareHelper.MatchType.Low);
        ct.ThrowIfCancellationRequested();

        if (result is not NeteaseSearchResult neteaseResult)
            return null;

        var response = await ProviderHelper.NeteaseApi.GetLyric(neteaseResult.Id);
        ct.ThrowIfCancellationRequested();

        // API-level instrumental flag — track has no lyrics
        if (response?.Nolyric == true)
            return null;

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

    // ── AMLL-TTML-DB (GitHub syllable-synced TTML database) ──

    private async Task<ProviderResult?> SearchAmllTtmlDbAsync(
        string? title, string? artist, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(artist))
            return null;

        var rawLyricFile = await _amllClient.SearchAsync(title, artist, ct);
        ct.ThrowIfCancellationRequested();

        if (rawLyricFile == null)
            return null;

        var ttml = await _amllClient.FetchTtmlAsync(rawLyricFile, ct);
        ct.ThrowIfCancellationRequested();

        var parsed = LyricsContentParser.Parse(ttml);
        return parsed != null ? new ProviderResult("AMLL-TTML-DB", parsed) : null;
    }

    // ── Musixmatch (richsync syllable format) ──

    private async Task<ProviderResult?> SearchMusixmatchAsync(
        string? title, string? artist, double durationMs, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(title))
            return null;

        var durationSec = durationMs > 0 ? (int?)(durationMs / 1000) : null;
        var rawJson = await _musixmatchApi.GetFullLyricsRaw(title, artist ?? string.Empty, durationSec);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(rawJson))
            return null;

        var lyricifyData = MusixmatchParser.Parse(rawJson);
        if (lyricifyData?.Lines is not { Count: > 0 })
            return null;

        // Convert Lyricify LyricsData → ControlsLyricsData using the same QRC/KRC path
        // (MusixmatchParser produces SyllableLineInfo which ParseQrcKrc handles)
        var lines = lyricifyData.Lines.Where(x => x.Text != string.Empty).ToList();
        if (lines.Count == 0)
            return null;

        var controlsLines = new List<Wavee.Controls.Lyrics.Models.Lyrics.LyricsLine>(lines.Count);
        foreach (var lineRead in lines)
        {
            var lineWrite = new Wavee.Controls.Lyrics.Models.Lyrics.LyricsLine
            {
                StartMs = lineRead.StartTime ?? 0,
                PrimaryText = lineRead.Text,
                IsPrimaryHasRealSyllableInfo = true,
            };

            var syllables = (lineRead as Lyricify.Lyrics.Models.SyllableLineInfo)?.Syllables;
            if (syllables != null)
            {
                int startIndex = 0;
                foreach (var syllable in syllables)
                {
                    lineWrite.PrimarySyllables.Add(new Wavee.Controls.Lyrics.Models.Lyrics.BaseLyrics
                    {
                        StartMs = syllable.StartTime,
                        EndMs = syllable.EndTime,
                        Text = syllable.Text,
                        StartIndex = startIndex,
                    });
                    startIndex += syllable.Text.Length;
                }
            }

            controlsLines.Add(lineWrite);
        }

        // Fill EndMs gaps
        for (int i = 0; i < controlsLines.Count; i++)
        {
            var line = controlsLines[i];
            if (line.EndMs is null or 0 && i + 1 < controlsLines.Count)
                line.EndMs = controlsLines[i + 1].StartMs;

            for (int j = 0; j < line.PrimarySyllables.Count; j++)
            {
                var syl = line.PrimarySyllables[j];
                if (syl.EndMs is null or 0)
                    syl.EndMs = j + 1 < line.PrimarySyllables.Count
                        ? line.PrimarySyllables[j + 1].StartMs
                        : line.EndMs;
            }
        }

        var result = new ControlsLyricsData
        {
            LyricsLines = controlsLines,
            LanguageCode = lyricifyData.TrackMetadata?.Language?.FirstOrDefault(),
        };

        return new ProviderResult("Musixmatch", result);
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

    /// <summary>
    /// Known instrumental-only markers returned by Chinese lyrics databases
    /// when a track has no actual lyrics (pure instrumental).
    /// </summary>
    private static readonly string[] InstrumentalMarkers =
    [
        "纯音乐，请欣赏",                         // Netease: "Pure music, please enjoy"
        "此歌曲为没有填词的纯音乐，请您欣赏",       // QQ Music: "This song has no lyrics, pure music"
        "纯音乐,请欣赏",                           // Comma variant
    ];

    /// <summary>
    /// Returns true if the lyrics result contains an instrumental marker
    /// (e.g., Chinese "pure music" placeholders). Any line matching a marker
    /// means the track is instrumental — other lines are just metadata
    /// (composer credits like "作曲 :") and not real lyrics.
    /// </summary>
    private static bool IsInstrumentalOnlyResult(ControlsLyricsData data)
    {
        if (data.LyricsLines.Count == 0) return false;

        foreach (var line in data.LyricsLines)
        {
            var text = line.PrimaryText?.Trim();
            if (string.IsNullOrEmpty(text)) continue;

            foreach (var marker in InstrumentalMarkers)
            {
                if (text.Contains(marker, StringComparison.Ordinal))
                    return true; // Any instrumental marker means the whole result is instrumental
            }
        }

        return false;
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

    private sealed record ProviderResult(string Provider, ControlsLyricsData? Data, string? Error = null);

    /// <summary>
    /// Bounded LRU cache of lyrics keyed by track id. Exposes a minimal subset of the
    /// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/> API
    /// (TryGetValue, TryRemove, indexer set) so call sites read the same as before.
    /// Thread-safe via a single lock — contention is low because lyrics fetches happen
    /// on track changes, not on a hot UI path.
    /// </summary>
    private sealed class LyricsMemoryCache
    {
        private readonly int _capacity;
        private readonly object _lock = new();
        private readonly LinkedList<KeyValuePair<string, (ControlsLyricsData Data, string Provider)>> _lru = new();
        private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, (ControlsLyricsData Data, string Provider)>>> _map = new();

        public LyricsMemoryCache(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
        }

        public bool TryGetValue(string key, out (ControlsLyricsData Data, string Provider) value)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    // LRU bump: move hit to the front so eviction targets cold entries.
                    if (!ReferenceEquals(_lru.First, node))
                    {
                        _lru.Remove(node);
                        _lru.AddFirst(node);
                    }
                    value = node.Value.Value;
                    return true;
                }
            }
            value = default;
            return false;
        }

        public bool TryRemove(string key, out (ControlsLyricsData Data, string Provider) value)
        {
            lock (_lock)
            {
                if (_map.Remove(key, out var node))
                {
                    _lru.Remove(node);
                    value = node.Value.Value;
                    return true;
                }
            }
            value = default;
            return false;
        }

        public (ControlsLyricsData Data, string Provider) this[string key]
        {
            set
            {
                lock (_lock)
                {
                    if (_map.TryGetValue(key, out var existing))
                    {
                        // Update in place + move to front.
                        existing.Value = new KeyValuePair<string, (ControlsLyricsData, string)>(key, value);
                        if (!ReferenceEquals(_lru.First, existing))
                        {
                            _lru.Remove(existing);
                            _lru.AddFirst(existing);
                        }
                        return;
                    }

                    var node = _lru.AddFirst(new KeyValuePair<string, (ControlsLyricsData, string)>(key, value));
                    _map[key] = node;

                    // Evict tail entries until within capacity.
                    while (_map.Count > _capacity && _lru.Last is { } tail)
                    {
                        _map.Remove(tail.Value.Key);
                        _lru.RemoveLast();
                    }
                }
            }
        }
    }
}
