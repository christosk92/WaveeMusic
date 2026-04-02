using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http.Lyrics;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Services.Lyrics;

/// <summary>
/// Orchestrates lyrics search across multiple providers with Sequential or BestMatch strategies.
/// </summary>
public sealed class LyricsSearchService
{
    private readonly ISettingsService _settingsService;
    private readonly ILyricsProvider[] _allProviders;
    private readonly ILyricsCacheService _cache;
    private readonly ILogger? _logger;

    private static readonly TimeSpan PerProviderTimeout = TimeSpan.FromSeconds(15);

    public LyricsSearchService(
        ISettingsService settingsService,
        ILyricsProvider[] allProviders,
        ILyricsCacheService cache,
        ILogger<LyricsSearchService>? logger = null)
    {
        _settingsService = settingsService;
        _allProviders = allProviders;
        _cache = cache;
        _logger = logger;
    }

    public async Task<(LyricsResponse? Response, Dictionary<int, List<LrcWordTiming>>? WordTimings)> SearchAsync(
        string? title, string? artist, string? album, double durationMs,
        string? trackId, string? imageUri, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(trackId))
            return (null, null);

        // Check cache
        var cacheKey = $"{title}|{artist}";
        var cached = _cache.TryGet(cacheKey);
        if (cached.HasValue)
        {
            _logger?.LogDebug("Lyrics cache hit for {Title} - {Artist}", title, artist);
            return cached.Value;
        }

        var config = _settingsService.Settings.LyricsProviders;
        var enabledProviders = GetEnabledProviders(config);

        if (enabledProviders.Count == 0)
            return (null, null);

        _logger?.LogDebug("Searching lyrics for {Title} - {Artist} using {Strategy} strategy ({Count} providers)",
            title, artist, config.SearchStrategy, enabledProviders.Count);

        LyricsSearchResult? bestResult;

        if (config.SearchStrategy == "BestMatch")
            bestResult = await SearchBestMatchAsync(enabledProviders, config, title!, artist!, album, durationMs, trackId, imageUri, ct);
        else
            bestResult = await SearchSequentialAsync(enabledProviders, config, title!, artist!, album, durationMs, trackId, imageUri, ct);

        if (bestResult == null)
            return (null, null);

        // Cache the result
        _cache.Set(cacheKey, bestResult.Response, bestResult.WordTimings);

        _logger?.LogInformation("Lyrics found via {Provider} (score: {Score}) for {Title} - {Artist}",
            bestResult.Response.Lyrics?.Provider, bestResult.MatchScore, title, artist);

        return (bestResult.Response, bestResult.WordTimings);
    }

    private async Task<LyricsSearchResult?> SearchSequentialAsync(
        List<(ILyricsProvider Provider, int Threshold)> providers,
        LyricsProviderSettings config,
        string title, string artist, string? album, double durationMs,
        string? trackId, string? imageUri, CancellationToken ct)
    {
        foreach (var (provider, threshold) in providers)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(PerProviderTimeout);

                _logger?.LogDebug("Trying {Provider}...", provider.DisplayName);

                var result = await provider.SearchAsync(title, artist, album, durationMs, trackId, imageUri, timeoutCts.Token);

                if (result != null && result.MatchScore >= threshold)
                {
                    _logger?.LogDebug("{Provider} returned match (score: {Score}, threshold: {Threshold})",
                        provider.DisplayName, result.MatchScore, threshold);
                    return result;
                }

                _logger?.LogDebug("{Provider}: no match (score: {Score}, threshold: {Threshold})",
                    provider.DisplayName, result?.MatchScore ?? 0, threshold);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger?.LogDebug("{Provider} timed out", provider.DisplayName);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "{Provider} failed", provider.DisplayName);
            }
        }

        return null;
    }

    private async Task<LyricsSearchResult?> SearchBestMatchAsync(
        List<(ILyricsProvider Provider, int Threshold)> providers,
        LyricsProviderSettings config,
        string title, string artist, string? album, double durationMs,
        string? trackId, string? imageUri, CancellationToken ct)
    {
        var tasks = providers.Select(async p =>
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(PerProviderTimeout);

                _logger?.LogDebug("Trying {Provider} (parallel)...", p.Provider.DisplayName);

                var result = await p.Provider.SearchAsync(title, artist, album, durationMs, trackId, imageUri, timeoutCts.Token);

                if (result != null && result.MatchScore >= p.Threshold)
                    return result;

                return null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger?.LogDebug("{Provider} timed out", p.Provider.DisplayName);
                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "{Provider} failed", p.Provider.DisplayName);
                return null;
            }
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        return results
            .Where(r => r != null)
            .OrderByDescending(r => r!.MatchScore)
            .FirstOrDefault();
    }

    private List<(ILyricsProvider Provider, int Threshold)> GetEnabledProviders(LyricsProviderSettings config)
    {
        var result = new List<(ILyricsProvider, int)>();
        var defaultThreshold = config.DefaultMatchThreshold;

        foreach (var entry in config.Providers)
        {
            if (!entry.IsEnabled) continue;

            var provider = _allProviders.FirstOrDefault(p => p.Id == entry.Id);
            if (provider == null) continue;

            var threshold = entry.MatchThreshold ?? defaultThreshold;
            result.Add((provider, threshold));
        }

        return result;
    }
}
